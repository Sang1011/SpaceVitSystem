using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SVS.Application.Common.Exceptions;
using SVS.Application.Common.Interfaces;
using SVS.Domain.Entities;

namespace SVS.Application.Transcription;

/// <summary>
/// Điều phối vòng đời job phía backend: tạo job, gửi sang AI service, đồng bộ trạng thái/transcript về DB, hủy.
/// Trạng thái job nằm trong Postgres nên sống sót khi AI service restart (job bị mất bên đó sẽ được gửi lại).
/// Cách lấy trạng thái (polling hay callback) nằm ngoài lớp này: caller chỉ cần gọi SyncAsync khi có thay đổi.
/// </summary>
public sealed class TranscriptionJobCoordinator(
    IApplicationDbContext db, IAiJobClient ai, IJobProgressNotifier notifier,
    ILogger<TranscriptionJobCoordinator> log)
{
    public const int MaxSubmitAttempts = 5;

    private static readonly TranscriptionJobStatus[] Active =
    [
        TranscriptionJobStatus.Queued, TranscriptionJobStatus.Preprocessing,
        TranscriptionJobStatus.Transcribing, TranscriptionJobStatus.Analyzing,
    ];

    private static string AiId(TranscriptionJob job) => job.Id.ToString("N");

    // ------------------------------------------------------------------ tạo / hủy

    /// <exception cref="KeyNotFoundException">Không có recording.</exception>
    /// <exception cref="InvalidOperationException">Recording đang được xử lý, hoặc không đủ dữ liệu cho mode.</exception>
    public async Task<TranscriptionJob> StartAsync(
        Guid recordingId, TranscriptionMode mode, string outputLanguage, CancellationToken ct)
    {
        var recording = await db.Recordings.FirstOrDefaultAsync(r => r.Id == recordingId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy recording");

        if (mode == TranscriptionMode.Resummarize)
        {
            if (!await db.TranscriptSegments.AnyAsync(s => s.RecordingId == recordingId, ct))
                throw new InvalidOperationException("Recording chưa có transcript để tóm tắt");
        }
        else if (recording.MediaKey is null)
        {
            throw new InvalidOperationException("Recording không có file media");
        }

        if (await db.TranscriptionJobs.AnyAsync(j => j.RecordingId == recordingId && Active.Contains(j.Status), ct))
            throw new InvalidOperationException("Recording đang được xử lý");

        var job = new TranscriptionJob { RecordingId = recordingId, Mode = mode, OutputLanguage = outputLanguage };
        db.TranscriptionJobs.Add(job);
        await db.SaveChangesAsync(ct);

        await TrySubmitAsync(job, recording, ct);
        await NotifyAsync(job, ct);
        return job;
    }

    /// <summary>Đánh dấu hủy ngay trong DB (poller bỏ qua job đã kết thúc) rồi báo AI service dừng, nếu được.</summary>
    public async Task<bool> CancelAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.TranscriptionJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return false;
        if (!Active.Contains(job.Status)) return true;

        job.Status = TranscriptionJobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await TryDeleteRemoteAsync(job, ct);
        await NotifyAsync(job, ct);
        return true;
    }

    // ------------------------------------------------------------------ đồng bộ (gọi bởi poller)

    public Task<List<Guid>> GetActiveJobIdsAsync(CancellationToken ct) =>
        db.TranscriptionJobs.Where(j => Active.Contains(j.Status)).OrderBy(j => j.CreatedAt).Select(j => j.Id).ToListAsync(ct);

    public async Task SyncByIdAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.TranscriptionJobs.Include(j => j.Recording).FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is not null && Active.Contains(job.Status)) await SyncAsync(job, ct);
    }

    public async Task SyncAsync(TranscriptionJob job, CancellationToken ct)
    {
        var recording = job.Recording;

        if (job.SubmittedAt is null)
        {
            await TrySubmitAsync(job, recording, ct);
            await NotifyAsync(job, ct);
            return;
        }

        AiJobStatus? st;
        try
        {
            st = await ai.GetStatusAsync(AiId(job), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or AiServiceException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Không lấy được trạng thái job {JobId}: {Message}", job.Id, ex.Message);
            return; // AI service tạm thời không với tới: giữ nguyên, thử lại vòng sau
        }

        if (st is null)
        {
            // AI service không còn biết job (restart): gửi lại. File vẫn nằm trong thư mục media.
            job.SubmittedAt = null;
            job.SubmitAttempts++;
            if (job.SubmitAttempts >= MaxSubmitAttempts)
                Fail(job, "AI_LOST_JOB", "AI service liên tục mất job");
            await db.SaveChangesAsync(ct);
            await NotifyAsync(job, ct);
            return;
        }

        var status = MapStatus(st.Status, job.Status);

        if (status is TranscriptionJobStatus.Completed or TranscriptionJobStatus.PartialFailed)
        {
            AiJobResult? result;
            try
            {
                result = await ai.GetResultAsync(AiId(job), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or AiServiceException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning("Không lấy được kết quả job {JobId}: {Message}", job.Id, ex.Message);
                return; // chưa lưu gì, vòng sau thử lại
            }
            if (result is null) return;

            ApplyResult(job, recording, result);
            job.Status = status;
            job.Progress = status == TranscriptionJobStatus.Completed ? 1.0 : job.Progress;
            job.QueuePosition = null;
            job.Error = st.Error;
            job.ErrorCode = st.ErrorCode;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await TryDeleteRemoteAsync(job, ct);
            await NotifyAsync(job, ct);
            return;
        }

        job.Status = status;
        job.Progress = st.Progress;
        job.QueuePosition = st.QueuePosition;
        if (job.StartedAt is null && status != TranscriptionJobStatus.Queued) job.StartedAt = DateTimeOffset.UtcNow;

        if (status is TranscriptionJobStatus.Failed or TranscriptionJobStatus.Cancelled)
        {
            job.Error = st.Error;
            job.ErrorCode = st.ErrorCode;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
        else if (job.Mode != TranscriptionMode.Resummarize && st.SegmentsReady > job.SegmentsSynced)
        {
            await SyncPartialSegmentsAsync(job, ct);
        }

        await db.SaveChangesAsync(ct);
        if (job.CompletedAt is not null) await TryDeleteRemoteAsync(job, ct);
        await NotifyAsync(job, ct);
    }

    // ------------------------------------------------------------------ chi tiết

    private async Task TrySubmitAsync(TranscriptionJob job, Recording recording, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<AiSegment>? segments = null;
            if (job.Mode == TranscriptionMode.Resummarize)
            {
                segments = await db.TranscriptSegments
                    .Where(s => s.RecordingId == recording.Id).OrderBy(s => s.Index)
                    .Select(s => new AiSegment(s.StartSec, s.EndSec, s.Text)).ToListAsync(ct);
            }

            await ai.SubmitAsync(new AiSubmitRequest(
                AiId(job), job.Mode, recording.Language, job.OutputLanguage,
                job.Mode == TranscriptionMode.Resummarize ? null : recording.MediaKey, segments), ct);
            job.SubmittedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex) when (ex is HttpRequestException or AiServiceException or TaskCanceledException or InvalidOperationException
                                       && !ct.IsCancellationRequested)
        {
            job.SubmitAttempts++;
            log.LogWarning("Gửi job {JobId} sang AI service thất bại (lần {Attempt}): {Message}", job.Id, job.SubmitAttempts, ex.Message);
            var permanent = ex is AiServiceException { IsPermanent: true };
            if (permanent || job.SubmitAttempts >= MaxSubmitAttempts)
                Fail(job, permanent ? "AI_REJECTED" : "AI_UNREACHABLE", ex.Message);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SyncPartialSegmentsAsync(TranscriptionJob job, CancellationToken ct)
    {
        AiSegmentsPage? page;
        try
        {
            page = await ai.GetSegmentsAsync(AiId(job), job.SegmentsSynced, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or AiServiceException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Không lấy được transcript từng phần của job {JobId}: {Message}", job.Id, ex.Message);
            return; // chỉ là xem trước, kết quả cuối cùng sẽ thay thế toàn bộ
        }
        if (page is null || page.Segments.Count == 0) return;

        if (job.SegmentsSynced == 0) // lần đầu: bỏ transcript cũ của recording (nếu chạy lại)
            db.TranscriptSegments.RemoveRange(await db.TranscriptSegments.Where(s => s.RecordingId == job.RecordingId).ToListAsync(ct));

        foreach (var s in page.Segments)
            db.TranscriptSegments.Add(new TranscriptSegment
            {
                RecordingId = job.RecordingId, Index = s.Index, StartSec = s.Start, EndSec = s.End, Text = s.Text,
            });
        job.SegmentsSynced = page.Next;
    }

    /// <summary>Kết quả cuối thay thế toàn bộ transcript (trừ Resummarize chỉ cập nhật tóm tắt).</summary>
    private void ApplyResult(TranscriptionJob job, Recording recording, AiJobResult result)
    {
        if (job.Mode != TranscriptionMode.Resummarize)
        {
            var old = db.TranscriptSegments.Where(s => s.RecordingId == job.RecordingId).ToList();
            db.TranscriptSegments.RemoveRange(old);
            var i = 0;
            foreach (var s in result.Segments)
                db.TranscriptSegments.Add(new TranscriptSegment
                {
                    RecordingId = job.RecordingId, Index = i++, StartSec = s.Start, EndSec = s.End, Text = s.Text,
                });
            job.SegmentsSynced = result.Segments.Count;
            recording.DurationSec = result.AudioSec ?? recording.DurationSec;
        }

        job.SummaryJson = result.Summary is { ValueKind: not JsonValueKind.Null } sm ? sm.GetRawText() : null;
        job.SummaryError = result.SummaryError;
        job.StageTimingsJson = result.Stages is { ValueKind: not JsonValueKind.Null } st ? st.GetRawText() : null;
    }

    private static TranscriptionJobStatus MapStatus(string aiStatus, TranscriptionJobStatus current) => aiStatus switch
    {
        "QUEUED" => TranscriptionJobStatus.Queued,
        "PREPROCESSING" => TranscriptionJobStatus.Preprocessing,
        "TRANSCRIBING" => TranscriptionJobStatus.Transcribing,
        "ANALYZING" => TranscriptionJobStatus.Analyzing,
        "COMPLETED" => TranscriptionJobStatus.Completed,
        "PARTIAL_FAILED" => TranscriptionJobStatus.PartialFailed,
        "FAILED" => TranscriptionJobStatus.Failed,
        "CANCELLED" => TranscriptionJobStatus.Cancelled,
        _ => current, // trạng thái lạ: giữ nguyên thay vì đoán
    };

    private static void Fail(TranscriptionJob job, string code, string message)
    {
        job.Status = TranscriptionJobStatus.Failed;
        job.ErrorCode = code;
        job.Error = message.Length > 1000 ? message[..1000] : message;
        job.CompletedAt = DateTimeOffset.UtcNow;
    }

    private async Task TryDeleteRemoteAsync(TranscriptionJob job, CancellationToken ct)
    {
        try { await ai.DeleteAsync(AiId(job), ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogDebug("Không dọn được job {JobId} bên AI service: {Message}", job.Id, ex.Message);
        }
    }

    private async Task NotifyAsync(TranscriptionJob job, CancellationToken ct)
    {
        try
        {
            await notifier.NotifyAsync(new JobProgressUpdate(
                job.Id, job.RecordingId, job.Status, job.Progress, job.QueuePosition, job.SegmentsSynced, job.Error), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Gửi thông báo tiến độ job {JobId} thất bại", job.Id);
        }
    }
}

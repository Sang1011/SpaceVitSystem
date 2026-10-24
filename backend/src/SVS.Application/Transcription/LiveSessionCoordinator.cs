using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SVS.Application.Common.Exceptions;
using SVS.Application.Common.Interfaces;
using SVS.Domain.Entities;

namespace SVS.Application.Transcription;

public sealed record ChunkReceipt(int Seq, string Outcome, int NextExpectedSeq, int ReceivedChunks);

public sealed record LiveSessionView(
    LiveSession Session, IReadOnlyList<int> MissingSeqs);

/// <summary>
/// Điều phối phiên ghi âm trực tiếp (xem docs/live-recording.md). Backend giữ trạng thái phiên và chunk trong Postgres/thư mục
/// media; AI service giữ buffer đang chép. Cách lấy kết quả (polling hay callback) nằm ngoài lớp này: caller chỉ cần gọi
/// SyncByIdAsync khi có thay đổi.
/// </summary>
public sealed class LiveSessionCoordinator(
    IApplicationDbContext db, ILiveAiClient ai, IMediaStorage storage, IOptions<LiveOptions> options,
    ILogger<LiveSessionCoordinator> log)
{
    private const int BytesPerSec = 16000 * 2;
    private const int MaxForwardPerCycle = 200;
    private const int MaxAiRecreate = 3;

    private static readonly LiveSessionStatus[] Active =
    [
        LiveSessionStatus.Active, LiveSessionStatus.Finalizing, LiveSessionStatus.Analyzing,
    ];

    private LiveOptions Opt => options.Value;
    private static string AiId(LiveSession s) => s.Id.ToString("N");

    // ------------------------------------------------------------------ tạo phiên

    /// <exception cref="LiveSessionException">429 khi hệ thống đã đủ phiên đồng thời, 503 khi AI service không sẵn sàng.</exception>
    public async Task<LiveSession> CreateAsync(
        Guid spaceId, string language, string outputLanguage, double? chunkSec, CancellationToken ct)
    {
        var session = new LiveSession
        {
            Language = language, OutputLanguage = outputLanguage, ChunkSec = chunkSec ?? Opt.DefaultChunkSec,
        };
        try
        {
            // Đăng ký với AI service TRƯỚC: không để người dùng bắt đầu ghi khi không có ai chép (hoặc đã quá tải).
            await ai.CreateSessionAsync(AiId(session), language, outputLanguage, session.ChunkSec, ct);
        }
        catch (AiServiceException ex) when ((int)ex.StatusCode == 429)
        {
            throw new LiveSessionException(429, "LIVE_BUSY", "Hệ thống đang có quá nhiều phiên ghi âm, thử lại sau");
        }
        catch (Exception ex) when (ex is HttpRequestException or AiServiceException or TaskCanceledException or InvalidOperationException)
        {
            log.LogWarning("Không tạo được phiên bên AI service: {Message}", ex.Message);
            throw new LiveSessionException(503, "AI_UNAVAILABLE", "AI service chưa sẵn sàng");
        }

        var recording = new Recording
        {
            SpaceId = spaceId, Language = language, Source = RecordingSource.Live,
            FileName = $"live-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}",
        };
        session.RecordingId = recording.Id;
        session.AiRegistered = true;
        db.Recordings.Add(recording);
        db.LiveSessions.Add(session);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await TryDeleteRemoteAsync(session, CancellationToken.None);
            throw;
        }
        return session;
    }

    // ------------------------------------------------------------------ nhận chunk

    /// <summary>Lưu chunk, ghi DB rồi báo AI service. Idempotent theo seq: gửi lại cùng seq không tạo bản sao.</summary>
    public async Task<ChunkReceipt> ReceiveChunkAsync(
        Guid sessionId, int seq, Stream body, long? contentLength, CancellationToken ct)
    {
        var s = await Load(sessionId, ct);
        if (s.Status is not (LiveSessionStatus.Active or LiveSessionStatus.Finalizing))
            throw new LiveSessionException(409, "SESSION_CLOSED", $"Phiên đã ở trạng thái {s.Status}");
        if (seq < 0 || seq > Opt.MaxSeq)
            throw new LiveSessionException(400, "SEQ_INVALID", "seq không hợp lệ");
        if (s.TotalChunks is { } total && seq >= total)
            throw new LiveSessionException(400, "SEQ_OUT_OF_RANGE", "seq vượt total_chunks đã báo khi dừng");

        var existing = await db.RecordingChunks.FirstOrDefaultAsync(c => c.LiveSessionId == sessionId && c.Seq == seq, ct);
        if (existing is not null)
        {
            if (contentLength is { } len && len != existing.SizeBytes)
                throw new LiveSessionException(409, "SEQ_CONFLICT", "seq đã tồn tại với nội dung khác");
            if (!existing.Forwarded) await TryForwardAsync(s, existing, ct);
            return await Receipt(s, seq, "duplicate", ct);
        }

        if (contentLength is { } declared && declared > Opt.MaxChunkBytes)
            throw new LiveSessionException(413, "CHUNK_TOO_LARGE", $"Chunk tối đa {Opt.MaxChunkBytes} byte");
        var maxBytes = (long)Opt.MaxSessionSeconds * BytesPerSec;
        if (s.TotalBytes >= maxBytes)
            throw new LiveSessionException(413, "SESSION_TOO_LONG", $"Phiên vượt giới hạn {Opt.MaxSessionSeconds / 60} phút");

        StoredMedia stored;
        try
        {
            stored = await storage.SaveLiveChunkAsync(s.RecordingId, seq, body, Opt.MaxChunkBytes, ct);
        }
        catch (MediaTooLargeException)
        {
            throw new LiveSessionException(413, "CHUNK_TOO_LARGE", $"Chunk tối đa {Opt.MaxChunkBytes} byte");
        }
        catch (InvalidChunkException ex)
        {
            throw new LiveSessionException(400, "CHUNK_INVALID", ex.Message);
        }

        var chunk = new RecordingChunk { LiveSessionId = sessionId, Seq = seq, MediaKey = stored.MediaKey, SizeBytes = stored.SizeBytes };
        db.RecordingChunks.Add(chunk);
        s.LastChunkAt = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // hai request cùng seq chạy đua: bản kia đã thắng
        {
            return await Receipt(s, seq, "duplicate", ct);
        }

        await RefreshCounters(s, ct);
        await TryForwardAsync(s, chunk, ct);
        return await Receipt(s, seq, "accepted", ct);
    }

    /// <summary>Giữ phiên sống khi client đang tạm dừng (không gửi chunk).</summary>
    public async Task KeepAliveAsync(Guid sessionId, CancellationToken ct)
    {
        var s = await Load(sessionId, ct);
        if (s.Status == LiveSessionStatus.Active)
        {
            s.LastChunkAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    // ------------------------------------------------------------------ dừng / hủy

    /// <summary>Dừng ghi: mode chọn lúc này (Transcribe hoặc Summarize). Idempotent nếu gọi lại cùng totalChunks.</summary>
    public async Task<LiveSession> StopAsync(
        Guid sessionId, int totalChunks, TranscriptionMode mode, string? outputLanguage, CancellationToken ct)
    {
        if (mode == TranscriptionMode.Resummarize)
            throw new LiveSessionException(400, "MODE_INVALID", "mode phải là transcribe hoặc summarize");
        var s = await Load(sessionId, ct);

        if (s.Status == LiveSessionStatus.Finalizing || (s.TotalChunks is not null && s.Status != LiveSessionStatus.Active))
        {
            if (s.TotalChunks == totalChunks) return s;
            throw new LiveSessionException(409, "STOP_CONFLICT", "Phiên đã dừng với total_chunks khác");
        }
        if (s.Status != LiveSessionStatus.Active)
            throw new LiveSessionException(409, "SESSION_CLOSED", $"Phiên đã ở trạng thái {s.Status}");
        if (totalChunks < 0 || totalChunks <= s.HighestSeq)
            throw new LiveSessionException(400, "TOTAL_INVALID", "total_chunks phải lớn hơn seq lớn nhất đã gửi");

        await BeginFinalize(s, totalChunks, mode, outputLanguage ?? s.OutputLanguage, Opt.FinalizeWaitMissingSeconds, ct);
        return s;
    }

    public async Task<bool> CancelAsync(Guid sessionId, CancellationToken ct)
    {
        var s = await db.LiveSessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct);
        if (s is null) return false;
        if (!Active.Contains(s.Status)) return true;
        s.Status = LiveSessionStatus.Cancelled;
        s.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await TryDeleteRemoteAsync(s, ct);
        await storage.DeleteLiveChunksAsync(s.RecordingId, ct);
        return true;
    }

    // ------------------------------------------------------------------ truy vấn

    public async Task<LiveSessionView?> GetViewAsync(Guid sessionId, CancellationToken ct)
    {
        var s = await db.LiveSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId, ct);
        if (s is null) return null;
        var seqs = await db.RecordingChunks.Where(c => c.LiveSessionId == sessionId).Select(c => c.Seq).ToListAsync(ct);
        return new LiveSessionView(s, Missing(seqs));
    }

    // ------------------------------------------------------------------ đồng bộ (gọi bởi poller)

    public Task<List<Guid>> GetActiveSessionIdsAsync(CancellationToken ct) =>
        db.LiveSessions.Where(s => Active.Contains(s.Status)).OrderBy(s => s.StartedAt).Select(s => s.Id).ToListAsync(ct);

    public async Task SyncByIdAsync(Guid sessionId, CancellationToken ct)
    {
        var s = await db.LiveSessions.Include(x => x.Recording).FirstOrDefaultAsync(x => x.Id == sessionId, ct);
        if (s is not null && Active.Contains(s.Status)) await SyncAsync(s, ct);
    }

    public async Task SyncAsync(LiveSession s, CancellationToken ct)
    {
        // Người dùng bỏ đi không bấm Stop (đóng tab, mất mạng): tự dừng, chỉ lấy transcript (không tóm tắt).
        var last = s.LastChunkAt ?? s.StartedAt;
        if (s.Status == LiveSessionStatus.Active && (DateTimeOffset.UtcNow - last).TotalSeconds > Opt.AbandonAfterSeconds)
        {
            log.LogInformation("Phiên {SessionId} bị bỏ, tự dừng", s.Id);
            await BeginFinalize(s, s.HighestSeq + 1, TranscriptionMode.Transcribe, s.OutputLanguage, 0, ct);
            return;
        }

        try
        {
            if (!s.AiRegistered && !await Reregister(s, ct)) return;
            await ForwardPendingChunks(s, ct);

            if (s.Status == LiveSessionStatus.Finalizing && !s.FinalizeSent)
            {
                await ai.FinalizeAsync(AiId(s), s.TotalChunks ?? 0, ModeText(s), Opt.FinalizeWaitMissingSeconds, ct);
                s.FinalizeSent = true;
                await db.SaveChangesAsync(ct);
            }

            var st = await ai.GetStatusAsync(AiId(s), ct);
            if (st is null) // AI service không còn biết phiên (restart): tạo lại và gửi lại toàn bộ chunk
            {
                s.AiRecreateCount++;
                s.AiRegistered = false;
                if (s.AiRecreateCount > MaxAiRecreate) Fail(s, "AI_LOST_SESSION", "AI service liên tục mất phiên");
                await db.SaveChangesAsync(ct);
                return;
            }

            switch (st.State)
            {
                case "COMPLETED" or "PARTIAL_FAILED":
                    await CompleteAsync(s, st.State == "COMPLETED" ? LiveSessionStatus.Completed : LiveSessionStatus.PartialFailed, ct);
                    return;
                case "FAILED" or "CANCELLED":
                    s.Status = st.State == "FAILED" ? LiveSessionStatus.Failed : LiveSessionStatus.Cancelled;
                    s.Error = st.Error;
                    s.ErrorCode = st.ErrorCode;
                    s.CompletedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await TryDeleteRemoteAsync(s, ct);
                    return;
            }

            if (st.State == "ANALYZING" && s.Status != LiveSessionStatus.Analyzing) s.Status = LiveSessionStatus.Analyzing;
            if (st.SegmentsReady > s.SegmentsSynced) await SyncPartialSegmentsAsync(s, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or AiServiceException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Đồng bộ phiên {SessionId} thất bại: {Message}", s.Id, ex.Message);
        }
    }

    // ------------------------------------------------------------------ chi tiết

    private async Task<LiveSession> Load(Guid id, CancellationToken ct) =>
        await db.LiveSessions.FirstOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new KeyNotFoundException("Không tìm thấy phiên");

    private async Task BeginFinalize(
        LiveSession s, int totalChunks, TranscriptionMode mode, string outputLanguage, double waitMissing, CancellationToken ct)
    {
        s.Status = LiveSessionStatus.Finalizing;
        s.TotalChunks = totalChunks;
        s.StopMode = mode;
        s.OutputLanguage = outputLanguage;
        s.StoppedAt = DateTimeOffset.UtcNow;
        s.FinalizeSent = false;
        await db.SaveChangesAsync(ct);
        try
        {
            await ForwardPendingChunks(s, ct);
            await ai.FinalizeAsync(AiId(s), totalChunks, ModeText(s), waitMissing, ct);
            s.FinalizeSent = true;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or AiServiceException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Chưa báo được dừng cho AI service ({Message}); poller sẽ thử lại", ex.Message);
        }
    }

    private static string ModeText(LiveSession s) =>
        s.StopMode == TranscriptionMode.Summarize ? "summarize" : "transcribe";

    private async Task<bool> Reregister(LiveSession s, CancellationToken ct)
    {
        try
        {
            await ai.CreateSessionAsync(AiId(s), s.Language, s.OutputLanguage, s.ChunkSec, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or AiServiceException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Chưa tạo lại được phiên {SessionId}: {Message}", s.Id, ex.Message);
            return false;
        }
        // Phiên mới bên AI service không có buffer/segment cũ: gửi lại mọi chunk và đồng bộ lại transcript từ đầu.
        var chunks = await db.RecordingChunks.Where(c => c.LiveSessionId == s.Id).ToListAsync(ct);
        foreach (var c in chunks) c.Forwarded = false;
        s.AiRegistered = true;
        s.SegmentsSynced = 0;
        s.FinalizeSent = false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task ForwardPendingChunks(LiveSession s, CancellationToken ct)
    {
        var pending = await db.RecordingChunks.Where(c => c.LiveSessionId == s.Id && !c.Forwarded)
            .OrderBy(c => c.Seq).Take(MaxForwardPerCycle).ToListAsync(ct);
        foreach (var c in pending)
        {
            await ai.PutChunkAsync(AiId(s), c.Seq, c.MediaKey, ct);
            c.Forwarded = true;
        }
        if (pending.Count > 0) await db.SaveChangesAsync(ct);
    }

    private async Task TryForwardAsync(LiveSession s, RecordingChunk chunk, CancellationToken ct)
    {
        if (!s.AiRegistered) return; // poller sẽ tạo lại phiên rồi gửi bù
        try
        {
            await ai.PutChunkAsync(AiId(s), chunk.Seq, chunk.MediaKey, ct);
            chunk.Forwarded = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or AiServiceException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Chưa báo được chunk {Seq} của phiên {SessionId}: {Message}", chunk.Seq, s.Id, ex.Message);
            if (ex is AiServiceException { StatusCode: System.Net.HttpStatusCode.NotFound }) s.AiRegistered = false;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RefreshCounters(LiveSession s, CancellationToken ct)
    {
        var rows = await db.RecordingChunks.Where(c => c.LiveSessionId == s.Id)
            .Select(c => new { c.Seq, c.SizeBytes }).ToListAsync(ct);
        var seqs = rows.Select(r => r.Seq).ToHashSet();
        s.ReceivedChunks = rows.Count;
        s.HighestSeq = rows.Count == 0 ? -1 : rows.Max(r => r.Seq);
        s.TotalBytes = rows.Sum(r => r.SizeBytes);
        var next = 0;
        while (seqs.Contains(next)) next++;
        s.NextExpectedSeq = next;
        await db.SaveChangesAsync(ct);
    }

    private async Task<ChunkReceipt> Receipt(LiveSession s, int seq, string outcome, CancellationToken ct)
    {
        if (outcome == "duplicate") await RefreshCounters(s, ct);
        return new ChunkReceipt(seq, outcome, s.NextExpectedSeq, s.ReceivedChunks);
    }

    private static List<int> Missing(IEnumerable<int> seqs)
    {
        var set = seqs.ToHashSet();
        var max = set.Count == 0 ? -1 : set.Max();
        return Enumerable.Range(0, Math.Max(max, 0)).Where(i => !set.Contains(i)).Take(200).ToList();
    }

    private async Task SyncPartialSegmentsAsync(LiveSession s, CancellationToken ct)
    {
        var page = await ai.GetSegmentsAsync(AiId(s), s.SegmentsSynced, ct);
        if (page is null || page.Segments.Count == 0) return;

        if (s.SegmentsSynced == 0) // lần đầu (hoặc sau khi tạo lại phiên): bỏ transcript đã đồng bộ trước đó
            db.TranscriptSegments.RemoveRange(await db.TranscriptSegments.Where(x => x.RecordingId == s.RecordingId).ToListAsync(ct));

        foreach (var seg in page.Segments)
            db.TranscriptSegments.Add(new TranscriptSegment
            {
                RecordingId = s.RecordingId, Index = seg.Index, StartSec = seg.Start, EndSec = seg.End, Text = seg.Text,
            });
        s.SegmentsSynced = page.Next;
    }

    /// <summary>Kết quả cuối: thay transcript bằng bản đã sắp theo thời gian, ghi tóm tắt, ghép audio, dọn chunk.</summary>
    private async Task CompleteAsync(LiveSession s, LiveSessionStatus status, CancellationToken ct)
    {
        var result = await ai.GetResultAsync(AiId(s), ct);
        if (result is null) return;

        var old = await db.TranscriptSegments.Where(x => x.RecordingId == s.RecordingId).ToListAsync(ct);
        db.TranscriptSegments.RemoveRange(old);
        var i = 0;
        foreach (var seg in result.Segments)
            db.TranscriptSegments.Add(new TranscriptSegment
            {
                RecordingId = s.RecordingId, Index = i++, StartSec = seg.Start, EndSec = seg.End, Text = seg.Text,
            });
        s.SegmentsSynced = result.Segments.Count;

        var recording = s.Recording ?? await db.Recordings.FirstAsync(r => r.Id == s.RecordingId, ct);
        recording.DurationSec = result.AudioSec;

        // Một TranscriptionJob "ảo" để endpoint summary/recording dùng chung với luồng upload.
        db.TranscriptionJobs.Add(new TranscriptionJob
        {
            RecordingId = s.RecordingId,
            Mode = s.StopMode ?? TranscriptionMode.Transcribe,
            Status = status == LiveSessionStatus.Completed ? TranscriptionJobStatus.Completed : TranscriptionJobStatus.PartialFailed,
            Progress = 1.0, OutputLanguage = s.OutputLanguage, SubmittedAt = s.StartedAt, StartedAt = s.StartedAt,
            CompletedAt = DateTimeOffset.UtcNow, SegmentsSynced = result.Segments.Count,
            SummaryJson = result.Summary is { ValueKind: not JsonValueKind.Null } sm ? sm.GetRawText() : null,
            SummaryError = result.SummaryError,
            StageTimingsJson = result.Stages is { ValueKind: not JsonValueKind.Null } st ? st.GetRawText() : null,
            ErrorCode = status == LiveSessionStatus.PartialFailed ? "SUMMARY_FAILED" : null,
            Error = status == LiveSessionStatus.PartialFailed ? result.SummaryError : null,
        });

        s.GapsJson = result.Gaps.Count == 0 ? null : JsonSerializer.Serialize(result.Gaps);
        s.Status = status;
        s.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await MergeAudioAsync(s, recording, result, ct);
        await TryDeleteRemoteAsync(s, ct);
    }

    private async Task MergeAudioAsync(LiveSession s, Recording recording, AiLiveResult result, CancellationToken ct)
    {
        try
        {
            var gapSeqs = result.Gaps.Where(g => g.Seq is not null).Select(g => g.Seq!.Value).ToHashSet();
            var merged = await storage.MergeLiveChunksAsync(s.RecordingId, s.TotalChunks ?? 0, gapSeqs, s.ChunkSec, ct);
            recording.MediaKey = merged.MediaKey;
            recording.SizeBytes = merged.SizeBytes;
            db.RecordingChunks.RemoveRange(await db.RecordingChunks.Where(c => c.LiveSessionId == s.Id).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            await storage.DeleteLiveChunksAsync(s.RecordingId, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Không ghép được: transcript vẫn đủ. Giữ nguyên chunk để có thể ghép lại thủ công.
            log.LogWarning(ex, "Không ghép được audio của phiên {SessionId}", s.Id);
        }
    }

    private static void Fail(LiveSession s, string code, string message)
    {
        s.Status = LiveSessionStatus.Failed;
        s.ErrorCode = code;
        s.Error = message;
        s.CompletedAt = DateTimeOffset.UtcNow;
    }

    private async Task TryDeleteRemoteAsync(LiveSession s, CancellationToken ct)
    {
        try { await ai.DeleteAsync(AiId(s), ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogDebug("Không dọn được phiên {SessionId} bên AI service: {Message}", s.Id, ex.Message);
        }
    }
}

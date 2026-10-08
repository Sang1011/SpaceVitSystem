using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using SVS.Application.Common.Exceptions;
using SVS.Application.Common.Interfaces;
using SVS.Application.Transcription;
using SVS.Domain.Entities;
using SVS.Infrastructure.Media;

namespace SVS.Api.Endpoints;

public sealed record JobDto(
    Guid Id, Guid RecordingId, string Mode, string Status, double Progress, int? QueuePosition,
    string? Error, string? ErrorCode, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record RecordingDto(
    Guid Id, Guid SpaceId, string FileName, long SizeBytes, string Language, double? DurationSec,
    DateTimeOffset CreatedAt, int SegmentCount, JobDto? LatestJob);

public sealed record UploadResponse(Guid RecordingId, JobDto Job);
public sealed record SegmentDto(int Index, double StartSec, double EndSec, string Text);
public sealed record TranscriptDto(int After, int Next, IReadOnlyList<SegmentDto> Segments);

public static class RecordingEndpoints
{
    private static readonly string[] Languages = ["vi", "en"];

    public static IEndpointRouteBuilder MapRecordingEndpoints(this IEndpointRouteBuilder app)
    {
        // TODO(auth): chưa có đăng nhập, các endpoint này đang MỞ. Khi có auth: yêu cầu đăng nhập và kiểm tra người dùng
        // là thành viên của Space (Editor trở lên được upload, Viewer chỉ xem).
        var api = app.MapGroup("/api").WithTags("Recordings");

        api.MapPost("/spaces/{spaceId:guid}/recordings", Upload);
        api.MapGet("/recordings/{id:guid}", GetRecording);
        api.MapGet("/recordings/{id:guid}/transcript", GetTranscript);
        api.MapGet("/recordings/{id:guid}/summary", GetSummary);
        api.MapPost("/recordings/{id:guid}/summarize", Summarize);
        api.MapGet("/jobs/{id:guid}", GetJob);
        api.MapPost("/jobs/{id:guid}/cancel", CancelJob);

        // TODO(live-recording): phiên ghi âm trực tiếp sẽ có nhóm endpoint riêng (/api/spaces/{id}/live-sessions...),
        // không dùng chung endpoint upload. Xem docs/live-recording-todo.md.
        return app;
    }

    /// <summary>
    /// Upload dạng multipart/form-data, ĐỌC STREAM thẳng ra đĩa (không buffer file 2 GB trong RAM).
    /// Tham số query: mode=transcribe|summarize (mặc định transcribe), language=vi|en (mặc định vi), outputLanguage=vi|en.
    /// </summary>
    private static async Task<IResult> Upload(
        Guid spaceId, HttpRequest request, IApplicationDbContext db, IMediaStorage storage,
        TranscriptionJobCoordinator coordinator, IOptions<MediaOptions> mediaOptions, CancellationToken ct)
    {
        var maxBytes = mediaOptions.Value.MaxUploadBytes;

        var modeText = request.Query["mode"].ToString().ToLowerInvariant();
        TranscriptionMode mode;
        switch (modeText)
        {
            case "" or "transcribe": mode = TranscriptionMode.Transcribe; break;
            case "summarize": mode = TranscriptionMode.Summarize; break;
            default: return Results.BadRequest(new { error = "mode phải là transcribe hoặc summarize" });
        }
        var language = Normalize(request.Query["language"].ToString());
        var outputLanguage = Normalize(request.Query["outputLanguage"].ToString());
        if (language is null || outputLanguage is null)
            return Results.BadRequest(new { error = "language/outputLanguage phải là vi hoặc en" });

        if (!await db.Spaces.AnyAsync(s => s.Id == spaceId && !s.IsArchived, ct))
            return Results.NotFound(new { error = "Không tìm thấy Space" });

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || StringSegment.IsNullOrEmpty(mediaType.Boundary))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value!;

        // Nâng giới hạn cho riêng request này (mặc định Kestrel chỉ ~30 MB) và bỏ giới hạn tốc độ tối thiểu khi upload chậm.
        var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = maxBytes + 1024 * 1024;
        var rateFeature = request.HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>();
        if (rateFeature is not null) rateFeature.MinDataRate = null;

        var recordingId = Guid.NewGuid();
        StoredMedia? stored = null;
        string? fileName = null;
        try
        {
            var reader = new MultipartReader(boundary, request.Body) { BodyLengthLimit = maxBytes };
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                var disposition = section.GetContentDispositionHeader();
                if (disposition is null || !disposition.IsFileDisposition()) continue;

                fileName = disposition.FileName.HasValue ? disposition.FileName.Value : disposition.FileNameStar.Value;
                stored = await storage.SaveAsync(recordingId, fileName ?? string.Empty, section.Body, maxBytes, ct);
                break;
            }
        }
        catch (MediaTooLargeException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status413PayloadTooLarge); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.Json(new { error = "File vượt quá giới hạn upload" }, statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (UnsupportedMediaTypeException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status415UnsupportedMediaType); }
        catch (InvalidDataException) { return Results.BadRequest(new { error = "Nội dung multipart không hợp lệ" }); }

        if (stored is null) return Results.BadRequest(new { error = "Không tìm thấy file trong request" });

        var recording = new Recording
        {
            Id = recordingId, SpaceId = spaceId, FileName = SafeName(fileName), MediaKey = stored.MediaKey,
            SizeBytes = stored.SizeBytes, Language = language,
        };
        db.Recordings.Add(recording);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await storage.DeleteAsync(stored.MediaKey, CancellationToken.None); // không để file mồ côi
            throw;
        }

        try
        {
            var job = await coordinator.StartAsync(recordingId, mode, outputLanguage, ct);
            return Results.Accepted($"/api/jobs/{job.Id}", new UploadResponse(recordingId, ToDto(job)));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message, recordingId });
        }
    }

    private static async Task<IResult> GetRecording(Guid id, IApplicationDbContext db, CancellationToken ct)
    {
        var r = await db.Recordings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Results.NotFound();
        var job = await db.TranscriptionJobs.AsNoTracking().Where(j => j.RecordingId == id)
            .OrderByDescending(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        var count = await db.TranscriptSegments.CountAsync(s => s.RecordingId == id, ct);
        return Results.Ok(new RecordingDto(
            r.Id, r.SpaceId, r.FileName, r.SizeBytes, r.Language, r.DurationSec, r.CreatedAt, count,
            job is null ? null : ToDto(job)));
    }

    /// <summary>Hỗ trợ xem từng phần: gọi lại với after = next của lần trước.</summary>
    private static async Task<IResult> GetTranscript(Guid id, int? after, IApplicationDbContext db, CancellationToken ct)
    {
        if (!await db.Recordings.AnyAsync(r => r.Id == id, ct)) return Results.NotFound();
        var from = Math.Max(0, after ?? 0);
        var items = await db.TranscriptSegments.AsNoTracking()
            .Where(s => s.RecordingId == id && s.Index >= from).OrderBy(s => s.Index).Take(5000)
            .Select(s => new SegmentDto(s.Index, s.StartSec, s.EndSec, s.Text)).ToListAsync(ct);
        return Results.Ok(new TranscriptDto(from, items.Count == 0 ? from : items[^1].Index + 1, items));
    }

    private static async Task<IResult> GetSummary(Guid id, IApplicationDbContext db, CancellationToken ct)
    {
        var json = await db.TranscriptionJobs.AsNoTracking()
            .Where(j => j.RecordingId == id && j.SummaryJson != null)
            .OrderByDescending(j => j.CreatedAt).Select(j => j.SummaryJson).FirstOrDefaultAsync(ct);
        return json is null ? Results.NotFound() : Results.Content(json, "application/json");
    }

    /// <summary>Tóm tắt (lại) từ transcript đã có, không chạy lại STT. Dùng khi tóm tắt lỗi hoặc muốn tóm tắt sau.</summary>
    private static async Task<IResult> Summarize(
        Guid id, TranscriptionJobCoordinator coordinator, CancellationToken ct, string? outputLanguage = null)
    {
        var lang = Normalize(outputLanguage ?? string.Empty);
        if (lang is null) return Results.BadRequest(new { error = "outputLanguage phải là vi hoặc en" });
        try
        {
            var job = await coordinator.StartAsync(id, TranscriptionMode.Resummarize, lang, ct);
            return Results.Accepted($"/api/jobs/{job.Id}", ToDto(job));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }

    private static async Task<IResult> GetJob(Guid id, IApplicationDbContext db, CancellationToken ct)
    {
        var job = await db.TranscriptionJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        return job is null ? Results.NotFound() : Results.Ok(ToDto(job));
    }

    private static async Task<IResult> CancelJob(Guid id, TranscriptionJobCoordinator coordinator, IApplicationDbContext db, CancellationToken ct)
    {
        if (!await coordinator.CancelAsync(id, ct)) return Results.NotFound();
        var job = await db.TranscriptionJobs.AsNoTracking().FirstAsync(j => j.Id == id, ct);
        return Results.Ok(ToDto(job));
    }

    private static string? Normalize(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "vi" : value.Trim().ToLowerInvariant();
        return Languages.Contains(v) ? v : null;
    }

    private static string SafeName(string? name)
    {
        var n = Path.GetFileName((name ?? "recording").Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(n)) n = "recording";
        return n.Length > 255 ? n[^255..] : n;
    }

    private static JobDto ToDto(TranscriptionJob j) => new(
        j.Id, j.RecordingId, j.Mode.ToString(), j.Status.ToString(), j.Progress, j.QueuePosition,
        j.Error, j.ErrorCode, j.CreatedAt, j.CompletedAt);
}

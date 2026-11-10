using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SVS.Application.Common.Exceptions;
using SVS.Application.Common.Interfaces;
using SVS.Application.Spaces;
using SVS.Application.Transcription;
using SVS.Domain.Entities;

namespace SVS.Api.Endpoints;

public sealed record StartLiveRequest(string? Language, string? OutputLanguage, double? ChunkSec);
public sealed record StopLiveRequest(int TotalChunks, string? Mode, string? OutputLanguage);

public sealed record LiveProtocolDto(int SampleRate, int Channels, string Format, double ChunkSec, long MaxChunkBytes);

public sealed record LiveSessionDto(
    Guid Id, Guid RecordingId, string Status, string Language, double ChunkSec, int ReceivedChunks,
    int NextExpectedSeq, IReadOnlyList<int> MissingSeqs, int SegmentsSynced, int? TotalChunks, string? Error, string? ErrorCode,
    DateTimeOffset StartedAt, DateTimeOffset? StoppedAt, DateTimeOffset? CompletedAt, JsonElement? Gaps);

public sealed record StartLiveResponse(LiveSessionDto Session, LiveProtocolDto Protocol);
public sealed record ChunkReceiptDto(int Seq, string Outcome, int NextExpectedSeq, int ReceivedChunks);

/// <summary>
/// Ghi âm trực tiếp. Client gửi PCM s16le 16 kHz mono theo từng chunk (PUT, đánh số seq từ 0). Transcript đọc ở
/// GET /api/recordings/{recordingId}/transcript?after=N (xem RecordingEndpoints). Chi tiết: docs/live-recording.md.
/// </summary>
public static class LiveEndpoints
{
    private static readonly string[] Languages = ["vi", "en"];

    public static IEndpointRouteBuilder MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        // Yêu cầu đăng nhập. Bắt đầu phiên cần Editor trở lên; gửi chunk/dừng/hủy chỉ chủ phiên hoặc Owner; mọi thành viên xem được.
        var api = app.MapGroup("/api").WithTags("Live recording");

        api.MapPost("/spaces/{spaceId:guid}/live-sessions", Start);
        api.MapPut("/live-sessions/{id:guid}/chunks/{seq:int}", PutChunk);
        api.MapPost("/live-sessions/{id:guid}/keepalive", KeepAlive);
        api.MapPost("/live-sessions/{id:guid}/stop", Stop);
        api.MapGet("/live-sessions/{id:guid}", Get);
        api.MapDelete("/live-sessions/{id:guid}", Cancel);
        return app;
    }

    private static async Task<IResult> Start(
        Guid spaceId, StartLiveRequest? request, IApplicationDbContext db, LiveSessionCoordinator coordinator,
        IOptions<LiveOptions> options, SpaceAccessService access, ICurrentUser currentUser, CancellationToken ct)
    {
        await access.RequireAsync(spaceId, SpaceRole.Editor, ct);
        var language = Normalize(request?.Language);
        var outputLanguage = Normalize(request?.OutputLanguage);
        if (language is null || outputLanguage is null)
            return Results.BadRequest(new { error = "language/outputLanguage phải là vi hoặc en" });
        if (request?.ChunkSec is { } c && (c < 0.5 || c > 30))
            return Results.BadRequest(new { error = "chunkSec phải trong khoảng 0.5 đến 30" });
        return await Run(async () =>
        {
            var session = await coordinator.CreateAsync(spaceId, language, outputLanguage, request?.ChunkSec, currentUser.RequireUserId(), ct);
            var view = new LiveSessionView(session, []);
            var protocol = new LiveProtocolDto(16000, 1, "pcm_s16le", session.ChunkSec, options.Value.MaxChunkBytes);
            return Results.Created($"/api/live-sessions/{session.Id}", new StartLiveResponse(ToDto(view), protocol));
        });
    }

    /// <summary>Thân request là PCM thô (application/octet-stream). Gửi lại cùng seq là an toàn (idempotent).</summary>
    private static Task<IResult> PutChunk(
        Guid id, int seq, HttpRequest request, LiveSessionCoordinator coordinator, SpaceAccessService access, CancellationToken ct) =>
        Run(async () =>
        {
            await access.RequireLiveSessionAsync(id, SpaceRole.Editor, creatorOnly: true, ct);
            var r = await coordinator.ReceiveChunkAsync(id, seq, request.Body, request.ContentLength, ct);
            return Results.Ok(new ChunkReceiptDto(r.Seq, r.Outcome, r.NextExpectedSeq, r.ReceivedChunks));
        });

    private static Task<IResult> KeepAlive(Guid id, LiveSessionCoordinator coordinator, SpaceAccessService access, CancellationToken ct) =>
        Run(async () =>
        {
            await access.RequireLiveSessionAsync(id, SpaceRole.Editor, creatorOnly: true, ct);
            await coordinator.KeepAliveAsync(id, ct);
            return Results.NoContent();
        });

    /// <summary>totalChunks = số chunk đã gửi (seq 0..totalChunks-1). mode = transcribe | summarize.</summary>
    private static Task<IResult> Stop(Guid id, StopLiveRequest request, LiveSessionCoordinator coordinator, SpaceAccessService access, CancellationToken ct) =>
        Run(async () =>
        {
            await access.RequireLiveSessionAsync(id, SpaceRole.Editor, creatorOnly: true, ct);
            var mode = (request.Mode ?? "transcribe").ToLowerInvariant() switch
            {
                "transcribe" => TranscriptionMode.Transcribe,
                "summarize" => TranscriptionMode.Summarize,
                _ => (TranscriptionMode?)null,
            };
            if (mode is null) return Results.BadRequest(new { error = "mode phải là transcribe hoặc summarize" });
            var outLang = request.OutputLanguage is null ? null : Normalize(request.OutputLanguage);
            if (request.OutputLanguage is not null && outLang is null)
                return Results.BadRequest(new { error = "outputLanguage phải là vi hoặc en" });

            await coordinator.StopAsync(id, request.TotalChunks, mode.Value, outLang, ct);
            var view = await coordinator.GetViewAsync(id, ct);
            return Results.Accepted($"/api/live-sessions/{id}", ToDto(view!));
        });

    private static Task<IResult> Get(Guid id, LiveSessionCoordinator coordinator, SpaceAccessService access, CancellationToken ct) =>
        Run(async () =>
        {
            await access.RequireLiveSessionAsync(id, SpaceRole.Viewer, creatorOnly: false, ct);
            var view = await coordinator.GetViewAsync(id, ct);
            return view is null ? Results.NotFound() : Results.Ok(ToDto(view));
        });

    private static Task<IResult> Cancel(Guid id, LiveSessionCoordinator coordinator, SpaceAccessService access, CancellationToken ct) =>
        Run(async () =>
        {
            await access.RequireLiveSessionAsync(id, SpaceRole.Editor, creatorOnly: true, ct);
            return await coordinator.CancelAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        });

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (LiveSessionException ex)
        {
            return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: ex.StatusCode);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static string? Normalize(string? value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "vi" : value.Trim().ToLowerInvariant();
        return Languages.Contains(v) ? v : null;
    }

    private static LiveSessionDto ToDto(LiveSessionView v)
    {
        var s = v.Session;
        JsonElement? gaps = string.IsNullOrEmpty(s.GapsJson) ? null : JsonDocument.Parse(s.GapsJson).RootElement.Clone();
        return new LiveSessionDto(
            s.Id, s.RecordingId, s.Status.ToString(), s.Language, s.ChunkSec, s.ReceivedChunks, s.NextExpectedSeq,
            v.MissingSeqs, s.SegmentsSynced, s.TotalChunks, s.Error, s.ErrorCode, s.StartedAt, s.StoppedAt, s.CompletedAt, gaps);
    }
}

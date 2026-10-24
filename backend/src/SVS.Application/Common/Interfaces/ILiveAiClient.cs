using System.Text.Json;

namespace SVS.Application.Common.Interfaces;

public sealed record AiLiveStatus(
    string State, int NextSeq, int Received, int HighestSeq, IReadOnlyList<int> MissingSeqs,
    int SegmentsReady, double CommittedSec, double BufferedSec, string? Error, string? ErrorCode);

public sealed record AiLiveGap(int? Seq, double Start, double End, string Reason);

public sealed record AiLiveResult(
    string State, double? AudioSec, IReadOnlyList<AiSegment> Segments, IReadOnlyList<AiLiveGap> Gaps,
    JsonElement? Summary, string? SummaryError, JsonElement? Stages);

/// <summary>
/// Giao diện backend -> AI service cho ghi âm trực tiếp. Tách khỏi IAiJobClient (job upload).
/// Hiện triển khai bằng HTTP + polling; đổi sang kênh khác chỉ cần thay triển khai.
/// </summary>
public interface ILiveAiClient
{
    /// <summary>Idempotent theo sessionId. Ném AiServiceException 429 khi AI service đã đủ phiên đồng thời.</summary>
    Task CreateSessionAsync(string sessionId, string language, string outputLanguage, double chunkSec, CancellationToken ct);

    Task PutChunkAsync(string sessionId, int seq, string mediaKey, CancellationToken ct);

    /// <summary>null = AI service không biết phiên này (thường do restart).</summary>
    Task<AiLiveStatus?> GetStatusAsync(string sessionId, CancellationToken ct);

    Task<AiSegmentsPage?> GetSegmentsAsync(string sessionId, int after, CancellationToken ct);

    Task FinalizeAsync(string sessionId, int totalChunks, string mode, double waitMissingSec, CancellationToken ct);

    Task<AiLiveResult?> GetResultAsync(string sessionId, CancellationToken ct);

    Task DeleteAsync(string sessionId, CancellationToken ct);
}

using System.Text.Json;
using SVS.Domain.Entities;

namespace SVS.Application.Common.Interfaces;

public sealed record AiSegment(double Start, double End, string Text);

public sealed record AiSubmitRequest(
    string JobId, TranscriptionMode Mode, string Language, string OutputLanguage,
    string? MediaKey, IReadOnlyList<AiSegment>? Segments);

public sealed record AiJobStatus(
    string Status, double Progress, int? QueuePosition, int SegmentsReady,
    double? AudioSec, string? Error, string? ErrorCode);

public sealed record AiIndexedSegment(int Index, double Start, double End, string Text);

public sealed record AiSegmentsPage(int Next, IReadOnlyList<AiIndexedSegment> Segments);

public sealed record AiJobResult(
    string Status, double? AudioSec, IReadOnlyList<AiSegment> Segments,
    JsonElement? Summary, string? SummaryError, JsonElement? Stages);

/// <summary>
/// Giao diện giữa backend và AI service. Hiện triển khai bằng HTTP + polling (HttpAiJobClient). Đổi sang callback
/// hoặc message queue chỉ cần thay triển khai và cách gọi SyncAsync của TranscriptionJobCoordinator.
/// TODO(live-recording): luồng ghi âm trực tiếp dùng interface riêng (ILiveTranscriptionClient), không nhồi vào đây.
/// </summary>
public interface IAiJobClient
{
    /// <exception cref="Exceptions.AiServiceException"/>
    Task SubmitAsync(AiSubmitRequest request, CancellationToken ct);

    /// <summary>null = AI service không biết job này (thường do service restart).</summary>
    Task<AiJobStatus?> GetStatusAsync(string jobId, CancellationToken ct);

    Task<AiSegmentsPage?> GetSegmentsAsync(string jobId, int after, CancellationToken ct);

    Task<AiJobResult?> GetResultAsync(string jobId, CancellationToken ct);

    /// <summary>Hủy job đang chờ/chạy, hoặc xóa kết quả đã xong. Không báo lỗi nếu job không tồn tại.</summary>
    Task DeleteAsync(string jobId, CancellationToken ct);
}

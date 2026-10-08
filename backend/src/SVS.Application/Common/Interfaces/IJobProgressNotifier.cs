using SVS.Domain.Entities;

namespace SVS.Application.Common.Interfaces;

public sealed record JobProgressUpdate(
    Guid JobId, Guid RecordingId, TranscriptionJobStatus Status, double Progress,
    int? QueuePosition, int SegmentsSynced, string? Error);

/// <summary>
/// Đẩy tiến độ job về client. Hiện chưa có triển khai thật (no-op); giai đoạn 2 cài bản SignalR.
/// TODO(live-recording): cũng dùng để đẩy transcript tăng dần của phiên ghi âm.
/// </summary>
public interface IJobProgressNotifier
{
    Task NotifyAsync(JobProgressUpdate update, CancellationToken ct);
}

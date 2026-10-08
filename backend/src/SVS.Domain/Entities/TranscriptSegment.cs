namespace SVS.Domain.Entities;

/// <summary>
/// Một đoạn transcript của bản ghi. Khóa theo (RecordingId, Index), không gắn với một job duy nhất, để sau này
/// chunk của luồng ghi âm trực tiếp (TODO(live-recording)) có thể thêm segment vào cùng Recording.
/// </summary>
public class TranscriptSegment
{
    public long Id { get; set; }

    public Guid RecordingId { get; set; }
    public Recording Recording { get; set; } = null!;

    /// <summary>Thứ tự trong bản ghi, bắt đầu từ 0, liên tục.</summary>
    public int Index { get; set; }
    public double StartSec { get; set; }
    public double EndSec { get; set; }
    public string Text { get; set; } = string.Empty;
}

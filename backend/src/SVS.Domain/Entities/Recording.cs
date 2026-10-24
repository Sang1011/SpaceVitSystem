namespace SVS.Domain.Entities;

public class Recording
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SpaceId { get; set; }
    public Space Space { get; set; } = null!;

    public string FileName { get; set; } = string.Empty;

    /// <summary>Đường dẫn TƯƠNG ĐỐI trong thư mục media dùng chung với AI service (không phải đường dẫn tuyệt đối).</summary>
    public string? MediaKey { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>Ngôn ngữ lời nói: "vi" hoặc "en".</summary>
    public string Language { get; set; } = "vi";

    // Upload hoặc Live (bản ghi tạo từ phiên ghi âm trực tiếp; xem docs/live-recording.md)
    public RecordingSource Source { get; set; } = RecordingSource.Upload;

    public double? DurationSec { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // TODO(auth): thêm UploadedByUserId khi có đăng nhập

    public List<TranscriptionJob> Jobs { get; set; } = new();
    public List<TranscriptSegment> Segments { get; set; } = new();
}

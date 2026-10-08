namespace SVS.Domain.Entities;

public enum TranscriptionJobStatus
{
    Queued,
    Preprocessing,
    Transcribing,
    Analyzing,
    Completed,
    /// <summary>Chép chữ xong nhưng tóm tắt lỗi; transcript vẫn dùng được, có thể chạy lại bằng Resummarize.</summary>
    PartialFailed,
    Failed,
    Cancelled
}

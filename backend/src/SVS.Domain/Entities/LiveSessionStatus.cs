namespace SVS.Domain.Entities;

public enum LiveSessionStatus
{
    /// <summary>Đang ghi: nhận chunk, transcript tăng dần.</summary>
    Active,
    /// <summary>Đã dừng ghi: đang chép nốt phần đuôi và đối soát chunk thiếu.</summary>
    Finalizing,
    /// <summary>Đang tóm tắt bằng Gemini.</summary>
    Analyzing,
    Completed,
    /// <summary>Chép chữ xong nhưng tóm tắt lỗi; transcript vẫn dùng được, chạy lại bằng Resummarize.</summary>
    PartialFailed,
    Failed,
    Cancelled
}

namespace SVS.Domain.Entities;

public class TranscriptionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecordingId { get; set; }
    public Recording Recording { get; set; } = null!;

    public TranscriptionMode Mode { get; set; }
    public TranscriptionJobStatus Status { get; set; } = TranscriptionJobStatus.Queued;

    /// <summary>0..1. Với Summarize, chép chữ chiếm 0..0.95 và tóm tắt phần còn lại.</summary>
    public double Progress { get; set; }
    public int? QueuePosition { get; set; }

    public string OutputLanguage { get; set; } = "vi";

    public string? Error { get; set; }
    public string? ErrorCode { get; set; }

    /// <summary>Thời điểm đã gửi thành công sang AI service; null = chưa gửi (hoặc cần gửi lại).</summary>
    public DateTimeOffset? SubmittedAt { get; set; }
    public int SubmitAttempts { get; set; }

    /// <summary>Số segment đã đồng bộ về DB trong lúc job đang chạy (để lấy transcript từng phần).</summary>
    public int SegmentsSynced { get; set; }

    /// <summary>Kết quả tóm tắt (JSON, cột jsonb). Cấu trúc xem ai-service/app/summary_prompt.py.</summary>
    public string? SummaryJson { get; set; }
    public string? SummaryError { get; set; }

    /// <summary>Timing từng stage (extract, vad, stt, merge, gemini, total) dạng JSON.</summary>
    public string? StageTimingsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

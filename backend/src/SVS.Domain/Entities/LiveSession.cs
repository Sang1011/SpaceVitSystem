namespace SVS.Domain.Entities;

/// <summary>
/// Một phiên ghi âm trực tiếp. Mỗi phiên có một Recording (Source = Live); transcript nằm ở TranscriptSegment của Recording
/// giống luồng upload. Trạng thái phiên nằm ở backend (Postgres); AI service chỉ giữ buffer đang xử lý.
/// </summary>
public class LiveSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RecordingId { get; set; }
    public Recording Recording { get; set; } = null!;

    public LiveSessionStatus Status { get; set; } = LiveSessionStatus.Active;

    public string Language { get; set; } = "vi";
    public string OutputLanguage { get; set; } = "vi";

    /// <summary>Độ dài chunk danh nghĩa (giây), dùng khi một chunk bị thiếu để giữ đúng mốc thời gian.</summary>
    public double ChunkSec { get; set; } = 5;

    /// <summary>Chọn lúc Stop: Transcribe (chỉ transcript) hoặc Summarize (thêm tóm tắt).</summary>
    public TranscriptionMode? StopMode { get; set; }
    public int? TotalChunks { get; set; }

    public int ReceivedChunks { get; set; }
    public int HighestSeq { get; set; } = -1;
    /// <summary>seq nhỏ nhất chưa nhận (tính từ 0, liên tục). Client dùng để biết cần gửi bù từ đâu.</summary>
    public int NextExpectedSeq { get; set; }
    public long TotalBytes { get; set; }

    public bool AiRegistered { get; set; }
    public bool FinalizeSent { get; set; }
    public int AiRecreateCount { get; set; }
    public int SegmentsSynced { get; set; }

    public string? Error { get; set; }
    public string? ErrorCode { get; set; }

    /// <summary>Các khoảng thời gian thiếu audio (chunk không tới hoặc lỗi), JSON (cột jsonb).</summary>
    public string? GapsJson { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastChunkAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<RecordingChunk> Chunks { get; set; } = new();
}

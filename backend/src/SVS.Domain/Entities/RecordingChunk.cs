namespace SVS.Domain.Entities;

/// <summary>Một chunk PCM (s16le, 16 kHz, mono) của phiên live. Bị xóa sau khi ghép thành file audio của Recording.</summary>
public class RecordingChunk
{
    public long Id { get; set; }

    public Guid LiveSessionId { get; set; }
    public LiveSession LiveSession { get; set; } = null!;

    public int Seq { get; set; }
    public string MediaKey { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>Đã báo cho AI service chưa. Chưa thì poller gửi lại.</summary>
    public bool Forwarded { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace SVS.Application.Transcription;

/// <summary>Cấu hình luồng ghi âm trực tiếp (mục "Live" trong appsettings).</summary>
public sealed class LiveOptions
{
    public double DefaultChunkSec { get; set; } = 5;
    public long MaxChunkBytes { get; set; } = 2 * 1024 * 1024;       // ~65 giây PCM 16 kHz
    public int MaxSessionSeconds { get; set; } = 7200;               // 2 giờ, bằng upload
    public int AbandonAfterSeconds { get; set; } = 300;              // không có chunk/keepalive mới -> tự dừng
    public double FinalizeWaitMissingSeconds { get; set; } = 60;     // chờ chunk thiếu sau khi dừng
    public int PollIntervalSeconds { get; set; } = 1;
    public int MaxSeq { get; set; } = 20000;
}

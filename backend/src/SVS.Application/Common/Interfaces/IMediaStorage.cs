namespace SVS.Application.Common.Interfaces;

public sealed record StoredMedia(string MediaKey, long SizeBytes);

/// <summary>
/// Lưu file media vào nơi dùng chung với AI service. Trả về khóa TƯƠNG ĐỐI (MediaKey), không phải đường dẫn.
/// Triển khai hiện tại là thư mục cục bộ; đổi sang object storage chỉ cần thay lớp này.
/// </summary>
public interface IMediaStorage
{
    /// <exception cref="Exceptions.MediaTooLargeException"/>
    /// <exception cref="Exceptions.UnsupportedMediaTypeException"/>
    Task<StoredMedia> SaveAsync(Guid recordingId, string originalFileName, Stream content, long maxBytes, CancellationToken ct);

    Task DeleteAsync(string mediaKey, CancellationToken ct);

    /// <summary>Lưu một chunk PCM s16le của phiên live. Ghi đè nếu seq đã có (gửi lại sau lỗi).</summary>
    /// <exception cref="Exceptions.MediaTooLargeException"/>
    /// <exception cref="Exceptions.InvalidChunkException">Chunk rỗng hoặc số byte lẻ.</exception>
    Task<StoredMedia> SaveLiveChunkAsync(Guid recordingId, int seq, Stream content, long maxBytes, CancellationToken ct);

    /// <summary>
    /// Ghép chunk 0..totalChunks-1 thành một file WAV 16 kHz mono. Chunk trong gapSeqs (hoặc mất file) được thay bằng im lặng
    /// dài chunkSec để mốc thời gian trong audio khớp với transcript.
    /// </summary>
    Task<StoredMedia> MergeLiveChunksAsync(
        Guid recordingId, int totalChunks, IReadOnlySet<int> gapSeqs, double chunkSec, CancellationToken ct);

    Task DeleteLiveChunksAsync(Guid recordingId, CancellationToken ct);
}

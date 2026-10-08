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
}

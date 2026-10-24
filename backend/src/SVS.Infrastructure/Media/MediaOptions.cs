namespace SVS.Infrastructure.Media;

public sealed class MediaOptions
{
    /// <summary>
    /// Thư mục media dùng chung với AI service. Đường dẫn tương đối được tính từ thư mục làm việc hiện tại
    /// (với dotnet run là thư mục project SVS.Api). Trong Docker đặt bằng biến Media__RootPath=/data/media.
    /// </summary>
    public string RootPath { get; set; } = "data/media";

    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

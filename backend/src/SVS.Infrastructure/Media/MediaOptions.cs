namespace SVS.Infrastructure.Media;

public sealed class MediaOptions
{
    public string RootPath { get; set; } = "data/media";

    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

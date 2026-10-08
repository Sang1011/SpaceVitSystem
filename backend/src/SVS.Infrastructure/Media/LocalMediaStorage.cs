using Microsoft.Extensions.Options;
using SVS.Application.Common.Exceptions;
using SVS.Application.Common.Interfaces;

namespace SVS.Infrastructure.Media;

public sealed class LocalMediaStorage(IOptions<MediaOptions> options) : IMediaStorage
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".webm", ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg",
    };

    private string Root => Path.GetFullPath(options.Value.RootPath);

    public async Task<StoredMedia> SaveAsync(
        Guid recordingId, string originalFileName, Stream content, long maxBytes, CancellationToken ct)
    {
        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        if (!Allowed.Contains(ext)) throw new UnsupportedMediaTypeException(ext);

        var key = $"uploads/{recordingId:N}/source{ext}";
        var path = Path.Combine(Root, "uploads", recordingId.ToString("N"), "source" + ext);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long total = 0;
        try
        {
            await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > maxBytes) throw new MediaTooLargeException(maxBytes);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch
        {
            TryDeleteDirectory(Path.GetDirectoryName(path)!);
            throw;
        }
        return new StoredMedia(key, total);
    }

    public Task DeleteAsync(string mediaKey, CancellationToken ct)
    {
        var full = Path.GetFullPath(Path.Combine(Root, mediaKey));
        if (full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            TryDeleteDirectory(Path.GetDirectoryName(full)!);
        return Task.CompletedTask;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }
}

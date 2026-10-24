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

        // Tên file lưu không dùng tên người dùng gửi: tránh ký tự lạ và path traversal.
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

    // ------------------------------------------------------------------ ghi âm trực tiếp

    private string ChunkDir(Guid recordingId) => Path.Combine(Root, "live", recordingId.ToString("N"), "chunks");

    public async Task<StoredMedia> SaveLiveChunkAsync(Guid recordingId, int seq, Stream content, long maxBytes, CancellationToken ct)
    {
        var dir = ChunkDir(recordingId);
        Directory.CreateDirectory(dir);
        var name = $"{seq:D8}.pcm";
        var path = Path.Combine(dir, name);

        long total = 0;
        try
        {
            await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new MediaTooLargeException(maxBytes);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            if (total == 0 || total % 2 != 0)
                throw new InvalidChunkException("Chunk phải là PCM s16le: không rỗng và số byte chẵn");
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { /* best-effort */ }
            throw;
        }
        return new StoredMedia($"live/{recordingId:N}/chunks/{name}", total);
    }

    public async Task<StoredMedia> MergeLiveChunksAsync(
        Guid recordingId, int totalChunks, IReadOnlySet<int> gapSeqs, double chunkSec, CancellationToken ct)
    {
        var outDir = Path.Combine(Root, "live", recordingId.ToString("N"));
        var outPath = Path.Combine(outDir, "audio.wav");
        var silence = new byte[(int)Math.Round(chunkSec * 16000) * 2];
        long dataBytes = 0;

        await using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
        {
            await fs.WriteAsync(new byte[44], ct); // chừa chỗ cho header WAV, ghi lại sau khi biết kích thước
            for (var seq = 0; seq < totalChunks; seq++)
            {
                var chunkPath = Path.Combine(ChunkDir(recordingId), $"{seq:D8}.pcm");
                if (!gapSeqs.Contains(seq) && File.Exists(chunkPath))
                {
                    await using var src = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await src.CopyToAsync(fs, ct);
                    dataBytes += src.Length;
                }
                else
                {
                    await fs.WriteAsync(silence, ct); // giữ đúng mốc thời gian dù thiếu chunk
                    dataBytes += silence.Length;
                }
            }
            fs.Position = 0;
            await fs.WriteAsync(WavHeader(dataBytes), ct);
        }
        return new StoredMedia($"live/{recordingId:N}/audio.wav", dataBytes + 44);
    }

    public Task DeleteLiveChunksAsync(Guid recordingId, CancellationToken ct)
    {
        TryDeleteDirectory(ChunkDir(recordingId));
        return Task.CompletedTask;
    }

    private static byte[] WavHeader(long dataBytes)
    {
        const int sampleRate = 16000, channels = 1, bits = 16;
        var h = new byte[44];
        var span = h.AsSpan();
        "RIFF"u8.CopyTo(span);
        BitConverter.TryWriteBytes(span[4..], (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(span[8..]);
        BitConverter.TryWriteBytes(span[16..], 16u);
        BitConverter.TryWriteBytes(span[20..], (ushort)1);
        BitConverter.TryWriteBytes(span[22..], (ushort)channels);
        BitConverter.TryWriteBytes(span[24..], (uint)sampleRate);
        BitConverter.TryWriteBytes(span[28..], (uint)(sampleRate * channels * bits / 8));
        BitConverter.TryWriteBytes(span[32..], (ushort)(channels * bits / 8));
        BitConverter.TryWriteBytes(span[34..], (ushort)bits);
        "data"u8.CopyTo(span[36..]);
        BitConverter.TryWriteBytes(span[40..], (uint)dataBytes);
        return h;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { /* dọn dẹp best-effort */ }
    }
}

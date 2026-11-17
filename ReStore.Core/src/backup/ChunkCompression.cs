using System.IO.Compression;
using ReStore.Core.src.core;

namespace ReStore.Core.src.backup;

/// <summary>
/// Per-chunk Brotli compression for v3 manifests. The decision is made per chunk: compress, and keep the result only when
/// it saves at least <see cref="MinSavingsRatio"/>. File types that are already compressed skip the attempt entirely.
/// PDF is deliberately not in the skip list: many PDFs hold uncompressed streams and the size check catches the rest.
/// </summary>
internal static class ChunkCompression
{
    /// <summary>Quality 2: fast enough to run inline on every chunk while still giving a useful ratio.</summary>
    private const int Quality = 2;
    private const int WindowBits = 22;

    /// <summary>Compressed output must be below this fraction of the plain size, otherwise the chunk is stored raw.</summary>
    public const double MaxCompressedRatio = 0.97;

    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac",
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv",
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".lz4", ".cab",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub", ".jar", ".apk", ".msix"
    };

    /// <summary>Largest plain chunk accepted when decompressing: the validator caps maxChunkSizeKB at 8 MiB.</summary>
    public const int MaxPlainChunkBytes = 8 * 1024 * 1024;

    public static double MinSavingsRatio => 1 - MaxCompressedRatio;

    public static bool IsKnownCompressedExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && CompressedExtensions.Contains(extension);
    }

    /// <summary>Compresses the chunk; null when the result is not worth storing.</summary>
    public static byte[]? TryCompress(byte[] plain)
    {
        if (plain.Length == 0)
        {
            return null;
        }

        var buffer = new byte[BrotliEncoder.GetMaxCompressedLength(plain.Length)];
        if (!BrotliEncoder.TryCompress(plain, buffer, out var written, Quality, WindowBits))
        {
            return null;
        }

        if (written >= plain.Length * MaxCompressedRatio)
        {
            return null;
        }

        return buffer.AsSpan(0, written).ToArray();
    }

    /// <summary>Decompresses a stored payload; the output is bounded by the size the manifest declares.</summary>
    public static byte[] Decompress(byte[] compressed, int expectedPlainSize)
    {
        if (expectedPlainSize < 0 || expectedPlainSize > MaxPlainChunkBytes)
        {
            throw new InvalidDataException("the recorded chunk size is out of range");
        }

        var buffer = new byte[expectedPlainSize];
        if (!BrotliDecoder.TryDecompress(compressed, buffer, out var written) || written != expectedPlainSize)
        {
            throw new InvalidDataException("compressed chunk data is invalid or does not match the recorded size");
        }

        return buffer;
    }

    /// <summary>Reverses the stored form: decompresses brotli chunks, passes raw ones through.</summary>
    public static byte[] Decode(string? codec, byte[] decrypted, int expectedPlainSize)
    {
        return SnapshotChunkCodecs.IsBrotli(codec) ? Decompress(decrypted, expectedPlainSize) : decrypted;
    }
}

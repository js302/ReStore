using System.Buffers;
using System.Numerics;
using System.Security.Cryptography;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.backup;

public class ChunkBuildPayload
{
    public string ChunkId { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int PlainSizeBytes { get; set; }
    public byte[] StoredPayload { get; set; } = [];

    /// <summary>How <see cref="StoredPayload"/> is encoded (<see cref="SnapshotChunkCodecs"/>).</summary>
    public string Codec { get; set; } = SnapshotChunkCodecs.Raw;

    /// <summary>Stored size this chunk would have uncompressed (plain size plus any encryption overhead).</summary>
    public long RawStoredSize { get; set; }

    /// <summary>
    /// Stored size this chunk would have with Brotli (null when compression does not pay off). Computed on demand, only to
    /// recognise an object already stored under the same id when the payload itself is raw.
    /// </summary>
    internal Func<long?>? ComputeCompressedStoredSize { get; set; }

    /// <summary>
    /// Decodes an already-stored object (decrypting first when encrypted) as raw and as brotli and checks the plaintext
    /// against the chunk id. Returns the codec that verifies, or null when neither does (corrupt or truncated).
    /// </summary>
    internal Func<byte[], string?>? IdentifyStoredForm { get; set; }
}

public class ChunkedFileBuildResult
{
    public SnapshotFileManifestEntry FileEntry { get; set; } = new();
    public List<ChunkBuildPayload> ChunkPayloads { get; set; } = [];
}

/// <summary>A source file could not be opened or read (locked, vanished, access denied).</summary>
public class SourceFileUnreadableException(string path, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public string FilePath { get; } = path;
}

/// <summary>A source file's size or timestamp changed while it was being read.</summary>
public sealed class FileChangedDuringBackupException(string path)
    : SourceFileUnreadableException(path, $"File changed during backup: {path}");

public class ChunkingService
{
    private static readonly ulong[] GEAR_TABLE = BuildGearTable();

    private readonly ILogger _logger;
    private readonly ChunkDiffingConfig _chunkConfig;
    private readonly ChunkingProfile _profile;
    private readonly EncryptionService _encryptionService;
    private readonly bool _encryptionEnabled;
    private readonly byte[]? _encryptionMasterKey;
    private readonly Func<string, Stream>? _openFile;
    private readonly bool _recordCodecs;
    private readonly bool _compress;
    private readonly ChunkHashScheme _hashScheme;
    private readonly int _algorithmVersion;

    /// <param name="openFile">Test seam replacing how source files are opened; null opens them shared (read/write/delete).</param>
    /// <param name="recordCodecs">True when writing a v3 manifest: every chunk entry then carries its codec.</param>
    /// <param name="compress">True to Brotli-compress chunks where that pays off (v3 only; implies <paramref name="recordCodecs"/>).</param>
    /// <param name="algorithmVersion">Chunk-boundary algorithm: 2 = <see cref="RollingWindowHash"/> (v2 writers), 3 = fast chunker (v3 writers).</param>
    /// <param name="hashScheme">How chunk ids and file content hashes are computed; null = plain SHA-256 (v2, unencrypted v3). Encrypted v3 passes the keyed scheme.</param>
    public ChunkingService(
        ILogger logger,
        ChunkDiffingConfig chunkConfig,
        EncryptionService encryptionService,
        bool encryptionEnabled,
        byte[]? encryptionMasterKey,
        Func<string, Stream>? openFile = null,
        bool recordCodecs = false,
        bool compress = false,
        ChunkHashScheme? hashScheme = null,
        int algorithmVersion = ChunkingProfile.CurrentAlgorithmVersion)
    {
        _algorithmVersion = algorithmVersion;
        _openFile = openFile;
        _hashScheme = hashScheme ?? ChunkHashScheme.Sha256;
        _compress = compress;
        _recordCodecs = recordCodecs || compress;
        _logger = logger;
        _chunkConfig = chunkConfig;
        _profile = ChunkingProfile.FromConfig(chunkConfig, algorithmVersion);
        _encryptionService = encryptionService;
        _encryptionEnabled = encryptionEnabled;
        _encryptionMasterKey = encryptionMasterKey;

        if (_encryptionEnabled && (_encryptionMasterKey == null || _encryptionMasterKey.Length == 0))
        {
            throw new ArgumentException("Encryption master key is required when chunk encryption is enabled", nameof(encryptionMasterKey));
        }
    }

    public Task<ChunkedFileBuildResult> BuildFileManifestEntryAsync(string filePath, string baseDirectory, CancellationToken cancellationToken = default)
    {
        return BuildFileManifestEntryAsync(filePath, baseDirectory, null, cancellationToken);
    }

    /// <summary>
    /// Chunks a file and builds its manifest entry.
    /// </summary>
    /// <param name="chunkSink">
    /// Invoked once per chunk as it is produced. When supplied, payload bytes are handed to the
    /// sink and not retained, so peak memory stays at roughly one chunk rather than the whole
    /// file. When null the payloads are collected into
    /// <see cref="ChunkedFileBuildResult.ChunkPayloads"/> instead.
    /// </param>
    public async Task<ChunkedFileBuildResult> BuildFileManifestEntryAsync(
        string filePath,
        string baseDirectory,
        Func<ChunkBuildPayload, CancellationToken, Task>? chunkSink,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new SourceFileUnreadableException(filePath, $"Cannot chunk missing file: {filePath}", new FileNotFoundException($"Cannot chunk missing file: {filePath}", filePath));
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory cannot be null or empty", nameof(baseDirectory));
        }

        var fileInfo = new FileInfo(filePath);
        var sizeBefore = fileInfo.Length;
        var modifiedBefore = fileInfo.LastWriteTimeUtc;
        var totalBytesRead = 0L;
        using var fileHasher = _hashScheme.NewFileHasher();
        var chunkPayloads = new List<ChunkBuildPayload>();
        var chunkEntries = new List<SnapshotChunkManifestEntry>();

        using var fileStream = OpenSourceFile(filePath);

        if (_algorithmVersion >= ChunkingProfile.FastChunkerAlgorithmVersion)
        {
            totalBytesRead = await ChunkStreamFastAsync(fileStream, filePath, fileHasher, chunkEntries, chunkPayloads, chunkSink, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            totalBytesRead = await ChunkStreamLegacyAsync(fileStream, filePath, fileHasher, chunkEntries, chunkPayloads, chunkSink, cancellationToken).ConfigureAwait(false);
        }

        if (chunkEntries.Count > _chunkConfig.MaxChunksPerFile)
        {
            throw new InvalidOperationException($"File exceeds maxChunksPerFile safety limit ({_chunkConfig.MaxChunksPerFile}): {filePath}");
        }

        // Re-stat after reading: a file modified (or still being written) while it was read yields chunks
        // that do not match any single version of it.
        try
        {
            fileInfo.Refresh();
            if (!fileInfo.Exists
                || fileInfo.Length != sizeBefore
                || fileInfo.LastWriteTimeUtc != modifiedBefore
                || totalBytesRead != sizeBefore)
            {
                throw new FileChangedDuringBackupException(filePath);
            }
        }
        catch (Exception ex) when (ex is IOException and not SourceFileUnreadableException or UnauthorizedAccessException)
        {
            throw new SourceFileUnreadableException(filePath, $"Cannot stat '{filePath}': {ex.Message}", ex);
        }

        var relativePath = Path.GetRelativePath(baseDirectory, filePath)
            .Replace(Path.DirectorySeparatorChar, '/');

        var fileHash = Convert.ToHexStringLower(fileHasher.GetHashAndReset());
        var fileEntry = new SnapshotFileManifestEntry
        {
            RelativePath = relativePath,
            SizeBytes = chunkEntries.Sum(chunk => (long)chunk.PlainSizeBytes),
            LastModifiedUtc = modifiedBefore,
            ContentHash = fileHash,
            Chunks = chunkEntries
        };

        _logger.Log($"Chunked file '{relativePath}' into {chunkEntries.Count} chunk(s)", LogLevel.Debug);

        return new ChunkedFileBuildResult
        {
            FileEntry = fileEntry,
            ChunkPayloads = chunkPayloads
        };
    }

    /// <summary>Algorithm 1/2 chunker: per-byte rolling hash. Boundaries are frozen; v2 repositories depend on them.</summary>
    private async Task<long> ChunkStreamLegacyAsync(
        Stream fileStream,
        string filePath,
        IncrementalHash fileHasher,
        List<SnapshotChunkManifestEntry> chunkEntries,
        List<ChunkBuildPayload> chunkPayloads,
        Func<ChunkBuildPayload, CancellationToken, Task>? chunkSink,
        CancellationToken cancellationToken)
    {
        using var chunkStream = new MemoryStream(_profile.MaxChunkSizeBytes);
        var totalBytesRead = 0L;
        var readBuffer = new byte[128 * 1024];
        var currentChunkSize = 0;
        var rollingHash = new RollingWindowHash(_profile.RollingHashWindowSize, GEAR_TABLE);

        while (true)
        {
            int bytesRead;
            try
            {
                bytesRead = await fileStream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SourceFileUnreadableException(filePath, $"Cannot read '{filePath}': {ex.Message}", ex);
            }

            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;

            // Once per read buffer, not per byte: a per-byte check dominates this loop without
            // making cancellation meaningfully more responsive.
            cancellationToken.ThrowIfCancellationRequested();

            fileHasher.AppendData(readBuffer.AsSpan(0, bytesRead));

            for (var index = 0; index < bytesRead; index++)
            {
                var currentByte = readBuffer[index];
                chunkStream.WriteByte(currentByte);
                currentChunkSize++;

                rollingHash.Add(currentByte);

                if (!ShouldCutChunk(rollingHash.Value, currentChunkSize))
                {
                    continue;
                }

                await AppendChunkPayloadAsync(chunkStream, filePath, chunkEntries, chunkPayloads, chunkSink, cancellationToken).ConfigureAwait(false);
                if (chunkEntries.Count > _chunkConfig.MaxChunksPerFile)
                {
                    throw new InvalidOperationException($"File exceeds maxChunksPerFile safety limit ({_chunkConfig.MaxChunksPerFile}): {filePath}");
                }

                currentChunkSize = 0;
                rollingHash.Reset();
            }
        }

        if (chunkStream.Length > 0)
        {
            await AppendChunkPayloadAsync(chunkStream, filePath, chunkEntries, chunkPayloads, chunkSink, cancellationToken).ConfigureAwait(false);
        }

        return totalBytesRead;
    }

    /// <summary>
    /// Algorithm 3 chunker. Same min/target/max semantics and rolling hash as algorithm 2, but: bytes are copied into the
    /// chunk buffer as spans (no per-byte stream writes), the first <c>min - window</c> bytes of each chunk are not hashed
    /// (no cut is possible before <c>min</c>, and the window is full again by then), the cut test is a power-of-two mask
    /// instead of <c>%</c>, and chunk/read buffers come from <see cref="ArrayPool{T}"/>.
    /// </summary>
    private async Task<long> ChunkStreamFastAsync(
        Stream fileStream,
        string filePath,
        IncrementalHash fileHasher,
        List<SnapshotChunkManifestEntry> chunkEntries,
        List<ChunkBuildPayload> chunkPayloads,
        Func<ChunkBuildPayload, CancellationToken, Task>? chunkSink,
        CancellationToken cancellationToken)
    {
        var min = Math.Max(0, _profile.MinChunkSizeBytes);
        var max = Math.Max(Math.Max(1, _profile.MaxChunkSizeBytes), min);
        var window = Math.Max(0, _profile.RollingHashWindowSize);
        var mask = CutMask(_profile.TargetChunkSizeBytes);
        var hashStart = window > 0 ? Math.Max(0, min - window) : 0;
        var oldestMultiplier = OldestByteMultiplier(window);
        // Bytes are copied (and then scanned) in blocks about one chunk-average long so a cut wastes little speculative copying.
        var block = (int)Math.Min(max, (long)Math.Max(mask + 1, 256));

        var chunk = ArrayPool<byte>.Shared.Rent(max);
        var readBuffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            var totalBytesRead = 0L;
            var length = 0;
            ulong hash = 0;

            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await fileStream.ReadAsync(readBuffer.AsMemory(0, 128 * 1024), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new SourceFileUnreadableException(filePath, $"Cannot read '{filePath}': {ex.Message}", ex);
                }

                if (bytesRead == 0)
                {
                    break;
                }

                totalBytesRead += bytesRead;
                cancellationToken.ThrowIfCancellationRequested();
                fileHasher.AppendData(readBuffer.AsSpan(0, bytesRead));

                var consumed = 0;
                while (consumed < bytesRead)
                {
                    var want = length < min ? min - length : block;
                    var count = Math.Min(Math.Min(bytesRead - consumed, max - length), want);
                    readBuffer.AsSpan(consumed, count).CopyTo(chunk.AsSpan(length));

                    var cut = ScanForCut(chunk, length, length + count, min, max, window, hashStart, mask, oldestMultiplier, ref hash);
                    if (cut < 0)
                    {
                        length += count;
                        consumed += count;
                        continue;
                    }

                    // Cut after `cut` bytes; the bytes copied past it are re-copied into the next chunk.
                    consumed += cut - length;
                    await EmitChunkAsync(chunk.AsSpan(0, cut).ToArray(), filePath, chunkEntries, chunkPayloads, chunkSink, cancellationToken).ConfigureAwait(false);
                    if (chunkEntries.Count > _chunkConfig.MaxChunksPerFile)
                    {
                        throw new InvalidOperationException($"File exceeds maxChunksPerFile safety limit ({_chunkConfig.MaxChunksPerFile}): {filePath}");
                    }

                    length = 0;
                    hash = 0;
                }
            }

            if (length > 0)
            {
                await EmitChunkAsync(chunk.AsSpan(0, length).ToArray(), filePath, chunkEntries, chunkPayloads, chunkSink, cancellationToken).ConfigureAwait(false);
            }

            return totalBytesRead;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    /// <summary>
    /// Hashes chunk[from..to) (skipping bytes before <paramref name="hashStart"/>) and returns the chunk length at the first
    /// cut point in that range, or -1. <paramref name="hash"/> carries the rolling state across calls within one chunk.
    /// </summary>
    private static int ScanForCut(
        byte[] chunk, int from, int to, int min, int max, int window, int hashStart, ulong mask, ulong oldestMultiplier, ref ulong hash)
    {
        const ulong HashBase = 257;
        var gear = GEAR_TABLE;
        var h = hash;
        var i = Math.Max(from, hashStart);

        unchecked
        {
            // Window still filling (or unbounded when window == 0): add only.
            var fillEnd = window > 0 ? Math.Min(to, hashStart + window) : to;
            for (; i < fillEnd; i++)
            {
                h = (h * HashBase) + gear[chunk[i]];
                if ((h & mask) == mask && i + 1 >= min)
                {
                    hash = h;
                    return i + 1;
                }
            }

            for (; i < to; i++)
            {
                h = ((h - (gear[chunk[i - window]] * oldestMultiplier)) * HashBase) + gear[chunk[i]];
                if ((h & mask) == mask && i + 1 >= min)
                {
                    hash = h;
                    return i + 1;
                }
            }
        }

        hash = h;
        return to >= max ? max : -1;
    }

    /// <summary>
    /// Cut mask for the target size: the nearest power of two minus one, so <c>(hash &amp; mask) == mask</c> replaces
    /// <c>hash % target</c> and the mean distance past the minimum stays about the target. Targets of 1 or less cut at the minimum.
    /// </summary>
    private static ulong CutMask(int target)
    {
        if (target <= 1)
        {
            return 0;
        }

        var up = (long)BitOperations.RoundUpToPowerOf2((uint)target);
        var down = up >> 1;
        var nearest = target - down < up - target ? down : up;
        return (ulong)nearest - 1;
    }

    private static ulong OldestByteMultiplier(int windowSize)
    {
        ulong multiplier = 1;
        unchecked
        {
            for (var index = 1; index < windowSize; index++)
            {
                multiplier *= 257;
            }
        }

        return multiplier;
    }

    private Stream OpenSourceFile(string filePath)
    {
        try
        {
            return _openFile != null
                ? _openFile(filePath)
                // Shared so a file another process holds open (Outlook PST, a running database) can still be read.
                : new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 128 * 1024, useAsync: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SourceFileUnreadableException(filePath, $"Cannot open '{filePath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The one place a chunk id is computed: SHA-256 over the plaintext, or HMAC-SHA256 under the repository chunk-id key
    /// for encrypted v3 repositories (see <see cref="ChunkHashScheme"/>).
    /// </summary>
    private string ComputeChunkId(byte[] plaintext) => _hashScheme.ChunkId(plaintext);

    private string? IdentifyStoredForm(byte[] stored, byte[] plaintext, string chunkId)
    {
        byte[]? Decode(string codec)
        {
            try
            {
                return _encryptionEnabled
                    ? EncryptionService.DecryptChunkDeterministic(stored, _encryptionMasterKey!, chunkId, CodecContext(codec))
                    : stored;
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
            {
                return null;
            }
        }

        bool Matches(byte[] candidate) => candidate.Length == plaintext.Length && ComputeChunkId(candidate) == chunkId;

        if (Decode(SnapshotChunkCodecs.Raw) is { } rawInner && Matches(rawInner))
        {
            return SnapshotChunkCodecs.Raw;
        }

        if (Decode(SnapshotChunkCodecs.Brotli) is { } brotliInner)
        {
            try
            {
                if (Matches(ChunkCompression.Decompress(brotliInner, plaintext.Length)))
                {
                    return SnapshotChunkCodecs.Brotli;
                }
            }
            catch (InvalidDataException)
            {
            }
        }

        return null;
    }

    /// <summary>Encrypted v3 binds the codec into the chunk key/IV/AAD; v2 (SHA-256 ids) keeps the original derivation.</summary>
    private string? CodecContext(string codec) => _hashScheme.IsKeyed ? SnapshotChunkCodecs.Normalize(codec) : null;

    /// <summary>The bytes that go to storage for already-encoded content: encrypted when encryption is on.</summary>
    private byte[] Seal(byte[] content, string chunkId, string codec)
    {
        return _encryptionEnabled
            ? EncryptionService.EncryptChunkDeterministic(content, _encryptionMasterKey!, chunkId, CodecContext(codec))
            : content;
    }

    private bool ShouldCutChunk(ulong rollingHash, int currentChunkSize)
    {
        if (currentChunkSize < _profile.MinChunkSizeBytes)
        {
            return false;
        }

        if (currentChunkSize >= _profile.MaxChunkSizeBytes)
        {
            return true;
        }

        if (_profile.TargetChunkSizeBytes <= 1)
        {
            return true;
        }

        var targetSize = (ulong)_profile.TargetChunkSizeBytes;
        return rollingHash % targetSize == targetSize - 1;
    }

    private async Task AppendChunkPayloadAsync(
        MemoryStream chunkStream,
        string filePath,
        List<SnapshotChunkManifestEntry> chunkEntries,
        List<ChunkBuildPayload> chunkPayloads,
        Func<ChunkBuildPayload, CancellationToken, Task>? chunkSink,
        CancellationToken cancellationToken)
    {
        var plaintext = chunkStream.ToArray();
        chunkStream.SetLength(0);
        chunkStream.Position = 0;
        await EmitChunkAsync(plaintext, filePath, chunkEntries, chunkPayloads, chunkSink, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitChunkAsync(
        byte[] plaintext,
        string filePath,
        List<SnapshotChunkManifestEntry> chunkEntries,
        List<ChunkBuildPayload> chunkPayloads,
        Func<ChunkBuildPayload, CancellationToken, Task>? chunkSink,
        CancellationToken cancellationToken)
    {
        if (plaintext.Length == 0)
        {
            return;
        }

        var chunkHash = ComputeChunkId(plaintext);
        var overhead = _encryptionEnabled ? EncryptionService.ChunkEncryptionOverheadBytes : 0;

        var compressed = _compress && !ChunkCompression.IsKnownCompressedExtension(filePath)
            ? ChunkCompression.TryCompress(plaintext)
            : null;
        var codec = compressed != null ? SnapshotChunkCodecs.Brotli : SnapshotChunkCodecs.Raw;
        var storedPayload = Seal(compressed ?? plaintext, chunkHash, codec);

        var entry = new SnapshotChunkManifestEntry
        {
            ChunkId = chunkHash,
            ContentHash = chunkHash,
            PlainSizeBytes = plaintext.Length,
            StoredSizeBytes = storedPayload.Length,
            Codec = _recordCodecs ? codec : null
        };
        chunkEntries.Add(entry);

        var payload = new ChunkBuildPayload
        {
            ChunkId = chunkHash,
            ContentHash = chunkHash,
            PlainSizeBytes = plaintext.Length,
            StoredPayload = storedPayload,
            Codec = codec,
            RawStoredSize = plaintext.Length + overhead,
            ComputeCompressedStoredSize = compressed != null
                ? () => (long)storedPayload.Length
                : () => ChunkCompression.TryCompress(plaintext) is { } c ? c.Length + overhead : null,
            IdentifyStoredForm = stored => IdentifyStoredForm(stored, plaintext, chunkHash)
        };

        if (chunkSink != null)
        {
            await chunkSink(payload, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            chunkPayloads.Add(payload);
        }
    }

    private static ulong[] BuildGearTable()
    {
        var table = new ulong[256];
        ulong seed = 0x9E3779B185EBCA87UL;
        for (var index = 0; index < table.Length; index++)
        {
            seed ^= seed >> 12;
            seed ^= seed << 25;
            seed ^= seed >> 27;
            table[index] = seed * 0x2545F4914F6CDD1DUL;
        }

        return table;
    }

    private sealed class RollingWindowHash
    {
        private const ulong HashBase = 257;

        private readonly int _windowSize;
        private readonly ulong[] _gearTable;
        private readonly byte[] _window;
        private readonly ulong _oldestByteMultiplier;
        private int _position;
        private int _count;

        public RollingWindowHash(int windowSize, ulong[] gearTable)
        {
            _windowSize = Math.Max(0, windowSize);
            _gearTable = gearTable;
            _window = _windowSize == 0 ? [] : new byte[_windowSize];
            _oldestByteMultiplier = ComputeOldestByteMultiplier(_windowSize);
        }

        public ulong Value { get; private set; }

        public void Add(byte value)
        {
            var contribution = _gearTable[value];

            unchecked
            {
                if (_windowSize == 0)
                {
                    Value = (Value * HashBase) + contribution;
                    return;
                }

                if (_count < _windowSize)
                {
                    _window[_position] = value;
                    _position = (_position + 1) % _windowSize;
                    _count++;
                    Value = (Value * HashBase) + contribution;
                    return;
                }

                var removed = _window[_position];
                _window[_position] = value;
                _position = (_position + 1) % _windowSize;

                Value = ((Value - (_gearTable[removed] * _oldestByteMultiplier)) * HashBase) + contribution;
            }
        }

        public void Reset()
        {
            Value = 0;
            _position = 0;
            _count = 0;
        }

        private static ulong ComputeOldestByteMultiplier(int windowSize)
        {
            if (windowSize <= 1)
            {
                return 1;
            }

            ulong multiplier = 1;
            unchecked
            {
                for (var index = 1; index < windowSize; index++)
                {
                    multiplier *= HashBase;
                }
            }

            return multiplier;
        }
    }
}

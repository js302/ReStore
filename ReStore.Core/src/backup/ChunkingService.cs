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
}

public class ChunkedFileBuildResult
{
    public SnapshotFileManifestEntry FileEntry { get; set; } = new();
    public List<ChunkBuildPayload> ChunkPayloads { get; set; } = [];
}

public class ChunkingService
{
    private static readonly ulong[] GEAR_TABLE = BuildGearTable();

    private readonly ILogger _logger;
    private readonly ChunkDiffingConfig _chunkConfig;
    private readonly ChunkingProfile _profile;
    private readonly EncryptionService _encryptionService;
    private readonly bool _encryptionEnabled;
    private readonly byte[]? _encryptionMasterKey;

    public ChunkingService(
        ILogger logger,
        ChunkDiffingConfig chunkConfig,
        EncryptionService encryptionService,
        bool encryptionEnabled,
        byte[]? encryptionMasterKey)
    {
        _logger = logger;
        _chunkConfig = chunkConfig;
        _profile = ChunkingProfile.FromConfig(chunkConfig);
        _encryptionService = encryptionService;
        _encryptionEnabled = encryptionEnabled;
        _encryptionMasterKey = encryptionMasterKey;

        if (_encryptionEnabled && (_encryptionMasterKey == null || _encryptionMasterKey.Length == 0))
        {
            throw new ArgumentException("Encryption master key is required when chunk encryption is enabled", nameof(encryptionMasterKey));
        }
    }

    public async Task<ChunkedFileBuildResult> BuildFileManifestEntryAsync(string filePath, string baseDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Cannot chunk missing file: {filePath}", filePath);
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory cannot be null or empty", nameof(baseDirectory));
        }

        var fileInfo = new FileInfo(filePath);
        var fileHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunkPayloads = new List<ChunkBuildPayload>();
        var chunkEntries = new List<SnapshotChunkManifestEntry>();

        using var chunkStream = new MemoryStream(_profile.MaxChunkSizeBytes);
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 128 * 1024, useAsync: true);

        var readBuffer = new byte[128 * 1024];
        var currentChunkSize = 0;
        var rollingWindow = new Queue<byte>(_profile.RollingHashWindowSize);
        ulong rollingHash = 0;

        while (true)
        {
            var bytesRead = await fileStream.ReadAsync(readBuffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            fileHasher.AppendData(readBuffer.AsSpan(0, bytesRead));

            for (var index = 0; index < bytesRead; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentByte = readBuffer[index];
                chunkStream.WriteByte(currentByte);
                currentChunkSize++;

                rollingWindow.Enqueue(currentByte);
                if (rollingWindow.Count > _profile.RollingHashWindowSize)
                {
                    rollingWindow.Dequeue();
                }

                rollingHash = ComputeRollingWindowHash(rollingWindow);

                if (!ShouldCutChunk(rollingHash, currentChunkSize))
                {
                    continue;
                }

                AppendChunkPayload(chunkStream, chunkEntries, chunkPayloads);
                if (chunkEntries.Count > _chunkConfig.MaxChunksPerFile)
                {
                    throw new InvalidOperationException($"File exceeds maxChunksPerFile safety limit ({_chunkConfig.MaxChunksPerFile}): {filePath}");
                }

                currentChunkSize = 0;
                rollingWindow.Clear();
                rollingHash = 0;
            }
        }

        if (chunkStream.Length > 0)
        {
            AppendChunkPayload(chunkStream, chunkEntries, chunkPayloads);
        }

        if (chunkEntries.Count > _chunkConfig.MaxChunksPerFile)
        {
            throw new InvalidOperationException($"File exceeds maxChunksPerFile safety limit ({_chunkConfig.MaxChunksPerFile}): {filePath}");
        }

        var relativePath = Path.GetRelativePath(baseDirectory, filePath)
            .Replace(Path.DirectorySeparatorChar, '/');

        var fileHash = Convert.ToHexStringLower(fileHasher.GetHashAndReset());
        var fileEntry = new SnapshotFileManifestEntry
        {
            RelativePath = relativePath,
            SizeBytes = fileInfo.Length,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
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

    private static ulong ComputeRollingWindowHash(IEnumerable<byte> rollingWindow)
    {
        ulong hash = 0;
        foreach (var value in rollingWindow)
        {
            hash = (hash << 1) + GEAR_TABLE[value];
        }

        return hash;
    }

    private void AppendChunkPayload(
        MemoryStream chunkStream,
        List<SnapshotChunkManifestEntry> chunkEntries,
        List<ChunkBuildPayload> chunkPayloads)
    {
        var plaintext = chunkStream.ToArray();
        if (plaintext.Length == 0)
        {
            chunkStream.SetLength(0);
            return;
        }

        var chunkHash = Convert.ToHexStringLower(SHA256.HashData(plaintext));
        var storedPayload = _encryptionEnabled
            ? EncryptionService.EncryptChunkDeterministic(plaintext, _encryptionMasterKey!, chunkHash)
            : plaintext;

        chunkEntries.Add(new SnapshotChunkManifestEntry
        {
            ChunkId = chunkHash,
            ContentHash = chunkHash,
            PlainSizeBytes = plaintext.Length,
            StoredSizeBytes = storedPayload.Length
        });

        chunkPayloads.Add(new ChunkBuildPayload
        {
            ChunkId = chunkHash,
            ContentHash = chunkHash,
            PlainSizeBytes = plaintext.Length,
            StoredPayload = storedPayload
        });

        chunkStream.SetLength(0);
        chunkStream.Position = 0;
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
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

public class ChunkingProfile
{
    public int MinChunkSizeBytes { get; set; }
    public int TargetChunkSizeBytes { get; set; }
    public int MaxChunkSizeBytes { get; set; }
    public int RollingHashWindowSize { get; set; }

    /// <summary>
    /// Chunk-boundary algorithm: 1 = pre-1.7 Queue&lt;byte&gt; rolling hash, 2 = RollingWindowHash (1.7+), 3 = fast chunker (v3 manifests).
    /// 0 means the manifest did not record it; use <see cref="SnapshotManifest.GetChunkingAlgorithmVersion"/>.
    /// Excluded from the v2 root hash so existing v2 manifests keep validating; always present in, and
    /// covered by, the v3 root hash.
    /// </summary>
    public int AlgorithmVersion { get; set; }

    /// <summary>Algorithm written by v2 manifest writers (RollingWindowHash).</summary>
    public const int CurrentAlgorithmVersion = 2;

    /// <summary>Fast chunker (span copies, cut-point skipping, mask cut test); written only with v3 manifests.</summary>
    public const int FastChunkerAlgorithmVersion = 3;

    /// <summary>Chunking algorithm used when writing a manifest of the given format version.</summary>
    public static int AlgorithmForManifestVersion(int manifestVersion)
        => manifestVersion >= SnapshotManifest.VersionV3 ? FastChunkerAlgorithmVersion : CurrentAlgorithmVersion;

    public static ChunkingProfile FromConfig(ChunkDiffingConfig config, int algorithmVersion = CurrentAlgorithmVersion)
    {
        return new ChunkingProfile
        {
            MinChunkSizeBytes = config.MinChunkSizeKB * 1024,
            TargetChunkSizeBytes = config.TargetChunkSizeKB * 1024,
            MaxChunkSizeBytes = config.MaxChunkSizeKB * 1024,
            RollingHashWindowSize = config.RollingHashWindowSize,
            AlgorithmVersion = algorithmVersion
        };
    }
}

public class SnapshotManifest
{
    /// <summary>Manifest format written by v1.6/v1.7: plain JSON, no file metadata.</summary>
    public const int VersionV2 = 2;

    /// <summary>
    /// Gzip-compressed JSON container with file attributes/creation time, empty directories and a root hash that
    /// covers the chunking algorithm. Later v3 additions (per-chunk codec, encrypted body/header) are optional
    /// fields: readers ignore fields they do not know, and the root hash only covers what a given writer emitted.
    /// </summary>
    public const int VersionV3 = 3;

    public int Version { get; set; } = VersionV2;
    public string SnapshotId { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string BackupMode { get; set; } = BackupType.Incremental.ToString();
    public bool EncryptionEnabled { get; set; }
    public string? EncryptionSalt { get; set; }
    public int KeyDerivationIterations { get; set; } = 1_000_000;
    public string? ChunkStorageNamespace { get; set; }
    public ChunkingProfile Profile { get; set; } = new();
    public List<SnapshotFileManifestEntry> Files { get; set; } = [];

    /// <summary>v3 only: directories recorded because they hold nothing (so restore can recreate them). Null in v2.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SnapshotDirectoryManifestEntry>? Directories { get; set; }

    public string RootHash { get; set; } = string.Empty;

    /// <summary>
    /// v3 encrypted repositories: <see cref="ChunkHashScheme.HmacSha256Name"/> (chunk ids and content hashes are HMACs under the
    /// repository key). Null = plain SHA-256 (v2, unencrypted v3). Part of the (encrypted) body and of the v3 root hash.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChunkIdScheme { get; set; }

    /// <summary>Repository keys this manifest was decrypted with (set by the reader, never serialized); needed to verify HMAC ids.</summary>
    [JsonIgnore]
    public RepositoryKeys? Keys { get; set; }

    /// <summary>How chunk ids and content hashes of this snapshot are computed and checked.</summary>
    public ChunkHashScheme GetHashScheme()
    {
        if (string.Equals(ChunkIdScheme, ChunkHashScheme.HmacSha256Name, StringComparison.Ordinal))
        {
            return Keys?.HashScheme
                ?? throw new InvalidOperationException("The snapshot uses keyed chunk ids but the repository keys are not available.");
        }

        return ChunkHashScheme.Sha256;
    }

    // Commit 16c5df2 (v1.7.0, RollingWindowHash) date, UTC. Manifests that predate it used algorithm 1.
    private static readonly DateTime RollingWindowHashReleaseUtc = new(2026, 8, 29, 14, 21, 58, DateTimeKind.Utc);

    /// <summary>Recorded algorithm version, or inferred from <see cref="CreatedUtc"/> when the manifest has none.</summary>
    public int GetChunkingAlgorithmVersion()
    {
        if (Profile.AlgorithmVersion > 0)
        {
            return Profile.AlgorithmVersion;
        }

        return CreatedUtc.ToUniversalTime() >= RollingWindowHashReleaseUtc ? 2 : 1;
    }
}

public class SnapshotFileManifestEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastModifiedUtc { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public List<SnapshotChunkManifestEntry> Chunks { get; set; } = [];

    /// <summary>v3 only: <see cref="FileAttributes"/> flags at backup time; null when the manifest does not record them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attributes { get; set; }

    /// <summary>v3 only: file creation time; null when the manifest does not record it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedUtc { get; set; }
}

/// <summary>v3: a directory recorded explicitly (currently the empty ones, which no file entry implies).</summary>
public class SnapshotDirectoryManifestEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public int Attributes { get; set; }
    public DateTime LastWriteUtc { get; set; }
}

public class SnapshotChunkManifestEntry
{
    public string ChunkId { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int PlainSizeBytes { get; set; }
    public int StoredSizeBytes { get; set; }

    /// <summary>
    /// v3: how the stored payload is encoded (null = <see cref="SnapshotChunkCodecs.Raw"/>, otherwise
    /// <see cref="SnapshotChunkCodecs.Brotli"/>). Readers refuse any other value. Never written in v2 manifests.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Codec { get; set; }
}

public static class SnapshotChunkCodecs
{
    public const string Raw = "raw";
    public const string Brotli = "brotli";

    public static bool IsBrotli(string? codec) => string.Equals(codec, Brotli, StringComparison.OrdinalIgnoreCase);

    public static bool IsRaw(string? codec) => string.IsNullOrEmpty(codec) || string.Equals(codec, Raw, StringComparison.OrdinalIgnoreCase);

    /// <summary>Canonical value for comparisons: <c>raw</c> or <c>brotli</c>.</summary>
    public static string Normalize(string? codec) => IsBrotli(codec) ? Brotli : Raw;
}

public static class SnapshotManifestHasher
{
    public static string ComputeRootHash(SnapshotManifest manifest)
    {
        return manifest.Version >= SnapshotManifest.VersionV3 ? ComputeRootHashV3(manifest) : ComputeRootHashV2(manifest);
    }

    /// <summary>
    /// v3: the v2 content plus everything v3 adds (algorithm version, file metadata, codecs, directories), so none of it
    /// can be altered without invalidating the root hash. Optional values hash as an empty field when absent.
    /// </summary>
    private static string ComputeRootHashV3(SnapshotManifest manifest)
    {
        var chunkStorageNamespace = SnapshotStoragePaths.NormalizeChunkStorageNamespace(manifest.ChunkStorageNamespace);

        var builder = new StringBuilder();
        builder.Append(manifest.Version).Append('|')
            .Append(manifest.SnapshotId).Append('|')
            .Append(manifest.Group).Append('|')
            .Append(manifest.CreatedUtc.ToUniversalTime().Ticks).Append('|')
            .Append(manifest.BackupMode).Append('|')
            .Append(manifest.EncryptionEnabled).Append('|')
            .Append(manifest.KeyDerivationIterations).Append('|')
            .Append(manifest.Profile.MinChunkSizeBytes).Append('|')
            .Append(manifest.Profile.TargetChunkSizeBytes).Append('|')
            .Append(manifest.Profile.MaxChunkSizeBytes).Append('|')
            .Append(manifest.Profile.RollingHashWindowSize).Append('|')
            .Append(manifest.Profile.AlgorithmVersion).Append('|')
            .Append(chunkStorageNamespace ?? string.Empty).Append('|')
            .Append(manifest.Files.Count).Append('|')
            .Append(manifest.Directories?.Count ?? 0);

        // Only encrypted v3 writers set a scheme, so unencrypted v3 hashes stay exactly as before.
        if (manifest.ChunkIdScheme != null)
        {
            builder.Append('|').Append(manifest.ChunkIdScheme);
        }

        builder.Append('\n');

        foreach (var file in manifest.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(file.RelativePath).Append('|')
                .Append(file.SizeBytes).Append('|')
                .Append(file.LastModifiedUtc.ToUniversalTime().Ticks).Append('|')
                .Append(file.ContentHash).Append('|')
                .Append(file.Attributes?.ToString() ?? string.Empty).Append('|')
                .Append(file.CreatedUtc?.ToUniversalTime().Ticks.ToString() ?? string.Empty).Append('|')
                .Append(file.Chunks.Count).Append('\n');

            foreach (var chunk in file.Chunks)
            {
                builder.Append(chunk.ChunkId).Append('|')
                    .Append(chunk.ContentHash).Append('|')
                    .Append(chunk.PlainSizeBytes).Append('|')
                    .Append(chunk.StoredSizeBytes).Append('|')
                    .Append(chunk.Codec ?? string.Empty).Append('\n');
            }
        }

        foreach (var directory in (manifest.Directories ?? []).OrderBy(d => d.RelativePath, StringComparer.Ordinal))
        {
            builder.Append("dir|").Append(directory.RelativePath).Append('|')
                .Append(directory.Attributes).Append('|')
                .Append(directory.LastWriteUtc.ToUniversalTime().Ticks).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string ComputeRootHashV2(SnapshotManifest manifest)
    {
        var chunkStorageNamespace = SnapshotStoragePaths.NormalizeChunkStorageNamespace(manifest.ChunkStorageNamespace);

        var builder = new StringBuilder();
        builder.Append(manifest.Version).Append('|')
            .Append(manifest.SnapshotId).Append('|')
            .Append(manifest.Group).Append('|')
            .Append(manifest.CreatedUtc.ToUniversalTime().Ticks).Append('|')
            .Append(manifest.BackupMode).Append('|')
            .Append(manifest.EncryptionEnabled).Append('|')
            .Append(manifest.KeyDerivationIterations).Append('|')
            .Append(manifest.Profile.MinChunkSizeBytes).Append('|')
            .Append(manifest.Profile.TargetChunkSizeBytes).Append('|')
            .Append(manifest.Profile.MaxChunkSizeBytes).Append('|')
            .Append(manifest.Profile.RollingHashWindowSize).Append('|');

        if (!string.IsNullOrWhiteSpace(chunkStorageNamespace))
        {
            builder.Append(chunkStorageNamespace).Append('|');
        }

        builder
            .Append(manifest.Files.Count).Append('\n');

        foreach (var file in manifest.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(file.RelativePath).Append('|')
                .Append(file.SizeBytes).Append('|')
                .Append(file.LastModifiedUtc.ToUniversalTime().Ticks).Append('|')
                .Append(file.ContentHash).Append('|')
                .Append(file.Chunks.Count).Append('\n');

            foreach (var chunk in file.Chunks)
            {
                builder.Append(chunk.ChunkId).Append('|')
                    .Append(chunk.ContentHash).Append('|')
                    .Append(chunk.PlainSizeBytes).Append('|')
                    .Append(chunk.StoredSizeBytes).Append('\n');
            }
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hashBytes);
    }

    public static bool IsValid(SnapshotManifest manifest)
    {
        try
        {
            var expected = ComputeRootHash(manifest);
            return expected.Equals(manifest.RootHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// The manifest container. v2 is indented plain JSON; v3 is gzip-compressed JSON under the same
/// <c>.manifest.json</c> name. Readers detect the form by magic bytes, never by the file name, so either loads from any path.
/// </summary>
public static class SnapshotManifestSerializer
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <param name="keys">Repository keys: when given (encrypted v3) the manifest is written as an encrypted envelope.</param>
    public static byte[] Serialize(SnapshotManifest manifest, RepositoryKeys? keys = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (keys != null)
        {
            if (manifest.Version < SnapshotManifest.VersionV3 || manifest.ChunkIdScheme == null)
            {
                throw new InvalidOperationException("Only v3 manifests with keyed chunk ids can be encrypted.");
            }

            // Compact JSON: the body is compressed and encrypted, readability does not matter.
            return EncryptedManifestEnvelope.Seal(JsonSerializer.SerializeToUtf8Bytes(manifest), manifest.SnapshotId, SnapshotStoragePaths.BuildGroupStorageKey(manifest.Group), keys);
        }

        if (manifest.ChunkIdScheme != null)
        {
            throw new InvalidOperationException("A manifest with keyed chunk ids must be written encrypted.");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, WriteOptions);
        if (manifest.Version < SnapshotManifest.VersionV3)
        {
            return json;
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(json);
        }

        return output.ToArray();
    }

    public static bool IsGzip(ReadOnlySpan<byte> data) => data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b;

    /// <exception cref="ManifestKeyRequiredException">The manifest is encrypted and <paramref name="keys"/> is null.</exception>
    /// <param name="remotePath">Where the bytes were read from; for an encrypted manifest the header must match it (snapshot id and group folder).</param>
    public static SnapshotManifest Deserialize(byte[] data, RepositoryKeys? keys = null, string? remotePath = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        byte[] json = data;
        EncryptedManifestHeader? sealedHeader = null;
        if (EncryptedManifestEnvelope.IsEncrypted(data))
        {
            if (keys == null)
            {
                throw new ManifestKeyRequiredException(EncryptedManifestEnvelope.ReadHeader(data));
            }

            json = EncryptedManifestEnvelope.Open(data, keys, out sealedHeader);
            if (remotePath != null)
            {
                EncryptedManifestEnvelope.AssertLocation(sealedHeader, remotePath);
            }
        }
        else if (IsGzip(data))
        {
            using var input = new MemoryStream(data);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            json = output.ToArray();
        }

        var start = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? 3 : 0;
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(json.AsSpan(start))
            ?? throw new InvalidOperationException("Failed to deserialize snapshot manifest");
        if (sealedHeader != null)
        {
            if (!string.Equals(sealedHeader.SnapshotId, manifest.SnapshotId, StringComparison.Ordinal)
                || manifest.ChunkIdScheme == null)
            {
                throw new InvalidOperationException("The encrypted snapshot manifest does not match its header.");
            }

            manifest.Keys = keys;
        }
        else if (manifest.ChunkIdScheme != null)
        {
            throw new InvalidOperationException("A manifest with keyed chunk ids must be encrypted; this one is not.");
        }

        EnsureSupported(manifest);
        return manifest;
    }

    /// <summary>JSON can null out any member; every consumer then dereferences it. Refuse such a manifest up front, with a clean error.</summary>
    private static void EnsureWellFormed(SnapshotManifest manifest)
    {
        const string Malformed = "The snapshot manifest is malformed: ";
        if (manifest.Files == null || manifest.Profile == null || manifest.SnapshotId == null || manifest.Group == null || manifest.BackupMode == null || manifest.RootHash == null)
        {
            throw new InvalidOperationException(Malformed + "a required member is missing or null.");
        }

        foreach (var file in manifest.Files)
        {
            if (file == null || file.RelativePath == null || file.ContentHash == null || file.Chunks == null || file.Chunks.Any(c => c == null || c.ChunkId == null || c.ContentHash == null))
            {
                throw new InvalidOperationException(Malformed + "a file entry has a missing or null member.");
            }
        }

        if (manifest.Directories != null && manifest.Directories.Any(d => d == null || d.RelativePath == null))
        {
            throw new InvalidOperationException(Malformed + "a directory entry has a missing or null member.");
        }
    }

    /// <summary>
    /// Refuses what this build cannot decode, rather than restoring garbage or (for repair/retention) treating a
    /// snapshot as unreferenced: a manifest from a newer format version, or a chunk stored with an unknown codec.
    /// </summary>
    private static void EnsureSupported(SnapshotManifest manifest)
    {
        EnsureWellFormed(manifest);

        if (manifest.Version > SnapshotManifest.VersionV3)
        {
            throw new NotSupportedException(
                $"Snapshot manifest format version {manifest.Version} was written by a newer ReStore. Update ReStore to read it.");
        }

        Int128 totalBytes = 0;
        foreach (var file in manifest.Files)
        {
            // Sizes feed progress totals and sums; a hostile or corrupt manifest must not make those overflow or go negative.
            if (file.SizeBytes < 0 || file.Chunks.Any(c => c.PlainSizeBytes < 0 || c.StoredSizeBytes < 0))
            {
                throw new InvalidOperationException($"Snapshot manifest lists a negative size for '{file.RelativePath}'.");
            }

            totalBytes += file.SizeBytes;
            if (totalBytes > long.MaxValue)
            {
                throw new InvalidOperationException("Snapshot manifest lists implausible file sizes.");
            }

            foreach (var chunk in file.Chunks)
            {
                if (chunk.Codec != null
                    && !string.Equals(chunk.Codec, SnapshotChunkCodecs.Raw, StringComparison.OrdinalIgnoreCase)
                    && !SnapshotChunkCodecs.IsBrotli(chunk.Codec))
                {
                    throw new NotSupportedException(
                        $"Snapshot chunk '{chunk.ChunkId}' uses the unsupported codec '{chunk.Codec}'. Update ReStore to read it.");
                }
            }
        }
    }

    public static async Task<SnapshotManifest> ReadFileAsync(string path, CancellationToken cancellationToken = default)
        => Deserialize(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));

    public static async Task<SnapshotManifest> ReadFileAsync(string path, RepositoryKeys? keys, string remotePath, CancellationToken cancellationToken = default)
    {
        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Deserialize(data, keys, remotePath);
    }

    public static Task WriteFileAsync(string path, SnapshotManifest manifest, CancellationToken cancellationToken = default)
        => WriteFileAsync(path, manifest, null, cancellationToken);

    public static Task WriteFileAsync(string path, SnapshotManifest manifest, RepositoryKeys? keys, CancellationToken cancellationToken = default)
        => File.WriteAllBytesAsync(path, Serialize(manifest, keys), cancellationToken);
}

public static class SnapshotStoragePaths
{
    public static string BuildSnapshotId()
    {
        return $"snapshot_{DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}_{Guid.NewGuid():N}";
    }

    public static string GetManifestPath(string group, string snapshotId)
    {
        var key = BuildGroupStorageKey(group);
        return $"snapshots/{key}/{snapshotId}.manifest.json";
    }

    public static string GetHeadPath(string group)
    {
        var key = BuildGroupStorageKey(group);
        return $"snapshots/{key}/HEAD";
    }

    /// <summary>The repository index of a group: <c>snapshots/&lt;groupKey&gt;/group.json</c>.</summary>
    public static string GetGroupIndexPath(string group)
    {
        var key = BuildGroupStorageKey(group);
        return $"snapshots/{key}/group.json";
    }

    public static string GetChunkPath(string chunkId, string? chunkStorageNamespace = null)
    {
        var normalizedChunkId = NormalizeChunkId(chunkId);
        var normalizedNamespace = NormalizeChunkStorageNamespace(chunkStorageNamespace);
        var prefix = normalizedChunkId.Length >= 2 ? normalizedChunkId[..2] : normalizedChunkId;

        if (string.IsNullOrWhiteSpace(normalizedNamespace))
        {
            return $"chunks/{prefix}/{normalizedChunkId}.chunk";
        }

        return $"chunks/{normalizedNamespace}/{prefix}/{normalizedChunkId}.chunk";
    }

    public static string NormalizeChunkId(string chunkId)
    {
        if (string.IsNullOrWhiteSpace(chunkId))
        {
            throw new ArgumentException("Chunk id cannot be null or empty", nameof(chunkId));
        }

        var normalized = chunkId.Trim().ToLowerInvariant();
        if (normalized.Length is < 2 or > 128)
        {
            throw new ArgumentException("Chunk id length must be between 2 and 128 characters.", nameof(chunkId));
        }

        if (!char.IsAsciiLetterOrDigit(normalized[0]))
        {
            throw new ArgumentException("Chunk id must start with an alphanumeric character.", nameof(chunkId));
        }

        if (!normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException("Chunk id may only contain lowercase letters, digits, '-' and '_'.", nameof(chunkId));
        }

        return normalized;
    }

    public static string? NormalizeChunkStorageNamespace(string? chunkStorageNamespace)
    {
        if (string.IsNullOrWhiteSpace(chunkStorageNamespace))
        {
            return null;
        }

        var normalized = chunkStorageNamespace.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 64)
        {
            throw new ArgumentException("Chunk storage namespace length must be between 3 and 64 characters.", nameof(chunkStorageNamespace));
        }

        if (!char.IsAsciiLetterOrDigit(normalized[0]))
        {
            throw new ArgumentException("Chunk storage namespace must start with an alphanumeric character.", nameof(chunkStorageNamespace));
        }

        if (!normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException("Chunk storage namespace may only contain lowercase letters, digits, '-' and '_'.", nameof(chunkStorageNamespace));
        }

        return normalized;
    }

    public static string BuildEncryptedChunkNamespace(byte[] encryptionMasterKey)
    {
        if (encryptionMasterKey == null || encryptionMasterKey.Length == 0)
        {
            throw new ArgumentException("Encryption master key cannot be null or empty.", nameof(encryptionMasterKey));
        }

        var keyHash = SHA256.HashData(encryptionMasterKey);
        return $"enc_{Convert.ToHexStringLower(keyHash)[..24]}";
    }

    public static string BuildGroupStorageKey(string group)
    {
        var normalizedGroup = NormalizePathForKey(group);
        var groupName = Path.GetFileName(normalizedGroup.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(groupName))
        {
            groupName = "root";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedGroup));
        var hashText = Convert.ToHexStringLower(hash)[..16];
        return $"{SanitizeSegment(groupName)}_{hashText}";
    }

    private static string NormalizePathForKey(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return path;
        }
    }

    private static string SanitizeSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var buffer = value
            .Select(character => invalidChars.Contains(character) || character == ' ' ? '_' : character)
            .ToArray();

        return new string(buffer).Trim('_').ToLowerInvariant();
    }
}

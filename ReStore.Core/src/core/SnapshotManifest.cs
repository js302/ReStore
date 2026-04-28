using System.Security.Cryptography;
using System.Text;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

public class ChunkingProfile
{
    public int MinChunkSizeBytes { get; set; }
    public int TargetChunkSizeBytes { get; set; }
    public int MaxChunkSizeBytes { get; set; }
    public int RollingHashWindowSize { get; set; }

    public static ChunkingProfile FromConfig(ChunkDiffingConfig config)
    {
        return new ChunkingProfile
        {
            MinChunkSizeBytes = config.MinChunkSizeKB * 1024,
            TargetChunkSizeBytes = config.TargetChunkSizeKB * 1024,
            MaxChunkSizeBytes = config.MaxChunkSizeKB * 1024,
            RollingHashWindowSize = config.RollingHashWindowSize
        };
    }
}

public class SnapshotManifest
{
    public int Version { get; set; } = 2;
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
    public string RootHash { get; set; } = string.Empty;
}

public class SnapshotFileManifestEntry
{
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastModifiedUtc { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public List<SnapshotChunkManifestEntry> Chunks { get; set; } = [];
}

public class SnapshotChunkManifestEntry
{
    public string ChunkId { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public int PlainSizeBytes { get; set; }
    public int StoredSizeBytes { get; set; }
}

public static class SnapshotManifestHasher
{
    public static string ComputeRootHash(SnapshotManifest manifest)
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

public static class SnapshotStoragePaths
{
    public static string BuildSnapshotId()
    {
        return $"snapshot_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
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

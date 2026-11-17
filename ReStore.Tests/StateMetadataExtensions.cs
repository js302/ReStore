using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Test helpers over the per-(group, storage type) file metadata of <see cref="SystemState"/>.</summary>
internal static class StateMetadataExtensions
{
    /// <summary>Records the file's current size, mtime and hash as if a snapshot had just captured it.</summary>
    public static async Task SeedFileMetadataAsync(this SystemState state, string group, string storageType, string filePath)
    {
        var info = new FileInfo(filePath);
        state.SetFileMetadata(group, storageType, filePath, new FileMetadata
        {
            FilePath = filePath,
            Size = info.Length,
            LastModified = info.LastWriteTimeUtc,
            Hash = await FileHasher.ComputeHashAsync(filePath)
        });
    }

    public static bool HasMetadata(this SystemState state, string group, string path, string storageType = "local")
        => state.TryGetFileMetadata(group, storageType, path, out _);

    public static FileMetadata MetadataFor(this SystemState state, string group, string path, string storageType = "local")
    {
        state.TryGetFileMetadata(group, storageType, path, out var metadata).Should().BeTrue($"'{path}' should have metadata for {storageType}");
        return metadata!;
    }
}

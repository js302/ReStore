using ReStore.Core.src.core;

namespace ReStore.Tests;

/// <summary>Reads/writes manifest files in whatever container (plain v2 JSON, gzip v3, or encrypted v3) their version uses.</summary>
internal static class ManifestIo
{
    public const string DefaultPassword = "hunter2hunter2";

    /// <summary>Encrypted v3 manifests are opened with the key file found under an ancestor directory ("keys/repository.keys.json").</summary>
    public static SnapshotManifest Read(string path) => Read(path, DefaultPassword);

    public static SnapshotManifest Read(string path, string password)
    {
        var data = File.ReadAllBytes(path);
        if (!EncryptedManifestEnvelope.IsEncrypted(data))
        {
            return SnapshotManifestSerializer.Deserialize(data);
        }

        return SnapshotManifestSerializer.Deserialize(data, UnlockNear(path, password));
    }

    public static RepositoryKeys UnlockNear(string path, string password = DefaultPassword)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(path)); dir != null; dir = Path.GetDirectoryName(dir))
        {
            var keyFile = Path.Combine(dir, "keys", "repository.keys.json");
            if (File.Exists(keyFile))
            {
                return RepositoryKeyFiles.Unlock(RepositoryKeyFiles.Parse(File.ReadAllBytes(keyFile)), password);
            }
        }

        throw new FileNotFoundException("No keys/repository.keys.json above " + path);
    }

    public static void Write(string path, SnapshotManifest manifest) => File.WriteAllBytes(path, SnapshotManifestSerializer.Serialize(manifest));

    public static Task WriteAsync(string path, SnapshotManifest manifest) => File.WriteAllBytesAsync(path, SnapshotManifestSerializer.Serialize(manifest));
}

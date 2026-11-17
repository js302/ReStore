using System.Text.Json;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

/// <summary>
/// What <c>snapshots/&lt;groupKey&gt;/group.json</c> records about a group, so a repository can be understood
/// from storage alone (the group key is only a hash of the original path).
/// </summary>
public sealed class GroupIndex
{
    public string GroupPath { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string InstallId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>True when the group's snapshots are encrypted (their chunks live under <see cref="ChunkStorageNamespace"/>).</summary>
    public bool Encrypted { get; set; }

    public string? ChunkStorageNamespace { get; set; }
}

/// <summary>
/// The stored form of group.json. Plain: <see cref="Index"/> is set. Encrypted: the index JSON is encrypted with the
/// <see cref="EncryptionService"/> file envelope and only the key-derivation parameters, which a new machine needs to
/// derive the key from the password, are in clear.
/// </summary>
public sealed class GroupIndexFile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public bool Encrypted { get; set; }
    public GroupIndex? Index { get; set; }

    // Encrypted form: the EncryptionMetadata fields (salt/iterations are the clear header) and the ciphertext file.
    public string? Salt { get; set; }
    public int KeyDerivationIterations { get; set; }
    public string? IV { get; set; }
    public string? EncryptedDek { get; set; }
    public string? Algorithm { get; set; }
    public int MetadataVersion { get; set; }
    public string? Payload { get; set; }

    // Keyed form (encrypted v3 repositories): Payload is AES-GCM(manifest-key, index JSON) with the key file reference as AAD;
    // no password-derivation parameters are needed (the key file holds the slots).
    public string? KeyFileId { get; set; }
    public string? Nonce { get; set; }
    public string? Tag { get; set; }
}

public enum GroupIndexReadStatus
{
    Ok,
    Missing,

    /// <summary>The index is encrypted and no password was available.</summary>
    PasswordRequired,

    /// <summary>Corrupt, unsupported format, or the password does not decrypt it.</summary>
    Unreadable
}

public sealed record GroupIndexReadResult(GroupIndexReadStatus Status, GroupIndex? Index, string? Message = null);

/// <summary>Reads and writes <c>group.json</c>. Absence is never an error for readers.</summary>
public sealed class GroupIndexStore(ILogger logger, EncryptionService? encryptionService = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly EncryptionService _encryption = encryptionService ?? new EncryptionService(logger);

    public async Task<GroupIndexReadResult> ReadAsync(IStorage storage, string path, string? password, CancellationToken cancellationToken = default, RepositoryKeys? keys = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"restore_groupindex_{Guid.NewGuid():N}.json");
        try
        {
            try
            {
                await storage.DownloadAsync(path, temp, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return new GroupIndexReadResult(GroupIndexReadStatus.Missing, null);
            }

            var json = await File.ReadAllTextAsync(temp, cancellationToken).ConfigureAwait(false);
            return await ParseAsync(json, password, cancellationToken, keys, storage).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    internal async Task<GroupIndexReadResult> ParseAsync(string json, string? password, CancellationToken cancellationToken = default, RepositoryKeys? keys = null, IStorage? storage = null)
    {
        GroupIndexFile? file;
        try
        {
            file = JsonSerializer.Deserialize<GroupIndexFile>(json);
        }
        catch (JsonException ex)
        {
            return new GroupIndexReadResult(GroupIndexReadStatus.Unreadable, null, $"group.json is not valid JSON: {ex.Message}");
        }

        if (file == null || file.FormatVersion is < 1 or > GroupIndexFile.CurrentFormatVersion)
        {
            return new GroupIndexReadResult(GroupIndexReadStatus.Unreadable, null, "group.json has an unsupported format.");
        }

        if (!file.Encrypted)
        {
            return file.Index == null
                ? new GroupIndexReadResult(GroupIndexReadStatus.Unreadable, null, "group.json holds no index.")
                : new GroupIndexReadResult(GroupIndexReadStatus.Ok, file.Index);
        }

        if (string.IsNullOrEmpty(password))
        {
            return new GroupIndexReadResult(GroupIndexReadStatus.PasswordRequired, null);
        }

        if (file.KeyFileId != null)
        {
            return await ParseKeyedAsync(file, password, keys, storage, cancellationToken).ConfigureAwait(false);
        }

        var tempIn = Path.Combine(Path.GetTempPath(), $"restore_groupindex_{Guid.NewGuid():N}.enc");
        var tempOut = Path.Combine(Path.GetTempPath(), $"restore_groupindex_{Guid.NewGuid():N}.dec");
        try
        {
            var metadata = new EncryptionMetadata
            {
                Salt = Convert.FromBase64String(file.Salt ?? ""),
                IV = Convert.FromBase64String(file.IV ?? ""),
                EncryptedDEK = Convert.FromBase64String(file.EncryptedDek ?? ""),
                Algorithm = file.Algorithm ?? "AES-256-GCM",
                Version = file.MetadataVersion,
                KeyDerivationIterations = file.KeyDerivationIterations
            };
            await File.WriteAllBytesAsync(tempIn, Convert.FromBase64String(file.Payload ?? ""), cancellationToken).ConfigureAwait(false);
            await _encryption.DecryptFileAsync(tempIn, tempOut, password, metadata).ConfigureAwait(false);
            var index = JsonSerializer.Deserialize<GroupIndex>(await File.ReadAllTextAsync(tempOut, cancellationToken).ConfigureAwait(false));
            return index == null
                ? new GroupIndexReadResult(GroupIndexReadStatus.Unreadable, null, "group.json decrypted to nothing.")
                : new GroupIndexReadResult(GroupIndexReadStatus.Ok, index);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GroupIndexReadResult(GroupIndexReadStatus.Unreadable, null, $"group.json could not be decrypted (wrong password?): {ex.Message}");
        }
        finally
        {
            TryDelete(tempIn);
            TryDelete(tempOut);
        }
    }

    private static byte[] KeyedAad(string keyFileId) => System.Text.Encoding.UTF8.GetBytes($"restore-group-index-v1|{keyFileId}");

    private static async Task<GroupIndexReadResult> ParseKeyedAsync(GroupIndexFile file, string? password, RepositoryKeys? keys, IStorage? storage, CancellationToken cancellationToken)
    {
        try
        {
            if (keys == null)
            {
                var keyFile = storage == null ? null : await RepositoryKeyStore.ReadAsync(storage, cancellationToken).ConfigureAwait(false);
                if (keyFile == null || keyFile.KeyFileId != file.KeyFileId)
                {
                    return new GroupIndexReadResult(GroupIndexReadStatus.Unreadable, null, "group.json belongs to a repository key file that is not available.");
                }

                keys = RepositoryKeyFiles.Unlock(keyFile, password!);
            }
            else if (keys.KeyFileId != file.KeyFileId)
            {
                return new GroupIndexReadResult(GroupIndexReadStatus.Unreadable, null, "group.json belongs to a different repository key.");
            }

            var plain = keys.OpenWithManifestKey(
                Convert.FromBase64String(file.Payload ?? ""),
                Convert.FromBase64String(file.Nonce ?? ""),
                Convert.FromBase64String(file.Tag ?? ""),
                KeyedAad(file.KeyFileId!));
            var index = JsonSerializer.Deserialize<GroupIndex>(plain);
            return index == null
                ? new GroupIndexReadResult(GroupIndexReadStatus.Unreadable, null, "group.json decrypted to nothing.")
                : new GroupIndexReadResult(GroupIndexReadStatus.Ok, index);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GroupIndexReadResult(GroupIndexReadStatus.Unreadable, null, $"group.json could not be decrypted (wrong password?): {ex.Message}");
        }
    }

    /// <summary>
    /// Writes group.json for <paramref name="desired"/> unless the stored one already says the same (path, machine,
    /// install id, encryption flag and namespace); a rewrite keeps the original <c>CreatedUtc</c>. For an encrypted
    /// group pass the password and the repository's salt/iterations. Returns true when something was uploaded.
    /// </summary>
    public async Task<bool> EnsureAsync(
        IStorage storage,
        GroupIndex desired,
        string? password,
        byte[]? salt,
        int iterations,
        CancellationToken cancellationToken = default,
        RepositoryKeys? keys = null)
    {
        var path = SnapshotStoragePaths.GetGroupIndexPath(desired.GroupPath);
        var now = DateTime.UtcNow;

        GroupIndexReadResult existing;
        try
        {
            existing = await storage.ExistsAsync(path, cancellationToken).ConfigureAwait(false)
                ? await ReadAsync(storage, path, password, cancellationToken, keys).ConfigureAwait(false)
                : new GroupIndexReadResult(GroupIndexReadStatus.Missing, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Log($"Could not read {path}; rewriting it: {ex.Message}", LogLevel.Debug);
            existing = new GroupIndexReadResult(GroupIndexReadStatus.Unreadable, null);
        }

        if (existing.Index is { } current && Matches(current, desired))
        {
            return false;
        }

        desired.CreatedUtc = existing.Index?.CreatedUtc is { } created && created != default ? created : now;
        desired.UpdatedUtc = now;

        await UploadAsync(storage, path, desired, password, salt, iterations, cancellationToken, keys).ConfigureAwait(false);
        logger.Log($"Wrote repository index {path}", LogLevel.Debug);
        return true;
    }

    // Machine name and install id are "last writer" information: comparing them would make two machines sharing a
    // repository rewrite the file on every backup.
    private static bool Matches(GroupIndex a, GroupIndex b) =>
        string.Equals(a.GroupPath, b.GroupPath, StringComparison.Ordinal)
        && a.Encrypted == b.Encrypted
        && string.Equals(a.ChunkStorageNamespace ?? "", b.ChunkStorageNamespace ?? "", StringComparison.OrdinalIgnoreCase);

    private async Task UploadAsync(
        IStorage storage,
        string path,
        GroupIndex index,
        string? password,
        byte[]? salt,
        int iterations,
        CancellationToken cancellationToken,
        RepositoryKeys? keys = null)
    {
        var file = new GroupIndexFile { Encrypted = index.Encrypted };
        var tempPlain = Path.Combine(Path.GetTempPath(), $"restore_groupindex_{Guid.NewGuid():N}.json");
        var tempEnc = Path.Combine(Path.GetTempPath(), $"restore_groupindex_{Guid.NewGuid():N}.enc");
        var tempUpload = Path.Combine(Path.GetTempPath(), $"restore_groupindex_{Guid.NewGuid():N}.up");
        try
        {
            if (index.Encrypted && keys != null)
            {
                var cipher = keys.SealWithManifestKey(JsonSerializer.SerializeToUtf8Bytes(index), KeyedAad(keys.KeyFileId), out var nonce, out var tag);
                file.KeyFileId = keys.KeyFileId;
                file.Nonce = Convert.ToBase64String(nonce);
                file.Tag = Convert.ToBase64String(tag);
                file.Payload = Convert.ToBase64String(cipher);
            }
            else if (index.Encrypted)
            {
                if (string.IsNullOrEmpty(password) || salt == null)
                {
                    throw new InvalidOperationException("An encrypted repository index needs the password and salt.");
                }

                await File.WriteAllTextAsync(tempPlain, JsonSerializer.Serialize(index, JsonOptions), cancellationToken).ConfigureAwait(false);
                var metadata = await _encryption.EncryptFileAsync(tempPlain, tempEnc, password, salt, iterations).ConfigureAwait(false);
                file.Salt = Convert.ToBase64String(metadata.Salt);
                file.KeyDerivationIterations = metadata.KeyDerivationIterations;
                file.IV = Convert.ToBase64String(metadata.IV);
                file.EncryptedDek = Convert.ToBase64String(metadata.EncryptedDEK);
                file.Algorithm = metadata.Algorithm;
                file.MetadataVersion = metadata.Version;
                file.Payload = Convert.ToBase64String(await File.ReadAllBytesAsync(tempEnc, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                file.Index = index;
            }

            await File.WriteAllTextAsync(tempUpload, JsonSerializer.Serialize(file, JsonOptions), cancellationToken).ConfigureAwait(false);
            await storage.UploadAsync(tempUpload, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tempPlain);
            TryDelete(tempEnc);
            TryDelete(tempUpload);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

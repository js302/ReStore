using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

/// <summary>
/// The clear header of an encrypted v3 manifest. It names the key file and carries the key check, so a reader can tell
/// "wrong password / other repository" from "tampered" before decrypting anything. It holds no path, size, time or hash.
/// </summary>
public sealed class EncryptedManifestHeader
{
    public int Format { get; set; } = 1;
    public int ManifestVersion { get; set; } = SnapshotManifest.VersionV3;
    public string SnapshotId { get; set; } = string.Empty;
    /// <summary>Storage key of the group folder (<c>snapshots/&lt;groupKey&gt;/</c>) this manifest belongs to; readers compare it with where they found the file.</summary>
    public string GroupKey { get; set; } = string.Empty;
    public string KeyFileId { get; set; } = string.Empty;
    public string KeyCheck { get; set; } = string.Empty;
    public string Cipher { get; set; } = "AES-256-GCM";
    public string Body { get; set; } = "gzip-json";
}

/// <summary>
/// Container of an encrypted v3 manifest: magic | header length (u32 BE) | header JSON | nonce (12) | tag (16) | ciphertext.
/// The ciphertext is AES-256-GCM(manifest-key, gzip(json)); everything before the nonce (magic, length and header, so the
/// snapshot id and key reference) is the associated data, so no part of the file can change unnoticed.
/// </summary>
public static class EncryptedManifestEnvelope
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RESTORE-EM1\n");
    private const int MaxHeaderBytes = 4096;
    private static readonly JsonSerializerOptions HeaderOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.Length > Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    public static EncryptedManifestHeader ReadHeader(byte[] data) => ParseHeader(data, out _);

    private static EncryptedManifestHeader ParseHeader(byte[] data, out int headerEnd)
    {
        if (!IsEncrypted(data) || data.Length < Magic.Length + 4)
        {
            throw new InvalidDataException("Not an encrypted manifest.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(Magic.Length, 4));
        if (length == 0 || length > MaxHeaderBytes || Magic.Length + 4 + (long)length + 28 > data.Length)
        {
            throw new InvalidDataException("The encrypted manifest header is malformed.");
        }

        headerEnd = Magic.Length + 4 + (int)length;
        try
        {
            return JsonSerializer.Deserialize<EncryptedManifestHeader>(data.AsSpan(Magic.Length + 4, (int)length), HeaderOptions)
                ?? throw new InvalidDataException("The encrypted manifest header is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The encrypted manifest header is malformed.", ex);
        }
    }

    public static byte[] Seal(byte[] manifestJson, string snapshotId, string groupKey, RepositoryKeys keys)
    {
        var header = new EncryptedManifestHeader
        {
            SnapshotId = snapshotId,
            GroupKey = groupKey,
            KeyFileId = keys.KeyFileId,
            KeyCheck = keys.KeyCheck
        };
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, HeaderOptions);

        byte[] gzipped;
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(manifestJson);
            }

            gzipped = output.ToArray();
        }

        var prefix = new byte[Magic.Length + 4 + headerBytes.Length];
        Magic.CopyTo(prefix, 0);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(Magic.Length, 4), (uint)headerBytes.Length);
        headerBytes.CopyTo(prefix, Magic.Length + 4);

        var cipher = keys.SealWithManifestKey(gzipped, prefix, out var nonce, out var tag);
        var result = new byte[prefix.Length + 12 + 16 + cipher.Length];
        prefix.CopyTo(result, 0);
        nonce.CopyTo(result, prefix.Length);
        tag.CopyTo(result, prefix.Length + 12);
        cipher.CopyTo(result, prefix.Length + 28);
        return result;
    }

    /// <summary>
    /// Binds a manifest to the place it was found: the file name must be <c>&lt;snapshotId&gt;.manifest.json</c> and the folder
    /// <c>snapshots/&lt;groupKey&gt;/</c> of the header, so a manifest copied over another one (same or other group) is refused.
    /// </summary>
    public static void AssertLocation(EncryptedManifestHeader header, string remotePath)
    {
        var parts = remotePath.Replace('\\', '/').Split('/');
        var name = parts[^1];
        var folder = parts.Length >= 2 ? parts[^2] : string.Empty;
        if (!string.Equals(name, header.SnapshotId + ".manifest.json", StringComparison.Ordinal)
            || !string.Equals(folder, header.GroupKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The encrypted manifest at '{remotePath}' belongs to snapshot '{header.SnapshotId}' of another location: it was moved or replaced.");
        }
    }

    /// <summary>Verifies the key reference, authenticates header and body, and returns the manifest JSON.</summary>
    public static byte[] Open(byte[] data, RepositoryKeys keys, out EncryptedManifestHeader header)
    {
        header = ParseHeader(data, out var headerEnd);
        if (header.Format != 1)
        {
            throw new NotSupportedException($"Encrypted manifest format {header.Format} was written by a newer ReStore. Update ReStore to read it.");
        }

        if (!string.Equals(header.KeyFileId, keys.KeyFileId, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(header.KeyCheck ?? string.Empty), Encoding.ASCII.GetBytes(keys.KeyCheck)))
        {
            throw new InvalidOperationException("The snapshot manifest was encrypted with a different repository key (wrong key file or password).");
        }

        var nonce = data.AsSpan(headerEnd, 12).ToArray();
        var tag = data.AsSpan(headerEnd + 12, 16).ToArray();
        var cipher = data.AsSpan(headerEnd + 28).ToArray();
        byte[] gzipped;
        try
        {
            gzipped = keys.OpenWithManifestKey(cipher, nonce, tag, data.AsSpan(0, headerEnd).ToArray());
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("The snapshot manifest failed authentication: it was modified or is corrupted.", ex);
        }

        using var input = new MemoryStream(gzipped);
        using var gunzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gunzip.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// Reads and writes the repository key file in storage. The file is kept twice, always written together:
/// <c>keys/repository.keys.json</c> and a mirror <c>keys/repository.keys.1.json</c>; a read falls back to the mirror when
/// the primary is missing or unreadable (and repairs the primary), so one lost or damaged object does not lose the repository.
/// </summary>
public static class RepositoryKeyStore
{
    public const string MirrorPath = "keys/repository.keys.1.json";

    private sealed record Slot(bool Exists, RepositoryKeyFile? File, string? Problem);

    private static async Task<Slot> TryReadOneAsync(IStorage storage, string path, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"restore_keys_{Guid.NewGuid():N}.json");
        try
        {
            try
            {
                if (!await storage.ExistsAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    return new Slot(false, null, null);
                }

                await storage.DownloadAsync(path, temp, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return new Slot(false, null, null);
            }

            try
            {
                return new Slot(true, RepositoryKeyFiles.Parse(await File.ReadAllBytesAsync(temp, cancellationToken).ConfigureAwait(false)), null);
            }
            catch (InvalidOperationException ex)
            {
                return new Slot(true, null, ex.Message);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static bool Same(RepositoryKeyFile a, RepositoryKeyFile b) =>
        RepositoryKeyFiles.Serialize(a).AsSpan().SequenceEqual(RepositoryKeyFiles.Serialize(b));

    /// <summary>Returns the key file (primary, else mirror), or null when the storage has none (a v2 repository).</summary>
    /// <param name="repair">Best effort: rewrite a missing/damaged/stale primary or mirror from the good copy.</param>
    /// <exception cref="InvalidOperationException">A key file exists but neither copy is readable.</exception>
    public static async Task<RepositoryKeyFile?> ReadAsync(IStorage storage, CancellationToken cancellationToken = default, bool repair = true)
    {
        var primary = await TryReadOneAsync(storage, RepositoryKeyFiles.StoragePath, cancellationToken).ConfigureAwait(false);
        var mirror = await TryReadOneAsync(storage, MirrorPath, cancellationToken).ConfigureAwait(false);

        var good = primary.File ?? mirror.File;
        if (good == null)
        {
            if (primary.Exists || mirror.Exists)
            {
                throw new InvalidOperationException(
                    $"The repository key file is damaged and its mirror is not usable ({primary.Problem ?? mirror.Problem}). Restore it with 'import-key' from an offline copy.");
            }

            return null;
        }

        if (repair)
        {
            try
            {
                if (primary.File == null)
                {
                    await UploadAsync(storage, RepositoryKeyFiles.StoragePath, good, cancellationToken).ConfigureAwait(false);
                }
                else if (mirror.File == null || !Same(primary.File, mirror.File))
                {
                    await UploadAsync(storage, MirrorPath, good, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Read-only credentials or a flaky provider: the good copy is still returned.
            }
        }

        return good;
    }

    private static async Task UploadAsync(IStorage storage, string path, RepositoryKeyFile file, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"restore_keys_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllBytesAsync(temp, RepositoryKeyFiles.Serialize(file), cancellationToken).ConfigureAwait(false);
            await storage.UploadAsync(temp, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Writes the primary and the mirror (always together).</summary>
    public static async Task WriteAsync(IStorage storage, RepositoryKeyFile file, CancellationToken cancellationToken = default)
    {
        await UploadAsync(storage, RepositoryKeyFiles.StoragePath, file, cancellationToken).ConfigureAwait(false);
        await UploadAsync(storage, MirrorPath, file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the key file, reads it back and checks that <paramref name="password"/> still opens the stored copy of this key file.</summary>
    /// <exception cref="InvalidOperationException">Another computer replaced the key file in the meantime.</exception>
    public static async Task WriteVerifiedAsync(IStorage storage, RepositoryKeyFile file, string password, CancellationToken cancellationToken = default)
    {
        await WriteAsync(storage, file, cancellationToken).ConfigureAwait(false);
        var back = await ReadAsync(storage, cancellationToken, repair: false).ConfigureAwait(false);
        if (back == null || back.KeyFileId != file.KeyFileId || !Same(back, file))
        {
            throw new InvalidOperationException(
                "The repository key file was changed by another computer while it was being written. Nothing was lost; check the state and retry.");
        }

        using var _ = RepositoryKeyFiles.Unlock(back, password);
    }

    /// <summary>
    /// Creates the key file only if none exists and returns the one that is stored afterwards: ours, or another computer's when
    /// it won a concurrent creation (atomic where the provider can, otherwise write-then-read-back). The caller must compare
    /// <see cref="RepositoryKeyFile.KeyFileId"/> with its own and abort before uploading data when they differ.
    /// </summary>
    public static async Task<RepositoryKeyFile> CreateIfAbsentAsync(IStorage storage, RepositoryKeyFile file, CancellationToken cancellationToken = default)
    {
        var existing = await ReadAsync(storage, cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            return existing;
        }

        if (storage is ICreateIfAbsentStorage atomic)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"restore_keys_{Guid.NewGuid():N}.json");
            try
            {
                await File.WriteAllBytesAsync(temp, RepositoryKeyFiles.Serialize(file), cancellationToken).ConfigureAwait(false);
                await atomic.TryCreateAsync(temp, RepositoryKeyFiles.StoragePath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(temp);
            }
        }
        else
        {
            await UploadAsync(storage, RepositoryKeyFiles.StoragePath, file, cancellationToken).ConfigureAwait(false);
        }

        var stored = await TryReadOneAsync(storage, RepositoryKeyFiles.StoragePath, cancellationToken).ConfigureAwait(false);
        var winner = stored.File ?? throw new InvalidOperationException("The repository key file could not be read back after it was created.");
        if (winner.KeyFileId == file.KeyFileId)
        {
            await UploadAsync(storage, MirrorPath, winner, cancellationToken).ConfigureAwait(false);
        }

        return winner;
    }

    /// <summary>Returns the key file bytes for an offline copy (wrapped slots only; it holds no plaintext secret).</summary>
    public static async Task<byte[]> ExportAsync(IStorage storage, CancellationToken cancellationToken = default)
    {
        var file = await ReadAsync(storage, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This storage has no repository key file (it is not an encrypted v3 repository).");
        return RepositoryKeyFiles.Serialize(file);
    }

    /// <summary>Puts an exported key file back (primary and mirror). Refuses to replace a different repository's key file.</summary>
    public static async Task ImportAsync(IStorage storage, byte[] data, CancellationToken cancellationToken = default)
    {
        var file = RepositoryKeyFiles.Parse(data);
        var existing = await ReadAsync(storage, cancellationToken, repair: false).ConfigureAwait(false);
        if (existing != null && existing.KeyFileId != file.KeyFileId)
        {
            throw new InvalidOperationException("The storage already holds a different repository key file; it was not replaced.");
        }

        await WriteAsync(storage, file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Password change: unlocks with the current password, re-wraps the data key under the new one and uploads the key file
    /// (verified by read-back). Chunks and manifests are not touched. Returns false when the repository has no key file.
    /// </summary>
    public static async Task<bool> ChangePasswordAsync(
        IStorage storage,
        string currentPassword,
        string newPassword,
        int iterations,
        CancellationToken cancellationToken = default)
    {
        var file = await ReadAsync(storage, cancellationToken).ConfigureAwait(false);
        if (file == null)
        {
            return false;
        }

        var updated = RepositoryKeyFiles.ChangePassword(file, currentPassword, newPassword, iterations);
        await WriteVerifiedAsync(storage, updated, newPassword, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Adds a recovery-key slot (shown once by the caller) and uploads the key file.</summary>
    public static async Task<string> AddRecoveryKeyAsync(IStorage storage, string password, CancellationToken cancellationToken = default)
    {
        var file = await ReadAsync(storage, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This repository has no key file (it is not an encrypted v3 repository).");
        var (updated, recoveryKey) = RepositoryKeyFiles.AddRecoveryKey(file, password);
        await WriteVerifiedAsync(storage, updated, password, cancellationToken).ConfigureAwait(false);
        return recoveryKey;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Reads manifests from one storage, unlocking the repository key file (once, asking the password provider) when a
/// manifest is an encrypted v3 one. v2 and unencrypted v3 manifests never touch the key file or the password provider.
/// </summary>
public sealed class RepositoryManifestReader(
    ILogger logger,
    IStorage storage,
    IPasswordProvider? passwordProvider,
    PasswordPurpose purpose = PasswordPurpose.Decrypt,
    int maxAttempts = 3,
    bool zeroKeysOnDispose = true,
    bool repairKeyFile = true) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RepositoryKeys? _keys;
    private InvalidOperationException? _terminal;

    /// <summary>The keys once unlocked (null before the first encrypted v3 manifest was read).</summary>
    public RepositoryKeys? Keys => _keys;

    /// <param name="remotePath">Required: where the file came from in storage; an encrypted manifest must belong to that place.</param>
    public async Task<SnapshotManifest> ReadFileAsync(string path, string remotePath, CancellationToken cancellationToken = default)
    {
        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(data, remotePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SnapshotManifest> ReadAsync(byte[] data, string remotePath, CancellationToken cancellationToken = default)
    {
        if (!EncryptedManifestEnvelope.IsEncrypted(data))
        {
            return SnapshotManifestSerializer.Deserialize(data);
        }

        var header = EncryptedManifestEnvelope.ReadHeader(data);
        var keys = await UnlockAsync(header.KeyFileId, cancellationToken).ConfigureAwait(false);
        return SnapshotManifestSerializer.Deserialize(data, keys, remotePath);
    }

    /// <summary>Unlocks the repository key file with the provider's password (retrying a wrong one a few times).</summary>
    public async Task<RepositoryKeys> UnlockAsync(string? expectedKeyFileId = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys != null && (expectedKeyFileId == null || _keys.KeyFileId == expectedKeyFileId))
            {
                return _keys;
            }

            // A refused or cancelled password is final for this reader: a listing of many manifests must not prompt for each one.
            if (_terminal != null)
            {
                throw _terminal;
            }

            var file = await RepositoryKeyStore.ReadAsync(storage, cancellationToken, repairKeyFile).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"The repository key file '{RepositoryKeyFiles.StoragePath}' is missing, so encrypted snapshots cannot be read.");
            if (expectedKeyFileId != null && file.KeyFileId != expectedKeyFileId)
            {
                throw new InvalidOperationException("The repository key file does not belong to this snapshot (it was replaced).");
            }

            if (passwordProvider == null)
            {
                throw _terminal = new RepositoryPasswordRequiredException("This repository is encrypted and no password is available.");
            }

            string? previous = null;
            WrongRepositoryPasswordException? last = null;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                var password = await passwordProvider.GetPasswordAsync(purpose).ConfigureAwait(false);
                if (string.IsNullOrEmpty(password))
                {
                    throw _terminal = new RepositoryPasswordRequiredException("A password is required to open this encrypted repository.");
                }

                if (previous != null && string.Equals(previous, password, StringComparison.Ordinal))
                {
                    break; // the provider would hand out the same wrong password again
                }

                previous = password;
                try
                {
                    _keys = RepositoryKeyFiles.Unlock(file, password);
                    return _keys;
                }
                catch (WrongRepositoryPasswordException ex)
                {
                    _keys = null;
                    last = ex;
                    logger.Log("Repository key file: password rejected.", LogLevel.Warning);
                    passwordProvider.ClearPassword();
                }
            }

            throw _terminal = last ?? new WrongRepositoryPasswordException("Incorrect password.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (zeroKeysOnDispose)
        {
            _keys?.Dispose();
        }

        _keys = null;
        _gate.Dispose();
    }
}

/// <summary>Outcome of <see cref="RepositoryPasswordService.ChangePasswordAsync"/>.</summary>
/// <param name="Changed">False when the storage has no repository key file (not an encrypted v3 repository): nothing was changed.</param>
/// <param name="RecoveryKey">The new recovery key (shown to the user once) when one was requested.</param>
public sealed record PasswordChangeResult(bool Changed, string? RecoveryKey = null);

/// <summary>
/// Password change for encrypted v3 repositories (F-13), shared by the CLI and the Settings page: re-wraps the data key in the
/// repository key file and moves this computer's configured password (verification token) along. No chunk or manifest is rewritten.
/// </summary>
public static class RepositoryPasswordService
{
    /// <exception cref="WrongRepositoryPasswordException">The current password (or recovery key) does not unlock the key file.</exception>
    public static async Task<PasswordChangeResult> ChangePasswordAsync(
        IStorage storage,
        IConfigManager config,
        ILogger logger,
        string currentPassword,
        string newPassword,
        bool createRecoveryKey,
        SystemState? state = null,
        RepositoryCoordinator? coordinator = null,
        CancellationToken cancellationToken = default)
    {
        if (!new EncryptionService(logger).ValidatePassword(newPassword))
        {
            throw new ArgumentException("The new password must be at least 8 characters long.", nameof(newPassword));
        }

        var lease = await (coordinator ?? RepositoryCoordinator.Instance)
            .AcquireMutationAsync("Changing the repository password", cancellationToken, state).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);

        var file = await RepositoryKeyStore.ReadAsync(storage, cancellationToken).ConfigureAwait(false);
        if (file == null)
        {
            return new PasswordChangeResult(false);
        }

        var updated = RepositoryKeyFiles.ChangePassword(file, currentPassword, newPassword, config.Encryption.KeyDerivationIterations);
        string? recoveryKey = null;
        if (createRecoveryKey)
        {
            (updated, recoveryKey) = RepositoryKeyFiles.AddRecoveryKey(updated, newPassword);
        }

        await RepositoryKeyStore.WriteVerifiedAsync(storage, updated, newPassword, cancellationToken).ConfigureAwait(false);

        // The configured verification token must follow, or this computer would reject its own new password.
        if (config.Encryption.Enabled && !string.IsNullOrWhiteSpace(config.Encryption.Salt))
        {
            EncryptionConfigurator.Enable(config, logger, newPassword, Convert.FromBase64String(config.Encryption.Salt));
            await config.SaveAsync().ConfigureAwait(false);
        }

        logger.Log("Repository password changed: the key file was re-wrapped; no data was re-uploaded.", LogLevel.Info);
        return new PasswordChangeResult(true, recoveryKey);
    }
}

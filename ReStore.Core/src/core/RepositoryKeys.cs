using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

/// <summary>The password (or recovery key) does not unlock any key slot of the repository key file.</summary>
public sealed class WrongRepositoryPasswordException(string message) : InvalidOperationException(message);

/// <summary>An encrypted v3 repository was opened without a password being available.</summary>
public sealed class RepositoryPasswordRequiredException(string message) : InvalidOperationException(message);

/// <summary>An encrypted v3 manifest was read without the repository keys.</summary>
public sealed class ManifestKeyRequiredException(EncryptedManifestHeader header)
    : InvalidOperationException("The snapshot manifest is encrypted; the repository password is required to read it.")
{
    public EncryptedManifestHeader Header { get; } = header;
}

/// <summary>
/// Chunk id and content-hash scheme of a snapshot: plain SHA-256 (v2 and unencrypted v3) or, for encrypted v3
/// repositories, HMAC-SHA256 keyed from the repository data key so a storage provider cannot confirm a guessed file.
/// </summary>
public sealed class ChunkHashScheme
{
    public const string HmacSha256Name = "hmac-sha256";

    private readonly RepositoryKeys? _keys;

    public static ChunkHashScheme Sha256 { get; } = new(null);

    internal ChunkHashScheme(RepositoryKeys? keys) => _keys = keys;

    public bool IsKeyed => _keys != null;

    /// <summary>Hex digest used as the chunk id (and chunk content hash): 64 hex characters either way.</summary>
    public string ChunkId(ReadOnlySpan<byte> plaintext) => Convert.ToHexStringLower(
        _keys == null ? SHA256.HashData(plaintext) : HMACSHA256.HashData(_keys.ChunkIdKey, plaintext));

    /// <summary>An incremental whole-file content hasher of this scheme (hex of its hash is the file ContentHash).</summary>
    public IncrementalHash NewFileHasher() => _keys == null
        ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        : IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _keys.ContentHashKey);

    public string HashFile(string path)
    {
        using var hasher = NewFileHasher();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }
}

/// <summary>
/// The sub-keys of an encrypted v3 repository, all derived with HKDF-SHA256 from the random 256-bit data key:
/// manifest key (manifest bodies, group.json), chunk-id key (HMAC chunk ids), content-hash key (HMAC file hashes),
/// chunk key (root of the per-chunk key/IV derivation). Dispose zeroes them.
/// </summary>
public sealed class RepositoryKeys : IDisposable
{
    private const string Info = "restore/v3/";

    /// <summary>Prefix of the chunk namespace of encrypted v3 repositories (distinct from v2's <c>enc_</c>).</summary>
    public const string NamespacePrefix = "e3_";

    // Kept only so slots can be added or re-wrapped while the keys are unlocked; zeroed on Dispose.
    internal byte[] DataKey { get; }
    internal byte[] ManifestKey { get; }
    internal byte[] ChunkIdKey { get; }
    internal byte[] ContentHashKey { get; }

    /// <summary>Root key for <see cref="EncryptionService.EncryptChunkDeterministic"/> (the "master key" of v2).</summary>
    public byte[] ChunkKey { get; }

    public string KeyFileId { get; }

    /// <summary>Public check value (HMAC under the manifest key): lets a reader tell a wrong key from corruption without any chunk access.</summary>
    public string KeyCheck { get; }

    /// <summary>Chunk storage namespace of this repository's encrypted v3 chunks; stable across password changes.</summary>
    public string ChunkNamespace { get; }

    public ChunkHashScheme HashScheme { get; }

    private RepositoryKeys(byte[] dataKey, string keyFileId)
    {
        KeyFileId = keyFileId;
        DataKey = (byte[])dataKey.Clone();
        ManifestKey = Sub(dataKey, "manifest-key", 32);
        ChunkIdKey = Sub(dataKey, "chunk-id-key", 32);
        ContentHashKey = Sub(dataKey, "content-hash-key", 32);
        ChunkKey = Sub(dataKey, "chunk-key", 32);
        KeyCheck = Convert.ToHexStringLower(HMACSHA256.HashData(ManifestKey, Encoding.UTF8.GetBytes(Info + "key-check")));
        ChunkNamespace = NamespacePrefix + Convert.ToHexStringLower(Sub(dataKey, "chunk-namespace", 12));
        HashScheme = new ChunkHashScheme(this);
    }

    internal static RepositoryKeys Derive(byte[] dataKey, string keyFileId) => new(dataKey, keyFileId);

    private static byte[] Sub(byte[] dataKey, string label, int length) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, length, salt: null, info: Encoding.UTF8.GetBytes(Info + label));

    internal byte[] SealWithManifestKey(byte[] plaintext, byte[] aad, out byte[] nonce, out byte[] tag)
    {
        nonce = RandomNumberGenerator.GetBytes(12);
        tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        using var aes = new AesGcm(ManifestKey, 16);
        aes.Encrypt(nonce, plaintext, cipher, tag, aad);
        return cipher;
    }

    internal byte[] OpenWithManifestKey(byte[] cipher, byte[] nonce, byte[] tag, byte[] aad)
    {
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(ManifestKey, 16);
        aes.Decrypt(nonce, cipher, tag, plain, aad);
        return plain;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(DataKey);
        CryptographicOperations.ZeroMemory(ManifestKey);
        CryptographicOperations.ZeroMemory(ChunkIdKey);
        CryptographicOperations.ZeroMemory(ContentHashKey);
        CryptographicOperations.ZeroMemory(ChunkKey);
    }
}

/// <summary>One way to unlock the data key: the key wrapped (AES-256-GCM) under a KEK derived from a password or recovery key.</summary>
public sealed class RepositoryKeySlot
{
    public string Id { get; set; } = string.Empty;

    /// <summary><c>password</c> (PBKDF2-SHA256) or <c>recovery</c> (HKDF over a random 256-bit recovery key).</summary>
    public string Kind { get; set; } = RepositoryKeyFiles.PasswordKind;

    public string Salt { get; set; } = string.Empty;
    public int Iterations { get; set; }
    public string Nonce { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string WrappedKey { get; set; } = string.Empty;
}

/// <summary>
/// <c>keys/repository.keys.json</c>: the clear-text key file of an encrypted v3 repository. Holds no secret: only
/// the data key wrapped by each slot's KEK. Replacing a slot (password change) never touches any chunk.
/// </summary>
public sealed class RepositoryKeyFile
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public string KeyFileId { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }

    /// <summary>The <see cref="RepositoryKeys.KeyCheck"/> of the data key, to detect a slot that unwrapped to something else.</summary>
    public string KeyCheck { get; set; } = string.Empty;

    public string Cipher { get; set; } = "AES-256-GCM";
    public string Kdf { get; set; } = "PBKDF2-HMAC-SHA256";
    public List<RepositoryKeySlot> Slots { get; set; } = [];
}

/// <summary>Pure (storage-free) key-file operations.</summary>
public static class RepositoryKeyFiles
{
    public const string StoragePath = "keys/repository.keys.json";
    public const string PasswordKind = "password";
    public const string RecoveryKind = "recovery";
    public const string RecoveryPrefix = "RSRK-";

    /// <summary>Lowest PBKDF2 iteration count a password slot may use: lower is clamped on create and refused on unlock.</summary>
    public const int MinIterations = 100_000;

    private const int MaxSlots = 16;
    private const int MaxIterations = 20_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static byte[] Serialize(RepositoryKeyFile file) => JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);

    public static RepositoryKeyFile Parse(byte[] data)
    {
        RepositoryKeyFile? file;
        try
        {
            file = JsonSerializer.Deserialize<RepositoryKeyFile>(data, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The repository key file is not valid JSON: {ex.Message}", ex);
        }

        if (file == null || string.IsNullOrEmpty(file.KeyFileId) || file.Slots == null || file.Slots.Count == 0)
        {
            throw new InvalidOperationException("The repository key file is malformed.");
        }

        if (file.Format > RepositoryKeyFile.CurrentFormat)
        {
            throw new NotSupportedException($"The repository key file format {file.Format} was written by a newer ReStore. Update ReStore to read it.");
        }

        if (file.Slots.Count > MaxSlots || file.Slots.Any(slot => slot.Iterations is < 0 or > MaxIterations))
        {
            throw new InvalidOperationException("The repository key file is malformed (slot limits exceeded).");
        }

        return file;
    }

    /// <summary>Creates a new repository key file with a fresh random data key and one password slot.</summary>
    public static (RepositoryKeyFile File, RepositoryKeys Keys) Create(string password, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        iterations = Math.Max(iterations, MinIterations);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var keyFileId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var keys = RepositoryKeys.Derive(dataKey, keyFileId);
            var file = new RepositoryKeyFile
            {
                KeyFileId = keyFileId,
                CreatedUtc = DateTime.UtcNow,
                KeyCheck = keys.KeyCheck,
                Slots = [WrapPassword(dataKey, keyFileId, password, iterations)]
            };
            return (file, keys);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Unlocks the file with a password or a recovery key. Throws <see cref="WrongRepositoryPasswordException"/> when no slot opens.</summary>
    public static RepositoryKeys Unlock(RepositoryKeyFile file, string secret) => Unlock(file, secret, out _);

    public static RepositoryKeys Unlock(RepositoryKeyFile file, string secret, out string slotId)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrEmpty(secret))
        {
            throw new WrongRepositoryPasswordException("A password is required.");
        }

        var recovery = TryParseRecoveryKey(secret);
        foreach (var slot in file.Slots)
        {
            byte[]? kek = null;
            byte[]? dataKey = null;
            try
            {
                var salt = Convert.FromBase64String(slot.Salt);
                if (slot.Kind == RecoveryKind)
                {
                    if (recovery == null)
                    {
                        continue;
                    }

                    kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, recovery, 32, salt, Encoding.UTF8.GetBytes("restore/v3/recovery-kek"));
                }
                else if (slot.Kind == PasswordKind)
                {
                    if (slot.Iterations < MinIterations)
                    {
                        throw new InvalidOperationException(
                            $"The repository key file has a password slot with only {slot.Iterations:N0} key-derivation iterations (minimum {MinIterations:N0}); it was not written by ReStore or was tampered with.");
                    }

                    if (recovery != null)
                    {
                        continue;
                    }

                    kek = Rfc2898DeriveBytes.Pbkdf2(secret, salt, slot.Iterations, HashAlgorithmName.SHA256, 32);
                }
                else
                {
                    continue;
                }

                var wrapped = Convert.FromBase64String(slot.WrappedKey);
                dataKey = new byte[wrapped.Length];
                using var aes = new AesGcm(kek, 16);
                aes.Decrypt(Convert.FromBase64String(slot.Nonce), wrapped, Convert.FromBase64String(slot.Tag), dataKey, SlotAad(file.KeyFileId, slot));

                var keys = RepositoryKeys.Derive(dataKey, file.KeyFileId);
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(keys.KeyCheck), Encoding.ASCII.GetBytes(file.KeyCheck ?? string.Empty)))
                {
                    keys.Dispose();
                    throw new InvalidOperationException("The repository key file is inconsistent (key check mismatch).");
                }

                slotId = slot.Id;
                return keys;
            }
            catch (CryptographicException)
            {
                // Not this slot's secret.
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("The repository key file is malformed (bad base64).");
            }
            finally
            {
                if (kek != null)
                {
                    CryptographicOperations.ZeroMemory(kek);
                }

                if (dataKey != null)
                {
                    CryptographicOperations.ZeroMemory(dataKey);
                }
            }
        }

        throw new WrongRepositoryPasswordException("Incorrect password: it does not unlock the repository key file.");
    }

    /// <summary>
    /// Re-wraps the data key under <paramref name="newPassword"/>: the password slot that <paramref name="oldPassword"/> opens
    /// is replaced (other slots, e.g. a recovery key, stay). No chunk or manifest is touched.
    /// </summary>
    public static RepositoryKeyFile ChangePassword(RepositoryKeyFile file, string oldPassword, string newPassword, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        if (TryParseRecoveryKey(oldPassword) != null)
        {
            // A recovery key may reset the password: it replaces every password slot.
            using var recovered = Unlock(file, oldPassword);
            return Rewrap(file, recovered, newPassword, iterations, replaceSlot: null);
        }

        using var keys = Unlock(file, oldPassword, out var slotId);
        return Rewrap(file, keys, newPassword, iterations, slotId);
    }

    private static RepositoryKeyFile Rewrap(RepositoryKeyFile file, RepositoryKeys keys, string newPassword, int iterations, string? replaceSlot)
    {
        var slots = file.Slots
            .Where(slot => replaceSlot == null ? slot.Kind != PasswordKind : slot.Id != replaceSlot)
            .ToList();
        slots.Add(WrapPassword(keys.DataKey, file.KeyFileId, newPassword, iterations));
        return Clone(file, slots);
    }

    /// <summary>Returns a copy of the file with an extra recovery slot and the one-time recovery key (shown once, never stored).</summary>
    public static (RepositoryKeyFile File, string RecoveryKey) AddRecoveryKey(RepositoryKeyFile file, string password)
    {
        using var keys = Unlock(file, password);
        var recovery = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(32);
        var kek = HKDF.DeriveKey(HashAlgorithmName.SHA256, recovery, 32, salt, Encoding.UTF8.GetBytes("restore/v3/recovery-kek"));
        try
        {
            var slots = file.Slots.Where(slot => slot.Kind != RecoveryKind).ToList();
            slots.Add(Wrap(keys.DataKey, file.KeyFileId, RecoveryKind, salt, 0, kek));
            return (Clone(file, slots), FormatRecoveryKey(recovery));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            CryptographicOperations.ZeroMemory(recovery);
        }
    }

    private static RepositoryKeyFile Clone(RepositoryKeyFile file, List<RepositoryKeySlot> slots) => new()
    {
        Format = file.Format,
        KeyFileId = file.KeyFileId,
        CreatedUtc = file.CreatedUtc,
        KeyCheck = file.KeyCheck,
        Cipher = file.Cipher,
        Kdf = file.Kdf,
        Slots = slots
    };

    internal static byte[]? TryParseRecoveryKey(string secret)
    {
        if (string.IsNullOrEmpty(secret) || !secret.StartsWith(RecoveryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = secret[RecoveryPrefix.Length..].Replace("-", string.Empty, StringComparison.Ordinal);
        try
        {
            var bytes = Convert.FromHexString(hex);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string FormatRecoveryKey(byte[] key)
    {
        var hex = Convert.ToHexString(key);
        return RecoveryPrefix + string.Join('-', Enumerable.Range(0, 8).Select(i => hex.Substring(i * 8, 8)));
    }

    private static byte[] SlotAad(string keyFileId, RepositoryKeySlot slot) =>
        Encoding.UTF8.GetBytes($"restore-keyslot-v1|{keyFileId}|{slot.Id}|{slot.Kind}|{slot.Salt}|{slot.Iterations}");

    private static RepositoryKeySlot WrapPassword(byte[] dataKey, string keyFileId, string password, int iterations)
    {
        iterations = Math.Max(iterations, MinIterations);
        var salt = RandomNumberGenerator.GetBytes(32);
        var kek = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            return Wrap(dataKey, keyFileId, PasswordKind, salt, iterations, kek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static RepositoryKeySlot Wrap(byte[] dataKey, string keyFileId, string kind, byte[] salt, int iterations, byte[] kek)
    {
        var slot = new RepositoryKeySlot
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            Kind = kind,
            Salt = Convert.ToBase64String(salt),
            Iterations = iterations
        };
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var wrapped = new byte[dataKey.Length];
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, dataKey, wrapped, tag, SlotAad(keyFileId, slot));
        slot.Nonce = Convert.ToBase64String(nonce);
        slot.Tag = Convert.ToBase64String(tag);
        slot.WrappedKey = Convert.ToBase64String(wrapped);
        return slot;
    }
}

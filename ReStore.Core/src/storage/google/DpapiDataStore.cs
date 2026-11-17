using System.Text;
using Google.Apis.Json;
using Google.Apis.Util.Store;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.storage.google;

/// <summary>
/// Google token cache whose files are encrypted with <see cref="ISecretProtector"/> (Windows DPAPI, current user)
/// instead of the plaintext JSON <see cref="FileDataStore"/> writes. Tokens left by an older version in a legacy
/// <see cref="IDataStore"/> are migrated (re-encrypted, then deleted from the legacy store) the first time they are read.
/// </summary>
public sealed class DpapiDataStore(string folder, ISecretProtector protector, IDataStore? legacyStore = null, ILogger? logger = null, Func<bool>? isUnattended = null) : IDataStore
{
    private const string FileExtension = ".dpapi";
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    /// <summary>The folder token files live in: the configured folder when rooted, otherwise under the ReStore data root.</summary>
    public static string ResolveFolder(string tokenFolder) =>
        Path.IsPathRooted(tokenFolder) ? tokenFolder : Path.Combine(AppPaths.Root, "tokens", tokenFolder);

    private string FilePathFor<T>(string key)
    {
        var name = Uri.EscapeDataString($"{typeof(T).FullName}-{key}");
        return Path.Combine(folder, name + FileExtension);
    }

    public async Task StoreAsync<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        var blob = protector.Protect(Encoding.UTF8.GetBytes(json));
        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(folder);
            var path = FilePathFor<T>(key);
            var temp = ReStore.Core.src.utils.SharedFile.UniqueTempPath(path);
            try
            {
                await File.WriteAllBytesAsync(temp, blob).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                ReStore.Core.src.utils.SharedFile.TryDelete(temp);
                throw;
            }
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task DeleteAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = FilePathFor<T>(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            FileLock.Release();
        }

        if (legacyStore != null)
        {
            await legacyStore.DeleteAsync<T>(key).ConfigureAwait(false);
        }
    }

    public async Task<T> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        byte[]? blob = null;
        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = FilePathFor<T>(key);
            if (File.Exists(path))
            {
                blob = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            }
        }
        finally
        {
            FileLock.Release();
        }

        if (blob != null)
        {
            try
            {
                var json = Encoding.UTF8.GetString(protector.Unprotect(blob));
                return NewtonsoftJsonSerializer.Instance.Deserialize<T>(json);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                // Written by another Windows user/machine.
                logger?.Log($"Google Drive token cache could not be decrypted ({ex.GetType().Name}); it was probably encrypted by a different Windows user or machine.", LogLevel.Warning);
                if (isUnattended?.Invoke() == true)
                {
                    throw new StorageAuthRequiredException(
                        "The Google Drive token cache was encrypted by a different Windows user or machine. Sign in again interactively (run ReStore from the desktop), then retry the unattended run.", ex);
                }

                // Interactive: behave as "no token" so the consent flow runs again.
                return default!;
            }
        }

        if (legacyStore != null)
        {
            var legacy = await legacyStore.GetAsync<T>(key).ConfigureAwait(false);
            if (legacy != null && !EqualityComparer<T>.Default.Equals(legacy, default!))
            {
                await StoreAsync(key, legacy).ConfigureAwait(false);
                await legacyStore.DeleteAsync<T>(key).ConfigureAwait(false);
                return legacy;
            }
        }

        return default!;
    }

    public async Task ClearAsync()
    {
        await FileLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*" + FileExtension))
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            FileLock.Release();
        }

        if (legacyStore != null)
        {
            await legacyStore.ClearAsync().ConfigureAwait(false);
        }
    }
}

using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

/// <summary>
/// Asks the password provider for a password BEFORE a repository lease is taken. A GUI provider shows a modal dialog
/// the first time; doing that while holding a lease would block every other operation (and the CLI) for as long as the
/// dialog is open. Providers cache the password, so the call made later under the lease returns immediately. Best
/// effort: a failure here is ignored, because the call under the lease raises the same error as before.
/// </summary>
internal static class PasswordPrefetch
{
    public static async Task WarmAsync(IPasswordProvider? provider, PasswordPurpose purpose)
    {
        if (provider == null)
        {
            return;
        }

        try
        {
            await provider.GetPasswordAsync(purpose).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Raised again by the real request under the lease.
        }
    }

    /// <summary>
    /// Warms the decrypt password when the snapshot (a manifest path or its HEAD) is encrypted. The manifest header is read
    /// without a lease; any failure just means no prefetch.
    /// </summary>
    public static async Task WarmForSnapshotAsync(
        ILogger logger, IStorage storage, IPasswordProvider? provider, string backupPath, string? tempRoot, CancellationToken cancellationToken)
    {
        if (provider == null)
        {
            return;
        }

        var tempDirectory = Path.Combine(tempRoot ?? Path.GetTempPath(), $"restore_prefetch_{Guid.NewGuid():N}");
        var encrypted = false;
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var manifestPath = backupPath;
            if (backupPath.EndsWith("/HEAD", StringComparison.OrdinalIgnoreCase))
            {
                var headFile = Path.Combine(tempDirectory, "snapshot.head");
                await storage.DownloadAsync(backupPath, headFile).ConfigureAwait(false);
                manifestPath = (await File.ReadAllLinesAsync(headFile, cancellationToken).ConfigureAwait(false))
                    .Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "";
            }

            if (!manifestPath.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var manifestFile = Path.Combine(tempDirectory, "snapshot.manifest.json");
            await storage.DownloadAsync(manifestPath, manifestFile).ConfigureAwait(false);
            var data = await File.ReadAllBytesAsync(manifestFile, cancellationToken).ConfigureAwait(false);
            encrypted = EncryptedManifestEnvelope.IsEncrypted(data) || SnapshotManifestSerializer.Deserialize(data).EncryptionEnabled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Log($"Could not inspect the snapshot before taking the lease ({ex.Message}); the password is requested later if needed.", LogLevel.Debug);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch (Exception) { /* best effort */ }
        }

        if (encrypted)
        {
            await WarmAsync(provider, PasswordPurpose.Decrypt).ConfigureAwait(false);
        }
    }
}

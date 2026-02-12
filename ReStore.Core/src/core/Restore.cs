using ReStore.Core.src.utils;
using ReStore.Core.src.storage;

namespace ReStore.Core.src.core;

public class Restore(ILogger logger, IStorage storage, IPasswordProvider? passwordProvider = null)
{
    private readonly ILogger _logger = logger;
    private readonly IStorage _storage = storage;
    private readonly CompressionUtil _compressionUtil = new();
    private readonly IPasswordProvider? _passwordProvider = passwordProvider;

    public async Task RestoreFromBackupAsync(string backupPath, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be null or empty", nameof(backupPath));
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Target directory cannot be null or empty", nameof(targetDirectory));
        }

        string tempDownloadPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(backupPath));
        try
        {
            _logger.Log($"Starting restore from {backupPath} to {targetDirectory}", LogLevel.Info);

            _logger.Log($"Downloading backup file: {backupPath}", LogLevel.Debug);
            await _storage.DownloadAsync(backupPath, tempDownloadPath);
            _logger.Log($"Downloaded to temporary path: {tempDownloadPath}", LogLevel.Debug);

            Directory.CreateDirectory(targetDirectory);
            _logger.Log($"Ensured target directory exists: {targetDirectory}", LogLevel.Debug);

            if (backupPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log("Backup is encrypted, downloading metadata...", LogLevel.Info);
                var metadataPath = backupPath + ".meta";
                var tempMetadataPath = tempDownloadPath + ".meta";

                try
                {
                    await _storage.DownloadAsync(metadataPath, tempMetadataPath);
                    _logger.Log($"Downloaded metadata to: {tempMetadataPath}", LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to download encryption metadata: {ex.Message}", ex);
                }

                _logger.Log("Decrypting backup...", LogLevel.Info);
                if (_passwordProvider == null)
                {
                    throw new InvalidOperationException("Encrypted backup detected but no password provider available");
                }

                var password = await _passwordProvider.GetPasswordAsync();
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("Password required to decrypt backup");
                }

                _logger.Log($"Decrypting and decompressing {tempDownloadPath} to {targetDirectory}", LogLevel.Info);

                try
                {
                    await _compressionUtil.DecryptAndDecompressAsync(tempDownloadPath, password, targetDirectory, _logger);
                }
                catch (Exception ex)
                {
                    _passwordProvider.ClearPassword();
                    _logger.Log("Decryption failed. Password cleared for retry.", LogLevel.Debug);
                    throw new InvalidOperationException($"Failed to decrypt backup: {ex.Message}", ex);
                }
                finally
                {
                    if (File.Exists(tempMetadataPath))
                    {
                        File.Delete(tempMetadataPath);
                    }
                }
            }
            else
            {
                _logger.Log($"Decompressing {tempDownloadPath} to {targetDirectory}", LogLevel.Info);
                await _compressionUtil.DecompressAsync(tempDownloadPath, targetDirectory);
            }

            _logger.Log("Restore completed successfully.", LogLevel.Info);
        }
        catch (FileNotFoundException fnfEx)
        {
            _logger.Log($"Restore failed: Backup file not found on remote storage or locally after download. {fnfEx.Message}", LogLevel.Error);
            throw;
        }
        catch (IOException ioEx)
        {
            _logger.Log($"Restore failed: IO error during download or decompression. {ioEx.Message}", LogLevel.Error);
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"Restore failed: {ex.Message}", LogLevel.Error);
            throw;
        }
        finally
        {
            if (File.Exists(tempDownloadPath))
            {
                try
                {
                    File.Delete(tempDownloadPath);
                    _logger.Log($"Cleaned up temporary file: {tempDownloadPath}", LogLevel.Debug);
                }
                catch (Exception cleanupEx)
                {
                    _logger.Log($"Failed to clean up temporary file {tempDownloadPath}: {cleanupEx.Message}", LogLevel.Warning);
                }
            }
        }
    }
}

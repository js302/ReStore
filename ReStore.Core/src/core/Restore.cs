using ReStore.Core.src.utils;
using ReStore.Core.src.storage;

namespace ReStore.Core.src.core;

public class Restore
{
    private readonly ILogger _logger;
    private readonly SystemState _state;
    private readonly IStorage _storage;
    private readonly CompressionUtil _compressionUtil;
    private readonly IPasswordProvider? _passwordProvider;

    public Restore(ILogger logger, SystemState state, IStorage storage, IPasswordProvider? passwordProvider = null)
    {
        _logger = logger;
        _state = state;
        _storage = storage;
        _compressionUtil = new CompressionUtil();
        _passwordProvider = passwordProvider;
    }

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

            if (backupPath.EndsWith(".diff", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log("Differential backup restore detected, finding base backup...", LogLevel.Info);
                
                var baseBackupPath = _state.GetBaseBackupPath(backupPath);
                if (string.IsNullOrEmpty(baseBackupPath))
                {
                    _logger.Log("No base backup found for differential restore.", LogLevel.Error);
                    return;
                }

                await RestoreFromDifferentialAsync(baseBackupPath, backupPath, targetDirectory);
            }
            else
            {
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
        }
        catch (FileNotFoundException fnfEx)
        {
            _logger.Log($"Restore failed: Backup file not found on remote storage or locally after download. {fnfEx.Message}", LogLevel.Error);
        }
        catch (IOException ioEx)
        {
            _logger.Log($"Restore failed: IO error during download or decompression. {ioEx.Message}", LogLevel.Error);
        }
        catch (Exception ex)
        {
            _logger.Log($"Restore failed: {ex.Message}", LogLevel.Error);
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

    private async Task RestoreFromDifferentialAsync(string baseBackupPath, string diffBackupPath, string targetDirectory)
    {
        var tempBasePath = Path.Combine(Path.GetTempPath(), "base_" + Path.GetFileName(baseBackupPath));
        var tempDiffPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(diffBackupPath));
        var tempBaseExtracted = Path.Combine(Path.GetTempPath(), "base_extracted");

        try
        {
            _logger.Log("Downloading base backup...", LogLevel.Info);
            await _storage.DownloadAsync(baseBackupPath, tempBasePath);
            
            _logger.Log("Downloading differential backup...", LogLevel.Info);
            await _storage.DownloadAsync(diffBackupPath, tempDiffPath);

            _logger.Log("Extracting base backup...", LogLevel.Info);
            Directory.CreateDirectory(tempBaseExtracted);
            
            if (baseBackupPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log("Base backup is encrypted, decrypting...", LogLevel.Info);
                if (_passwordProvider == null)
                {
                    throw new InvalidOperationException("Encrypted backup detected but no password provider available");
                }
                
                var password = await _passwordProvider.GetPasswordAsync();
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("Password required to decrypt backup");
                }
                
                try
                {
                    await _compressionUtil.DecryptAndDecompressAsync(tempBasePath, password, tempBaseExtracted, _logger);
                }
                catch (Exception ex)
                {
                    _passwordProvider.ClearPassword();
                    _logger.Log("Decryption failed. Password cleared for retry.", LogLevel.Debug);
                    throw new InvalidOperationException($"Failed to decrypt base backup: {ex.Message}", ex);
                }
            }
            else
            {
                await _compressionUtil.DecompressAsync(tempBasePath, tempBaseExtracted);
            }

            _logger.Log("Applying differential changes...", LogLevel.Info);
            var diffManager = new DiffManager();
            var diffBytes = await File.ReadAllBytesAsync(tempDiffPath);
            
            // This is a simplified approach - in a real implementation, 
            // the diff would contain metadata about which files to apply diffs to
            var baseFiles = Directory.GetFiles(tempBaseExtracted, "*", SearchOption.AllDirectories);
            
            if (baseFiles.Length > 0)
            {
                var tempRestoredFile = Path.Combine(targetDirectory, Path.GetFileName(baseFiles[0]));
                Directory.CreateDirectory(Path.GetDirectoryName(tempRestoredFile)!);
                await diffManager.ApplyDiffAsync(baseFiles[0], diffBytes, tempRestoredFile);
            }

            _logger.Log("Differential restore completed successfully.", LogLevel.Info);
        }
        finally
        {
            // Cleanup temp files
            var tempFiles = new[] { tempBasePath, tempDiffPath };
            foreach (var tempFile in tempFiles)
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
            
            if (Directory.Exists(tempBaseExtracted))
            {
                try { Directory.Delete(tempBaseExtracted, true); } catch { }
            }
        }
    }
}
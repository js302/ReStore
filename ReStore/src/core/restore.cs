using ReStore.src.utils;
using ReStore.src.storage;
using System.IO.Compression; // Keep for ZipFile usage if needed elsewhere, but not directly for diff

namespace ReStore.src.core;

public class Restore
{
    private readonly ILogger _logger;
    private readonly SystemState _state;
    private readonly IStorage _storage;
    private readonly CompressionUtil _compressionUtil = new();

    public Restore(ILogger logger, SystemState state, IStorage storage)
    {
        _logger = logger;
        _state = state;
        _storage = storage;
    }

    public async Task RestoreFromBackupAsync(string backupPath, string targetDirectory)
    {
        string tempDownloadPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(backupPath));
        try
        {
            _logger.Log($"Starting restore from {backupPath} to {targetDirectory}", LogLevel.Info);

            // Check if it's a differential backup path
            if (backupPath.EndsWith(".diff", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log("Restoring from differential backups (.diff) is not currently supported.", LogLevel.Error);
                // Optional: Throw an exception or return a specific status code/boolean
                // throw new NotSupportedException("Restoring from differential backups (.diff) is not currently supported.");
                return; // Exit the method as we cannot proceed
            }

            // Assume it's a full backup (.zip)
            _logger.Log($"Downloading backup file: {backupPath}", LogLevel.Debug);
            await _storage.DownloadAsync(backupPath, tempDownloadPath);
            _logger.Log($"Downloaded to temporary path: {tempDownloadPath}", LogLevel.Debug);

            // Ensure target directory exists
            Directory.CreateDirectory(targetDirectory);
            _logger.Log($"Ensured target directory exists: {targetDirectory}", LogLevel.Debug);

            _logger.Log($"Decompressing {tempDownloadPath} to {targetDirectory}", LogLevel.Info);
            // Use CompressionUtil which handles overwrite
            await _compressionUtil.DecompressAsync(tempDownloadPath, targetDirectory);

            _logger.Log("Restore completed successfully.", LogLevel.Info);
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
            // Consider logging stack trace for debugging: _logger.Log(ex.ToString(), LogLevel.Debug);
        }
        finally
        {
            // Clean up the downloaded temporary file
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
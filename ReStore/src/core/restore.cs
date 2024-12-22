using ReStore.Utils;
using ReStore.Storage;

namespace ReStore.Core;

public class Restore
{
    private readonly ILogger _logger;
    private readonly SystemState _state;
    private readonly IStorage _storage;
    private readonly CompressionUtil _compressionUtil;

    public Restore(ILogger logger, SystemState state, IStorage storage)
    {
        _logger = logger;
        _state = state;
        _storage = storage;
        _compressionUtil = new CompressionUtil();
    }

    public async Task RestoreFromBackupAsync(string backupPath, string targetDirectory)
    {
        _logger.Log($"Starting restore from {backupPath} to {targetDirectory}");

        // Ensure target directory exists
        Directory.CreateDirectory(targetDirectory);

        // Download backup from storage
        var localBackupFile = Path.Combine(Path.GetTempPath(), Path.GetFileName(backupPath));
        await _storage.DownloadAsync(backupPath, localBackupFile);
        _logger.Log($"Downloaded backup to {localBackupFile}");

        // Extract files
        await _compressionUtil.DecompressAsync(localBackupFile, targetDirectory);
        _logger.Log($"Extracted backup to {targetDirectory}");

        // Restore file permissions if necessary
        // TODO: Implement file permission restoration

        if (backupPath.EndsWith(".diff"))
        {
            var diffManager = new DiffManager();
            var baseBackupPath = _state.GetBaseBackupPath(backupPath);
            
            // Download and apply diff
            var localDiffFile = Path.Combine(Path.GetTempPath(), Path.GetFileName(backupPath));
            await _storage.DownloadAsync(backupPath, localDiffFile);
            
            var diff = await File.ReadAllBytesAsync(localDiffFile);
            if (baseBackupPath == null)
            {
                throw new InvalidOperationException("Base backup path cannot be null.");
            }
            await diffManager.ApplyDiffAsync(baseBackupPath, diff, targetDirectory);
            
            File.Delete(localDiffFile);
        }

        // Clean up temporary files
        File.Delete(localBackupFile);
        _logger.Log("Restore completed successfully");
    }
}
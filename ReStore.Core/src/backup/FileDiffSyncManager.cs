using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ReStore.Core.src.backup;

public class FileDiffSyncManager
{
    private readonly ILogger _logger;
    private readonly SystemState _systemState;
    private readonly BackupConfigurationManager _backupConfigManager;

    // Constructor updated to accept SystemState
    public FileDiffSyncManager(ILogger logger, SystemState systemState, BackupConfigurationManager backupConfigManager)
    {
        _logger = logger;
        _systemState = systemState;
        _backupConfigManager = backupConfigManager;
    }

    // Implement GetFilesToBackup using SystemState
    public List<string> GetFilesToBackup(List<string> allFiles)
    {
        var backupType = _backupConfigManager.Configuration.Type;
        _logger.Log($"Determining files to backup based on type: {backupType}", LogLevel.Debug);

        // Delegate the logic to SystemState
        var filesToBackup = _systemState.GetChangedFiles(allFiles, backupType);

        _logger.Log($"Identified {filesToBackup.Count} files requiring backup.", LogLevel.Info);
        return filesToBackup;
    }

    // Implement UpdateFileMetadataAsync using SystemState
    public async Task UpdateFileMetadataAsync(List<string> backedUpFiles)
    {
        _logger.Log($"Updating metadata for {backedUpFiles.Count} successfully backed up files.", LogLevel.Debug);
        foreach (var filePath in backedUpFiles)
        {
            try
            {
                // Ensure file still exists before updating metadata
                if (File.Exists(filePath))
                {
                    await _systemState.AddOrUpdateFileMetadataAsync(filePath);
                }
                else
                {
                    _logger.Log($"File no longer exists, skipping metadata update: {filePath}", LogLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error updating metadata for file {filePath}: {ex.Message}", LogLevel.Warning);
                // Continue updating metadata for other files
            }
        }
        _logger.Log("Metadata update complete.", LogLevel.Debug);
        // State saving should happen after metadata updates in the Backup class
    }
}
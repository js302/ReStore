using ReStore.src.utils;
using ReStore.src.monitoring;
using ReStore.src.storage;
using ReStore.src.backup;

namespace ReStore.src.core;

public class Backup
{
    private readonly ILogger _logger;
    private readonly SystemState _state;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly IStorage _storage;
    private readonly IConfigManager _config;
    private readonly FileSelectionService _fileSelectionService;
    private readonly FileDiffSyncManager? _diffSyncManager;
    private readonly CompressionUtil _compressionUtil = new();

    public Backup(ILogger logger, SystemState state, SizeAnalyzer sizeAnalyzer, IStorage storage, IConfigManager? config = null)
    {
        _logger = logger;
        _state = state;
        _sizeAnalyzer = sizeAnalyzer;
        _storage = storage;
        _config = config ?? throw new ArgumentNullException(nameof(config), "Config cannot be null");
        _fileSelectionService = new FileSelectionService(logger, _config);

        var backupConfig = new BackupConfigurationManager(logger, _config);
        _diffSyncManager = new FileDiffSyncManager(logger, state, backupConfig);
    }

    public async Task BackupDirectoryAsync(string sourceDirectory)
    {
        try
        {
            sourceDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourceDirectory));
            _logger.Log($"Starting backup of {sourceDirectory}");

            var (size, exceedsThreshold) = await _sizeAnalyzer.AnalyzeDirectoryAsync(sourceDirectory);

            if (exceedsThreshold)
            {
                _logger.Log($"Warning: Directory size ({size} bytes) exceeds threshold");
                // TODO: implement some user notification/confirmation logic here
            }

            // Get all files to consider for backup
            var allFiles = GetFilesInDirectory(sourceDirectory);

            // Determine which files need to be backed up based on backup type
            var backupType = _config.BackupType;
            var filesToBackup = _diffSyncManager != null
                ? _diffSyncManager.GetFilesToBackup(allFiles)
                : allFiles;

            if (filesToBackup.Count == 0)
            {
                _logger.Log("No files need to be backed up", LogLevel.Info);
                return;
            }

            _logger.Log($"Preparing to backup {filesToBackup.Count} files from {sourceDirectory}");

            // Update metadata for files being backed up
            foreach (var file in filesToBackup)
            {
                await _state.AddOrUpdateFileMetadataAsync(file);
            }

            var diffManager = new DiffManager();
            var previousBackupPath = _state.GetPreviousBackupPath(sourceDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");

            if (previousBackupPath != null && backupType != BackupType.Full)
            {
                try
                {
                    var diff = await diffManager.CreateDiffAsync(previousBackupPath, sourceDirectory);
                    var diffPath = Path.Combine(Path.GetTempPath(), $"backup_{timestamp}.diff");
                    await File.WriteAllBytesAsync(diffPath, diff);

                    var remotePath = $"backups/{Path.GetFileName(sourceDirectory)}/{timestamp}.diff";
                    await _storage.UploadAsync(diffPath, remotePath);
                    _state.AddBackup(sourceDirectory, remotePath, true);

                    File.Delete(diffPath);
                    _logger.Log($"Incremental backup completed: {remotePath}", LogLevel.Info);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Failed to create diff backup: {ex.Message}", LogLevel.Error);
                    // Fall back to full backup
                    await CreateFullBackupAsync(sourceDirectory, timestamp);
                }
            }
            else
            {
                await CreateFullBackupAsync(sourceDirectory, timestamp);
            }

            await _state.SaveMetadataAsync();

            _state.LastBackupTime = DateTime.UtcNow;

            // Update metadata after backup
            if (_diffSyncManager != null)
            {
                await _diffSyncManager.UpdateFileMetadataAsync(filesToBackup);
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup directory: {ex.Message}", LogLevel.Error);
        }
    }

    private List<string> GetFilesInDirectory(string directory)
    {
        var files = new List<string>();
        try
        {
            var directoryList = new List<string> { directory };
            files = _fileSelectionService.GetFilesToBackup(directoryList);

            _logger.Log($"Found {files.Count} files to backup in {directory}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"Error collecting files: {ex.Message}", LogLevel.Error);
        }

        return files;
    }

    private async Task CreateFullBackupAsync(string sourceDirectory, string timestamp)
    {
        try
        {
            var tempArchive = Path.Combine(Path.GetTempPath(), $"backup_{timestamp}.zip");
            await _compressionUtil.CompressDirectoryAsync(sourceDirectory, tempArchive);

            var remotePath = $"backups/{Path.GetFileName(sourceDirectory)}/{timestamp}.zip";
            await _storage.UploadAsync(tempArchive, remotePath);
            _state.AddBackup(sourceDirectory, remotePath, false);

            File.Delete(tempArchive);
            _logger.Log($"Full backup completed: {remotePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to create full backup: {ex.Message}", LogLevel.Error);
        }
    }
}

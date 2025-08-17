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
    private readonly CompressionUtil _compressionUtil;

    public Backup(ILogger logger, SystemState state, SizeAnalyzer sizeAnalyzer, IStorage storage, IConfigManager? config = null)
    {
        _logger = logger;
        _state = state;
        _sizeAnalyzer = sizeAnalyzer;
        _storage = storage;
        _config = config ?? throw new ArgumentNullException(nameof(config), "Config cannot be null");
        _fileSelectionService = new FileSelectionService(logger, _config);
        _compressionUtil = new CompressionUtil();

        var backupConfig = new BackupConfigurationManager(logger, _config);
        _diffSyncManager = new FileDiffSyncManager(logger, state, backupConfig);
    }

    public async Task BackupDirectoryAsync(string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new ArgumentException("Source directory cannot be null or empty", nameof(sourceDirectory));
        }

        try
        {
            sourceDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(sourceDirectory));
            
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");
            }

            _logger.Log($"Starting backup of {sourceDirectory}");

            _sizeAnalyzer.SizeThreshold = _config.SizeThresholdMB * 1024 * 1024;
            var (size, exceedsThreshold) = await _sizeAnalyzer.AnalyzeDirectoryAsync(sourceDirectory);

            if (exceedsThreshold)
            {
                _logger.Log($"Warning: Directory size ({size} bytes) exceeds threshold");
            }

            var allFiles = GetFilesInDirectory(sourceDirectory);

            var filesToBackup = _diffSyncManager != null
                ? _diffSyncManager.GetFilesToBackup(allFiles)
                : allFiles;

            if (!filesToBackup.Any())
            {
                _logger.Log("No files need to be backed up based on the current state and backup type.", LogLevel.Info);
                await _state.SaveStateAsync();
                return;
            }

            _logger.Log($"Preparing to backup {filesToBackup.Count} files from {sourceDirectory}");

            if (_diffSyncManager != null)
            {
                await _diffSyncManager.UpdateFileMetadataAsync(filesToBackup);
            }
            else
            {
                foreach (var file in filesToBackup)
                {
                    await _state.AddOrUpdateFileMetadataAsync(file);
                }
            }

            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");

            _logger.Log("Proceeding with full backup creation for selected files.", LogLevel.Info);
            await CreateFullBackupAsync(sourceDirectory, filesToBackup, timestamp);

            _state.LastBackupTime = DateTime.UtcNow;

            await _state.SaveStateAsync();
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup directory: {ex.Message}", LogLevel.Error);
        }
    }

    public async Task BackupFilesAsync(IEnumerable<string> filesToBackup, string baseDirectory)
    {
        if (filesToBackup == null)
        {
            throw new ArgumentNullException(nameof(filesToBackup));
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory cannot be null or empty", nameof(baseDirectory));
        }

        var fileList = filesToBackup.ToList();
        if (!fileList.Any())
        {
            _logger.Log("No files provided for backup.", LogLevel.Info);
            return;
        }

        _logger.Log($"Starting backup of {fileList.Count} specific files from base directory {baseDirectory}", LogLevel.Info);

        try
        {
            foreach (var file in fileList)
            {
                if (File.Exists(file))
                {
                    await _state.AddOrUpdateFileMetadataAsync(file);
                }
                else
                {
                    _logger.Log($"File no longer exists, skipping metadata update: {file}", LogLevel.Warning);
                }
            }

            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var archiveFileName = $"backup_{Path.GetFileName(baseDirectory)}_{timestamp}.zip";
            var tempArchive = Path.Combine(Path.GetTempPath(), archiveFileName);

            _logger.Log($"Creating temporary archive: {tempArchive}", LogLevel.Debug);

            var existingFilesToBackup = fileList.Where(File.Exists).ToList();
            if (!existingFilesToBackup.Any())
            {
                _logger.Log("All specified files were deleted before archiving could start.", LogLevel.Warning);
                return;
            }

            await _compressionUtil.CompressFilesAsync(existingFilesToBackup, baseDirectory, tempArchive);

            var remotePath = $"backups/{Path.GetFileName(baseDirectory)}/{archiveFileName}";

            _logger.Log($"Uploading archive {tempArchive} to {remotePath}", LogLevel.Debug);
            await _storage.UploadAsync(tempArchive, remotePath);

            _state.AddBackup(baseDirectory, remotePath, false);

            File.Delete(tempArchive);
            _logger.Log($"Deleted temporary archive: {tempArchive}", LogLevel.Debug);

            _logger.Log($"Specific file backup completed: {remotePath}", LogLevel.Info);

            await _state.SaveStateAsync();
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup specific files from {baseDirectory}: {ex.Message}", LogLevel.Error);
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

    private async Task CreateFullBackupAsync(string sourceDirectory, List<string> filesToInclude, string timestamp)
    {
        if (!filesToInclude.Any())
        {
            _logger.Log("CreateFullBackupAsync called with no files to include.", LogLevel.Warning);
            return;
        }

        try
        {
            var archiveFileName = $"backup_{Path.GetFileName(sourceDirectory)}_{timestamp}.zip";
            var tempArchive = Path.Combine(Path.GetTempPath(), archiveFileName);

            _logger.Log($"Creating temporary archive for full backup: {tempArchive}", LogLevel.Debug);

            await _compressionUtil.CompressFilesAsync(filesToInclude, sourceDirectory, tempArchive);

            var remotePath = $"backups/{Path.GetFileName(sourceDirectory)}/{archiveFileName}";
            _logger.Log($"Uploading full backup archive {tempArchive} to {remotePath}", LogLevel.Debug);
            await _storage.UploadAsync(tempArchive, remotePath);

            _state.AddBackup(sourceDirectory, remotePath, false);

            File.Delete(tempArchive);
            _logger.Log($"Deleted temporary archive: {tempArchive}", LogLevel.Debug);
            _logger.Log($"Full backup completed: {remotePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to create full backup: {ex.Message}", LogLevel.Error);
        }
    }
}

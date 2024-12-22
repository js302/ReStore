using ReStore.Utils;
using ReStore.Monitoring;
using ReStore.Storage;
namespace ReStore.Core;

public class Backup
{
    private readonly ILogger _logger;
    private readonly SystemState _state;
    private readonly SizeAnalyzer _sizeAnalyzer;
    private readonly IStorage _storage;
    private readonly CompressionUtil _compressionUtil;

    public Backup(ILogger logger, SystemState state, SizeAnalyzer sizeAnalyzer, IStorage storage, CompressionUtil compressionUtil)
    {
        _logger = logger;
        _state = state;
        _sizeAnalyzer = sizeAnalyzer;
        _storage = storage;
        _compressionUtil = compressionUtil;
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
                // TODO: Implement user notification/confirmation logic here
            }

            var diffManager = new DiffManager();
            var previousBackupPath = _state.GetPreviousBackupPath(sourceDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");

            if (previousBackupPath != null)
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
                }
                catch (Exception ex)
                {
                    _logger.Log($"Failed to create diff backup: {ex.Message}");
                }
            }
            else
            {
                await CreateFullBackupAsync(sourceDirectory, timestamp);
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to backup directory: {ex.Message}");
        }
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
            _logger.Log($"Backup completed successfully: {remotePath}");
        }
        catch (Exception ex)
        {
            _logger.Log($"Failed to create full backup: {ex.Message}");
        }
    }
}

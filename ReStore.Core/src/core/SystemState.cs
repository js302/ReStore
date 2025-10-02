using System.Text.Json;
using System.Security.Cryptography;
using ReStore.Core.src.utils;
using System.Text.Json.Serialization;

namespace ReStore.Core.src.core;

public class FileMetadata
{
    public string FilePath { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string Hash { get; set; } = "";
}

internal class PersistentStateData
{
    public DateTime LastBackupTime { get; set; }
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = [];
    public Dictionary<string, FileMetadata> FileMetadata { get; set; } = [];
}

public class SystemState
{
    public DateTime LastBackupTime { get; set; }
    public List<string> TrackedDirectories { get; set; } = [];
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = [];
    public Dictionary<string, FileMetadata> FileMetadata { get; set; } = [];

    private string _stateFilePath = Path.Combine(Directory.GetCurrentDirectory(), "state", "system_state.json");
    [JsonIgnore]
    private readonly ILogger? _logger;

    public SystemState(ILogger? logger = null)
    {
        _logger = logger;
    }

    public void SetStateFilePath(string path)
    {
        _stateFilePath = path;
        _logger?.Log($"System state file path set to: {_stateFilePath}", LogLevel.Debug);
    }

    public bool HasFileChanged(string path, string currentHash)
    {
        return !FileMetadata.TryGetValue(path, out var storedMetadata) || storedMetadata.Hash != currentHash;
    }

    public string? GetPreviousBackupPath(string directory)
    {
        if (!BackupHistory.TryGetValue(directory, out var backups) || backups.Count == 0)
            return null;

        return backups.OrderByDescending(b => b.Timestamp).First().Path;
    }

    public string? GetBaseBackupPath(string diffPath)
    {
        foreach (var history in BackupHistory.Values)
        {
            var baseBackup = history
                .OrderByDescending(b => b.Timestamp)
                .FirstOrDefault(b => !b.Path.EndsWith(".diff") && b.Timestamp < GetTimestampFromPath(diffPath));

            if (baseBackup != null)
                return baseBackup.Path;
        }
        return null;
    }

    private DateTime GetTimestampFromPath(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var timestampStr = fileName.Split('_').Last();
        return DateTime.ParseExact(timestampStr, "yyyyMMddHHmmss", null);
    }

    public void AddBackup(string directory, string path, bool isDiff)
    {
        if (!BackupHistory.ContainsKey(directory))
            BackupHistory[directory] = new List<BackupInfo>();

        BackupHistory[directory].Add(new BackupInfo
        {
            Path = path,
            Timestamp = DateTime.Now,
            IsDiff = isDiff
        });
    }

    public async Task AddOrUpdateFileMetadataAsync(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                if (FileMetadata.ContainsKey(filePath))
                {
                    FileMetadata.Remove(filePath);
                    _logger?.Log($"Removed metadata for deleted file: {filePath}", LogLevel.Debug);
                }
                return;
            }

            var metadata = new FileMetadata
            {
                FilePath = filePath,
                Size = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
                Hash = await CalculateFileHashAsync(filePath)
            };

            FileMetadata[filePath] = metadata;
        }
        catch (IOException ioEx)
        {
            _logger?.Log($"IO Error updating metadata for {filePath}: {ioEx.Message}. Skipping file.", LogLevel.Warning);
        }
        catch (UnauthorizedAccessException uaEx)
        {
            _logger?.Log($"Access Denied updating metadata for {filePath}: {uaEx.Message}. Skipping file.", LogLevel.Warning);
        }
        catch (Exception ex)
        {
            _logger?.Log($"Error updating metadata for {filePath}: {ex.Message}", LogLevel.Warning);
        }
    }

    public async Task SaveStateAsync()
    {
        try
        {
            var stateData = new PersistentStateData
            {
                LastBackupTime = this.LastBackupTime,
                BackupHistory = this.BackupHistory,
                FileMetadata = this.FileMetadata
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(stateData, options);
            await File.WriteAllTextAsync(_stateFilePath, json);
            _logger?.Log($"System state saved to {_stateFilePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger?.Log($"Error saving system state to {_stateFilePath}: {ex.Message}", LogLevel.Error);
        }
    }

    public async Task LoadStateAsync()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                _logger?.Log($"Loading system state from {_stateFilePath}", LogLevel.Debug);
                string json = await File.ReadAllTextAsync(_stateFilePath);
                var stateData = JsonSerializer.Deserialize<PersistentStateData>(json);
                if (stateData != null)
                {
                    this.LastBackupTime = stateData.LastBackupTime;
                    this.BackupHistory = stateData.BackupHistory ?? [];
                    this.FileMetadata = stateData.FileMetadata ?? [];
                    _logger?.Log($"Loaded state: {FileMetadata.Count} file metadata entries, {BackupHistory.Count} backup history entries.", LogLevel.Info);
                }
                else
                {
                    _logger?.Log($"Deserialization of state file resulted in null: {_stateFilePath}", LogLevel.Warning);
                    InitializeEmptyState();
                }
            }
            else
            {
                _logger?.Log($"State file not found, initializing empty state: {_stateFilePath}", LogLevel.Info);
                InitializeEmptyState();
            }
        }
        catch (JsonException jsonEx)
        {
            _logger?.Log($"Error deserializing system state file {_stateFilePath}: {jsonEx.Message}. Initializing empty state.", LogLevel.Error);
            InitializeEmptyState();
        }
        catch (Exception ex)
        {
            _logger?.Log($"Error loading system state from {_stateFilePath}: {ex.Message}. Initializing empty state.", LogLevel.Error);
            InitializeEmptyState();
        }
    }

    private void InitializeEmptyState()
    {
        this.LastBackupTime = DateTime.MinValue;
        this.BackupHistory = [];
        this.FileMetadata = [];
    }

    public List<string> GetChangedFiles(List<string> allFiles, BackupType backupType)
    {
        if (backupType == BackupType.Full)
        {
            _logger?.Log("Full backup requested, including all files.", LogLevel.Info);
            return allFiles;
        }

        var filesToBackup = new List<string>();
        DateTime lastFullBackupTime = GetLastFullBackupTime();

        foreach (var filePath in allFiles)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                    continue;

                bool shouldBackup = false;

                if (!FileMetadata.TryGetValue(filePath, out var previousMetadata))
                {
                    _logger?.Log($"New file detected: {filePath}", LogLevel.Debug);
                    shouldBackup = true;
                }
                else
                {
                    if (backupType == BackupType.Incremental)
                    {
                        if (fileInfo.LastWriteTimeUtc > previousMetadata.LastModified || fileInfo.Length != previousMetadata.Size)
                        {
                            _logger?.Log($"File changed (Incremental): {filePath}", LogLevel.Debug);
                            shouldBackup = true;
                        }
                    }
                    else if (backupType == BackupType.Differential)
                    {
                        if (fileInfo.LastWriteTimeUtc > lastFullBackupTime)
                        {
                            _logger?.Log($"File changed (Differential): {filePath}", LogLevel.Debug);
                            shouldBackup = true;
                        }
                    }
                }

                if (shouldBackup)
                {
                    filesToBackup.Add(filePath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"Error checking file {filePath}: {ex.Message}. Including in backup.", LogLevel.Warning);
                filesToBackup.Add(filePath);
            }
        }

        _logger?.Log($"Found {filesToBackup.Count} changed files out of {allFiles.Count} total files for {backupType} backup.", LogLevel.Info);
        return filesToBackup;
    }

    private DateTime GetLastFullBackupTime()
    {
        DateTime lastFullTime = DateTime.MinValue;
        foreach (var historyList in BackupHistory.Values)
        {
            var lastFull = historyList
                .Where(b => !b.IsDiff)
                .OrderByDescending(b => b.Timestamp)
                .FirstOrDefault();

            if (lastFull != null && lastFull.Timestamp > lastFullTime)
            {
                lastFullTime = lastFull.Timestamp;
            }
        }
        _logger?.Log($"Last full backup time determined as: {lastFullTime}", LogLevel.Debug);
        return lastFullTime;
    }

    private static async Task<string> CalculateFileHashAsync(string filePath)
    {
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] hash = await sha256.ComputeHashAsync(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}

public class BackupInfo
{
    public string Path { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public bool IsDiff { get; set; }
}
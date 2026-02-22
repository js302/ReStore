using System.Text.Json;
using System.Text.RegularExpressions;
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
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileMetadata> FileMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public partial class SystemState
{
    public DateTime LastBackupTime { get; set; }
    public List<string> TrackedDirectories { get; set; } = [];
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileMetadata> FileMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private string _stateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "ReStore",
        "state",
        "system_state.json"
    );
    [JsonIgnore]
    private readonly ILogger? _logger;
    private readonly Lock _stateLock = new();

    public SystemState(ILogger? logger = null)
    {
        _logger = logger;
        _logger?.Log($"System state will be stored at: {_stateFilePath}", LogLevel.Debug);
    }

    public void SetStateFilePath(string path)
    {
        lock (_stateLock)
        {
            _stateFilePath = path;
        }
        _logger?.Log($"System state file path set to: {_stateFilePath}", LogLevel.Debug);
    }

    public virtual bool HasFileChanged(string path, string currentHash)
    {
        lock (_stateLock)
        {
            return !FileMetadata.TryGetValue(path, out var storedMetadata) || storedMetadata.Hash != currentHash;
        }
    }

    public virtual string? GetPreviousBackupPath(string directory)
    {
        lock (_stateLock)
        {
            if (!BackupHistory.TryGetValue(directory, out var backups) || backups.Count == 0)
                return null;

            return backups.OrderByDescending(b => b.Timestamp).First().Path;
        }
    }

    public virtual string? GetBaseBackupPath(string diffPath)
    {
        lock (_stateLock)
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
    }

    private DateTime GetTimestampFromPath(string path)
    {
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                fileName = Path.GetFileNameWithoutExtension(fileName);
            }

            // Expected format: backup_<name>_<guid>_<timestamp> or similar patterns
            var match = TimestampRegex().Match(fileName);
            if (match.Success)
            {
                return DateTime.ParseExact(match.Groups[1].Value, "yyyyMMddHHmmss", null);
            }

            // Fallback: try the last underscore-separated segment
            var parts = fileName.Split('_');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (parts[i].Length == 14 && long.TryParse(parts[i], out _))
                {
                    return DateTime.ParseExact(parts[i], "yyyyMMddHHmmss", null);
                }
            }

            _logger?.Log($"No valid timestamp found in filename: {fileName}", LogLevel.Warning);
            return DateTime.MinValue;
        }
        catch (Exception ex)
        {
            _logger?.Log($"Failed to parse timestamp from path '{path}': {ex.Message}", LogLevel.Warning);
            return DateTime.MinValue;
        }
    }

    [GeneratedRegex(@"(\d{14})(?:[^\d]|$)")]
    private static partial Regex TimestampRegex();

    public virtual void AddBackup(string directory, string path, bool isDiff, long sizeBytes = 0)
    {
        lock (_stateLock)
        {
            if (!BackupHistory.ContainsKey(directory))
                BackupHistory[directory] = [];

            BackupHistory[directory].Add(new BackupInfo
            {
                Path = path,
                Timestamp = DateTime.UtcNow,
                IsDiff = isDiff,
                StorageType = null,
                SizeBytes = sizeBytes
            });
        }
    }

    public virtual void AddBackup(string directory, string path, bool isDiff, string? storageType, long sizeBytes = 0)
    {
        lock (_stateLock)
        {
            if (!BackupHistory.ContainsKey(directory))
                BackupHistory[directory] = [];

            BackupHistory[directory].Add(new BackupInfo
            {
                Path = path,
                Timestamp = DateTime.UtcNow,
                IsDiff = isDiff,
                StorageType = storageType,
                SizeBytes = sizeBytes
            });
        }
    }

    public virtual List<string> GetBackupGroups()
    {
        lock (_stateLock)
        {
            return [.. BackupHistory.Keys];
        }
    }

    public virtual List<BackupInfo> GetBackupsForGroup(string group)
    {
        lock (_stateLock)
        {
            if (!BackupHistory.TryGetValue(group, out var backups))
            {
                return [];
            }

            return [.. backups
                .OrderByDescending(b => b.Timestamp)
                .Select(b => new BackupInfo
                {
                    Path = b.Path,
                    Timestamp = b.Timestamp,
                    IsDiff = b.IsDiff,
                    StorageType = b.StorageType,
                    SizeBytes = b.SizeBytes
                })];
        }
    }

    public virtual void RemoveBackupsFromGroup(string group, IEnumerable<string> backupPaths)
    {
        var paths = backupPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!BackupHistory.TryGetValue(group, out var backups) || backups.Count == 0)
            {
                return;
            }

            backups.RemoveAll(b => paths.Contains(b.Path));

            if (backups.Count == 0)
            {
                BackupHistory.Remove(group);
            }
        }
    }

    public virtual async Task AddOrUpdateFileMetadataAsync(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                lock (_stateLock)
                {
                    if (FileMetadata.ContainsKey(filePath))
                    {
                        FileMetadata.Remove(filePath);
                        _logger?.Log($"Removed metadata for deleted file: {filePath}", LogLevel.Debug);
                    }
                }
                return;
            }

            var hash = await CalculateFileHashAsync(filePath);

            lock (_stateLock)
            {
                var metadata = new FileMetadata
                {
                    FilePath = filePath,
                    Size = fileInfo.Length,
                    LastModified = fileInfo.LastWriteTimeUtc,
                    Hash = hash
                };

                FileMetadata[filePath] = metadata;
            }
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

    public virtual async Task SaveStateAsync()
    {
        PersistentStateData stateData;
        string stateFilePath;

        lock (_stateLock)
        {
            stateData = new PersistentStateData
            {
                LastBackupTime = LastBackupTime,
                BackupHistory = CloneBackupHistory(BackupHistory),
                FileMetadata = CloneFileMetadata(FileMetadata)
            };

            stateFilePath = _stateFilePath;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath)!);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(stateData, options);

            var tempPath = stateFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, stateFilePath, overwrite: true);

            _logger?.Log($"System state saved to {stateFilePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger?.Log($"Error saving system state to {stateFilePath}: {ex.Message}", LogLevel.Error);
        }
    }

    public virtual async Task LoadStateAsync()
    {
        string stateFilePath;

        lock (_stateLock)
        {
            stateFilePath = _stateFilePath;
        }

        try
        {
            if (File.Exists(stateFilePath))
            {
                _logger?.Log($"Loading system state from {stateFilePath}", LogLevel.Debug);
                string json = await File.ReadAllTextAsync(stateFilePath);
                var stateData = JsonSerializer.Deserialize<PersistentStateData>(json);
                if (stateData != null)
                {
                    lock (_stateLock)
                    {
                        LastBackupTime = stateData.LastBackupTime;
                        BackupHistory = stateData.BackupHistory != null ? new(stateData.BackupHistory, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
                        FileMetadata = stateData.FileMetadata != null ? new(stateData.FileMetadata, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
                    }
                    _logger?.Log($"Loaded state: {FileMetadata.Count} file metadata entries, {BackupHistory.Count} backup history entries.", LogLevel.Info);
                }
                else
                {
                    _logger?.Log($"Deserialization of state file resulted in null: {stateFilePath}", LogLevel.Warning);
                    InitializeEmptyState();
                }
            }
            else
            {
                _logger?.Log($"State file not found, initializing empty state: {stateFilePath}", LogLevel.Info);
                InitializeEmptyState();
            }
        }
        catch (JsonException jsonEx)
        {
            _logger?.Log($"Error deserializing system state file {stateFilePath}: {jsonEx.Message}. Initializing empty state.", LogLevel.Error);
            InitializeEmptyState();
        }
        catch (Exception ex)
        {
            _logger?.Log($"Error loading system state from {stateFilePath}: {ex.Message}. Initializing empty state.", LogLevel.Error);
            InitializeEmptyState();
        }
    }

    private void InitializeEmptyState()
    {
        lock (_stateLock)
        {
            LastBackupTime = DateTime.MinValue;
            BackupHistory = new(StringComparer.OrdinalIgnoreCase);
            FileMetadata = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public virtual List<string> GetTrackedFilesInDirectory(string directory)
    {
        var normalizedDir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        lock (_stateLock)
        {
            return FileMetadata.Keys
                .Where(p => p.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                            p.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public virtual List<string> GetChangedFiles(List<string> allFiles, BackupType backupType)
    {
        if (backupType == BackupType.Full)
        {
            _logger?.Log("Full backup requested, including all files.", LogLevel.Info);
            return allFiles;
        }

        var filesToBackup = new List<string>();
        DateTime lastFullBackupTime;
        Dictionary<string, FileMetadata> currentMetadataSnapshot;

        lock (_stateLock)
        {
            lastFullBackupTime = GetLastFullBackupTime();
            currentMetadataSnapshot = new Dictionary<string, FileMetadata>(FileMetadata);
        }

        foreach (var filePath in allFiles)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                    continue;

                bool shouldBackup = false;

                if (!currentMetadataSnapshot.TryGetValue(filePath, out var previousMetadata))
                {
                    _logger?.Log($"New file detected: {filePath}", LogLevel.Debug);
                    shouldBackup = true;
                }
                else
                {
                    if (backupType == BackupType.Incremental)
                    {
                        if (fileInfo.Length != previousMetadata.Size)
                        {
                            _logger?.Log($"File changed (Incremental, size): {filePath}", LogLevel.Debug);
                            shouldBackup = true;
                        }
                        else if (fileInfo.LastWriteTimeUtc > previousMetadata.LastModified)
                        {
                            if (!string.IsNullOrEmpty(previousMetadata.Hash))
                            {
                                var currentHash = CalculateFileHash(filePath);
                                if (!string.IsNullOrEmpty(currentHash) && currentHash != previousMetadata.Hash)
                                {
                                    _logger?.Log($"File changed (Incremental, hash): {filePath}", LogLevel.Debug);
                                    shouldBackup = true;
                                }
                            }
                            else
                            {
                                _logger?.Log($"File changed (Incremental, timestamp): {filePath}", LogLevel.Debug);
                                shouldBackup = true;
                            }
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

    private static string CalculateFileHash(string filePath)
    {
        try
        {
            using var sha256 = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] hash = sha256.ComputeHash(stream);
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

    private static Dictionary<string, List<BackupInfo>> CloneBackupHistory(Dictionary<string, List<BackupInfo>> backupHistory)
    {
        return backupHistory.ToDictionary(
            item => item.Key,
            item => item.Value.Select(backup => new BackupInfo
            {
                Path = backup.Path,
                Timestamp = backup.Timestamp,
                IsDiff = backup.IsDiff,
                StorageType = backup.StorageType
            }).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, FileMetadata> CloneFileMetadata(Dictionary<string, FileMetadata> fileMetadata)
    {
        return fileMetadata.ToDictionary(
            item => item.Key,
            item => new FileMetadata
            {
                FilePath = item.Value.FilePath,
                Size = item.Value.Size,
                LastModified = item.Value.LastModified,
                Hash = item.Value.Hash
            },
            StringComparer.OrdinalIgnoreCase);
    }
}

public class BackupInfo
{
    public string Path { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public bool IsDiff { get; set; }
    public string? StorageType { get; set; }
    public long SizeBytes { get; set; }
}
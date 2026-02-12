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
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = [];
    public Dictionary<string, FileMetadata> FileMetadata { get; set; } = [];
}

public partial class SystemState
{
    public DateTime LastBackupTime { get; set; }
    public List<string> TrackedDirectories { get; set; } = [];
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = [];
    public Dictionary<string, FileMetadata> FileMetadata { get; set; } = [];

    private string _stateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "ReStore",
        "state",
        "system_state.json"
    );
    [JsonIgnore]
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SystemState(ILogger? logger = null)
    {
        _logger = logger;
        _logger?.Log($"System state will be stored at: {_stateFilePath}", LogLevel.Debug);
    }

    public void SetStateFilePath(string path)
    {
        _stateFilePath = path;
        _logger?.Log($"System state file path set to: {_stateFilePath}", LogLevel.Debug);
    }

    public virtual bool HasFileChanged(string path, string currentHash)
    {
        _lock.Wait();
        try
        {
            return !FileMetadata.TryGetValue(path, out var storedMetadata) || storedMetadata.Hash != currentHash;
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual string? GetPreviousBackupPath(string directory)
    {
        _lock.Wait();
        try
        {
            if (!BackupHistory.TryGetValue(directory, out var backups) || backups.Count == 0)
                return null;

            return backups.OrderByDescending(b => b.Timestamp).First().Path;
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual string? GetBaseBackupPath(string diffPath)
    {
        _lock.Wait();
        try
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
        finally
        {
            _lock.Release();
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

    public virtual void AddBackup(string directory, string path, bool isDiff)
    {
        _lock.Wait();
        try
        {
            if (!BackupHistory.ContainsKey(directory))
                BackupHistory[directory] = [];

            BackupHistory[directory].Add(new BackupInfo
            {
                Path = path,
                Timestamp = DateTime.UtcNow,
                IsDiff = isDiff,
                StorageType = null
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual void AddBackup(string directory, string path, bool isDiff, string? storageType)
    {
        _lock.Wait();
        try
        {
            if (!BackupHistory.ContainsKey(directory))
                BackupHistory[directory] = [];

            BackupHistory[directory].Add(new BackupInfo
            {
                Path = path,
                Timestamp = DateTime.UtcNow,
                IsDiff = isDiff,
                StorageType = storageType
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual List<string> GetBackupGroups()
    {
        _lock.Wait();
        try
        {
            return [.. BackupHistory.Keys];
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual List<BackupInfo> GetBackupsForGroup(string group)
    {
        _lock.Wait();
        try
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
                    StorageType = b.StorageType
                })];
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual void RemoveBackupsFromGroup(string group, IEnumerable<string> backupPaths)
    {
        var paths = backupPaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return;
        }

        _lock.Wait();
        try
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
        finally
        {
            _lock.Release();
        }
    }

    public virtual async Task AddOrUpdateFileMetadataAsync(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                await _lock.WaitAsync();
                try
                {
                    if (FileMetadata.ContainsKey(filePath))
                    {
                        FileMetadata.Remove(filePath);
                        _logger?.Log($"Removed metadata for deleted file: {filePath}", LogLevel.Debug);
                    }
                }
                finally
                {
                    _lock.Release();
                }
                return;
            }

            var hash = await CalculateFileHashAsync(filePath);

            await _lock.WaitAsync();
            try
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
            finally
            {
                _lock.Release();
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
        await _lock.WaitAsync();
        try
        {
            var stateData = new PersistentStateData
            {
                LastBackupTime = LastBackupTime,
                BackupHistory = BackupHistory,
                FileMetadata = FileMetadata
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(stateData, options);

            var tempPath = _stateFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _stateFilePath, overwrite: true);

            _logger?.Log($"System state saved to {_stateFilePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger?.Log($"Error saving system state to {_stateFilePath}: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            _lock.Release();
        }
    }

    public virtual async Task LoadStateAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (File.Exists(_stateFilePath))
            {
                _logger?.Log($"Loading system state from {_stateFilePath}", LogLevel.Debug);
                string json = await File.ReadAllTextAsync(_stateFilePath);
                var stateData = JsonSerializer.Deserialize<PersistentStateData>(json);
                if (stateData != null)
                {
                    LastBackupTime = stateData.LastBackupTime;
                    BackupHistory = stateData.BackupHistory ?? [];
                    FileMetadata = stateData.FileMetadata ?? [];
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
        finally
        {
            _lock.Release();
        }
    }

    private void InitializeEmptyState()
    {
        LastBackupTime = DateTime.MinValue;
        BackupHistory = [];
        FileMetadata = [];
    }

    public virtual List<string> GetChangedFiles(List<string> allFiles, BackupType backupType)
    {
        if (backupType == BackupType.Full)
        {
            _logger?.Log("Full backup requested, including all files.", LogLevel.Info);
            return allFiles;
        }

        var filesToBackup = new List<string>();

        _lock.Wait();
        try
        {
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
                            if (fileInfo.Length != previousMetadata.Size)
                            {
                                _logger?.Log($"File changed (Incremental, size): {filePath}", LogLevel.Debug);
                                shouldBackup = true;
                            }
                            else if (fileInfo.LastWriteTimeUtc > previousMetadata.LastModified)
                            {
                                if (!string.IsNullOrEmpty(previousMetadata.Hash))
                                {
                                    var currentHash = CalculateFileHashAsync(filePath).GetAwaiter().GetResult();
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
        }
        finally
        {
            _lock.Release();
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
    public string? StorageType { get; set; }
}
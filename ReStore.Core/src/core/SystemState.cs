using System.Text.Json;
using System.Text.RegularExpressions;
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

public class SnapshotBackupTelemetryAggregate
{
    public long SnapshotCount { get; set; }
    public long FileCount { get; set; }
    public long ChunkReferences { get; set; }
    public long UniqueChunks { get; set; }
    public long UploadedChunks { get; set; }
    public long UniqueReusedChunks { get; set; }
    public long StorageHitChunks { get; set; }
    public long CandidateChunks { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
}

public class SnapshotRestoreTelemetryAggregate
{
    public long AttemptCount { get; set; }
    public long SuccessCount { get; set; }
    public long ValidationFailureCount { get; set; }
    public long FilesExpected { get; set; }
    public long FilesRestored { get; set; }
    public long ChunkReferencesExpected { get; set; }
    public long ChunkReferencesProcessed { get; set; }
    public long ChunkDownloads { get; set; }
    public long ChunkCacheHits { get; set; }
    public Dictionary<string, long> FailureCategoryCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastUpdatedUtc { get; set; }
}

public class SnapshotVerificationTelemetryAggregate
{
    public long RunCount { get; set; }
    public long SuccessCount { get; set; }
    public long ValidationFailureCount { get; set; }
    public long FileCount { get; set; }
    public long ChunkReferences { get; set; }
    public long UniqueChunks { get; set; }
    public long DownloadedChunks { get; set; }
    public long MissingChunks { get; set; }
    public long InvalidChunks { get; set; }
    public long InvalidFiles { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
}

public class SnapshotTelemetryAggregate
{
    public SnapshotBackupTelemetryAggregate Backup { get; set; } = new();
    public SnapshotRestoreTelemetryAggregate Restore { get; set; } = new();
    public SnapshotVerificationTelemetryAggregate Verification { get; set; } = new();
}

internal class PersistentStateData
{
    public DateTime LastBackupTime { get; set; }
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileMetadata> FileMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ChunkReferenceCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SnapshotTelemetryAggregate Telemetry { get; set; } = new();
}

public partial class SystemState
{
    public DateTime LastBackupTime { get; set; }
    public List<string> TrackedDirectories { get; set; } = [];
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileMetadata> FileMetadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ChunkReferenceCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SnapshotTelemetryAggregate Telemetry { get; set; } = new();

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
                SizeBytes = sizeBytes,
                ArtifactType = BackupArtifactType.Archive,
                ChunkIds = []
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
                SizeBytes = sizeBytes,
                ArtifactType = BackupArtifactType.Archive,
                ChunkIds = []
            });
        }
    }

    public virtual void AddSnapshotBackup(
        string directory,
        string snapshotId,
        string manifestPath,
        string? storageType,
        IEnumerable<string> chunkIds,
        long sizeBytes = 0,
        string? rootHash = null,
        bool encrypted = false,
        string? chunkStorageNamespace = null)
    {
        var normalizedChunkIds = chunkIds
            .Where(chunkId => !string.IsNullOrWhiteSpace(chunkId))
            .Select(chunkId => chunkId.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_stateLock)
        {
            if (!BackupHistory.TryGetValue(directory, out List<BackupInfo>? value))
            {
                value = [];
                BackupHistory[directory] = value;
            }

            value.Add(new BackupInfo
            {
                Path = manifestPath,
                Timestamp = DateTime.UtcNow,
                IsDiff = false,
                StorageType = storageType,
                SizeBytes = sizeBytes,
                ArtifactType = BackupArtifactType.SnapshotManifest,
                SnapshotId = snapshotId,
                ManifestPath = manifestPath,
                ChunkIds = normalizedChunkIds,
                RootHash = rootHash,
                Encrypted = encrypted,
                ChunkStorageNamespace = chunkStorageNamespace
            });

            RegisterChunkReferencesLocked(storageType, chunkStorageNamespace, normalizedChunkIds);
        }
    }

    public virtual void RegisterChunkReferences(string? storageType, IEnumerable<string> chunkIds, string? chunkStorageNamespace = null)
    {
        var normalizedChunkIds = chunkIds
            .Where(chunkId => !string.IsNullOrWhiteSpace(chunkId))
            .Select(chunkId => chunkId.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_stateLock)
        {
            RegisterChunkReferencesLocked(storageType, chunkStorageNamespace, normalizedChunkIds);
        }
    }

    public virtual List<string> UnregisterChunkReferences(string? storageType, IEnumerable<string> chunkIds, string? chunkStorageNamespace = null)
    {
        var normalizedChunkIds = chunkIds
            .Where(chunkId => !string.IsNullOrWhiteSpace(chunkId))
            .Select(chunkId => chunkId.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unreferencedChunkIds = new List<string>();

        lock (_stateLock)
        {
            foreach (var chunkId in normalizedChunkIds)
            {
                var referenceKey = BuildChunkReferenceKey(storageType, chunkStorageNamespace, chunkId);
                if (!ChunkReferenceCounts.TryGetValue(referenceKey, out var count))
                {
                    continue;
                }

                if (count <= 1)
                {
                    ChunkReferenceCounts.Remove(referenceKey);
                    unreferencedChunkIds.Add(chunkId);
                    continue;
                }

                ChunkReferenceCounts[referenceKey] = count - 1;
            }
        }

        return unreferencedChunkIds;
    }

    public virtual void RecordSnapshotBackupTelemetry(
        int fileCount,
        int chunkReferences,
        int uniqueChunks,
        int uploadedChunks,
        int uniqueReusedChunks,
        int storageHitChunks,
        int candidateChunks)
    {
        lock (_stateLock)
        {
            NormalizeTelemetryLocked();

            var backup = Telemetry.Backup;
            backup.SnapshotCount++;
            backup.FileCount += fileCount;
            backup.ChunkReferences += chunkReferences;
            backup.UniqueChunks += uniqueChunks;
            backup.UploadedChunks += uploadedChunks;
            backup.UniqueReusedChunks += uniqueReusedChunks;
            backup.StorageHitChunks += storageHitChunks;
            backup.CandidateChunks += candidateChunks;
            backup.LastUpdatedUtc = DateTime.UtcNow;
        }
    }

    public virtual void RecordRestoreTelemetry(
        bool success,
        int filesExpected,
        int filesRestored,
        int chunkReferencesExpected,
        int chunkReferencesProcessed,
        int chunkDownloads,
        int chunkCacheHits,
        string? failureCategory,
        int validationFailures)
    {
        lock (_stateLock)
        {
            NormalizeTelemetryLocked();

            var restore = Telemetry.Restore;
            restore.AttemptCount++;
            if (success)
            {
                restore.SuccessCount++;
            }

            restore.ValidationFailureCount += validationFailures;
            restore.FilesExpected += filesExpected;
            restore.FilesRestored += filesRestored;
            restore.ChunkReferencesExpected += chunkReferencesExpected;
            restore.ChunkReferencesProcessed += chunkReferencesProcessed;
            restore.ChunkDownloads += chunkDownloads;
            restore.ChunkCacheHits += chunkCacheHits;

            if (!success && !string.IsNullOrWhiteSpace(failureCategory))
            {
                if (!restore.FailureCategoryCounts.TryGetValue(failureCategory, out var count))
                {
                    restore.FailureCategoryCounts[failureCategory] = 1;
                }
                else
                {
                    restore.FailureCategoryCounts[failureCategory] = count + 1;
                }
            }

            restore.LastUpdatedUtc = DateTime.UtcNow;
        }
    }

    public virtual void RecordVerificationTelemetry(
        bool success,
        int fileCount,
        int chunkReferences,
        int uniqueChunks,
        int downloadedChunks,
        int missingChunks,
        int invalidChunks,
        int invalidFiles,
        int validationFailures)
    {
        lock (_stateLock)
        {
            NormalizeTelemetryLocked();

            var verification = Telemetry.Verification;
            verification.RunCount++;
            if (success)
            {
                verification.SuccessCount++;
            }

            verification.ValidationFailureCount += validationFailures;
            verification.FileCount += fileCount;
            verification.ChunkReferences += chunkReferences;
            verification.UniqueChunks += uniqueChunks;
            verification.DownloadedChunks += downloadedChunks;
            verification.MissingChunks += missingChunks;
            verification.InvalidChunks += invalidChunks;
            verification.InvalidFiles += invalidFiles;
            verification.LastUpdatedUtc = DateTime.UtcNow;
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
                    SizeBytes = b.SizeBytes,
                    ArtifactType = b.ArtifactType,
                    SnapshotId = b.SnapshotId,
                    ManifestPath = b.ManifestPath,
                    ChunkIds = [.. b.ChunkIds],
                    RootHash = b.RootHash,
                    Encrypted = b.Encrypted,
                    ChunkStorageNamespace = b.ChunkStorageNamespace
                })];
        }
    }

    public virtual DateTime GetLastFullBackupTime(string group)
    {
        lock (_stateLock)
        {
            return GetLastFullBackupTimeLocked(group);
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
                FileMetadata = CloneFileMetadata(FileMetadata),
                ChunkReferenceCounts = CloneChunkReferenceCounts(ChunkReferenceCounts),
                Telemetry = CloneTelemetry(Telemetry)
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
                        ChunkReferenceCounts = stateData.ChunkReferenceCounts != null ? new(stateData.ChunkReferenceCounts, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
                        Telemetry = stateData.Telemetry ?? new SnapshotTelemetryAggregate();
                        NormalizeTelemetryLocked();

                        if (ChunkReferenceCounts.Count == 0 || ContainsLegacyChunkReferenceKeysLocked())
                        {
                            RebuildChunkReferenceCountsLocked();
                        }
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
            ChunkReferenceCounts = new(StringComparer.OrdinalIgnoreCase);
            Telemetry = new SnapshotTelemetryAggregate();
        }
    }

    public virtual List<string> GetTrackedFilesInDirectory(string directory)
    {
        var normalizedDirectory = NormalizePath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directoryPrefix = normalizedDirectory + Path.DirectorySeparatorChar;

        lock (_stateLock)
        {
            return FileMetadata.Keys
                .Where(path =>
                {
                    var normalizedPath = NormalizePath(path);
                    return normalizedPath.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
                           normalizedPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }
    }

    public virtual List<string> GetChangedFiles(List<string> allFiles, BackupType backupType)
    {
        return GetChangedFiles(allFiles, backupType, null);
    }

    public virtual List<string> GetChangedFiles(List<string> allFiles, BackupType backupType, string? group)
    {
        if (backupType == BackupType.Full)
        {
            _logger?.Log("Full backup requested, including all files.", LogLevel.Info);
            return allFiles;
        }

        var filesToBackup = new List<string>();
        Dictionary<string, FileMetadata> currentMetadataSnapshot;

        lock (_stateLock)
        {
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
                    else if (backupType == BackupType.ChunkSnapshot)
                    {
                        if (fileInfo.Length != previousMetadata.Size)
                        {
                            _logger?.Log($"File changed (ChunkSnapshot, size): {filePath}", LogLevel.Debug);
                            shouldBackup = true;
                        }
                        else if (fileInfo.LastWriteTimeUtc > previousMetadata.LastModified)
                        {
                            var currentHash = CalculateFileHash(filePath);
                            if (!string.IsNullOrEmpty(currentHash) && currentHash != previousMetadata.Hash)
                            {
                                _logger?.Log($"File changed (ChunkSnapshot, hash): {filePath}", LogLevel.Debug);
                                shouldBackup = true;
                            }
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

    private DateTime GetLastFullBackupTimeLocked(string? group)
    {
        DateTime lastFullTime = DateTime.MinValue;

        IEnumerable<KeyValuePair<string, List<BackupInfo>>> historyGroups = BackupHistory;
        if (!string.IsNullOrWhiteSpace(group))
        {
            historyGroups = BackupHistory.Where(entry => entry.Key.Equals(group, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var historyList in historyGroups.Select(entry => entry.Value))
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
            return await FileHasher.ComputeHashAsync(filePath);
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
            return FileHasher.ComputeHash(filePath);
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
                StorageType = backup.StorageType,
                SizeBytes = backup.SizeBytes,
                ArtifactType = backup.ArtifactType,
                SnapshotId = backup.SnapshotId,
                ManifestPath = backup.ManifestPath,
                ChunkIds = [.. backup.ChunkIds],
                RootHash = backup.RootHash,
                Encrypted = backup.Encrypted,
                ChunkStorageNamespace = backup.ChunkStorageNamespace
            }).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> CloneChunkReferenceCounts(Dictionary<string, int> chunkReferenceCounts)
    {
        return new Dictionary<string, int>(chunkReferenceCounts, StringComparer.OrdinalIgnoreCase);
    }

    private static SnapshotTelemetryAggregate CloneTelemetry(SnapshotTelemetryAggregate telemetry)
    {
        return new SnapshotTelemetryAggregate
        {
            Backup = new SnapshotBackupTelemetryAggregate
            {
                SnapshotCount = telemetry.Backup.SnapshotCount,
                FileCount = telemetry.Backup.FileCount,
                ChunkReferences = telemetry.Backup.ChunkReferences,
                UniqueChunks = telemetry.Backup.UniqueChunks,
                UploadedChunks = telemetry.Backup.UploadedChunks,
                UniqueReusedChunks = telemetry.Backup.UniqueReusedChunks,
                StorageHitChunks = telemetry.Backup.StorageHitChunks,
                CandidateChunks = telemetry.Backup.CandidateChunks,
                LastUpdatedUtc = telemetry.Backup.LastUpdatedUtc
            },
            Restore = new SnapshotRestoreTelemetryAggregate
            {
                AttemptCount = telemetry.Restore.AttemptCount,
                SuccessCount = telemetry.Restore.SuccessCount,
                ValidationFailureCount = telemetry.Restore.ValidationFailureCount,
                FilesExpected = telemetry.Restore.FilesExpected,
                FilesRestored = telemetry.Restore.FilesRestored,
                ChunkReferencesExpected = telemetry.Restore.ChunkReferencesExpected,
                ChunkReferencesProcessed = telemetry.Restore.ChunkReferencesProcessed,
                ChunkDownloads = telemetry.Restore.ChunkDownloads,
                ChunkCacheHits = telemetry.Restore.ChunkCacheHits,
                FailureCategoryCounts = new Dictionary<string, long>(telemetry.Restore.FailureCategoryCounts, StringComparer.OrdinalIgnoreCase),
                LastUpdatedUtc = telemetry.Restore.LastUpdatedUtc
            },
            Verification = new SnapshotVerificationTelemetryAggregate
            {
                RunCount = telemetry.Verification.RunCount,
                SuccessCount = telemetry.Verification.SuccessCount,
                ValidationFailureCount = telemetry.Verification.ValidationFailureCount,
                FileCount = telemetry.Verification.FileCount,
                ChunkReferences = telemetry.Verification.ChunkReferences,
                UniqueChunks = telemetry.Verification.UniqueChunks,
                DownloadedChunks = telemetry.Verification.DownloadedChunks,
                MissingChunks = telemetry.Verification.MissingChunks,
                InvalidChunks = telemetry.Verification.InvalidChunks,
                InvalidFiles = telemetry.Verification.InvalidFiles,
                LastUpdatedUtc = telemetry.Verification.LastUpdatedUtc
            }
        };
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

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private void RegisterChunkReferencesLocked(string? storageType, string? chunkStorageNamespace, IEnumerable<string> normalizedChunkIds)
    {
        foreach (var chunkId in normalizedChunkIds)
        {
            var referenceKey = BuildChunkReferenceKey(storageType, chunkStorageNamespace, chunkId);
            if (!ChunkReferenceCounts.TryGetValue(referenceKey, out var count))
            {
                ChunkReferenceCounts[referenceKey] = 1;
                continue;
            }

            ChunkReferenceCounts[referenceKey] = count + 1;
        }
    }

    private static string BuildChunkReferenceKey(string? storageType, string? chunkStorageNamespace, string chunkId)
    {
        var normalizedStorage = string.IsNullOrWhiteSpace(storageType)
            ? "default"
            : storageType.Trim().ToLowerInvariant();

        var normalizedNamespace = string.IsNullOrWhiteSpace(chunkStorageNamespace)
            ? "legacy"
            : chunkStorageNamespace.Trim().ToLowerInvariant();

        return $"{normalizedStorage}|{normalizedNamespace}|{chunkId}";
    }

    private void NormalizeTelemetryLocked()
    {
        Telemetry ??= new SnapshotTelemetryAggregate();
        Telemetry.Backup ??= new SnapshotBackupTelemetryAggregate();
        Telemetry.Restore ??= new SnapshotRestoreTelemetryAggregate();
        Telemetry.Verification ??= new SnapshotVerificationTelemetryAggregate();

        var categoryCounts = Telemetry.Restore.FailureCategoryCounts ?? [];
        Telemetry.Restore.FailureCategoryCounts = new Dictionary<string, long>(categoryCounts, StringComparer.OrdinalIgnoreCase);
    }

    private void RebuildChunkReferenceCountsLocked()
    {
        ChunkReferenceCounts.Clear();

        foreach (var backup in BackupHistory.Values.SelectMany(backups => backups))
        {
            if (backup.ArtifactType != BackupArtifactType.SnapshotManifest || backup.ChunkIds.Count == 0)
            {
                continue;
            }

            RegisterChunkReferencesLocked(backup.StorageType, backup.ChunkStorageNamespace, backup.ChunkIds);
        }
    }

    private bool ContainsLegacyChunkReferenceKeysLocked()
    {
        foreach (var key in ChunkReferenceCounts.Keys)
        {
            if (key.Count(character => character == '|') < 2)
            {
                return true;
            }
        }

        return false;
    }
}

public class BackupInfo
{
    public string Path { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public bool IsDiff { get; set; }
    public string? StorageType { get; set; }
    public long SizeBytes { get; set; }
    public BackupArtifactType ArtifactType { get; set; } = BackupArtifactType.Archive;
    public string? SnapshotId { get; set; }
    public string? ManifestPath { get; set; }
    public List<string> ChunkIds { get; set; } = [];
    public string? RootHash { get; set; }
    public bool Encrypted { get; set; }
    public string? ChunkStorageNamespace { get; set; }
}

public enum BackupArtifactType
{
    Archive,
    SnapshotManifest
}
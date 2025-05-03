using System.Text.Json;
using System.Security.Cryptography;
using ReStore.src.utils;

namespace ReStore.src.core;

public class FileMetadata
{
    public string FilePath { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string Hash { get; set; } = "";
}

public class SystemState
{
    public DateTime LastBackupTime { get; set; }
    public Dictionary<string, string> FileHashes { get; set; } = [];
    public List<string> TrackedDirectories { get; set; } = [];
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = [];
    public Dictionary<string, FileMetadata> FileMetadata { get; set; } = [];

    private string _metadataPath = "state/metadata.json";
    private ILogger? _logger;

    public SystemState(ILogger? logger = null)
    {
        _logger = logger;
        LoadMetadata();
    }

    public void SetMetadataPath(string path)
    {
        _metadataPath = path;
    }

    public void AddOrUpdateFile(string path, string hash)
    {
        FileHashes[path] = hash;
    }

    public bool HasFileChanged(string path, string currentHash)
    {
        return !FileHashes.TryGetValue(path, out var storedHash) || storedHash != currentHash;
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
                return;

            var metadata = new FileMetadata
            {
                FilePath = filePath,
                Size = fileInfo.Length,
                LastModified = fileInfo.LastWriteTimeUtc,
                Hash = await CalculateFileHashAsync(filePath)
            };

            FileMetadata[filePath] = metadata;
        }
        catch (Exception ex)
        {
            _logger?.Log($"Error updating metadata for {filePath}: {ex.Message}", LogLevel.Warning);
        }
    }

    public async Task SaveMetadataAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_metadataPath)!);
            var json = JsonSerializer.Serialize(FileMetadata.Values.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(_metadataPath, json);
            _logger?.Log($"Saved metadata for {FileMetadata.Count} files", LogLevel.Info);
        }
        catch (Exception ex)
        {
            _logger?.Log($"Error saving metadata: {ex.Message}", LogLevel.Error);
        }
    }

    private void LoadMetadata()
    {
        try
        {
            if (File.Exists(_metadataPath))
            {
                string json = File.ReadAllText(_metadataPath);
                var metadata = JsonSerializer.Deserialize<List<FileMetadata>>(json);
                if (metadata != null)
                {
                    FileMetadata = metadata.ToDictionary(m => m.FilePath);
                    _logger?.Log($"Loaded metadata for {FileMetadata.Count} files", LogLevel.Info);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Log($"Error loading metadata: {ex.Message}", LogLevel.Error);
            FileMetadata = new Dictionary<string, FileMetadata>();
        }
    }

    public List<string> GetChangedFiles(List<string> allFiles, BackupType backupType)
    {
        if (backupType == BackupType.Full)
        {
            return allFiles;
        }

        var filesToBackup = new List<string>();

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
                    // New file
                    shouldBackup = true;
                }
                else
                {
                    if (backupType == BackupType.Incremental)
                    {
                        // For incremental backup, check if file was modified since last backup
                        if (fileInfo.LastWriteTimeUtc > previousMetadata.LastModified ||
                            fileInfo.Length != previousMetadata.Size)
                        {
                            shouldBackup = true;
                        }
                    }
                    else if (backupType == BackupType.Differential)
                    {
                        // For differential backup, check if file was modified since last full backup
                        if (fileInfo.LastWriteTimeUtc > LastBackupTime)
                        {
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
                _logger?.Log($"Error checking file {filePath}: {ex.Message}", LogLevel.Warning);
                // If we can't check, include it to be safe
                filesToBackup.Add(filePath);
            }
        }

        _logger?.Log($"Found {filesToBackup.Count} changed files out of {allFiles.Count} total files", LogLevel.Info);
        return filesToBackup;
    }

    private static async Task<string> CalculateFileHashAsync(string filePath)
    {
        using var md5 = MD5.Create();
        using var stream = File.OpenRead(filePath);
        byte[] hash = await md5.ComputeHashAsync(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}

public class BackupInfo
{
    public string Path { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public bool IsDiff { get; set; }
}
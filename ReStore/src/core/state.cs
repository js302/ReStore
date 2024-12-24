namespace ReStore.src.core;

public class SystemState
{
    public DateTime LastBackupTime { get; set; }
    public Dictionary<string, string> FileHashes { get; set; } = [];
    public List<string> TrackedDirectories { get; set; } = [];
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = [];

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
}

public class BackupInfo
{
    public string Path { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public bool IsDiff { get; set; }
}
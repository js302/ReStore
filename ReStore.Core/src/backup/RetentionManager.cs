using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.backup;

public class RetentionManager(ILogger logger, IConfigManager config, SystemState systemState)
{
    private readonly ILogger _logger = logger;
    private readonly IConfigManager _config = config;
    private readonly SystemState _systemState = systemState;

    public async Task ApplyAllAsync()
    {
        if (!_config.Retention.Enabled)
        {
            return;
        }

        foreach (var group in _systemState.GetBackupGroups())
        {
            await ApplyGroupAsync(group);
        }
    }

    public async Task ApplyGroupAsync(string group)
    {
        if (!_config.Retention.Enabled)
        {
            return;
        }

        var backups = _systemState.GetBackupsForGroup(group);
        var toDelete = SelectBackupsToDelete(backups, _config.Retention);
        if (toDelete.Count == 0)
        {
            return;
        }

        _logger.Log($"Retention: group '{group}' will delete {toDelete.Count} backup(s).", LogLevel.Info);

        foreach (var storageGroup in toDelete
                     .GroupBy(b => string.IsNullOrWhiteSpace(b.StorageType) ? _config.GlobalStorageType : b.StorageType!, StringComparer.OrdinalIgnoreCase))
        {
            var storageType = storageGroup.Key;

            IStorage? storage = null;
            try
            {
                storage = await _config.CreateStorageAsync(storageType);
            }
            catch (Exception ex)
            {
                _logger.Log($"Retention: failed to create storage '{storageType}' for group '{group}': {ex.Message}", LogLevel.Error);
                continue;
            }

            try
            {
                foreach (var backup in storageGroup)
                {
                    await DeleteBackupFromStorageAsync(storage, group, backup);
                }
            }
            finally
            {
                storage.Dispose();
            }
        }

        await _systemState.SaveStateAsync();
    }

    public static List<BackupInfo> SelectBackupsToDelete(List<BackupInfo> backups, RetentionConfig retention)
    {
        if (backups.Count <= 1)
        {
            return [];
        }

        var ordered = backups
            .Where(b => !string.IsNullOrWhiteSpace(b.Path))
            .OrderByDescending(b => b.Timestamp)
            .ToList();

        if (ordered.Count <= 1)
        {
            return [];
        }

        var keepLast = retention.KeepLastPerDirectory;
        if (keepLast < 1)
        {
            keepLast = 1;
        }

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var b in ordered.Take(keepLast))
        {
            keep.Add(b.Path);
        }

        if (retention.MaxAgeDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retention.MaxAgeDays);
            foreach (var b in ordered)
            {
                if (b.Timestamp.ToUniversalTime() >= cutoff)
                {
                    keep.Add(b.Path);
                }
            }
        }

        // Guarantee at least the newest backup remains, even if older than maxAgeDays.
        keep.Add(ordered[0].Path);

        return ordered.Where(b => !keep.Contains(b.Path)).ToList();
    }

    private async Task DeleteBackupFromStorageAsync(IStorage storage, string group, BackupInfo backup)
    {
        bool backupDeleted;

        if (backup.ArtifactType == BackupArtifactType.SnapshotManifest)
        {
            backupDeleted = await DeleteSnapshotManifestBackupAsync(storage, backup);
        }
        else
        {
            backupDeleted = await DeleteArchiveBackupAsync(storage, backup);
        }

        if (backupDeleted)
        {
            _systemState.RemoveBackupsFromGroup(group, [backup.Path]);
        }
    }

    private async Task<bool> DeleteSnapshotManifestBackupAsync(IStorage storage, BackupInfo backup)
    {
        var manifestPath = string.IsNullOrWhiteSpace(backup.ManifestPath)
            ? backup.Path
            : backup.ManifestPath;

        bool manifestDeleted;
        try
        {
            var exists = await storage.ExistsAsync(manifestPath);
            if (!exists)
            {
                _logger.Log($"Retention: snapshot manifest missing in storage (will drop from state): {manifestPath}", LogLevel.Warning);
                manifestDeleted = true;
            }
            else
            {
                await storage.DeleteAsync(manifestPath);
                _logger.Log($"Retention: deleted snapshot manifest: {manifestPath}", LogLevel.Info);
                manifestDeleted = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Retention: failed deleting snapshot manifest {manifestPath}: {ex.Message}", LogLevel.Warning);
            return false;
        }

        if (!manifestDeleted)
        {
            return false;
        }

        var unreferencedChunkIds = _systemState.UnregisterChunkReferences(
            backup.StorageType,
            backup.ChunkIds,
            backup.ChunkStorageNamespace);
        foreach (var chunkId in unreferencedChunkIds)
        {
            string chunkPath;
            try
            {
                chunkPath = SnapshotStoragePaths.GetChunkPath(chunkId, backup.ChunkStorageNamespace);
            }
            catch (ArgumentException ex)
            {
                _logger.Log($"Retention: invalid chunk metadata for '{chunkId}': {ex.Message}", LogLevel.Warning);
                continue;
            }

            try
            {
                var chunkExists = await storage.ExistsAsync(chunkPath);
                if (!chunkExists)
                {
                    continue;
                }

                await storage.DeleteAsync(chunkPath);
                _logger.Log($"Retention: deleted unreferenced chunk: {chunkPath}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                _logger.Log($"Retention: failed deleting chunk {chunkPath}: {ex.Message}", LogLevel.Warning);
            }
        }

        return true;
    }

    private async Task<bool> DeleteArchiveBackupAsync(IStorage storage, BackupInfo backup)
    {
        bool backupDeleted;

        try
        {
            var exists = await storage.ExistsAsync(backup.Path);
            if (!exists)
            {
                _logger.Log($"Retention: backup missing in storage (will drop from state): {backup.Path}", LogLevel.Warning);
                backupDeleted = true;
            }
            else
            {
                await storage.DeleteAsync(backup.Path);
                _logger.Log($"Retention: deleted backup: {backup.Path}", LogLevel.Info);
                backupDeleted = true;
            }

            if (backupDeleted && backup.Path.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
            {
                var metadataPath = backup.Path + ".meta";
                try
                {
                    var metaExists = await storage.ExistsAsync(metadataPath);
                    if (metaExists)
                    {
                        await storage.DeleteAsync(metadataPath);
                        _logger.Log($"Retention: deleted metadata: {metadataPath}", LogLevel.Debug);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"Retention: failed deleting metadata for {backup.Path}: {ex.Message}", LogLevel.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Retention: failed deleting {backup.Path}: {ex.Message}", LogLevel.Warning);
            return false;
        }

        return backupDeleted;
    }
}

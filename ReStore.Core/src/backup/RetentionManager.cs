using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.backup;

public class RetentionManager(ILogger logger, IConfigManager config, SystemState systemState, RepositoryCoordinator? coordinator = null, IPasswordProvider? passwordProvider = null)
{
    private readonly ILogger _logger = logger;
    private readonly IConfigManager _config = config;
    private readonly SystemState _systemState = systemState;
    private readonly RepositoryCoordinator _coordinator = coordinator ?? RepositoryCoordinator.Instance;

    // One manifest reader (= one key-file unlock, one password prompt at most) per storage within a retention run.
    private readonly Dictionary<IStorage, RepositoryManifestReader> _readers = new(ReferenceEqualityComparer.Instance);

    private RepositoryManifestReader ReaderFor(IStorage storage)
    {
        if (!_readers.TryGetValue(storage, out var reader))
        {
            _readers[storage] = reader = new RepositoryManifestReader(_logger, storage, passwordProvider, PasswordPurpose.Encrypt);
        }

        return reader;
    }

    private void ReleaseReaders()
    {
        foreach (var reader in _readers.Values)
        {
            reader.Dispose();
        }

        _readers.Clear();
    }

    /// <summary>
    /// Applies retention to every group. Takes the mutation lease itself; do not call while holding one.
    /// With <paramref name="busyTimeout"/> (the scheduler) a busy repository throws <see cref="RepositoryBusyException"/>
    /// instead of waiting.
    /// </summary>
    public async Task ApplyAllAsync(CancellationToken cancellationToken = default, TimeSpan? busyTimeout = null)
    {
        if (!_config.Retention.Enabled)
        {
            return;
        }

        var mutation = (busyTimeout == null
            ? await _coordinator.AcquireMutationAsync("Applying retention", cancellationToken, _systemState).ConfigureAwait(false)
            : await _coordinator.TryAcquireMutationAsync(busyTimeout.Value, "Applying retention", cancellationToken, _systemState).ConfigureAwait(false)
                ?? throw new RepositoryBusyException("Another operation is using the repository; retention was deferred."));
        await using var mutationScope = mutation.ConfigureAwait(false);
        foreach (var group in _systemState.GetBackupGroups())
        {
            await ApplyGroupAsync(group, mutation, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies retention now to <paramref name="groups"/> (<c>retention apply</c>), optionally only to the snapshots stored in
    /// <paramref name="onlyStorageType"/>, under one mutation lease. Nothing happens while retention is disabled.
    /// Takes the lease itself; do not call while holding one.
    /// </summary>
    public async Task ApplyToGroupsAsync(IReadOnlyCollection<string> groups, string? onlyStorageType = null, CancellationToken cancellationToken = default)
    {
        if (!_config.Retention.Enabled || groups.Count == 0)
        {
            return;
        }

        var mutation = await _coordinator.AcquireMutationAsync("Applying retention", cancellationToken, _systemState).ConfigureAwait(false);
        await using var mutationScope = mutation.ConfigureAwait(false);
        foreach (var group in groups)
        {
            await ApplyGroupAsync(group, mutation, cancellationToken, onlyStorageType).ConfigureAwait(false);
        }
    }

    /// <summary>Applies retention to one group. Takes the mutation lease itself; do not call while holding one.</summary>
    public async Task ApplyGroupAsync(string group, CancellationToken cancellationToken = default)
    {
        if (!_config.Retention.Enabled)
        {
            return;
        }

        var mutation = await _coordinator.AcquireMutationAsync($"Applying retention to {group}", cancellationToken, _systemState).ConfigureAwait(false);
        await using var mutationScope = mutation.ConfigureAwait(false);
        await ApplyGroupAsync(group, mutation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies retention for callers that already hold the mutation lease (a backup in its retention
    /// phase). Takes the exclusive delete lease, so it waits for running restores/verifications.
    /// </summary>
    public async Task ApplyGroupAsync(string group, RepositoryLease mutationLease, CancellationToken cancellationToken = default, string? onlyStorageType = null)
    {
        ArgumentNullException.ThrowIfNull(mutationLease);
        if (mutationLease.Kind != RepositoryLeaseKind.Mutation)
        {
            throw new ArgumentException("Retention requires the caller to hold the mutation lease.", nameof(mutationLease));
        }

        if (!_config.Retention.Enabled)
        {
            return;
        }

        var delete = await _coordinator.AcquireDeleteAsync($"Retention for {group}", cancellationToken).ConfigureAwait(false);
        await using var deleteScope = delete.ConfigureAwait(false);
        try
        {
            await RepairReferenceCountsAsync().ConfigureAwait(false);
            await RetryPendingChunkDeletesAsync(cancellationToken).ConfigureAwait(false);
            await ApplyGroupCoreAsync(group, onlyStorageType).ConfigureAwait(false);
        }
        finally
        {
            ReleaseReaders();
        }
    }

    /// <summary>
    /// Deletes one backup (manifest and any chunks no other snapshot references, or the archive) and
    /// removes it from state. Takes the mutation and delete leases, so concurrent deletes of the same
    /// backup serialise and the second finds it already gone: chunk reference counts are decremented
    /// once. Returns false if the backup was already gone or its storage delete failed (see the log).
    /// </summary>
    public async Task<bool> DeleteBackupAsync(string group, string backupPath, CancellationToken cancellationToken = default)
    {
        var mutation = await _coordinator.AcquireMutationAsync($"Deleting backup in {group}", cancellationToken, _systemState).ConfigureAwait(false);
        await using var mutationScope = mutation.ConfigureAwait(false);
        var delete = await _coordinator.AcquireDeleteAsync($"Deleting backup in {group}", cancellationToken).ConfigureAwait(false);
        await using var deleteScope = delete.ConfigureAwait(false);
        try
        {
            await RepairReferenceCountsAsync().ConfigureAwait(false);
            await RetryPendingChunkDeletesAsync(cancellationToken).ConfigureAwait(false);

            var backup = _systemState.GetBackupsForGroup(group)
                .FirstOrDefault(b => string.Equals(b.Path, backupPath, StringComparison.OrdinalIgnoreCase));
            if (backup == null)
            {
                _logger.Log($"Delete: backup already removed from state: {backupPath}", LogLevel.Info);
                return false;
            }

            var storageType = string.IsNullOrWhiteSpace(backup.StorageType) ? _config.GlobalStorageType : backup.StorageType!;
            using var storage = await _config.CreateStorageAsync(storageType).ConfigureAwait(false);

            await DeleteBackupFromStorageAsync(storage, group, backup).ConfigureAwait(false);
            var removedFromState = _systemState.GetBackupsForGroup(group)
                .All(b => !string.Equals(b.Path, backupPath, StringComparison.OrdinalIgnoreCase));

            await _systemState.SaveStateAsync().ConfigureAwait(false);
            return removedFromState;
        }
        finally
        {
            ReleaseReaders();
        }
    }

    private async Task ApplyGroupCoreAsync(string group, string? onlyStorageType = null)
    {

        var backups = _systemState.GetBackupsForGroup(group);
        var toDelete = SelectBackupsToDelete(backups, _config.Retention, _config.GlobalStorageType);
        if (onlyStorageType != null)
        {
            toDelete = [.. toDelete.Where(b => string.Equals(
                string.IsNullOrWhiteSpace(b.StorageType) ? _config.GlobalStorageType : b.StorageType, onlyStorageType, StringComparison.OrdinalIgnoreCase))];
        }

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
                storage = await _config.CreateStorageAsync(storageType).ConfigureAwait(false);
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
                    await DeleteBackupFromStorageAsync(storage, group, backup).ConfigureAwait(false);
                }
            }
            finally
            {
                storage.Dispose();
            }
        }

        await _systemState.SaveStateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The snapshots retention would delete, planned per storage type by <see cref="RetentionPlanner"/>.
    /// An empty StorageType counts as <paramref name="defaultStorageType"/>.
    /// </summary>
    public static List<BackupInfo> SelectBackupsToDelete(List<BackupInfo> backups, RetentionConfig retention, string defaultStorageType = "", DateTime? now = null)
        => RetentionPlanner.Plan(backups, retention, now ?? DateTime.UtcNow, defaultStorageType).ToDelete;

    /// <summary>Dry run: the full keep/delete plan for every group (or one), using the current state and the given policy. Changes nothing.</summary>
    public IReadOnlyList<(string Group, RetentionPlan Plan)> PreviewPlans(RetentionConfig policy, string? group = null, DateTime? now = null)
    {
        var groups = group == null ? _systemState.GetBackupGroups() : [group];
        return [.. groups.Select(g => (g, RetentionPlanner.Plan(_systemState.GetBackupsForGroup(g), policy, now ?? DateTime.UtcNow, _config.GlobalStorageType)))];
    }

    private async Task DeleteBackupFromStorageAsync(IStorage storage, string group, BackupInfo backup)
    {
        bool backupDeleted;

        if (backup.ArtifactType == BackupArtifactType.SnapshotManifest)
        {
            backupDeleted = await DeleteSnapshotManifestBackupAsync(storage, backup).ConfigureAwait(false);
        }
        else
        {
            backupDeleted = await DeleteArchiveBackupAsync(storage, backup).ConfigureAwait(false);
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

        // Resolve the chunk list before the manifest goes: it is the fallback source when the sidecar is missing.
        // If the list cannot be determined because storage is unavailable, keep everything so no reference count is lost.
        var chunkIds = await ResolveSnapshotChunkIdsAsync(storage, backup, manifestPath).ConfigureAwait(false);
        if (chunkIds == null)
        {
            return false;
        }

        bool manifestDeleted;
        try
        {
            var exists = await storage.ExistsAsync(manifestPath).ConfigureAwait(false);
            if (!exists)
            {
                _logger.Log($"Retention: snapshot manifest missing in storage (will drop from state): {manifestPath}", LogLevel.Warning);
                manifestDeleted = true;
            }
            else
            {
                await storage.DeleteAsync(manifestPath).ConfigureAwait(false);
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
            chunkIds,
            backup.ChunkStorageNamespace);
        var queueStorageType = string.IsNullOrWhiteSpace(backup.StorageType) ? _config.GlobalStorageType : backup.StorageType!;
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
                var chunkExists = await storage.ExistsAsync(chunkPath).ConfigureAwait(false);
                if (!chunkExists)
                {
                    continue;
                }

                await storage.DeleteAsync(chunkPath).ConfigureAwait(false);
                _logger.Log($"Retention: deleted unreferenced chunk: {chunkPath}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                // The reference count is already gone, so nothing would ever retry this: queue it (persisted with the state).
                _systemState.QueuePendingChunkDeletes(queueStorageType, [chunkId], backup.ChunkStorageNamespace);
                _logger.Log($"Retention: failed deleting chunk {chunkPath}, queued for retry: {ex.Message}", LogLevel.Warning);
            }
        }

        return true;
    }

    private static bool IsProtected(IReadOnlySet<string>? protectedChunkPaths, PendingChunkDelete item)
    {
        if (protectedChunkPaths == null)
        {
            return false;
        }

        try
        {
            return protectedChunkPaths.Contains(SnapshotStoragePaths.GetChunkPath(item.ChunkId, item.ChunkStorageNamespace));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Retries the chunk deletes that failed earlier (the persistent pending-delete queue). A queued chunk that is
    /// referenced again, or pinned by a backup, is dropped from the queue instead of deleted. Does nothing while the
    /// reference counts are incomplete. Call while holding the delete lease. Returns the number of chunks removed.
    /// <paramref name="protectedChunkPaths"/> (repair: every chunk some manifest references) are never deleted.
    /// </summary>
    public async Task<int> RetryPendingChunkDeletesAsync(CancellationToken cancellationToken = default, IReadOnlySet<string>? protectedChunkPaths = null)
    {
        if (_systemState.ChunkReferenceCountsIncomplete)
        {
            return 0;
        }

        var pending = _systemState.GetPendingChunkDeletes();
        if (pending.Count == 0)
        {
            return 0;
        }

        var deleted = 0;
        var finished = new List<PendingChunkDelete>();
        var configured = _config.StorageSources;
        foreach (var storageGroup in pending.GroupBy(item => item.StorageType, StringComparer.OrdinalIgnoreCase))
        {
            // A storage type that is no longer configured can never be retried: drop its entries rather than keep them forever
            // (a later repair against that storage finds any leftover chunks as orphans).
            if (configured is { Count: > 0 } && !configured.ContainsKey(storageGroup.Key))
            {
                _logger.Log($"Retention: dropping {storageGroup.Count()} pending chunk delete(s) for storage '{storageGroup.Key}', which is no longer configured.", LogLevel.Warning);
                finished.AddRange(storageGroup);
                continue;
            }

            IStorage storage;
            try
            {
                storage = await _config.CreateStorageAsync(storageGroup.Key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log($"Retention: cannot retry pending chunk deletes for storage '{storageGroup.Key}': {ex.Message}", LogLevel.Warning);
                continue;
            }

            using (storage)
            {
                foreach (var item in storageGroup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_systemState.IsChunkInUse(item.StorageType, item.ChunkStorageNamespace, item.ChunkId)
                        || IsProtected(protectedChunkPaths, item))
                    {
                        finished.Add(item);
                        continue;
                    }

                    try
                    {
                        var chunkPath = SnapshotStoragePaths.GetChunkPath(item.ChunkId, item.ChunkStorageNamespace);
                        await storage.DeleteAsync(chunkPath, cancellationToken).ConfigureAwait(false);
                        finished.Add(item);
                        deleted++;
                    }
                    catch (ArgumentException)
                    {
                        finished.Add(item);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"Retention: pending delete of chunk {item.ChunkId} failed again: {ex.Message}", LogLevel.Warning);
                    }
                }
            }
        }

        if (finished.Count > 0)
        {
            _systemState.RemovePendingChunkDeletes(finished);
            await _systemState.SaveStateAsync().ConfigureAwait(false);
            _logger.Log($"Retention: retried pending chunk deletes: {deleted} deleted, {finished.Count} resolved.", LogLevel.Info);
        }

        return deleted;
    }

    /// <summary>
    /// The snapshot's unique chunk ids: from state (pending list or sidecar), else from the manifest in storage.
    /// Returns null when the manifest is needed but storage failed (retry later). When neither the sidecar nor the
    /// manifest exists the list is unknowable: returns empty (the snapshot is dropped, its chunks stay referenced
    /// and are left for a later cleanup rather than risking a wrong decrement).
    /// </summary>
    private async Task<IReadOnlyList<string>?> ResolveSnapshotChunkIdsAsync(IStorage storage, BackupInfo backup, string manifestPath)
    {
        var fromState = await _systemState.TryGetSnapshotChunkIdsAsync(backup).ConfigureAwait(false);
        if (fromState != null)
        {
            return fromState;
        }

        _logger.Log($"Retention: chunk list for {manifestPath} is missing from state; reading the manifest.", LogLevel.Warning);
        var (ids, manifestMissing) = await ReadManifestChunkIdsAsync(storage, manifestPath).ConfigureAwait(false);
        if (ids != null)
        {
            return ids;
        }

        if (manifestMissing)
        {
            _logger.Log($"Retention: neither a chunk list nor the manifest exists for {manifestPath}; its chunks stay referenced.", LogLevel.Warning);
            return [];
        }

        _logger.Log($"Retention: could not determine the chunks of {manifestPath}, leaving it in place.", LogLevel.Warning);
        return null;
    }

    /// <summary>Ids read from the manifest in storage; (null, true) when the manifest does not exist, (null, false) on any other failure.</summary>
    private async Task<(IReadOnlyList<string>? Ids, bool ManifestMissing)> ReadManifestChunkIdsAsync(IStorage storage, string manifestPath)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"restore_retention_manifest_{Guid.NewGuid():N}.json");
        try
        {
            await storage.DownloadAsync(manifestPath, tempPath).ConfigureAwait(false);
            // Encrypted v3 manifests need the repository password; without it the read fails and the snapshot stays in place.
            var manifest = await ReaderFor(storage).ReadFileAsync(tempPath, manifestPath).ConfigureAwait(false);
            return (manifest.Files
                .SelectMany(file => file.Chunks)
                .Select(chunk => chunk.ChunkId.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(), false);
        }
        catch (FileNotFoundException)
        {
            return (null, true);
        }
        catch (Exception ex)
        {
            _logger.Log($"Retention: could not read manifest {manifestPath}: {ex.Message}", LogLevel.Warning);
            return (null, false);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// When the reference counts are flagged incomplete, re-reads the missing chunk lists from the manifests and
    /// rebuilds the counts. Needs storage: if a manifest cannot be read the counts stay flagged and chunk deletion
    /// stays disabled. Call while holding the delete lease.
    /// </summary>
    private async Task RepairReferenceCountsAsync()
    {
        if (!_systemState.ChunkReferenceCountsIncomplete)
        {
            return;
        }

        var recovered = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var missing = _systemState.GetSnapshotsWithoutChunkLists();
        foreach (var storageGroup in missing.GroupBy(
                     b => string.IsNullOrWhiteSpace(b.StorageType) ? _config.GlobalStorageType : b.StorageType!,
                     StringComparer.OrdinalIgnoreCase))
        {
            IStorage storage;
            try
            {
                storage = await _config.CreateStorageAsync(storageGroup.Key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log($"Retention: cannot repair reference counts for storage '{storageGroup.Key}': {ex.Message}", LogLevel.Warning);
                continue;
            }

            using (storage)
            {
                foreach (var backup in storageGroup)
                {
                    var manifestPath = string.IsNullOrWhiteSpace(backup.ManifestPath) ? backup.Path : backup.ManifestPath;
                    var (ids, manifestMissing) = await ReadManifestChunkIdsAsync(storage, manifestPath).ConfigureAwait(false);
                    if (ids != null)
                    {
                        recovered[backup.Path] = ids;
                    }
                    else if (manifestMissing)
                    {
                        _logger.Log($"Retention: manifest {manifestPath} no longer exists; counting it as having no chunks.", LogLevel.Warning);
                        recovered[backup.Path] = [];
                    }
                }
            }
        }

        if (await _systemState.RepairChunkReferenceCountsAsync(recovered).ConfigureAwait(false))
        {
            _logger.Log("Retention: chunk reference counts repaired.", LogLevel.Info);
            await _systemState.SaveStateAsync().ConfigureAwait(false);
        }
        else
        {
            _logger.Log("Retention: chunk reference counts are still incomplete; chunks will not be deleted.", LogLevel.Warning);
        }
    }

    private async Task<bool> DeleteArchiveBackupAsync(IStorage storage, BackupInfo backup)
    {
        bool backupDeleted;

        try
        {
            var exists = await storage.ExistsAsync(backup.Path).ConfigureAwait(false);
            if (!exists)
            {
                _logger.Log($"Retention: backup missing in storage (will drop from state): {backup.Path}", LogLevel.Warning);
                backupDeleted = true;
            }
            else
            {
                await storage.DeleteAsync(backup.Path).ConfigureAwait(false);
                _logger.Log($"Retention: deleted backup: {backup.Path}", LogLevel.Info);
                backupDeleted = true;
            }

            if (backupDeleted && backup.Path.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
            {
                var metadataPath = backup.Path + ".meta";
                try
                {
                    var metaExists = await storage.ExistsAsync(metadataPath).ConfigureAwait(false);
                    if (metaExists)
                    {
                        await storage.DeleteAsync(metadataPath).ConfigureAwait(false);
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

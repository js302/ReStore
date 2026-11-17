using ReStore.Core.src.backup;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

/// <summary>Thrown when repair refuses to touch storage because what it saw cannot be trusted (nothing is deleted).</summary>
public sealed class RepositoryRepairAbortedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

public sealed class RepairOptions
{
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromHours(24);

    /// <summary>Report what would be deleted and change nothing (no deletes, no state changes).</summary>
    public bool DryRun { get; init; }

    /// <summary>Unreferenced chunks younger than this are kept (they may belong to a backup still in flight, possibly on another machine).</summary>
    public TimeSpan GracePeriod { get; init; } = DefaultGracePeriod;

    /// <summary>Clock override for tests.</summary>
    public Func<DateTime>? UtcNow { get; init; }
}

public sealed class RepairReport
{
    public bool DryRun { get; init; }
    public int ManifestsScanned { get; set; }
    public int ChunksListed { get; set; }
    public int ChunksReferenced { get; set; }

    /// <summary>Unreferenced chunks past the grace window (deleted, or in a dry run would be).</summary>
    public int OrphanChunks { get; set; }
    public long OrphanBytes { get; set; }
    public int ChunksDeleted { get; set; }
    public int ChunksDeleteFailed { get; set; }
    public long BytesFreed { get; set; }

    /// <summary>Unreferenced chunks kept because they are inside the grace window or their age is unknown.</summary>
    public int ChunksKeptInGrace { get; set; }

    public int PendingDeletesCompleted { get; set; }
    public int OrphanSnapshotsAdopted { get; set; }
    public bool RefCountsRebuilt { get; set; }
    public List<string> OrphanChunkPaths { get; } = [];
    public List<string> Messages { get; } = [];
}

/// <summary>
/// Mark-and-sweep garbage collection of one storage: marks every chunk referenced by any manifest under
/// <c>snapshots/</c> (every group, every machine), deletes unmarked chunks older than a grace window, rebuilds the
/// local reference counts and re-adopts snapshots that exist in storage but not in the local history. Runs under the
/// exclusive delete lease and aborts, deleting nothing, if listing is unsupported or fails or any manifest cannot be loaded.
/// </summary>
public sealed class RepositoryRepair(
    ILogger logger,
    IConfigManager config,
    SystemState state,
    RepositoryCoordinator? coordinator = null,
    IPasswordProvider? passwordProvider = null)
{
    private const string ChunksPrefix = "chunks/";
    private const string ChunkSuffix = ".chunk";

    private readonly RepositoryCoordinator _coordinator = coordinator ?? RepositoryCoordinator.Instance;

    /// <summary>Takes the mutation and delete leases itself (a dry run only needs the read lease); do not call while holding one.</summary>
    public async Task<RepairReport> RunAsync(
        IStorage storage,
        string storageType,
        RepairOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RepairOptions();
        RepositoryDiscovery.RequireListing(storage);

        if (options.DryRun)
        {
            var read = await _coordinator.AcquireReadAsync("Repair preview", cancellationToken).ConfigureAwait(false);
            await using var readScope = read.ConfigureAwait(false);
            return await RunCoreAsync(storage, storageType, options, progress, cancellationToken).ConfigureAwait(false);
        }

        var mutation = await _coordinator.AcquireMutationAsync("Repairing storage", cancellationToken, state).ConfigureAwait(false);
        await using var mutationScope = mutation.ConfigureAwait(false);
        var delete = await _coordinator.AcquireDeleteAsync("Repairing storage", cancellationToken).ConfigureAwait(false);
        await using var deleteScope = delete.ConfigureAwait(false);
        using var activity = ActivityRecorder.Begin(state, ActivityType.Repair, storageType, cancellationToken);
        var report = await RunCoreAsync(storage, storageType, options, progress, cancellationToken).ConfigureAwait(false);
        var summary = $"{report.ChunksDeleted} orphan chunk(s) deleted, {report.OrphanSnapshotsAdopted} snapshot(s) adopted";
        if (report.ChunksDeleteFailed > 0)
        {
            activity.Warning($"{summary}; {report.ChunksDeleteFailed} delete(s) failed");
        }
        else
        {
            activity.Succeeded(summary);
        }

        return report;
    }

    private async Task<RepairReport> RunCoreAsync(
        IStorage storage,
        string storageType,
        RepairOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var report = new RepairReport { DryRun = options.DryRun };
        var now = (options.UtcNow ?? (() => DateTime.UtcNow))();
        var cutoff = now - options.GracePeriod;

        // Phase 1 (mark): every manifest under snapshots/, checked against HEAD. Any failure aborts before anything is deleted.
        progress?.Report("Reading snapshot manifests...");
        // A missing or damaged primary key file is restored from its mirror (never in a dry run).
        try
        {
            await RepositoryKeyStore.ReadAsync(storage, cancellationToken, repair: !options.DryRun).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new RepositoryRepairAbortedException($"{ex.Message} Nothing was deleted.", ex);
        }

        var mark = await MarkAsync(storage, storageType, options.DryRun, cancellationToken).ConfigureAwait(false);
        var folders = mark.Folders;
        var loadedManifests = mark.Manifests;
        var marked = mark.Marked;
        report.ManifestsScanned = loadedManifests.Count;
        report.ChunksReferenced = marked.Count;

        // Phase 2 (sweep): list chunks/ completely, then delete unreferenced ones older than the grace window.
        progress?.Report("Listing chunks...");
        var orphans = new List<StorageEntry>();
        try
        {
            await foreach (var entry in storage.ListAsync(ChunksPrefix, cancellationToken).ConfigureAwait(false))
            {
                if (!entry.Path.EndsWith(ChunkSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                report.ChunksListed++;
                if (marked.Contains(entry.Path))
                {
                    continue;
                }

                if (entry.LastModifiedUtc is not { } modified || modified.ToUniversalTime() > cutoff)
                {
                    report.ChunksKeptInGrace++;
                    continue;
                }

                orphans.Add(entry);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RepositoryRepairAbortedException($"Listing the chunks failed, so nothing was deleted: {ex.Message}", ex);
        }

        report.OrphanChunks = orphans.Count;
        report.OrphanBytes = orphans.Sum(entry => entry.Size);
        report.OrphanChunkPaths.AddRange(orphans.Select(entry => entry.Path));

        if (options.DryRun)
        {
            return report;
        }

        // Another machine may have committed a snapshot since the first read (a backup that deduped against an old
        // unreferenced chunk is not protected by the grace window). Re-read everything right before deleting and keep
        // whatever is referenced now.
        progress?.Report("Re-checking snapshots before deleting...");
        mark = await MarkAsync(storage, storageType, options.DryRun, cancellationToken).ConfigureAwait(false);
        folders = mark.Folders;
        loadedManifests = mark.Manifests;
        marked = mark.Marked;
        report.ManifestsScanned = loadedManifests.Count;
        report.ChunksReferenced = marked.Count;
        var stillOrphans = orphans.Where(entry => !marked.Contains(entry.Path)).ToList();
        report.ChunksKeptInGrace += orphans.Count - stillOrphans.Count;
        orphans = stillOrphans;
        report.OrphanChunks = orphans.Count;
        report.OrphanBytes = orphans.Sum(entry => entry.Size);
        report.OrphanChunkPaths.Clear();
        report.OrphanChunkPaths.AddRange(orphans.Select(entry => entry.Path));

        // Retry deletes that failed earlier, never touching anything that is marked.
        progress?.Report("Retrying pending deletes...");
        report.PendingDeletesCompleted = await new RetentionManager(logger, config, state, _coordinator, passwordProvider)
            .RetryPendingChunkDeletesAsync(cancellationToken, marked).ConfigureAwait(false);

        var deleteIndex = 0;
        foreach (var orphan in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Deleting orphan chunks ({++deleteIndex}/{orphans.Count})...");
            try
            {
                await storage.DeleteAsync(orphan.Path, cancellationToken).ConfigureAwait(false);
                report.ChunksDeleted++;
                report.BytesFreed += orphan.Size;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.ChunksDeleteFailed++;
                report.Messages.Add($"Could not delete {orphan.Path}: {ex.Message}");
                logger.Log($"Repair: could not delete orphan chunk {orphan.Path}: {ex.Message}", LogLevel.Warning);
            }
        }

        // Phase 3: re-adopt snapshots this installation made but lost from its history (a crash between HEAD and the
        // state save). Other installations' groups are left to an explicit adopt.
        progress?.Report("Rebuilding local history...");
        var installId = state.GetOrCreateInstallId();
        var localGroups = new HashSet<string>(state.GetBackupGroups(), StringComparer.OrdinalIgnoreCase);
        var adopter = new RepositoryAdopter(logger, config, state, _coordinator, passwordProvider);
        foreach (var folder in folders)
        {
            var folderManifests = loadedManifests.Where(m => m.ManifestPath.StartsWith($"snapshots/{folder.Key}/", StringComparison.Ordinal)).ToList();
            if (folderManifests.Count == 0)
            {
                continue;
            }

            var groupPath = folderManifests.OrderByDescending(m => m.Manifest.CreatedUtc).First().Manifest.Group;
            var ours = localGroups.Contains(groupPath);
            if (!ours && folder.Value.IndexPath != null)
            {
                var read = await new GroupIndexStore(logger).ReadAsync(storage, folder.Value.IndexPath, null, cancellationToken).ConfigureAwait(false);
                ours = read.Index?.InstallId == installId;
            }

            if (!ours)
            {
                continue;
            }

            foreach (var loaded in folderManifests)
            {
                if (adopter.AdoptManifest(groupPath, storageType, loaded))
                {
                    report.OrphanSnapshotsAdopted++;
                    logger.Log($"Repair: re-adopted snapshot {loaded.Manifest.SnapshotId}.", LogLevel.Info);
                }
            }
        }

        // Persist the adopted entries (writing their chunk lists), then rebuild every reference count from the lists.
        await state.SaveStateAsync().ConfigureAwait(false);
        var recovered = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var byPath = loadedManifests.ToDictionary(m => m.ManifestPath, StringComparer.OrdinalIgnoreCase);
        foreach (var backup in state.GetSnapshotsWithoutChunkLists().Where(b => StorageTypeMatches(b, storageType)))
        {
            var manifestPath = string.IsNullOrWhiteSpace(backup.ManifestPath) ? backup.Path : backup.ManifestPath;
            if (byPath.TryGetValue(manifestPath, out var loaded))
            {
                recovered[backup.Path] = [.. loaded.Manifest.Files.SelectMany(file => file.Chunks).Select(chunk => chunk.ChunkId)];
            }
            else
            {
                // A snapshot recorded locally whose manifest is gone from storage has no chunks to protect.
                recovered[backup.Path] = [];
            }
        }

        report.RefCountsRebuilt = await state.RepairChunkReferenceCountsAsync(recovered).ConfigureAwait(false);
        if (!report.RefCountsRebuilt)
        {
            report.Messages.Add("Some snapshots stored elsewhere have no chunk list, so reference counts stay flagged incomplete and retention will not delete chunks until they are repaired against that storage.");
        }

        await state.SaveStateAsync().ConfigureAwait(false);
        logger.Log(
            $"Repair finished: {report.ChunksDeleted} orphan chunk(s) deleted ({report.BytesFreed} bytes), {report.ChunksKeptInGrace} kept in grace, {report.OrphanSnapshotsAdopted} snapshot(s) re-adopted.",
            LogLevel.Info);
        return report;
    }

    private sealed record MarkResult(
        Dictionary<string, RepositoryDiscovery.FolderListing> Folders,
        List<LoadedStorageManifest> Manifests,
        HashSet<string> Marked);

    private async Task<MarkResult> MarkAsync(IStorage storage, string storageType, bool dryRun, CancellationToken cancellationToken)
    {
        Dictionary<string, RepositoryDiscovery.FolderListing> folders;
        try
        {
            folders = await RepositoryDiscovery.ListSnapshotsAsync(storage, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RepositoryRepairAbortedException($"Listing the snapshots failed, so nothing was deleted: {ex.Message}", ex);
        }

        // HEAD is what a restore follows: it must name a manifest that is listed and loads, or the listing is not trustworthy.
        foreach (var folder in folders)
        {
            if (folder.Value.HeadPath == null)
            {
                continue;
            }

            var target = await RepositoryDiscovery.TryReadHeadTargetAsync(storage, folder.Value.HeadPath, cancellationToken).ConfigureAwait(false);
            if (target == null || !folder.Value.ManifestPaths.Contains(target, StringComparer.Ordinal))
            {
                throw new RepositoryRepairAbortedException(
                    $"HEAD '{folder.Value.HeadPath}' {(target == null ? "could not be read" : $"names '{target}', which is not among the listed manifests")}, so nothing was deleted.");
            }
        }

        // Encrypted v3 manifests can only be marked with the repository password; if one cannot be decrypted the run aborts below.
        using var manifestReader = new RepositoryManifestReader(logger, storage, passwordProvider, repairKeyFile: !dryRun);
        var marked = new HashSet<string>(StringComparer.Ordinal);
        var loadedManifests = new List<LoadedStorageManifest>();
        foreach (var manifestPath in folders.Values.SelectMany(folder => folder.ManifestPaths).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedStorageManifest loaded;
            try
            {
                loaded = await RepositoryDiscovery.LoadManifestAsync(storage, manifestPath, cancellationToken, manifestReader).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RepositoryRepairAbortedException(
                    $"The manifest '{manifestPath}' could not be loaded ({ex.Message}). Its chunks cannot be told from garbage, so nothing was deleted.", ex);
            }

            loadedManifests.Add(loaded);
            foreach (var chunk in loaded.Manifest.Files.SelectMany(file => file.Chunks))
            {
                try
                {
                    marked.Add(SnapshotStoragePaths.GetChunkPath(chunk.ChunkId, loaded.Manifest.ChunkStorageNamespace));
                }
                catch (ArgumentException ex)
                {
                    throw new RepositoryRepairAbortedException($"The manifest '{manifestPath}' names an invalid chunk ({ex.Message}); nothing was deleted.", ex);
                }
            }
        }

        // Also keep what the local history says this storage uses, in case a manifest is not visible in the listing yet.
        var localSnapshots = state.GetBackupGroups()
            .SelectMany(group => state.GetBackupsForGroup(group))
            .Where(backup => backup.ArtifactType == BackupArtifactType.SnapshotManifest && StorageTypeMatches(backup, storageType))
            .ToList();
        foreach (var backup in localSnapshots)
        {
            var ids = await state.TryGetSnapshotChunkIdsAsync(backup).ConfigureAwait(false);
            foreach (var id in ids ?? [])
            {
                try
                {
                    marked.Add(SnapshotStoragePaths.GetChunkPath(id, backup.ChunkStorageNamespace));
                }
                catch (ArgumentException)
                {
                    // An unusable id cannot name a chunk.
                }
            }
        }

        if (loadedManifests.Count == 0 && localSnapshots.Count > 0)
        {
            throw new RepositoryRepairAbortedException(
                $"No snapshot manifests were found in storage although {localSnapshots.Count} snapshot(s) are recorded locally. " +
                "The storage prefix or credentials may be wrong, so nothing was deleted.");
        }

        return new MarkResult(folders, loadedManifests, marked);
    }

    private bool StorageTypeMatches(BackupInfo backup, string storageType)
    {
        var effective = string.IsNullOrWhiteSpace(backup.StorageType) ? config.GlobalStorageType : backup.StorageType;
        return string.Equals(effective, storageType, StringComparison.OrdinalIgnoreCase);
    }
}

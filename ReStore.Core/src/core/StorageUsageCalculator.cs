using ReStore.Core.src.backup;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

/// <summary>Stored and plain size of the unique chunks one snapshot references, taken from its manifest.</summary>
public readonly record struct SnapshotChunkUsage(long StoredBytes, long PlainBytes)
{
    public static SnapshotChunkUsage FromManifest(SnapshotManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        long stored = 0, plain = 0;
        foreach (var chunk in manifest.Files.SelectMany(file => file.Chunks).GroupBy(c => c.ChunkId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            stored += chunk.StoredSizeBytes;
            plain += chunk.PlainSizeBytes;
        }

        return new SnapshotChunkUsage(stored, plain);
    }
}

/// <summary>Stored bytes a snapshot added (chunks no earlier snapshot in the same chunk store referenced), for the growth sparkline.</summary>
public sealed record StorageGrowthPoint(DateTime TimestampUtc, long AddedStoredBytes, long CumulativeStoredBytes);

/// <summary>Usage of one (group, storage) pair.</summary>
public sealed class GroupStorageUsage
{
    public string Group { get; init; } = "";
    public string StorageType { get; init; } = "";
    public int SnapshotCount { get; init; }

    /// <summary>Size of the files in the newest snapshot.</summary>
    public long LatestLogicalBytes { get; init; }

    /// <summary>Sum of the file sizes of every snapshot (what keeping them all uncompressed and undeduplicated would take).</summary>
    public long AllSnapshotsLogicalBytes { get; init; }

    /// <summary>Stored bytes of the unique chunks the group's snapshots reference (chunks shared with another group count in both).</summary>
    public long StoredBytes { get; init; }

    /// <summary>Uncompressed size of the same chunks.</summary>
    public long PlainBytes { get; init; }

    /// <summary>Stored bytes snapshots would take if each kept its own copy of its chunks, minus <see cref="StoredBytes"/>.</summary>
    public long DedupSavedBytes { get; init; }

    /// <summary><see cref="PlainBytes"/> minus <see cref="StoredBytes"/>, never negative (encryption overhead is not a saving).</summary>
    public long CompressionSavedBytes => Math.Max(0, PlainBytes - StoredBytes);

    /// <summary>Stored bytes freed if the retention policy ran now (chunks only referenced by snapshots it would delete).</summary>
    public long ReclaimableBytes { get; init; }

    public int ReclaimableSnapshots { get; init; }

    /// <summary>True when some snapshot has no recorded chunk sizes (made before they were stored) so its numbers are approximated.</summary>
    public bool HasEstimates { get; init; }

    public IReadOnlyList<StorageGrowthPoint> Growth { get; init; } = [];
}

public sealed class StorageUsageReport
{
    public IReadOnlyList<GroupStorageUsage> Groups { get; init; } = [];

    /// <summary>Stored bytes of every distinct chunk across all groups (shared chunks counted once).</summary>
    public long TotalStoredBytes { get; init; }
    public long TotalPlainBytes { get; init; }
    public long TotalLatestLogicalBytes { get; init; }
    public long TotalAllSnapshotsLogicalBytes { get; init; }
    public long TotalDedupSavedBytes { get; init; }
    public long TotalCompressionSavedBytes => Math.Max(0, TotalPlainBytes - TotalStoredBytes);
    public long TotalReclaimableBytes { get; init; }
    public int TotalReclaimableSnapshots { get; init; }
    public int TotalSnapshots { get; init; }
    public bool HasEstimates { get; init; }

    /// <summary>Whether the retention policy used for the estimate is switched on (an estimate is still given when it is off).</summary>
    public bool RetentionEnabled { get; init; }

    /// <summary>Snapshots whose chunk list could not be read, so they are missing from the stored-size figures.</summary>
    public int SnapshotsWithoutChunkList { get; init; }

    public IReadOnlyList<StorageGrowthPoint> Growth { get; init; } = [];
}

/// <summary>What a listing of <c>chunks/</c> found in one storage.</summary>
public sealed record MeasuredStorage(long ChunkCount, long Bytes);

/// <summary>
/// Works out how much space snapshots take, from local state only: <see cref="BackupInfo.StoredBytes"/>, the chunk-id
/// sidecars and the history. A chunk's size is the per-chunk average of the oldest snapshot that references it (the
/// manifest holds the exact sizes but lives in storage), so figures are close, not exact; <see cref="MeasureAsync"/>
/// gives the exact total for a storage by listing it. Chunks are shared per (storage type, chunk namespace), so
/// snapshots of different groups in one namespace count each chunk once.
/// </summary>
public static class StorageUsageCalculator
{
    private sealed record ChunkSize(long Stored, long Plain);

    private sealed class SnapshotInfo
    {
        public required string Group { get; init; }
        public required BackupInfo Backup { get; init; }
        public required string Scope { get; init; }
        public required string Storage { get; init; }
        public IReadOnlyList<string>? ChunkIds { get; set; }
        public long Added { get; set; }
        public bool Estimated { get; set; }
    }

    public static async Task<StorageUsageReport> ComputeAsync(
        SystemState state,
        RetentionConfig? policy,
        string defaultStorageType,
        DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var history = state.GetBackupHistorySnapshot();
        var snapshots = new List<SnapshotInfo>();
        foreach (var (group, backups) in history)
        {
            foreach (var backup in backups)
            {
                snapshots.Add(new SnapshotInfo { Group = group, Backup = backup, Scope = ScopeOf(backup, defaultStorageType), Storage = StorageOf(backup, defaultStorageType) });
            }
        }

        foreach (var snapshot in snapshots.Where(s => s.Backup.ArtifactType == BackupArtifactType.SnapshotManifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot.ChunkIds = await state.TryGetSnapshotChunkIdsAsync(snapshot.Backup).ConfigureAwait(false);
        }

        // Oldest first within each chunk store, so a chunk is sized by the snapshot that introduced it.
        var sizesByScope = new Dictionary<string, Dictionary<string, ChunkSize>>(StringComparer.Ordinal);
        foreach (var scopeGroup in snapshots.GroupBy(s => s.Scope, StringComparer.Ordinal))
        {
            var sizes = new Dictionary<string, ChunkSize>(StringComparer.OrdinalIgnoreCase);
            sizesByScope[scopeGroup.Key] = sizes;
            foreach (var snapshot in scopeGroup.OrderBy(s => s.Backup.Timestamp))
            {
                SizeSnapshotChunks(snapshot, sizes);
            }
        }

        var withoutList = snapshots.Count(s => s.Backup.ArtifactType == BackupArtifactType.SnapshotManifest && s.ChunkIds == null);

        // What retention would delete: per group, exactly as RetentionManager plans it.
        var toDelete = new HashSet<BackupInfo>(ReferenceEqualityComparer.Instance);
        if (policy != null)
        {
            foreach (var groupSnapshots in snapshots.GroupBy(s => s.Group, StringComparer.OrdinalIgnoreCase))
            {
                var plan = RetentionPlanner.Plan(groupSnapshots.Select(s => s.Backup), policy, nowUtc, defaultStorageType);
                foreach (var doomed in plan.ToDelete)
                {
                    toDelete.Add(doomed);
                }
            }
        }

        // Per chunk store: chunks that only deleted snapshots reference.
        var reclaimableByScope = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var scopeGroup in snapshots.GroupBy(s => s.Scope, StringComparer.Ordinal))
        {
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var doomed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in scopeGroup.Where(s => s.ChunkIds != null))
            {
                (toDelete.Contains(snapshot.Backup) ? doomed : kept).UnionWith(snapshot.ChunkIds!);
            }

            doomed.ExceptWith(kept);
            reclaimableByScope[scopeGroup.Key] = doomed;
        }

        var rows = new List<GroupStorageUsage>();
        foreach (var row in snapshots.GroupBy(s => (Group: s.Group.ToLowerInvariant(), Storage: s.Storage.ToLowerInvariant())))
        {
            var members = row.OrderBy(s => s.Backup.Timestamp).ToList();
            rows.Add(BuildRow(members, sizesByScope, reclaimableByScope, toDelete));
        }

        rows = [.. rows.OrderBy(r => r.Group, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.StorageType, StringComparer.OrdinalIgnoreCase)];

        var totalStored = sizesByScope.Values.Sum(sizes => sizes.Values.Sum(c => c.Stored))
            + snapshots.Where(s => s.Backup.ArtifactType == BackupArtifactType.Archive).Sum(s => Math.Max(0, s.Backup.SizeBytes));
        var totalPlain = sizesByScope.Values.Sum(sizes => sizes.Values.Sum(c => c.Plain))
            + snapshots.Where(s => s.Backup.ArtifactType == BackupArtifactType.Archive).Sum(s => Math.Max(0, s.Backup.SizeBytes));
        var reclaimable = reclaimableByScope.Sum(kv => kv.Value.Sum(id => sizesByScope[kv.Key].GetValueOrDefault(id)?.Stored ?? 0))
            + snapshots.Where(s => s.Backup.ArtifactType == BackupArtifactType.Archive && toDelete.Contains(s.Backup)).Sum(s => Math.Max(0, s.Backup.SizeBytes));

        var growth = BuildGrowth(snapshots.OrderBy(s => s.Backup.Timestamp));
        var latestLogical = rows.Sum(r => r.LatestLogicalBytes);

        return new StorageUsageReport
        {
            Groups = rows,
            TotalStoredBytes = totalStored,
            TotalPlainBytes = totalPlain,
            TotalLatestLogicalBytes = latestLogical,
            TotalAllSnapshotsLogicalBytes = rows.Sum(r => r.AllSnapshotsLogicalBytes),
            TotalDedupSavedBytes = Math.Max(0, snapshots.Sum(s => SnapshotOwnStored(s)) - totalStored),
            TotalReclaimableBytes = reclaimable,
            TotalReclaimableSnapshots = toDelete.Count,
            TotalSnapshots = snapshots.Count,
            HasEstimates = snapshots.Any(s => s.Estimated),
            RetentionEnabled = policy?.Enabled ?? false,
            SnapshotsWithoutChunkList = withoutList,
            Growth = growth
        };
    }

    /// <summary>Lists <c>chunks/</c> to get the real stored size (orphans included). Caller holds a read lease.</summary>
    public static async Task<MeasuredStorage> MeasureAsync(IStorage storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        long count = 0, bytes = 0;
        await foreach (var entry in storage.ListAsync("chunks/", cancellationToken).ConfigureAwait(false))
        {
            if (!entry.Path.EndsWith(".chunk", StringComparison.Ordinal))
            {
                continue;
            }

            count++;
            bytes += Math.Max(0, entry.Size);
        }

        return new MeasuredStorage(count, bytes);
    }

    private static string StorageOf(BackupInfo backup, string defaultStorageType)
        => string.IsNullOrWhiteSpace(backup.StorageType) ? defaultStorageType : backup.StorageType.Trim();

    private static string ScopeOf(BackupInfo backup, string defaultStorageType)
        => StorageOf(backup, defaultStorageType).ToLowerInvariant() + "|" + (backup.ChunkStorageNamespace ?? "").Trim().ToLowerInvariant();

    private static void SizeSnapshotChunks(SnapshotInfo snapshot, Dictionary<string, ChunkSize> sizes)
    {
        var backup = snapshot.Backup;
        if (snapshot.ChunkIds == null || snapshot.ChunkIds.Count == 0)
        {
            return;
        }

        var count = snapshot.ChunkIds.Count;
        long stored = backup.StoredBytes, plain = backup.PlainBytes;
        if (stored <= 0)
        {
            // No recorded sizes: the file size is the best guess for both.
            snapshot.Estimated = true;
            stored = plain = Math.Max(0, backup.SizeBytes);
        }
        else if (plain <= 0)
        {
            plain = stored;
        }

        var storedEach = stored / count;
        var plainEach = plain / count;
        foreach (var id in snapshot.ChunkIds)
        {
            if (sizes.TryAdd(id, new ChunkSize(storedEach, plainEach)))
            {
                snapshot.Added += storedEach;
            }
        }
    }

    // What this snapshot would take if it kept its own copy of every chunk.
    private static long SnapshotOwnStored(SnapshotInfo snapshot)
    {
        if (snapshot.Backup.ArtifactType == BackupArtifactType.Archive)
        {
            return Math.Max(0, snapshot.Backup.SizeBytes);
        }

        // A snapshot whose chunk list is unknown contributes no chunks, so it must not count toward dedup either.
        if (snapshot.ChunkIds is not { Count: > 0 })
        {
            return 0;
        }

        return snapshot.Backup.StoredBytes > 0 ? snapshot.Backup.StoredBytes : Math.Max(0, snapshot.Backup.SizeBytes);
    }

    private static GroupStorageUsage BuildRow(
        List<SnapshotInfo> members,
        Dictionary<string, Dictionary<string, ChunkSize>> sizesByScope,
        Dictionary<string, HashSet<string>> reclaimableByScope,
        HashSet<BackupInfo> toDelete)
    {
        var first = members[0];
        var chunkIds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var member in members.Where(m => m.ChunkIds != null))
        {
            if (!chunkIds.TryGetValue(member.Scope, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                chunkIds[member.Scope] = set;
            }

            set.UnionWith(member.ChunkIds!);
        }

        long stored = 0, plain = 0, reclaimable = 0;
        foreach (var (scope, ids) in chunkIds)
        {
            var sizes = sizesByScope[scope];
            var reclaim = reclaimableByScope[scope];
            foreach (var id in ids)
            {
                if (sizes.TryGetValue(id, out var size))
                {
                    stored += size.Stored;
                    plain += size.Plain;
                    if (reclaim.Contains(id))
                    {
                        reclaimable += size.Stored;
                    }
                }
            }
        }

        foreach (var archive in members.Where(m => m.Backup.ArtifactType == BackupArtifactType.Archive))
        {
            var size = Math.Max(0, archive.Backup.SizeBytes);
            stored += size;
            plain += size;
            if (toDelete.Contains(archive.Backup))
            {
                reclaimable += size;
            }
        }

        var own = members.Sum(SnapshotOwnStored);
        return new GroupStorageUsage
        {
            Group = first.Group,
            StorageType = first.Storage,
            SnapshotCount = members.Count,
            LatestLogicalBytes = Math.Max(0, members[^1].Backup.SizeBytes),
            AllSnapshotsLogicalBytes = members.Sum(m => Math.Max(0, m.Backup.SizeBytes)),
            StoredBytes = stored,
            PlainBytes = plain,
            DedupSavedBytes = Math.Max(0, own - stored),
            ReclaimableBytes = reclaimable,
            ReclaimableSnapshots = members.Count(m => toDelete.Contains(m.Backup)),
            HasEstimates = members.Any(m => m.Estimated),
            Growth = BuildGrowth(members)
        };
    }

    private static List<StorageGrowthPoint> BuildGrowth(IEnumerable<SnapshotInfo> ordered)
    {
        var points = new List<StorageGrowthPoint>();
        long cumulative = 0;
        foreach (var snapshot in ordered)
        {
            var added = snapshot.Backup.ArtifactType == BackupArtifactType.Archive ? Math.Max(0, snapshot.Backup.SizeBytes) : snapshot.Added;
            cumulative += added;
            points.Add(new StorageGrowthPoint(snapshot.Backup.Timestamp.ToUniversalTime(), added, cumulative));
        }

        return points;
    }
}

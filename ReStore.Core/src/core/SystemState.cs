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

    /// <summary>
    /// Creation time of the file when it was recorded. Together with size and <see cref="LastModified"/> it is the
    /// stat signature that lets an unchanged file be recognised without reading it. Null on state written before
    /// the stat cache existed, which makes the file be read (and its signature recorded) once.
    /// </summary>
    public DateTime? CreatedUtc { get; set; }

    /// <summary>
    /// When the scan that recorded this entry started. A file whose <see cref="LastModified"/> lies within
    /// <see cref="ChangeScanContext.RacyWindow"/> of this moment is "racy": a later write could leave the
    /// timestamp unchanged, so it is always read. Null on state written before the stat cache existed.
    /// </summary>
    public DateTime? RecordedUtc { get; set; }
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

    /// <summary>
    /// Stored bytes of every unique chunk a snapshot referenced, summed across snapshots.
    /// This is what storage would have cost with no deduplication.
    /// </summary>
    public long ReferencedStoredBytes { get; set; }

    /// <summary>Stored bytes actually transferred, i.e. chunks not already in the provider.</summary>
    public long UploadedStoredBytes { get; set; }

    /// <summary>
    /// Exact bytes deduplication avoided transferring:
    /// <see cref="ReferencedStoredBytes"/> minus <see cref="UploadedStoredBytes"/>.
    /// </summary>
    public long DedupSavedBytes => Math.Max(0, ReferencedStoredBytes - UploadedStoredBytes);

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

    /// <summary>
    /// Read-only bridge for state files written by v1.6/v1.7, which kept one flat absolute-path dictionary. Never
    /// written (so v1.7 sees no metadata in a newer file, does a full pass, and does not fail); on load its entries
    /// are moved into <see cref="FileMetadataByGroup"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, FileMetadata>? FileMetadata { get; set; }

    /// <summary>Group directory, then storage type, then path relative to the group (forward slashes) to metadata.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, FileMetadata>>> FileMetadataByGroup { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ChunkReferenceCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>See <see cref="SystemState.ChunkReferenceCountsIncomplete"/>.</summary>
    public bool ChunkReferenceCountsIncomplete { get; set; }
    public SnapshotTelemetryAggregate Telemetry { get; set; } = new();

    /// <summary>Last verification time per snapshot path; drives the scheduler's rotation.</summary>
    public Dictionary<string, DateTime> SnapshotVerificationTimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When each (group, storage type) was last deep-scanned; see <see cref="SystemState.IsDeepScanDue"/>.</summary>
    public Dictionary<string, DateTime> DeepScanTimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>See <see cref="SystemState.GetOrCreateInstallId"/>. Additive: older versions ignore it.</summary>
    public string? InstallId { get; set; }

    /// <summary>See <see cref="SystemState.LastRetentionRunUtc"/>.</summary>
    public DateTime LastRetentionRunUtc { get; set; }

    /// <summary>See <see cref="SystemState.GetPendingChunkDeletes"/>.</summary>
    public List<PendingChunkDelete> PendingChunkDeletes { get; set; } = [];

    /// <summary>See <see cref="SystemState.GetRecentBackupRuns"/>. Additive: older versions ignore it.</summary>
    public List<BackupRunReport>? BackupRuns { get; set; }

    /// <summary>See <see cref="SystemState.GetLastSuccessfulRunUtc"/>. Additive: older versions ignore it.</summary>
    public Dictionary<string, DateTime>? GroupSuccessUtc { get; set; }

    /// <summary>See <see cref="SystemState.GetRecentActivity"/>. Additive: older versions ignore it.</summary>
    public List<ActivityEntry>? Activity { get; set; }
}

/// <summary>A chunk whose storage delete failed after its reference count was dropped; retried by the next retention or repair pass.</summary>
public class PendingChunkDelete
{
    public string StorageType { get; set; } = "";
    public string ChunkId { get; set; } = "";
    public string? ChunkStorageNamespace { get; set; }
    public DateTime QueuedUtc { get; set; }
}

public partial class SystemState
{
    public DateTime LastBackupTime { get; set; }
    public List<string> TrackedDirectories { get; set; } = [];
    public Dictionary<string, List<BackupInfo>> BackupHistory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ChunkReferenceCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SnapshotTelemetryAggregate Telemetry { get; set; } = new();

    // What was last backed up, per (group, storage type), keyed by path relative to the group. Keyed per storage
    // type so that backing the same folder up to a second destination starts from that destination's own record
    // rather than from "unchanged" (which would leave the new destination empty). Guarded by _stateLock.
    private Dictionary<string, Dictionary<string, Dictionary<string, FileMetadata>>> _fileMetadata = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the reference counts were rebuilt without the chunk list of some snapshot (its sidecar was
    /// missing), so some counts may be too low. While set, <see cref="UnregisterChunkReferences"/> reports nothing
    /// as unreferenced, so no chunk is ever deleted on the strength of an under-count. Persisted with the state;
    /// cleared by <see cref="RepairChunkReferenceCountsAsync"/>.
    /// </summary>
    public bool ChunkReferenceCountsIncomplete { get; private set; }

    /// <summary>
    /// Last verification time per snapshot path. Persisted so the scheduler's rotation over
    /// older snapshots survives a restart instead of always restarting at the newest.
    /// </summary>
    public Dictionary<string, DateTime> SnapshotVerificationTimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Last deep scan per "group|storageType" (see DeepScanKey). Guarded by _stateLock.
    private Dictionary<string, DateTime> _deepScanTimes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the scheduler last ran retention over every group (UTC; MinValue = never). Persisted so the
    /// once-a-day cadence survives restarts.
    /// </summary>
    public DateTime LastRetentionRunUtc { get; set; }

    private string? _installId;
    private readonly List<PendingChunkDelete> _pendingChunkDeletes = [];

    private string _stateFilePath = AppPaths.SystemStateFile;
    [JsonIgnore]
    private readonly ILogger? _logger;
    private readonly Lock _stateLock = new();

    private const string BackupSuffix = ".bak";
    private const int LoadRetryAttempts = 5;
    // Compact: the state file is rewritten after every backup, so indentation is pure overhead.
    // Old indented files still load (the reader ignores whitespace).
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = false };
    private const string ChunkListDirectoryName = "snapshots";
    private const string ChunkListExtension = ".chunks";
    private const string ChunkListFallbackExtension = ".chunklist";
    private const int ChunkIdByteLength = 32;
    // Sidecar keys of snapshots removed from history; their files are deleted once a save has persisted the removal.
    // Guarded by _stateLock.
    private readonly List<string> _pendingChunkListDeletes = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private volatile bool _loadFailed;

    // Length + last-write time of the state file as last loaded or saved by this instance;
    // null when the file did not exist. Guarded by _saveGate. Lets ReloadIfChangedAsync notice
    // another process (CLI vs GUI) having rewritten the file.
    // Only meaningful once a load or save has happened for the current path (_hasSignatureBaseline);
    // a never-loaded instance treats its in-memory state as authoritative and never reloads.
    private (long Length, DateTime LastWriteUtc)? _knownFileSignature;
    private bool _hasSignatureBaseline;

    // Chunks an in-flight backup has decided to reuse, keyed by owner token. Guarded by _stateLock.
    private readonly Dictionary<object, HashSet<string>> _chunkPins = new(ReferenceEqualityComparer.Instance);

    /// <summary>First retry delay after an IO error while loading; doubles each attempt (100 ms to 1.6 s).</summary>
    internal TimeSpan LoadRetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(100);

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
            _pendingChunkListDeletes.Clear();
            _loadFailed = false;
            _hasSignatureBaseline = false;
        }
        _logger?.Log($"System state file path set to: {_stateFilePath}", LogLevel.Debug);
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
                return DateTime.ParseExact(match.Groups[1].Value, "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            }

            // Fallback: try the last underscore-separated segment
            var parts = fileName.Split('_');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (parts[i].Length == 14 && long.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    return DateTime.ParseExact(parts[i], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
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
        string? chunkStorageNamespace = null,
        SnapshotChunkUsage? usage = null)
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
                ChunkListPending = true,
                RootHash = rootHash,
                Encrypted = encrypted,
                ChunkStorageNamespace = chunkStorageNamespace,
                StoredBytes = usage?.StoredBytes ?? 0,
                PlainBytes = usage?.PlainBytes ?? 0
            });

            RegisterChunkReferencesLocked(storageType, chunkStorageNamespace, normalizedChunkIds);
        }
    }

    /// <summary>
    /// Adds a snapshot discovered in storage (adopt / repair) to the history with its original timestamp.
    /// Idempotent: returns false, changing nothing, when an entry with the same manifest path and storage type
    /// already exists in any group. Registers its chunk references only when added. The caller saves.
    /// </summary>
    public virtual bool AdoptSnapshotBackup(
        string directory,
        string snapshotId,
        string manifestPath,
        string? storageType,
        DateTime createdUtc,
        IEnumerable<string> chunkIds,
        long sizeBytes,
        string? rootHash,
        bool encrypted,
        string? chunkStorageNamespace,
        SnapshotChunkUsage? usage = null)
    {
        var normalizedChunkIds = chunkIds
            .Where(chunkId => !string.IsNullOrWhiteSpace(chunkId))
            .Select(chunkId => chunkId.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_stateLock)
        {
            var normalizedStorage = string.IsNullOrWhiteSpace(storageType) ? null : storageType.Trim();
            var exists = BackupHistory.Values.SelectMany(backups => backups).Any(b =>
                string.Equals(b.Path, manifestPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(string.IsNullOrWhiteSpace(b.StorageType) ? null : b.StorageType.Trim(), normalizedStorage, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                return false;
            }

            if (!BackupHistory.TryGetValue(directory, out var list))
            {
                list = [];
                BackupHistory[directory] = list;
            }

            list.Add(new BackupInfo
            {
                Path = manifestPath,
                Timestamp = createdUtc.ToUniversalTime(),
                IsDiff = false,
                StorageType = normalizedStorage,
                SizeBytes = sizeBytes,
                ArtifactType = BackupArtifactType.SnapshotManifest,
                SnapshotId = snapshotId,
                ManifestPath = manifestPath,
                ChunkIds = normalizedChunkIds,
                ChunkListPending = true,
                RootHash = rootHash,
                Encrypted = encrypted,
                ChunkStorageNamespace = chunkStorageNamespace,
                StoredBytes = usage?.StoredBytes ?? 0,
                PlainBytes = usage?.PlainBytes ?? 0
            });

            RegisterChunkReferencesLocked(normalizedStorage, chunkStorageNamespace, normalizedChunkIds);
            return true;
        }
    }

    /// <summary>
    /// Stable random id of this ReStore installation, created on first use and persisted with the next save.
    /// Written into each group's <c>group.json</c> so a repository shows which installation wrote it.
    /// </summary>
    public virtual string GetOrCreateInstallId()
    {
        lock (_stateLock)
        {
            return _installId ??= Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>True while the chunk is referenced (reference count above zero) or pinned by an in-flight backup.</summary>
    public virtual bool IsChunkInUse(string? storageType, string? chunkStorageNamespace, string chunkId)
    {
        var normalized = chunkId.Trim().ToLowerInvariant();
        lock (_stateLock)
        {
            return IsChunkPinnedLocked(normalized)
                || ChunkReferenceCounts.ContainsKey(BuildChunkReferenceKey(storageType, chunkStorageNamespace, normalized));
        }
    }

    /// <summary>Most chunk deletes remembered; beyond this new ones are dropped (a repair finds such chunks as orphans).</summary>
    public const int MaxPendingChunkDeletes = 100_000;

    // Identity is storage type + namespace + chunk id (case-insensitive), for O(1) duplicate checks. Guarded by _stateLock; kept in step with _pendingChunkDeletes.
    private readonly HashSet<string> _pendingChunkDeleteKeys = new(StringComparer.OrdinalIgnoreCase);

    private static string PendingKey(string storageType, string? chunkStorageNamespace, string chunkId) =>
        $"{storageType}\u001f{chunkStorageNamespace ?? ""}\u001f{chunkId}";

    private void RebuildPendingChunkDeleteKeysLocked()
    {
        _pendingChunkDeleteKeys.Clear();
        foreach (var item in _pendingChunkDeletes)
        {
            _pendingChunkDeleteKeys.Add(PendingKey(item.StorageType, item.ChunkStorageNamespace, item.ChunkId));
        }
    }

    /// <summary>Remembers chunks whose storage delete failed so a later pass retries them. Duplicates are ignored. The caller saves.</summary>
    public virtual void QueuePendingChunkDeletes(string storageType, IEnumerable<string> chunkIds, string? chunkStorageNamespace)
    {
        lock (_stateLock)
        {
            foreach (var chunkId in chunkIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim().ToLowerInvariant()))
            {
                if (_pendingChunkDeletes.Count >= MaxPendingChunkDeletes)
                {
                    _logger?.Log($"Pending chunk delete queue is full ({MaxPendingChunkDeletes}); further chunks are left for a repair run.", LogLevel.Warning);
                    return;
                }

                if (!_pendingChunkDeleteKeys.Add(PendingKey(storageType, chunkStorageNamespace, chunkId)))
                {
                    continue;
                }

                _pendingChunkDeletes.Add(new PendingChunkDelete
                {
                    StorageType = storageType,
                    ChunkId = chunkId,
                    ChunkStorageNamespace = chunkStorageNamespace,
                    QueuedUtc = DateTime.UtcNow
                });
            }
        }
    }

    /// <summary>The queued chunk deletes (a copy), optionally only those of one storage type.</summary>
    public virtual List<PendingChunkDelete> GetPendingChunkDeletes(string? storageType = null)
    {
        lock (_stateLock)
        {
            return [.. _pendingChunkDeletes
                .Where(item => storageType == null || string.Equals(item.StorageType, storageType, StringComparison.OrdinalIgnoreCase))
                .Select(item => new PendingChunkDelete
                {
                    StorageType = item.StorageType,
                    ChunkId = item.ChunkId,
                    ChunkStorageNamespace = item.ChunkStorageNamespace,
                    QueuedUtc = item.QueuedUtc
                })];
        }
    }

    /// <summary>Removes the given items (matched by storage type, namespace and chunk id) from the queue. The caller saves.</summary>
    public virtual void RemovePendingChunkDeletes(IEnumerable<PendingChunkDelete> done)
    {
        var keys = done.Select(item => PendingKey(item.StorageType, item.ChunkStorageNamespace, item.ChunkId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0)
        {
            return;
        }

        lock (_stateLock)
        {
            _pendingChunkDeletes.RemoveAll(existing => keys.Contains(PendingKey(existing.StorageType, existing.ChunkStorageNamespace, existing.ChunkId)));
            RebuildPendingChunkDeleteKeysLocked();
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

    /// <summary>
    /// Marks chunks as in use by an in-flight operation: <see cref="UnregisterChunkReferences"/> will
    /// not report them as unreferenced until <see cref="UnpinChunks"/> is called with the same token.
    /// Pins are not persisted. Chunk ids are matched regardless of storage type and namespace
    /// (conservative: at worst an orphan is left for a later cleanup).
    /// </summary>
    public virtual void PinChunks(object token, IEnumerable<string> chunkIds)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(chunkIds);

        lock (_stateLock)
        {
            if (!_chunkPins.TryGetValue(token, out var pinned))
            {
                pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _chunkPins[token] = pinned;
            }

            foreach (var chunkId in chunkIds)
            {
                if (!string.IsNullOrWhiteSpace(chunkId))
                {
                    pinned.Add(chunkId.Trim().ToLowerInvariant());
                }
            }
        }
    }

    /// <summary>Releases every pin taken with <paramref name="token"/>. Unknown tokens are ignored.</summary>
    public virtual void UnpinChunks(object token)
    {
        ArgumentNullException.ThrowIfNull(token);

        lock (_stateLock)
        {
            _chunkPins.Remove(token);
        }
    }

    private bool IsChunkPinnedLocked(string normalizedChunkId)
    {
        foreach (var pinned in _chunkPins.Values)
        {
            if (pinned.Contains(normalizedChunkId))
            {
                return true;
            }
        }

        return false;
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
            if (ChunkReferenceCountsIncomplete)
            {
                _logger?.Log("Chunk reference counts are incomplete (a snapshot's chunk list is missing); keeping all chunks until they are repaired.", LogLevel.Warning);
                return unreferencedChunkIds;
            }

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

                    // An in-flight backup is about to reference this chunk (and will register it
                    // on commit), so it must not be reported as deletable.
                    if (!IsChunkPinnedLocked(chunkId))
                    {
                        unreferencedChunkIds.Add(chunkId);
                    }

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
        int candidateChunks,
        long referencedStoredBytes = 0,
        long uploadedStoredBytes = 0)
    {
        lock (_stateLock)
        {
            NormalizeTelemetryLocked();

            foreach (var backup in new[] { Telemetry.Backup, _pendingTelemetry.Backup })
            {
            backup.SnapshotCount++;
            backup.FileCount += fileCount;
            backup.ChunkReferences += chunkReferences;
            backup.UniqueChunks += uniqueChunks;
            backup.UploadedChunks += uploadedChunks;
            backup.UniqueReusedChunks += uniqueReusedChunks;
            backup.StorageHitChunks += storageHitChunks;
            backup.CandidateChunks += candidateChunks;
            backup.ReferencedStoredBytes += referencedStoredBytes;
            backup.UploadedStoredBytes += uploadedStoredBytes;
            backup.LastUpdatedUtc = DateTime.UtcNow;
            }
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

            foreach (var restore in new[] { Telemetry.Restore, _pendingTelemetry.Restore })
            {
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

            foreach (var verification in new[] { Telemetry.Verification, _pendingTelemetry.Verification })
            {
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
    }

    /// <summary>
    /// Tags a recorded backup so a restore point can be chosen by meaning rather than by
    /// timestamp. Returns false when the group or path is unknown.
    /// </summary>
    public virtual bool SetBackupLabel(string group, string backupPath, string? label)
    {
        if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(backupPath))
        {
            return false;
        }

        var normalizedLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();

        lock (_stateLock)
        {
            if (!BackupHistory.TryGetValue(group, out var backups))
            {
                return false;
            }

            var target = backups.FirstOrDefault(backup =>
                backup.Path.Equals(backupPath, StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                return false;
            }

            target.Label = normalizedLabel;
            return true;
        }
    }

    /// <summary>Records that a snapshot was verified, for the scheduler's rotation.</summary>
    public virtual void RecordSnapshotVerified(string snapshotPath, DateTime verifiedUtc)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            return;
        }

        lock (_stateLock)
        {
            SnapshotVerificationTimes[snapshotPath] = verifiedUtc;
            _pendingVerified[snapshotPath] = verifiedUtc;
        }
    }

    /// <summary>
    /// Last verification time for a snapshot, or <see cref="DateTime.MinValue"/> if it has
    /// never been verified — which sorts it first in the rotation.
    /// </summary>
    public virtual DateTime GetSnapshotVerifiedUtc(string snapshotPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            return DateTime.MinValue;
        }

        lock (_stateLock)
        {
            return SnapshotVerificationTimes.TryGetValue(snapshotPath, out var verifiedAt)
                ? verifiedAt
                : DateTime.MinValue;
        }
    }

    public virtual List<string> GetBackupGroups()
    {
        lock (_stateLock)
        {
            return [.. BackupHistory.Keys];
        }
    }

    /// <summary>
    /// Total recorded backups across every group. Exists so callers don't enumerate
    /// <see cref="BackupHistory"/> off-lock, which races with the scheduler and watcher
    /// recording new snapshots.
    /// </summary>
    public virtual int GetTotalBackupCount()
    {
        lock (_stateLock)
        {
            return BackupHistory.Values.Sum(backups => backups.Count);
        }
    }

    /// <summary>
    /// Newest snapshot-manifest backup for a group, or null when the group has none.
    /// Cheaper than <see cref="GetBackupsForGroup"/>, which deep-copies every entry's chunk
    /// id list — costly for a UI that polls.
    /// </summary>
    public virtual BackupInfo? GetLatestSnapshotForGroup(string group)
    {
        return GetLatestSnapshotForGroup(group, storageType: null, defaultStorageType: null);
    }

    /// <summary>
    /// Newest snapshot-manifest backup for a group on one storage type (any storage type when
    /// <paramref name="storageType"/> is null). Entries recorded without a storage type count as
    /// <paramref name="defaultStorageType"/>.
    /// </summary>
    public virtual BackupInfo? GetLatestSnapshotForGroup(string group, string? storageType, string? defaultStorageType)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return null;
        }

        lock (_stateLock)
        {
            if (!BackupHistory.TryGetValue(group, out var backups) || backups.Count == 0)
            {
                return null;
            }

            var latest = backups
                .Where(backup => backup.ArtifactType == BackupArtifactType.SnapshotManifest
                    && (storageType == null
                        || string.Equals(string.IsNullOrWhiteSpace(backup.StorageType) ? defaultStorageType : backup.StorageType, storageType, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(backup => backup.Timestamp)
                .FirstOrDefault();

            return latest == null ? null : CloneBackupInfo(latest);
        }
    }

    /// <summary>
    /// Point-in-time copy of the whole history, safe to enumerate on another thread.
    /// </summary>
    public virtual Dictionary<string, List<BackupInfo>> GetBackupHistorySnapshot()
    {
        lock (_stateLock)
        {
            return CloneBackupHistory(BackupHistory);
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
                .Select(CloneBackupInfo)];
        }
    }

    public virtual DateTime GetLastFullBackupTime(string group)
    {
        lock (_stateLock)
        {
            return GetLastFullBackupTimeLocked(group);
        }
    }

    /// <summary>
    /// Most recent backup of any kind for a group, or <see cref="DateTime.MinValue"/> if
    /// never backed up. Unlike <see cref="GetLastFullBackupTime"/> this ignores
    /// <c>IsDiff</c>: the scheduler cares when the group was last captured at all.
    /// </summary>
    public virtual DateTime GetLastBackupTimeForGroup(string group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return DateTime.MinValue;
        }

        lock (_stateLock)
        {
            if (!BackupHistory.TryGetValue(group, out var backups) || backups.Count == 0)
            {
                return DateTime.MinValue;
            }

            return backups.Max(backup => backup.Timestamp.ToUniversalTime());
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

            foreach (var removed in backups.Where(b => paths.Contains(b.Path)))
            {
                _pendingChunkListDeletes.Add(ChunkListKey(removed));
            }

            backups.RemoveAll(b => paths.Contains(b.Path));

            if (backups.Count == 0)
            {
                BackupHistory.Remove(group);

                // A group without history keeps no scheduling bookkeeping either.
                try
                {
                    var normalized = NormalizeGroup(group);
                    _groupSuccessUtc.Remove(normalized);
                    var prefix = normalized + "|";
                    foreach (var key in _deepScanTimes.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        _deepScanTimes.Remove(key);
                    }
                }
                catch (ArgumentException)
                {
                    // Not a path-like group key: nothing was recorded under it.
                }
            }

            // Otherwise the rotation bookkeeping grows without bound as retention prunes
            // snapshots, since nothing else ever removes these entries.
            foreach (var path in paths)
            {
                SnapshotVerificationTimes.Remove(path);
                _pendingVerified.Remove(path);
            }
        }
    }

    /// <summary>Canonical group key: the full path without a trailing separator (except for a drive root).</summary>
    private static string NormalizeGroup(string group)
    {
        var full = NormalizePath(group);
        return string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ToRelativeKey(string group, string absolutePath)
        => Path.GetRelativePath(group, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    private static string ToAbsolutePath(string group, string relativeKey)
        => Path.Combine(group, relativeKey.Replace('/', Path.DirectorySeparatorChar));

    private Dictionary<string, FileMetadata> GetOrAddFileMetadataBucketLocked(string group, string storageType)
    {
        if (!_fileMetadata.TryGetValue(group, out var byStorage))
        {
            byStorage = new(StringComparer.OrdinalIgnoreCase);
            _fileMetadata[group] = byStorage;
        }

        if (!byStorage.TryGetValue(storageType, out var bucket))
        {
            bucket = new(StringComparer.OrdinalIgnoreCase);
            byStorage[storageType] = bucket;
        }

        return bucket;
    }

    /// <summary>
    /// Records what a snapshot actually holds for each file: hash, size and modification time come from the
    /// manifest entries the chunker produced, never from re-reading the file, so a file that changed after it was
    /// read is seen as changed on the next run.
    /// </summary>
    public virtual void RecordFileMetadata(string group, string storageType, IEnumerable<SnapshotFileManifestEntry> entries)
        => RecordFileMetadata(group, storageType, entries, null, null);

    /// <summary>
    /// As <see cref="RecordFileMetadata(string, string, IEnumerable{SnapshotFileManifestEntry})"/>, also recording the
    /// stat signature that lets the next run skip an unchanged file: the creation time comes from the walk's record
    /// of the file and <paramref name="scanStartUtc"/> (the moment before anything was read) anchors the racy-timestamp
    /// rule. Without a scan record for a file only the legacy fields are stored and the next run reads it once.
    /// </summary>
    public virtual void RecordFileMetadata(
        string group,
        string storageType,
        IEnumerable<SnapshotFileManifestEntry> entries,
        DateTime? scanStartUtc,
        IEnumerable<ScannedFile>? scannedFiles)
    {
        group = NormalizeGroup(group);
        Dictionary<string, ScannedFile>? scannedByKey = null;
        if (scanStartUtc != null && scannedFiles != null)
        {
            scannedByKey = new(StringComparer.OrdinalIgnoreCase);
            foreach (var file in scannedFiles)
            {
                scannedByKey[ToRelativeKey(group, NormalizePath(file.Path))] = file;
            }
        }

        lock (_stateLock)
        {
            var bucket = GetOrAddFileMetadataBucketLocked(group, storageType);
            foreach (var entry in entries)
            {
                var metadata = new FileMetadata
                {
                    FilePath = entry.RelativePath,
                    Size = entry.SizeBytes,
                    LastModified = entry.LastModifiedUtc,
                    Hash = entry.ContentHash
                };

                if (scannedByKey != null && scannedByKey.TryGetValue(entry.RelativePath, out var scanned))
                {
                    metadata.CreatedUtc = scanned.CreationUtc;
                    metadata.RecordedUtc = scanStartUtc;
                }

                bucket[entry.RelativePath] = metadata;
            }
        }
    }

    private static string DeepScanKey(string normalizedGroup, string storageType) => normalizedGroup + "|" + storageType;

    /// <summary>
    /// True when (group, storage type) has not been deep-scanned within <paramref name="intervalDays"/> days (never,
    /// or the recorded time is in the future). An interval of zero or less means every run is a deep scan.
    /// </summary>
    public virtual bool IsDeepScanDue(string group, string storageType, int intervalDays, DateTime nowUtc)
    {
        if (intervalDays <= 0)
        {
            return true;
        }

        group = NormalizeGroup(group);
        lock (_stateLock)
        {
            if (!_deepScanTimes.TryGetValue(DeepScanKey(group, storageType), out var last))
            {
                return true;
            }

            var age = nowUtc - last;
            return age < TimeSpan.Zero || age >= TimeSpan.FromDays(intervalDays);
        }
    }

    /// <summary>Records that every file of (group, storage type) was read and compared at <paramref name="utc"/>.</summary>
    public virtual void RecordDeepScan(string group, string storageType, DateTime utc)
    {
        group = NormalizeGroup(group);
        lock (_stateLock)
        {
            _deepScanTimes[DeepScanKey(group, storageType)] = utc;
        }
    }

    /// <summary>Stores one metadata record for an absolute path under the group.</summary>
    public virtual void SetFileMetadata(string group, string storageType, string absolutePath, FileMetadata metadata)
    {
        group = NormalizeGroup(group);
        lock (_stateLock)
        {
            GetOrAddFileMetadataBucketLocked(group, storageType)[ToRelativeKey(group, NormalizePath(absolutePath))] = metadata;
        }
    }

    /// <summary>Forgets the metadata of files that are gone from the source (or no longer selected).</summary>
    public virtual void RemoveFileMetadata(string group, string storageType, IEnumerable<string> absolutePaths)
    {
        group = NormalizeGroup(group);
        lock (_stateLock)
        {
            if (!_fileMetadata.TryGetValue(group, out var byStorage) || !byStorage.TryGetValue(storageType, out var bucket))
            {
                return;
            }

            foreach (var path in absolutePaths)
            {
                if (bucket.Remove(ToRelativeKey(group, NormalizePath(path))))
                {
                    _logger?.Log($"Removed metadata for deleted file: {path}", LogLevel.Debug);
                }
            }
        }
    }

    /// <summary>Copy of the metadata recorded for (group, storage type), keyed by path relative to the group.</summary>
    public virtual IReadOnlyDictionary<string, FileMetadata> GetFileMetadata(string group, string storageType)
    {
        group = NormalizeGroup(group);
        lock (_stateLock)
        {
            return _fileMetadata.TryGetValue(group, out var byStorage) && byStorage.TryGetValue(storageType, out var bucket)
                ? new Dictionary<string, FileMetadata>(bucket, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public virtual bool TryGetFileMetadata(string group, string storageType, string absolutePath, out FileMetadata? metadata)
    {
        group = NormalizeGroup(group);
        lock (_stateLock)
        {
            if (_fileMetadata.TryGetValue(group, out var byStorage)
                && byStorage.TryGetValue(storageType, out var bucket)
                && bucket.TryGetValue(ToRelativeKey(group, NormalizePath(absolutePath)), out metadata))
            {
                return true;
            }
        }

        metadata = null;
        return false;
    }

    /// <summary>
    /// Writes the state atomically: serialise to a unique temp file in the same directory, flush it
    /// to disk, then swap it over the live file (keeping the previous one as <c>.bak</c>). Saves are
    /// serialised. Failures propagate: a swallowed save would let a backup report success while its
    /// snapshot is missing from the state. Refuses to run after a failed load so a real state file
    /// is never replaced by an empty one.
    /// </summary>
    public virtual async Task SaveStateAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_loadFailed)
            {
                throw new InvalidOperationException(
                    "System state could not be loaded, so it will not be saved: writing now would overwrite the " +
                    "existing state file with incomplete data. Resolve the problem reading the state file and restart.");
            }

            PersistentStateData stateData;
            string stateFilePath;
            List<(BackupInfo Live, List<string> Ids)> pendingChunkLists;
            List<string> pendingDeletes;
            PendingUnsaved? takenPending;

            lock (_stateLock)
            {
                takenPending = TakePendingUnsavedLocked();
                pendingChunkLists = [.. BackupHistory.Values
                    .SelectMany(backups => backups)
                    .Where(backup => backup.ChunkIds.Count > 0 || backup.ChunkListPending)
                    .Select(backup => (backup, new List<string>(backup.ChunkIds)))];
                pendingDeletes = [.. _pendingChunkListDeletes];

                stateData = new PersistentStateData
                {
                    LastBackupTime = LastBackupTime,
                    BackupHistory = CloneBackupHistory(BackupHistory),
                    FileMetadataByGroup = CloneFileMetadata(_fileMetadata),
                    ChunkReferenceCounts = CloneChunkReferenceCounts(ChunkReferenceCounts),
                    ChunkReferenceCountsIncomplete = ChunkReferenceCountsIncomplete,
                    Telemetry = CloneTelemetry(Telemetry),
                    SnapshotVerificationTimes = new Dictionary<string, DateTime>(SnapshotVerificationTimes, StringComparer.OrdinalIgnoreCase),
                    DeepScanTimes = new Dictionary<string, DateTime>(_deepScanTimes, StringComparer.OrdinalIgnoreCase),
                    InstallId = _installId,
                    LastRetentionRunUtc = LastRetentionRunUtc,
                    PendingChunkDeletes = [.. _pendingChunkDeletes.Select(item => new PendingChunkDelete
                    {
                        StorageType = item.StorageType,
                        ChunkId = item.ChunkId,
                        ChunkStorageNamespace = item.ChunkStorageNamespace,
                        QueuedUtc = item.QueuedUtc
                    })],
                    BackupRuns = CloneBackupRunsLocked(),
                    GroupSuccessUtc = CloneGroupSuccessLocked(),
                    Activity = CloneActivityLocked()
                };

                stateFilePath = _stateFilePath;
            }

            var tempPath = SharedFile.UniqueTempPath(stateFilePath);
            var writtenChunkLists = new List<string>();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath)!);

                // Chunk-id lists go to sidecars first: if the state save then fails, no committed entry
                // lacks its list, and the sidecars written by this attempt are removed again below.
                foreach (var (live, ids) in pendingChunkLists)
                {
                    var written = await WriteChunkListAsync(stateFilePath, live, ids).ConfigureAwait(false);
                    if (written != null)
                    {
                        writtenChunkLists.Add(written);
                    }
                }

                var stream = new FileStream(
                    tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                await using (stream.ConfigureAwait(false))
                {
                    await JsonSerializer.SerializeAsync(stream, stateData, SaveOptions).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(stateFilePath))
                {
                    await SharedFile.ReplaceAsync(tempPath, stateFilePath, stateFilePath + BackupSuffix).ConfigureAwait(false);
                }
                else
                {
                    File.Move(tempPath, stateFilePath);
                }

                RecordFileSignature(stateFilePath);
                _logger?.Log($"System state saved to {stateFilePath}", LogLevel.Info);

                // The lists are durable now: drop them from memory (only if untouched since they were captured),
                // and delete the sidecars of snapshots whose removal this save persisted.
                lock (_stateLock)
                {
                    foreach (var (live, ids) in pendingChunkLists)
                    {
                        if (live.ChunkIds.Count == ids.Count)
                        {
                            live.ChunkIds = [];
                            live.ChunkListPending = false;
                        }
                    }

                    _pendingChunkListDeletes.RemoveAll(pendingDeletes.Contains);

                    // Never delete the sidecar of a snapshot that is in the history again (e.g. resurrected by a reload).
                    var liveKeys = BackupHistory.Values.SelectMany(backups => backups).Select(ChunkListKey).ToHashSet(StringComparer.Ordinal);
                    pendingDeletes.RemoveAll(liveKeys.Contains);
                }

                foreach (var key in pendingDeletes)
                {
                    DeleteChunkListFiles(stateFilePath, key);
                }
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    RestorePendingUnsavedLocked(takenPending);
                }

                TryDeleteFile(tempPath);
                foreach (var written in writtenChunkLists)
                {
                    TryDeleteFile(written);
                }

                _logger?.Log($"Error saving system state to {stateFilePath}: {ex.Message}", LogLevel.Error);
                throw;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Loads the state file. A missing file gives an empty state. A file that cannot be parsed is kept
    /// as <c>.corrupt-&lt;utc&gt;</c> and the <c>.bak</c> copy is tried next; only if that is also unusable
    /// does it start empty. An IO failure (locked, access denied) is retried with backoff and then
    /// thrown, and blocks <see cref="SaveStateAsync"/> so the real file is never overwritten.
    /// </summary>
    public virtual async Task LoadStateAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            string stateFilePath;
            lock (_stateLock)
            {
                stateFilePath = _stateFilePath;
            }

            try
            {
                await LoadStateCoreAsync(stateFilePath).ConfigureAwait(false);
                lock (_stateLock)
                {
                    // A fresh load is authoritative; there is nothing "unsaved" relative to it.
                    _pendingActivity.Clear();
                    _pendingTelemetry = new SnapshotTelemetryAggregate();
                    _pendingVerified.Clear();
                }

                RecordFileSignature(stateFilePath);
                _loadFailed = false;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                _logger?.Log($"Error loading system state from {stateFilePath}: {ex.Message}. Saving is disabled to protect the existing file.", LogLevel.Error);
                throw;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Reloads the state file if it differs (length or last-write time) from what this instance last
    /// loaded or saved, so a process that takes the repository lock does not mutate and then save
    /// over another process's work. Also retries after a failed load. In-memory changes that were
    /// never saved are replaced when a reload happens, except activity entries, restore/verify/backup telemetry
    /// and snapshot verification times recorded since the last save or load, which are re-applied on top. Returns true when a reload occurred.
    /// Limitation: change detection compares file length and last-write time, so a rewrite that keeps the
    /// same length within the file system's timestamp granularity would go unnoticed (the state JSON
    /// carries timestamps, so this is very unlikely).
    /// </summary>
    public virtual async Task<bool> ReloadIfChangedAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            string stateFilePath;
            lock (_stateLock)
            {
                stateFilePath = _stateFilePath;
            }

            if (!_loadFailed && (!_hasSignatureBaseline || _knownFileSignature == ReadFileSignature(stateFilePath)))
            {
                return false;
            }

            _logger?.Log($"System state file changed on disk; reloading {stateFilePath}.", LogLevel.Info);
            try
            {
                await LoadStateCoreAsync(stateFilePath).ConfigureAwait(false);
                lock (_stateLock)
                {
                    // Results recorded in memory but never saved would be lost by the reload: lay them over what was loaded.
                    ReapplyPendingUnsavedLocked();
                }

                RecordFileSignature(stateFilePath);
                _loadFailed = false;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                _logger?.Log($"Error reloading system state from {stateFilePath}: {ex.Message}. Saving is disabled to protect the existing file.", LogLevel.Error);
                throw;
            }

            return true;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void RecordFileSignature(string path)
    {
        _knownFileSignature = ReadFileSignature(path);
        _hasSignatureBaseline = true;
    }

    private static (long Length, DateTime LastWriteUtc)? ReadFileSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task LoadStateCoreAsync(string stateFilePath)
    {
        if (!File.Exists(stateFilePath))
        {
            if (await TryRecoverInterruptedSwapAsync(stateFilePath).ConfigureAwait(false))
            {
                return;
            }

            // Another process may have finished creating the file while its temp file was being looked at: then it is simply loaded.
            if (!File.Exists(stateFilePath))
            {
                _logger?.Log($"State file not found, initializing empty state: {stateFilePath}", LogLevel.Info);
                InitializeEmptyState();
                return;
            }
        }

        _logger?.Log($"Loading system state from {stateFilePath}", LogLevel.Debug);
        var stateData = await TryReadStateFileAsync(stateFilePath).ConfigureAwait(false);
        if (stateData != null)
        {
            ApplyLoadedState(stateData);
            await MigrateLegacyChunkListsAsync(stateFilePath).ConfigureAwait(false);
            return;
        }

        var corruptPath = $"{stateFilePath}.corrupt-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}";
        try
        {
            File.Move(stateFilePath, corruptPath, overwrite: true);
            _logger?.Log($"System state file {stateFilePath} is corrupt; kept as {corruptPath}.", LogLevel.Error);
            PruneCorruptCopies(stateFilePath);
        }
        catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
        {
            // Without the move a later save would turn the corrupt file into the .bak.
            throw new IOException($"System state file {stateFilePath} is corrupt and could not be set aside: {moveEx.Message}", moveEx);
        }

        var backupPath = stateFilePath + BackupSuffix;
        if (File.Exists(backupPath))
        {
            var backupData = await TryReadStateFileAsync(backupPath).ConfigureAwait(false);
            if (backupData != null)
            {
                _logger?.Log($"Recovered system state from backup copy {backupPath}; changes since that save are lost.", LogLevel.Warning);
                ApplyLoadedState(backupData);
                await MigrateLegacyChunkListsAsync(stateFilePath).ConfigureAwait(false);
                return;
            }

            _logger?.Log($"Backup copy {backupPath} is also unreadable.", LogLevel.Error);
        }

        _logger?.Log($"No usable system state found for {stateFilePath}. Initializing empty state.", LogLevel.Error);
        InitializeEmptyState();
    }

    /// <summary>
    /// A save swaps its temp file over the live file (<c>File.Replace</c>: live to <c>.bak</c>, then temp to live). A crash between the two
    /// renames leaves no live file but the temp file (a complete state, flushed before the swap) and the <c>.bak</c> (the previous one).
    /// Treating that as "no state yet" would silently drop the whole history, so when the live file is missing and a temp file is left behind,
    /// the newest readable of temp files and <c>.bak</c> is restored. Without a leftover temp file a missing live file still means
    /// "no state" (a deliberate reset), so an old <c>.bak</c> is never resurrected.
    /// </summary>
    private async Task<bool> TryRecoverInterruptedSwapAsync(string stateFilePath)
    {
        var fullPath = Path.GetFullPath(stateFilePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory == null || !Directory.Exists(directory))
        {
            return false;
        }

        var temps = Directory.GetFiles(directory, Path.GetFileName(fullPath) + ".*.tmp")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        if (temps.Count == 0)
        {
            return false;
        }

        var backupPath = stateFilePath + BackupSuffix;
        foreach (var candidate in File.Exists(backupPath) ? [.. temps, backupPath] : temps)
        {
            PersistentStateData? data;
            try
            {
                data = await TryReadStateFileAsync(candidate).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // Not a leftover after all: another process is saving right now and has just renamed its temp file to the live file
                // (or abandoned it). The caller looks at the live file again.
                continue;
            }

            if (data == null)
            {
                continue;
            }

            _logger?.Log($"System state file {stateFilePath} is missing after an interrupted save; recovered it from {candidate}.", LogLevel.Warning);
            ApplyLoadedState(data);
            try
            {
                if (candidate == backupPath)
                {
                    File.Copy(candidate, stateFilePath, overwrite: false);
                }
                else
                {
                    File.Move(candidate, stateFilePath, overwrite: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The state is applied in memory; the next save writes the file again.
                _logger?.Log($"Could not put the recovered state back at {stateFilePath}: {ex.Message}", LogLevel.Warning);
            }

            await MigrateLegacyChunkListsAsync(stateFilePath).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the parsed state, or null when the content is not a valid state document. IO errors are
    /// retried with exponential backoff and then thrown.
    /// </summary>
    private async Task<PersistentStateData?> TryReadStateFileAsync(string path)
    {
        string json;
        var delay = LoadRetryBaseDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                json = await SharedFile.ReadAllTextAsync(path).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not FileNotFoundException)
            {
                // A file that vanished between the existence check and the read was renamed or deleted by another process; waiting does not bring it back.
                if (attempt >= LoadRetryAttempts)
                {
                    throw;
                }

                _logger?.Log($"Could not read {path} (attempt {attempt}/{LoadRetryAttempts}): {ex.Message}. Retrying in {delay.TotalMilliseconds:0} ms.", LogLevel.Warning);
                await Task.Delay(delay).ConfigureAwait(false);
                delay += delay;
            }
        }

        try
        {
            return JsonSerializer.Deserialize<PersistentStateData>(json);
        }
        catch (JsonException jsonEx)
        {
            _logger?.Log($"Error deserializing system state file {path}: {jsonEx.Message}", LogLevel.Error);
            return null;
        }
    }

    private void ApplyLoadedState(PersistentStateData stateData)
    {
        bool needsRebuild;
        lock (_stateLock)
        {
            LastBackupTime = stateData.LastBackupTime;
            BackupHistory = stateData.BackupHistory != null ? new(stateData.BackupHistory, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            _fileMetadata = BuildFileMetadataLocked(stateData);
            ChunkReferenceCounts = stateData.ChunkReferenceCounts != null ? new(stateData.ChunkReferenceCounts, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            Telemetry = stateData.Telemetry ?? new SnapshotTelemetryAggregate();
            SnapshotVerificationTimes = stateData.SnapshotVerificationTimes != null
                ? new(stateData.SnapshotVerificationTimes, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
            _deepScanTimes = stateData.DeepScanTimes != null
                ? new(stateData.DeepScanTimes, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
            NormalizeTelemetryLocked();

            _installId = string.IsNullOrWhiteSpace(stateData.InstallId) ? null : stateData.InstallId;
            LastRetentionRunUtc = stateData.LastRetentionRunUtc;
            _pendingChunkDeletes.Clear();
            if (stateData.PendingChunkDeletes != null)
            {
                _pendingChunkDeletes.AddRange(stateData.PendingChunkDeletes
                    .Where(item => !string.IsNullOrWhiteSpace(item.ChunkId))
                    .DistinctBy(item => PendingKey(item.StorageType, item.ChunkStorageNamespace, item.ChunkId), StringComparer.OrdinalIgnoreCase)
                    .Take(MaxPendingChunkDeletes));
            }

            RebuildPendingChunkDeleteKeysLocked();

            ApplyBackupRunsLocked(stateData.BackupRuns, stateData.GroupSuccessUtc);
            ApplyActivityLocked(stateData.Activity);

            ChunkReferenceCountsIncomplete = stateData.ChunkReferenceCountsIncomplete;
            needsRebuild = ChunkReferenceCounts.Count == 0 || ContainsLegacyChunkReferenceKeysLocked();

            // A reload can resurrect snapshots whose removal was never saved: their sidecars must survive.
            var loadedKeys = BackupHistory.Values.SelectMany(backups => backups).Select(ChunkListKey).ToHashSet(StringComparer.Ordinal);
            _pendingChunkListDeletes.RemoveAll(loadedKeys.Contains);
        }

        if (needsRebuild)
        {
            RebuildChunkReferenceCounts();
        }
        _logger?.Log($"Loaded state: {CountFileMetadata()} file metadata entries, {BackupHistory.Count} backup history entries.", LogLevel.Info);
    }

    /// <summary>
    /// Unique chunk ids of a snapshot: the in-memory list while it is still pending a save, otherwise the
    /// sidecar. Null when neither exists or the sidecar is unreadable (the caller should fall back to the manifest).
    /// </summary>
    public virtual Task<IReadOnlyList<string>?> TryGetSnapshotChunkIdsAsync(BackupInfo backup)
    {
        ArgumentNullException.ThrowIfNull(backup);

        string stateFilePath;
        lock (_stateLock)
        {
            if (backup.ChunkIds.Count > 0)
            {
                return Task.FromResult<IReadOnlyList<string>?>([.. backup.ChunkIds]);
            }

            stateFilePath = _stateFilePath;
        }

        var ids = TryReadChunkList(stateFilePath, backup);
        return Task.FromResult<IReadOnlyList<string>?>(ids);
    }

    private static string ChunkListDirectory(string stateFilePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(stateFilePath))!, ChunkListDirectoryName);

    private static string ChunkListKey(BackupInfo backup)
    {
        var id = backup.SnapshotId;
        if (!string.IsNullOrWhiteSpace(id) && id.Length <= 128 && id != "." && id != ".."
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return id;
        }

        var seed = string.IsNullOrWhiteSpace(backup.ManifestPath) ? backup.Path : backup.ManifestPath;
        return "m-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed ?? "")))[..32];
    }

    /// <summary>
    /// Writes the snapshot's sidecar atomically unless an up-to-date one exists. Ids that are all 64-hex
    /// go to <c>&lt;key&gt;.chunks</c> as concatenated 32-byte values; anything else (non-SHA-256 ids) to the
    /// length-prefixed <c>&lt;key&gt;.chunklist</c>. Returns the path written, or null when nothing was written.
    /// </summary>
    private static async Task<string?> WriteChunkListAsync(string stateFilePath, BackupInfo backup, List<string> ids)
    {
        var directory = ChunkListDirectory(stateFilePath);
        Directory.CreateDirectory(directory);
        var key = ChunkListKey(backup);

        var binary = new byte[ids.Count * ChunkIdByteLength];
        var isHex = true;
        for (var i = 0; i < ids.Count && isHex; i++)
        {
            isHex = ids[i].Length == ChunkIdByteLength * 2 && ids[i].All(Uri.IsHexDigit);
            if (isHex)
            {
                Convert.FromHexString(ids[i]).CopyTo(binary, i * ChunkIdByteLength);
            }
        }

        string path;
        byte[] content;
        if (isHex)
        {
            path = Path.Combine(directory, key + ChunkListExtension);
            content = binary;
        }
        else
        {
            path = Path.Combine(directory, key + ChunkListFallbackExtension);
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(ids.Count);
                foreach (var id in ids)
                {
                    writer.Write(id);
                }
            }

            content = buffer.ToArray();
        }

        if (new FileInfo(path) is { Exists: true } existing && existing.Length == content.Length)
        {
            return null;
        }

        var tempPath = SharedFile.UniqueTempPath(path);
        try
        {
            var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        return path;
    }

    private List<string>? TryReadChunkList(string stateFilePath, BackupInfo backup)
    {
        var directory = ChunkListDirectory(stateFilePath);
        var key = ChunkListKey(backup);
        try
        {
            var binaryPath = Path.Combine(directory, key + ChunkListExtension);
            if (File.Exists(binaryPath))
            {
                var bytes = File.ReadAllBytes(binaryPath);
                if (bytes.Length % ChunkIdByteLength != 0)
                {
                    _logger?.Log($"Chunk list {binaryPath} is corrupt (length {bytes.Length}); ignoring it.", LogLevel.Warning);
                    return null;
                }

                var ids = new List<string>(bytes.Length / ChunkIdByteLength);
                for (var offset = 0; offset < bytes.Length; offset += ChunkIdByteLength)
                {
                    ids.Add(Convert.ToHexStringLower(bytes.AsSpan(offset, ChunkIdByteLength)));
                }

                return ids;
            }

            var fallbackPath = Path.Combine(directory, key + ChunkListFallbackExtension);
            if (File.Exists(fallbackPath))
            {
                using var reader = new BinaryReader(File.OpenRead(fallbackPath), System.Text.Encoding.UTF8);
                var count = reader.ReadInt32();
                var ids = new List<string>(Math.Max(0, Math.Min(count, 1_000_000)));
                for (var i = 0; i < count; i++)
                {
                    ids.Add(reader.ReadString());
                }

                return ids;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _logger?.Log($"Could not read the chunk list for snapshot '{backup.SnapshotId ?? backup.Path}': {ex.Message}", LogLevel.Warning);
        }

        return null;
    }

    private static void DeleteChunkListFiles(string stateFilePath, string key)
    {
        var directory = ChunkListDirectory(stateFilePath);
        TryDeleteFile(Path.Combine(directory, key + ChunkListExtension));
        TryDeleteFile(Path.Combine(directory, key + ChunkListFallbackExtension));
    }

    /// <summary>
    /// State files written before sidecars carry the chunk lists inline. Writes each to its sidecar and clears
    /// it from memory so the next save omits it. A failure keeps the list in memory; the save retries.
    /// </summary>
    private async Task MigrateLegacyChunkListsAsync(string stateFilePath)
    {
        List<(BackupInfo Live, List<string> Ids)> legacy;
        lock (_stateLock)
        {
            legacy = [.. BackupHistory.Values
                .SelectMany(backups => backups)
                .Where(backup => backup.ChunkIds.Count > 0)
                .Select(backup => (backup, new List<string>(backup.ChunkIds)))];
        }

        if (legacy.Count == 0)
        {
            return;
        }

        var migrated = 0;
        foreach (var (live, ids) in legacy)
        {
            try
            {
                await WriteChunkListAsync(stateFilePath, live, ids).ConfigureAwait(false);
                lock (_stateLock)
                {
                    live.ChunkIds = [];
                }

                migrated++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.Log($"Could not write chunk list sidecar for '{live.SnapshotId ?? live.Path}': {ex.Message}", LogLevel.Warning);
            }
        }

        _logger?.Log($"Migrated {migrated} of {legacy.Count} inline chunk lists to sidecars.", LogLevel.Info);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort: a leftover temp file is harmless.
        }
    }

    /// <summary>Corrupt state copies kept (newest by name, which carries the UTC time).</summary>
    internal const int MaxCorruptCopies = 5;

    private void PruneCorruptCopies(string stateFilePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(stateFilePath)!;
            var stale = Directory.GetFiles(directory, Path.GetFileName(stateFilePath) + ".corrupt-*")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(MaxCorruptCopies);
            foreach (var path in stale)
            {
                TryDeleteFile(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only.
        }
    }

    private void InitializeEmptyState()
    {
        lock (_stateLock)
        {
            LastBackupTime = DateTime.MinValue;
            BackupHistory = new(StringComparer.OrdinalIgnoreCase);
            _fileMetadata = new(StringComparer.OrdinalIgnoreCase);
            ChunkReferenceCounts = new(StringComparer.OrdinalIgnoreCase);
            ChunkReferenceCountsIncomplete = false;
            _pendingChunkListDeletes.Clear();
            Telemetry = new SnapshotTelemetryAggregate();
            SnapshotVerificationTimes = new(StringComparer.OrdinalIgnoreCase);
            _deepScanTimes = new(StringComparer.OrdinalIgnoreCase);
            _installId = null;
            LastRetentionRunUtc = DateTime.MinValue;
            _pendingChunkDeletes.Clear();
            _pendingChunkDeleteKeys.Clear();
            ApplyBackupRunsLocked(null, null);
            ApplyActivityLocked(null);
        }
    }

    /// <summary>
    /// Absolute paths of the files recorded for (group, storage type). A lookup of one bucket, so it does not
    /// scan other groups' metadata.
    /// </summary>
    public virtual List<string> GetTrackedFiles(string group, string storageType)
    {
        group = NormalizeGroup(group);
        lock (_stateLock)
        {
            return _fileMetadata.TryGetValue(group, out var byStorage) && byStorage.TryGetValue(storageType, out var bucket)
                ? [.. bucket.Keys.Select(relative => ToAbsolutePath(group, relative))]
                : [];
        }
    }

    /// <summary>
    /// Files that differ from what was last backed up for (group, storage type), judged on a fresh stat of each
    /// path (a normal, non-deep pass that trusts size and timestamps where they are recorded). Callers that already
    /// walked the tree use the overload taking <see cref="ScannedFile"/> records.
    /// </summary>
    public List<string> GetChangedFiles(List<string> allFiles, BackupType backupType, string group, string storageType)
    {
        if (backupType == BackupType.Full)
        {
            _logger?.Log("Full backup requested, including all files.", LogLevel.Info);
            return allFiles;
        }

        var scanned = new List<ScannedFile>(allFiles.Count);
        var unstatable = new List<string>();
        foreach (var path in allFiles)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    scanned.Add(new ScannedFile(path, info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes));
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"Error checking file {path}: {ex.Message}. Including in backup.", LogLevel.Warning);
                unstatable.Add(path);
            }
        }

        return [.. GetChangedFiles(scanned, backupType, group, storageType, new ChangeScanContext()), .. unstatable];
    }

    /// <summary>
    /// Files that differ from what was last backed up for (group, storage type). A full backup type returns every
    /// file; callers that know the destination holds no usable snapshot should treat every file as changed
    /// themselves rather than rely on metadata recorded for a different destination.
    /// <para>
    /// A file is unchanged when its size matches and either (normal pass) its last-write and creation times match
    /// the recorded ones and the recorded time was not racy, or (deep scan, or no usable recorded signature) the
    /// content hash matches. A file found unchanged by hash gets its signature refreshed so later passes can skip it.
    /// </para>
    /// </summary>
    public virtual List<string> GetChangedFiles(
        IReadOnlyList<ScannedFile> allFiles,
        BackupType backupType,
        string group,
        string storageType,
        ChangeScanContext scan)
    {
        scan.FilesScanned += allFiles.Count;

        if (backupType == BackupType.Full)
        {
            _logger?.Log("Full backup requested, including all files.", LogLevel.Info);
            return [.. allFiles.Select(file => file.Path)];
        }

        var filesToBackup = new List<string>();
        var normalizedGroup = NormalizeGroup(group);
        var currentMetadataSnapshot = GetFileMetadata(normalizedGroup, storageType);
        var refreshed = new List<(string Key, FileMetadata Previous, ScannedFile File)>();

        foreach (var file in allFiles)
        {
            var filePath = file.Path;
            try
            {
                bool shouldBackup = false;
                var key = ToRelativeKey(normalizedGroup, NormalizePath(filePath));

                if (!currentMetadataSnapshot.TryGetValue(key, out var previousMetadata))
                {
                    _logger?.Log($"New file detected: {filePath}", LogLevel.Debug);
                    shouldBackup = true;
                }
                else if (backupType == BackupType.Incremental || backupType == BackupType.ChunkSnapshot)
                {
                    var backupTypeLabel = backupType.ToString();

                    if (file.Length != previousMetadata.Size)
                    {
                        _logger?.Log($"File changed ({backupTypeLabel}, size): {filePath}", LogLevel.Debug);
                        shouldBackup = true;
                    }
                    else if (!scan.DeepScan && IsStatUnchanged(previousMetadata, file))
                    {
                        scan.FilesReused++;
                    }
                    else if (!string.IsNullOrEmpty(previousMetadata.Hash))
                    {
                        scan.FilesHashed++;
                        var currentHash = CalculateFileHash(filePath, scan.ContentHasher);
                        if (!string.IsNullOrEmpty(currentHash))
                        {
                            if (!string.Equals(currentHash, previousMetadata.Hash, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger?.Log($"File changed ({backupTypeLabel}, hash): {filePath}", LogLevel.Debug);
                                shouldBackup = true;
                            }
                            else
                            {
                                refreshed.Add((key, previousMetadata, file));
                            }
                        }
                        else
                        {
                            // Cannot verify it: back it up, so the per-file skip/carry-forward path decides.
                            scan.FilesUnverified++;
                            _logger?.Log($"File changed ({backupTypeLabel}, unreadable for hashing): {filePath}", LogLevel.Debug);
                            shouldBackup = true;
                        }
                    }
                    else if (file.LastWriteUtc > previousMetadata.LastModified)
                    {
                        _logger?.Log($"File changed ({backupTypeLabel}, timestamp): {filePath}", LogLevel.Debug);
                        shouldBackup = true;
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

        RefreshStatSignatures(normalizedGroup, storageType, refreshed, scan.ScanStartUtc);

        scan.FilesChanged += filesToBackup.Count;
        _logger?.Log($"Found {filesToBackup.Count} changed files out of {allFiles.Count} total files for {backupType} backup.", LogLevel.Info);
        return filesToBackup;
    }

    /// <summary>
    /// True when the recorded stat signature (size already compared) still matches and was recorded safely: the
    /// last-write time is older than the recording scan's start by more than the racy window, so no later write
    /// can have left it unchanged. Entries without a signature are never trusted.
    /// </summary>
    private static bool IsStatUnchanged(FileMetadata previous, ScannedFile current)
    {
        if (previous.RecordedUtc is not { } recordedUtc || previous.CreatedUtc is not { } createdUtc)
        {
            return false;
        }

        return previous.LastModified.Ticks == current.LastWriteUtc.Ticks
            && createdUtc.Ticks == current.CreationUtc.Ticks
            && previous.LastModified < recordedUtc - ChangeScanContext.RacyWindow;
    }

    /// <summary>
    /// Stores the current signature of files whose content was just verified equal to the recorded hash, so the
    /// next normal pass can skip them. Skips an entry that was replaced since the snapshot was taken.
    /// </summary>
    private void RefreshStatSignatures(
        string normalizedGroup,
        string storageType,
        List<(string Key, FileMetadata Previous, ScannedFile File)> refreshed,
        DateTime scanStartUtc)
    {
        if (refreshed.Count == 0)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!_fileMetadata.TryGetValue(normalizedGroup, out var byStorage) || !byStorage.TryGetValue(storageType, out var bucket))
            {
                return;
            }

            foreach (var (key, previous, file) in refreshed)
            {
                if (bucket.TryGetValue(key, out var live) && ReferenceEquals(live, previous))
                {
                    live.LastModified = file.LastWriteUtc;
                    live.CreatedUtc = file.CreationUtc;
                    live.RecordedUtc = scanStartUtc;
                }
            }
        }
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
            return await FileHasher.ComputeHashAsync(filePath).ConfigureAwait(false);
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

    private static string CalculateFileHash(string filePath, Func<string, string>? hasher = null)
    {
        try
        {
            return hasher != null ? hasher(filePath) : FileHasher.ComputeHash(filePath);
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
            item => item.Value.Select(CloneBackupInfo).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static BackupInfo CloneBackupInfo(BackupInfo backup)
    {
        return new BackupInfo
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
            ChunkStorageNamespace = backup.ChunkStorageNamespace,
            Label = backup.Label,
            StoredBytes = backup.StoredBytes,
            PlainBytes = backup.PlainBytes
        };
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
                ReferencedStoredBytes = telemetry.Backup.ReferencedStoredBytes,
                UploadedStoredBytes = telemetry.Backup.UploadedStoredBytes,
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

    private static Dictionary<string, Dictionary<string, Dictionary<string, FileMetadata>>> CloneFileMetadata(
        Dictionary<string, Dictionary<string, Dictionary<string, FileMetadata>>> fileMetadata)
    {
        return fileMetadata.ToDictionary(
            group => group.Key,
            group => group.Value.ToDictionary(
                storage => storage.Key,
                storage => storage.Value.ToDictionary(
                    item => item.Key,
                    item => new FileMetadata
                    {
                        FilePath = item.Value.FilePath,
                        Size = item.Value.Size,
                        LastModified = item.Value.LastModified,
                        Hash = item.Value.Hash,
                        CreatedUtc = item.Value.CreatedUtc,
                        RecordedUtc = item.Value.RecordedUtc
                    },
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private int CountFileMetadata()
    {
        lock (_stateLock)
        {
            return _fileMetadata.Values.Sum(byStorage => byStorage.Values.Sum(bucket => bucket.Count));
        }
    }

    /// <summary>
    /// Builds the in-memory metadata from a loaded state document. Entries of the old flat dictionary (v1.6/v1.7,
    /// absolute paths, no storage type) are moved to the group that contains them, under the storage type of that
    /// group's newest snapshot: the destination the group was last backed up to, which is what the old record
    /// described. Groups without a snapshot cannot be attributed and their entries are dropped (there is nothing
    /// to be "unchanged" relative to, so the first backup is a full pass anyway). Caller holds <c>_stateLock</c>
    /// with <see cref="BackupHistory"/> already applied.
    /// </summary>
    private Dictionary<string, Dictionary<string, Dictionary<string, FileMetadata>>> BuildFileMetadataLocked(PersistentStateData stateData)
    {
        var result = new Dictionary<string, Dictionary<string, Dictionary<string, FileMetadata>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (group, byStorage) in stateData.FileMetadataByGroup ?? [])
        {
            foreach (var (storageType, files) in byStorage ?? [])
            {
                if (files == null)
                {
                    continue;
                }

                var normalizedGroup = NormalizeGroup(group);
                if (!result.TryGetValue(normalizedGroup, out var storages))
                {
                    storages = new(StringComparer.OrdinalIgnoreCase);
                    result[normalizedGroup] = storages;
                }

                var bucket = new Dictionary<string, FileMetadata>(StringComparer.OrdinalIgnoreCase);
                foreach (var (relative, metadata) in files)
                {
                    bucket[relative] = metadata;
                }

                storages[storageType] = bucket;
            }
        }

        if (stateData.FileMetadata is not { Count: > 0 } legacy)
        {
            return result;
        }

        var groupStorage = new List<(string Group, string Prefix, string StorageType)>();
        foreach (var (group, backups) in BackupHistory)
        {
            var newest = backups
                .Where(b => b.ArtifactType == BackupArtifactType.SnapshotManifest && !string.IsNullOrWhiteSpace(b.StorageType))
                .OrderByDescending(b => b.Timestamp)
                .FirstOrDefault();
            if (newest == null)
            {
                continue;
            }

            var normalizedGroup = NormalizeGroup(group);
            groupStorage.Add((normalizedGroup, normalizedGroup.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, newest.StorageType!));
        }

        var migrated = 0;
        foreach (var (path, metadata) in legacy)
        {
            var normalizedPath = NormalizePath(path);
            (string Group, string Prefix, string StorageType)? owner = null;
            foreach (var candidate in groupStorage)
            {
                if (normalizedPath.StartsWith(candidate.Prefix, StringComparison.OrdinalIgnoreCase)
                    && (owner == null || candidate.Prefix.Length > owner.Value.Prefix.Length))
                {
                    owner = candidate;
                }
            }

            if (owner == null)
            {
                continue;
            }

            if (!result.TryGetValue(owner.Value.Group, out var storages))
            {
                storages = new(StringComparer.OrdinalIgnoreCase);
                result[owner.Value.Group] = storages;
            }

            if (!storages.TryGetValue(owner.Value.StorageType, out var bucket))
            {
                bucket = new(StringComparer.OrdinalIgnoreCase);
                storages[owner.Value.StorageType] = bucket;
            }

            // An entry already in the new structure is newer than anything from the flat dictionary.
            bucket.TryAdd(ToRelativeKey(owner.Value.Group, normalizedPath), metadata);
            migrated++;
        }

        _logger?.Log($"Migrated {migrated} of {legacy.Count} file metadata entries to per-destination records.", LogLevel.Info);
        return result;
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

    /// <summary>Snapshot-manifest entries with their in-memory list (null when it must come from a sidecar), taken under the lock.</summary>
    private (string StateFilePath, List<(BackupInfo Live, List<string>? Ids)> Entries, HashSet<string> AllKeys) CaptureSnapshotsForRebuild()
    {
        lock (_stateLock)
        {
            var all = BackupHistory.Values.SelectMany(backups => backups).ToList();
            var entries = all
                .Where(backup => backup.ArtifactType == BackupArtifactType.SnapshotManifest)
                .Select(backup => (backup, backup.ChunkIds.Count > 0 ? new List<string>(backup.ChunkIds) : null))
                .ToList();
            return (_stateFilePath, entries, all.Select(ChunkListKey).ToHashSet(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Recomputes the reference counts from every snapshot's chunk list. Sidecars are read outside the state lock.
    /// A snapshot whose list cannot be found marks the counts incomplete (never silently under-counts).
    /// </summary>
    private void RebuildChunkReferenceCounts()
    {
        var (stateFilePath, entries, allKeys) = CaptureSnapshotsForRebuild();

        var resolved = new List<(BackupInfo Live, IReadOnlyList<string>? Ids)>(entries.Count);
        foreach (var (live, ids) in entries)
        {
            resolved.Add((live, ids ?? (IReadOnlyList<string>?)TryReadChunkList(stateFilePath, live)));
        }

        var incomplete = false;
        lock (_stateLock)
        {
            ChunkReferenceCounts.Clear();
            foreach (var (live, ids) in resolved)
            {
                if (ids == null)
                {
                    incomplete = true;
                    _logger?.Log($"Chunk list for snapshot '{live.SnapshotId ?? live.Path}' is missing; reference counts are incomplete and no chunk will be deleted until they are repaired.", LogLevel.Error);
                    continue;
                }

                RegisterChunkReferencesLocked(live.StorageType, live.ChunkStorageNamespace, ids);
            }

            ChunkReferenceCountsIncomplete = incomplete;
        }

        SweepOrphanChunkLists(stateFilePath, allKeys);
    }

    /// <summary>Deletes sidecars that no history entry refers to. Only files older than an hour, so a sidecar another process has just written for a snapshot it has not yet saved survives.</summary>
    private void SweepOrphanChunkLists(string stateFilePath, HashSet<string> knownKeys)
    {
        try
        {
            var directory = ChunkListDirectory(stateFilePath);
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                string? key = null;
                if (name.EndsWith(ChunkListExtension, StringComparison.Ordinal))
                {
                    key = name[..^ChunkListExtension.Length];
                }
                else if (name.EndsWith(ChunkListFallbackExtension, StringComparison.Ordinal))
                {
                    key = name[..^ChunkListFallbackExtension.Length];
                }

                if (key != null && !knownKeys.Contains(key) && File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
                {
                    _logger?.Log($"Removing orphan chunk list {name}.", LogLevel.Info);
                    TryDeleteFile(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Log($"Could not sweep orphan chunk lists: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>Snapshots with neither an in-memory chunk list nor a sidecar (candidates for recovery from their manifest).</summary>
    public virtual List<BackupInfo> GetSnapshotsWithoutChunkLists()
    {
        var (stateFilePath, entries, _) = CaptureSnapshotsForRebuild();
        return [.. entries
            .Where(entry => entry.Ids == null && TryReadChunkList(stateFilePath, entry.Live) == null)
            .Select(entry => CloneBackupInfo(entry.Live))];
    }

    /// <summary>
    /// Completes the chunk lists of snapshots whose sidecar was missing using <paramref name="recoveredByPath"/>
    /// (snapshot path to ids read from its manifest; an empty list for a manifest that no longer exists), writes
    /// them as sidecars and rebuilds every reference count. Returns true, and clears
    /// <see cref="ChunkReferenceCountsIncomplete"/>, only when every snapshot now has a list. The caller saves.
    /// </summary>
    public virtual async Task<bool> RepairChunkReferenceCountsAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> recoveredByPath)
    {
        ArgumentNullException.ThrowIfNull(recoveredByPath);

        foreach (var backup in GetSnapshotsWithoutChunkLists())
        {
            if (!recoveredByPath.TryGetValue(backup.Path, out var ids))
            {
                continue;
            }

            string stateFilePath;
            BackupInfo? live;
            lock (_stateLock)
            {
                stateFilePath = _stateFilePath;
                live = BackupHistory.Values.SelectMany(backups => backups).FirstOrDefault(b => b.Path == backup.Path);
            }

            if (live != null)
            {
                await WriteChunkListAsync(stateFilePath, live, [.. ids.Select(id => id.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)]).ConfigureAwait(false);
            }
        }

        RebuildChunkReferenceCounts();
        return !ChunkReferenceCountsIncomplete;
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

    /// <summary>
    /// Unique chunk ids of the snapshot. Held in memory only until the next save writes them to the
    /// snapshot's sidecar and clears them; never written to system_state.json. Use
    /// <see cref="SystemState.TryGetSnapshotChunkIdsAsync"/> to read them regardless of where they live.
    /// </summary>
    [JsonIgnore]
    public List<string> ChunkIds { get; set; } = [];

    /// <summary>Read-only bridge for state files written before sidecars, which stored <c>ChunkIds</c> inline. Never written.</summary>
    [JsonPropertyName("ChunkIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public List<string>? LegacyChunkIds
    {
        get => null;
        set
        {
            if (value is { Count: > 0 })
            {
                ChunkIds = value;
            }
        }
    }

    public string? RootHash { get; set; }
    public bool Encrypted { get; set; }
    public string? ChunkStorageNamespace { get; set; }

    /// <summary>True until the next save has written this snapshot's chunk list to its sidecar (also covers an empty list).</summary>
    [JsonIgnore]
    internal bool ChunkListPending { get; set; }

    /// <summary>User-supplied tag, e.g. "before Windows reinstall". Null when unlabelled.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Stored (compressed/encrypted) size of the unique chunks this snapshot references, from its manifest; 0 when
    /// unknown (snapshots made before this was recorded). Additive: older versions ignore it.
    /// </summary>
    public long StoredBytes { get; set; }

    /// <summary>Uncompressed size of the same chunks; 0 when unknown.</summary>
    public long PlainBytes { get; set; }
}

public enum BackupArtifactType
{
    Archive,
    SnapshotManifest
}

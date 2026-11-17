using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

public partial class SystemState
{
    /// <summary>Activity entries kept; older ones are dropped so the state file stays small.</summary>
    public const int MaxActivityEntries = 200;

    private const int MaxActivityTextLength = 500;

    // Oldest first. Guarded by _stateLock.
    private readonly List<ActivityEntry> _activity = [];

    public virtual void RecordActivity(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Messages can carry exception text; keep each entry small.
        // They are persisted and shown in bug reports, so secrets in exception text are masked first.
        entry.Message = Truncate(DiagnosticsReport.RedactText(entry.Message ?? ""));
        entry.Target = Truncate(DiagnosticsReport.RedactText(entry.Target ?? ""));

        lock (_stateLock)
        {
            _activity.Add(entry);
            _pendingActivity.Add(entry);
            if (_activity.Count > MaxActivityEntries)
            {
                _activity.RemoveRange(0, _activity.Count - MaxActivityEntries);
            }
        }
    }

    /// <summary>Newest first (by end time).</summary>
    public virtual List<ActivityEntry> GetRecentActivity(int max = MaxActivityEntries)
    {
        lock (_stateLock)
        {
            return [.. _activity.OrderByDescending(a => a.EndUtc).Take(Math.Max(0, max))];
        }
    }

    private static string Truncate(string? text)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= MaxActivityTextLength ? text : text[..MaxActivityTextLength];

    private List<ActivityEntry> CloneActivityLocked() => [.. _activity];

    private void ApplyActivityLocked(List<ActivityEntry>? entries)
    {
        _activity.Clear();
        if (entries != null)
        {
            _activity.AddRange(entries.Where(e => e != null).TakeLast(MaxActivityEntries));
        }
    }

    // Results recorded since the last save or load. A reload (another process saved while this one held no lease)
    // would otherwise discard them, so they are laid over the reloaded state. Guarded by _stateLock.
    private readonly List<ActivityEntry> _pendingActivity = [];
    private SnapshotTelemetryAggregate _pendingTelemetry = new();
    private readonly Dictionary<string, DateTime> _pendingVerified = new(StringComparer.OrdinalIgnoreCase);

    private sealed record PendingUnsaved(
        List<ActivityEntry> Activity, SnapshotTelemetryAggregate Telemetry, Dictionary<string, DateTime> Verified);

    private PendingUnsaved TakePendingUnsavedLocked()
    {
        var taken = new PendingUnsaved([.. _pendingActivity], _pendingTelemetry, new(_pendingVerified, StringComparer.OrdinalIgnoreCase));
        _pendingActivity.Clear();
        _pendingTelemetry = new SnapshotTelemetryAggregate();
        _pendingVerified.Clear();
        return taken;
    }

    // A save failed: what it took is unsaved again.
    private void RestorePendingUnsavedLocked(PendingUnsaved? taken)
    {
        if (taken == null)
        {
            return;
        }

        _pendingActivity.InsertRange(0, taken.Activity);
        AddTelemetry(taken.Telemetry, _pendingTelemetry);
        _pendingTelemetry = taken.Telemetry;
        foreach (var (path, utc) in taken.Verified)
        {
            if (!_pendingVerified.TryGetValue(path, out var existing) || existing < utc)
            {
                _pendingVerified[path] = utc;
            }
        }
    }

    // Called right after a reload replaced the live state; the pending items stay pending (still unsaved).
    private void ReapplyPendingUnsavedLocked()
    {
        var known = _activity.Select(ActivityKey).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in _pendingActivity)
        {
            if (known.Add(ActivityKey(entry)))
            {
                _activity.Add(entry);
            }
        }

        if (_activity.Count > MaxActivityEntries)
        {
            _activity.RemoveRange(0, _activity.Count - MaxActivityEntries);
        }

        NormalizeTelemetryLocked();
        AddTelemetry(Telemetry, _pendingTelemetry);
        foreach (var (path, utc) in _pendingVerified)
        {
            if (!SnapshotVerificationTimes.TryGetValue(path, out var existing) || existing < utc)
            {
                SnapshotVerificationTimes[path] = utc;
            }
        }
    }

    private static string ActivityKey(ActivityEntry e) => $"{e.Type}|{e.Target}|{e.StartUtc.Ticks}|{e.EndUtc.Ticks}|{e.Result}|{e.Message}";

    private static void AddTelemetry(SnapshotTelemetryAggregate dst, SnapshotTelemetryAggregate src)
    {
        var b = dst.Backup;
        var sb = src.Backup;
        b.SnapshotCount += sb.SnapshotCount;
        b.FileCount += sb.FileCount;
        b.ChunkReferences += sb.ChunkReferences;
        b.UniqueChunks += sb.UniqueChunks;
        b.UploadedChunks += sb.UploadedChunks;
        b.UniqueReusedChunks += sb.UniqueReusedChunks;
        b.StorageHitChunks += sb.StorageHitChunks;
        b.CandidateChunks += sb.CandidateChunks;
        b.ReferencedStoredBytes += sb.ReferencedStoredBytes;
        b.UploadedStoredBytes += sb.UploadedStoredBytes;
        b.LastUpdatedUtc = Max(b.LastUpdatedUtc, sb.LastUpdatedUtc);

        var r = dst.Restore;
        var sr = src.Restore;
        r.AttemptCount += sr.AttemptCount;
        r.SuccessCount += sr.SuccessCount;
        r.ValidationFailureCount += sr.ValidationFailureCount;
        r.FilesExpected += sr.FilesExpected;
        r.FilesRestored += sr.FilesRestored;
        r.ChunkReferencesExpected += sr.ChunkReferencesExpected;
        r.ChunkReferencesProcessed += sr.ChunkReferencesProcessed;
        r.ChunkDownloads += sr.ChunkDownloads;
        r.ChunkCacheHits += sr.ChunkCacheHits;
        foreach (var (category, count) in sr.FailureCategoryCounts)
        {
            r.FailureCategoryCounts[category] = r.FailureCategoryCounts.GetValueOrDefault(category) + count;
        }

        r.LastUpdatedUtc = Max(r.LastUpdatedUtc, sr.LastUpdatedUtc);

        var v = dst.Verification;
        var sv = src.Verification;
        v.RunCount += sv.RunCount;
        v.SuccessCount += sv.SuccessCount;
        v.ValidationFailureCount += sv.ValidationFailureCount;
        v.FileCount += sv.FileCount;
        v.ChunkReferences += sv.ChunkReferences;
        v.UniqueChunks += sv.UniqueChunks;
        v.DownloadedChunks += sv.DownloadedChunks;
        v.MissingChunks += sv.MissingChunks;
        v.InvalidChunks += sv.InvalidChunks;
        v.InvalidFiles += sv.InvalidFiles;
        v.LastUpdatedUtc = Max(v.LastUpdatedUtc, sv.LastUpdatedUtc);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

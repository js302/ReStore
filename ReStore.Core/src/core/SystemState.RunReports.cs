
namespace ReStore.Core.src.core;

public partial class SystemState
{
    /// <summary>Run reports kept; older ones are dropped so the state file stays small.</summary>
    public const int MaxBackupRunReports = 50;

    // Oldest first. Guarded by _stateLock.
    private readonly List<BackupRunReport> _backupRuns = [];

    // Last time a run for the group finished without error, including runs that found nothing to back up
    // (which record no history). Drives the staleness alert. Guarded by _stateLock.
    private Dictionary<string, DateTime> _groupSuccessUtc = new(StringComparer.OrdinalIgnoreCase);

    public virtual void RecordBackupRun(BackupRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        lock (_stateLock)
        {
            _backupRuns.Add(report);
            if (_backupRuns.Count > MaxBackupRunReports)
            {
                _backupRuns.RemoveRange(0, _backupRuns.Count - MaxBackupRunReports);
            }

            _groupSuccessUtc[NormalizeGroup(report.Group)] = report.TimestampUtc;
        }

        // Counts the run anonymously (coarse buckets only; never the group path).
        ReStore.Core.src.telemetry.Telemetry.TrackBackupRun(report);
    }

    /// <summary>Records a run that completed but had nothing to snapshot.</summary>
    public virtual void RecordGroupRunSucceeded(string group, DateTime utc)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return;
        }

        lock (_stateLock)
        {
            _groupSuccessUtc[NormalizeGroup(group)] = utc;
        }
    }

    /// <summary>Newest first.</summary>
    public virtual List<BackupRunReport> GetRecentBackupRuns(int max = MaxBackupRunReports)
    {
        lock (_stateLock)
        {
            return [.. Enumerable.Reverse(_backupRuns).Take(Math.Max(0, max))];
        }
    }

    /// <summary>
    /// When the group last completed a run successfully (a new snapshot, or a check that found nothing to
    /// do), or the newest snapshot if that is later; <see cref="DateTime.MinValue"/> if never.
    /// </summary>
    public virtual DateTime GetLastSuccessfulRunUtc(string group)
    {
        var snapshot = GetLastBackupTimeForGroup(group);
        if (string.IsNullOrWhiteSpace(group))
        {
            return snapshot;
        }

        lock (_stateLock)
        {
            return _groupSuccessUtc.TryGetValue(NormalizeGroup(group), out var checkedUtc) && checkedUtc > snapshot
                ? checkedUtc
                : snapshot;
        }
    }

    /// <summary>
    /// Newest snapshot manifests per group, cloned under the lock without chunk lists, so a Dashboard refresh
    /// does not copy the whole history while a backup waits for the lock.
    /// </summary>
    public virtual Dictionary<string, List<BackupInfo>> GetRecentBackups(int perGroup)
    {
        lock (_stateLock)
        {
            var result = new Dictionary<string, List<BackupInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (group, backups) in BackupHistory)
            {
                result[group] = [.. backups
                    .OrderByDescending(b => b.Timestamp)
                    .Take(perGroup)
                    .Select(b =>
                    {
                        var copy = CloneBackupInfo(b);
                        copy.ChunkIds = [];
                        return copy;
                    })];
            }

            return result;
        }
    }

    private List<BackupRunReport> CloneBackupRunsLocked() => [.. _backupRuns];

    private Dictionary<string, DateTime> CloneGroupSuccessLocked() => new(_groupSuccessUtc, StringComparer.OrdinalIgnoreCase);

    private void ApplyBackupRunsLocked(List<BackupRunReport>? runs, Dictionary<string, DateTime>? groupSuccess)
    {
        _backupRuns.Clear();
        if (runs != null)
        {
            _backupRuns.AddRange(runs.Where(r => r != null).TakeLast(MaxBackupRunReports));
        }

        _groupSuccessUtc = groupSuccess != null
            ? new(groupSuccess, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }
}

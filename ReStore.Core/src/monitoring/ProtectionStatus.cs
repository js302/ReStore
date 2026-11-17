using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.monitoring;

/// <summary>One watched folder in <see cref="ProtectionStatus"/>.</summary>
public sealed record GroupStatus(
    string Path,
    string StorageType,
    bool Exists,
    int SnapshotCount,
    DateTime? LastSnapshotUtc,
    DateTime? LastSuccessUtc,
    BackupRunReport? LastRun,
    DateTime? NextDueUtc,
    bool Overdue,
    bool Stale,
    ActivityEntry? LastFailure = null,
    string? Schedule = null,
    string ScheduleDescription = "",
    string ScheduleSource = "default",
    bool WaitingForWindow = false);

/// <summary>The backup window (quiet hours) as <c>status</c> reports it.</summary>
/// <param name="Configured">A <c>backupWindow</c> section exists (it may be switched off).</param>
/// <param name="Active">The window is enabled and valid, so automatic work is restricted to it.</param>
/// <param name="OpenNow">Automatic work may start now (always true without an active window).</param>
/// <param name="NextOpenUtc">When the window next opens, when it is closed now.</param>
public sealed record BackupWindowStatus(
    bool Configured, bool Active, string? Start, string? End, string? Days, bool OpenNow, DateTime? NextOpenUtc, string Description);

/// <summary>The last recorded verification activity.</summary>
public sealed record VerifyStatus(DateTime? LastVerifiedUtc, ActivityResult? LastResult, string? LastMessage);

/// <summary>
/// What <c>restore.exe status</c> prints: the protection configuration, per-folder backup state, when the next backup is
/// due, the last verification, whether the repository is busy and which folders are stale. Built from local state only.
/// </summary>
public sealed class ProtectionStatus
{
    public DateTime GeneratedUtc { get; init; }
    public string StorageType { get; init; } = "";
    public bool EncryptionEnabled { get; init; }
    public bool AutoStart { get; init; }
    public TimeSpan BackupInterval { get; init; }
    public bool SystemBackupEnabled { get; init; }
    public TimeSpan SystemBackupInterval { get; init; }
    public DateTime? LastSystemBackupUtc { get; init; }
    public bool RetentionEnabled { get; init; }
    public DateTime? LastRetentionRunUtc { get; init; }
    public bool VerificationEnabled { get; init; }
    public string VerificationMode { get; init; } = "full";
    public int VerificationSamplePercent { get; init; }
    public TimeSpan VerifyInterval { get; init; }
    public VerifyStatus Verify { get; init; } = new(null, null, null);
    public bool RepositoryBusy { get; init; }
    public string? RepositoryBusyReason { get; init; }
    public DateTime? NextDueUtc { get; init; }

    /// <summary>The system backup's own schedule expression, or null when it follows <see cref="SystemBackupInterval"/>.</summary>
    public string? SystemBackupSchedule { get; init; }
    public string SystemBackupScheduleDescription { get; init; } = "";
    public DateTime? SystemBackupNextDueUtc { get; init; }
    public BackupWindowStatus Window { get; init; } = new(false, false, null, null, null, true, null, "No backup window: automatic work may start at any time.");
    public List<GroupStatus> Groups { get; init; } = [];

    public List<GroupStatus> StaleGroups => [.. Groups.Where(group => group.Stale)];
}

public static class ProtectionStatusBuilder
{
    /// <summary>
    /// Whether another operation (in this process or another ReStore process) holds the repository. Takes and immediately
    /// releases the mutation lease without waiting, so it never blocks and never reloads state.
    /// </summary>
    public static async Task<(bool Busy, string? Reason)> ProbeBusyAsync(RepositoryCoordinator coordinator, CancellationToken cancellationToken = default)
    {
        var lease = await coordinator.TryAcquireMutationAsync(TimeSpan.Zero, "Checking repository status", cancellationToken).ConfigureAwait(false);
        if (lease == null)
        {
            return (true, coordinator.CurrentReason ?? "another ReStore process is using the repository");
        }

        await lease.DisposeAsync().ConfigureAwait(false);
        return (false, null);
    }

    private static BackupWindowStatus BuildWindowStatus(BackupWindowConfig? configured, BackupWindow? window, bool closedNow, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (configured == null)
        {
            return new BackupWindowStatus(false, false, null, null, null, true, null, "No backup window: automatic work may start at any time.");
        }

        if (window == null)
        {
            var reason = configured.Enabled ? "the backup window is invalid and ignored" : "the backup window is switched off";
            return new BackupWindowStatus(true, false, configured.Start, configured.End, configured.Days, true, null, $"No restriction: {reason}.");
        }

        return new BackupWindowStatus(
            true, true, window.StartText, window.EndText, window.DaysText, !closedNow, closedNow ? window.NextOpenAtOrAfter(nowUtc, zone) : null, window.Describe());
    }

    /// <param name="scheduler">Supplies the due times, so the report uses the scheduler's own rules. It need not be started.</param>
    /// <param name="busyReason">Null when the repository is free (or its state is unknown); otherwise what holds it.</param>
    public static ProtectionStatus Build(
        IConfigManager config, SystemState state, BackupScheduler scheduler, DateTime nowUtc, bool repositoryBusy = false, string? busyReason = null)
    {
        var perGroupDue = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var nextDue = scheduler.ComputeNextRunUtc(perGroupDue);
        var interval = config.BackupInterval;
        var window = scheduler.CurrentWindow();
        var windowClosed = window != null && !window.IsOpen(nowUtc, scheduler.ScheduleTimeZone);

        var failures = state.GetRecentActivity()
            .Where(entry => entry.Type == ActivityType.Backup && entry.Result == ActivityResult.Failed)
            .ToList();

        var groups = new List<GroupStatus>();
        foreach (var watch in config.WatchDirectories.ToList())
        {
            if (string.IsNullOrWhiteSpace(watch.Path))
            {
                continue;
            }

            var group = BackupScheduler.NormalizeGroup(watch.ExpandedPath);
            var lastSnapshot = state.GetLastBackupTimeForGroup(group);
            var lastSuccess = state.GetLastSuccessfulRunUtc(group);
            var lastRun = state.GetRecentBackupRuns()
                .FirstOrDefault(run => string.Equals(BackupScheduler.NormalizeGroup(run.Group), group, StringComparison.OrdinalIgnoreCase));
            DateTime? due = perGroupDue.TryGetValue(group, out var d) ? d : null;
            var neverBackedUp = due == DateTime.MinValue;
            var own = BackupScheduler.OwnSchedule(watch.Schedule);
            var folderSchedule = scheduler.ResolveFolderSchedule(watch);

            // The due time is the schedule's; when the backup window is closed at that moment the folder starts when it opens.
            DateTime? startsAt = due == null || neverBackedUp ? null : scheduler.EffectiveStartUtc(due.Value, nowUtc);

            groups.Add(new GroupStatus(
                watch.Path,
                string.IsNullOrWhiteSpace(watch.StorageType) ? config.GlobalStorageType : watch.StorageType,
                Directory.Exists(watch.ExpandedPath),
                state.GetBackupsForGroup(group).Count(backup => backup.ArtifactType == BackupArtifactType.SnapshotManifest),
                lastSnapshot == DateTime.MinValue ? null : lastSnapshot,
                lastSuccess == DateTime.MinValue ? null : lastSuccess,
                lastRun,
                neverBackedUp ? null : startsAt,
                due != null && due <= nowUtc,
                BackupStaleness.IntervalFor(watch.Schedule, interval, nowUtc, scheduler.ScheduleTimeZone) is { } staleAfter && BackupStaleness.IsStale(lastSuccess, staleAfter, nowUtc),
                failures.FirstOrDefault(entry => string.Equals(BackupScheduler.NormalizeGroup(entry.Target), group, StringComparison.OrdinalIgnoreCase)),
                own?.Expression,
                own?.Description ?? (folderSchedule != null ? folderSchedule.Description : "Off (backupInterval is not positive)"),
                own != null ? "folder" : "default",
                due != null && due <= nowUtc && windowClosed));
        }

        var verifyActivity = state.GetRecentActivity().FirstOrDefault(entry => entry.Type == ActivityType.Verify
            && !entry.Target.StartsWith("Test restore", StringComparison.Ordinal));
        var lastVerified = state.SnapshotVerificationTimes.Count == 0 ? DateTime.MinValue : state.SnapshotVerificationTimes.Values.Max();

        var lastSystem = new[]
        {
            state.GetLastBackupTimeForGroup("system_programs"),
            state.GetLastBackupTimeForGroup("system_environment"),
            state.GetLastBackupTimeForGroup("system_settings")
        }.Max();

        return new ProtectionStatus
        {
            GeneratedUtc = nowUtc,
            StorageType = config.GlobalStorageType,
            EncryptionEnabled = config.Encryption.Enabled,
            AutoStart = config.Protection.AutoStart,
            BackupInterval = interval,
            SystemBackupEnabled = config.SystemBackup.Enabled,
            SystemBackupInterval = config.SystemBackup.BackupInterval,
            LastSystemBackupUtc = lastSystem == DateTime.MinValue ? null : lastSystem,
            RetentionEnabled = config.Retention.Enabled,
            LastRetentionRunUtc = state.LastRetentionRunUtc == DateTime.MinValue ? null : state.LastRetentionRunUtc,
            VerificationEnabled = config.Verification.Enabled,
            VerificationMode = config.Verification.Mode,
            VerificationSamplePercent = config.Verification.SamplePercent,
            VerifyInterval = config.Verification.VerifyInterval,
            Verify = new VerifyStatus(
                verifyActivity?.EndUtc ?? (lastVerified == DateTime.MinValue ? null : lastVerified),
                verifyActivity?.Result,
                verifyActivity?.Message),
            RepositoryBusy = repositoryBusy,
            RepositoryBusyReason = busyReason,
            NextDueUtc = nextDue,
            SystemBackupSchedule = BackupScheduler.OwnSchedule(config.SystemBackup.Schedule)?.Expression,
            SystemBackupScheduleDescription = scheduler.ResolveSystemSchedule()?.Description ?? "Off",
            SystemBackupNextDueUtc = scheduler.ComputeSystemNextDueUtc(nowUtc),
            Window = BuildWindowStatus(config.BackupWindow, window, windowClosed, nowUtc, scheduler.ScheduleTimeZone),
            Groups = groups
        };
    }
}

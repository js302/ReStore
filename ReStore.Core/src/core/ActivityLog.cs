using ReStore.Core.src.telemetry;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

/// <summary>
/// What kind of operation an <see cref="ActivityEntry"/> records. Backup runs are recorded as <see cref="BackupRunReport"/>
/// and only appear as <see cref="Backup"/> in the merged <see cref="ActivityTimeline"/>.
/// </summary>
public enum ActivityType
{
    Restore,
    Verify,
    SystemBackup,
    SystemRestore,
    Repair,
    Backup
}

public enum ActivityResult
{
    Succeeded,

    /// <summary>Finished, but with something the user should look at (for example skipped items).</summary>
    Warning,
    Failed,
    Cancelled
}

/// <summary>
/// One finished operation in the bounded activity log kept in <see cref="SystemState"/>. Plain settable properties:
/// it is persisted as JSON and never mutated after creation.
/// </summary>
public sealed class ActivityEntry
{
    public ActivityType Type { get; set; }

    /// <summary>What the operation worked on: a snapshot path, a backup type, a storage name.</summary>
    public string Target { get; set; } = "";

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public ActivityResult Result { get; set; }
    public string Message { get; set; } = "";

    public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
}

/// <summary>
/// Records an operation in the activity log. <c>using var activity = ActivityRecorder.Begin(...)</c> at the start,
/// then <see cref="ActivityScope.Succeeded"/> / <see cref="ActivityScope.Warning"/> / <see cref="ActivityScope.Failed"/>
/// where the outcome is known. A scope disposed without an outcome records <see cref="ActivityResult.Cancelled"/> if the
/// token was cancelled, otherwise <see cref="ActivityResult.Failed"/> (the operation threw). Recording is in memory;
/// the next state save persists it.
/// </summary>
public static class ActivityRecorder
{
    public static ActivityScope Begin(SystemState? state, ActivityType type, string target, CancellationToken cancellationToken = default)
        => new(state, type, target, cancellationToken);

    /// <summary>
    /// Records an operation that ended in an exception: cancelled for <see cref="OperationCanceledException"/>,
    /// otherwise failed with the message. Does nothing for <see cref="RepositoryBusyException"/> (it never started).
    /// Pass a null exception for a cancellation.
    /// </summary>
    public static void RecordFailure(SystemState? state, ActivityType type, string target, DateTime startUtc, Exception? exception)
    {
        if (exception is RepositoryBusyException)
        {
            return;
        }

        var cancelled = exception is null or OperationCanceledException;
        TryRecord(state, new ActivityEntry
        {
            Type = type,
            Target = target,
            StartUtc = startUtc,
            EndUtc = DateTime.UtcNow,
            Result = cancelled ? ActivityResult.Cancelled : ActivityResult.Failed,
            Message = cancelled ? "Cancelled." : exception!.Message
        });
    }

    /// <summary>
    /// Saves the state (activity included) under a short mutation lease, for callers that record activity but do not
    /// otherwise save. Skips quietly when another operation holds the lease or the save fails; the entry then goes out
    /// with the next save.
    /// </summary>
    public static async Task TryPersistAsync(SystemState state, RepositoryCoordinator? coordinator = null, TimeSpan? wait = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            var lease = await (coordinator ?? RepositoryCoordinator.Instance)
                .TryAcquireMutationAsync(wait ?? TimeSpan.FromSeconds(2), "Saving activity", CancellationToken.None, state).ConfigureAwait(false);
            if (lease == null)
            {
                return;
            }

            await using (lease.ConfigureAwait(false))
            {
                await state.SaveStateAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Informational data; never fail the caller.
        }
    }

    internal static void TryRecord(SystemState? state, ActivityEntry entry)
    {
        // Anonymous counters (type, result, duration bucket); never the target or message, which contain paths.
        Telemetry.TrackActivity(entry);

        try
        {
            state?.RecordActivity(entry);
        }
        catch (Exception)
        {
            // The activity log is informational; it must never turn a finished operation into a failure.
        }
    }
}

public sealed class ActivityScope(SystemState? state, ActivityType type, string target, CancellationToken cancellationToken) : IDisposable
{
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private ActivityResult? _result;
    private string _message = "";
    private bool _recorded;

    public void Succeeded(string? message = null) => Set(ActivityResult.Succeeded, message);

    public void Warning(string? message) => Set(ActivityResult.Warning, message);

    public void Failed(string? message) => Set(ActivityResult.Failed, message);

    private void Set(ActivityResult result, string? message)
    {
        _result = result;
        _message = message ?? "";
    }

    public void Dispose()
    {
        if (_recorded)
        {
            return;
        }

        _recorded = true;
        var result = _result;
        var message = _message;
        if (result == null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result = ActivityResult.Cancelled;
                message = "Cancelled.";
            }
            else
            {
                result = ActivityResult.Failed;
                if (string.IsNullOrEmpty(message))
                {
                    message = "Did not complete (see the log for details).";
                }
            }
        }

        ActivityRecorder.TryRecord(state, new ActivityEntry
        {
            Type = type,
            Target = target,
            StartUtc = _startUtc,
            EndUtc = DateTime.UtcNow,
            Result = result.Value,
            Message = message
        });
    }
}

/// <summary>One row of the Activity page: a backup run or a recorded operation.</summary>
public sealed record ActivityItem(
    ActivityType Type, string Target, DateTime StartUtc, DateTime EndUtc, ActivityResult Result, string Message, string? StorageType = null)
{
    public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;

    /// <summary>Plain-text block for "Copy details".</summary>
    public string ToDetailsText()
    {
        var lines = new List<string>
        {
            $"Operation: {Type}",
            $"Target: {Target}",
            $"Result: {Result}",
            $"Started: {DisplayTime.Format(StartUtc)} ({DisplayTime.Tooltip(StartUtc)})",
            $"Finished: {DisplayTime.Format(EndUtc)} ({DisplayTime.Tooltip(EndUtc)})",
            $"Duration: {(int)Duration.TotalSeconds}s"
        };
        if (!string.IsNullOrWhiteSpace(StorageType))
        {
            lines.Add($"Storage: {StorageType}");
        }

        if (!string.IsNullOrWhiteSpace(Message))
        {
            lines.Add($"Details: {Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Merges backup run reports and the activity log into one newest-first list, optionally filtered.</summary>
public static class ActivityTimeline
{
    public static List<ActivityItem> Build(SystemState state, ActivityType? type = null, ActivityResult? result = null, int max = 250)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Build(state.GetRecentBackupRuns(), state.GetRecentActivity(), type, result, max);
    }

    public static List<ActivityItem> Build(
        IEnumerable<BackupRunReport> runs, IEnumerable<ActivityEntry> entries, ActivityType? type = null, ActivityResult? result = null, int max = 250)
    {
        var items = new List<ActivityItem>();
        items.AddRange(runs.Select(FromRun));
        items.AddRange(entries.Select(e => new ActivityItem(e.Type, e.Target, e.StartUtc, e.EndUtc, e.Result, e.Message)));

        return [.. items
            .Where(i => (type == null || i.Type == type) && (result == null || i.Result == result))
            .OrderByDescending(i => i.EndUtc)
            .Take(Math.Max(0, max))];
    }

    private static ActivityItem FromRun(BackupRunReport run)
    {
        var end = run.TimestampUtc;
        var message = run.Summary + (run.BytesUploaded > 0 ? $"; {ByteFormatter.Format(run.BytesUploaded)} uploaded" : "");
        return new ActivityItem(
            ActivityType.Backup, run.Group, end.AddMilliseconds(-Math.Max(0, run.DurationMs)), end,
            run.FilesSkipped > 0 ? ActivityResult.Warning : ActivityResult.Succeeded, message, run.StorageType);
    }
}

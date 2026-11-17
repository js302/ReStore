using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.monitoring;

/// <summary>How worried the status bar should look.</summary>
public enum GlobalStatusLevel
{
    Idle,
    Busy,
    Warning,
    Error
}

/// <summary>The four pieces of text in the global status bar, already formatted.</summary>
public sealed record GlobalStatusText(string Operation, string Protection, string NextRun, string LastBackup, GlobalStatusLevel Level);

/// <summary>Pure formatting of the global status bar, so it can be unit tested without the GUI.</summary>
public static class GlobalStatus
{
    public static GlobalStatusText Build(
        string? busyReason,
        TimeSpan busyElapsed,
        ProtectionState protection,
        DateTime? pausedUntilUtc,
        DateTime? nextRunUtc,
        BackupRunReport? lastRun,
        bool nextRunHeldByWindow = false)
    {
        var paused = protection == ProtectionState.Stopped && pausedUntilUtc != null;
        var level = busyReason != null ? GlobalStatusLevel.Busy : GlobalStatusLevel.Idle;
        if (protection == ProtectionState.PausedNeedsPassword && level == GlobalStatusLevel.Idle)
        {
            level = GlobalStatusLevel.Warning;
        }

        if (protection == ProtectionState.Error)
        {
            level = GlobalStatusLevel.Error;
        }

        var operation = busyReason != null ? $"{busyReason} ({FormatElapsed(busyElapsed)})" : "Idle";
        var protectionText = protection switch
        {
            ProtectionState.Running => "Protection on",
            ProtectionState.PausedNeedsPassword => "Needs password",
            ProtectionState.Error => "Protection error",
            _ => paused ? "Protection paused" : "Protection off"
        };

        string nextRun;
        if (protection is ProtectionState.Running or ProtectionState.PausedNeedsPassword && nextRunUtc is { } next)
        {
            nextRun = NextRunText(next, nextRunHeldByWindow);
        }
        else if (paused && pausedUntilUtc is { } until)
        {
            nextRun = ProtectionPause.IsIndefinite(until) ? "Paused until resumed" : $"Resumes {DisplayTime.Format(until)}";
        }
        else
        {
            nextRun = "No run scheduled";
        }

        var lastBackup = lastRun == null
            ? "No backups yet"
            : $"Last backup {DisplayTime.Format(lastRun.TimestampUtc)}: "
                + (lastRun.FilesSkipped > 0 ? $"done, {lastRun.FilesSkipped} skipped" : "OK");

        return new GlobalStatusText(operation, protectionText, nextRun, lastBackup, level);
    }

    /// <summary>
    /// "Next run 22:00", and when the schedule would have run earlier but the backup window is closed until then, "Next run 22:00
    /// (waiting for the backup window)": without the note a start hours after the due time looks like a stuck scheduler.
    /// </summary>
    public static string NextRunText(DateTime nextRunUtc, bool heldByWindow)
        => $"Next run {DisplayTime.Format(nextRunUtc)}" + (heldByWindow ? " (waiting for the backup window)" : string.Empty);

    /// <summary>m:ss, or h:mm:ss from an hour.</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }
}

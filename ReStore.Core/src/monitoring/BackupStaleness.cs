using ReStore.Core.src.core;

namespace ReStore.Core.src.monitoring;

/// <summary>A watched folder that has gone without a successful backup for too long.</summary>
public sealed record StaleGroup(string Group, DateTime LastSuccessUtc, TimeSpan Age);

/// <summary>
/// The "no successful backup for 2x the interval" rule behind the Dashboard banner and the staleness
/// notification. Success includes a run that found nothing to back up, so a quiet folder is not stale.
/// </summary>
public static class BackupStaleness
{
    /// <summary>Threshold when no interval is configured.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromDays(2);

    /// <summary>Floor, so a one-minute test interval does not raise an alert after two minutes.</summary>
    public static readonly TimeSpan MinimumThreshold = TimeSpan.FromHours(1);

    public static TimeSpan Threshold(TimeSpan interval)
        => interval <= TimeSpan.Zero
            ? DefaultThreshold
            : TimeSpan.FromTicks(Math.Max(interval.Ticks * 2, MinimumThreshold.Ticks));

    /// <summary>
    /// True when the last success is older than the threshold. A group that has never succeeded is not
    /// reported here: the Dashboard already lists it as never backed up, and a just-added folder must not alarm.
    /// </summary>
    public static bool IsStale(DateTime lastSuccessUtc, TimeSpan interval, DateTime nowUtc)
        => lastSuccessUtc != DateTime.MinValue && nowUtc - lastSuccessUtc > Threshold(interval);

    /// <summary>
    /// The interval the staleness rule measures a folder against: the longest gap between its scheduled runs when it has a clock
    /// schedule (a weekday schedule is allowed to be quiet over the weekend), its own interval for <c>every</c>, the global
    /// <paramref name="globalInterval"/> otherwise. Null for <c>off</c>: the watcher alone backs the folder up, so a quiet folder is never stale.
    /// </summary>
    public static TimeSpan? IntervalFor(string? schedule, TimeSpan globalInterval, DateTime nowUtc, TimeZoneInfo? timeZone = null)
    {
        if (!string.IsNullOrWhiteSpace(schedule) && BackupSchedule.TryParse(schedule, out var parsed, out _))
        {
            return parsed.IsOff ? null : parsed.LongestGap(nowUtc, timeZone) ?? globalInterval;
        }

        return globalInterval;
    }

    /// <summary>Like <see cref="Evaluate(IEnumerable{string}, SystemState, TimeSpan, DateTime)"/> with an interval per group (null = never stale).</summary>
    public static List<StaleGroup> Evaluate(IEnumerable<(string Group, TimeSpan? Interval)> groups, SystemState state, DateTime nowUtc)
    {
        var stale = new List<StaleGroup>();
        foreach (var (group, interval) in groups)
        {
            if (interval == null)
            {
                continue;
            }

            var last = state.GetLastSuccessfulRunUtc(group);
            if (IsStale(last, interval.Value, nowUtc))
            {
                stale.Add(new StaleGroup(group, last, nowUtc - last));
            }
        }

        return stale;
    }

    public static List<StaleGroup> Evaluate(IEnumerable<string> groups, SystemState state, TimeSpan interval, DateTime nowUtc)
    {
        var stale = new List<StaleGroup>();
        foreach (var group in groups)
        {
            var last = state.GetLastSuccessfulRunUtc(group);
            if (IsStale(last, interval, nowUtc))
            {
                stale.Add(new StaleGroup(group, last, nowUtc - last));
            }
        }

        return stale;
    }
}

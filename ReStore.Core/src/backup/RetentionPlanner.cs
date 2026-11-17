using System.Globalization;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.backup;

/// <summary>What the planner decided for one snapshot, with the reasons behind it.</summary>
public sealed record RetentionDecision(BackupInfo Backup, string StorageType, bool Keep, IReadOnlyList<string> Reasons);

/// <summary>The planner result: every decision (per storage type, newest first) plus the snapshots to delete.</summary>
public sealed class RetentionPlan(IReadOnlyList<RetentionDecision> decisions)
{
    public IReadOnlyList<RetentionDecision> Decisions { get; } = decisions;

    public List<BackupInfo> ToDelete => [.. Decisions.Where(d => !d.Keep).Select(d => d.Backup)];
}

/// <summary>
/// Pure retention planning: no I/O, no clock, no storage. Snapshots are planned independently per storage type
/// (an empty StorageType means <c>defaultStorageType</c>), so a snapshot on one storage never keeps or deletes one on another.
/// <para>Rules (see docs/configuration.md): newest snapshot per storage and labelled snapshots are always kept.
/// Otherwise the rules are keep-last-N (always on, minimum 1), keep-within-days (0 = off) and the GFS tiers
/// hourly/daily/weekly/monthly/yearly (0 = off). A tier keeps the newest snapshot of each of the last N distinct
/// buckets that contain a snapshot, in local time (restic semantics; weeks are ISO weeks).
/// Mode "any" keeps a snapshot if any rule keeps it; mode "all" keeps it only if every enabled rule keeps it.</para>
/// </summary>
public static class RetentionPlanner
{
    public static RetentionPlan Plan(
        IEnumerable<BackupInfo> snapshots, RetentionConfig policy, DateTime now, string defaultStorageType = "", TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(policy);
        var zone = timeZone ?? TimeZoneInfo.Local;

        var decisions = new List<RetentionDecision>();
        var series = snapshots
            .Where(b => !string.IsNullOrWhiteSpace(b.Path))
            .GroupBy(b => string.IsNullOrWhiteSpace(b.StorageType) ? defaultStorageType : b.StorageType!, StringComparer.OrdinalIgnoreCase);
        foreach (var storageSeries in series)
        {
            decisions.AddRange(PlanSeries(storageSeries.Key, [.. storageSeries.OrderByDescending(b => b.Timestamp)], policy, now, zone));
        }

        return new RetentionPlan(decisions);
    }

    private static List<RetentionDecision> PlanSeries(string storageType, List<BackupInfo> ordered, RetentionConfig policy, DateTime now, TimeZoneInfo zone)
    {
        var count = ordered.Count;
        var all = string.Equals(policy.Mode, RetentionModes.All, StringComparison.OrdinalIgnoreCase);

        // Each rule: name and the set of indexes (into ordered) it keeps. keep-last is always enabled, minimum 1.
        var rules = new List<(string Name, HashSet<int> Kept)>();

        var keepLast = Math.Max(1, policy.KeepLastPerDirectory);
        rules.Add(($"last {keepLast}", [.. Enumerable.Range(0, Math.Min(keepLast, count))]));

        if (policy.MaxAgeDays > 0)
        {
            var cutoff = now.ToUniversalTime().AddDays(-policy.MaxAgeDays);
            var within = new HashSet<int>();
            for (var i = 0; i < count; i++)
            {
                if (ordered[i].Timestamp.ToUniversalTime() >= cutoff)
                {
                    within.Add(i);
                }
            }

            rules.Add(($"within {policy.MaxAgeDays} days", within));
        }

        AddTier(rules, ordered, policy.KeepHourly, "hourly", zone, (t, o) => $"{t.ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture)}@{o}");
        AddTier(rules, ordered, policy.KeepDaily, "daily", zone, (t, _) => t.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        AddTier(rules, ordered, policy.KeepWeekly, "weekly", zone, (t, _) => $"{ISOWeek.GetYear(t)}-W{ISOWeek.GetWeekOfYear(t)}");
        AddTier(rules, ordered, policy.KeepMonthly, "monthly", zone, (t, _) => t.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture));
        AddTier(rules, ordered, policy.KeepYearly, "yearly", zone, (t, _) => t.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture));

        var result = new List<RetentionDecision>(count);
        for (var i = 0; i < count; i++)
        {
            var reasons = new List<string>();
            if (i == 0)
            {
                reasons.Add("newest snapshot");
            }

            if (!string.IsNullOrWhiteSpace(ordered[i].Label))
            {
                reasons.Add($"labelled '{ordered[i].Label}'");
            }

            var protectedSnapshot = reasons.Count > 0;
            var matched = rules.Where(r => r.Kept.Contains(i)).Select(r => r.Name).ToList();
            var missed = rules.Where(r => !r.Kept.Contains(i)).Select(r => r.Name).ToList();

            bool keep;
            if (protectedSnapshot)
            {
                keep = true;
                reasons.AddRange(matched.Select(n => $"keep {n}"));
            }
            else if (all)
            {
                keep = missed.Count == 0;
                reasons.AddRange(keep
                    ? matched.Select(n => $"keep {n}")
                    : missed.Select(n => $"not kept by {n} (mode all)"));
            }
            else
            {
                keep = matched.Count > 0;
                reasons.AddRange(keep ? matched.Select(n => $"keep {n}") : ["not kept by any rule"]);
            }

            result.Add(new RetentionDecision(ordered[i], storageType, keep, reasons));
        }

        return result;
    }

    private static void AddTier(
        List<(string Name, HashSet<int> Kept)> rules, List<BackupInfo> ordered, int limit, string name, TimeZoneInfo zone,
        Func<DateTime, TimeSpan, string> bucketKey)
    {
        if (limit <= 0)
        {
            return;
        }

        var kept = new HashSet<int>();
        var seen = new HashSet<string>();
        for (var i = 0; i < ordered.Count && seen.Count < limit; i++)
        {
            var utc = ordered[i].Timestamp.ToUniversalTime();
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            var key = bucketKey(local, zone.GetUtcOffset(utc));
            if (seen.Add(key))
            {
                kept.Add(i);
            }
        }

        rules.Add(($"{name} {limit}", kept));
    }
}

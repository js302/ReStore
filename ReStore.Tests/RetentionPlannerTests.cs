using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using System.Reflection;
using System.Text.Json;

namespace ReStore.Tests;

public class RetentionPlannerTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A fixed US-Eastern-like zone (UTC-5, +1h DST from the second Sunday of March to the first Sunday of November,
    /// 02:00 local), built by hand so these tests never depend on the OS tz database.
    /// </summary>
    private static TimeZoneInfo NewYork()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test/Eastern", TimeSpan.FromHours(-5), "Test Eastern", "EST", "EDT", [rule]);
    }

    private static BackupInfo Snap(DateTime utc, string? storage = "local", string? label = null, string? name = null) => new()
    {
        Path = name ?? $"snap_{utc:yyyyMMddHHmmss}_{storage}",
        Timestamp = DateTime.SpecifyKind(utc, DateTimeKind.Utc),
        StorageType = storage,
        Label = label
    };

    private static List<string> Deleted(IEnumerable<BackupInfo> snaps, RetentionConfig policy, TimeZoneInfo? zone = null) =>
        [.. RetentionPlanner.Plan(snaps, policy, Now, "local", zone ?? TimeZoneInfo.Utc).ToDelete.Select(b => b.Path)];

    private static RetentionConfig Policy(int last = 1, int days = 0, int h = 0, int d = 0, int w = 0, int m = 0, int y = 0, string mode = "any") => new()
    {
        Enabled = true, KeepLastPerDirectory = last, MaxAgeDays = days, KeepHourly = h, KeepDaily = d, KeepWeekly = w, KeepMonthly = m, KeepYearly = y, Mode = mode
    };

    [Fact]
    public void KeepLast_KeepsNewestN()
    {
        var snaps = Enumerable.Range(0, 5).Select(i => Snap(Now.AddHours(-i))).ToList();
        Deleted(snaps, Policy(last: 2)).Should().BeEquivalentTo(snaps.Skip(2).Select(s => s.Path));
    }

    [Fact]
    public void Hourly_KeepsNewestPerHourBucket()
    {
        var snaps = new[] { Snap(Now.AddMinutes(-5)), Snap(Now.AddMinutes(-35)), Snap(Now.AddHours(-1)), Snap(Now.AddHours(-2).AddMinutes(-10)), Snap(Now.AddHours(-3)) };
        // hour 11: 11:55 kept, 11:25 and 11:00 deleted; hour 09: 09:50 kept, 09:00 deleted
        var deleted = Deleted(snaps, Policy(h: 2));
        deleted.Should().BeEquivalentTo(snaps[1].Path, snaps[2].Path, snaps[4].Path);
    }

    [Fact]
    public void Daily_Weekly_Monthly_Yearly_KeepOnePerBucketNewestFirst()
    {
        var snaps = new List<BackupInfo>
        {
            Snap(new DateTime(2026, 6, 15, 8, 0, 0)), Snap(new DateTime(2026, 6, 15, 1, 0, 0)),   // same day
            Snap(new DateTime(2026, 6, 14, 8, 0, 0)),                                             // Sunday, ISO week 24 (15th is Monday of week 25)
            Snap(new DateTime(2026, 6, 10, 8, 0, 0)),                                             // week 24 too
            Snap(new DateTime(2026, 5, 31, 8, 0, 0)), Snap(new DateTime(2026, 5, 2, 8, 0, 0)),
            Snap(new DateTime(2025, 12, 31, 8, 0, 0)), Snap(new DateTime(2025, 1, 5, 8, 0, 0)),
        };

        Deleted(snaps, Policy(d: 2)).Should().BeEquivalentTo(snaps.Where((_, i) => i is not (0 or 2)).Select(s => s.Path));
        Deleted(snaps, Policy(w: 2)).Should().BeEquivalentTo(snaps.Where((_, i) => i is not (0 or 2)).Select(s => s.Path));
        Deleted(snaps, Policy(m: 2)).Should().BeEquivalentTo(snaps.Where((_, i) => i is not (0 or 4)).Select(s => s.Path));
        Deleted(snaps, Policy(y: 2)).Should().BeEquivalentTo(snaps.Where((_, i) => i is not (0 or 6)).Select(s => s.Path));
    }

    [Fact]
    public void Weekly_UsesIsoWeeksAcrossYearBoundary()
    {
        // 2026-01-01 (Thu) is ISO week 1 of 2026; 2025-12-29 (Mon) is also week 1 of 2026; 2025-12-28 (Sun) is week 52 of 2025.
        var snaps = new[] { Snap(new DateTime(2026, 1, 1, 9, 0, 0)), Snap(new DateTime(2025, 12, 29, 9, 0, 0)), Snap(new DateTime(2025, 12, 28, 9, 0, 0)) };
        Deleted(snaps, Policy(w: 2)).Should().BeEquivalentTo(snaps[1].Path);
    }

    [Fact]
    public void Monthly_BucketsAcrossMonthBoundaryInLocalTime()
    {
        var zone = NewYork();
        // 2026-07-01 02:00Z is June 30 22:00 in New York (EDT): same local month as June 15, not July.
        var snaps = new[] { Snap(new DateTime(2026, 7, 1, 2, 0, 0)), Snap(new DateTime(2026, 6, 15, 12, 0, 0)), Snap(new DateTime(2026, 5, 20, 12, 0, 0)) };
        Deleted(snaps, Policy(m: 2), zone).Should().BeEquivalentTo(snaps[1].Path);
        Deleted(snaps, Policy(m: 2), TimeZoneInfo.Utc).Should().BeEquivalentTo(snaps[2].Path);
    }

    [Fact]
    public void Hourly_DstFallBackRepeatedHourIsTwoBuckets_DailyIsOne()
    {
        var zone = NewYork();
        // 2026-11-01: 01:30 EDT = 05:30Z and 01:30 EST = 06:30Z. Same local clock hour, different real hours.
        var snaps = new[] { Snap(new DateTime(2026, 11, 1, 6, 30, 0)), Snap(new DateTime(2026, 11, 1, 5, 30, 0)) };
        Deleted(snaps, Policy(h: 2), zone).Should().BeEmpty();
        Deleted(snaps, Policy(d: 2), zone).Should().BeEquivalentTo(snaps[1].Path);
    }

    [Fact]
    public void LabelledAndNewestAreAlwaysKept_EvenInAllMode()
    {
        var snaps = new[] { Snap(Now.AddDays(-400), label: "release"), Snap(Now.AddDays(-300)), Snap(Now.AddDays(-1)) };
        foreach (var mode in new[] { "any", "all" })
        {
            Deleted(snaps, Policy(last: 1, days: 5, mode: mode)).Should().BeEquivalentTo(snaps[1].Path);
        }
    }

    [Fact]
    public void AnyMode_IsUnion_AllMode_IsIntersection()
    {
        var snaps = new[] { Snap(Now), Snap(Now.AddDays(-1)), Snap(Now.AddDays(-2)), Snap(Now.AddDays(-20)) };
        // last 2 {0,1}; within 3 days {0,1,2}; => any keeps 0,1,2 ; all keeps 0,1
        Deleted(snaps, Policy(last: 2, days: 3)).Should().BeEquivalentTo(snaps[3].Path);
        Deleted(snaps, Policy(last: 2, days: 3, mode: "all")).Should().BeEquivalentTo(snaps[2].Path, snaps[3].Path);
    }

    [Fact]
    public void AllMode_IncludesTiersAsRules()
    {
        var snaps = new[] { Snap(Now), Snap(Now.AddHours(-1)), Snap(Now.AddDays(-1)), Snap(Now.AddDays(-2)) };
        // last 3 {0,1,2}; daily 2 {0 (day 15), 2 (day 14)} => all: 0 and 2 survive
        Deleted(snaps, Policy(last: 3, d: 2, mode: "all")).Should().BeEquivalentTo(snaps[1].Path, snaps[3].Path);
        Deleted(snaps, Policy(last: 3, d: 2)).Should().BeEquivalentTo(snaps[3].Path);
    }

    [Fact]
    public void TiersAreIndependentPerStorageType()
    {
        var local = Enumerable.Range(0, 3).Select(i => Snap(Now.AddDays(-i), "local")).ToList();
        var s3 = Enumerable.Range(0, 3).Select(i => Snap(Now.AddDays(-i).AddHours(-1), "s3")).ToList();
        var plan = RetentionPlanner.Plan(local.Concat(s3), Policy(last: 1), Now, "local", TimeZoneInfo.Utc);
        plan.ToDelete.Select(b => b.Path).Should().BeEquivalentTo(local.Skip(1).Concat(s3.Skip(1)).Select(b => b.Path));
        plan.Decisions.Where(d => d.Keep).Select(d => d.StorageType).Should().BeEquivalentTo("local", "s3");
    }

    [Fact]
    public void EmptyStorageTypeCountsAsDefaultStorage()
    {
        var snaps = new[] { Snap(Now, null, name: "a"), Snap(Now.AddDays(-1), "local", name: "b") };
        Deleted(snaps, Policy(last: 1)).Should().BeEquivalentTo("b");
    }

    [Fact]
    public void DecisionsCarryReasons()
    {
        var snaps = new[] { Snap(Now), Snap(Now.AddDays(-2), label: "x"), Snap(Now.AddDays(-9)) };
        var plan = RetentionPlanner.Plan(snaps, Policy(last: 1, d: 1), Now, "local", TimeZoneInfo.Utc);
        plan.Decisions[0].Reasons.Should().Contain("newest snapshot");
        plan.Decisions[1].Reasons.Should().Contain(r => r.StartsWith("labelled"));
        plan.Decisions[2].Keep.Should().BeFalse();
        plan.Decisions[2].Reasons.Should().ContainSingle().Which.Should().Contain("not kept");
    }

    // ----- backward compatibility: the pre-v2 algorithm, verbatim apart from an injectable clock -----
    private static List<string> LegacySelect(List<BackupInfo> backups, int keepLastConfig, int maxAgeDays, DateTime now)
    {
        if (backups.Count <= 1) return [];
        var ordered = backups.Where(b => !string.IsNullOrWhiteSpace(b.Path)).OrderByDescending(b => b.Timestamp).ToList();
        if (ordered.Count <= 1) return [];
        var keepLast = keepLastConfig < 1 ? 1 : keepLastConfig;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in ordered.Take(keepLast)) keep.Add(b.Path);
        if (maxAgeDays > 0)
        {
            var cutoff = now.AddDays(-maxAgeDays);
            foreach (var b in ordered) if (b.Timestamp.ToUniversalTime() >= cutoff) keep.Add(b.Path);
        }
        foreach (var b in ordered.Where(b => !string.IsNullOrWhiteSpace(b.Label))) keep.Add(b.Path);
        keep.Add(ordered[0].Path);
        return [.. ordered.Where(b => !keep.Contains(b.Path)).Select(b => b.Path)];
    }

    [Fact]
    public void LegacyConfig_ProducesIdenticalDecisionsToTheOldAlgorithm_OnRandomHistories()
    {
        var random = new Random(12345);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var count = random.Next(0, 40);
            var snaps = Enumerable.Range(0, count).Select(i => new BackupInfo
            {
                Path = $"p{i}",
                // Includes future timestamps (clock skew) and, via the coarse 7-minute grid, many equal timestamps.
                Timestamp = Now.AddMinutes(-(random.Next(-3 * 24 * 60, 90 * 24 * 60) / 7 * 7)),
                StorageType = "local",
                Label = random.Next(0, 10) == 0 ? "L" : null,
            }).ToList();
            var keepLast = random.Next(-1, 8);
            var maxAge = random.Next(0, 60);

            var legacy = LegacySelect(snaps, keepLast, maxAge, Now);
            var policy = new RetentionConfig { Enabled = true, KeepLastPerDirectory = keepLast, MaxAgeDays = maxAge };
            var current = RetentionPlanner.Plan(snaps, policy, Now, "local").ToDelete.Select(b => b.Path).ToList();
            current.Should().BeEquivalentTo(legacy, $"iteration {iteration}");

            RetentionManager.SelectBackupsToDelete(snaps, policy, "local", Now).Select(b => b.Path).Should().BeEquivalentTo(legacy);
        }
    }

    // ----- config -----
    [Fact]
    public void ConfigLoad_OldConfigKeepsDefaults_AliasesAndTiersAreRead()
    {
        var manager = new ConfigManager(new Mock<ILogger>().Object);
        var load = typeof(ConfigManager).GetMethod("LoadRetentionSettings", BindingFlags.NonPublic | BindingFlags.Instance)!;

        load.Invoke(manager, [JsonDocument.Parse("""{"retention":{"enabled":true,"keepLastPerDirectory":7,"maxAgeDays":14}}""").RootElement]);
        manager.Retention.KeepLastPerDirectory.Should().Be(7);
        manager.Retention.MaxAgeDays.Should().Be(14);
        manager.Retention.Mode.Should().Be("any");
        manager.Retention.KeepDaily.Should().Be(0);

        load.Invoke(manager, [JsonDocument.Parse("""{"retention":{"enabled":true,"keepLast":3,"keepWithinDays":9,"keepHourly":1,"keepDaily":2,"keepWeekly":3,"keepMonthly":4,"keepYearly":5,"mode":"ALL"}}""").RootElement]);
        var r = manager.Retention;
        (r.KeepLastPerDirectory, r.MaxAgeDays, r.KeepHourly, r.KeepDaily, r.KeepWeekly, r.KeepMonthly, r.KeepYearly, r.Mode)
            .Should().Be((3, 9, 1, 2, 3, 4, 5, "all"));
    }
}

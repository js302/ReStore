using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>H22 / F-5 / M-G1 / M-G2 logic that is WPF-free: staleness rule, deferred notification queue, list sync, time format.</summary>
public class NotificationRulesTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(24, 47, false)]
    [InlineData(24, 49, true)]
    [InlineData(1, 1.5, false)]   // floored at 1 hour so a short interval is not instantly stale
    [InlineData(1, 2.5, true)]
    [InlineData(0, 47, false)]    // no interval: 2 days
    [InlineData(0, 49, true)]
    public void IsStale_UsesTwiceTheIntervalOrTwoDays(double intervalHours, double ageHours, bool expected)
    {
        BackupStaleness.IsStale(Now.AddHours(-ageHours), TimeSpan.FromHours(intervalHours), Now).Should().Be(expected);
    }

    [Fact]
    public void IsStale_NeverBackedUp_IsNotReported()
        => BackupStaleness.IsStale(DateTime.MinValue, TimeSpan.FromHours(24), Now).Should().BeFalse();

    [Fact]
    public void Evaluate_ReturnsOnlyStaleGroups()
    {
        var state = new SystemState();
        state.RecordGroupRunSucceeded("/old", Now.AddDays(-5));
        state.RecordGroupRunSucceeded("/fresh", Now.AddHours(-2));

        var stale = BackupStaleness.Evaluate(["/old", "/fresh", "/never"], state, TimeSpan.FromHours(24), Now);

        stale.Should().ContainSingle().Which.Group.Should().Be("/old");
        stale[0].Age.Should().Be(TimeSpan.FromDays(5));
    }

    [Fact]
    public void Gate_AllowsOncePerDayPerKey()
    {
        var gate = new PeriodicNotificationGate();

        gate.TryAcquire("a", Now).Should().BeTrue();
        gate.TryAcquire("a", Now.AddHours(23)).Should().BeFalse();
        gate.TryAcquire("b", Now.AddHours(1)).Should().BeTrue();
        gate.TryAcquire("a", Now.AddHours(25)).Should().BeTrue();
    }

    private static PendingNotification N(string title, NotificationSeverity s = NotificationSeverity.Info, DateTime? at = null, NotificationTarget t = NotificationTarget.Dashboard)
        => new(title, "m", s, t, at ?? Now);

    [Fact]
    public void Queue_DeduplicatesAndBounds()
    {
        var queue = new NotificationQueue(capacity: 3);
        queue.Enqueue(N("a"));
        queue.Enqueue(N("a"));
        queue.Count.Should().Be(1);

        foreach (var title in new[] { "b", "c", "d" })
        {
            queue.Enqueue(N(title));
        }

        queue.Drain(Now).Select(n => n.Title).Should().Equal("b", "c", "d");
        queue.Count.Should().Be(0);
    }

    [Fact]
    public void Queue_DropsStaleEntriesOnDrain()
    {
        var queue = new NotificationQueue(maxAge: TimeSpan.FromHours(1));
        queue.Enqueue(N("old", at: Now.AddHours(-3)));
        queue.Enqueue(N("new", at: Now.AddMinutes(-5)));

        queue.Drain(Now).Select(n => n.Title).Should().Equal("new");
    }

    [Fact]
    public void Coalesce_SingleIsUnchanged_ManyBecomeWorstSeveritySummary()
    {
        NotificationQueue.Coalesce([], Now).Should().BeNull();

        var single = N("only");
        NotificationQueue.Coalesce([single], Now).Should().BeSameAs(single);

        var summary = NotificationQueue.Coalesce(
            [N("info", NotificationSeverity.Info), N("bad", NotificationSeverity.Error, t: NotificationTarget.Backups)], Now)!;
        summary.Severity.Should().Be(NotificationSeverity.Error);
        summary.Target.Should().Be(NotificationTarget.Backups);
        summary.Title.Should().Contain("2");
        summary.Message.Should().Contain("info").And.Contain("bad");
    }

    [Fact]
    public void CollectionSync_TouchesOnlyDifferingPositions()
    {
        var target = new List<string> { "a", "b", "c", "d" };

        CollectionSync.Apply(target, ["a", "x", "c"], (x, y) => x == y).Should().Be(2); // replace b, remove d

        target.Should().Equal("a", "x", "c");
        CollectionSync.Apply(target, ["a", "x", "c"], (x, y) => x == y).Should().Be(0);
        CollectionSync.Apply(target, ["a", "x", "c", "e"], (x, y) => x == y).Should().Be(1);
    }

    [Fact]
    public void DisplayTime_FormatsLocalAndIsoTooltip()
    {
        var utc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        DisplayTime.Format(utc).Should().Be(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        DisplayTime.Tooltip(utc).Should().Be("2026-03-04T05:06:07Z");
        DisplayTime.Tooltip(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)).Should().Be("2026-03-04T05:06:07Z");
    }
}

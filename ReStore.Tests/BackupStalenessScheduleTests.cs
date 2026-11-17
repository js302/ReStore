using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;

namespace ReStore.Tests;

/// <summary>The "no successful backup for twice the interval" rule with per-folder schedules.</summary>
public class BackupStalenessScheduleTests
{
    private static readonly DateTime Now = new(2025, 1, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Global = TimeSpan.FromHours(1);

    [Theory]
    [InlineData(null, 60)]
    [InlineData("", 60)]
    [InlineData("   ", 60)]
    [InlineData("every 4h", 4 * 60)]
    [InlineData("daily 02:00", 24 * 60)]
    [InlineData("weekdays 18:30", 72 * 60)]
    [InlineData("weekly sun 03:00", 7 * 24 * 60)]
    [InlineData("garbage", 60)]
    public void IntervalFor_IsTheGapOfTheSchedule_OrTheGlobalInterval(string? schedule, int expectedMinutes)
        => BackupStaleness.IntervalFor(schedule, Global, Now, TimeZoneInfo.Utc).Should().Be(TimeSpan.FromMinutes(expectedMinutes));

    [Fact]
    public void IntervalFor_Off_IsNeverStale() => BackupStaleness.IntervalFor("off", Global, Now, TimeZoneInfo.Utc).Should().BeNull();

    [Fact]
    public void Evaluate_UsesTheIntervalOfEachGroup_AndSkipsOffGroups()
    {
        var state = new SystemState(new TestLogger());
        var old = Now.AddHours(-30);
        foreach (var group in new[] { "hourly", "daily", "off" })
        {
            state.RecordGroupRunSucceeded(group, old);
        }

        var stale = BackupStaleness.Evaluate(
            [("hourly", (TimeSpan?)TimeSpan.FromHours(1)), ("daily", TimeSpan.FromDays(1)), ("off", null)], state, Now);

        stale.Select(g => g.Group).Should().Equal("hourly");
    }
}

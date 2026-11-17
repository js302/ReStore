using FluentAssertions;
using ReStore.Core.src.monitoring;

namespace ReStore.Tests;

/// <summary>The quiet-hours window: crossing midnight, whole-day windows, the days filter, inclusive start and exclusive end.</summary>
public class BackupWindowTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTime U(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => BackupScheduleTests.U(year, month, day, hour, minute, second);

    private static BackupWindow W(string start, string end, string? days = null)
        => BackupWindow.TryCreate(start, end, days, out var window, out var error) ? window : throw new InvalidOperationException(error);

    // 2025-01-06 is a Monday.

    [Fact]
    public void CrossingMidnight_IsOpenFromStartInclusive_UntilEndExclusive()
    {
        var window = W("22:00", "06:00");

        window.IsOpen(U(2025, 1, 6, 21, 59, 59), Utc).Should().BeFalse();
        window.IsOpen(U(2025, 1, 6, 22, 0, 0), Utc).Should().BeTrue("the start is inclusive");
        window.IsOpen(U(2025, 1, 6, 23, 59, 59), Utc).Should().BeTrue();
        window.IsOpen(U(2025, 1, 7, 0, 0, 0), Utc).Should().BeTrue("midnight is inside");
        window.IsOpen(U(2025, 1, 7, 5, 59, 59), Utc).Should().BeTrue();
        window.IsOpen(U(2025, 1, 7, 6, 0, 0), Utc).Should().BeFalse("the end is exclusive");
        window.IsOpen(U(2025, 1, 7, 12), Utc).Should().BeFalse();
    }

    [Fact]
    public void SameDayWindow_IsOpenBetweenStartAndEnd()
    {
        var window = W("09:00", "17:00");

        window.IsOpen(U(2025, 1, 6, 8, 59), Utc).Should().BeFalse();
        window.IsOpen(U(2025, 1, 6, 9, 0), Utc).Should().BeTrue();
        window.IsOpen(U(2025, 1, 6, 16, 59, 59), Utc).Should().BeTrue();
        window.IsOpen(U(2025, 1, 6, 17, 0), Utc).Should().BeFalse();
        window.IsOpen(U(2025, 1, 6, 23), Utc).Should().BeFalse();
    }

    [Fact]
    public void StartEqualsEnd_MeansTheWholeDay()
    {
        var window = W("08:00", "08:00");

        window.IsWholeDay.Should().BeTrue();
        window.IsAlwaysOpen.Should().BeTrue();
        foreach (var hour in new[] { 0, 7, 8, 12, 23 })
        {
            window.IsOpen(U(2025, 1, 6, hour), Utc).Should().BeTrue();
        }
    }

    [Fact]
    public void WholeDayWindow_WithADaysFilter_IsOpenOnlyOnThoseDays()
    {
        var window = W("00:00", "00:00", "sat,sun");

        window.IsAlwaysOpen.Should().BeFalse();
        window.IsOpen(U(2025, 1, 4, 12), Utc).Should().BeTrue("Saturday");
        window.IsOpen(U(2025, 1, 5, 23, 59), Utc).Should().BeTrue("Sunday");
        window.IsOpen(U(2025, 1, 6, 0, 0), Utc).Should().BeFalse("Monday");
        window.IsOpen(U(2025, 1, 3, 23, 59), Utc).Should().BeFalse("Friday");
    }

    [Fact]
    public void TheDaysAreTheDaysTheWindowStarts_SoANightCrossingMidnightBelongsToItsStartDay()
    {
        var window = W("22:00", "06:00", "mon");

        window.IsOpen(U(2025, 1, 6, 23), Utc).Should().BeTrue("Monday evening starts the window");
        window.IsOpen(U(2025, 1, 7, 3), Utc).Should().BeTrue("Tuesday 03:00 is still Monday's window");
        window.IsOpen(U(2025, 1, 7, 6), Utc).Should().BeFalse();
        window.IsOpen(U(2025, 1, 7, 23), Utc).Should().BeFalse("Tuesday evening does not start a window");
        window.IsOpen(U(2025, 1, 6, 3), Utc).Should().BeFalse("Monday 03:00 belongs to Sunday's window, which is not selected");
        window.IsOpen(U(2025, 1, 13, 23), Utc).Should().BeTrue("next Monday");
    }

    [Fact]
    public void ADaysFilter_AppliesToSameDayWindowsToo()
    {
        var window = W("09:00", "17:00", "weekdays");

        window.IsOpen(U(2025, 1, 6, 10), Utc).Should().BeTrue();
        window.IsOpen(U(2025, 1, 4, 10), Utc).Should().BeFalse("Saturday");
    }

    [Fact]
    public void SundayNightCrossingMidnight_WrapsIntoMonday()
    {
        var window = W("22:00", "06:00", "sun");

        window.IsOpen(U(2025, 1, 5, 23), Utc).Should().BeTrue();
        window.IsOpen(U(2025, 1, 6, 5), Utc).Should().BeTrue("Monday morning belongs to Sunday's window");
        window.IsOpen(U(2025, 1, 6, 23), Utc).Should().BeFalse();
    }

    [Fact]
    public void TimesAreLocalWallClock()
    {
        var window = W("22:00", "06:00");

        // UTC-5 in January: 22:00 local is 03:00 UTC the next day.
        window.IsOpen(U(2025, 1, 7, 2, 59), BackupScheduleTests.UsZone).Should().BeFalse();
        window.IsOpen(U(2025, 1, 7, 3, 0), BackupScheduleTests.UsZone).Should().BeTrue();
        window.IsOpen(U(2025, 1, 7, 10, 59), BackupScheduleTests.UsZone).Should().BeTrue();
        window.IsOpen(U(2025, 1, 7, 11, 0), BackupScheduleTests.UsZone).Should().BeFalse();
    }

    [Fact]
    public void NextOpenAtOrAfter_ReturnsTheInstantItselfWhenOpen_OtherwiseTheNextStart()
    {
        var window = W("22:00", "06:00");

        window.NextOpenAtOrAfter(U(2025, 1, 6, 23), Utc).Should().Be(U(2025, 1, 6, 23));
        window.NextOpenAtOrAfter(U(2025, 1, 7, 6, 0), Utc).Should().Be(U(2025, 1, 7, 22, 0), "closed exactly at the end");
        window.NextOpenAtOrAfter(U(2025, 1, 7, 12), Utc).Should().Be(U(2025, 1, 7, 22));
        window.NextOpenAtOrAfter(U(2025, 1, 7, 21, 59, 59), Utc).Should().Be(U(2025, 1, 7, 22));
    }

    [Fact]
    public void NextOpenAtOrAfter_SkipsDaysThatDoNotStartAWindow()
    {
        var window = W("22:00", "06:00", "mon,fri");

        // Tuesday noon: next start is Friday 22:00.
        window.NextOpenAtOrAfter(U(2025, 1, 7, 12), Utc).Should().Be(U(2025, 1, 10, 22));
        // Saturday 07:00: Friday's window is over, next is Monday 22:00.
        window.NextOpenAtOrAfter(U(2025, 1, 11, 7), Utc).Should().Be(U(2025, 1, 13, 22));
        // Monday 07:00 (closed: Sunday is not selected): the same Monday 22:00.
        window.NextOpenAtOrAfter(U(2025, 1, 6, 7), Utc).Should().Be(U(2025, 1, 6, 22));
    }

    [Fact]
    public void NextOpenAtOrAfter_WholeDayWindow_OpensAtMidnightOfTheNextSelectedDay()
    {
        var window = W("00:00", "00:00", "sat");

        window.NextOpenAtOrAfter(U(2025, 1, 6, 12), Utc).Should().Be(U(2025, 1, 11, 0));
        window.NextOpenAtOrAfter(U(2025, 1, 11, 12), Utc).Should().Be(U(2025, 1, 11, 12));
    }

    [Fact]
    public void NextOpenAtOrAfter_UsesTheLocalZone()
    {
        var window = W("22:00", "06:00");

        // 2025-01-07 12:00 UTC is 07:00 local; the window opens at 22:00 local = 03:00 UTC next day.
        window.NextOpenAtOrAfter(U(2025, 1, 7, 12), BackupScheduleTests.UsZone).Should().Be(U(2025, 1, 8, 3));
    }

    [Fact]
    public void NextOpenAtOrAfter_AStartInTheSpringForwardGap_OpensAtTheFirstValidMinute()
    {
        // 2025-03-09 in the test zone: 02:30 does not exist; a window starting at 02:30 opens at 03:00 EDT = 07:00 UTC.
        var window = W("02:30", "05:00");

        window.NextOpenAtOrAfter(U(2025, 3, 9, 0), BackupScheduleTests.UsZone).Should().Be(U(2025, 3, 9, 7));
    }

    [Theory]
    [InlineData("22:00", "06:00", null, "22:00", "06:00", "all")]
    [InlineData("6:30", "7:05", "", "06:30", "07:05", "all")]
    [InlineData("22:00", "06:00", "all", "22:00", "06:00", "all")]
    [InlineData("22:00", "06:00", "Weekdays", "22:00", "06:00", "weekdays")]
    [InlineData("22:00", "06:00", "weekends", "22:00", "06:00", "weekends")]
    [InlineData("22:00", "06:00", "mon-fri", "22:00", "06:00", "weekdays")]
    [InlineData("22:00", "06:00", "Fri, Mon", "22:00", "06:00", "mon,fri")]
    [InlineData("22:00", "06:00", "daily", "22:00", "06:00", "all")]
    public void TryCreate_NormalizesTheTimesAndDays(string start, string end, string? days, string startText, string endText, string daysText)
    {
        BackupWindow.TryCreate(start, end, days, out var window, out var error).Should().BeTrue(error);

        window!.StartText.Should().Be(startText);
        window.EndText.Should().Be(endText);
        window.DaysText.Should().Be(daysText);
    }

    [Theory]
    [InlineData("25:00", "06:00", null, "backup window start: expected HH:mm")]
    [InlineData("22:00", "6am", null, "backup window end: expected HH:mm")]
    [InlineData("", "06:00", null, "backup window start")]
    [InlineData(null, "06:00", null, "backup window start")]
    [InlineData("22:00", "06:60", null, "backup window end")]
    [InlineData("22:00", "06:00", "someday", "backup window days")]
    [InlineData("22:00", "06:00", "mon-xyz", "backup window days")]
    public void TryCreate_RejectsBadValues_WithAMessage(string? start, string? end, string? days, string expected)
    {
        BackupWindow.TryCreate(start, end, days, out var window, out var error).Should().BeFalse();

        window.Should().BeNull();
        error.Should().Contain(expected);
    }

    [Theory]
    [InlineData("22:00", "06:00", null, "Open from 22:00 to 06:00 every day")]
    [InlineData("22:00", "06:00", "weekdays", "Open from 22:00 to 06:00 on weekdays")]
    [InlineData("09:00", "17:00", "sat,sun", "Open from 09:00 to 17:00 on weekends")]
    [InlineData("09:00", "17:00", "mon,wed", "Open from 09:00 to 17:00 on mon,wed")]
    [InlineData("00:00", "00:00", null, "Open all day, every day")]
    [InlineData("00:00", "00:00", "sat", "Open all day on sat")]
    public void Describe_IsReadable(string start, string end, string? days, string expected)
        => W(start, end, days).Describe().Should().Be(expected);
}

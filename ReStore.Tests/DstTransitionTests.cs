using FluentAssertions;
using ReStore.Core.src.monitoring;

namespace ReStore.Tests;

/// <summary>
/// Transition shapes the original schedule tests do not cover: a gap that starts at midnight (the "00:30" of that day does not exist) and a
/// 30-minute shift (Lord Howe style). The zones are built by hand so nothing depends on the OS tz database.
/// </summary>
public class DstTransitionTests
{
    private static DateTime FirstDay(TimeZoneInfo zone, int year, int month, Func<DateTime, bool> isTransitionDay)
    {
        for (var day = new DateTime(year, month, 1); day.Month == month; day = day.AddDays(1))
        {
            if (isTransitionDay(day))
            {
                return day;
            }
        }

        throw new InvalidOperationException("no transition found");
    }

    private static List<DateTime> LocalRuns(string expression, TimeZoneInfo zone, DateTime afterUtc, int count) =>
        [.. BackupSchedule.Parse(expression).NextOccurrences(afterUtc, count, zone).Select(utc => TimeZoneInfo.ConvertTimeFromUtc(utc, zone))];

    [Fact]
    public void MidnightGap_ANonexistentMidnightTime_RunsAtTheEndOfTheGap_ExactlyOnce()
    {
        var zone = PropertyBodies.Midnight();
        var gapDay = FirstDay(zone, 2026, 10, d => zone.IsInvalidTime(d.AddMinutes(30)));
        var after = TimeZoneInfo.ConvertTimeToUtc(gapDay.AddDays(-2).AddHours(12), zone);

        var runs = LocalRuns("daily 00:30", zone, after, 4);

        runs.Select(r => r.Date).Should().Equal(gapDay.AddDays(-1), gapDay, gapDay.AddDays(1), gapDay.AddDays(2));
        runs[0].TimeOfDay.Should().Be(TimeSpan.FromMinutes(30));
        runs[1].TimeOfDay.Should().Be(TimeSpan.FromHours(1), "the first valid minute after the gap");
        runs[2].TimeOfDay.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void MidnightGap_ATimeRightAfterTheGap_IsNotRunTwice()
    {
        var zone = PropertyBodies.Midnight();
        var gapDay = FirstDay(zone, 2026, 10, d => zone.IsInvalidTime(d.AddMinutes(30)));
        var after = TimeZoneInfo.ConvertTimeToUtc(gapDay.AddDays(-1).AddHours(12), zone);

        var runs = LocalRuns("daily 00:30,01:00", zone, after, 4);

        runs.Where(r => r.Date == gapDay).Should().ContainSingle("both times collapse onto 01:00 of the gap day").Which.TimeOfDay.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void HalfHourShift_ATimeInsideTheThirtyMinuteGap_RunsWhenTheGapEnds()
    {
        var zone = PropertyBodies.HalfHour();
        var gapDay = FirstDay(zone, 2026, 10, d => zone.IsInvalidTime(d.AddHours(2).AddMinutes(10)));
        var after = TimeZoneInfo.ConvertTimeToUtc(gapDay.AddDays(-1).AddHours(12), zone);

        var runs = LocalRuns("daily 02:15", zone, after, 3);

        runs.Select(r => r.Date).Should().Equal(gapDay, gapDay.AddDays(1), gapDay.AddDays(2));
        runs[0].TimeOfDay.Should().Be(new TimeSpan(2, 30, 0), "02:15 does not exist that day; the gap ends at 02:30");
        runs[1].TimeOfDay.Should().Be(new TimeSpan(2, 15, 0));
    }

    [Fact]
    public void HalfHourShift_EveryLocalDayRunsOnce_AcrossBothTransitions()
    {
        var zone = PropertyBodies.HalfHour();
        var after = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 3, 20, 12, 0, 0), zone);

        foreach (var expression in new[] { "daily 02:15", "daily 01:45", "daily 02:30", "cron 15 2 * * *" })
        {
            var runs = LocalRuns(expression, zone, after, 220);

            runs.Select(r => r.Date).Should().OnlyHaveUniqueItems($"{expression}: a day with a repeated or skipped time still runs once");
            runs.Zip(runs.Skip(1), (a, b) => (b.Date - a.Date).Days).Should().OnlyContain(d => d == 1, expression);
        }
    }

    [Theory]
    [InlineData("Test/Midnight")]
    [InlineData("Test/HalfHour")]
    [InlineData("Test/Eastern")]
    public void SpringForward_NeverProducesALocalTimeThatDoesNotExist(string zoneId)
    {
        var zone = new[] { PropertyBodies.Midnight(), PropertyBodies.HalfHour(), PropertyBodies.NewYork() }.Single(z => z.Id == zoneId);
        var after = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 1, 1, 12, 0, 0), zone.IsInvalidTime(new DateTime(2026, 1, 1, 12, 0, 0)) ? TimeZoneInfo.Utc : zone);

        foreach (var expression in new[] { "daily 00:00,00:30,01:00,01:30,02:00,02:15,02:30,03:00,23:59", "cron */15 0-3 * * *" })
        {
            foreach (var utc in BackupSchedule.Parse(expression).NextOccurrences(after, 400, zone))
            {
                zone.IsInvalidTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone)).Should().BeFalse($"{expression}: {utc:O}");
            }
        }
    }
}

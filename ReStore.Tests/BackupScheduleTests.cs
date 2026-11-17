using FluentAssertions;
using ReStore.Core.src.monitoring;

namespace ReStore.Tests;

/// <summary>The schedule expression parser and its occurrence maths (pure: no clock, no I/O, an injected time zone).</summary>
public class BackupScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    /// <summary>
    /// A deterministic US-style zone: UTC-5, daylight saving (UTC-4) from the second Sunday of March 02:00 to the first Sunday
    /// of November 02:00 (local daylight time). 2025: spring forward Sun 9 March (02:00-03:00 does not exist), fall back Sun 2 November
    /// (01:00-02:00 happens twice).
    /// </summary>
    internal static readonly TimeZoneInfo UsZone = CreateUsZone();

    private static TimeZoneInfo CreateUsZone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone("Test/US", TimeSpan.FromHours(-5), "Test US", "Test Standard", "Test Daylight", [rule]);
    }

    internal static DateTime U(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    private static BackupSchedule P(string text) => BackupSchedule.Parse(text);

    // ---- valid expressions -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("off", ScheduleKind.Off, "off")]
    [InlineData("  OFF ", ScheduleKind.Off, "off")]
    [InlineData("every 30m", ScheduleKind.Interval, "every 30m")]
    [InlineData("every 4h", ScheduleKind.Interval, "every 4h")]
    [InlineData("every 1d", ScheduleKind.Interval, "every 1d")]
    [InlineData("EVERY   4H", ScheduleKind.Interval, "every 4h")]
    [InlineData("every 4 h", ScheduleKind.Interval, "every 4h")]
    [InlineData("every 5m", ScheduleKind.Interval, "every 5m")]
    [InlineData("every 90m", ScheduleKind.Interval, "every 90m")]
    [InlineData("every 24h", ScheduleKind.Interval, "every 1d")]
    [InlineData("every 365d", ScheduleKind.Interval, "every 365d")]
    [InlineData("daily 02:00", ScheduleKind.Calendar, "daily 02:00")]
    [InlineData("Daily 2:00", ScheduleKind.Calendar, "daily 02:00")]
    [InlineData("daily   02:00 ,  18:30", ScheduleKind.Calendar, "daily 02:00,18:30")]
    [InlineData("daily 18:30,02:00,18:30", ScheduleKind.Calendar, "daily 02:00,18:30")]
    [InlineData("daily 00:00", ScheduleKind.Calendar, "daily 00:00")]
    [InlineData("daily 23:59", ScheduleKind.Calendar, "daily 23:59")]
    [InlineData("weekdays 18:30", ScheduleKind.Calendar, "weekdays 18:30")]
    [InlineData("WEEKDAYS 6:05", ScheduleKind.Calendar, "weekdays 06:05")]
    [InlineData("weekends 10:00", ScheduleKind.Calendar, "weekends 10:00")]
    [InlineData("mon,wed,fri 07:15", ScheduleKind.Calendar, "mon,wed,fri 07:15")]
    [InlineData("Fri, Mon, Wed 07:15", ScheduleKind.Calendar, "mon,wed,fri 07:15")]
    [InlineData("mon-fri 18:30", ScheduleKind.Calendar, "weekdays 18:30")]
    [InlineData("monday 09:00", ScheduleKind.Calendar, "mon 09:00")]
    [InlineData("mon-wed,fri 09:00", ScheduleKind.Calendar, "mon,tue,wed,fri 09:00")]
    [InlineData("sat-mon 09:00", ScheduleKind.Calendar, "mon,sat,sun 09:00")]
    [InlineData("sat,sun 09:00", ScheduleKind.Calendar, "weekends 09:00")]
    [InlineData("weekly sun 03:00", ScheduleKind.Calendar, "weekly sun 03:00")]
    [InlineData("WEEKLY Sunday 3:00", ScheduleKind.Calendar, "weekly sun 03:00")]
    [InlineData("monthly 1 03:00", ScheduleKind.Calendar, "monthly 1 03:00")]
    [InlineData("monthly 28 23:59", ScheduleKind.Calendar, "monthly 28 23:59")]
    [InlineData("monthly last 04:00", ScheduleKind.Calendar, "monthly last 04:00")]
    [InlineData("MONTHLY LAST 4:00", ScheduleKind.Calendar, "monthly last 04:00")]
    [InlineData("cron 0 2 * * *", ScheduleKind.Cron, "cron 0 2 * * *")]
    [InlineData("CRON  */15  *  * * *", ScheduleKind.Cron, "cron */15 * * * *")]
    [InlineData("cron 30 6 * jan-mar mon-fri", ScheduleKind.Cron, "cron 30 6 * jan-mar mon-fri")]
    [InlineData("cron 0 0 1,15 * *", ScheduleKind.Cron, "cron 0 0 1,15 * *")]
    public void Parse_AcceptsEveryForm_AndNormalizes(string text, ScheduleKind kind, string expression)
    {
        BackupSchedule.TryParse(text, out var schedule, out var error).Should().BeTrue(error);

        schedule!.Kind.Should().Be(kind);
        schedule.Expression.Should().Be(expression);

        // The normalized text parses back to the same schedule.
        BackupSchedule.Parse(schedule.Expression).Expression.Should().Be(expression);
    }

    [Fact]
    public void Parse_IntervalSchedules_ExposeTheirInterval_AndAreNotClockSchedules()
    {
        var thirty = P("every 30m");
        thirty.IsInterval.Should().BeTrue();
        thirty.IsClock.Should().BeFalse();
        thirty.Interval.Should().Be(TimeSpan.FromMinutes(30));
        P("every 4h").Interval.Should().Be(TimeSpan.FromHours(4));
        P("every 2d").Interval.Should().Be(TimeSpan.FromDays(2));
        P("daily 02:00").Interval.Should().BeNull();
        P("daily 02:00").IsClock.Should().BeTrue();
        P("off").IsOff.Should().BeTrue();
        P("cron * * * * *").IsClock.Should().BeTrue();
    }

    [Fact]
    public void FromInterval_AllowsTheClassicShortIntervals_AndRejectsNonPositive()
    {
        BackupSchedule.FromInterval(TimeSpan.FromMinutes(1)).Interval.Should().Be(TimeSpan.FromMinutes(1));
        BackupSchedule.FromInterval(TimeSpan.FromHours(1)).Description.Should().Be("Every hour");
        BackupSchedule.FromInterval(TimeSpan.FromHours(1)).Expression.Should().Be("every 1h");

        var act = () => BackupSchedule.FromInterval(TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- invalid expressions ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData("off now", "'off' takes no arguments")]
    [InlineData("every", "expected 'every <N>m'")]
    [InlineData("every 30", "expected 'every <N>m'")]
    [InlineData("every m", "expected 'every <N>m'")]
    [InlineData("every 1.5h", "expected 'every <N>m'")]
    [InlineData("every -5m", "expected 'every <N>m'")]
    [InlineData("every 4w", "expected 'every <N>m'")]
    [InlineData("every 4m", "minimum is 5m")]
    [InlineData("every 0h", "minimum is 5m")]
    [InlineData("every 366d", "maximum is 365d")]
    [InlineData("every 99999999999d", "maximum is 365d")]
    [InlineData("daily", "expected HH:mm at 'daily'")]
    [InlineData("daily 2pm", "expected HH:mm at 'daily 2pm'")]
    [InlineData("daily 24:00", "expected HH:mm at 'daily 24:00'")]
    [InlineData("daily 12:60", "expected HH:mm at 'daily 12:60'")]
    [InlineData("daily 02:00,xx", "expected HH:mm at 'daily 02:00,xx'")]
    [InlineData("daily 02:00 03:00", "expected HH:mm at 'daily 02:00 03:00'")]
    [InlineData("daily 0200", "expected HH:mm")]
    [InlineData("weekdays", "expected HH:mm")]
    [InlineData("weekends 25:00", "expected HH:mm")]
    [InlineData("mon,wed", "expected HH:mm at 'mon,wed'")]
    [InlineData("mon,xyz 07:00", "'xyz' is not a day of the week")]
    [InlineData("mon-xyz 07:00", "is not a range of days")]
    [InlineData("noon 12:00", "unknown schedule 'noon'")]
    [InlineData("hourly", "unknown schedule 'hourly'")]
    [InlineData("weekly", "expected 'weekly <day> HH:mm'")]
    [InlineData("weekly sun", "expected 'weekly <day> HH:mm'")]
    [InlineData("weekly mon,tue 03:00", "takes a single day")]
    [InlineData("weekly mon-fri 03:00", "takes a single day")]
    [InlineData("weekly funday 03:00", "not a day of the week")]
    [InlineData("monthly", "expected 'monthly <1-28|last> HH:mm'")]
    [InlineData("monthly 1", "expected 'monthly <1-28|last> HH:mm'")]
    [InlineData("monthly 0 03:00", "day of the month from 1 to 28")]
    [InlineData("monthly 29 03:00", "day of the month from 1 to 28")]
    [InlineData("monthly 31 03:00", "day of the month from 1 to 28")]
    [InlineData("monthly first 03:00", "day of the month from 1 to 28")]
    [InlineData("monthly 1 3pm", "expected HH:mm")]
    [InlineData("cron", "expected 5 cron fields")]
    [InlineData("cron * * * *", "expected 5 cron fields")]
    [InlineData("cron * * * * * *", "expected 5 cron fields")]
    [InlineData("cron 60 * * * *", "minute field '60': 60 is out of range 0-59")]
    [InlineData("cron * 24 * * *", "hour field '24': 24 is out of range 0-23")]
    [InlineData("cron * * 0 * *", "day-of-month field '0': 0 is out of range 1-31")]
    [InlineData("cron * * 32 * *", "out of range 1-31")]
    [InlineData("cron * * * 0 *", "month field '0': 0 is out of range 1-12")]
    [InlineData("cron * * * 13 *", "out of range 1-12")]
    [InlineData("cron * * * * 8", "day-of-week field '8': 8 is out of range 0-7")]
    [InlineData("cron */0 * * * *", "not a valid step")]
    [InlineData("cron */x * * * *", "not a valid step")]
    [InlineData("cron 5-3 * * * *", "runs backwards")]
    [InlineData("cron a * * * *", "'a' is not a number")]
    [InlineData("cron 1,,2 * * * *", "empty list item")]
    [InlineData("cron * * * foo *", "'foo' is not a number or a name")]
    [InlineData("cron * * * * funday", "'funday' is not a number or a name")]
    [InlineData("cron * * * mayday *", "not a number or a name")]
    [InlineData("cron 0 0 31 2 *", "would never run")]
    [InlineData("cron 0 0 30 2 *", "would never run")]
    [InlineData("cron 0 0 31 4,6,9,11 *", "would never run")]
    public void Parse_RejectsInvalidExpressions_WithAClearMessage(string? text, string expectedFragment)
    {
        BackupSchedule.TryParse(text, out var schedule, out var error).Should().BeFalse();

        schedule.Should().BeNull();
        error.Should().Contain(expectedFragment);
    }

    [Fact]
    public void Parse_TheTimeError_NamesTheOffendingTextAndTheWholeExpression()
    {
        BackupSchedule.TryParse("daily 2pm", out _, out var error).Should().BeFalse();

        error.Should().Be("expected HH:mm at 'daily 2pm' ('2pm' is not a time of day; use 24-hour HH:mm such as 02:00 or 18:30).");
    }

    [Fact]
    public void Parse_Throws_AFormatException_WithTheSameMessage()
    {
        var act = () => BackupSchedule.Parse("every 1m");

        act.Should().Throw<FormatException>().WithMessage("*minimum is 5m*");
    }

    // ---- cron specifics --------------------------------------------------------------------------------------------

    [Fact]
    public void Cron_Steps_Ranges_AndLists_ExpandToTheRightMinutes()
    {
        P("cron */15 * * * *").NextOccurrences(U(2025, 1, 1, 10, 7), 4, Utc)
            .Should().Equal(U(2025, 1, 1, 10, 15), U(2025, 1, 1, 10, 30), U(2025, 1, 1, 10, 45), U(2025, 1, 1, 11, 0));

        P("cron 5-20/5 12 * * *").NextOccurrences(U(2025, 1, 1), 5, Utc)
            .Should().Equal(U(2025, 1, 1, 12, 5), U(2025, 1, 1, 12, 10), U(2025, 1, 1, 12, 15), U(2025, 1, 1, 12, 20), U(2025, 1, 2, 12, 5));

        P("cron 10/20 3 * * *").NextOccurrences(U(2025, 1, 1), 4, Utc)
            .Should().Equal(U(2025, 1, 1, 3, 10), U(2025, 1, 1, 3, 30), U(2025, 1, 1, 3, 50), U(2025, 1, 2, 3, 10));

        P("cron 0 */6 * * *").NextOccurrences(U(2025, 1, 1, 0, 0), 4, Utc)
            .Should().Equal(U(2025, 1, 1, 6), U(2025, 1, 1, 12), U(2025, 1, 1, 18), U(2025, 1, 2, 0));

        P("cron 0,30 9,17 * * *").NextOccurrences(U(2025, 1, 1), 5, Utc)
            .Should().Equal(U(2025, 1, 1, 9, 0), U(2025, 1, 1, 9, 30), U(2025, 1, 1, 17, 0), U(2025, 1, 1, 17, 30), U(2025, 1, 2, 9, 0));
    }

    [Fact]
    public void Cron_MonthAndDayNames_AreCaseInsensitive_AndAcceptFullNames()
    {
        // 2025-01-01 is a Wednesday.
        P("cron 30 6 * JAN-MAR MON-FRI").NextOccurrence(U(2025, 1, 1, 7), Utc).Should().Be(U(2025, 1, 2, 6, 30));
        P("cron 0 12 1 Jan,JUL *").NextOccurrences(U(2025, 1, 1, 13), 3, Utc).Should().Equal(U(2025, 7, 1, 12), U(2026, 1, 1, 12), U(2026, 7, 1, 12));
        P("cron 0 12 1 january,july *").NextOccurrence(U(2025, 1, 1, 13), Utc).Should().Be(U(2025, 7, 1, 12));
        P("cron 0 8 * * monday,friday").NextOccurrences(U(2025, 1, 1), 3, Utc).Should().Equal(U(2025, 1, 3, 8), U(2025, 1, 6, 8), U(2025, 1, 10, 8));
    }

    [Theory]
    [InlineData("cron 0 0 * * 0")]
    [InlineData("cron 0 0 * * 7")]
    [InlineData("cron 0 0 * * sun")]
    [InlineData("cron 0 0 * * SUNDAY")]
    [InlineData("cron 0 0 * * 6-7")]
    public void Cron_SundayIsZeroAndSeven(string expression)
    {
        // 2025-01-05 is a Sunday.
        var next = P(expression).NextOccurrence(U(2025, 1, 1), Utc);

        next.Should().Be(expression.EndsWith("6-7", StringComparison.Ordinal) ? U(2025, 1, 4) : U(2025, 1, 5));
    }

    [Fact]
    public void Cron_WhenBothDayFieldsAreRestricted_EitherMatches()
    {
        // The 13th of the month OR any Friday (classic cron): 2025-01-03 is a Friday, the 13th is a Monday.
        P("cron 0 0 13 * fri").NextOccurrences(U(2025, 1, 1), 8, Utc).Should().Equal(
            U(2025, 1, 3), U(2025, 1, 10), U(2025, 1, 13), U(2025, 1, 17), U(2025, 1, 24), U(2025, 1, 31), U(2025, 2, 7), U(2025, 2, 13));
    }

    [Fact]
    public void Cron_WhenADayFieldStartsWithAStar_BothMustMatch()
    {
        // Odd days of the month that are Mondays: January 2025 has Mondays on 6, 13, 20, 27.
        P("cron 0 0 */2 * mon").NextOccurrences(U(2025, 1, 1), 2, Utc).Should().Equal(U(2025, 1, 13), U(2025, 1, 27));

        P("cron 0 0 15 * *").NextOccurrence(U(2025, 1, 16), Utc).Should().Be(U(2025, 2, 15));
    }

    [Fact]
    public void Cron_February29_RunsOnlyInLeapYears_AndFebruary31IsRejected()
    {
        var schedule = P("cron 0 0 29 2 *");

        schedule.NextOccurrence(U(2025, 1, 1), Utc).Should().Be(U(2028, 2, 29));
        schedule.NextOccurrence(U(2028, 2, 29), Utc).Should().Be(U(2032, 2, 29));
        schedule.LatestOccurrenceAtOrBefore(U(2027, 12, 31), Utc).Should().Be(U(2024, 2, 29));
        // 2100 is not a leap year: the gap from 2096 to 2104 is eight years and is still found.
        schedule.NextOccurrence(U(2096, 3, 1), Utc).Should().Be(U(2104, 2, 29));

        BackupSchedule.TryParse("cron 0 0 31 2 *", out _, out _).Should().BeFalse();
        BackupSchedule.TryParse("cron 0 0 30 2 *", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Cron_Day31_SkipsTheMonthsThatDoNotHaveIt()
    {
        P("cron 0 0 31 * *").NextOccurrences(U(2025, 1, 1), 5, Utc)
            .Should().Equal(U(2025, 1, 31), U(2025, 3, 31), U(2025, 5, 31), U(2025, 7, 31), U(2025, 8, 31));
    }

    [Fact]
    public void Cron_Describe_SummarisesSimpleSchedules_AndFallsBackToTheExpression()
    {
        P("cron 0 2 * * *").Description.Should().Be("Every day at 02:00");
        P("cron 30 18 * * 1-5").Description.Should().Be("Every weekday at 18:30");
        P("cron 0 9,17 * * mon").Description.Should().Be("Every Monday at 09:00 and 17:00");
        P("cron */15 * * * *").Description.Should().Be("Cron schedule '*/15 * * * *'");
        P("cron 0 0 1 * *").Description.Should().Be("Cron schedule '0 0 1 * *'");
    }

    // ---- Describe --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("off", "Off (no scheduled backups; changes are backed up by the watcher or by hand)")]
    [InlineData("every 30m", "Every 30 minutes")]
    [InlineData("every 60m", "Every hour")]
    [InlineData("every 1h", "Every hour")]
    [InlineData("every 4h", "Every 4 hours")]
    [InlineData("every 1d", "Every day")]
    [InlineData("every 7d", "Every 7 days")]
    [InlineData("daily 02:00", "Every day at 02:00")]
    [InlineData("daily 02:00,18:30", "Every day at 02:00 and 18:30")]
    [InlineData("daily 02:00,12:00,18:30", "Every day at 02:00, 12:00 and 18:30")]
    [InlineData("weekdays 18:30", "Every weekday at 18:30")]
    [InlineData("mon-fri 18:30", "Every weekday at 18:30")]
    [InlineData("weekends 10:00", "Every Saturday and Sunday at 10:00")]
    [InlineData("mon,wed,fri 07:15", "Every Monday, Wednesday and Friday at 07:15")]
    [InlineData("tue 07:15", "Every Tuesday at 07:15")]
    [InlineData("weekly sun 03:00", "Every Sunday at 03:00")]
    [InlineData("monthly 1 03:00", "On the 1st of every month at 03:00")]
    [InlineData("monthly 2 03:00", "On the 2nd of every month at 03:00")]
    [InlineData("monthly 3 03:00", "On the 3rd of every month at 03:00")]
    [InlineData("monthly 11 03:00", "On the 11th of every month at 03:00")]
    [InlineData("monthly 21 03:00", "On the 21st of every month at 03:00")]
    [InlineData("monthly 22 03:00", "On the 22nd of every month at 03:00")]
    [InlineData("monthly 28 03:00", "On the 28th of every month at 03:00")]
    [InlineData("monthly last 23:30", "On the last day of every month at 23:30")]
    public void Describe_GivesAHumanSentence(string expression, string expected)
        => P(expression).Description.Should().Be(expected);

    // ---- occurrences -----------------------------------------------------------------------------------------------

    [Fact]
    public void IntervalAndOffSchedules_HaveNoClockOccurrences()
    {
        P("every 1h").NextOccurrence(U(2025, 1, 1), Utc).Should().BeNull();
        P("every 1h").LatestOccurrenceAtOrBefore(U(2025, 1, 1), Utc).Should().BeNull();
        P("off").NextOccurrence(U(2025, 1, 1), Utc).Should().BeNull();
        P("off").LatestOccurrenceAtOrBefore(U(2025, 1, 1), Utc).Should().BeNull();
    }

    [Fact]
    public void NextOccurrence_IsStrictlyAfter_AndLatestIsAtOrBefore()
    {
        var schedule = P("daily 02:00");

        schedule.NextOccurrence(U(2025, 1, 1, 2, 0), Utc).Should().Be(U(2025, 1, 2, 2, 0));
        schedule.NextOccurrence(U(2025, 1, 1, 1, 59, 59), Utc).Should().Be(U(2025, 1, 1, 2, 0));
        schedule.LatestOccurrenceAtOrBefore(U(2025, 1, 1, 2, 0), Utc).Should().Be(U(2025, 1, 1, 2, 0));
        schedule.LatestOccurrenceAtOrBefore(U(2025, 1, 1, 1, 59, 59), Utc).Should().Be(U(2024, 12, 31, 2, 0));
    }

    [Fact]
    public void Occurrences_CrossMidnightMonthAndYearBoundaries()
    {
        P("daily 23:30").NextOccurrence(U(2025, 1, 1, 23, 45), Utc).Should().Be(U(2025, 1, 2, 23, 30));
        P("daily 23:30").LatestOccurrenceAtOrBefore(U(2025, 1, 2, 0, 10), Utc).Should().Be(U(2025, 1, 1, 23, 30));
        P("daily 00:00").NextOccurrence(U(2025, 12, 31, 23, 59), Utc).Should().Be(U(2026, 1, 1, 0, 0));
        P("monthly 1 03:00").NextOccurrence(U(2025, 12, 15), Utc).Should().Be(U(2026, 1, 1, 3, 0));
        P("monthly 1 03:00").LatestOccurrenceAtOrBefore(U(2026, 1, 1, 2, 59), Utc).Should().Be(U(2025, 12, 1, 3, 0));
        P("weekly sun 03:00").NextOccurrence(U(2025, 12, 28, 3, 0), Utc).Should().Be(U(2026, 1, 4, 3, 0));
    }

    [Fact]
    public void MonthlyLast_FollowsTheLengthOfEachMonth_IncludingLeapFebruary()
    {
        P("monthly last 23:30").NextOccurrences(U(2025, 1, 31, 23, 30), 4, Utc)
            .Should().Equal(U(2025, 2, 28, 23, 30), U(2025, 3, 31, 23, 30), U(2025, 4, 30, 23, 30), U(2025, 5, 31, 23, 30));

        P("monthly last 23:30").NextOccurrence(U(2028, 1, 31, 23, 30), Utc).Should().Be(U(2028, 2, 29, 23, 30));
        P("monthly last 23:30").LatestOccurrenceAtOrBefore(U(2025, 3, 1), Utc).Should().Be(U(2025, 2, 28, 23, 30));
    }

    [Fact]
    public void Weekdays_SkipTheWeekend()
    {
        // 2025-01-03 is a Friday.
        P("weekdays 18:30").NextOccurrence(U(2025, 1, 3, 18, 30), Utc).Should().Be(U(2025, 1, 6, 18, 30));
        P("weekdays 18:30").LatestOccurrenceAtOrBefore(U(2025, 1, 6, 9, 0), Utc).Should().Be(U(2025, 1, 3, 18, 30));
        P("weekends 10:00").NextOccurrences(U(2025, 1, 3), 3, Utc).Should().Equal(U(2025, 1, 4, 10), U(2025, 1, 5, 10), U(2025, 1, 11, 10));
    }

    [Fact]
    public void MultipleTimesPerDay_AreVisitedInOrder()
    {
        P("daily 18:30,02:00,12:00").NextOccurrences(U(2025, 1, 1, 3), 4, Utc)
            .Should().Equal(U(2025, 1, 1, 12), U(2025, 1, 1, 18, 30), U(2025, 1, 2, 2), U(2025, 1, 2, 12));
    }

    [Fact]
    public void TimesAreLocalWallClock_NotUtc()
    {
        // UTC-5 in January: 02:00 local is 07:00 UTC.
        P("daily 02:00").NextOccurrence(U(2025, 1, 1), UsZone).Should().Be(U(2025, 1, 1, 7));
        // And UTC-4 in July.
        P("daily 02:00").NextOccurrence(U(2025, 7, 1), UsZone).Should().Be(U(2025, 7, 1, 6));
    }

    [Fact]
    public void NextOccurrence_AcceptsLocalAndUnspecifiedKinds()
    {
        var schedule = P("daily 02:00");
        var unspecified = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

        schedule.NextOccurrence(unspecified, Utc).Should().Be(U(2025, 1, 1, 2));
        schedule.NextOccurrence(U(2025, 1, 1), Utc)!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    // ---- daylight saving -------------------------------------------------------------------------------------------

    [Fact]
    public void SpringForward_ANonexistentTime_RunsAtTheFirstValidMinuteAfterTheGap()
    {
        // 2025-03-09: local 02:00-03:00 does not exist. 02:30 therefore runs at 03:00 EDT = 07:00 UTC.
        var schedule = P("daily 02:30");

        schedule.NextOccurrence(U(2025, 3, 9, 0), UsZone).Should().Be(U(2025, 3, 9, 7));
        // The day before and after are ordinary: 02:30 EST = 07:30 UTC, 02:30 EDT = 06:30 UTC.
        schedule.NextOccurrence(U(2025, 3, 8, 0), UsZone).Should().Be(U(2025, 3, 8, 7, 30));
        schedule.NextOccurrence(U(2025, 3, 9, 7), UsZone).Should().Be(U(2025, 3, 10, 6, 30));
        schedule.LatestOccurrenceAtOrBefore(U(2025, 3, 9, 6, 59), UsZone).Should().Be(U(2025, 3, 8, 7, 30));
        schedule.LatestOccurrenceAtOrBefore(U(2025, 3, 9, 7, 0), UsZone).Should().Be(U(2025, 3, 9, 7));
    }

    [Fact]
    public void SpringForward_SeveralTimesInTheGap_RunOnceAtTheEndOfTheGap()
    {
        P("daily 02:15,02:45").NextOccurrences(U(2025, 3, 9, 0), 3, UsZone)
            .Should().Equal(U(2025, 3, 9, 7), U(2025, 3, 10, 6, 15), U(2025, 3, 10, 6, 45));
    }

    [Fact]
    public void SpringForward_TheTimeRightAfterTheGapIsNotDuplicated()
    {
        // 03:00 exists (the gap end) and 02:30 maps onto it: one run, not two.
        P("daily 02:30,03:00").NextOccurrences(U(2025, 3, 9, 0), 2, UsZone)
            .Should().Equal(U(2025, 3, 9, 7), U(2025, 3, 10, 6, 30));
    }

    [Fact]
    public void SpringForward_CronInTheGap_AlsoRunsAfterTheGap()
    {
        P("cron 30 2 * * *").NextOccurrence(U(2025, 3, 9, 0), UsZone).Should().Be(U(2025, 3, 9, 7));
    }

    [Fact]
    public void FallBack_AnAmbiguousTime_RunsOnceAtItsFirstOccurrence()
    {
        // 2025-11-02: local 01:00-02:00 happens twice. 01:30 EDT = 05:30 UTC (first), 01:30 EST = 06:30 UTC (second, skipped).
        var schedule = P("daily 01:30");

        schedule.NextOccurrence(U(2025, 11, 2, 0), UsZone).Should().Be(U(2025, 11, 2, 5, 30));
        schedule.NextOccurrence(U(2025, 11, 2, 5, 30), UsZone).Should().Be(U(2025, 11, 3, 6, 30), "the repeated 01:30 does not run again");
        schedule.NextOccurrence(U(2025, 11, 2, 6, 0), UsZone).Should().Be(U(2025, 11, 3, 6, 30));
        schedule.LatestOccurrenceAtOrBefore(U(2025, 11, 2, 6, 45), UsZone).Should().Be(U(2025, 11, 2, 5, 30), "01:45 EST is after the first 01:30 only");
        schedule.LatestOccurrenceAtOrBefore(U(2025, 11, 2, 12), UsZone).Should().Be(U(2025, 11, 2, 5, 30));
    }

    [Fact]
    public void FallBack_NeighbouringTimes_AreUnaffected()
    {
        P("daily 00:30").NextOccurrence(U(2025, 11, 2, 0), UsZone).Should().Be(U(2025, 11, 2, 4, 30));
        P("daily 02:30").NextOccurrence(U(2025, 11, 2, 0), UsZone).Should().Be(U(2025, 11, 2, 7, 30), "02:30 EST");
    }

    [Fact]
    public void EveryDay_ThroughBothTransitions_RunsExactlyOncePerLocalDay()
    {
        var schedule = P("daily 01:30");
        var runs = schedule.NextOccurrences(U(2025, 3, 7), 400, UsZone);

        var localDays = runs.Select(r => TimeZoneInfo.ConvertTimeFromUtc(r, UsZone).Date).ToList();
        localDays.Should().OnlyHaveUniqueItems();
        localDays.Zip(localDays.Skip(1), (a, b) => (b - a).Days).Should().OnlyContain(d => d == 1);
    }

    [Fact]
    public void ADailyJob_DoesNotRunTwice_InTheSameLocalDay_AfterFallBack()
    {
        // Latest/Next form a partition: whatever now is, the latest run is never after now and the next is always after it.
        var schedule = P("daily 01:30");
        for (var minutes = 0; minutes < 6 * 60; minutes += 5)
        {
            var now = U(2025, 11, 2, 4).AddMinutes(minutes);
            var latest = schedule.LatestOccurrenceAtOrBefore(now, UsZone)!.Value;
            var next = schedule.NextOccurrence(now, UsZone)!.Value;
            latest.Should().BeOnOrBefore(now);
            next.Should().BeAfter(now);
            (next - latest).Should().BeGreaterThan(TimeSpan.FromHours(20));
        }
    }

    // ---- due logic -------------------------------------------------------------------------------------------------

    [Fact]
    public void DueUtc_Interval_IsTheLastActivityPlusTheInterval_AndNeverBackedUpIsDueNow()
    {
        var schedule = P("every 4h");

        schedule.DueUtc(DateTime.MinValue, U(2025, 1, 1), Utc).Should().Be(DateTime.MinValue);
        schedule.DueUtc(U(2025, 1, 1, 1), U(2025, 1, 1, 2), Utc).Should().Be(U(2025, 1, 1, 5));
        schedule.DueUtc(U(2025, 1, 1, 1), U(2025, 1, 2), Utc).Should().Be(U(2025, 1, 1, 5), "an overdue interval is due since last + interval");
    }

    [Fact]
    public void DueUtc_Off_IsNeverDue()
    {
        P("off").DueUtc(DateTime.MinValue, U(2025, 1, 1), Utc).Should().BeNull();
        P("off").DueUtc(U(2024, 1, 1), U(2025, 1, 1), Utc).Should().BeNull();
    }

    [Fact]
    public void DueUtc_Clock_IsDueWhenTheLatestRunIsLaterThanTheLastActivity()
    {
        var schedule = P("daily 02:00");

        // Backed up yesterday at 02:00:40; today 01:00 nothing is due, the next run is today at 02:00.
        schedule.DueUtc(U(2025, 1, 1, 2, 0, 40), U(2025, 1, 2, 1), Utc).Should().Be(U(2025, 1, 2, 2));

        // At 02:00:10 today the 02:00 run is due (the last activity is yesterday's).
        schedule.DueUtc(U(2025, 1, 1, 2, 0, 40), U(2025, 1, 2, 2, 0, 10), Utc).Should().Be(U(2025, 1, 2, 2));

        // Once today's run finished (even a few seconds after 02:00) the next one is tomorrow.
        schedule.DueUtc(U(2025, 1, 2, 2, 0, 40), U(2025, 1, 2, 2, 5), Utc).Should().Be(U(2025, 1, 3, 2));
    }

    [Fact]
    public void DueUtc_Clock_AMissedRunCatchesUpOnce_ThenWaitsForTheNextRun()
    {
        var schedule = P("daily 02:00");
        var last = U(2025, 1, 1, 2, 0, 30);

        // The PC was off for three nights and comes back at 10:00 on the 5th: one catch-up, due since the latest missed run (the 5th, 02:00).
        var due = schedule.DueUtc(last, U(2025, 1, 5, 10), Utc);
        due.Should().Be(U(2025, 1, 5, 2));

        // After that backup ran at 10:00:30, no further catch-up: the next run is the 6th.
        schedule.DueUtc(U(2025, 1, 5, 10, 0, 30), U(2025, 1, 5, 10, 5), Utc).Should().Be(U(2025, 1, 6, 2));
    }

    [Fact]
    public void DueUtc_Clock_NeverBackedUp_IsDueNow()
    {
        P("daily 02:00").DueUtc(DateTime.MinValue, U(2025, 1, 1, 12), Utc).Should().Be(DateTime.MinValue);
    }

    [Fact]
    public void DueUtc_Clock_WithAManualBackupAfterTheScheduledRun_WaitsForTheNextRun()
    {
        // The 02:00 run is satisfied by a manual backup at 09:00 the same day.
        P("daily 02:00").DueUtc(U(2025, 1, 2, 9), U(2025, 1, 2, 12), Utc).Should().Be(U(2025, 1, 3, 2));
    }

    // ---- longest gap -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("every 4h", 4 * 60)]
    [InlineData("daily 02:00", 24 * 60)]
    [InlineData("daily 06:00,10:00", 20 * 60)]
    [InlineData("weekdays 18:30", 72 * 60)]
    [InlineData("weekly sun 03:00", 7 * 24 * 60)]
    [InlineData("monthly 1 03:00", 31 * 24 * 60)]
    [InlineData("cron */30 * * * *", 30)]
    public void LongestGap_IsTheWorstCaseTimeBetweenRuns(string expression, int expectedMinutes)
        => P(expression).LongestGap(U(2025, 1, 1), Utc).Should().Be(TimeSpan.FromMinutes(expectedMinutes));

    [Fact]
    public void LongestGap_OfOff_IsNull() => P("off").LongestGap(U(2025, 1, 1), Utc).Should().BeNull();
}

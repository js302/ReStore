using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReStore.Core.src.monitoring;

/// <summary>How a <see cref="BackupSchedule"/> decides when a backup is due.</summary>
public enum ScheduleKind
{
    /// <summary>Never scheduled: the folder is backed up by the watcher or by hand only.</summary>
    Off,

    /// <summary>A minimum time between backups, anchored on the last backup (the classic <c>backupInterval</c>).</summary>
    Interval,

    /// <summary><c>daily 02:00</c>, <c>weekdays 18:30</c>, <c>mon,wed 07:00</c>, <c>weekly sun 03:00</c>, <c>monthly last 04:00</c>.</summary>
    Calendar,

    /// <summary>A standard five-field cron expression.</summary>
    Cron
}

/// <summary>
/// A backup schedule expression, parsed. Pure (no I/O, no clock): everything that needs "now" or a time zone takes them as
/// arguments, so it is deterministic under test.
/// <para>
/// Grammar (case-insensitive, extra spaces ignored): <c>off</c>; <c>every &lt;N&gt;m|h|d</c> (at least 5m);
/// <c>daily HH:mm[,HH:mm...]</c>; <c>weekdays HH:mm</c>; <c>weekends HH:mm</c>; <c>&lt;days&gt; HH:mm</c> with days such as
/// <c>mon,wed,fri</c> or <c>mon-fri</c>; <c>weekly &lt;day&gt; HH:mm</c>; <c>monthly &lt;1-28|last&gt; HH:mm</c>;
/// <c>cron &lt;minute hour day-of-month month day-of-week&gt;</c>.
/// </para>
/// <para>
/// Clock times are local wall-clock times. A time that does not exist on a day (spring forward) runs at the first valid minute
/// after the gap; a time that happens twice (fall back) runs once, at its first occurrence.
/// </para>
/// </summary>
public sealed partial class BackupSchedule
{
    /// <summary>Shortest interval an <c>every</c> expression may use.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    /// <summary>Longest interval an <c>every</c> expression may use.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(365);

    // Feb 29 happens at most 8 years apart (1896 to 1904), so this many days always contain an occurrence of any satisfiable schedule.
    private const int HorizonDays = (366 * 8) + 2;

    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private readonly int[] _minutesOfDay = [];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _daysOfMonth = new bool[32];
    private readonly bool[] _daysOfWeek = new bool[7];
    private readonly bool _lastDayOfMonth;
    private readonly bool _domStar = true;
    private readonly bool _dowStar;

    private BackupSchedule(ScheduleKind kind, string expression, string description)
    {
        Kind = kind;
        Expression = expression;
        Description = description;
    }

    private BackupSchedule(
        ScheduleKind kind, string expression, string description, int[] minutesOfDay, bool[] months, bool[] daysOfMonth,
        bool lastDayOfMonth, bool[] daysOfWeek, bool domStar, bool dowStar)
        : this(kind, expression, description)
    {
        _minutesOfDay = minutesOfDay;
        _months = months;
        _daysOfMonth = daysOfMonth;
        _lastDayOfMonth = lastDayOfMonth;
        _daysOfWeek = daysOfWeek;
        _domStar = domStar;
        _dowStar = dowStar;
    }

    public ScheduleKind Kind { get; }

    /// <summary>The normalized text (lower case, single spaces, zero-padded times); parsing it gives the same schedule.</summary>
    public string Expression { get; }

    /// <summary>A human sentence, for example "Every weekday at 18:30".</summary>
    public string Description { get; }

    /// <summary>The interval of an <c>every</c> schedule; null for the others.</summary>
    public TimeSpan? Interval { get; private init; }

    public bool IsOff => Kind == ScheduleKind.Off;

    public bool IsInterval => Kind == ScheduleKind.Interval;

    /// <summary>True for schedules with fixed times of day (calendar and cron).</summary>
    public bool IsClock => Kind is ScheduleKind.Calendar or ScheduleKind.Cron;

    /// <summary>A schedule from the configured <c>backupInterval</c> (not subject to the 5 minute minimum of <c>every</c>).</summary>
    public static BackupSchedule FromInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
        }

        return CreateInterval(interval);
    }

    public static BackupSchedule Off { get; } = new(ScheduleKind.Off, "off", "Off (no scheduled backups; changes are backed up by the watcher or by hand)");

    public static BackupSchedule Parse(string? text)
        => TryParse(text, out var schedule, out var error) ? schedule : throw new FormatException(error);

    /// <summary>Parses an expression. <paramref name="error"/> is a message for the user that names the offending part.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out BackupSchedule? schedule, [NotNullWhen(false)] out string? error)
    {
        schedule = null;
        error = null;
        var original = text?.Trim() ?? string.Empty;
        if (original.Length == 0)
        {
            error = "A schedule is required, for example 'daily 02:00', 'every 4h', 'weekdays 18:30' or 'off'.";
            return false;
        }

        var tokens = TrimAroundCommas().Replace(original, ",").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var keyword = tokens[0].ToLowerInvariant();
        var arguments = tokens.Skip(1).Select(t => t.ToLowerInvariant()).ToArray();

        schedule = keyword switch
        {
            "off" => ParseOff(original, arguments, out error),
            "every" => ParseEvery(original, arguments, out error),
            "daily" => ParseCalendar(original, "daily", ScheduleDays.AllDays, arguments, out error),
            "weekdays" => ParseCalendar(original, "weekdays", ScheduleDays.Weekdays, arguments, out error),
            "weekends" => ParseCalendar(original, "weekends", ScheduleDays.Weekends, arguments, out error),
            "weekly" => ParseWeekly(original, arguments, out error),
            "monthly" => ParseMonthly(original, arguments, out error),
            "cron" => ParseCron(original, arguments, out error),
            _ => ParseDaysAndTimes(original, keyword, arguments, out error)
        };
        return schedule != null;
    }

    // ---- parsing ---------------------------------------------------------------------------------------------------

    private static BackupSchedule? ParseOff(string original, string[] arguments, out string? error)
    {
        error = arguments.Length > 0 ? $"'off' takes no arguments at '{original}'." : null;
        return error == null ? Off : null;
    }

    private static BackupSchedule? ParseEvery(string original, string[] arguments, out string? error)
    {
        var amount = string.Concat(arguments);
        var match = EveryAmount().Match(amount);
        if (arguments.Length == 0 || !match.Success)
        {
            error = $"expected 'every <N>m', 'every <N>h' or 'every <N>d' at '{original}' (for example 'every 30m', 'every 4h', 'every 1d').";
            return null;
        }

        var digits = match.Groups[1].Value.TrimStart('0');
        if (digits.Length > 6)
        {
            error = $"the interval is too long at '{original}': the maximum is 365d.";
            return null;
        }

        var count = digits.Length == 0 ? 0 : long.Parse(digits, CultureInfo.InvariantCulture);
        var interval = match.Groups[2].Value switch
        {
            "m" => TimeSpan.FromMinutes(count),
            "h" => TimeSpan.FromHours(count),
            _ => TimeSpan.FromDays(count)
        };

        if (interval < MinimumInterval)
        {
            error = $"the interval is too short at '{original}': the minimum is 5m.";
            return null;
        }

        if (interval > MaximumInterval)
        {
            error = $"the interval is too long at '{original}': the maximum is 365d.";
            return null;
        }

        error = null;
        return CreateInterval(interval);
    }

    private static BackupSchedule CreateInterval(TimeSpan interval)
    {
        string text;
        string description;
        if (interval.Ticks % TimeSpan.TicksPerDay == 0)
        {
            var days = (long)interval.TotalDays;
            text = $"every {days}d";
            description = days == 1 ? "Every day" : $"Every {days} days";
        }
        else if (interval.Ticks % TimeSpan.TicksPerHour == 0)
        {
            var hours = (long)interval.TotalHours;
            text = $"every {hours}h";
            description = hours == 1 ? "Every hour" : $"Every {hours} hours";
        }
        else
        {
            var minutes = Math.Max(1, (long)Math.Round(interval.TotalMinutes));
            text = $"every {minutes}m";
            description = minutes == 1 ? "Every minute" : $"Every {minutes} minutes";
        }

        return new BackupSchedule(ScheduleKind.Interval, text, description) { Interval = interval };
    }

    private static BackupSchedule? ParseCalendar(string original, string keyword, bool[] days, string[] arguments, out string? error)
    {
        if (arguments.Length != 1)
        {
            error = $"expected HH:mm at '{original}': write the time after '{keyword}', for example '{keyword} 02:00'.";
            return null;
        }

        if (!TryParseTimes(arguments[0], original, out var minutes, out error))
        {
            return null;
        }

        return CreateCalendar(keyword, ScheduleDays.Phrase(days), days, minutes, lastDay: false, dayOfMonth: null, description: null, out error);
    }

    private static BackupSchedule? ParseDaysAndTimes(string original, string first, string[] arguments, out string? error)
    {
        if (!ScheduleDays.TryParse(first, allowKeywords: false, out var days, out var daysError))
        {
            error = IsPlainWord(first)
                ? $"unknown schedule '{first}' at '{original}'. Start with every, daily, weekdays, weekends, weekly, monthly, cron, a list of days (mon,wed,fri or mon-fri) or off."
                : $"{daysError} at '{original}'.";
            return null;
        }

        if (arguments.Length != 1)
        {
            error = $"expected HH:mm at '{original}': write the time after the days, for example '{first} 18:30'.";
            return null;
        }

        if (!TryParseTimes(arguments[0], original, out var minutes, out error))
        {
            return null;
        }

        // Every day written as a list (mon-sun) must not become "all": that keyword is only valid for the backup window, not here.
        var expression = days.All(d => d) ? "daily" : ScheduleDays.Canonical(days);
        return CreateCalendar(expression, ScheduleDays.Phrase(days), days, minutes, lastDay: false, dayOfMonth: null, description: null, out error);
    }

    private static BackupSchedule? ParseWeekly(string original, string[] arguments, out string? error)
    {
        if (arguments.Length != 2)
        {
            error = $"expected 'weekly <day> HH:mm' at '{original}' (for example 'weekly sun 03:00').";
            return null;
        }

        if (!ScheduleDays.TryParse(arguments[0], allowKeywords: false, out var days, out var daysError))
        {
            error = $"{daysError} at '{original}'.";
            return null;
        }

        if (days.Count(d => d) != 1)
        {
            error = $"'weekly' takes a single day at '{original}'; use '{arguments[0]} HH:mm' for several days.";
            return null;
        }

        if (!TryParseTimes(arguments[1], original, out var minutes, out error))
        {
            return null;
        }

        return CreateCalendar($"weekly {ScheduleDays.Canonical(days)}", ScheduleDays.Phrase(days), days, minutes, lastDay: false, dayOfMonth: null, description: null, out error);
    }

    private static BackupSchedule? ParseMonthly(string original, string[] arguments, out string? error)
    {
        if (arguments.Length != 2)
        {
            error = $"expected 'monthly <1-28|last> HH:mm' at '{original}' (for example 'monthly 1 03:00' or 'monthly last 23:30').";
            return null;
        }

        var dayText = arguments[0];
        var isLast = dayText == "last";
        var dayOfMonth = 0;
        if (!isLast && !(int.TryParse(dayText, NumberStyles.None, CultureInfo.InvariantCulture, out dayOfMonth) && dayOfMonth is >= 1 and <= 28))
        {
            error = $"expected a day of the month from 1 to 28, or 'last', at '{original}' ('{dayText}' is not valid; 29-31 are not allowed because not every month has them, use 'last' for the end of the month).";
            return null;
        }

        if (!TryParseTimes(arguments[1], original, out var minutes, out error))
        {
            return null;
        }

        var description = isLast
            ? "On the last day of every month"
            : $"On the {Ordinal(dayOfMonth)} of every month";
        return CreateCalendar($"monthly {(isLast ? "last" : dayOfMonth.ToString(CultureInfo.InvariantCulture))}", null, ScheduleDays.AllDays, minutes, isLast, isLast ? null : dayOfMonth, description, out error);
    }

    /// <summary>Shared by the calendar forms: <paramref name="prefix"/> is the normalized text before the times; <paramref name="phrase"/> the "Every ..." part.</summary>
    private static BackupSchedule? CreateCalendar(
        string prefix, string? phrase, bool[] days, int[] minutes, bool lastDay, int? dayOfMonth, string? description, out string? error)
    {
        error = null;
        var months = new bool[13];
        Array.Fill(months, true, 1, 12);
        var daysOfMonth = new bool[32];
        if (dayOfMonth is { } specific)
        {
            daysOfMonth[specific] = true;
        }
        else if (!lastDay)
        {
            Array.Fill(daysOfMonth, true, 1, 31);
        }

        var times = string.Join(',', minutes.Select(FormatMinute));
        description ??= phrase!;
        return new BackupSchedule(
            ScheduleKind.Calendar,
            $"{prefix} {times}",
            $"{description} at {JoinTimes(minutes)}",
            minutes, months, daysOfMonth, lastDay, (bool[])days.Clone(), domStar: dayOfMonth == null && !lastDay, dowStar: true);
    }

    private static BackupSchedule? ParseCron(string original, string[] arguments, out string? error)
    {
        if (arguments.Length != 5)
        {
            error = $"expected 5 cron fields (minute hour day-of-month month day-of-week) at '{original}', found {arguments.Length}.";
            return null;
        }

        var minuteSet = new bool[60];
        var hourSet = new bool[24];
        var domSet = new bool[32];
        var monthSet = new bool[13];
        var dowSet = new bool[8];
        if (!TryParseCronField(arguments[0], "minute", 0, 59, null, minuteSet, original, out error)
            || !TryParseCronField(arguments[1], "hour", 0, 23, null, hourSet, original, out error)
            || !TryParseCronField(arguments[2], "day-of-month", 1, 31, null, domSet, original, out error)
            || !TryParseCronField(arguments[3], "month", 1, 12, MonthNames, monthSet, original, out error)
            || !TryParseCronField(arguments[4], "day-of-week", 0, 7, DayNames, dowSet, original, out error))
        {
            return null;
        }

        // 7 is Sunday as well.
        var weekdays = new bool[7];
        for (var i = 0; i < 7; i++)
        {
            weekdays[i] = dowSet[i] || (i == 0 && dowSet[7]);
        }

        var minutes = new List<int>();
        for (var hour = 0; hour < 24; hour++)
        {
            for (var minute = 0; minute < 60; minute++)
            {
                if (hourSet[hour] && minuteSet[minute])
                {
                    minutes.Add((hour * 60) + minute);
                }
            }
        }

        var domStar = arguments[2].StartsWith('*');
        var dowStar = arguments[4].StartsWith('*');

        // With the "and" rule (a field starting with '*') a day of the month that no chosen month has can never match.
        if (domStar || dowStar)
        {
            var reachable = false;
            for (var month = 1; month <= 12 && !reachable; month++)
            {
                for (var day = 1; day <= MaxDaysIn(month) && !reachable; day++)
                {
                    reachable = monthSet[month] && domSet[day];
                }
            }

            if (!reachable)
            {
                error = $"'{original}' would never run: none of the chosen days of the month exists in the chosen months (for example day 31 of February).";
                return null;
            }
        }

        var normalized = $"cron {string.Join(' ', arguments)}";
        error = null;
        return new BackupSchedule(
            ScheduleKind.Cron, normalized, DescribeCron(normalized, arguments, minutes.ToArray(), monthSet, domSet, weekdays, domStar, dowStar),
            [.. minutes], monthSet, domSet, lastDayOfMonth: false, weekdays, domStar, dowStar);
    }

    private static string DescribeCron(string normalized, string[] fields, int[] minutes, bool[] months, bool[] doms, bool[] weekdays, bool domStar, bool dowStar)
    {
        var everyMonth = Enumerable.Range(1, 12).All(m => months[m]);
        var everyDom = Enumerable.Range(1, 31).All(d => doms[d]);
        if (everyMonth && everyDom && domStar && minutes.Length is >= 1 and <= 4)
        {
            return $"{ScheduleDays.Phrase(weekdays)} at {JoinTimes(minutes)}";
        }

        return $"Cron schedule '{string.Join(' ', fields)}'";
    }

    private static bool TryParseCronField(
        string field, string name, int min, int max, string[]? names, bool[] set, string original, out string? error)
    {
        error = null;
        foreach (var item in field.Split(','))
        {
            if (item.Length == 0)
            {
                error = $"cron {name} field '{field}' has an empty list item at '{original}'.";
                return false;
            }

            var stepText = (string?)null;
            var rangeText = item;
            var slash = item.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                rangeText = item[..slash];
                stepText = item[(slash + 1)..];
            }

            var step = 1;
            if (stepText != null
                && !(int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) && step >= 1))
            {
                error = $"cron {name} field '{field}': '{stepText}' is not a valid step (use a whole number of 1 or more) at '{original}'.";
                return false;
            }

            int from;
            int to;
            if (rangeText == "*")
            {
                from = min;
                to = name == "day-of-week" ? 6 : max;
            }
            else
            {
                var dash = rangeText.IndexOf('-', StringComparison.Ordinal);
                if (dash < 0)
                {
                    if (!TryCronValue(rangeText, name, min, max, names, field, original, out from, out error))
                    {
                        return false;
                    }

                    // "5/15" means "from 5 to the end, every 15".
                    to = stepText != null ? (name == "day-of-week" ? 6 : max) : from;
                }
                else if (!TryCronValue(rangeText[..dash], name, min, max, names, field, original, out from, out error)
                    || !TryCronValue(rangeText[(dash + 1)..], name, min, max, names, field, original, out to, out error))
                {
                    return false;
                }
                else if (from > to)
                {
                    error = $"cron {name} field '{field}': the range '{rangeText}' runs backwards at '{original}'.";
                    return false;
                }
            }

            for (var value = from; value <= to; value += step)
            {
                set[value] = true;
            }
        }

        return true;
    }

    private static bool TryCronValue(string text, string name, int min, int max, string[]? names, string field, string original, out int value, out string? error)
    {
        error = null;
        value = 0;
        if (names != null)
        {
            var index = Array.IndexOf(names, text.Length >= 3 ? text[..3] : text);
            if (text.Length >= 3 && index >= 0 && FullNameMatches(names, index, text))
            {
                // Month names are numbered from 1; day names from 0.
                value = name == "month" ? index + 1 : index;
                return true;
            }
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            error = $"cron {name} field '{field}': '{text}' is not a number{(names != null ? " or a name" : string.Empty)} at '{original}'.";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"cron {name} field '{field}': {value} is out of range {min}-{max} at '{original}'.";
            return false;
        }

        return true;
    }

    private static bool FullNameMatches(string[] names, int index, string text)
    {
        if (text.Length == 3)
        {
            return true;
        }

        // Accept the full English names (monday, january) and nothing else that merely starts with the abbreviation.
        var full = names == DayNames
            ? new[] { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" }[index]
            : new[] { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" }[index];
        return text == full;
    }

    private static bool TryParseTimes(string text, string original, out int[] minutes, out string? error)
    {
        var list = new SortedSet<int>();
        foreach (var item in text.Split(','))
        {
            var match = TimeOfDay().Match(item);
            if (!match.Success)
            {
                minutes = [];
                error = $"expected HH:mm at '{original}' ('{item}' is not a time of day; use 24-hour HH:mm such as 02:00 or 18:30).";
                return false;
            }

            list.Add((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60) + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        minutes = [.. list];
        error = null;
        return true;
    }

    private static bool IsPlainWord(string text) => text.All(char.IsLetter);

    // ---- text ------------------------------------------------------------------------------------------------------

    internal static string FormatMinute(int minuteOfDay) => $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}";

    private static string JoinTimes(int[] minutes)
    {
        var times = minutes.Select(FormatMinute).ToArray();
        return times.Length switch
        {
            1 => times[0],
            _ when times.Length <= 6 => $"{string.Join(", ", times[..^1])} and {times[^1]}",
            _ => $"{times[0]}, {times[1]}, ... {times[^1]} ({times.Length} times a day)"
        };
    }

    private static string Ordinal(int day)
    {
        var suffix = (day % 100) is >= 11 and <= 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{day}{suffix}";
    }

    private static int MaxDaysIn(int month) => month == 2 ? 29 : DateTime.DaysInMonth(2001, month);

    public override string ToString() => Expression;

    // ---- occurrences -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The first run strictly after <paramref name="afterUtc"/>, as UTC; null for <c>off</c> and <c>every</c> schedules (those are
    /// anchored on the last backup, not on the clock) and for a schedule that cannot run within eight years.
    /// </summary>
    public DateTime? NextOccurrence(DateTime afterUtc, TimeZoneInfo? timeZone = null)
    {
        if (!IsClock)
        {
            return null;
        }

        var zone = timeZone ?? TimeZoneInfo.Local;
        var after = LocalClock.AsUtc(afterUtc);
        var day = TimeZoneInfo.ConvertTimeFromUtc(after, zone).Date;
        for (var i = 0; i < HorizonDays && day < DateTime.MaxValue.Date.AddDays(-2); i++, day = day.AddDays(1))
        {
            if (!MatchesDate(day))
            {
                continue;
            }

            DateTime? best = null;
            foreach (var minute in _minutesOfDay)
            {
                var utc = LocalClock.ToUtc(day.AddMinutes(minute), zone);
                if (utc > after && (best == null || utc < best))
                {
                    best = utc;
                }
            }

            if (best != null)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>The last run at or before <paramref name="nowUtc"/>, as UTC; null when there is none (see <see cref="NextOccurrence"/>).</summary>
    public DateTime? LatestOccurrenceAtOrBefore(DateTime nowUtc, TimeZoneInfo? timeZone = null)
    {
        if (!IsClock)
        {
            return null;
        }

        var zone = timeZone ?? TimeZoneInfo.Local;
        var now = LocalClock.AsUtc(nowUtc);
        var day = TimeZoneInfo.ConvertTimeFromUtc(now, zone).Date;
        for (var i = 0; i < HorizonDays && day > DateTime.MinValue.Date.AddDays(2); i++, day = day.AddDays(-1))
        {
            if (!MatchesDate(day))
            {
                continue;
            }

            DateTime? best = null;
            foreach (var minute in _minutesOfDay)
            {
                var utc = LocalClock.ToUtc(day.AddMinutes(minute), zone);
                if (utc <= now && (best == null || utc > best))
                {
                    best = utc;
                }
            }

            if (best != null)
            {
                return best;
            }
        }

        return null;
    }

    /// <summary>The next <paramref name="count"/> runs after <paramref name="afterUtc"/> (clock schedules only).</summary>
    public List<DateTime> NextOccurrences(DateTime afterUtc, int count, TimeZoneInfo? timeZone = null)
    {
        var result = new List<DateTime>();
        var cursor = afterUtc;
        while (result.Count < count && NextOccurrence(cursor, timeZone) is { } next)
        {
            result.Add(next);
            cursor = next;
        }

        return result;
    }

    /// <summary>
    /// The longest time between two consecutive runs (sampled over the next year of runs): the interval of an <c>every</c> schedule,
    /// null for <c>off</c>. Drives the "no successful backup for twice the interval" staleness alert for clock schedules.
    /// </summary>
    public TimeSpan? LongestGap(DateTime fromUtc, TimeZoneInfo? timeZone = null)
    {
        if (IsInterval)
        {
            return Interval;
        }

        if (!IsClock)
        {
            return null;
        }

        var zone = timeZone ?? TimeZoneInfo.Local;
        var day = TimeZoneInfo.ConvertTimeFromUtc(LocalClock.AsUtc(fromUtc), zone).Date;

        // Calendar arithmetic on whole days is enough here (a daylight-saving hour does not change what "too long" means).
        var inDay = TimeSpan.Zero;
        for (var i = 1; i < _minutesOfDay.Length; i++)
        {
            inDay = TimeSpan.FromMinutes(Math.Max(inDay.TotalMinutes, _minutesOfDay[i] - _minutesOfDay[i - 1]));
        }

        var first = TimeSpan.FromMinutes(_minutesOfDay[0]);
        var last = TimeSpan.FromMinutes(_minutesOfDay[^1]);
        var longest = inDay;
        DateTime? previousDay = null;
        for (var i = 0; i < HorizonDays && day < DateTime.MaxValue.Date.AddDays(-2); i++, day = day.AddDays(1))
        {
            if (!MatchesDate(day))
            {
                continue;
            }

            if (previousDay is { } p)
            {
                var gap = (day - p) - last + first;
                if (gap > longest)
                {
                    longest = gap;
                }
            }

            previousDay = day;
        }

        return longest > TimeSpan.Zero ? longest : null;
    }

    /// <summary>
    /// When a target with this schedule is next due, unclamped: <see cref="DateTime.MinValue"/> when it was never backed up (due now),
    /// otherwise the time it became or becomes due; null for <c>off</c>. <paramref name="lastActivityUtc"/> is the later of the last
    /// successful backup and the last attempt (<see cref="DateTime.MinValue"/> for none).
    /// <list type="bullet">
    /// <item>Interval: the last activity plus the interval.</item>
    /// <item>Clock: the latest scheduled run at or before <paramref name="nowUtc"/> when it is later than the last activity (one catch-up run,
    /// however many runs were missed), otherwise the next scheduled run.</item>
    /// </list>
    /// </summary>
    public DateTime? DueUtc(DateTime lastActivityUtc, DateTime nowUtc, TimeZoneInfo? timeZone = null)
    {
        if (IsOff)
        {
            return null;
        }

        if (lastActivityUtc == DateTime.MinValue)
        {
            return DateTime.MinValue;
        }

        if (IsInterval)
        {
            return lastActivityUtc + Interval!.Value;
        }

        var latest = LatestOccurrenceAtOrBefore(nowUtc, timeZone);
        return latest is { } l && l > lastActivityUtc ? l : NextOccurrence(nowUtc, timeZone);
    }

    private bool MatchesDate(DateTime date)
    {
        if (!_months[date.Month])
        {
            return false;
        }

        var dayOk = _daysOfMonth[date.Day] || (_lastDayOfMonth && date.Day == DateTime.DaysInMonth(date.Year, date.Month));
        var weekdayOk = _daysOfWeek[(int)date.DayOfWeek];

        // Classic cron: when both day fields are restricted either may match; if one is '*' both must.
        return _domStar || _dowStar ? dayOk && weekdayOk : dayOk || weekdayOk;
    }

    [GeneratedRegex(@"\s*,\s*")]
    private static partial Regex TrimAroundCommas();

    [GeneratedRegex(@"^(\d+)([mhd])$", RegexOptions.IgnoreCase)]
    private static partial Regex EveryAmount();

    [GeneratedRegex(@"^([01]?\d|2[0-3]):([0-5]\d)$")]
    private static partial Regex TimeOfDay();
}

/// <summary>Days of the week as <c>mon,wed,fri</c> / <c>mon-fri</c> / <c>weekdays</c>, shared by schedules and the backup window.</summary>
internal static class ScheduleDays
{
    public static readonly bool[] AllDays = [true, true, true, true, true, true, true];

    // Index = (int)DayOfWeek, so Sunday is 0.
    public static readonly bool[] Weekdays = [false, true, true, true, true, true, false];
    public static readonly bool[] Weekends = [true, false, false, false, false, false, true];

    private static readonly string[] Short = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly string[] Full = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    // Monday first, as people read a week.
    private static readonly int[] DisplayOrder = [1, 2, 3, 4, 5, 6, 0];

    /// <param name="allowKeywords">Also accept <c>all</c>, <c>daily</c>, <c>weekdays</c> and <c>weekends</c> (the backup window's days).</param>
    public static bool TryParse(string? text, bool allowKeywords, out bool[] days, out string? error)
    {
        days = new bool[7];
        error = null;
        var value = (text ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (value.Length == 0)
        {
            error = "expected days such as mon,wed,fri or mon-fri";
            return false;
        }

        if (allowKeywords)
        {
            switch (value)
            {
                case "all" or "daily" or "everyday":
                    days = [.. AllDays];
                    return true;
                case "weekdays":
                    days = [.. Weekdays];
                    return true;
                case "weekends":
                    days = [.. Weekends];
                    return true;
            }
        }

        foreach (var item in value.Split(','))
        {
            var dash = item.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                if (!TryDay(item, out var single))
                {
                    error = $"'{item}' is not a day of the week (use mon, tue, wed, thu, fri, sat, sun)";
                    return false;
                }

                days[single] = true;
                continue;
            }

            if (!TryDay(item[..dash], out var from) || !TryDay(item[(dash + 1)..], out var to))
            {
                error = $"'{item}' is not a range of days (for example mon-fri)";
                return false;
            }

            // A range may wrap over the weekend (sat-mon).
            for (var day = from; ; day = (day + 1) % 7)
            {
                days[day] = true;
                if (day == to)
                {
                    break;
                }
            }
        }

        return true;
    }

    private static bool TryDay(string text, out int day)
    {
        day = Array.IndexOf(Short, text);
        if (day < 0)
        {
            day = Array.IndexOf(Full, text);
        }

        if (day < 0)
        {
            day = text switch { "tues" => 2, "thur" or "thurs" => 4, _ => -1 };
        }

        return day >= 0;
    }

    /// <summary>"Every day", "Every weekday", "Every Saturday and Sunday", "Every Monday, Wednesday and Friday".</summary>
    public static string Phrase(bool[] days)
    {
        if (days.All(d => d))
        {
            return "Every day";
        }

        if (days.SequenceEqual(Weekdays))
        {
            return "Every weekday";
        }

        var names = DisplayOrder.Where(i => days[i]).Select(i => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Full[i])).ToArray();
        return names.Length == 1 ? $"Every {names[0]}" : $"Every {string.Join(", ", names[..^1])} and {names[^1]}";
    }

    /// <summary>The text that parses back to the same days: <c>all</c>, <c>weekdays</c>, <c>weekends</c> or <c>mon,wed,fri</c>.</summary>
    public static string Canonical(bool[] days)
    {
        if (days.All(d => d))
        {
            return "all";
        }

        if (days.SequenceEqual(Weekdays))
        {
            return "weekdays";
        }

        if (days.SequenceEqual(Weekends))
        {
            return "weekends";
        }

        return string.Join(',', DisplayOrder.Where(i => days[i]).Select(i => Short[i]));
    }
}

/// <summary>Local wall-clock to UTC conversion with the daylight-saving rules of the schedules.</summary>
internal static class LocalClock
{
    public static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// The UTC instant of a local wall-clock time. A time skipped by a spring-forward gap maps to the first valid minute after the gap;
    /// a time that occurs twice (fall back) maps to its first occurrence.
    /// </summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Gaps are at most a few hours; stepping a minute at a time cannot overshoot the first valid minute.
            for (var i = 0; i < 24 * 60 && zone.IsInvalidTime(local); i++)
            {
                local = local.AddMinutes(1);
            }
        }

        if (zone.IsAmbiguousTime(local))
        {
            // The later UTC offset is the daylight-saving one, i.e. the first of the two occurrences.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}

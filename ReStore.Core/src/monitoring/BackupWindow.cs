using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReStore.Core.src.monitoring;

/// <summary>
/// The quiet-hours window automatic work may start in (scheduled and watcher-triggered backups, the system backup, scheduled
/// verification, retention). Pure: the time zone and the instant are arguments.
/// <list type="bullet">
/// <item>Times are local wall-clock times. The start is inclusive and the end exclusive: with 22:00-06:00, 22:00:00 is inside and 06:00:00 is outside.</item>
/// <item>The window may cross midnight (22:00-06:00). <c>start == end</c> means the whole day.</item>
/// <item>The days are the days the window <i>starts</i> on: with 22:00-06:00 on <c>mon</c>, it is open Monday 22:00 until Tuesday 06:00.
/// For a whole-day window they are the days it is open (00:00-24:00).</item>
/// </list>
/// </summary>
public sealed partial class BackupWindow
{
    private readonly bool[] _days;

    private BackupWindow(TimeSpan start, TimeSpan end, bool[] days)
    {
        Start = start;
        End = end;
        _days = days;
    }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    /// <summary>True when start equals end: open for the whole day on the chosen days.</summary>
    public bool IsWholeDay => Start == End;

    /// <summary>True when the window is open at every instant (whole day on every day).</summary>
    public bool IsAlwaysOpen => IsWholeDay && _days.All(d => d);

    /// <summary>The days as text that parses back: <c>all</c>, <c>weekdays</c>, <c>weekends</c> or <c>mon,wed,fri</c>.</summary>
    public string DaysText => ScheduleDays.Canonical(_days);

    public string StartText => Format(Start);

    public string EndText => Format(End);

    /// <param name="days">Null or empty means every day.</param>
    public static bool TryCreate(string? start, string? end, string? days, [NotNullWhen(true)] out BackupWindow? window, [NotNullWhen(false)] out string? error)
    {
        window = null;
        if (!TryParseTime(start, "start", out var startTime, out error) || !TryParseTime(end, "end", out var endTime, out error))
        {
            return false;
        }

        bool[] mask = [.. ScheduleDays.AllDays];
        if (!string.IsNullOrWhiteSpace(days) && !ScheduleDays.TryParse(days, allowKeywords: true, out mask, out var daysError))
        {
            error = $"backup window days: {daysError}. Use all, weekdays, weekends or days such as mon,wed,fri or mon-fri.";
            return false;
        }

        window = new BackupWindow(startTime, endTime, mask);
        error = null;
        return true;
    }

    private static bool TryParseTime(string? text, string name, out TimeSpan time, [NotNullWhen(false)] out string? error)
    {
        time = default;
        var match = TimeOfDay().Match((text ?? string.Empty).Trim());
        if (!match.Success)
        {
            error = $"backup window {name}: expected HH:mm (24-hour) but found '{text}'.";
            return false;
        }

        time = new TimeSpan(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), 0);
        error = null;
        return true;
    }

    /// <summary>Whether automatic work may start at <paramref name="utc"/>.</summary>
    public bool IsOpen(DateTime utc, TimeZoneInfo? timeZone = null)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(LocalClock.AsUtc(utc), timeZone ?? TimeZoneInfo.Local);
        var today = _days[(int)local.DayOfWeek];
        var time = local.TimeOfDay;

        if (IsWholeDay)
        {
            return today;
        }

        if (Start < End)
        {
            return today && time >= Start && time < End;
        }

        // Crosses midnight: the evening part belongs to today's window, the morning part to yesterday's.
        return (today && time >= Start) || (_days[((int)local.DayOfWeek + 6) % 7] && time < End);
    }

    /// <summary>
    /// <paramref name="utc"/> itself when the window is open then, otherwise the instant it next opens.
    /// </summary>
    public DateTime NextOpenAtOrAfter(DateTime utc, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var instant = LocalClock.AsUtc(utc);
        if (IsOpen(instant, zone))
        {
            return instant;
        }

        var day = TimeZoneInfo.ConvertTimeFromUtc(instant, zone).Date;
        for (var i = 0; i <= 8; i++, day = day.AddDays(1))
        {
            if (!_days[(int)day.DayOfWeek])
            {
                continue;
            }

            var opens = LocalClock.ToUtc(day + (IsWholeDay ? TimeSpan.Zero : Start), zone);
            if (opens > instant)
            {
                return opens;
            }
        }

        // Unreachable: some day is always selected, so a start lies within a week.
        return instant;
    }

    /// <summary>
    /// The window of a configuration; null when none is configured or it is switched off. A malformed one (hand-edited file; the
    /// validator reports it) also gives null with <paramref name="error"/> set, so automatic work is never blocked by a typo.
    /// </summary>
    public static BackupWindow? FromConfig(ReStore.Core.src.utils.BackupWindowConfig? config, out string? error)
    {
        error = null;
        if (config is not { Enabled: true })
        {
            return null;
        }

        return TryCreate(config.Start, config.End, config.Days, out var window, out error) ? window : null;
    }

    public string Describe()
    {
        var days = ScheduleDays.Canonical(_days);
        var on = days switch
        {
            "all" => "every day",
            "weekdays" => "on weekdays",
            "weekends" => "on weekends",
            _ => $"on {days}"
        };
        return IsWholeDay
            ? (IsAlwaysOpen ? "Open all day, every day" : $"Open all day {on}")
            : $"Open from {StartText} to {EndText} {on}";
    }

    private static string Format(TimeSpan time) => $"{time.Hours:00}:{time.Minutes:00}";

    [GeneratedRegex(@"^([01]?\d|2[0-3]):([0-5]\d)$")]
    private static partial Regex TimeOfDay();
}

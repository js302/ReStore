using System.Globalization;
using System.Text.RegularExpressions;

namespace ReStore.Core.src.cli;

/// <summary>
/// Parses the short durations the CLI accepts (<c>30m</c>, <c>2h</c>, <c>1d</c>, <c>1h30m</c>): one or more
/// <c>&lt;whole number&gt;&lt;unit&gt;</c> parts with the units <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c> and <c>w</c>.
/// </summary>
internal static partial class CliDurations
{
    public const string Expected = "Expected a duration such as 30m, 2h, 1d or 1h30m (units: s, m, h, d, w).";

    public static bool TryParse(string? text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || !WholeDuration().IsMatch(value))
        {
            return false;
        }

        double seconds = 0;
        foreach (Match part in DurationPart().Matches(value))
        {
            if (!double.TryParse(part.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            {
                return false;
            }

            seconds += amount * char.ToLowerInvariant(part.Groups[2].Value[0]) switch
            {
                's' => 1,
                'm' => 60,
                'h' => 3600,
                'd' => 86400,
                _ => 604800
            };
        }

        if (seconds <= 0 || seconds > TimeSpan.FromDays(3650).TotalSeconds)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>Short text such as <c>2h</c> or <c>1d 6h</c> for messages.</summary>
    public static string Format(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
        {
            return $"{(int)duration.TotalSeconds}s";
        }

        var parts = new List<string>();
        if (duration.Days > 0)
        {
            parts.Add($"{duration.Days}d");
        }

        if (duration.Hours > 0)
        {
            parts.Add($"{duration.Hours}h");
        }

        if (duration.Minutes > 0)
        {
            parts.Add($"{duration.Minutes}m");
        }

        return string.Join(' ', parts);
    }

    [GeneratedRegex(@"^(?:\d+[smhdwSMHDW])+$")]
    private static partial Regex WholeDuration();

    [GeneratedRegex(@"(\d+)([smhdwSMHDW])")]
    private static partial Regex DurationPart();
}

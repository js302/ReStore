using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class DisplayTimeTests
{
    [Fact]
    public void Format_ConvertsUtcToLocalTimeWithFixedFormat()
    {
        var utc = new DateTime(2026, 3, 9, 14, 5, 59, DateTimeKind.Utc);
        var expected = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        DisplayTime.Format(utc).Should().Be(expected);
        DisplayTime.Format(utc).Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$");
    }

    [Fact]
    public void Format_TreatsUnspecifiedKindAsUtc()
    {
        var utc = new DateTime(2026, 3, 9, 14, 5, 0, DateTimeKind.Utc);
        var unspecified = new DateTime(2026, 3, 9, 14, 5, 0, DateTimeKind.Unspecified);

        DisplayTime.Format(unspecified).Should().Be(DisplayTime.Format(utc));
    }

    [Fact]
    public void Format_LocalKindDescribesTheSameInstant()
    {
        var utc = new DateTime(2026, 7, 1, 23, 30, 0, DateTimeKind.Utc);

        DisplayTime.Format(utc.ToLocalTime()).Should().Be(DisplayTime.Format(utc));
    }

    [Fact]
    public void Tooltip_IsIso8601Utc()
    {
        var utc = new DateTime(2026, 3, 9, 14, 5, 59, DateTimeKind.Utc);

        DisplayTime.Tooltip(utc).Should().Be("2026-03-09T14:05:59Z");
        DisplayTime.Tooltip(utc.ToLocalTime()).Should().Be("2026-03-09T14:05:59Z");
        DisplayTime.Tooltip(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)).Should().Be("2026-03-09T14:05:59Z");
    }
}

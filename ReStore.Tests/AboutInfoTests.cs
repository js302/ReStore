using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class AboutInfoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "restore-about-" + Guid.NewGuid().ToString("N"));

    public AboutInfoTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("1.7.0+abc123", "1.7.0")]
    [InlineData("1.7.0", "1.7.0")]
    [InlineData("1.8.0-beta.1+sha", "1.8.0-beta.1")]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("+x", "unknown")]
    public void FormatVersion_strips_build_metadata(string? input, string expected) =>
        AboutInfo.FormatVersion(input).Should().Be(expected);

    [Fact]
    public void FindCrashReports_lists_newest_first_and_only_crash_files()
    {
        var older = Path.Combine(_dir, "crash-1.txt");
        var newer = Path.Combine(_dir, "crash-2.txt");
        File.WriteAllText(older, "a");
        File.WriteAllText(newer, "bb");
        File.WriteAllText(Path.Combine(_dir, "restore.log"), "x");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddHours(-1));

        var reports = AboutInfo.FindCrashReports(_dir);

        reports.Select(r => r.Name).Should().Equal("crash-2.txt", "crash-1.txt");
        reports[0].Length.Should().Be(2);
    }

    [Fact]
    public void FindCrashReports_respects_the_limit_and_missing_directory()
    {
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_dir, $"crash-{i}.txt"), "x");
        }

        AboutInfo.FindCrashReports(_dir, 3).Should().HaveCount(3);
        AboutInfo.FindCrashReports(Path.Combine(_dir, "nope")).Should().BeEmpty();
    }

    [Fact]
    public void DataPaths_match_AppPaths()
    {
        var paths = AboutInfo.DataPaths();
        paths.Should().Contain(p => p.Label == "Logs" && p.Path == AppPaths.LogsDirectory && p.IsDirectory);
        paths.Single(p => p.Label == "Configuration").Path.Should().Be(AppPaths.ConfigFile);
        paths.Single(p => p.Label == "Configuration").IsDirectory.Should().BeFalse();
    }
}

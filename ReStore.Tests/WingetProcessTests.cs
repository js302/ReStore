using System.Diagnostics;
using FluentAssertions;
using ReStore.Core.src.backup;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Puts a scripted stand-in for <c>winget</c> first on PATH (POSIX only; Windows runs return early) so the install and
/// discovery code paths run end to end through <see cref="ProcessRunner"/> without installing anything.
/// </summary>
internal sealed class FakeWinget : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReStoreFakeWinget_" + Guid.NewGuid().ToString("N"));
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");

    public static bool IsSupported => !OperatingSystem.IsWindows();

    public string InvocationLog => Path.Combine(_dir, "calls.log");

    /// <param name="script">Shell snippet; <c>$@</c> are winget's arguments. Every call is appended to <see cref="InvocationLog"/>.</param>
    public FakeWinget(string script)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "winget");
        // The test sources are checked out with CRLF (.gitattributes); a shell script must not contain '\r'.
        File.WriteAllText(path, ("#!/bin/sh\necho \"$@\" >> \"" + InvocationLog + "\"\n" + script + "\n").Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Environment.SetEnvironmentVariable("PATH", _dir + Path.PathSeparator + "/usr/bin" + Path.PathSeparator + "/bin");
    }

    public string[] Calls => File.Exists(InvocationLog) ? File.ReadAllLines(InvocationLog) : [];

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>PATH with no winget at all.</summary>
    public static IDisposable WithoutWinget()
    {
        var original = Environment.GetEnvironmentVariable("PATH");
        var empty = Path.Combine(Path.GetTempPath(), "ReStoreNoWinget_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        Environment.SetEnvironmentVariable("PATH", empty);
        return new Restore(() =>
        {
            Environment.SetEnvironmentVariable("PATH", original);
            try { Directory.Delete(empty, true); } catch { }
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

[Collection("GlobalState")]
public class ProgramRestoreWingetTests
{
    private const string InstallScript = """
        case "$1" in
          --version) echo v1.7; exit 0;;
          install)
            case "$4" in
              *Slow*) /bin/sleep 5; exit 0;;
              *Fail*) echo "boom" >&2; exit 1;;
              *Throw*) exit 1;;
              *) exit 0;;
            esac;;
        esac
        """;

    private static InstalledProgram Winget(string name, string id) =>
        new() { Name = name, WingetId = id, IsWingetAvailable = true, Source = "winget" };

    [NonWindowsFact]
    public async Task RestorePrograms_InstallsEachWingetProgram_AndListsTheManualOnes()
    {
        using var winget = new FakeWinget(InstallScript);
        var logger = new TestLogger();

        var result = await new ProgramRestoreManager(logger).RestoreProgramsAsync(
        [
            Winget("Good", "Vendor.Good"),
            Winget("Bad", "Vendor.Fail"),
            new InstalledProgram { Name = "Manual", Source = "registry" },
            Winget("Evil", "Vendor.Good; calc.exe")
        ]);

        result.SuccessfulInstalls.Should().Be(1);
        result.FailedInstalls.Should().Be(1);
        result.InstalledPrograms.Select(p => p.Name).Should().Equal("Good");
        result.FailedPrograms.Select(p => p.Name).Should().Equal("Bad");
        result.ManualInstallRequired.Select(p => p.Name).Should().BeEquivalentTo("Manual", "Evil");
        result.WingetPrograms.Should().Be(2);
        result.ManualPrograms.Should().Be(2);
        result.Success.Should().BeTrue("some installs succeeded");
        winget.Calls.Should().Contain(c => c.StartsWith("install --exact --id Vendor.Good --silent"));
        winget.Calls.Should().NotContain(c => c.Contains("calc.exe"), "an id that is not a plain winget id is never passed to winget");
        logger.Messages.Should().Contain(m => m.Contains("Ignoring invalid winget id for 'Evil'"));
    }

    [NonWindowsFact]
    public async Task RestorePrograms_WhenEveryInstallFails_ReportsFailure()
    {
        using var winget = new FakeWinget(InstallScript);

        var result = await new ProgramRestoreManager(new TestLogger()).RestoreProgramsAsync([Winget("Bad", "Vendor.Fail")]);

        result.Success.Should().BeFalse();
        result.FailedInstalls.Should().Be(1);
    }

    [NonWindowsFact]
    public async Task RestorePrograms_WingetOnly_SkipsTheManualList()
    {
        using var winget = new FakeWinget(InstallScript);

        var result = await new ProgramRestoreManager(new TestLogger()).RestoreProgramsAsync(
            [Winget("Good", "Vendor.Good"), new InstalledProgram { Name = "Manual" }], wingetOnly: true);

        result.ManualInstallRequired.Should().BeEmpty();
        result.ManualPrograms.Should().Be(1);
        result.SuccessfulInstalls.Should().Be(1);
    }

    [NonWindowsFact]
    public async Task RestorePrograms_InstallTimeout_CountsAsAFailure()
    {
        using var winget = new FakeWinget(InstallScript);
        var logger = new TestLogger();
        var manager = new ProgramRestoreManager(logger) { InstallTimeout = TimeSpan.FromMilliseconds(300) };

        var result = await manager.RestoreProgramsAsync([Winget("Slow", "Vendor.Slow")]);

        result.FailedInstalls.Should().Be(1);
        logger.Messages.Should().Contain(m => m.Contains("timed out"));
    }

    [NonWindowsFact]
    public async Task RestorePrograms_Cancelled_Throws()
    {
        using var winget = new FakeWinget(InstallScript);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => new ProgramRestoreManager(new TestLogger()).RestoreProgramsAsync([Winget("Good", "Vendor.Good")], cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [NonWindowsFact]
    public async Task RestorePrograms_WithoutWinget_WingetOnlyFails_OtherwiseEverythingIsManual()
    {
        using var _ = FakeWinget.WithoutWinget();
        var manager = new ProgramRestoreManager(new TestLogger());
        var programs = new List<InstalledProgram> { Winget("Good", "Vendor.Good") };

        var strict = await manager.RestoreProgramsAsync(programs, wingetOnly: true);
        var lenient = await manager.RestoreProgramsAsync(programs);

        strict.Success.Should().BeFalse();
        strict.ErrorMessage.Should().Contain("Winget is required");
        lenient.SuccessfulInstalls.Should().Be(0);
        lenient.ManualInstallRequired.Should().HaveCount(0, "installable programs are only skipped, not listed as manual");
        lenient.WingetPrograms.Should().Be(1);
    }

    [NonWindowsFact]
    public async Task RestoreProgramsFromJson_InstallsFromTheExportFile_AndRejectsEmptyEntries()
    {
        using var winget = new FakeWinget(InstallScript);
        var dir = Path.Combine(Path.GetTempPath(), "ReStoreRestoreJson_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var good = Path.Combine(dir, "good.json");
            await File.WriteAllTextAsync(good, """{"programs":[{"name":"Good","wingetId":"Vendor.Good","isWingetAvailable":true}]}""");
            var malformed = Path.Combine(dir, "bad.json");
            await File.WriteAllTextAsync(malformed, """{"programs":[{"version":"1"},{"version":"2"}]}""");
            var manager = new ProgramRestoreManager(new TestLogger());

            var installed = await manager.RestoreProgramsFromJsonAsync(good);
            var rejected = await manager.RestoreProgramsFromJsonAsync(malformed);

            installed.SuccessfulInstalls.Should().Be(1);
            rejected.Success.Should().BeFalse();
            rejected.ErrorMessage.Should().Contain("malformed");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [NonWindowsFact]
    public async Task DefaultProgramInstaller_MapsTheRestoreResultToASummary()
    {
        using var winget = new FakeWinget(InstallScript);

        var summary = await new DefaultProgramInstaller(new TestLogger())
            .InstallAsync([Winget("Good", "Vendor.Good"), Winget("Bad", "Vendor.Fail")], CancellationToken.None);

        summary.Succeeded.Should().Be(1);
        summary.Failed.Should().Be(1);
        summary.Error.Should().BeNull();
    }

    [NonWindowsFact]
    public async Task DefaultProgramInstaller_SurfacesAnErrorMessage_WhenWingetIsRequiredButMissing()
    {
        using var _ = FakeWinget.WithoutWinget();

        var summary = await new DefaultProgramInstaller(new TestLogger()).InstallAsync([], CancellationToken.None);

        summary.Should().Be(new ProgramInstallSummary(0, 0));
    }
}

[Collection("GlobalState")]
public class SystemProgramDiscoveryWingetTests
{
    private const string ListOutput = """
        Name                        Id                            Version      Available Source
        ---------------------------------------------------------------------------------------
        App One                     Contoso.AppOne                1.2.3                  winget
        App Two                     Fabrikam.AppTwo               2.0.1
        """;

    private static string Script(string listExit = "0", string exportBody = """{"Sources":[{"SourceDetails":{"Name":"winget"},"Packages":[{"PackageIdentifier":"Contoso.AppOne"},{"PackageIdentifier":"Only.InExport"}]}]}""") => $$"""
        case "$1" in
          list) cat <<'EOT'
        {{ListOutput}}
        EOT
            exit {{listExit}};;
          export) cat > "$3" <<'EOT'
        {{exportBody}}
        EOT
            exit 0;;
        esac
        """;

    private static async Task<List<InstalledProgram>> DiscoverAsync(string? script)
    {
        using var winget = new FakeWinget(script ?? "exit 1");
        return await new SystemProgramDiscovery(new TestLogger()).GetAllInstalledProgramsAsync();
    }

    [NonWindowsFact]
    public async Task Discover_MergesListAndExport_MarkingOnlyExportedIdsAsInstallable()
    {

        var programs = await DiscoverAsync(Script());

        programs.Single(p => p.WingetId == "Contoso.AppOne").IsWingetAvailable.Should().BeTrue();
        programs.Single(p => p.WingetId == "Fabrikam.AppTwo").IsWingetAvailable.Should().BeFalse("it is listed but not exportable");
        programs.Should().Contain(p => p.WingetId == "Only.InExport" && p.IsWingetAvailable);
        programs.Select(p => p.Name).Should().BeInAscendingOrder();
    }

    [NonWindowsFact]
    public async Task Discover_WhenWingetListFails_ReturnsNoWingetPrograms()
    {

        var programs = await DiscoverAsync(Script(listExit: "3"));

        programs.Should().NotContain(p => p.WingetId == "Contoso.AppOne");
    }

    [NonWindowsFact]
    public async Task Discover_WhenExportIsUnusable_FallsBackToTheListedSources()
    {
        var script = """
            case "$1" in
              list) cat <<'EOT'
            Name                        Id                            Version      Available Source
            ---------------------------------------------------------------------------------------
            App One                     Contoso.AppOne                1.2.3                  winget
            EOT
                exit 0;;
              export) exit 1;;
            esac
            """;

        var programs = await DiscoverAsync(script);

        programs.Single(p => p.WingetId == "Contoso.AppOne").IsWingetAvailable.Should().BeTrue();
    }

    [NonWindowsFact]
    public async Task Discover_WhenWingetIsMissing_StillReturns()
    {
        using var _ = FakeWinget.WithoutWinget();
        var logger = new TestLogger();

        var programs = await new SystemProgramDiscovery(logger).GetAllInstalledProgramsAsync();

        programs.Should().NotContain(p => p.Source == "winget");
        logger.Messages.Should().Contain(m => m.Contains("Error running winget"));
    }

    [Fact]
    public void ApplyExportedIds_FlagsMatches_DemotesTheRest_AndAddsLeftovers()
    {
        var programs = new List<InstalledProgram>
        {
            new() { Name = "A", WingetId = "Vendor.A", Source = "registry" },
            new() { Name = "B", WingetId = "Vendor.B", IsWingetAvailable = true },
            new() { Name = "NoId" }
        };
        var exported = new List<WingetExportEntry> { new("vendor.a", "msstore"), new("Vendor.Leftover", "") };

        SystemProgramDiscovery.ApplyExportedIds(programs, exported);

        programs[0].IsWingetAvailable.Should().BeTrue();
        programs[0].Source.Should().Be("msstore");
        programs[1].IsWingetAvailable.Should().BeFalse();
        programs[2].IsWingetAvailable.Should().BeFalse();
        programs.Should().ContainSingle(p => p.WingetId == "Vendor.Leftover" && p.IsWingetAvailable && p.Source == "winget" && p.Name == "Vendor.Leftover");
    }
}

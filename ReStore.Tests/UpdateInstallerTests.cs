using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

internal sealed class FakeMsixLauncher : IMsixInstallLauncher
{
    public List<(string Msix, string Log, string? Relaunch)> Launches { get; } = [];
    public void Launch(string msixPath, string logPath, string? relaunchAppUserModelId) => Launches.Add((msixPath, logPath, relaunchAppUserModelId));
}

[Collection("GlobalState")]
public class UpdateInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReStoreUpdInst_" + Guid.NewGuid().ToString("N"));
    private readonly string _updates;
    private readonly string _install;
    private readonly FakeMsixLauncher _launcher = new();

    public UpdateInstallerTests()
    {
        _updates = Path.Combine(_dir, "updates");
        _install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static string Appx(string name, string publisher) =>
        $"""<?xml version="1.0" encoding="utf-8"?><Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="{name}" Publisher="{publisher}" Version="1.8.0.0" /></Package>""";

    private static UpdateCheckResult Result(string name, byte[] payload, string version = "1.8.0", string kind = "cli-zip", string? sha = null, long? size = null, string url = "https://github.com/js302/ReStore/releases/download/v1.8.0/x") => new()
    {
        Status = UpdateStatus.UpdateAvailable,
        Current = "1.7.0",
        Latest = version,
        Asset = new UpdateAsset(kind, "x64", name, url, sha ?? Hex(payload), size ?? payload.Length)
    };

    private UpdateInstaller Installer(byte[]? payload, string channel, RepositoryCoordinator? coordinator = null, string? runningManifestDirectory = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(payload == null
            ? FakeHttpHandler.Status(status)
            : new HttpResponseMessage(status) { Content = new ByteArrayContent(payload) }));
        return new UpdateInstaller(handler, _launcher, channel, "x64", _updates, _install, runningManifestDirectory ?? _dir, coordinator ?? new RepositoryCoordinator(Path.Combine(_dir, "repo.lock")), () => "ReStore_abc123");
    }

    // ---- planning -------------------------------------------------------------------------------------------------------

    [Fact]
    public void DryRunPlan_ChangesNothing()
    {
        var payload = Zip(("restore.exe", "new"));
        var plan = Installer(payload, AppChannel.Portable).PreparePlan(Result("ReStore-cli_1.8.0_win-x64.zip", payload));
        plan.Asset.Name.Should().Be("ReStore-cli_1.8.0_win-x64.zip");
        plan.DownloadPath.Should().Be(Path.Combine(_updates, "1.8.0", "ReStore-cli_1.8.0_win-x64.zip"));
        Directory.Exists(_updates).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(_install).Should().BeEmpty();
    }

    [Fact]
    public void DevChannel_IsNotInstallable()
    {
        var payload = Zip(("restore.exe", "new"));
        var act = () => Installer(payload, AppChannel.Dev).PreparePlan(Result("a.zip", payload));
        act.Should().Throw<UpdateInstallException>().WithMessage("*built from source*");
    }

    [Fact]
    public void MissingSha_Size_Url_AndNames_AreRefused()
    {
        var payload = new byte[] { 1 };
        var installer = Installer(payload, AppChannel.Portable);
        var missingSha = Result("a.zip", payload) with { Asset = new UpdateAsset("cli-zip", "x64", "a.zip", "https://x/a.zip", null, 1) };
        installer.Invoking(i => i.PreparePlan(missingSha)).Should().Throw<UpdateInstallException>().WithMessage("*SHA-256*");
        installer.Invoking(i => i.PreparePlan(Result("a.zip", payload, url: "http://insecure/a.zip"))).Should().Throw<UpdateInstallException>().WithMessage("*https*");
        installer.Invoking(i => i.PreparePlan(Result("../evil.zip", payload))).Should().Throw<UpdateInstallException>().WithMessage("*file name*");
        installer.Invoking(i => i.PreparePlan(Result("a.zip", payload, version: "1.8.0/../.."))).Should().Throw<UpdateInstallException>();
        installer.Invoking(i => i.PreparePlan(Result("a.zip", payload) with { VersionOnly = true })).Should().Throw<UpdateInstallException>().WithMessage("*manifest*");
        installer.Invoking(i => i.PreparePlan(Result("a.zip", payload) with { Asset = null })).Should().Throw<UpdateInstallException>();
    }

    [Theory]
    [InlineData(AppChannel.Msix, "msix", "x64", true)]
    [InlineData(AppChannel.Msix, "cli-zip", "x64", false)]
    [InlineData(AppChannel.Portable, "cli-zip", "arm64", false)]
    [InlineData(AppChannel.Portable, "cli-zip", "X64", true)]
    [InlineData(AppChannel.Dev, "msix", "x64", false)]
    public void SelectAsset_MatchesKindAndArchitecture(string channel, string kind, string arch, bool found)
    {
        var assets = new[] { new UpdateAsset(kind, arch, "f", "https://x/f", "ab", 1) };
        (UpdateInstaller.SelectAsset(assets, channel, "x64") != null).Should().Be(found);
    }

    // ---- download verification ------------------------------------------------------------------------------------------

    [Fact]
    public async Task SizeMismatch_DeletesTheFile_AndRefuses()
    {
        var payload = Zip(("restore.exe", "new"));
        var installer = Installer(payload, AppChannel.Portable);
        var act = () => installer.InstallAsync(Result("a.zip", payload, size: payload.Length + 1));
        (await act.Should().ThrowAsync<UpdateInstallException>()).WithMessage("*wrong size*");
        Directory.EnumerateFiles(_updates, "*", SearchOption.AllDirectories).Should().BeEmpty();
        File.Exists(Path.Combine(_install, "restore.exe")).Should().BeFalse();
    }

    [Fact]
    public async Task ShaMismatch_DeletesTheFile_AndRefuses()
    {
        var payload = Zip(("restore.exe", "new"));
        var installer = Installer(payload, AppChannel.Portable);
        var act = () => installer.InstallAsync(Result("a.zip", payload, sha: new string('a', 64)));
        (await act.Should().ThrowAsync<UpdateInstallException>()).WithMessage("*SHA-256*");
        Directory.EnumerateFiles(_updates, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task HttpError_IsAnInstallError()
    {
        var installer = Installer(null, AppChannel.Portable, status: HttpStatusCode.NotFound);
        var act = () => installer.InstallAsync(Result("a.zip", [1]));
        (await act.Should().ThrowAsync<UpdateInstallException>()).WithMessage("*404*");
    }

    [Fact]
    public async Task Download_ReportsProgress()
    {
        var payload = Zip(("restore.exe", new string('x', 200_000)));
        var reports = new List<DownloadProgress>();
        var installer = Installer(payload, AppChannel.Portable);
        await installer.DownloadAsync(Result("a.zip", payload).Asset!, Path.Combine(_dir, "dl", "a.zip"), new Progress<DownloadProgress>(reports.Add), CancellationToken.None);
        await Task.Delay(50);
        reports.Should().NotBeEmpty();
        reports.Max(r => r.Received).Should().Be(payload.Length);
    }

    // ---- MSIX -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Msix_VerifiedPackage_LaunchesInstallerWithRelaunch()
    {
        var payload = Zip(("AppxManifest.xml", Appx("ReStore", "CN=Jaskaran Singh")), ("ReStore.exe", "x"));
        var installer = Installer(payload, AppChannel.Msix);
        var result = await installer.InstallAsync(Result("ReStore_1.8.0_x64.msix", payload, kind: "msix"), new InstallOptions(Relaunch: true));

        result.ProcessShouldExit.Should().BeTrue();
        var launch = _launcher.Launches.Should().ContainSingle().Subject;
        launch.Msix.Should().Be(Path.Combine(_updates, "1.8.0", "ReStore_1.8.0_x64.msix"));
        launch.Log.Should().EndWith("update-install.log");
        launch.Relaunch.Should().Be("ReStore_abc123!ReStore");
        File.Exists(launch.Msix).Should().BeTrue();
    }

    [Fact]
    public async Task Msix_WithoutRelaunch_DoesNotPassAnAppId()
    {
        var payload = Zip(("AppxManifest.xml", Appx("ReStore", "CN=Jaskaran Singh")));
        await Installer(payload, AppChannel.Msix).InstallAsync(Result("p.msix", payload, kind: "msix"));
        _launcher.Launches.Single().Relaunch.Should().BeNull();
    }

    [Theory]
    [InlineData("OtherApp", "CN=Jaskaran Singh", "*named*")]
    [InlineData("ReStore", "CN=Someone Else", "*published by someone*")]
    public async Task Msix_WrongIdentity_IsRefusedAndDeleted(string name, string publisher, string message)
    {
        var payload = Zip(("AppxManifest.xml", Appx(name, publisher)));
        var act = () => Installer(payload, AppChannel.Msix).InstallAsync(Result("p.msix", payload, kind: "msix"));
        (await act.Should().ThrowAsync<UpdateInstallException>()).WithMessage(message);
        _launcher.Launches.Should().BeEmpty();
        File.Exists(Path.Combine(_updates, "1.8.0", "p.msix")).Should().BeFalse();
    }

    [Fact]
    public async Task Msix_PublisherIsComparedToTheRunningPackage()
    {
        File.WriteAllText(Path.Combine(_dir, "AppxManifest.xml"), Appx("ReStore", "CN=Other Publisher"));
        var payload = Zip(("AppxManifest.xml", Appx("ReStore", "CN=Jaskaran Singh")));
        var act = () => Installer(payload, AppChannel.Msix).InstallAsync(Result("p.msix", payload, kind: "msix"));
        await act.Should().ThrowAsync<UpdateInstallException>();

        var matching = Zip(("AppxManifest.xml", Appx("ReStore", "CN=Other Publisher")));
        await Installer(matching, AppChannel.Msix).InstallAsync(Result("p2.msix", matching, kind: "msix"));
        _launcher.Launches.Should().ContainSingle();
    }

    [Fact]
    public void RunningPublisher_FallsBackToTheConstant()
    {
        UpdateInstaller.ReadRunningPublisher(Path.Combine(_dir, "nothing-here")).Should().Be("CN=Jaskaran Singh");
        File.WriteAllText(Path.Combine(_dir, "AppxManifest.xml"), "garbage");
        UpdateInstaller.ReadRunningPublisher(_dir).Should().Be("CN=Jaskaran Singh");
    }

    [Fact]
    public async Task Msix_NotAZipOrNoManifest_IsRefused()
    {
        var notZip = new byte[] { 1, 2, 3, 4 };
        var act = () => Installer(notZip, AppChannel.Msix).InstallAsync(Result("a.msix", notZip, kind: "msix"));
        await act.Should().ThrowAsync<UpdateInstallException>();

        var noManifest = Zip(("other.txt", "x"));
        var act2 = () => Installer(noManifest, AppChannel.Msix).InstallAsync(Result("b.msix", noManifest, kind: "msix"));
        await act2.Should().ThrowAsync<UpdateInstallException>();
        _launcher.Launches.Should().BeEmpty();
    }

    [Fact]
    public async Task Msix_RefusesWhileRepositoryIsBusy()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_dir, "busy.lock"), TimeSpan.FromMilliseconds(100));
        await using var held = await coordinator.AcquireMutationAsync("a running backup");
        var payload = Zip(("AppxManifest.xml", Appx("ReStore", "CN=Jaskaran Singh")));
        var installer = Installer(payload, AppChannel.Msix, coordinator);

        var act = () => installer.InstallAsync(Result("p.msix", payload, kind: "msix"), new InstallOptions(RepositoryWait: TimeSpan.FromMilliseconds(100)));
        await act.Should().ThrowAsync<RepositoryBusyException>();
        _launcher.Launches.Should().BeEmpty();
    }

    [Fact]
    public void LauncherScript_QuotesPathsSafely()
    {
        var script = PowerShellMsixInstallLauncher.BuildScript(@"C:\Users\O'Brien\ReStore\updates\1.8.0\p.msix", @"C:\Users\O'Brien\logs\update-install.log", "Fam_1!ReStore");
        script.Should().Contain("$pkg = 'C:\\Users\\O''Brien\\ReStore\\updates\\1.8.0\\p.msix'");
        script.Should().Contain("$log = 'C:\\Users\\O''Brien\\logs\\update-install.log'");
        script.Should().Contain("Add-AppxPackage -Path $pkg -ForceApplicationShutdown");
        script.Should().Contain("Start-Sleep -Seconds 2");
        script.Should().Contain(@"-ArgumentList 'shell:AppsFolder\Fam_1!ReStore'");
        script.Should().NotContain("\"");
    }

    [Fact]
    public void LauncherScript_NeutralisesInjectionAndTypographicQuotes()
    {
        var script = PowerShellMsixInstallLauncher.BuildScript("C:\\x'; Remove-Item C:\\ -Recurse; '\\p’q.msix", "C:\\log.txt", null);
        script.Should().Contain("$pkg = 'C:\\x''; Remove-Item C:\\ -Recurse; ''\\p’’q.msix'");
        script.Should().NotContain("explorer.exe");
    }

    [Fact]
    public void LauncherArguments_AreHiddenBypassCommand()
    {
        var args = PowerShellMsixInstallLauncher.BuildArguments("p.msix", "l.log", null);
        args.Should().ContainInOrder("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command");
        args[^1].Should().Contain("Invoke-CimMethod", "the helper is created through WMI so it is not a child of the package");
        var info = PowerShellMsixInstallLauncher.BuildStartInfo("p.msix", "l.log", null);
        info.FileName.Should().Be("powershell.exe");
        info.CreateNoWindow.Should().BeTrue();
        info.UseShellExecute.Should().BeFalse();
        info.ArgumentList.Should().HaveCount(args.Length);
    }

    // ---- Portable -------------------------------------------------------------------------------------------------------

    private void SeedInstall()
    {
        File.WriteAllText(Path.Combine(_install, "restore.exe"), "old exe");
        File.WriteAllText(Path.Combine(_install, "ReStore.channel"), "portable");
        File.WriteAllText(Path.Combine(_install, "config.json"), "user config");
        Directory.CreateDirectory(Path.Combine(_install, "config"));
        File.WriteAllText(Path.Combine(_install, "config", "config.example.json"), "old example");
    }

    private static byte[] PortableZip(string exe = "new exe") =>
        Zip(("restore.exe", exe), ("config/config.example.json", "new example"), ("ReStore.channel", "portable"), ("LICENSE.txt", "license"));

    [Fact]
    public async Task Portable_SwapsFiles_KeepsOldExe_AndUserConfig()
    {
        SeedInstall();
        var payload = PortableZip();
        var result = await Installer(payload, AppChannel.Portable).InstallAsync(Result("z.zip", payload));

        result.ProcessShouldExit.Should().BeFalse();
        File.ReadAllText(Path.Combine(_install, "restore.exe")).Should().Be("new exe");
        File.ReadAllText(Path.Combine(_install, "restore.exe.old")).Should().Be("old exe");
        File.ReadAllText(Path.Combine(_install, "config", "config.example.json")).Should().Be("new example");
        File.ReadAllText(Path.Combine(_install, "LICENSE.txt")).Should().Be("license");
        File.ReadAllText(Path.Combine(_install, "config.json")).Should().Be("user config");
        Directory.Exists(Path.Combine(_updates, "1.8.0", "staging")).Should().BeFalse();
        Directory.Exists(Path.Combine(_updates, "1.8.0", "rollback")).Should().BeFalse();
    }

    [Fact]
    public async Task Portable_FailureRollsBack()
    {
        SeedInstall();
        File.Delete(Path.Combine(_install, "ReStore.channel"));
        // A directory where a file must be written makes the copy fail after restore.exe was already renamed.
        Directory.CreateDirectory(Path.Combine(_install, "ReStore.channel"));
        var payload = PortableZip();

        var act = () => Installer(payload, AppChannel.Portable).InstallAsync(Result("z.zip", payload));
        (await act.Should().ThrowAsync<UpdateInstallException>()).WithMessage("*previous version was restored*");

        File.ReadAllText(Path.Combine(_install, "restore.exe")).Should().Be("old exe");
        File.Exists(Path.Combine(_install, "restore.exe.old")).Should().BeFalse();
        File.ReadAllText(Path.Combine(_install, "config", "config.example.json")).Should().Be("old example");
        File.Exists(Path.Combine(_install, "LICENSE.txt")).Should().BeFalse("files created by the failed swap are removed");
        File.ReadAllText(Path.Combine(_install, "config.json")).Should().Be("user config");
    }

    [Fact]
    public async Task Portable_ZipWithoutExe_ChangesNothing()
    {
        SeedInstall();
        var payload = Zip(("other.txt", "x"));
        var act = () => Installer(payload, AppChannel.Portable).InstallAsync(Result("z.zip", payload));
        (await act.Should().ThrowAsync<UpdateInstallException>()).WithMessage("*restore.exe*");
        File.ReadAllText(Path.Combine(_install, "restore.exe")).Should().Be("old exe");
        File.Exists(Path.Combine(_install, "restore.exe.old")).Should().BeFalse();
    }

    [Fact]
    public async Task Portable_ZipSlip_IsRefused()
    {
        SeedInstall();
        var payload = Zip(("restore.exe", "x"), ("../../evil.txt", "boom"));
        var act = () => Installer(payload, AppChannel.Portable).InstallAsync(Result("z.zip", payload));
        await act.Should().ThrowAsync<UpdateInstallException>();
        File.Exists(Path.Combine(_dir, "evil.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_updates, "evil.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(_install, "restore.exe")).Should().Be("old exe");
    }

    [Fact]
    public void CleanupLeftovers_DeletesOldExe_AndIsHarmlessWithout()
    {
        UpdateInstaller.CleanupLeftovers(_install);
        File.WriteAllText(Path.Combine(_install, "restore.exe.old"), "x");
        File.WriteAllText(Path.Combine(_install, "restore.exe"), "keep");
        UpdateInstaller.CleanupLeftovers(_install);
        File.Exists(Path.Combine(_install, "restore.exe.old")).Should().BeFalse();
        File.Exists(Path.Combine(_install, "restore.exe")).Should().BeTrue();
        UpdateInstaller.CleanupLeftovers(Path.Combine(_dir, "missing"));
    }

    [Fact]
    public async Task OtherVersionFolders_AreCleanedUp()
    {
        SeedInstall();
        Directory.CreateDirectory(Path.Combine(_updates, "1.7.5"));
        File.WriteAllText(Path.Combine(_updates, "1.7.5", "old.zip"), "x");
        var payload = PortableZip();
        await Installer(payload, AppChannel.Portable).InstallAsync(Result("z.zip", payload));
        Directory.Exists(Path.Combine(_updates, "1.7.5")).Should().BeFalse();
    }
}

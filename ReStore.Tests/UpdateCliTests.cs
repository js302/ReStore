using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.core;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

[Collection("GlobalState")]
public class UpdateCliTests : IDisposable
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreUpdCli_" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _updates;
    private readonly Dictionary<string, string?> _previousVariables = [];
    private readonly byte[] _zip;
    private string _latest = "99.0.0";
    private bool _managerFails;
    private int _requests;

    public UpdateCliTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot(_root);
        foreach (var name in new[] { "RESTORE_NON_INTERACTIVE", "CI", "RESTORE_NO_UPDATE_CHECK" })
        {
            _previousVariables[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        _install = Path.Combine(_root, "install");
        _updates = Path.Combine(_root, "updates");
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, "restore.exe"), "old exe");

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("restore.exe").Open());
            writer.Write("new exe");
        }

        _zip = stream.ToArray();
        UpdateCommands.CreateService = CreateService;
        UpdateCommands.CreateInstaller = CreateInstaller;
    }

    public void Dispose()
    {
        UpdateCommands.CreateService = () => new UpdateService();
        UpdateCommands.CreateInstaller = () => new UpdateInstaller();
        AppPaths.OverrideRoot(_previousRoot);
        foreach (var (name, value) in _previousVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try { Directory.Delete(_root, true); } catch { }
    }

    private HttpMessageHandler Handler() => new FakeHttpHandler((request, _) =>
    {
        _requests++;
        if (_managerFails)
        {
            return Task.FromResult(FakeHttpHandler.Status(HttpStatusCode.NotFound));
        }

        if (request.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_zip) });
        }

        var manifest = UpdateCheckTests.ManifestJson(_latest)
            .Replace("\"url\": \"https://x/cli64.zip\", \"sha256\": \"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\", \"size\": 456",
                $"\"url\": \"https://x/cli64.zip\", \"sha256\": \"{Convert.ToHexStringLower(SHA256.HashData(_zip))}\", \"size\": {_zip.Length}");
        return Task.FromResult(FakeHttpHandler.Json(manifest));
    });

    private string _channel = AppChannel.Portable;

    private UpdateService CreateService() =>
        new(new UpdateChecker(Handler(), "1.7.0", _channel, "x64", null, _ => null), new UpdateCache(), null, _ => null, disableUnderTestOverride: false, "1.7.0");

    private UpdateInstaller CreateInstaller() =>
        new(Handler(), new FakeMsixLauncher(), _channel, "x64", _updates, _install, _root, new RepositoryCoordinator(Path.Combine(_root, "repo.lock")));

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        await ConsoleLock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalIn = Console.In;
        try
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var outWriter = new StringWriter(stdout);
            using var errWriter = new StringWriter(stderr);
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            Console.SetIn(new StringReader(string.Empty));
            var exitCode = await Program.Main(args);
            outWriter.Flush();
            errWriter.Flush();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Console.SetIn(originalIn);
            ConsoleLock.Release();
        }
    }

    private static JsonObject Doc(string stdout) => JsonNode.Parse(stdout)!.AsObject();

    [Fact]
    public async Task Check_Json_HasTheDocumentedShape_AndExitsZero()
    {
        var (exit, stdout, _) = await RunAsync("update", "check", "--json");
        exit.Should().Be(0);
        var doc = Doc(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["current"]!.GetValue<string>().Should().Be("1.7.0");
        doc["latest"]!.GetValue<string>().Should().Be("99.0.0");
        doc["updateAvailable"]!.GetValue<bool>().Should().BeTrue();
        doc["critical"]!.GetValue<bool>().Should().BeFalse();
        doc["releaseNotesUrl"]!.GetValue<string>().Should().Contain("v99.0.0");
        doc["channel"]!.GetValue<string>().Should().Be(AppChannel.Current, "the channel of the running process");
        doc.ContainsKey("canInstall").Should().BeTrue();
        doc.ContainsKey("reason").Should().BeTrue();
        doc.ContainsKey("installCommand").Should().BeTrue();
        doc["fromCache"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Check_UpToDate_ExitsZero_AndReusesRecentAnswerUnlessForced()
    {
        _latest = "1.7.0";
        var (exit, stdout, _) = await RunAsync("update", "check", "--json");
        exit.Should().Be(0);
        Doc(stdout)["updateAvailable"]!.GetValue<bool>().Should().BeFalse();
        var afterFirst = _requests;

        var (_, second, _) = await RunAsync("update", "check", "--json");
        Doc(second)["fromCache"]!.GetValue<bool>().Should().BeTrue();
        _requests.Should().Be(afterFirst);

        await RunAsync("update", "check", "--force");
        _requests.Should().BeGreaterThan(afterFirst);
    }

    [Fact]
    public async Task Check_Failure_IsExit1_WithErrorDocument()
    {
        _managerFails = true;
        var (exit, stdout, _) = await RunAsync("update", "check", "--json");
        exit.Should().Be(1);
        var doc = Doc(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["error"]!["code"]!.GetValue<string>().Should().Be("failure");
    }

    [Fact]
    public async Task Install_NonInteractiveWithoutYes_IsExit4_AndChangesNothing()
    {
        var (exit, stdout, _) = await RunAsync("update", "install", "--non-interactive", "--json");
        exit.Should().Be(4);
        Doc(stdout)["error"]!["code"]!.GetValue<string>().Should().Be("needs_user");
        File.ReadAllText(Path.Combine(_install, "restore.exe")).Should().Be("old exe");
        Directory.Exists(_updates).Should().BeFalse();
    }

    [Fact]
    public async Task Install_DryRun_ChangesNothing_AndDescribesThePlan()
    {
        var (exit, stdout, _) = await RunAsync("update", "install", "--dry-run", "--json");
        exit.Should().Be(0);
        var doc = Doc(stdout);
        doc["dryRun"]!.GetValue<bool>().Should().BeTrue();
        doc["installed"]!.GetValue<bool>().Should().BeFalse();
        doc["latest"]!.GetValue<string>().Should().Be("99.0.0");
        doc["asset"]!["name"]!.GetValue<string>().Should().Be("ReStore-cli_99.0.0_win-x64.zip");
        File.ReadAllText(Path.Combine(_install, "restore.exe")).Should().Be("old exe");
        Directory.Exists(_updates).Should().BeFalse();
    }

    [Fact]
    public async Task Install_WithYes_UpdatesPortableInstall()
    {
        var (exit, stdout, _) = await RunAsync("update", "install", "--yes", "--non-interactive", "--json");
        exit.Should().Be(0);
        var doc = Doc(stdout);
        doc["installed"]!.GetValue<bool>().Should().BeTrue();
        doc["restartRequired"]!.GetValue<bool>().Should().BeTrue();
        File.ReadAllText(Path.Combine(_install, "restore.exe")).Should().Be("new exe");
        File.ReadAllText(Path.Combine(_install, "restore.exe.old")).Should().Be("old exe");
    }

    [Fact]
    public async Task Install_OnDevChannel_FailsWithBuiltFromSourceMessage()
    {
        _channel = AppChannel.Dev;
        var (exit, stdout, stderr) = await RunAsync("update", "install", "--yes", "--json");
        exit.Should().Be(1);
        Doc(stdout)["error"]!["message"]!.GetValue<string>().Should().Contain("built from source");
        stderr.Should().Contain("built from source");
    }

    [Fact]
    public async Task Install_WhenUpToDate_SaysSo_ExitZero()
    {
        _latest = "1.7.0";
        var (exit, stdout, _) = await RunAsync("update", "install", "--yes", "--json");
        exit.Should().Be(0);
        Doc(stdout)["installed"]!.GetValue<bool>().Should().BeFalse();
        File.ReadAllText(Path.Combine(_install, "restore.exe")).Should().Be("old exe");
    }

    [Fact]
    public async Task Skip_SetsAndClears_AndRejectsBadInput()
    {
        var (exit, stdout, _) = await RunAsync("update", "skip", "v1.8.0", "--json");
        exit.Should().Be(0);
        Doc(stdout)["skippedVersion"]!.GetValue<string>().Should().Be("1.8.0");
        new UpdateCache().Load().SkippedVersion.Should().Be("1.8.0");

        var (clearExit, clearOut, _) = await RunAsync("update", "skip", "--clear", "--json");
        clearExit.Should().Be(0);
        Doc(clearOut)["cleared"]!.GetValue<bool>().Should().BeTrue();
        new UpdateCache().Load().SkippedVersion.Should().BeNull();

        (await RunAsync("update", "skip")).ExitCode.Should().Be(2);
        (await RunAsync("update", "skip", "banana")).ExitCode.Should().Be(2);
        (await RunAsync("update", "skip", "1.8.0", "--clear")).ExitCode.Should().Be(2);
    }

    // ---- passive notice, status, background refresh ---------------------------------------------------------------------

    private void SeedCachedUpdate(string latest = "99.0.0")
    {
        var current = UpdateChecker.CurrentVersion;
        new UpdateCache().Save(new UpdateCacheData
        {
            LastCheckUtc = DateTime.UtcNow,
            Result = new UpdateCheckResult
            {
                Status = UpdateStatus.UpdateAvailable, Current = current, Latest = latest, CheckedUtc = DateTime.UtcNow,
                ReleaseNotesUrl = "https://github.com/js302/ReStore/releases/tag/v" + latest
            }
        });
    }

    [Fact]
    public async Task Notice_IsPrintedOnceOnStderr_NoNetwork()
    {
        SeedCachedUpdate();
        var (exit, stdout, stderr) = await RunAsync("version");
        exit.Should().Be(0);
        stdout.Should().NotContain("is available");
        stderr.Should().Contain("ReStore 99.0.0 is available (you have ").And.Contain("Update: restore update install - notes: https://github.com/js302/ReStore/releases/tag/v99.0.0");
        _requests.Should().Be(0);

        (await RunAsync("version")).Stderr.Should().NotContain("is available", "told within 24 hours");
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--quiet")]
    public async Task Notice_IsSuppressed_ForJsonAndQuiet(string flag)
    {
        SeedCachedUpdate();
        (await RunAsync("version", flag)).Stderr.Should().NotContain("is available");
    }

    [Fact]
    public async Task Notice_IsNotPrintedByUpdateCommands_AndNotForSkippedVersions()
    {
        SeedCachedUpdate();
        (await RunAsync("update", "skip", "--clear")).Stderr.Should().NotContain("is available");
        (await RunAsync("update", "skip", "99.0.0")).Stderr.Should().NotContain("is available");
        (await RunAsync("version")).Stderr.Should().NotContain("is available");
    }

    [Fact]
    public async Task StatusJson_HasUpdateObject_FromCacheOnly()
    {
        ConfigInitializer.EnsureConfigurationSetup();
        SeedCachedUpdate();
        var (exit, stdout, _) = await RunAsync("status", "--json");
        exit.Should().Be(0);
        var update = Doc(stdout)["update"]!.AsObject();
        update["latest"]!.GetValue<string>().Should().Be("99.0.0");
        update["updateAvailable"]!.GetValue<bool>().Should().BeTrue();
        update.ContainsKey("skippedVersion").Should().BeTrue();
        _requests.Should().Be(0);
    }

    [Fact]
    public async Task StatusText_HasOneUpdateLine()
    {
        ConfigInitializer.EnsureConfigurationSetup();
        var (_, none, _) = await RunAsync("status");
        none.Should().Contain("Updates: not checked yet");
        SeedCachedUpdate();
        var (_, available, _) = await RunAsync("status");
        available.Should().Contain("Updates: ReStore 99.0.0 is available");
    }

    [Fact]
    public async Task Registry_ListsUpdateCommands()
    {
        var (_, stdout, _) = await RunAsync("commands", "--json");
        var names = Doc(stdout)["commands"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
        names.Should().Contain(["update check", "update install", "update skip"]);
    }

    [Fact]
    public async Task RunDue_RefreshesTheCacheAtMostDaily()
    {
        ConfigInitializer.EnsureConfigurationSetup();
        var config = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        config["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = source, ["storageType"] = null });
        config["excludedPaths"] = new JsonArray();
        config["globalStorageType"] = "local";
        config["storageSources"] = new JsonObject { ["local"] = new JsonObject { ["path"] = Path.Combine(_root, "storage") } };
        File.WriteAllText(AppPaths.ConfigFile, config.ToJsonString());

        (await RunAsync("run-due")).ExitCode.Should().Be(0);
        _requests.Should().BeGreaterThan(0);
        new UpdateCache().Load().Result!.Latest.Should().Be("99.0.0");

        var before = _requests;
        (await RunAsync("run-due")).ExitCode.Should().Be(0);
        _requests.Should().Be(before, "the cache is younger than 24 hours");
    }
}

using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.telemetry;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary><c>restore telemetry ...</c>, the first-run notice and the cli.command event, driven through <c>Program.Main</c> with a private root.</summary>
[Collection("GlobalState")]
public class TelemetryCliTests : IDisposable
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreTelemetryCli_" + Guid.NewGuid().ToString("N"));

    public TelemetryCliTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot(_root);
    }

    public void Dispose()
    {
        Telemetry.Install(null);
        AppPaths.OverrideRoot(_previousRoot);
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Installs a client that is active for this root (as on a shipped build), ignoring the real environment.</summary>
    private TelemetryClient InstallActiveClient(bool noticeAlreadyShown = false)
    {
        var client = new TelemetryClient(new TelemetryClientOptions
        {
            Channel = AppChannel.Msix,
            IsTestRootOverride = () => false,
            GetEnv = _ => null,
            GetEnvNames = () => [],
            Interactive = false
        });
        if (noticeAlreadyShown)
        {
            client.MarkNoticeShown("cli");
            client = new TelemetryClient(new TelemetryClientOptions
            {
                Channel = AppChannel.Msix, IsTestRootOverride = () => false, GetEnv = _ => null, GetEnvNames = () => [], Interactive = false
            });
        }

        Telemetry.Install(client);
        return client;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        await ConsoleLock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var outWriter = new StringWriter(stdout);
            using var errWriter = new StringWriter(stderr);
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            var exitCode = await Program.Main(args);
            outWriter.Flush();
            errWriter.Flush();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleLock.Release();
        }
    }

    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    private static List<JsonObject> Events(TelemetryClient client) => client.Queue.ReadAll().Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

    [Fact]
    public async Task Status_Json_HasTheDocumentedShape_AndExplainsWhyItIsOff()
    {
        var (exitCode, stdout, _) = await RunAsync("telemetry", "status", "--json");

        exitCode.Should().Be(0);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["enabled"]!.GetValue<bool>().Should().BeFalse("the suite runs under the test root override");
        doc["reason"]!.GetValue<string>().Should().Be("test-override");
        doc["reasonText"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        doc["configEnabled"]!.GetValue<bool>().Should().BeTrue();
        doc["endpointConfigured"]!.GetValue<bool>().Should().BeFalse();
        doc["queuedEvents"]!.GetValue<int>().Should().Be(0);
        doc["noticeShown"]!.GetValue<bool>().Should().BeFalse();
        doc.ContainsKey("installId").Should().BeTrue();
        doc["channel"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Status_Text_ListsTheEssentials()
    {
        var (exitCode, stdout, _) = await RunAsync("telemetry", "status");
        exitCode.Should().Be(0);
        stdout.Should().Contain("Anonymous usage statistics: off").And.Contain("Queued events").And.Contain("telemetry.md");
    }

    [Fact]
    public async Task Status_Works_WithoutAnyConfig()
    {
        File.Exists(AppPaths.ConfigFile).Should().BeFalse();
        (await RunAsync("telemetry", "status", "--json")).ExitCode.Should().Be(0);
        File.Exists(AppPaths.ConfigFile).Should().BeFalse("status must not create a config");
    }

    [Fact]
    public async Task Disable_WritesTheConfig_ClearsTheQueue_AndEnableRestoresIt()
    {
        var client = InstallActiveClient(noticeAlreadyShown: true);
        client.Track(TelemetrySchema.AppStart, new Dictionary<string, object?> { ["startMode"] = "normal" });
        client.Queue.Count().Should().BeGreaterThan(0);

        var (exitCode, stdout, _) = await RunAsync("telemetry", "disable", "--json");
        exitCode.Should().Be(0);
        var doc = Json(stdout);
        doc["enabled"]!.GetValue<bool>().Should().BeFalse();
        doc["configEnabled"]!.GetValue<bool>().Should().BeFalse();
        doc["reason"]!.GetValue<string>().Should().Be("config");
        doc["queueCleared"]!.GetValue<bool>().Should().BeTrue();
        File.ReadAllText(AppPaths.ConfigFile).Should().MatchRegex("\"telemetry\"\\s*:\\s*\\{\\s*\"enabled\"\\s*:\\s*false");
        client.Queue.Count().Should().Be(0);

        client.Track(TelemetrySchema.AppStart, new Dictionary<string, object?> { ["startMode"] = "normal" });
        client.Queue.Count().Should().Be(0, "disabled means nothing is recorded");

        (await RunAsync("telemetry", "enable", "--json")).Stdout.Should().Contain("\"configEnabled\": true");
        File.ReadAllText(AppPaths.ConfigFile).Should().MatchRegex("\"telemetry\"\\s*:\\s*\\{\\s*\"enabled\"\\s*:\\s*true");
        client.Decision.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Enable_CountsAsHavingSeenTheNotice()
    {
        var client = InstallActiveClient();
        var (exitCode, _, stderr) = await RunAsync("telemetry", "enable");
        exitCode.Should().Be(0);
        stderr.Should().NotContain("anonymous usage statistics");
        client.StateStore.Load().NoticeShownUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Preview_ShowsTheQueuedEventsExactly()
    {
        var client = InstallActiveClient(noticeAlreadyShown: true);
        client.Track(TelemetrySchema.AppStart, new Dictionary<string, object?> { ["startMode"] = "tray" });
        var queued = client.Queue.ReadAll();

        var (_, text, _) = await RunAsync("telemetry", "preview");
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Should().Equal(queued);

        var doc = Json((await RunAsync("telemetry", "preview", "--json")).Stdout);
        doc["count"]!.GetValue<int>().Should().Be(queued.Count);
        doc["events"]!.AsArray()[0]!["name"]!.GetValue<string>().Should().Be("telemetry.notice");
    }

    [Fact]
    public async Task Preview_WhenEmpty()
    {
        (await RunAsync("telemetry", "preview")).Stdout.Should().Contain("No telemetry events");
        Json((await RunAsync("telemetry", "preview", "--json")).Stdout)["count"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task ResetId_ChangesTheInstallId()
    {
        var client = InstallActiveClient(noticeAlreadyShown: true);
        var before = client.StateStore.Load().InstallId;
        var doc = Json((await RunAsync("telemetry", "reset-id", "--json")).Stdout);
        doc["installId"]!.GetValue<string>().Should().NotBe(before).And.Be(client.StateStore.Load().InstallId);
    }

    [Fact]
    public async Task Clear_DeletesTheQueue()
    {
        var client = InstallActiveClient(noticeAlreadyShown: true);
        client.Track(TelemetrySchema.AppStart, new Dictionary<string, object?> { ["startMode"] = "tray" });
        var doc = Json((await RunAsync("telemetry", "clear", "--json")).Stdout);
        doc["cleared"]!.GetValue<bool>().Should().BeTrue();
        doc["removed"]!.GetValue<int>().Should().BeGreaterThan(0);
        client.Queue.Count().Should().Be(0);
    }

    [Fact]
    public async Task UnknownSubcommand_IsAUsageError()
    {
        var (exitCode, _, stderr) = await RunAsync("telemetry", "bogus");
        exitCode.Should().Be(2);
        stderr.Should().Contain("Unknown subcommand");
    }

    [Fact]
    public async Task FirstRun_PrintsTheNoticeToStderrOnce_AndCollectsFromTheNextRun()
    {
        var client = InstallActiveClient();

        var (exitCode, stdout, stderr) = await RunAsync("status", "--json");
        exitCode.Should().Be(0);
        stderr.Should().Contain("anonymous usage statistics").And.Contain("restore telemetry disable").And.Contain("docs/telemetry.md");
        stdout.Should().NotContain("anonymous usage statistics");
        Json(stdout)["ok"]!.GetValue<bool>().Should().BeTrue("the notice never disturbs the --json document");
        client.Queue.Count().Should().Be(0, "nothing is queued in the run that showed the notice");

        client = InstallActiveClient();
        var second = await RunAsync("status", "--json");
        second.Stderr.Should().NotContain("anonymous usage statistics");

        var events = Events(client);
        events.Select(e => e["name"]!.GetValue<string>()).Should().Equal("telemetry.notice", "cli.command");
        var props = events[1]["props"]!.AsObject();
        props["command"]!.GetValue<string>().Should().Be("status");
        props["exitCode"]!.GetValue<int>().Should().Be(0);
        props["json"]!.GetValue<bool>().Should().BeTrue();
        props["flags"]!.GetValue<string>().Should().Be("--json");
        props["durationBucket"]!.GetValue<string>().Should().BeOneOf(TelemetryBuckets.Durations);
        events[1]["surface"]!.GetValue<string>().Should().Be("cli");
    }

    [Fact]
    public async Task Quiet_DefersTheNotice_ToTheNextNonQuietRun()
    {
        var client = InstallActiveClient();

        var quiet = await RunAsync("status", "--quiet");
        quiet.Stderr.Should().NotContain("anonymous usage statistics");
        client.StateStore.Load().NoticeShownUtc.Should().BeNull();
        client.Queue.Count().Should().Be(0);

        var loud = await RunAsync("status");
        loud.Stderr.Should().Contain("anonymous usage statistics");
        client.StateStore.Load().NoticeShownUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("commands")]
    [InlineData("help")]
    public async Task InfoCommands_DoNotShowTheNotice(string command)
    {
        var client = InstallActiveClient();
        var (_, _, stderr) = await RunAsync(command);
        stderr.Should().NotContain("anonymous usage statistics");
        client.StateStore.Load().NoticeShownUtc.Should().BeNull();
    }

    [Fact]
    public async Task OptedOut_ShowsNoNotice_AndRecordsNothing()
    {
        var client = new TelemetryClient(new TelemetryClientOptions
        {
            Channel = AppChannel.Msix, IsTestRootOverride = () => false, GetEnv = n => n == "DO_NOT_TRACK" ? "1" : null, GetEnvNames = () => []
        });
        Telemetry.Install(client);

        var (_, _, stderr) = await RunAsync("status");
        await RunAsync("status");
        stderr.Should().NotContain("anonymous usage statistics");
        client.Queue.Count().Should().Be(0);
        Directory.Exists(AppPaths.StateDirectory).Should().BeTrue();
        File.Exists(client.StateStore.Path).Should().BeFalse();
    }

    [Fact]
    public async Task FailedCommands_AreRecordedWithTheirExitCode_AndNoArguments()
    {
        var client = InstallActiveClient(noticeAlreadyShown: true);
        var secret = Path.Combine(_root, "private-folder-name");
        var (exitCode, _, _) = await RunAsync("verify", secret + "/HEAD", "--mode", "quick", "--json");
        exitCode.Should().NotBe(0);

        var e = Events(client).Single(x => x["name"]!.GetValue<string>() == "cli.command");
        e["props"]!["command"]!.GetValue<string>().Should().Be("verify");
        e["props"]!["exitCode"]!.GetValue<int>().Should().Be(exitCode);
        e["props"]!["flags"]!.GetValue<string>().Should().Be("--json,--mode");
        File.ReadAllText(client.Queue.Path).Should().NotContain("private-folder-name").And.NotContain(_root);
    }

    [Fact]
    public async Task UsageErrors_AreNotRecorded()
    {
        var client = InstallActiveClient(noticeAlreadyShown: true);
        await RunAsync("bakcup");
        Events(client).Where(e => e["name"]!.GetValue<string>() == "cli.command").Should().BeEmpty();
    }
}

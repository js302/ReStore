using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.telemetry;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Anonymous usage telemetry: decision matrix, allow-list, buckets, queue, notice, sender, hooks. Never touches the network or the real profile.</summary>
[Collection("GlobalState")]
public class TelemetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestEnvironment.RootDirectory, "tel_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    public TelemetryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Telemetry.Install(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private TelemetryClient NewClient(
        Dictionary<string, string?>? env = null, bool config = true, string channel = AppChannel.Msix, bool testOverride = false,
        ITelemetrySender? sender = null, string surface = "cli")
    {
        env ??= [];
        return new TelemetryClient(new TelemetryClientOptions
        {
            Surface = surface,
            Channel = channel,
            GetEnv = name => env.GetValueOrDefault(name),
            GetEnvNames = () => env.Keys,
            IsTestRootOverride = () => testOverride,
            StateDirectory = () => _dir,
            ConfigEnabled = () => config,
            UtcNow = () => _now,
            AppVersion = "9.9.9",
            Interactive = false,
            SenderFactory = sender == null ? null : () => sender
        });
    }

    /// <summary>A client of a later run than the one that showed the notice, so collection is active.</summary>
    private TelemetryClient NewActiveClient(Dictionary<string, string?>? env = null, ITelemetrySender? sender = null, bool config = true)
    {
        var first = NewClient(env, config, sender: sender);
        if (first.StateStore.Load().NoticeShownUtc == null)
        {
            first.MarkNoticeShown("cli");
        }

        return NewClient(env, config, sender: sender);
    }

    private static Dictionary<string, object?> Props(params (string Key, object? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Bodies)
            {
                Bodies.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            }

            return await respond(request, cancellationToken);
        }
    }

    private sealed class RecordingSender(bool result = true) : ITelemetrySender
    {
        public List<IReadOnlyList<string>> Batches { get; } = [];

        public Task<bool> SendAsync(IReadOnlyList<string> eventJsonLines, CancellationToken cancellationToken)
        {
            Batches.Add(eventJsonLines);
            return Task.FromResult(result);
        }
    }

    // ---- buckets ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.2, "<1s")]
    [InlineData(1, "1-10s")]
    [InlineData(9.9, "1-10s")]
    [InlineData(10, "10-60s")]
    [InlineData(59, "10-60s")]
    [InlineData(60, "1-10m")]
    [InlineData(599, "1-10m")]
    [InlineData(600, "10-60m")]
    [InlineData(3599, "10-60m")]
    [InlineData(3600, ">1h")]
    public void DurationBuckets(double seconds, string expected) =>
        TelemetryBuckets.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Theory]
    [InlineData(-5, "0")]
    [InlineData(0, "0")]
    [InlineData(1, "1-10")]
    [InlineData(10, "1-10")]
    [InlineData(11, "11-100")]
    [InlineData(100, "11-100")]
    [InlineData(101, "101-1k")]
    [InlineData(1000, "101-1k")]
    [InlineData(1001, "1k-10k")]
    [InlineData(10000, "1k-10k")]
    [InlineData(10001, "10k-100k")]
    [InlineData(100000, "10k-100k")]
    [InlineData(100001, ">100k")]
    public void CountBuckets(long count, string expected) => TelemetryBuckets.Count(count).Should().Be(expected);

    [Theory]
    [InlineData(0L, "<1MB")]
    [InlineData(1048575L, "<1MB")]
    [InlineData(1048576L, "1-100MB")]
    [InlineData(104857599L, "1-100MB")]
    [InlineData(104857600L, "100MB-1GB")]
    [InlineData(1073741824L, "1-10GB")]
    [InlineData(10737418240L, "10-100GB")]
    [InlineData(107374182400L, ">100GB")]
    public void SizeBuckets(long bytes, string expected) => TelemetryBuckets.Size(bytes).Should().Be(expected);

    // ---- enable / disable decision matrix -------------------------------------------------------------------------

    private static TelemetryDecision Decide(bool config = true, string channel = AppChannel.Msix, bool testOverride = false, params (string, string)[] env)
    {
        var map = env.ToDictionary(e => e.Item1, e => e.Item2);
        return TelemetryEnablement.Evaluate(config, name => map.GetValueOrDefault(name), channel, testOverride);
    }

    [Fact]
    public void Enabled_ByDefault_OnShippedChannels()
    {
        Decide().Enabled.Should().BeTrue();
        Decide(channel: AppChannel.Portable).Enabled.Should().BeTrue();
    }

    [Fact]
    public void Disabled_ByConfig()
    {
        var decision = Decide(config: false);
        decision.Enabled.Should().BeFalse();
        decision.Reason.Should().Be(TelemetryEnablement.ReasonConfig);
        decision.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("RESTORE_TELEMETRY_OPTOUT", "1", "env-optout")]
    [InlineData("RESTORE_TELEMETRY_OPTOUT", "true", "env-optout")]
    [InlineData("RESTORE_TELEMETRY_OPTOUT", "YES", "env-optout")]
    [InlineData("DO_NOT_TRACK", "1", "env-do-not-track")]
    [InlineData("CI", "true", "env-ci")]
    [InlineData("CI", "1", "env-ci")]
    public void Disabled_ByEnvironment(string name, string value, string reason)
    {
        var decision = Decide(env: [(name, value)]);
        decision.Enabled.Should().BeFalse();
        decision.Reason.Should().Be(reason);
    }

    [Theory]
    [InlineData("RESTORE_TELEMETRY_OPTOUT", "0")]
    [InlineData("RESTORE_TELEMETRY_OPTOUT", "")]
    [InlineData("DO_NOT_TRACK", "0")]
    [InlineData("CI", "false")]
    public void NotDisabled_ByFalsyEnvironment(string name, string value) =>
        Decide(env: [(name, value)]).Enabled.Should().BeTrue();

    [Fact]
    public void DevChannel_IsOff_UnlessDevVariable()
    {
        Decide(channel: AppChannel.Dev).Reason.Should().Be(TelemetryEnablement.ReasonDevChannel);
        Decide(channel: AppChannel.Dev, env: [("RESTORE_TELEMETRY_DEV", "1")]).Enabled.Should().BeTrue();
        Decide(channel: AppChannel.Dev, env: [("RESTORE_TELEMETRY_DEV", "1"), ("RESTORE_TELEMETRY_OPTOUT", "1")]).Enabled.Should().BeFalse();
    }

    [Fact]
    public void TestRootOverride_AlwaysDisables() =>
        Decide(testOverride: true, channel: AppChannel.Dev, env: [("RESTORE_TELEMETRY_DEV", "1")]).Reason.Should().Be(TelemetryEnablement.ReasonTestOverride);

    [Fact]
    public void AppPaths_ReportsTheOverride() => AppPaths.IsRootOverridden.Should().BeTrue("the test suite runs with an AppPaths override");

    [Fact]
    public void RealClient_IsDisabledUnderTheTestOverride()
    {
        var client = new TelemetryClient();
        client.Decision.Enabled.Should().BeFalse();
        client.Track(TelemetrySchema.Notice);
        client.Queue.Count().Should().Be(0);
    }

    [Theory]
    [InlineData("{\"telemetry\":{\"enabled\":false}}", false)]
    [InlineData("{\"telemetry\":{\"enabled\":true}}", true)]
    [InlineData("{\"other\":1}", true)]
    [InlineData("{ not json", true)]
    [InlineData("{\"telemetry\":{\"enabled\":\"no\"}}", true)]
    public void ReadConfigEnabled_ParsesTheConfigFile(string json, bool expected)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        TelemetryEnablement.ReadConfigEnabled(path).Should().Be(expected);
        TelemetryEnablement.ReadConfigEnabled(Path.Combine(_dir, "missing.json")).Should().BeTrue();
    }

    [Fact]
    public void Client_ReReadsTheConfig_WhenItChanges()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{\"telemetry\":{\"enabled\":true}}");
        var client = new TelemetryClient(new TelemetryClientOptions { ConfigFile = () => path, StateDirectory = () => _dir });
        client.ConfigEnabled.Should().BeTrue();
        File.WriteAllText(path, "{\"telemetry\":{\"enabled\":false,\"x\":1}}");
        client.ConfigEnabled.Should().BeFalse();
    }

    // ---- notice and deferred collection ---------------------------------------------------------------------------

    [Fact]
    public void Notice_IsNeededOnce_AndNothingIsQueuedInTheRunThatShowedIt()
    {
        var run1 = NewClient();
        run1.NoticeNeeded.Should().BeTrue();
        run1.MarkNoticeShown("cli");
        run1.NoticeNeeded.Should().BeFalse();
        run1.Track(TelemetrySchema.AppStart, Props(("startMode", "normal")));
        run1.Queue.Count().Should().Be(0, "collection starts with the next run");

        var state = run1.StateStore.Load();
        state.NoticeShownUtc.Should().Be(_now);
        state.NoticeSurface.Should().Be("cli");
        state.InstallId.Should().NotBeNullOrEmpty();

        var run2 = NewClient();
        run2.NoticeNeeded.Should().BeFalse("the notice is shared by CLI and GUI through telemetry.json");
        run2.Track(TelemetrySchema.AppStart, Props(("startMode", "normal")));
        var events = run2.Queue.ReadAll().Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        events.Select(e => e["name"]!.GetValue<string>()).Should().Equal("telemetry.notice", "app.start");
    }

    [Fact]
    public void Notice_IsNotNeeded_WhenTelemetryIsAlreadyOff()
    {
        NewClient(config: false).NoticeNeeded.Should().BeFalse();
        NewClient(env: new() { ["RESTORE_TELEMETRY_OPTOUT"] = "1" }).NoticeNeeded.Should().BeFalse();
        NewClient(channel: AppChannel.Dev).NoticeNeeded.Should().BeFalse();
    }

    [Fact]
    public void NothingIsQueued_BeforeTheNoticeWasEverShown()
    {
        var client = NewClient();
        client.Track(TelemetrySchema.AppStart, Props(("startMode", "tray")));
        client.Queue.Count().Should().Be(0);
        File.Exists(client.StateStore.Path).Should().BeFalse();
    }

    [Fact]
    public void DisabledClient_RecordsNothing_AndCreatesNoFiles()
    {
        var client = NewClient(config: false);
        client.Track(TelemetrySchema.AppStart, Props(("startMode", "tray")));
        Directory.GetFileSystemEntries(_dir).Should().BeEmpty();
    }

    [Fact]
    public void ActiveClient_StopsRecording_WhenOptedOutByEnvironment()
    {
        NewActiveClient();
        var optedOut = NewClient(env: new() { ["DO_NOT_TRACK"] = "1" });
        optedOut.Track(TelemetrySchema.AppStart, Props(("startMode", "tray")));
        optedOut.Queue.Count().Should().Be(0);
    }

    // ---- event shape -----------------------------------------------------------------------------------------------

    [Fact]
    public void Events_CarryTheCommonProperties()
    {
        var env = new Dictionary<string, string?> { ["CLAUDECODE"] = "1" };
        var client = NewActiveClient(env);
        client.Track(TelemetrySchema.AppStart, Props(("startMode", "tray")));

        var line = client.Queue.ReadAll().Last();
        var e = JsonNode.Parse(line)!.AsObject();
        e["name"]!.GetValue<string>().Should().Be("app.start");
        e["ts"]!.GetValue<string>().Should().Be("2026-05-01T12:00:00Z");
        e["appVersion"]!.GetValue<string>().Should().Be("9.9.9");
        e["channel"]!.GetValue<string>().Should().Be("msix");
        e["arch"]!.GetValue<string>().Should().Be(AppChannel.Architecture);
        e["osVersion"]!.GetValue<string>().Should().MatchRegex(@"^\d+\.\d+\.\d+$");
        e["surface"]!.GetValue<string>().Should().Be("cli");
        e["sessionId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        e["installId"]!.GetValue<string>().Should().Be(client.StateStore.Load().InstallId);
        e["agent"]!.GetValue<string>().Should().Be("claude-code");
        e["interactive"]!.GetValue<bool>().Should().BeFalse();
        e["props"]!["startMode"]!.GetValue<string>().Should().Be("tray");
    }

    [Fact]
    public void InstallId_IsNotTheRepositoryId_AndResetChangesIt()
    {
        var client = NewActiveClient();
        var before = client.StateStore.Load().InstallId;
        Guid.TryParse(before, out _).Should().BeTrue();
        var after = client.ResetInstallId();
        after.Should().NotBe(before);
        client.StateStore.Load().InstallId.Should().Be(after);
        client.StateStore.Load().NoticeShownUtc.Should().NotBeNull("resetting the id keeps the notice state");
    }

    [Fact]
    public void Agent_Detection()
    {
        static string? Detect(Dictionary<string, string?> env) => TelemetryAgent.Detect(n => env.GetValueOrDefault(n), () => env.Keys);
        Detect([]).Should().BeNull();
        Detect(new() { ["RESTORE_AGENT"] = "My Agent/../x_1" }).Should().Be("myagentx1");
        Detect(new() { ["RESTORE_AGENT"] = new string('a', 80) }).Should().HaveLength(32);
        Detect(new() { ["RESTORE_AGENT"] = "custom", ["CLAUDECODE"] = "1" }).Should().Be("custom");
        Detect(new() { ["CLAUDECODE"] = "1" }).Should().Be("claude-code");
        Detect(new() { ["CURSOR_TRACE_ID"] = "x" }).Should().Be("cursor");
        Detect(new() { ["TERM_PROGRAM"] = "vscode" }).Should().Be("vscode");
        Detect(new() { ["GEMINI_CLI"] = "1" }).Should().Be("gemini-cli");
        Detect(new() { ["CODEX_SANDBOX"] = "seatbelt" }).Should().Be("codex");
        Detect(new() { ["TERM_PROGRAM"] = "iTerm.app" }).Should().BeNull();
    }

    [Fact]
    public void NoAgent_MeansNoAgentField()
    {
        var client = NewActiveClient();
        client.Track(TelemetrySchema.AppStart, Props(("startMode", "normal")));
        JsonNode.Parse(client.Queue.ReadAll().Last())!.AsObject().ContainsKey("agent").Should().BeFalse();
    }

    // ---- allow-list ------------------------------------------------------------------------------------------------

    [Fact]
    public void UnknownEvents_AreRejected()
    {
        TelemetrySchema.TryBuild("backup.path", null, out _).Should().BeFalse();
        TelemetrySchema.TryBuild(null, null, out _).Should().BeFalse();
        var client = NewActiveClient();
        client.Track("custom.event", Props(("a", "b")));
        client.Queue.Count().Should().Be(0);
    }

    [Fact]
    public void UnknownProperties_AreDropped()
    {
        TelemetrySchema.TryBuild(TelemetrySchema.AppStart, Props(("startMode", "tray"), ("path", "x"), ("user", "bob")), out var clean).Should().BeTrue();
        clean.Keys.Should().Equal("startMode");
    }

    private const string PathLike = @"C:\Users\bob\Documents\secret plan.docx";

    [Fact]
    public void PathLikeValues_NeverPassTheAllowList()
    {
        var paths = new[] { PathLike, "/home/bob/secret", "\\\\server\\share\\x", "bob@host", "bob-laptop", "my-bucket-name", "System.IO.FileNotFoundException: " + PathLike };
        foreach (var name in TelemetrySchema.EventNames)
        {
            foreach (var property in TelemetrySchema.PropertyNames(name))
            {
                foreach (var value in paths)
                {
                    TelemetrySchema.TryBuild(name, Props((property, value)), out var clean).Should().BeTrue();
                    clean.Should().BeEmpty($"{name}.{property} must not accept '{value}'");
                }
            }
        }
    }

    [Fact]
    public void PathLikeValues_NeverReachTheQueue()
    {
        var client = NewActiveClient();
        foreach (var name in TelemetrySchema.EventNames)
        {
            var props = TelemetrySchema.PropertyNames(name).ToDictionary(p => p, _ => (object?)PathLike);
            props["path"] = PathLike;
            client.Track(name, props);
        }

        client.Track(TelemetrySchema.CliCommand, Props(("command", "backup"), ("flags", "--json," + PathLike)));
        client.Track(TelemetrySchema.ErrorUnhandled, Props(("surface", "cli"), ("exceptionType", "Acme.Secret.Thing")));

        var text = File.ReadAllText(client.Queue.Path);
        text.Should().NotContain("bob").And.NotContain("Documents").And.NotContain("secret").And.NotContain("Acme").And.NotContain(@"C:\\");
    }

    [Fact]
    public void ClosedSets_AreEnforced()
    {
        TelemetrySchema.TryBuild(TelemetrySchema.AppStart, Props(("startMode", "weird")), out var a); a.Should().BeEmpty();
        TelemetrySchema.TryBuild(TelemetrySchema.CliCommand, Props(("command", "nope")), out var b); b.Should().BeEmpty();
        TelemetrySchema.TryBuild(TelemetrySchema.CliCommand, Props(("command", "config show"), ("exitCode", 300)), out var c);
        c.Keys.Should().Equal("command");
        TelemetrySchema.TryBuild(TelemetrySchema.CliCommand, Props(("durationBucket", "3.5s"), ("json", "yes")), out var d); d.Should().BeEmpty();
        TelemetrySchema.TryBuild(TelemetrySchema.Activity, Props(("type", ActivityType.Verify), ("result", ActivityResult.Warning), ("storageType", "ftp")), out var e);
        e.Keys.Should().Equal("result", "type");
        e["type"].Should().Be("Verify");
    }

    [Fact]
    public void Flags_AreRegistryNames_SortedAndDeduplicated()
    {
        TelemetrySchema.TryBuild(TelemetrySchema.CliCommand, Props(("flags", "--yes,--json,--json")), out var clean);
        clean["flags"].Should().Be("--json,--yes");
        TelemetrySchema.TryBuild(TelemetrySchema.CliCommand, Props(("flags", "--json,--bogus")), out var bad);
        bad.Should().BeEmpty();
        TelemetrySchema.TryBuild(TelemetrySchema.CliCommand, Props(("flags", "")), out var none);
        none["flags"].Should().Be("");
    }

    [Fact]
    public void ExceptionType_OnlyForSystemAndReStoreTypes()
    {
        TelemetrySchema.TryBuild(TelemetrySchema.ErrorUnhandled, Props(("exceptionType", "System.IO.IOException")), out var ok);
        ok["exceptionType"].Should().Be("System.IO.IOException");
        TelemetrySchema.TryBuild(TelemetrySchema.ErrorUnhandled, Props(("exceptionType", "ReStore.Core.Foo+Bar`1")), out var nested);
        nested.Should().ContainKey("exceptionType");
        TelemetrySchema.TryBuild(TelemetrySchema.ErrorUnhandled, Props(("exceptionType", "Google.Apis.GoogleApiException")), out var third);
        third.Should().BeEmpty();
        TelemetrySchema.TryBuild(TelemetrySchema.ErrorUnhandled, Props(("exceptionType", "System.IO.IOException at C:\\x")), out var smuggled);
        smuggled.Should().BeEmpty();
    }

    // ---- queue -----------------------------------------------------------------------------------------------------

    private TelemetryQueue NewQueue(int maxEvents = 2000, long maxBytes = 1024 * 1024) =>
        new(Path.Combine(_dir, "q_" + Guid.NewGuid().ToString("N") + ".jsonl"), maxEvents, maxBytes);

    private static string Line(int i, int pad = 0) => "{\"n\":" + i + (pad > 0 ? ",\"p\":\"" + new string('x', pad) + "\"" : "") + "}";

    [Fact]
    public void Queue_AppendsAndReadsInOrder_AndRemovesSent()
    {
        var queue = NewQueue();
        for (var i = 0; i < 5; i++)
        {
            queue.Append(Line(i)).Should().BeTrue();
        }

        queue.ReadAll().Should().Equal(Enumerable.Range(0, 5).Select(i => Line(i)));
        queue.Remove([Line(1), Line(3)]).Should().BeTrue();
        queue.ReadAll().Should().Equal(Line(0), Line(2), Line(4));
        queue.Clear().Should().BeTrue();
        queue.Count().Should().Be(0);
    }

    [Fact]
    public void Queue_RejectsMultilineAndEmpty()
    {
        var queue = NewQueue();
        queue.Append("").Should().BeFalse();
        queue.Append("{\"a\":1}\n{\"b\":2}").Should().BeFalse();
        queue.Count().Should().Be(0);
    }

    [Fact]
    public void Queue_DropsTheOldest_WhenTooManyEvents()
    {
        var queue = NewQueue(maxEvents: 50, maxBytes: 64 * 1024);
        for (var i = 0; i < 400; i++)
        {
            queue.Append(Line(i, pad: 200));
        }

        var lines = queue.ReadAll();
        lines.Count.Should().BeLessThanOrEqualTo(50).And.BeGreaterThan(10);
        lines.Last().Should().Be(Line(399, 200), "the newest events are kept");
        lines.First().Should().NotBe(Line(0, 200));
    }

    [Fact]
    public void Queue_DropsTheOldest_WhenTooManyBytes()
    {
        var queue = NewQueue(maxEvents: 100000, maxBytes: 20 * 1024);
        for (var i = 0; i < 200; i++)
        {
            queue.Append(Line(i, pad: 300));
        }

        new FileInfo(queue.Path).Length.Should().BeLessThanOrEqualTo(20 * 1024 + 400);
        queue.ReadAll().Last().Should().Be(Line(199, 300));
    }

    [Fact]
    public void Queue_ToleratesAPartialLastLine()
    {
        var queue = NewQueue();
        queue.Append(Line(1)).Should().BeTrue();
        File.AppendAllText(queue.Path, "{\"n\":2,\"half");
        queue.ReadAll().Should().Equal(Line(1));

        queue.Append(Line(3)).Should().BeTrue();
        queue.ReadAll().Should().Equal(Line(1), Line(3));
    }

    [Fact]
    public void Queue_SkipsGarbageLines()
    {
        var queue = NewQueue();
        File.WriteAllText(queue.Path, "garbage\n\n[1,2]\n{\"ok\":true}\n{broken}\n");
        queue.ReadAll().Should().Equal("{\"ok\":true}");
    }

    [Fact]
    public async Task Queue_TwoWritersDoNotCorruptEachOther()
    {
        var path = Path.Combine(_dir, "shared.jsonl");
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(writer => Task.Run(() =>
        {
            var queue = new TelemetryQueue(path);
            var ok = 0;
            for (var i = 0; i < 150; i++)
            {
                if (queue.Append("{\"w\":" + writer + ",\"i\":" + i + ",\"pad\":\"" + new string('y', 100) + "\"}"))
                {
                    ok++;
                }
            }

            return ok;
        })));

        var lines = new TelemetryQueue(path).ReadAll();
        lines.Should().HaveCount(results.Sum());
        results.Sum().Should().BeGreaterThan(250);
        lines.Distinct().Should().HaveCount(lines.Count);
        foreach (var writer in new[] { 0, 1 })
        {
            lines.Where(l => l.StartsWith("{\"w\":" + writer + ",", StringComparison.Ordinal))
                .Select(l => JsonNode.Parse(l)!["i"]!.GetValue<int>()).Should().BeInAscendingOrder();
        }
    }

    [Fact]
    public void Queue_GivesUpQuickly_WhenTheFileStaysLocked()
    {
        var queue = NewQueue();
        queue.Append(Line(1));
        using var held = new FileStream(queue.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        queue.Append(Line(2)).Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    // ---- sender and flush ------------------------------------------------------------------------------------------

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    [Fact]
    public void Endpoint_ResolvesFromEnvironment_AndDefaultsToNone()
    {
        TelemetryEndpoint.Default.Should().BeNull("no collection endpoint is decided yet");
        TelemetryEndpoint.Resolve(_ => null).Should().BeNull();
        TelemetryEndpoint.Resolve(_ => "not a url").Should().BeNull();
        TelemetryEndpoint.Resolve(_ => "ftp://x/y").Should().BeNull();
        TelemetryEndpoint.Resolve(_ => "https://example.test/ingest")!.Host.Should().Be("example.test");
    }

    [Fact]
    public async Task HttpSender_PostsAnEventsDocument_AndSucceedsOn2xx()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Status(HttpStatusCode.Accepted)));
        using var sender = new HttpTelemetrySender(new Uri("https://example.test/ingest"), handler);
        (await sender.SendAsync([Line(1), Line(2)], CancellationToken.None)).Should().BeTrue();

        var body = JsonNode.Parse(handler.Bodies.Single())!.AsObject();
        body["events"]!.AsArray().Should().HaveCount(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task HttpSender_FailsOnNon2xx(HttpStatusCode code)
    {
        using var sender = new HttpTelemetrySender(new Uri("https://example.test/"), new StubHandler((_, _) => Task.FromResult(Status(code))));
        (await sender.SendAsync([Line(1)], CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task HttpSender_FailsOnNetworkErrorAndTimeout()
    {
        using var broken = new HttpTelemetrySender(new Uri("https://example.test/"), new StubHandler((_, _) => throw new HttpRequestException("down")));
        (await broken.SendAsync([Line(1)], CancellationToken.None)).Should().BeFalse();

        using var slow = new HttpTelemetrySender(new Uri("https://example.test/"), new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Status(HttpStatusCode.OK);
        }));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        (await slow.SendAsync([Line(1)], cts.Token)).Should().BeFalse();
    }

    [Fact]
    public async Task Flush_RemovesEvents_OnlyAfterSuccess()
    {
        var sender = new RecordingSender(true);
        var client = NewActiveClient(sender: sender);
        for (var i = 0; i < 120; i++)
        {
            client.Track(TelemetrySchema.AppStart, Props(("startMode", "normal")));
        }

        var queued = client.Queue.Count();
        queued.Should().BeGreaterThan(100);
        var sent = await client.FlushAsync(TimeSpan.FromSeconds(5));
        sent.Should().Be(queued);
        client.Queue.Count().Should().Be(0);
        sender.Batches.Should().HaveCountGreaterThan(2).And.OnlyContain(b => b.Count <= 50);
    }

    [Fact]
    public async Task Flush_KeepsEvents_OnFailure_AndBacksOff()
    {
        var sender = new RecordingSender(false);
        var client = NewActiveClient(sender: sender);
        client.Track(TelemetrySchema.AppStart, Props(("startMode", "normal")));
        var queued = client.Queue.Count();

        (await client.FlushAsync(TimeSpan.FromSeconds(5))).Should().Be(0);
        client.Queue.Count().Should().Be(queued);
        var state = client.StateStore.Load();
        state.FailureCount.Should().Be(1);
        state.NextAttemptUtc.Should().BeAfter(_now);

        sender.Batches.Clear();
        (await client.FlushAsync(TimeSpan.FromSeconds(5))).Should().Be(0);
        sender.Batches.Should().BeEmpty("the backoff has not elapsed");

        _now = _now.AddHours(1);
        await client.FlushAsync(TimeSpan.FromSeconds(5));
        sender.Batches.Should().HaveCount(1);
        client.StateStore.Load().FailureCount.Should().Be(2);
    }

    [Fact]
    public async Task Flush_ResetsTheBackoff_AfterSuccess()
    {
        var failing = NewActiveClient(sender: new RecordingSender(false));
        failing.Track(TelemetrySchema.AppStart, Props(("startMode", "normal")));
        await failing.FlushAsync(TimeSpan.FromSeconds(5));
        _now = _now.AddDays(1);

        var ok = NewClient(sender: new RecordingSender(true));
        (await ok.FlushAsync(TimeSpan.FromSeconds(5))).Should().BeGreaterThan(0);
        ok.StateStore.Load().FailureCount.Should().Be(0);
        ok.StateStore.Load().NextAttemptUtc.Should().BeNull();
    }

    [Fact]
    public async Task Flush_Timeout_KeepsEvents_WithinTheBudget()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Status(HttpStatusCode.OK);
        });
        using var sender = new HttpTelemetrySender(new Uri("https://example.test/"), handler);
        var client = NewActiveClient(sender: sender);
        client.Track(TelemetrySchema.AppStart, Props(("startMode", "normal")));
        var queued = client.Queue.Count();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        (await client.FlushAsync(TimeSpan.FromMilliseconds(200))).Should().Be(0);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        client.Queue.Count().Should().Be(queued);
    }

    [Fact]
    public async Task Flush_WithoutEndpoint_DoesNothing()
    {
        var client = NewActiveClient();
        client.Track(TelemetrySchema.AppStart, Props(("startMode", "normal")));
        var queued = client.Queue.Count();
        client.Endpoint.Should().BeNull();
        (await client.FlushAsync(TimeSpan.FromSeconds(1))).Should().Be(0);
        client.Queue.Count().Should().Be(queued);
    }

    [Fact]
    public async Task Flush_WhenDisabled_DoesNotSend()
    {
        var sender = new RecordingSender();
        var client = NewActiveClient(sender: sender);
        client.Track(TelemetrySchema.AppStart, Props(("startMode", "normal")));
        var disabled = NewClient(config: false, sender: sender);
        (await disabled.FlushAsync(TimeSpan.FromSeconds(1))).Should().Be(0);
        sender.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task FlushTimer_TickRunsOneFlushAtATime()
    {
        var calls = 0;
        var gate = new TaskCompletionSource();
        using var timer = new TelemetryFlushTimer(TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), async _ =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
        });
        var first = timer.TickAsync();
        await timer.TickAsync();
        calls.Should().Be(1);
        gate.SetResult();
        await first;
        await timer.TickAsync();
        calls.Should().Be(2);
    }

    // ---- hooks -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ActivityRecorder_CountsOperations_WithoutTargetsOrMessages()
    {
        var client = NewActiveClient();
        Telemetry.Install(client);

        using (var scope = ActivityRecorder.Begin(null, ActivityType.Verify, @"C:\Users\bob\secret\HEAD"))
        {
            scope.Warning(@"1 file skipped: C:\Users\bob\x.txt");
        }

        ActivityRecorder.RecordFailure(null, ActivityType.Restore, @"D:\private", DateTime.UtcNow.AddMinutes(-2), new IOException(@"cannot read C:\Users\bob"));

        var events = client.Queue.ReadAll().Select(l => JsonNode.Parse(l)!.AsObject()).Where(e => e["name"]!.GetValue<string>() == "activity").ToList();
        events.Should().HaveCount(2);
        events[0]["props"]!["type"]!.GetValue<string>().Should().Be("Verify");
        events[0]["props"]!["result"]!.GetValue<string>().Should().Be("Warning");
        events[0]["props"]!["durationBucket"]!.GetValue<string>().Should().Be("<1s");
        events[1]["props"]!["type"]!.GetValue<string>().Should().Be("Restore");
        events[1]["props"]!["result"]!.GetValue<string>().Should().Be("Failed");
        events[1]["props"]!["durationBucket"]!.GetValue<string>().Should().Be("1-10m");

        var text = File.ReadAllText(client.Queue.Path);
        text.Should().NotContain("bob").And.NotContain("private").And.NotContain("skipped");
    }

    [Fact]
    public void SystemStateBackupRun_IsCountedWithBuckets()
    {
        var client = NewActiveClient();
        Telemetry.Install(client);

        var state = new SystemState(new TestLogger());
        var report = BackupRunReport.Create(
            @"C:\Users\bob\Photos", "s3", Enumerable.Range(0, 150).Select(i => $"C:\\Users\\bob\\Photos\\{i}.jpg"), [], 0,
            [new SkippedFile(@"C:\Users\bob\Photos\locked.db", "locked", false)], TimeSpan.FromSeconds(75), 5L * 1024 * 1024 * 1024, DateTime.UtcNow);
        state.RecordBackupRun(report);

        var e = client.Queue.ReadAll().Select(l => JsonNode.Parse(l)!.AsObject()).Single(x => x["name"]!.GetValue<string>() == "activity");
        var props = e["props"]!.AsObject();
        props["type"]!.GetValue<string>().Should().Be("Backup");
        props["result"]!.GetValue<string>().Should().Be("Warning");
        props["storageType"]!.GetValue<string>().Should().Be("s3");
        props["durationBucket"]!.GetValue<string>().Should().Be("1-10m");
        props["fileCountBucket"]!.GetValue<string>().Should().Be("101-1k");
        props["bytesBucket"]!.GetValue<string>().Should().Be("1-10GB");
        props["skippedCountBucket"]!.GetValue<string>().Should().Be("1-10");
        File.ReadAllText(client.Queue.Path).Should().NotContain("bob").And.NotContain("Photos");
    }

    [Fact]
    public void UnknownStorageType_IsReportedAsOther()
    {
        var client = NewActiveClient();
        Telemetry.Install(client);
        new SystemState(new TestLogger()).RecordBackupRun(BackupRunReport.Create("g", "my-private-bucket-store", [], [], 0, [], TimeSpan.Zero, 0, DateTime.UtcNow));
        client.Queue.ReadAll().Single(l => l.Contains("\"activity\"")).Should().Contain("\"storageType\":\"other\"").And.NotContain("private");
    }

    [Fact]
    public void TrackUnhandled_SendsOnlyTheTypeName()
    {
        var client = NewActiveClient();
        Telemetry.Install(client);
        Telemetry.TrackUnhandled(new InvalidOperationException(@"failed for C:\Users\bob\file.txt"));
        Telemetry.TrackUnhandled(new Xunit.Sdk.XunitException("x"));

        var lines = client.Queue.ReadAll().Where(l => l.Contains("error.unhandled")).ToList();
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("System.InvalidOperationException").And.NotContain("bob");
        lines[1].Should().NotContain("exceptionType", "only System.* and ReStore.* types are named");
        lines[1].Should().NotContain("Xunit");
    }

    [Fact]
    public void Track_NeverThrows_EvenWhenTheStateDirectoryIsUnusable()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var client = new TelemetryClient(new TelemetryClientOptions
        {
            Channel = AppChannel.Msix, IsTestRootOverride = () => false, ConfigEnabled = () => true,
            GetEnv = _ => null, StateDirectory = () => Path.Combine(blocker, "sub")
        });
        var act = () => client.Track(TelemetrySchema.AppStart, Props(("startMode", "normal")));
        act.Should().NotThrow();
    }
}

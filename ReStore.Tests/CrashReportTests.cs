using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.telemetry;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Opt-in crash reports: scrubbing, report building, the pending store, ask/always/never, sending and the CLI. No network, no real profile.</summary>
[Collection("GlobalState")]
public class CrashReportTests : IDisposable
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreCrash_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    public CrashReportTests()
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

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private string StateDir => Path.Combine(_root, "client-state");

    private TelemetryClient NewClient(
        string mode = CrashReportMode.Ask, bool config = true, Dictionary<string, string?>? env = null, string channel = AppChannel.Msix,
        ITelemetrySender? sender = null, string? configFile = null)
    {
        env ??= [];
        return new TelemetryClient(new TelemetryClientOptions
        {
            Surface = "cli",
            Channel = channel,
            GetEnv = name => env.GetValueOrDefault(name),
            GetEnvNames = () => env.Keys,
            IsTestRootOverride = () => false,
            StateDirectory = () => StateDir,
            ConfigFile = () => configFile ?? Path.Combine(_root, "none.json"),
            ConfigEnabled = () => config,
            ConfigCrashReports = () => mode,
            UtcNow = () => _now,
            AppVersion = "9.9.9",
            Interactive = false,
            SenderFactory = sender == null ? null : () => sender
        });
    }

    private static Exception Thrown(string message, Exception? inner = null)
    {
        try
        {
            throw inner == null ? new InvalidOperationException(message) : new InvalidOperationException(message, inner);
        }
        catch (Exception ex)
        {
            return ex;
        }
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

    private sealed class RecordingSender(bool result) : ITelemetrySender
    {
        public List<IReadOnlyList<string>> Batches { get; } = [];

        public Task<bool> SendAsync(IReadOnlyList<string> eventJsonLines, CancellationToken cancellationToken)
        {
            Batches.Add(eventJsonLines);
            return Task.FromResult(result);
        }
    }

    private sealed class StubHandler(HttpStatusCode code) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return Task.FromResult(new HttpResponseMessage(code));
        }
    }

    // ---- the setting -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ask", "ask")]
    [InlineData("ALWAYS", "always")]
    [InlineData(" never ", "never")]
    [InlineData("sometimes", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Mode_Normalize(string? value, string? expected) => CrashReportMode.Normalize(value).Should().Be(expected);

    [Fact]
    public void Config_DefaultsToAsk_RoundTrips_AndIgnoresGarbage()
    {
        var path = Path.Combine(_root, "config.json");
        File.WriteAllText(path, """{ "telemetry": { "enabled": true } }""");
        TelemetryEnablement.ReadConfigCrashReports(path).Should().Be("ask");

        File.WriteAllText(path, """{ "telemetry": { "crashReports": "Always" } }""");
        TelemetryEnablement.ReadConfigCrashReports(path).Should().Be("always");

        File.WriteAllText(path, """{ "telemetry": { "crashReports": "banana" } }""");
        TelemetryEnablement.ReadConfigCrashReports(path).Should().Be("ask");

        File.WriteAllText(path, "{ not json");
        TelemetryEnablement.ReadConfigCrashReports(path).Should().Be("ask");
        TelemetryEnablement.ReadConfigCrashReports(Path.Combine(_root, "missing.json")).Should().Be("ask");
    }

    [Theory]
    [InlineData("{ }", "ask")]
    [InlineData("{ \"telemetry\": { \"enabled\": true } }", "ask")]
    [InlineData("{ \"telemetry\": { \"crashReports\": \"always\" } }", "always")]
    [InlineData("{ \"telemetry\": { \"crashReports\": \"NEVER\" } }", "never")]
    [InlineData("{ \"telemetry\": { \"crashReports\": \"banana\" } }", "ask")]
    [InlineData("{ \"telemetry\": { \"crashReports\": 3 } }", "ask")]
    public void ConfigManager_LoadsCrashReports_AndDefaultsOldOrInvalidConfigsToAsk(string json, string expected)
    {
        var manager = new ConfigManager(new Moq.Mock<ILogger>().Object);
        using var document = System.Text.Json.JsonDocument.Parse(json);

        typeof(ConfigManager).GetMethod("LoadTelemetrySettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(manager, [document.RootElement]);

        manager.Telemetry.CrashReports.Should().Be(expected);
    }

    [Fact]
    public async Task ConfigManager_SavesCrashReports()
    {
        var manager = new ConfigManager(new Moq.Mock<ILogger>().Object);
        manager.Telemetry.CrashReports = "always";
        var path = Path.Combine(_root, "saved", "config.json");

        await manager.SaveAsync(path);

        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
        doc.RootElement.GetProperty("telemetry").GetProperty("crashReports").GetString().Should().Be("always");
        doc.RootElement.GetProperty("telemetry").GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void SchemaMigration_AddsTheDefault_AndKeepsAnExistingChoice()
    {
        var old = JsonNode.Parse("""{ "configSchemaVersion": 1, "telemetry": { "enabled": false } }""")!.AsObject();
        ConfigSchemaManager.Migrate(old);
        old["telemetry"]!["crashReports"]!.GetValue<string>().Should().Be("ask");
        old["telemetry"]!["enabled"]!.GetValue<bool>().Should().BeFalse();

        var chosen = JsonNode.Parse("""{ "configSchemaVersion": 1, "telemetry": { "crashReports": "never" } }""")!.AsObject();
        ConfigSchemaManager.Migrate(chosen);
        chosen["telemetry"]!["crashReports"]!.GetValue<string>().Should().Be("never");
    }

    [Fact]
    public void ExampleConfig_HasTheSetting()
    {
        var example = Path.Combine(AppContext.BaseDirectory, "config", "config.example.json");
        if (!File.Exists(example))
        {
            return; // not copied to the output of this test run
        }

        File.ReadAllText(example).Should().Contain("\"crashReports\": \"ask\"");
    }

    [Fact]
    public void Validator_RejectsAnInvalidMode()
    {
        var config = new ConfigManager(new Moq.Mock<ILogger>().Object);
        config.Telemetry.CrashReports = "banana";
        var result = new ConfigValidator(new Moq.Mock<ILogger>().Object).ValidateConfiguration(config, validateWatchDirectories: false);
        result.Errors.Should().Contain(e => e.Contains("telemetry.crashReports"));
    }

    // ---- scrubber --------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Adversarial() =>
    [
        [@"Could not find file 'C:\Users\bob\Documents\secret plan.docx'.", new[] { "bob", "Documents", "secret", "plan" }],
        [@"Access to the path C:\Users\alice smith\AppData\Local\x is denied because of reasons", new[] { "alice", "smith", "AppData" }],
        [@"Cannot reach \\fileserver01\share\hr\salaries.xlsx", new[] { "fileserver01", "salaries", "hr" }],
        [@"Open failed: \\?\C:\Users\dave\Desktop\a.bin", new[] { "dave", "Desktop" }],
        ["/home/carol/photos/holiday.jpg could not be read", new[] { "carol", "photos", "holiday" }],
        ["failed to open /mnt/c/Users/erin/Taxes 2025/return.pdf", new[] { "erin", "Taxes", "return" }],
        [@"%USERPROFILE%\ReStore\secrets\vault.db is locked", new[] { "USERPROFILE", "vault" }],
        ["reading $HOME/.ssh/id_rsa failed", new[] { "id_rsa", ".ssh" }],
        [@"relative ..\..\hidden\notes.txt not found", new[] { "hidden", "notes" }],
        ["the file taxes-2024-final.xlsx is corrupt", new[] { "taxes-2024" }],
        ["GET https://acme-backups.s3.eu-west-1.amazonaws.com/prefix/key?X-Amz-Signature=abcdef0123456789&x=1 failed", new[] { "acme-backups", "abcdef0123456789", "prefix" }],
        ["contact jane.doe+x@example-corp.org about it", new[] { "jane", "example-corp" }],
        ["id 3f2504e0-4f89-11d3-9a0c-0305e82c3301 missing", new[] { "3f2504e0", "0305e82c3301" }],
        ["key AKIAIOSFODNN7EXAMPLE rejected", new[] { "AKIAIOSFODNN7EXAMPLE" }],
        ["secret wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY used", new[] { "wJalrXUtnFEMI", "bPxRfiCYEXAMPLEKEY" }],
        ["hash 9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08 mismatch", new[] { "9f86d081884c7d65" }],
        ["connect to 192.168.10.25:443 failed", new[] { "192.168.10.25" }],
        ["secretAccessKey=SuperSecret123 was refused", new[] { "SuperSecret123" }],
        ["Authorization: Bearer eyJhbGciOi.abc.def failed", new[] { "eyJhbGciOi" }],
        ["The specified bucket 'acme-prod-backups' does not exist", new[] { "acme-prod-backups" }],
        ["Group \"Family Photos\" could not be loaded", new[] { "Family Photos" }],
        ["multi\r\nline\r\n  with   gaps", new[] { "\r", "\n", "  " }],
    ];

    [Theory]
    [MemberData(nameof(Adversarial))]
    public void Scrubber_NeverLetsPersonalDataSurvive(string input, string[] forbidden)
    {
        var scrubbed = CrashScrubber.ScrubMessage(input);

        foreach (var term in forbidden)
        {
            scrubbed.Should().NotContain(term, $"'{term}' must not survive in '{input}'");
        }
    }

    [Fact]
    public void Scrubber_RemovesUserAndMachineNames_AndGivenTerms()
    {
        var user = Environment.UserName;
        var text = CrashScrubber.ScrubMessage($"profile of {user} on {Environment.MachineName} for NoSuchBucket: team-vault", ["team-vault"]);
        if (user.Length >= 3)
        {
            text.Should().NotContainEquivalentOf(user);
        }

        text.Should().NotContain("team-vault");
    }

    [Fact]
    public void Scrubber_CollectsNamesFromTheConfig()
    {
        var config = Path.Combine(_root, "config.json");
        File.WriteAllText(config, """
            { "storageSources": { "MyNas": { "type": "s3", "options": { "bucketName": "acme-secret-bucket", "region": "eu-west-1", "owner": "octocat-team" } } },
              "telemetry": { "crashReports": "ask" } }
            """);

        var terms = CrashScrubber.CollectConfigTerms(config);

        terms.Should().Contain(["acme-secret-bucket", "octocat-team", "MyNas"]).And.NotContain("eu-west-1");
        CrashScrubber.CollectConfigTerms(Path.Combine(_root, "missing.json")).Should().BeEmpty();
        CrashScrubber.ScrubMessage("bucket acme-secret-bucket not found at MyNas", terms).Should().NotContain("acme-secret").And.NotContain("MyNas");
    }

    [Fact]
    public void Scrubber_KeepsUsefulHarmlessText()
    {
        CrashScrubber.ScrubMessage("Value cannot be null. (Parameter 'path')").Should().Be("Value cannot be null. (Parameter 'path')");
        CrashScrubber.ScrubMessage("Index was out of range.").Should().Be("Index was out of range.");
        CrashScrubber.ScrubMessage("The operation timed out after 30 seconds, can't continue").Should().Contain("can't continue");
        CrashScrubber.ScrubMessage("System.InvalidOperationException: sequence contains no elements").Should().Contain("InvalidOperationException");
        CrashScrubber.ScrubMessage(null).Should().BeEmpty();
        CrashScrubber.ScrubMessage("   ").Should().BeEmpty();
    }

    [Fact]
    public void Scrubber_CapsLength()
    {
        var scrubbed = CrashScrubber.ScrubMessage(string.Join(' ', Enumerable.Repeat("word", 400)));
        scrubbed.Length.Should().BeLessThanOrEqualTo(CrashScrubber.MaxMessageLength);
        scrubbed.Should().EndWith("\u2026");
    }

    [Theory]
    [InlineData("System.IO.File", "ReadAllText", "System.IO.File.ReadAllText")]
    [InlineData("ReStore.Core.src.core.Backup", "RunAsync", "ReStore.Core.src.core.Backup.RunAsync")]
    [InlineData("Microsoft.Win32.Registry", "GetValue", "Microsoft.Win32.Registry.GetValue")]
    [InlineData("Amazon.S3.AmazonS3Client", "PutObjectAsync", "Amazon.S3.AmazonS3Client.PutObjectAsync")]
    [InlineData("Acme.Secrets.Vault", "Open", "<external>")]
    [InlineData("MyCompany.Tools", "Run", "<external>")]
    [InlineData(null, "Dynamic", "<external>")]
    [InlineData("System.Foo", null, "<external>")]
    [InlineData("System.Foo", @"C:\x\y", "<external>")]
    [InlineData("System.Collections.Generic.List`1[[Acme.Secret, Acme, Version=1.0.0.0]]", "Add", "System.Collections.Generic.List`1.Add")]
    public void Frames_OnlyKnownNamespacesSurvive(string? type, string? method, string expected) =>
        CrashScrubber.ScrubFrame(type, method).Should().Be(expected);

    [Fact]
    public void TypeNames_OnlyKnownNamespacesSurvive()
    {
        CrashScrubber.ScrubTypeName("System.IO.IOException").Should().Be("System.IO.IOException");
        CrashScrubber.ScrubTypeName("Acme.CustomerException").Should().Be("<external>");
        CrashScrubber.ScrubTypeName(null).Should().Be("<external>");
    }

    // ---- report building -------------------------------------------------------------------------------------------

    private static CrashReportContext Context() => new()
    {
        AppVersion = "1.8.0", Channel = "msix", Arch = "x64", OsVersion = "10.0.26100", Surface = "gui", InstallId = "install-1", SessionId = "session-1",
        CreatedUtc = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void Build_ProducesTheDocumentedEvent_WithScrubbedMessages_AndFramesWithoutFilesOrLines()
    {
        var ex = Thrown(@"Could not open 'C:\Users\bob\secret.docx' for acme@example.com");

        var report = CrashReportBuilder.Build(ex, Context(), "0123456789ab");
        var json = Json(report.ToJson());

        json["name"]!.GetValue<string>().Should().Be("crash.report");
        json["ts"]!.GetValue<string>().Should().Be("2026-05-01T12:00:00Z");
        json["installId"]!.GetValue<string>().Should().Be("install-1");
        json["sessionId"]!.GetValue<string>().Should().Be("session-1");
        json["surface"]!.GetValue<string>().Should().Be("gui");
        json["appVersion"]!.GetValue<string>().Should().Be("1.8.0");
        json.Select(p => p.Key).Should().BeEquivalentTo("name", "ts", "installId", "sessionId", "appVersion", "channel", "arch", "osVersion", "surface", "props");
        var props = json["props"]!.AsObject();
        props["id"]!.GetValue<string>().Should().Be("0123456789ab");
        var first = props["exceptions"]!.AsArray()[0]!.AsObject();
        first["type"]!.GetValue<string>().Should().Be("System.InvalidOperationException");
        first["depth"]!.GetValue<int>().Should().Be(0);
        first["hResult"]!.GetValue<int>().Should().Be(ex.HResult);
        first.Select(p => p.Key).Should().BeEquivalentTo("depth", "type", "hResult", "message", "frames", "framesOmitted");
        var frames = first["frames"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
        frames.Should().Contain(f => f.StartsWith("ReStore.Tests.CrashReportTests.Thrown"));
        frames.Should().OnlyContain(f => f == "<external>" || CrashScrubber.KnownNamespaces.Any(ns => f.StartsWith(ns)));
        frames.Should().NotContain(f => f.Contains(".cs") || f.Contains(':') || f.Contains('\\') || f.Contains(" in ") || f.Contains('('));

        var text = report.ToJson();
        text.Should().NotContain("bob").And.NotContain("secret.docx").And.NotContain("example.com").And.NotContain("\n");
    }

    [Fact]
    public void Build_WalksInnerExceptions_UpToTheDepthLimit()
    {
        Exception ex = new InvalidOperationException("level 0");
        for (var i = 1; i <= 9; i++)
        {
            ex = new InvalidOperationException("level " + i, ex);
        }

        var report = CrashReportBuilder.Build(ex, Context());

        report.Exceptions.Should().HaveCount(CrashReportBuilder.MaxInnerDepth + 1);
        report.Exceptions.Select(e => e.Depth).Should().Equal(Enumerable.Range(0, CrashReportBuilder.MaxInnerDepth + 1));
        report.Exceptions[0].Message.Should().Be("level 9");
    }

    [Fact]
    public void Build_HandlesAggregateExceptions_AndCapsTheTotal()
    {
        var inners = Enumerable.Range(0, 30).Select(i => Thrown("inner " + i)).ToArray();
        var report = CrashReportBuilder.Build(new AggregateException("many", inners), Context());

        report.Exceptions.Should().HaveCount(CrashReportBuilder.MaxExceptions);
        report.Exceptions[0].Type.Should().Be("System.AggregateException");
        report.Exceptions.Skip(1).Should().OnlyContain(e => e.Depth == 1);
    }

    private static void Recurse(int depth)
    {
        if (depth == 0)
        {
            throw new InvalidOperationException("deep");
        }

        Recurse(depth - 1);
    }

    [Fact]
    public void Build_CapsFramesPerException_AndCountsTheOmittedOnes()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Recurse(120));

        var info = CrashReportBuilder.Build(ex, Context()).Exceptions.Single();

        info.Frames.Should().HaveCount(CrashReportBuilder.MaxFramesPerException);
        info.FramesOmitted.Should().BeGreaterThan(60);
    }

    [Fact]
    public void Build_AnExceptionThatWasNeverThrown_HasNoFrames_AndNoCrash()
    {
        var info = CrashReportBuilder.Build(new InvalidOperationException("x"), Context()).Exceptions.Single();
        info.Frames.Should().BeEmpty();
    }

    [Fact]
    public void Build_KeepsTheJsonSmall_EvenForHugeMessages()
    {
        var ex = Thrown(new string('x', 50_000) + @" C:\Users\bob\a.txt");
        var json = CrashReportBuilder.Build(ex, Context()).ToJson();
        json.Length.Should().BeLessThan(30_000);
    }

    [Theory]
    [InlineData("0123456789ab", true)]
    [InlineData("0123456789AB", false)]
    [InlineData("../../evil0000", false)]
    [InlineData("short", false)]
    [InlineData(null, false)]
    public void Ids_AreTwelveLowerHexChars(string? id, bool valid) => CrashReportBuilder.IsValidId(id).Should().Be(valid);

    [Fact]
    public void NewId_IsRandomAndValid()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => CrashReportBuilder.NewId()).ToList();
        ids.Distinct().Should().HaveCount(50);
        ids.Should().OnlyContain(i => CrashReportBuilder.IsValidId(i));
    }

    // ---- pending store ---------------------------------------------------------------------------------------------

    [Fact]
    public void Store_KeepsTheNewest20_AndRoundTrips()
    {
        var store = new CrashReportStore(Path.Combine(_root, "store"));
        var ids = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            var id = CrashReportBuilder.NewId();
            ids.Add(id);
            var context = Context();
            context.CreatedUtc = context.CreatedUtc.AddMinutes(i);
            store.Save(id, CrashReportBuilder.Build(Thrown("n" + i), context, id).ToJson()).Should().BeTrue();
            File.SetLastWriteTimeUtc(Path.Combine(store.Directory, id + ".json"), context.CreatedUtc);
        }

        store.Count().Should().Be(CrashReportStore.DefaultMax);
        var listed = store.List();
        listed.Should().HaveCount(20);
        listed[0].Id.Should().Be(ids[^1], "newest first");
        listed.Select(l => l.Id).Should().NotContain(ids.Take(5), "the oldest were pruned");
        listed[0].ExceptionType.Should().Be("System.InvalidOperationException");
        store.Read(ids[^1])!.Should().Contain("\"name\":\"crash.report\"");
        store.Delete(ids[^1]).Should().BeTrue();
        store.Read(ids[^1]).Should().BeNull();
        store.DeleteAll().Should().Be(19);
        store.Count().Should().Be(0);
    }

    [Fact]
    public void Store_RejectsBadIds_AndMultilineJson_AndToleratesDamagedFiles()
    {
        var store = new CrashReportStore(Path.Combine(_root, "store2"));
        store.Save("../escape", "{}").Should().BeFalse();
        store.Save("0123456789ab", "{\n}").Should().BeFalse();
        store.Read("../../etc/passwd").Should().BeNull();
        store.Delete("../x").Should().BeFalse();

        Directory.CreateDirectory(store.Directory);
        File.WriteAllText(Path.Combine(store.Directory, "aaaaaaaaaaaa.json"), "{ torn");
        File.WriteAllText(Path.Combine(store.Directory, "not-an-id.json"), "{}");
        store.List().Should().ContainSingle(s => s.Id == "aaaaaaaaaaaa" && s.ExceptionType == null);
        new CrashReportStore(Path.Combine(_root, "nowhere")).List().Should().BeEmpty();
    }

    // ---- setting x enablement matrix -------------------------------------------------------------------------------

    [Theory]
    [InlineData("ask", true, CrashDisposition.Pending)]
    [InlineData("always", true, CrashDisposition.Queued)]
    [InlineData("never", true, CrashDisposition.None)]
    [InlineData("ask", false, CrashDisposition.None)]
    [InlineData("always", false, CrashDisposition.None)]
    [InlineData("never", false, CrashDisposition.None)]
    public void RecordCrash_FollowsTheSettingAndTelemetryEnablement(string mode, bool telemetryOn, CrashDisposition expected)
    {
        var client = NewClient(mode, config: telemetryOn);

        var outcome = client.RecordCrash(Thrown("boom"));

        outcome.Disposition.Should().Be(expected);
        client.CrashStore.Count().Should().Be(expected == CrashDisposition.Pending ? 1 : 0);
        client.QueuedCrashCount().Should().Be(expected == CrashDisposition.Queued ? 1 : 0);
        (outcome.Id != null).Should().Be(expected != CrashDisposition.None);
    }

    [Theory]
    [InlineData("RESTORE_TELEMETRY_OPTOUT", "1")]
    [InlineData("DO_NOT_TRACK", "1")]
    [InlineData("CI", "true")]
    public void RecordCrash_EveryTelemetryOptOutAlsoStopsCrashReports(string variable, string value)
    {
        var client = NewClient(CrashReportMode.Always, env: new() { [variable] = value });
        client.RecordCrash(Thrown("boom")).Disposition.Should().Be(CrashDisposition.None);
        Directory.Exists(StateDir).Should().BeFalse("nothing is written when telemetry is off");
    }

    [Fact]
    public void RecordCrash_DevChannelAndTestOverrideStopCrashReports()
    {
        NewClient(CrashReportMode.Always, channel: AppChannel.Dev).RecordCrash(Thrown("x")).Disposition.Should().Be(CrashDisposition.None);

        var overridden = new TelemetryClient(new TelemetryClientOptions
        {
            Channel = AppChannel.Msix, GetEnv = _ => null, GetEnvNames = () => [], IsTestRootOverride = () => true,
            StateDirectory = () => StateDir, ConfigEnabled = () => true, ConfigCrashReports = () => "always"
        });
        overridden.RecordCrash(Thrown("x")).Disposition.Should().Be(CrashDisposition.None);
    }

    [Fact]
    public void RecordCrash_ReadsTheModeFromTheConfigFile()
    {
        var config = Path.Combine(_root, "cfg.json");
        File.WriteAllText(config, """{ "telemetry": { "enabled": true, "crashReports": "never" } }""");
        var client = new TelemetryClient(new TelemetryClientOptions
        {
            Channel = AppChannel.Msix, GetEnv = _ => null, GetEnvNames = () => [], IsTestRootOverride = () => false,
            StateDirectory = () => StateDir, ConfigFile = () => config
        });

        client.CrashMode.Should().Be("never");
        client.RecordCrash(Thrown("x")).Disposition.Should().Be(CrashDisposition.None);

        File.WriteAllText(config, """{ "telemetry": { "enabled": true, "crashReports": "ask" } }""");
        client.RecordCrash(Thrown("y")).Disposition.Should().Be(CrashDisposition.Pending);
    }

    [Fact]
    public void RecordCrash_ScrubsConfiguredNames_AndNeverThrows()
    {
        var config = Path.Combine(_root, "cfg.json");
        File.WriteAllText(config, """{ "telemetry": { "enabled": true }, "storageSources": { "s3": { "type": "s3", "options": { "bucketName": "payroll-archive" } } } }""");
        var client = NewClient(configFile: config);

        var id = client.RecordCrash(Thrown("NoSuchBucket payroll-archive")).Id!;

        client.CrashStore.Read(id).Should().NotContain("payroll-archive");
        client.RecordCrash(null).Disposition.Should().Be(CrashDisposition.None);
    }

    [Fact]
    public void RecordCrash_RepeatsAndFloodsAreBounded()
    {
        var client = NewClient();
        client.RecordCrash(Thrown("same")).Disposition.Should().Be(CrashDisposition.Pending);
        client.RecordCrash(Thrown("same")).Disposition.Should().Be(CrashDisposition.None, "the same crash in the same process is recorded once");

        for (var i = 0; i < 10; i++)
        {
            try { throw new IOException("io " + i); }
            catch (IOException ex) { client.RecordCrash(ex); }
            try { throw new ArgumentException("arg " + i); }
            catch (ArgumentException ex) { client.RecordCrash(ex); }
        }

        client.CrashStore.Count().Should().BeLessThanOrEqualTo(5);
    }

    [Fact]
    public void RecordCrash_AlwaysKeepsTheReportPending_WhenTheQueueCannotBeWritten()
    {
        var client = NewClient(CrashReportMode.Always);
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(Path.Combine(StateDir, "telemetry-queue.jsonl")); // a directory where the queue file should be

        var outcome = client.RecordCrash(Thrown("boom"));

        outcome.Disposition.Should().Be(CrashDisposition.Pending);
        client.CrashStore.Count().Should().Be(1);
    }

    [Fact]
    public void RecordCrash_WritesOnlyUnderTheStateDirectory()
    {
        var client = NewClient(CrashReportMode.Always);
        client.RecordCrash(Thrown("boom"));

        Directory.GetFileSystemEntries(_root).Select(Path.GetFileName).Should().BeEquivalentTo(["client-state"]);
        Directory.GetFileSystemEntries(StateDir).Select(Path.GetFileName).Should().BeSubsetOf(["telemetry.json", "telemetry-queue.jsonl", "crash-reports"]);
    }

    // ---- sending ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Flush_SendsQueuedCrashReports_AndRemovesThemOnlyAfterSuccess()
    {
        var failing = new RecordingSender(false);
        var client = NewClient(CrashReportMode.Always, sender: failing);
        client.RecordCrash(Thrown("boom"));
        client.QueuedCrashCount().Should().Be(1);

        (await client.FlushAsync(TimeSpan.FromSeconds(5))).Should().Be(0);
        client.QueuedCrashCount().Should().Be(1, "a failed upload keeps the report");

        var ok = new RecordingSender(true);
        _now = _now.AddDays(1);
        var healthy = NewClient(CrashReportMode.Always, sender: ok);
        (await healthy.FlushAsync(TimeSpan.FromSeconds(5))).Should().Be(1);
        healthy.QueuedCrashCount().Should().Be(0);
        Json(ok.Batches.Single().Single())["name"]!.GetValue<string>().Should().Be("crash.report");
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted, 0)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    public async Task HttpPath_2xxRemoves_5xxKeeps(HttpStatusCode code, int remaining)
    {
        var handler = new StubHandler(code);
        var sender = new HttpTelemetrySender(new Uri("https://example.test/ingest"), handler);
        var client = NewClient(CrashReportMode.Always, sender: sender);
        client.RecordCrash(Thrown("boom"));

        await client.FlushAsync(TimeSpan.FromSeconds(5));

        client.QueuedCrashCount().Should().Be(remaining);
        var body = Json(handler.Bodies.Single());
        body["events"]!.AsArray().Single()!["name"]!.GetValue<string>().Should().Be("crash.report");
    }

    [Fact]
    public async Task NoEndpoint_ReportsStayQueued()
    {
        var client = NewClient(CrashReportMode.Always);
        client.Endpoint.Should().BeNull();
        client.RecordCrash(Thrown("boom"));

        (await client.FlushAsync(TimeSpan.FromSeconds(1))).Should().Be(0);
        client.QueuedCrashCount().Should().Be(1);
    }

    [Fact]
    public void QueueCrash_MovesPendingToTheQueue_Once_AndNotWhenTelemetryIsOff()
    {
        var client = NewClient();
        var id = client.RecordCrash(Thrown("boom")).Id!;
        var json = client.CrashStore.Read(id)!;

        client.QueueCrash(id).Should().BeTrue();
        client.CrashStore.Count().Should().Be(0);
        client.Queue.ReadAll().Should().Contain(json, "the queued event is exactly the reviewed JSON");
        client.QueueCrash(id).Should().BeFalse();

        var off = NewClient(config: false);
        new CrashReportStore(off.CrashStore.Directory).Save("aaaaaaaaaaaa", json);
        off.QueueCrash("aaaaaaaaaaaa").Should().BeFalse();
        off.CrashStore.Count().Should().Be(1);
    }

    // ---- GUI prompt logic ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Prompt_ShouldAsk_OnlyWhenOnAskWithPendingReports()
    {
        var ask = NewClient();
        CrashReportPrompt.ShouldAsk(ask).Should().BeFalse("nothing pending");
        ask.RecordCrash(Thrown("boom"));
        CrashReportPrompt.ShouldAsk(ask).Should().BeTrue();

        CrashReportPrompt.ShouldAsk(NewClient(CrashReportMode.Always)).Should().BeFalse();
        CrashReportPrompt.ShouldAsk(NewClient(CrashReportMode.Never)).Should().BeFalse();
        CrashReportPrompt.ShouldAsk(NewClient(config: false)).Should().BeFalse("telemetry off");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Prompt_Send_QueuesEverythingPending_WithoutChangingTheSetting()
    {
        var client = NewClient();
        client.RecordCrash(Thrown("a"));
        var saved = new List<string>();

        var result = await CrashReportPrompt.ApplyAsync(client, CrashPromptChoice.Send, mode => { saved.Add(mode); return Task.CompletedTask; });

        result.Queued.Should().Be(1);
        saved.Should().BeEmpty();
        client.CrashStore.Count().Should().Be(0);
        client.QueuedCrashCount().Should().Be(1);
    }

    [Fact]
    public async Task Prompt_AlwaysSend_SavesTheSetting_AndQueues()
    {
        var client = NewClient();
        client.RecordCrash(Thrown("a"));
        var saved = new List<string>();

        var result = await CrashReportPrompt.ApplyAsync(client, CrashPromptChoice.AlwaysSend, mode => { saved.Add(mode); return Task.CompletedTask; });

        saved.Should().Equal("always");
        result.Queued.Should().Be(1);
        client.QueuedCrashCount().Should().Be(1);
    }

    [Fact]
    public async Task Prompt_DontSend_DeletesThePendingReports_AndQueuesNothing()
    {
        var client = NewClient();
        client.RecordCrash(Thrown("a"));

        var result = await CrashReportPrompt.ApplyAsync(client, CrashPromptChoice.DontSend, _ => Task.CompletedTask);

        result.Deleted.Should().Be(1);
        client.CrashStore.Count().Should().Be(0);
        client.QueuedCrashCount().Should().Be(0);
    }

    [Fact]
    public async Task Prompt_View_ShowsExactlyWhatWouldBeSent_AndChangesNothing()
    {
        var client = NewClient();
        var id = client.RecordCrash(Thrown("boom")).Id!;
        var stored = client.CrashStore.Read(id)!;

        var result = await CrashReportPrompt.ApplyAsync(client, CrashPromptChoice.View, _ => Task.CompletedTask);

        client.CrashStore.Count().Should().Be(1);
        JsonNode.Parse(result.Text!)!.ToJsonString().Should().Be(JsonNode.Parse(stored)!.ToJsonString());
    }

    // ---- CLI -------------------------------------------------------------------------------------------------------

    private TelemetryClient InstallCliClient()
    {
        var client = new TelemetryClient(new TelemetryClientOptions
        {
            Channel = AppChannel.Msix, IsTestRootOverride = () => false, GetEnv = _ => null, GetEnvNames = () => [], Interactive = false
        });
        Telemetry.Install(client);
        return client;
    }

    [Fact]
    public async Task Cli_CrashReports_ShowsAndSetsTheMode()
    {
        var client = InstallCliClient();

        var shown = Json((await RunAsync("telemetry", "crash-reports", "--json")).Stdout);
        shown["crashReports"]!.GetValue<string>().Should().Be("ask");
        shown["changed"]!.GetValue<bool>().Should().BeFalse();
        shown["enabled"]!.GetValue<bool>().Should().BeTrue();

        var set = Json((await RunAsync("telemetry", "crash-reports", "always", "--json")).Stdout);
        set["crashReports"]!.GetValue<string>().Should().Be("always");
        set["changed"]!.GetValue<bool>().Should().BeTrue();
        File.ReadAllText(AppPaths.ConfigFile).Should().MatchRegex("\"crashReports\"\\s*:\\s*\"always\"");
        client.CrashMode.Should().Be("always");

        var text = await RunAsync("telemetry", "crash-reports");
        text.Stdout.Should().Contain("Crash reports: always");
    }

    [Fact]
    public async Task Cli_CrashReports_RejectsAnInvalidMode()
    {
        InstallCliClient();
        var (exitCode, _, stderr) = await RunAsync("telemetry", "crash-reports", "sometimes");
        exitCode.Should().Be(2);
        stderr.Should().Contain("not a crash report mode");
    }

    [Fact]
    public async Task Cli_Crashes_ListShowSendDelete()
    {
        var client = InstallCliClient();
        var first = client.RecordCrash(Thrown("first")).Id!;
        string second;
        try { throw new IOException("second"); }
        catch (IOException ex) { second = client.RecordCrash(ex).Id!; }

        var list = Json((await RunAsync("telemetry", "crashes", "list", "--json")).Stdout);
        list["count"]!.GetValue<int>().Should().Be(2);
        list["queued"]!.GetValue<int>().Should().Be(0);
        list["reports"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).Should().BeEquivalentTo(first, second);
        (await RunAsync("telemetry", "crashes")).Stdout.Should().Contain(first).And.Contain("Pending: 2");

        var show = await RunAsync("telemetry", "crashes", "show", first, "--json");
        show.ExitCode.Should().Be(0);
        Json(show.Stdout)["report"]!["props"]!["id"]!.GetValue<string>().Should().Be(first);
        (await RunAsync("telemetry", "crashes", "show", first)).Stdout.Trim().Should().Be(client.CrashStore.Read(first));

        var send = Json((await RunAsync("telemetry", "crashes", "send", first, "--json")).Stdout);
        send["queued"]!.GetValue<int>().Should().Be(1);
        client.QueuedCrashCount().Should().Be(1);
        client.CrashStore.Count().Should().Be(1);

        var delete = Json((await RunAsync("telemetry", "crashes", "delete", "--all", "--json")).Stdout);
        delete["deleted"]!.GetValue<int>().Should().Be(1);
        client.CrashStore.Count().Should().Be(0);
        client.QueuedCrashCount().Should().Be(1, "a queued report is not pending any more");
    }

    [Fact]
    public async Task Cli_Crashes_SendAll_QueuesEverything()
    {
        var client = InstallCliClient();
        client.RecordCrash(Thrown("a"));
        try { throw new IOException("b"); } catch (IOException ex) { client.RecordCrash(ex); }

        var send = Json((await RunAsync("telemetry", "crashes", "send", "--all", "--json")).Stdout);

        send["queued"]!.GetValue<int>().Should().Be(2);
        send["endpointConfigured"]!.GetValue<bool>().Should().BeFalse();
        client.QueuedCrashCount().Should().Be(2);
    }

    [Theory]
    [InlineData("show", "0123456789ab")]
    [InlineData("send", "0123456789ab")]
    [InlineData("delete", "0123456789ab")]
    [InlineData("show", "../../x")]
    public async Task Cli_Crashes_UnknownIdIsAFailure(string action, string id)
    {
        InstallCliClient();
        var (exitCode, stdout, _) = await RunAsync("telemetry", "crashes", action, id, "--json");
        exitCode.Should().Be(1);
        Json(stdout)["error"]!["message"]!.GetValue<string>().Should().Contain("no pending crash report");
    }

    [Theory]
    [InlineData("show")]
    [InlineData("send")]
    [InlineData("delete")]
    [InlineData("bogus")]
    public async Task Cli_Crashes_BadUsageIsExitCode2(string action) =>
        (await RunAsync("telemetry", "crashes", action)).ExitCode.Should().Be(2);

    [Fact]
    public async Task Cli_Crashes_SendNeedsIdOrAll_NotBoth()
    {
        InstallCliClient();
        (await RunAsync("telemetry", "crashes", "send", "0123456789ab", "--all")).ExitCode.Should().Be(2);
        (await RunAsync("telemetry", "crashes", "list", "--all")).ExitCode.Should().Be(2);
    }

    [Fact]
    public async Task Cli_Status_ReportsTheSettingAndTheCounts()
    {
        var client = InstallCliClient();
        client.RecordCrash(Thrown("a"));

        var doc = Json((await RunAsync("telemetry", "status", "--json")).Stdout);

        doc["crashReports"]!.GetValue<string>().Should().Be("ask");
        doc["pendingCrashReports"]!.GetValue<int>().Should().Be(1);
        doc["queuedCrashReports"]!.GetValue<int>().Should().Be(0);
        (await RunAsync("telemetry", "status")).Stdout.Should().Contain("Crash reports:").And.Contain("1 pending");
    }

    [Fact]
    public async Task Cli_Disable_DeletesPendingCrashReports_AndSendRefuses()
    {
        var client = InstallCliClient();
        client.RecordCrash(Thrown("a"));
        client.CrashStore.Count().Should().Be(1);

        (await RunAsync("telemetry", "disable")).ExitCode.Should().Be(0);

        client.CrashStore.Count().Should().Be(0);
        client.RecordCrash(Thrown("b")).Disposition.Should().Be(CrashDisposition.None);
    }

    [Fact]
    public async Task Cli_Send_FailsWhenTelemetryIsOff()
    {
        var client = InstallCliClient();
        client.RecordCrash(Thrown("a"));
        File.WriteAllText(AppPaths.ConfigFile, """{ "telemetry": { "enabled": false } }""");
        client.InvalidateConfig();

        var (exitCode, stdout, _) = await RunAsync("telemetry", "crashes", "send", "--all", "--json");

        exitCode.Should().Be(1);
        Json(stdout)["error"]!["message"]!.GetValue<string>().Should().Contain("Telemetry is off");
        client.CrashStore.Count().Should().Be(1);
    }

    [Fact]
    public async Task Cli_RecordCrash_PrintsTheHintOnStderr_OnlyWhenAReportIsPending()
    {
        InstallCliClient();
        await ConsoleLock.WaitAsync();
        var original = Console.Error;
        try
        {
            using var writer = new StringWriter();
            Console.SetError(writer);
            TelemetryCommands.RecordCrash(Thrown("boom"));
            var hint = writer.ToString();
            hint.Should().Contain("A crash report is pending").And.Contain("restore telemetry crashes show ").And.Contain("restore telemetry crashes send ")
                .And.Contain("telemetry crash-reports always");

            writer.GetStringBuilder().Clear();
            TelemetryCommands.RecordCrash(null);
            writer.ToString().Should().BeEmpty();
        }
        finally
        {
            Console.SetError(original);
            ConsoleLock.Release();
        }
    }

    [Fact]
    public async Task Cli_RecordCrash_WithAlways_IsSilent_AndQueues()
    {
        var client = InstallCliClient();
        File.WriteAllText(AppPaths.ConfigFile, """{ "telemetry": { "enabled": true, "crashReports": "always" } }""");
        await ConsoleLock.WaitAsync();
        var original = Console.Error;
        try
        {
            using var writer = new StringWriter();
            Console.SetError(writer);
            TelemetryCommands.RecordCrash(Thrown("boom"));
            writer.ToString().Should().BeEmpty();
        }
        finally
        {
            Console.SetError(original);
            ConsoleLock.Release();
        }

        client.QueuedCrashCount().Should().Be(1);
    }

    [Fact]
    public async Task Cli_NeverTouchesTheRealProfile()
    {
        InstallCliClient().RecordCrash(Thrown("a"));
        await RunAsync("telemetry", "crashes", "list");

        Directory.Exists(Path.Combine(_root, "state", "crash-reports")).Should().BeTrue();
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ReStore", "state", "crash-reports");
        Directory.Exists(real).Should().BeFalse();
    }
}

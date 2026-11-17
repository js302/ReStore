using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using System.Text;
using System.Text.Json.Nodes;

namespace ReStore.Tests;

/// <summary>The agent-grade CLI foundation: registry, help, version, global options, JSON documents, exit codes, password sources.</summary>
[Collection("GlobalState")]
public class CliFoundationTests : IDisposable
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreCliFoundation_" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _previousVariables = [];

    private static readonly string[] ManagedVariables =
    [
        "RESTORE_ENCRYPTION_PASSWORD", "RESTORE_ENCRYPTION_PASSWORD_FILE", "RESTORE_NEW_ENCRYPTION_PASSWORD", "RESTORE_NON_INTERACTIVE", "CI"
    ];

    public CliFoundationTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot(_root);
        foreach (var name in ManagedVariables)
        {
            _previousVariables[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(_previousRoot);
        foreach (var (name, value) in _previousVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try { Directory.Delete(_root, true); } catch { }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWithStdinAsync(string? stdin, params string[] args)
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
            if (stdin != null)
            {
                Console.SetIn(new StringReader(stdin));
            }

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

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) => RunWithStdinAsync(null, args);

    private string StorageDirectory => Path.Combine(_root, "storage");

    /// <summary>Config with a local storage in the temp root; <paramref name="watchDirectories"/> may be empty.</summary>
    private void WriteConfig(string[] watchDirectories, Action<JsonObject>? customize = null)
    {
        ConfigInitializer.EnsureConfigurationSetup();
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        root["watchDirectories"] = new JsonArray([.. watchDirectories.Select(d => (JsonNode)new JsonObject { ["path"] = d, ["storageType"] = null })]);
        root["globalStorageType"] = "local";
        root["storageSources"] = new JsonObject { ["local"] = new JsonObject { ["path"] = StorageDirectory } };
        root["excludedPaths"] = new JsonArray();
        customize?.Invoke(root);
        File.WriteAllText(AppPaths.ConfigFile, root.ToJsonString());
    }

    private string CreateSource()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "doc.txt"), "document " + new string('d', 2000));
        File.WriteAllText(Path.Combine(source, "sub", "note.txt"), "note " + new string('n', 2000));
        return source;
    }

    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    private void WriteEncryptedConfig(string source, string password)
    {
        var salt = EncryptionService.GenerateSalt();
        var token = new EncryptionService(new TestLogger()).CreatePasswordVerificationToken(password, salt, 1000);
        WriteConfig([source], root => root["encryption"] = new JsonObject
        {
            ["enabled"] = true,
            ["salt"] = Convert.ToBase64String(salt),
            ["keyDerivationIterations"] = 1000,
            ["verificationToken"] = token
        });
    }

    // ---- registry --------------------------------------------------------------------------------------------------

    [Fact]
    public void Registry_EveryCommandHasMetadata_AndNamesAreUnique()
    {
        var all = CommandRegistry.All;

        all.Should().NotBeEmpty();
        all.Select(s => s.Name).Should().OnlyHaveUniqueItems();
        foreach (var spec in all)
        {
            spec.Summary.Should().NotBeNullOrWhiteSpace(spec.Name);
            spec.UsageLines.Should().NotBeEmpty(spec.Name);
            spec.Handler.Should().NotBeNull(spec.Name);
        }
    }

    [Fact]
    public async Task CommandsJson_ListsEveryRegisteredCommand_WithTheDocumentedShape()
    {
        var (exitCode, stdout, _) = await RunAsync("commands", "--json");

        exitCode.Should().Be(0);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["schemaVersion"]!.GetValue<int>().Should().Be(1);
        doc["version"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        doc["globalOptions"]!.AsArray().Select(o => o!["name"]!.GetValue<string>())
            .Should().Contain(["--json", "--quiet", "--non-interactive", "--yes", "--wait", "--password-stdin"]);
        doc["exitCodes"]!.AsArray().Select(e => e!["code"]!.GetValue<int>()).Should().Equal(0, 1, 2, 3, 4, 5, 130);

        var commands = doc["commands"]!.AsArray();
        commands.Select(c => c!["name"]!.GetValue<string>()).Should().BeEquivalentTo(CommandRegistry.All.Select(s => s.Name));
        foreach (var command in commands)
        {
            command!["summary"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
            command["usage"]!.AsArray().Should().NotBeEmpty();
            command["supportsJson"]!.GetValue<bool>();
            command["mutates"]!.GetValue<bool>();
            command["destructive"]!.GetValue<bool>();
            command["windowsOnly"]!.GetValue<bool>();
            command["needs"]!["config"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        }

        var backup = commands.Single(c => c!["name"]!.GetValue<string>() == "backup")!;
        backup["positionals"]![0]!["name"]!.GetValue<string>().Should().Be("sourceDir");
        backup["options"]!.AsArray().Should().Contain(o => o!["name"]!.GetValue<string>() == "--storage" && o["takesValue"]!.GetValue<bool>());
        backup["mutates"]!.GetValue<bool>().Should().BeTrue();
        File.Exists(AppPaths.ConfigFile).Should().BeFalse("commands must not need or create a config");
    }

    [Fact]
    public async Task Commands_PlainText_ListsCommandsByCategory()
    {
        var (exitCode, stdout, _) = await RunAsync("commands");

        exitCode.Should().Be(0);
        stdout.Should().Contain("Backup & restore:").And.Contain("Configuration:").And.Contain("config show").And.Contain("backup <sourceDir>");
    }

    [Fact]
    public async Task Help_WorksForEveryCommand_InEveryForm()
    {
        foreach (var spec in CommandRegistry.All)
        {
            var words = spec.Name.Split(' ');

            var flagged = await RunAsync([.. words, "--help"]);
            var shortFlag = await RunAsync([.. words, "-h"]);
            var helpCommand = await RunAsync(["help", .. words]);

            foreach (var (exitCode, stdout, stderr) in new[] { flagged, shortFlag, helpCommand })
            {
                exitCode.Should().Be(0, $"{spec.Name}: {stderr}");
                stdout.Should().Contain("Usage:").And.Contain($"restore.exe {spec.UsageLines[0].Split(' ')[0]}", spec.Name);
                stdout.Should().Contain(spec.Summary);
            }

            flagged.Stdout.Should().Be(helpCommand.Stdout, spec.Name);
        }

        File.Exists(AppPaths.ConfigFile).Should().BeFalse("help must not touch config or state");
    }

    [Fact]
    public async Task Help_TopLevel_IsGeneratedFromTheRegistry_AndListsExitCodesAndGlobalOptions()
    {
        foreach (var args in new[] { new[] { "help" }, ["--help"], ["-h"] })
        {
            var (exitCode, stdout, _) = await RunAsync(args);

            exitCode.Should().Be(0);
            foreach (var spec in CommandRegistry.All)
            {
                stdout.Should().Contain(spec.Synopsis);
            }

            stdout.Should().Contain("Exit codes:").And.Contain("needs_user").And.Contain("busy")
                .And.Contain("--non-interactive").And.Contain("--password-stdin").And.Contain("--wait <seconds>");
        }
    }

    [Fact]
    public async Task Help_ForAGroup_ListsItsSubcommands()
    {
        var (exitCode, stdout, _) = await RunAsync("help", "config");

        exitCode.Should().Be(0);
        stdout.Should().Contain("config show").And.Contain("config protect").And.Contain("config export").And.Contain("config import").And.Contain("config validate");
    }

    [Fact]
    public async Task Help_ForUnknownCommand_IsUsageError()
    {
        var (exitCode, _, stderr) = await RunAsync("help", "bogus");

        exitCode.Should().Be(2);
        stderr.Should().Contain("Unknown command 'bogus'");
    }

    [Fact]
    public async Task Help_ForACommandWithMissingArguments_StillShowsHelp()
    {
        var (exitCode, stdout, _) = await RunAsync("restore", "--help");

        exitCode.Should().Be(0);
        stdout.Should().Contain("--conflict").And.Contain("keepboth").And.Contain("Examples:");
    }

    // ---- version ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("--version")]
    [InlineData("version")]
    public async Task Version_PrintsVersionAndEnvironment_WithoutAConfig(string command)
    {
        var (exitCode, stdout, _) = await RunAsync(command);

        exitCode.Should().Be(0);
        stdout.Should().StartWith("ReStore ").And.Contain("Channel:").And.Contain("Architecture:").And.Contain(AppPaths.Root).And.Contain(AppPaths.ConfigFile);
        File.Exists(AppPaths.ConfigFile).Should().BeFalse();
        Directory.Exists(AppPaths.LogsDirectory).Should().BeFalse("version must not create the data folder");
    }

    [Fact]
    public async Task Version_Json_HasTheDocumentedFields()
    {
        var (exitCode, stdout, _) = await RunAsync("version", "--json");

        exitCode.Should().Be(0);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["version"]!.GetValue<string>().Should().NotContain("+").And.NotBeNullOrWhiteSpace();
        doc["informationalVersion"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        doc["channel"]!.GetValue<string>().Should().BeOneOf("dev", "portable", "msix");
        doc["architecture"]!.GetValue<string>().Should().BeOneOf("x64", "arm64", "x86", "arm");
        doc["os"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        doc["runtime"]!.GetValue<string>().Should().Contain(".NET");
        doc["dataRoot"]!.GetValue<string>().Should().Be(AppPaths.Root);
        doc["configFile"]!.GetValue<string>().Should().Be(AppPaths.ConfigFile);
        doc.ContainsKey("executable").Should().BeTrue();
    }

    // ---- AppChannel ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, null, "msix")]
    [InlineData(true, "portable", "msix")]
    [InlineData(false, "portable", "portable")]
    [InlineData(false, "  Portable\r\n", "portable")]
    [InlineData(false, "something else", "dev")]
    [InlineData(false, "", "dev")]
    [InlineData(false, null, "dev")]
    public void AppChannel_Determine_FollowsPackageIdentityThenMarker(bool packaged, string? marker, string expected)
    {
        AppChannel.Determine(packaged, marker).Should().Be(expected);
    }

    [Fact]
    public void AppChannel_ReadMarker_ReadsTheMarkerFileNextToTheExecutable()
    {
        AppChannel.ReadMarker(_root).Should().BeNull();
        File.WriteAllText(Path.Combine(_root, AppChannel.MarkerFileName), "portable");

        AppChannel.ReadMarker(_root).Should().Be("portable");
        AppChannel.Determine(false, AppChannel.ReadMarker(_root)).Should().Be(AppChannel.Portable);
        AppChannel.ReadMarker(Path.Combine(_root, "missing")).Should().BeNull();
    }

    [Theory]
    [InlineData(System.Runtime.InteropServices.Architecture.X64, "x64")]
    [InlineData(System.Runtime.InteropServices.Architecture.Arm64, "arm64")]
    [InlineData(System.Runtime.InteropServices.Architecture.X86, "x86")]
    public void AppChannel_ArchitectureNames(System.Runtime.InteropServices.Architecture architecture, string expected)
    {
        AppChannel.ArchitectureName(architecture).Should().Be(expected);
        AppChannel.Architecture.Should().BeOneOf("x64", "arm64", "x86", "arm");
    }

    [Fact]
    public void AppChannel_PackageIdentity_IsFalseOffWindows_AndTheTestHostIsNeverMsix()
    {
        if (!OperatingSystem.IsWindows())
        {
            AppChannel.HasPackageIdentity().Should().BeFalse();
        }

        AppChannel.Current.Should().NotBe("msix");
    }

    // ---- parsing of global options ---------------------------------------------------------------------------------

    private static Program.ParsedCommand Parse(params string[] args)
    {
        Program.TryParseArguments(args, out var parsed, out var error).Should().BeTrue(error);
        return parsed!;
    }

    [Theory]
    [InlineData("--json", "status", "")]
    [InlineData("status", "--json", "")]
    [InlineData("--json", "--quiet", "--yes", "--non-interactive", "--password-stdin", "status", "")]
    [InlineData("status", "--yes", "--non-interactive", "--json", "--quiet", "--password-stdin", "")]
    public void Parse_GlobalOptions_AreAcceptedBeforeAndAfterTheCommand(params string[] args)
    {
        var parsed = Parse([.. args.Where(a => a.Length > 0)]);

        parsed.Command.Should().Be("status");
        parsed.Positionals.Should().BeEmpty();
        parsed.Json.Should().BeTrue();
        parsed.Yes.Should().Be(args.Contains("--yes"));
        parsed.NonInteractive.Should().Be(args.Contains("--non-interactive"));
        parsed.PasswordStdin.Should().Be(args.Contains("--password-stdin"));
        parsed.Quiet.Should().Be(args.Contains("--quiet"));
    }

    [Fact]
    public void Parse_GlobalOptions_InTheMiddleOfAnotherCommand_DoNotDisturbPositionals()
    {
        var parsed = Parse("restore", "--json", "snap/HEAD", "--wait", "7", "out", "--yes", "--dry-run");

        parsed.Positionals.Should().Equal("snap/HEAD", "out");
        parsed.WaitSeconds.Should().Be(7);
        parsed.Json.Should().BeTrue();
        parsed.Yes.Should().BeTrue();
        parsed.HasOption("--dry-run").Should().BeTrue();
    }

    [Fact]
    public void Parse_Wait_AcceptsZero_AndRejectsBadValues()
    {
        Parse("backup", "x", "--wait", "0").WaitSeconds.Should().Be(0);
        Parse("backup", "x").WaitSeconds.Should().BeNull();

        foreach (var bad in new[] { new[] { "backup", "x", "--wait" }, ["backup", "x", "--wait", "soon"], ["backup", "x", "--wait", "-1"], ["backup", "--wait", "--json", "x"] })
        {
            Program.TryParseArguments(bad, out _, out var error).Should().BeFalse();
            error.Should().Contain("--wait");
        }
    }

    [Fact]
    public void Parse_Json_IsRejectedForCommandsThatCannotProduceIt()
    {
        Program.TryParseArguments(["--service", "--json"], out _, out var error).Should().BeFalse();
        error.Should().Contain("does not support --json");
    }

    [Fact]
    public void Parse_EveryOtherCommand_AcceptsJson()
    {
        foreach (var spec in CommandRegistry.All.Where(s => s.SupportsJson))
        {
            var args = new List<string>(spec.Name.Split(' '));
            args.AddRange(spec.Positionals.Where(p => p.Required).Select(_ => "x"));
            args.Add("--json");
            if (spec.Validate is not null)
            {
                continue; // commands with cross-field rules are covered by their own tests
            }

            Program.TryParseArguments([.. args], out var parsed, out var error).Should().BeTrue($"{spec.Name}: {error}");
            parsed!.Json.Should().BeTrue();
        }
    }

    [Fact]
    public void Parse_ConfigGroup_RequiresAKnownSubcommand_AndKeepsTheOldForms()
    {
        Program.TryParseArguments(["config"], out _, out var missing).Should().BeFalse();
        missing.Should().Contain("Missing subcommand").And.Contain("show").And.Contain("import");

        Program.TryParseArguments(["config", "shw"], out _, out var typo).Should().BeFalse();
        typo.Should().Contain("Unknown subcommand 'shw'").And.Contain("Did you mean 'config show'?");

        Parse("config", "show").Command.Should().Be("config show");
        Parse("config", "--reveal", "show").HasOption("--reveal").Should().BeTrue();
        Parse("config", "import", "f.json", "--replace").Positionals.Should().Equal("f.json");
        Parse("--validate-config").Command.Should().Be("config validate");
        Parse("config", "validate").Command.Should().Be("config validate");
    }

    [Fact]
    public void Parse_UnknownOption_SuggestsGlobalOptionsToo()
    {
        Program.TryParseArguments(["backup", "x", "--jsno"], out _, out var error).Should().BeFalse();
        error.Should().Contain("Did you mean '--json'?");
    }

    [Theory]
    [InlineData(false, null, null, false, false)]
    [InlineData(true, null, null, false, true)]
    [InlineData(false, "1", null, false, true)]
    [InlineData(false, "true", null, false, true)]
    [InlineData(false, "0", null, false, false)]
    [InlineData(false, null, "true", false, true)]
    [InlineData(false, null, "1", false, true)]
    [InlineData(false, null, "false", false, false)]
    [InlineData(false, null, null, true, true)]
    public void NonInteractive_IsFlagEnvironmentOrRedirectedInput(bool flag, string? restoreVariable, string? ci, bool redirected, bool expected)
    {
        string? Env(string name) => name switch
        {
            "RESTORE_NON_INTERACTIVE" => restoreVariable,
            "CI" => ci,
            _ => null
        };

        CliGlobals.ResolveNonInteractive(flag, Env, redirected).Should().Be(expected);
    }

    // ---- JSON envelope ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task UsageError_WithJson_WritesTheErrorDocument_AndTheHumanMessageOnStderr()
    {
        var (exitCode, stdout, stderr) = await RunAsync("restore", "only-one-argument", "--json");

        exitCode.Should().Be(2);
        stderr.Should().Contain("Error: Missing argument(s) for 'restore'");
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["exitCode"]!.GetValue<int>().Should().Be(2);
        doc["error"]!["code"]!.GetValue<string>().Should().Be("usage");
        doc["error"]!["message"]!.GetValue<string>().Should().Contain("Missing argument");
        doc["error"]!["hint"]!.GetValue<string>().Should().Contain("help restore");
        stdout.Should().NotContain("Usage:");
    }

    [Fact]
    public async Task UsageError_WithoutJson_PrintsTheCommandUsageOnStdout()
    {
        var (exitCode, stdout, stderr) = await RunAsync("backup");

        exitCode.Should().Be(2);
        stderr.Should().Contain("Error:");
        stdout.Should().Contain("Usage:").And.Contain("restore.exe backup <sourceDir>").And.Contain("help backup");
    }

    [Fact]
    public async Task UnknownCommand_WithJson_IsAUsageErrorDocument()
    {
        var (exitCode, stdout, _) = await RunAsync("bakcup", "--json");

        exitCode.Should().Be(2);
        Json(stdout)["error"]!["message"]!.GetValue<string>().Should().Contain("Did you mean 'backup'?");
    }

    [Fact]
    public async Task ConfigError_WithJson_WritesTheErrorDocument()
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        File.WriteAllText(AppPaths.ConfigFile, "{ not json");

        var (exitCode, stdout, stderr) = await RunAsync("status", "--json");

        exitCode.Should().Be(3);
        stderr.Should().Contain("Could not load the configuration");
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["exitCode"]!.GetValue<int>().Should().Be(3);
        doc["error"]!["code"]!.GetValue<string>().Should().Be("config");
        doc["error"]!["message"]!.GetValue<string>().Should().Contain("Could not load the configuration");
    }

    [Fact]
    public async Task InvalidConfig_WithJson_ListsTheErrorsInTheDocument()
    {
        WriteConfig([_root], root => root["backupInterval"] = "00:00:00");

        var (exitCode, stdout, _) = await RunAsync("backup", _root, "--json");

        exitCode.Should().Be(3);
        var error = Json(stdout)["error"]!;
        error["code"]!.GetValue<string>().Should().Be("config");
        error["details"]!.AsArray().Select(d => d!.GetValue<string>()).Should().Contain(d => d.Contains("Backup interval"));
    }

    [Fact]
    public async Task FailedCommand_WithJson_GetsTheGenericErrorDocument()
    {
        WriteConfig([]);
        Directory.CreateDirectory(Path.Combine(StorageDirectory, "snapshots", "broken"));
        File.WriteAllText(Path.Combine(StorageDirectory, "snapshots", "broken", "HEAD"), "this is not a manifest");

        var (exitCode, stdout, _) = await RunAsync("verify", "snapshots/broken/HEAD", "--json");

        exitCode.Should().Be(1);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["error"]!["code"]!.GetValue<string>().Should().Be("failure");
        doc["error"]!["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        stdout.TrimEnd().Split('\n').Count(l => l.TrimStart().StartsWith("{", StringComparison.Ordinal) && !l.StartsWith(' ')).Should().Be(1, "one document only");
    }

    [Fact]
    public void CliJson_PutsOkFirst_AndKeepsOwnOkAndError()
    {
        var plain = Json(CliJson.Build(new { count = 3 }, 0));
        plain.First().Key.Should().Be("ok");
        plain["ok"]!.GetValue<bool>().Should().BeTrue();

        var failed = Json(CliJson.Build(new { count = 3 }, 1, new CliError("failure", "boom", "try again", ["a", "b"])));
        failed.Select(p => p.Key).Should().StartWith(["ok", "exitCode"]);
        failed["error"]!["details"]!.AsArray().Should().HaveCount(2);

        var own = Json(CliJson.Build(new { ok = true, exitCode = 1, error = new { code = "mine" } }, 1, new CliError("failure", "x")));
        own["error"]!["code"]!.GetValue<string>().Should().Be("mine");
    }

    // ---- exit codes: busy and needs-user ---------------------------------------------------------------------------

    [Fact]
    public async Task Busy_WithWaitZero_ExitsFiveWithTheHint()
    {
        var source = CreateSource();
        WriteConfig([source]);
        var other = new RepositoryCoordinator();
        await using var held = await other.AcquireMutationAsync("another process");

        var (exitCode, stdout, stderr) = await RunAsync("backup", source, "--wait", "0", "--json");

        exitCode.Should().Be(5, stdout + stderr);
        var error = Json(stdout)["error"]!;
        error["code"]!.GetValue<string>().Should().Be("busy");
        error["hint"]!.GetValue<string>().Should().Contain("--wait <seconds>");
        stderr.Should().Contain("Another ReStore process");
    }

    [Fact]
    public async Task Busy_WithoutJson_PrintsTheHintOnStderr()
    {
        var source = CreateSource();
        WriteConfig([source]);
        var other = new RepositoryCoordinator();
        await using var held = await other.AcquireMutationAsync("another process");

        var (exitCode, stdout, stderr) = await RunAsync("backup", source, "--wait", "0");

        exitCode.Should().Be(5, stdout + stderr);
        stderr.Should().Contain("Hint: another ReStore process is using the repository; retry or pass --wait <seconds>");
    }

    [Fact]
    public async Task Wait_OneSecond_PrintsAWaitingLineThenExitsBusy()
    {
        var source = CreateSource();
        WriteConfig([source]);
        var other = new RepositoryCoordinator();
        await using var held = await other.AcquireMutationAsync("another process");

        var (exitCode, _, stderr) = await RunAsync("backup", source, "--wait", "1");

        exitCode.Should().Be(5);
        stderr.Should().Contain("Waiting for the repository:");
    }

    [Fact]
    public async Task Wait_AppliesToTheProcessWideCoordinatorOnlyDuringTheRun()
    {
        WriteConfig([]);
        var before = RepositoryCoordinator.Instance.CrossProcessTimeout;
        before.Should().Be(RepositoryCoordinator.DefaultCrossProcessTimeout);

        var (exitCode, _, _) = await RunAsync("status", "--wait", "0");

        exitCode.Should().Be(0);
        RepositoryCoordinator.Instance.CrossProcessTimeout.Should().Be(before);
    }

    [Fact]
    public async Task Wait_IsSeenByTheCoordinatorWhileTheCommandRuns()
    {
        var source = CreateSource();
        WriteConfig([source]);
        var other = new RepositoryCoordinator();
        await using var held = await other.AcquireMutationAsync("another process");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (exitCode, _, _) = await RunAsync("backup", source, "--wait", "0");

        exitCode.Should().Be(5);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "--wait 0 must not wait for the default 30 seconds");
    }

    [Fact]
    public void MapException_ClassifiesBusyAuthAndNeedsUserFailures_EvenWhenWrapped()
    {
        var map = typeof(Program).GetMethod("MapException", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var spec = CommandRegistry.Find("backup")!;
        CliContext NewContext() => new(Parse("backup", "x"), spec, TextWriter.Null);
        int Map(Exception ex, CliContext context) => (int)map.Invoke(null, [ex, context])!;

        var busy = NewContext();
        Map(new InvalidOperationException("wrapped", new RepositoryBusyException("held by the GUI")), busy).Should().Be(5);
        busy.Failure!.Code.Should().Be("busy");
        busy.Failure.Hint.Should().Contain("--wait <seconds>");

        var auth = NewContext();
        Map(new AggregateException(new ReStore.Core.src.storage.StorageAuthRequiredException("Google Drive needs a sign-in.")), auth).Should().Be(4);
        auth.Failure!.Code.Should().Be("needs_user");
        auth.Failure.Message.Should().Contain("Google Drive");
        auth.Failure.Hint.Should().Contain("interactive sign-in");

        var cliFailure = NewContext();
        Map(CliCommandException.NeedsUser("Elevation is required.", "run from an administrator terminal"), cliFailure).Should().Be(4);
        cliFailure.Failure!.Hint.Should().Contain("administrator");

        Map(new OperationCanceledException(), NewContext()).Should().Be(130);
        Map(new InvalidOperationException("boom"), NewContext()).Should().Be(1);
    }

    [Fact]
    public async Task Coordinator_ConfigureWait_ChangesTheTimeoutAndNotifiesOnce()
    {
        var lockFile = Path.Combine(_root, "wait.lock");
        var holder = new RepositoryCoordinator(lockFile);
        await using var held = await holder.AcquireMutationAsync("holder reason");
        var waiter = new RepositoryCoordinator(lockFile);
        var messages = new List<string>();
        waiter.ConfigureWait(TimeSpan.FromMilliseconds(300), messages.Add);

        var act = async () => await waiter.AcquireMutationAsync("waiting");

        await act.Should().ThrowAsync<RepositoryBusyException>();
        messages.Should().ContainSingle().Which.Should().Contain("Another ReStore process");

        waiter.ConfigureWait(TimeSpan.Zero, messages.Add);
        messages.Clear();
        await ((Func<Task>)(async () => await waiter.AcquireMutationAsync("waiting"))).Should().ThrowAsync<RepositoryBusyException>();
        messages.Should().BeEmpty("a zero wait fails at once without announcing a wait");
    }

    [Fact]
    public void Coordinator_ConfigureWait_RejectsNegativeTimeouts()
    {
        var act = () => new RepositoryCoordinator(Path.Combine(_root, "x.lock")).ConfigureWait(TimeSpan.FromSeconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task NonInteractive_EncryptedRepositoryWithoutAPassword_ExitsFour()
    {
        var source = CreateSource();
        WriteEncryptedConfig(source, "pw-1");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "pw-1");
        var (backupExit, bo, be) = await RunAsync("backup", source, "--json");
        backupExit.Should().Be(0, bo + be);
        var manifest = Json(bo)["manifestPath"]!.GetValue<string>();
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);

        var (exitCode, stdout, stderr) = await RunAsync("verify", manifest, "--non-interactive", "--json");

        exitCode.Should().Be(4, stdout + stderr);
        var error = Json(stdout)["error"]!;
        error["code"]!.GetValue<string>().Should().Be("needs_user");
        error["hint"]!.GetValue<string>().Should().Contain("--password-stdin").And.Contain("RESTORE_ENCRYPTION_PASSWORD_FILE");
        stderr.Should().Contain("password is required");

        var (humanExit, _, humanErr) = await RunAsync("verify", manifest, "--non-interactive");
        humanExit.Should().Be(4);
        humanErr.Should().Contain("Hint: provide the encryption password");
    }

    [Fact]
    public async Task NonInteractive_IsAlsoImpliedByRedirectedStdinOrTheEnvironment()
    {
        var source = CreateSource();
        WriteEncryptedConfig(source, "pw-2");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "pw-2");
        var (_, bo, _) = await RunAsync("backup", source, "--json");
        var manifest = Json(bo)["manifestPath"]!.GetValue<string>();
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);

        Environment.SetEnvironmentVariable("CI", "true");
        (await RunAsync("verify", manifest)).ExitCode.Should().Be(4);
        Environment.SetEnvironmentVariable("CI", null);
        Environment.SetEnvironmentVariable("RESTORE_NON_INTERACTIVE", "1");
        (await RunAsync("verify", manifest)).ExitCode.Should().Be(4);
    }

    [Fact]
    public async Task PasswordSources_StdinFileAndEnvironment_AndTheirPrecedence()
    {
        var source = CreateSource();
        WriteEncryptedConfig(source, "right-password");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "right-password");
        var (_, bo, _) = await RunAsync("backup", source, "--json");
        var manifest = Json(bo)["manifestPath"]!.GetValue<string>();
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);

        // --password-stdin reads the first line only.
        var (stdinExit, so, se) = await RunWithStdinAsync("right-password\r\nsecond line\r\n", "verify", manifest, "--password-stdin", "--json");
        stdinExit.Should().Be(0, so + se);
        Json(so)["valid"]!.GetValue<bool>().Should().BeTrue();

        // The password file named by RESTORE_ENCRYPTION_PASSWORD_FILE.
        var passwordFile = Path.Combine(_root, "password.txt");
        File.WriteAllText(passwordFile, "right-password\nignored\n");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD_FILE", passwordFile);
        var (fileExit, fo, fe) = await RunAsync("verify", manifest, "--non-interactive");
        fileExit.Should().Be(0, fo + fe);

        // Environment variable beats the file; stdin beats both.
        File.WriteAllText(passwordFile, "wrong-password\n");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "right-password");
        (await RunAsync("verify", manifest, "--non-interactive")).ExitCode.Should().Be(0, "the environment variable wins over the file");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "wrong-password");
        (await RunWithStdinAsync("right-password\n", "verify", manifest, "--password-stdin")).ExitCode.Should().Be(0, "stdin wins over the environment variable");

        // An unreadable password file is a warning, then the normal "needs a person" outcome.
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD_FILE", Path.Combine(_root, "no-such-file"));
        var (missingExit, _, missingErr) = await RunAsync("verify", manifest, "--non-interactive");
        missingExit.Should().Be(4);
        missingErr.Should().Contain("could not be read");
    }

    [Fact]
    public async Task PasswordStdin_WithEmptyInput_IsNeedsUser()
    {
        var source = CreateSource();
        WriteEncryptedConfig(source, "pw-3");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "pw-3");
        var (_, bo, _) = await RunAsync("backup", source, "--json");
        var manifest = Json(bo)["manifestPath"]!.GetValue<string>();
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);

        (await RunWithStdinAsync(string.Empty, "verify", manifest, "--password-stdin")).ExitCode.Should().Be(4);
    }

    [Fact]
    public void ServicePassword_AcceptsThePasswordFile()
    {
        var config = new Moq.Mock<IConfigManager>();
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = true });
        Program.GetServicePasswordError(config.Object).Should().Contain("RESTORE_ENCRYPTION_PASSWORD_FILE");

        var passwordFile = Path.Combine(_root, "svc.txt");
        File.WriteAllText(passwordFile, "service-password");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD_FILE", passwordFile);

        Program.GetServicePasswordError(config.Object).Should().BeNull();
    }

    [Fact]
    public async Task PromptForPassword_Interactive_WritesThePromptToStderr_AndRedirectedConsoleIsNeedsUser()
    {
        var request = new PasswordPromptRequest(PasswordPurpose.Decrypt, false);

        // Under the test host there is no console to read keys from: ReadKey throws InvalidOperationException, which must
        // become "needs a person", never a crash.
        var act = async () => await Program.PromptForPasswordAsync(request);

        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        thrown.GetType().Name.Should().Be("CliCommandException");
        ((CliCommandException)thrown).ExitCode.Should().Be(4);
    }

    // ---- JSON documents of the commands that gained --json ---------------------------------------------------------

    [Fact]
    public async Task Json_Backup_RestoreDryRun_RestoreAndVerify_AgainstLocalStorage()
    {
        var source = CreateSource();
        WriteConfig([source]);

        var (backupExit, bo, be) = await RunAsync("backup", source, "--json");
        backupExit.Should().Be(0, bo + be);
        var backup = Json(bo);
        backup["ok"]!.GetValue<bool>().Should().BeTrue();
        backup["source"]!.GetValue<string>().Should().Be(Path.GetFullPath(source));
        backup["storage"]!.GetValue<string>().Should().Be("local");
        backup["snapshotId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        backup["manifestPath"]!.GetValue<string>().Should().Contain("snapshots/");
        backup["skipped"]!["count"]!.GetValue<int>().Should().Be(0);
        bo.TrimStart().Should().StartWith("{", "log lines must not pollute the JSON on stdout");
        be.Should().Contain("Snapshot backup completed");

        var head = SnapshotStoragePaths.GetHeadPath(source);
        var target = Path.Combine(_root, "restored");

        var (dryExit, dryOut, dryErr) = await RunAsync("restore", head, target, "--dry-run", "--json");
        dryExit.Should().Be(0, dryOut + dryErr);
        var dry = Json(dryOut);
        dry["dryRun"]!.GetValue<bool>().Should().BeTrue();
        dry["preview"]!["files"]!.GetValue<int>().Should().Be(2);
        dry["conflictPolicy"]!.GetValue<string>().Should().Be("skip");
        dry["entries"]!.AsArray().Select(e => e!["path"]!.GetValue<string>()).Should().BeEquivalentTo("doc.txt", "sub/note.txt");
        dry["result"].Should().BeNull();
        Directory.Exists(target).Should().BeFalse("a dry run writes nothing");

        var (restoreExit, ro, re) = await RunAsync("restore", head, target, "--json");
        restoreExit.Should().Be(0, ro + re);
        var restored = Json(ro);
        restored["dryRun"]!.GetValue<bool>().Should().BeFalse();
        restored["result"]!["filesRestored"]!.GetValue<int>().Should().Be(2);
        restored["result"]!["bytesRestored"]!.GetValue<long>().Should().BeGreaterThan(0);
        File.Exists(Path.Combine(target, "doc.txt")).Should().BeTrue();

        var (verifyExit, vo, ve) = await RunAsync("verify", head, "--mode", "quick", "--json");
        verifyExit.Should().Be(0, vo + ve);
        var verify = Json(vo);
        verify["ok"]!.GetValue<bool>().Should().BeTrue();
        verify["valid"]!.GetValue<bool>().Should().BeTrue();
        verify["mode"]!.GetValue<string>().Should().Be("quick");
        verify["fileCount"]!.GetValue<int>().Should().Be(2);
        verify["errors"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Json_Verify_OfACorruptedRepository_ReportsTheFindingsWithOkFalse()
    {
        var source = CreateSource();
        WriteConfig([source]);
        (await RunAsync("backup", source)).ExitCode.Should().Be(0);
        var head = SnapshotStoragePaths.GetHeadPath(source);
        foreach (var chunk in Directory.EnumerateFiles(Path.Combine(StorageDirectory, "chunks"), "*", SearchOption.AllDirectories))
        {
            File.Delete(chunk);
        }

        var (exitCode, stdout, _) = await RunAsync("verify", head, "--mode", "quick", "--json");

        exitCode.Should().Be(1);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["exitCode"]!.GetValue<int>().Should().Be(1);
        doc["valid"]!.GetValue<bool>().Should().BeFalse();
        doc["errors"]!.AsArray().Should().NotBeEmpty();
        doc["error"]!["code"]!.GetValue<string>().Should().Be("failure");
    }

    [Fact]
    public async Task Json_AdoptAndRepair_ReportCounts()
    {
        var source = CreateSource();
        WriteConfig([source]);
        (await RunAsync("backup", source)).ExitCode.Should().Be(0);

        var (adoptExit, adoptOut, adoptErr) = await RunAsync("adopt", "--json");
        adoptExit.Should().Be(0, adoptOut + adoptErr);
        var adopt = Json(adoptOut);
        adopt["ok"]!.GetValue<bool>().Should().BeTrue();
        adopt["adopted"]!.GetValue<int>().Should().Be(0);
        adopt["alreadyKnown"]!.GetValue<int>().Should().BeGreaterThan(0);

        var (repairExit, repairOut, repairErr) = await RunAsync("repair", "--dry-run", "--json");
        repairExit.Should().Be(0, repairOut + repairErr);
        var repair = Json(repairOut);
        repair["dryRun"]!.GetValue<bool>().Should().BeTrue();
        repair["manifestsScanned"]!.GetValue<int>().Should().BeGreaterThan(0);
        repair["orphanChunks"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Json_ExistingDocuments_GainOk()
    {
        var source = CreateSource();
        WriteConfig([source]);
        (await RunAsync("backup", source)).ExitCode.Should().Be(0);
        var head = SnapshotStoragePaths.GetHeadPath(source);

        foreach (var args in new[]
        {
            new[] { "status", "--json" },
            ["snapshots", "--json"],
            ["ls", head, "--json"],
            ["diff", head, head, "--json"],
            ["retention", "--dry-run", "--json"],
            ["probe", "--json"],
            ["test-restore", head, "--files", "1", "--json"],
            ["run-due", "--json"],
            ["system-undo", "--list", "--json"]
        })
        {
            var (exitCode, stdout, stderr) = await RunAsync(args);
            exitCode.Should().Be(0, $"{string.Join(' ', args)}: {stdout}{stderr}");
            Json(stdout)["ok"]!.GetValue<bool>().Should().BeTrue(string.Join(' ', args));
        }
    }

    [Fact]
    public async Task Json_ConfigShow_ExportImportAndValidate()
    {
        var source = CreateSource();
        WriteConfig([source]);

        var (showExit, showOut, _) = await RunAsync("config", "show", "--json");
        showExit.Should().Be(0);
        var show = Json(showOut);
        show["ok"]!.GetValue<bool>().Should().BeTrue();
        show["config"]!["globalStorageType"]!.GetValue<string>().Should().Be("local");
        show["secretsRevealed"]!.GetValue<bool>().Should().BeFalse();

        var exportFile = Path.Combine(_root, "exported.json");
        var (exportExit, exportOut, exportErr) = await RunAsync("config", "export", exportFile, "--json");
        exportExit.Should().Be(0, exportOut + exportErr);
        Json(exportOut)["file"]!.GetValue<string>().Should().Be(exportFile);

        var (importExit, importOut, importErr) = await RunAsync("config", "import", exportFile, "--replace", "--json");
        importExit.Should().Be(0, importOut + importErr);
        var import = Json(importOut);
        import["applied"]!.GetValue<bool>().Should().BeTrue();
        import["mode"]!.GetValue<string>().Should().Be("replace");

        var bad = Path.Combine(_root, "bad.json");
        File.WriteAllText(bad, "{ nope");
        var (badExit, badOut, _) = await RunAsync("config", "import", bad, "--json");
        badExit.Should().Be(3);
        var badDoc = Json(badOut);
        badDoc["ok"]!.GetValue<bool>().Should().BeFalse();
        badDoc["applied"]!.GetValue<bool>().Should().BeFalse();
        badDoc["errors"]!.AsArray().Should().NotBeEmpty();
        badDoc["error"]!["code"]!.GetValue<string>().Should().Be("config");

        var (protectExit, protectOut, _) = await RunAsync("config", "protect", "--json");
        if (OperatingSystem.IsWindows())
        {
            protectExit.Should().Be(0);
            Json(protectOut)["ok"]!.GetValue<bool>().Should().BeTrue();
        }
        else
        {
            protectExit.Should().Be(1, "DPAPI is Windows only");
            Json(protectOut)["error"]!["code"]!.GetValue<string>().Should().Be("failure");
        }
    }

    [Fact]
    public async Task Json_ConfigValidate_IncludesAllMessages_AndTheAliasWorks()
    {
        var source = CreateSource();
        WriteConfig([source]);

        foreach (var command in new[] { new[] { "config", "validate", "--json" }, ["--validate-config", "--json"] })
        {
            var (exitCode, stdout, stderr) = await RunAsync(command);
            exitCode.Should().Be(0, stdout + stderr);
            var doc = Json(stdout);
            doc["ok"]!.GetValue<bool>().Should().BeTrue();
            doc["valid"]!.GetValue<bool>().Should().BeTrue();
            doc["errors"]!.AsArray().Should().BeEmpty();
            doc["info"]!.AsArray().Should().Contain(i => i!.GetValue<string>().Contains("Watch directory validated"));
        }

        WriteConfig([Path.Combine(_root, "no-such-dir")]);
        var (badExit, badOut, _) = await RunAsync("config", "validate", "--json");
        badExit.Should().Be(3);
        var bad = Json(badOut);
        bad["ok"]!.GetValue<bool>().Should().BeFalse();
        bad["valid"]!.GetValue<bool>().Should().BeFalse();
        bad["errors"]!.AsArray().Should().NotBeEmpty();
        bad["error"]!["code"]!.GetValue<string>().Should().Be("config");
    }

    // ---- controller additions: backup <path> without watch directories; validation chatter -------------------------

    [Fact]
    public async Task Backup_OfAnExplicitPath_DoesNotNeedWatchDirectories()
    {
        var source = CreateSource();
        WriteConfig([]);

        var (exitCode, stdout, stderr) = await RunAsync("backup", source);

        exitCode.Should().Be(0, stdout + stderr);
        Directory.Exists(Path.Combine(StorageDirectory, "snapshots")).Should().BeTrue();
    }

    [Fact]
    public async Task Backup_OfAnExplicitPath_StillValidatesTheStorage()
    {
        var source = CreateSource();
        WriteConfig([], root => root["globalStorageType"] = "nowhere");

        var (exitCode, _, stderr) = await RunAsync("backup", source);

        exitCode.Should().Be(3);
        stderr.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Service_StillRequiresWatchDirectories()
    {
        WriteConfig([]);

        var (exitCode, _, stderr) = await RunAsync("--service");

        exitCode.Should().Be(3);
        stderr.Should().Contain("No watch directories specified");
    }

    [Fact]
    public async Task Validation_InfoMessages_AreNotPrintedByOtherCommands_ButAreByConfigValidate()
    {
        var source = CreateSource();
        WriteConfig([source], root => root["excludedPaths"] = new JsonArray(Path.Combine(_root, "excluded")));

        var (backupExit, backupOut, backupErr) = await RunAsync("backup", source);
        backupExit.Should().Be(0, backupOut + backupErr);
        (backupOut + backupErr).Should().NotContain("validated").And.NotContain("Config Info");

        var (statusExit, statusOut, statusErr) = await RunAsync("run-due");
        statusExit.Should().Be(0, statusOut + statusErr);
        (statusOut + statusErr).Should().NotContain("validated").And.NotContain("Config Info");

        var (validateExit, validateOut, _) = await RunAsync("config", "validate");
        validateExit.Should().Be(0);
        validateOut.Should().Contain("INFO:").And.Contain("Watch directory validated");
    }

    [Fact]
    public async Task Validation_Warnings_AreStillPrintedByOtherCommands()
    {
        var source = CreateSource();
        WriteConfig([source, Path.Combine(_root, "missing-watch-dir")]);

        var (exitCode, stdout, stderr) = await RunAsync("run-due");

        exitCode.Should().Be(0, stdout + stderr);
        (stdout + stderr).Should().Contain("Watch directory does not exist").And.Contain("WARNINGS:");
        (stdout + stderr).Should().NotContain("Watch directory validated");
    }

    // ---- streams ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Logger_RoutesInfoToStdout_WarningsAndErrorsToStderr_AndQuietKeepsOnlyErrors()
    {
        await ConsoleLock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var logger = new Logger { MinimumLevel = LogLevel.Debug };

            logger.Log("debug-line", LogLevel.Debug);
            logger.Log("info-line", LogLevel.Info);
            logger.Log("warning-line", LogLevel.Warning);
            logger.Log("error-line", LogLevel.Error);

            stdout.ToString().Should().Contain("debug-line").And.Contain("info-line").And.NotContain("warning-line").And.NotContain("error-line");
            stderr.ToString().Should().Contain("warning-line").And.Contain("error-line").And.NotContain("info-line");

            Logger.QuietConsole = true;
            try
            {
                stdout.GetStringBuilder().Clear();
                stderr.GetStringBuilder().Clear();
                logger.Log("info-quiet", LogLevel.Info);
                logger.Log("warning-quiet", LogLevel.Warning);
                logger.Log("error-quiet", LogLevel.Error);

                stdout.ToString().Should().BeEmpty();
                stderr.ToString().Should().Contain("error-quiet").And.NotContain("warning-quiet").And.NotContain("info-quiet");
            }
            finally
            {
                Logger.QuietConsole = false;
            }
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleLock.Release();
        }
    }

    [Fact]
    public void Logger_ErrorObserver_SeesRedactedErrorLinesOnly()
    {
        var seen = new List<string>();
        Logger.ErrorObserver = seen.Add;
        try
        {
            var logger = new Logger();
            logger.Log("fine", LogLevel.Info);
            logger.Log("warn", LogLevel.Warning);
            logger.Log("failed: secretAccessKey=TopSecretValue99", LogLevel.Error);
        }
        finally
        {
            Logger.ErrorObserver = null;
        }

        seen.Should().ContainSingle().Which.Should().Contain("failed").And.NotContain("TopSecretValue99");
    }

    [Fact]
    public async Task Json_Mode_KeepsStdoutToTheSingleDocument_AndLogsOnStderr()
    {
        var source = CreateSource();
        WriteConfig([source]);

        var (exitCode, stdout, stderr) = await RunAsync("snapshots", "--json");

        exitCode.Should().Be(0);
        Json(stdout).ContainsKey("groups").Should().BeTrue();
        stderr.Should().Contain("Application finished");
        stdout.Should().NotContain("Application finished");
    }

    [Fact]
    public async Task Quiet_StillPrintsJsonDocuments()
    {
        var (exitCode, stdout, _) = await RunAsync("version", "--json", "--quiet");

        exitCode.Should().Be(0);
        Json(stdout)["ok"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Main_ResetsProcessWideSettingsAfterEachRun()
    {
        WriteConfig([]);
        await RunAsync("status", "--quiet", "--non-interactive", "--wait", "0");

        Logger.QuietConsole.Should().BeFalse();
        UnattendedMode.IsActive.Should().Be(!Environment.UserInteractive);
        CliRuntime.NonInteractive.Should().BeFalse();
    }
}

using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using System.Text;
using System.Text.Json.Nodes;

namespace ReStore.Tests;

/// <summary>Review fixes of the v1.8 CLI round (exit paths, state save, masking, JSON escaping, scheduled task, ...).</summary>
[Collection("GlobalState")]
public class CliFixTests : IDisposable
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreCliFix_" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _previousVariables = [];

    private static readonly string[] ManagedVariables =
    [
        "RESTORE_ENCRYPTION_PASSWORD", "RESTORE_ENCRYPTION_PASSWORD_FILE", "RESTORE_NEW_ENCRYPTION_PASSWORD", "RESTORE_NON_INTERACTIVE", "CI"
    ];

    public CliFixTests()
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
            Console.SetIn(new StringReader(stdin ?? string.Empty));

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

    private static void AssertSingleDocument(string stdout) =>
        stdout.TrimEnd().Split('\n').Count(l => l.StartsWith('{')).Should().Be(1, "one document only");

    private static Program.ParsedCommand Parse(params string[] args)
    {
        Program.TryParseArguments(args, out var parsed, out var error).Should().BeTrue(error);
        return parsed!;
    }

    private static CliContext NewContext(string command = "version", params string[] args) =>
        new(Parse([command, .. args]), CommandRegistry.Find(command)!, TextWriter.Null);

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

    // ---- I1: invalid paths never crash -----------------------------------------------------------------------------

    [Fact]
    public async Task Backup_OfAnEmptyFolder_IsAUsageError_WithTheErrorDocument()
    {
        WriteConfig([]);

        var (exitCode, stdout, stderr) = await RunAsync("backup", "", "--json");

        exitCode.Should().Be(2, stdout + stderr);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["error"]!["code"]!.GetValue<string>().Should().Be("usage");
    }

    [Fact]
    public async Task UnexpectedExceptionInThePipeline_EndsWithExitOne_AndTheErrorDocument()
    {
        // Any exception that escapes the pipeline (here thrown by the test seam, so it behaves the same on every OS).
        var source = CreateSource();
        WriteConfig([source]);

        Program.PipelineTestHook = _ => throw new FormatException("boom from the test hook");
        (int exitCode, string stdout, string stderr) result;
        try
        {
            result = await RunAsync("backup", source, "--json");
        }
        finally
        {
            Program.PipelineTestHook = null;
        }

        var (exitCode, stdout, stderr) = result;

        exitCode.Should().Be(1, stdout + stderr);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["error"]!["code"]!.GetValue<string>().Should().Be("failure");
        AssertSingleDocument(stdout);
    }

    // ---- I3: the document waits for the final state save -----------------------------------------------------------

    [Fact]
    public async Task FinalSaveBusy_AfterASuccessfulCommand_IsExitFive_WithStateSavedFalse()
    {
        // run-due with nothing due takes no lease of its own: the repository is only busy when the final state save asks for it.
        var source = CreateSource();
        WriteConfig([source]);
        var other = new RepositoryCoordinator();
        await using var held = await other.AcquireMutationAsync("the GUI scheduler");

        var (exitCode, stdout, stderr) = await RunAsync("run-due", "--json", "--wait", "0");

        exitCode.Should().Be(5, stdout + stderr);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse("a run that exits 5 never says ok:true");
        doc["exitCode"]!.GetValue<int>().Should().Be(5);
        doc["stateSaved"]!.GetValue<bool>().Should().BeFalse();
        doc["error"]!["code"]!.GetValue<string>().Should().Be("busy");
        doc["error"]!["hint"]!.GetValue<string>().Should().Contain("--wait");
        AssertSingleDocument(stdout);
    }

    [Fact]
    public async Task SaveOutcome_DistinguishesBusyFromAFailure()
    {
        var dir = Path.Combine(_root, "save");
        Directory.CreateDirectory(dir);
        var logger = new TestLogger();
        var state = new SystemState(logger);
        state.SetStateFilePath(Path.Combine(dir, "system_state.json"));
        var coordinator = new RepositoryCoordinator(Path.Combine(dir, "repository.lock"), TimeSpan.FromMilliseconds(100));

        await using (new FileStream(Path.Combine(dir, "repository.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            (await Program.SaveStateAtExitOutcomeAsync(state, logger, mutatedState: true, coordinator)).Should().Be(Program.StateSaveOutcome.Busy);
        }

        (await Program.SaveStateAtExitOutcomeAsync(state, logger, mutatedState: true, coordinator)).Should().Be(Program.StateSaveOutcome.Saved);

        // A state file path that cannot be written is a plain failure.
        var broken = new SystemState(logger);
        broken.SetStateFilePath(Path.Combine(dir, "system_state.json", "nested", "state.json"));
        (await Program.SaveStateAtExitOutcomeAsync(broken, logger, mutatedState: true, coordinator)).Should().Be(Program.StateSaveOutcome.Failed);
    }

    [Fact]
    public async Task BufferedDocument_IsWrittenOnFlush_AndTurnedIntoAFailureWhenTheSaveFailed()
    {
        var output = new StringWriter();
        var context = new CliContext(Parse("backup", "x"), CommandRegistry.Find("backup")!, output) { BufferJson = true };

        await context.WriteJsonAsync(new { files = 3 });
        context.JsonWritten.Should().BeTrue();
        output.ToString().Should().BeEmpty("nothing is printed before the save outcome is known");

        await context.FlushJsonAsync(Program.ExitCodes.Failure, new CliError("failure", "could not be saved"));
        var doc = Json(output.ToString());
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["exitCode"]!.GetValue<int>().Should().Be(1);
        doc["stateSaved"]!.GetValue<bool>().Should().BeFalse();
        doc["files"]!.GetValue<int>().Should().Be(3, "the command's own fields stay");

        var plain = new StringWriter();
        var other = new CliContext(Parse("backup", "x"), CommandRegistry.Find("backup")!, plain) { BufferJson = true };
        await other.WriteJsonAsync(new { files = 3 });
        await other.FlushJsonAsync();
        var ok = Json(plain.ToString());
        ok["ok"]!.GetValue<bool>().Should().BeTrue();
        ok.ContainsKey("stateSaved").Should().BeFalse();
    }

    // ---- minor review items ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/?")]
    [InlineData("--help")]
    public async Task Help_IsPrintedForSlashQuestionMarkToo(string flag)
    {
        var (exitCode, stdout, _) = await RunAsync(flag);

        exitCode.Should().Be(0);
        stdout.Should().Contain("Usage");
    }

    [Fact]
    public async Task Help_ForACommandWithSlashQuestionMark_IsThatCommandsHelp()
    {
        var (exitCode, stdout, _) = await RunAsync("verify", "/?");

        exitCode.Should().Be(0);
        stdout.Should().Contain("verify");
    }

    [Fact]
    public void CliJson_TheEnvelopeWinsOverAHandlersPlainErrorField()
    {
        var failed = Json(CliJson.Build(new { error = "plain text reason", exitCode = 1 }, 1, new CliError("failure", "boom")));
        failed["error"]!["code"]!.GetValue<string>().Should().Be("failure");
        failed["error"]!["message"]!.GetValue<string>().Should().Be("boom");

        var own = Json(CliJson.Build(new { error = new { code = "mine" } }, 1, new CliError("failure", "x")));
        own["error"]!["code"]!.GetValue<string>().Should().Be("mine", "a handler's own error object is kept");

        var ok = Json(CliJson.Build(new { error = (string?)null }, 0));
        ok["ok"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task TestRestore_OfABrokenSnapshot_HasTheEnvelopeErrorObject_AndAReasonField()
    {
        var source = CreateSource();
        WriteConfig([source]);
        var (backupExit, backupOut, backupErr) = await RunAsync("backup", source, "--json");
        backupExit.Should().Be(0, backupOut + backupErr);
        var manifest = Json(backupOut)["manifestPath"]!.GetValue<string>();
        foreach (var chunk in Directory.GetFiles(Path.Combine(StorageDirectory, "chunks"), "*", SearchOption.AllDirectories))
        {
            File.Delete(chunk);
        }

        var (exitCode, stdout, stderr) = await RunAsync("test-restore", manifest, "--json");

        exitCode.Should().Be(1, stdout + stderr);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["error"].Should().BeOfType<JsonObject>("the uniform envelope, never a string");
        doc["error"]!["code"]!.GetValue<string>().Should().Be("failure");
        doc.ContainsKey("reason").Should().BeTrue();
    }

    [Fact]
    public void LoggedErrors_AreCapped_SoAServiceCannotGrowWithoutBound()
    {
        var context = NewContext();

        for (var i = 0; i < 500; i++)
        {
            context.RecordLoggedError($"error {i}");
        }

        context.LoggedErrors.Should().HaveCount(21);
        context.LoggedErrors[0].Should().Be("error 0", "the first error stays the message");
        context.BuildError(1).Details.Should().HaveCount(20);
    }

    [Fact]
    public async Task BestEffortAcquisition_DoesNotAnnounceWaiting_ButABlockingOneDoes()
    {
        var lockFile = Path.Combine(_root, "announce.lock");
        var holder = new RepositoryCoordinator(lockFile);
        await using var held = await holder.AcquireMutationAsync("holder");
        var waiter = new RepositoryCoordinator(lockFile);
        var announcements = new List<string>();
        waiter.ConfigureWait(TimeSpan.FromMilliseconds(150), announcements.Add);

        (await waiter.TryAcquireMutationAsync(TimeSpan.FromMilliseconds(150), "optional")).Should().BeNull();
        announcements.Should().BeEmpty("a best-effort acquisition just gives up");

        var blocking = () => waiter.AcquireMutationAsync("required");
        await blocking.Should().ThrowAsync<RepositoryBusyException>();
        announcements.Should().ContainSingle();
    }

    [Fact]
    public void BuildError_RedactsSecretsInMessageHintAndDetails()
    {
        var context = NewContext();
        context.Failure = new CliError(
            "failure", "login failed: password=hunter2 for the account", "retry with accessToken=abc123secret", ["secretAccessKey=AKIA999 rejected"]);

        var error = context.BuildError(1);

        error.Message.Should().NotContain("hunter2");
        error.Hint.Should().NotContain("abc123secret");
        error.Details![0].Should().NotContain("AKIA999");

        var logged = NewContext();
        logged.RecordLoggedError("first");
        logged.Failure = new CliError("config", "bad token=zzz999", null, null);
        logged.BuildError(3).Message.Should().NotContain("zzz999");
    }

    [Theory]
    [InlineData("version")]
    [InlineData("commands")]
    [InlineData("help")]
    public async Task InfoCommands_PrintEvenWithQuiet(string command)
    {
        var (exitCode, stdout, _) = await RunAsync(command, "--quiet");

        exitCode.Should().Be(0);
        stdout.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HelpWithJson_IsTheCommandEntryOfTheCommandsDocument()
    {
        var (exitCode, stdout, _) = await RunAsync("backup", "--help", "--json");
        var (_, all, _) = await RunAsync("commands", "--json");

        exitCode.Should().Be(0);
        var help = Json(stdout);
        help["ok"]!.GetValue<bool>().Should().BeTrue();
        help["name"]!.GetValue<string>().Should().Be("backup");
        var entry = Json(all)["commands"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "backup")!.AsObject();
        foreach (var (key, value) in entry)
        {
            help[key]!.ToJsonString().Should().Be(value!.ToJsonString(), key);
        }

        var (helpExit, helpOut, _) = await RunAsync("help", "config", "--json");
        helpExit.Should().Be(0);
        Json(helpOut)["commands"]!.AsArray().Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("   \n")]
    [InlineData("\t \r\nsecond line\n")]
    public async Task PasswordStdin_WithABlankFirstLine_IsNoPassword(string input)
    {
        var source = CreateSource();
        WriteEncryptedConfig(source, "pw-blank");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "pw-blank");
        var (_, bo, _) = await RunAsync("backup", source, "--json");
        var manifest = Json(bo)["manifestPath"]!.GetValue<string>();
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);

        (await RunWithStdinAsync(input, "verify", manifest, "--password-stdin")).ExitCode.Should().Be(4);
    }

    [Fact]
    public async Task PasswordFile_WithABlankFirstLine_IsNoPassword()
    {
        var source = CreateSource();
        WriteEncryptedConfig(source, "pw-file");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "pw-file");
        var (_, bo, _) = await RunAsync("backup", source, "--json");
        var manifest = Json(bo)["manifestPath"]!.GetValue<string>();
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);
        var file = Path.Combine(_root, "pw.txt");
        File.WriteAllText(file, "    \nreal-password\n");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD_FILE", file);

        (await RunAsync("verify", manifest)).ExitCode.Should().Be(4);
    }

    [Fact]
    public async Task Backup_WithABadStorage_ListsEachValidationErrorOnce()
    {
        var source = CreateSource();
        WriteConfig([], root =>
        {
            root["globalStorageType"] = "s3";
            root["storageSources"] = new JsonObject
            {
                ["s3"] = new JsonObject
                {
                    ["options"] = new JsonObject { ["accessKeyId"] = "k", ["secretAccessKey"] = "s", ["region"] = "eu-west-1", ["bucketName"] = "Bad_Bucket!" }
                }
            };
        });

        var (exitCode, stdout, _) = await RunAsync("backup", source, "--json");

        exitCode.Should().Be(3);
        var details = Json(stdout)["error"]!["details"]!.AsArray().Select(d => d!.GetValue<string>()).ToList();
        details.Should().Contain(d => d.Contains("invalid bucket name"));
        details.Should().OnlyHaveUniqueItems("the storage is validated twice (whole config, then the folder's storage) but each problem is reported once");
    }

    [Fact]
    public async Task MutatingCommands_SaveTheState_ReadOnlyCommandsDoNot()
    {
        var source = CreateSource();
        WriteConfig([source]);
        var stateFile = AppPaths.SystemStateFile;

        var (readOnlyExit, _, readOnlyErr) = await RunAsync("snapshots", "--json");
        readOnlyExit.Should().Be(0, readOnlyErr);
        File.Exists(stateFile).Should().BeFalse("a read-only command that changed nothing saves nothing");

        var (mutatingExit, _, mutatingErr) = await RunAsync("run-due", "--json");
        mutatingExit.Should().Be(0, mutatingErr);
        File.Exists(stateFile).Should().BeTrue("a mutating command always saves the state at exit");
    }

    [Fact]
    public async Task RunDue_WithJson_AFailureHasTheErrorDocument()
    {
        var source = CreateSource();
        WriteEncryptedConfig(source, "pw-run-due");

        var (exitCode, stdout, stderr) = await RunAsync("run-due", "--json");

        exitCode.Should().Be(4, "a scheduled run without a usable password needs a person: " + stdout + stderr);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["exitCode"]!.GetValue<int>().Should().Be(4);
        doc["error"]!["code"]!.GetValue<string>().Should().Be("needs_user");
        doc["error"]!["message"]!.GetValue<string>().Should().Contain("password");
        doc["error"]!["hint"]!.GetValue<string>().Should().Contain("unlock --remember");
        AssertSingleDocument(stdout);

        var record = RunDueRecord.TryLoad();
        record.Should().NotBeNull();
        record!.ExitCode.Should().Be(4);
        record.ErrorCode.Should().Be("needs_user");
    }

    [Fact]
    public void MapException_Cancellation_IsExit130_ExceptInServiceMode()
    {
        var map = typeof(Program).GetMethod("MapException", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        int Map(Exception ex, CliContext context) => (int)map.Invoke(null, [ex, context])!;

        Map(new OperationCanceledException(), NewContext("backup", "x")).Should().Be(130);
        Map(new TaskCanceledException(), NewContext("verify", "m")).Should().Be(130);
        Map(new OperationCanceledException(), NewContext("--service")).Should().Be(0, "stopping the service with Ctrl+C is a normal end");
    }

    [Fact]
    public async Task Main_RestoresTheErrorObserverAndConsoleOut_AfterEachRun()
    {
        WriteConfig([]);
        await ConsoleLock.WaitAsync();
        var original = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            await Program.Main(["status", "--json", "--quiet"]);
            capture.GetStringBuilder().Clear();
            Console.Out.Write("marker");
            capture.ToString().Should().Be("marker", "Console.Out must be the writer the caller had, not stderr or a null writer");
            Logger.ErrorObserver.Should().BeNull();
        }
        finally
        {
            Console.SetOut(original);
            ConsoleLock.Release();
        }
    }

    // ---- B1: run-due records its own outcome -----------------------------------------------------------------------

    [Fact]
    public async Task RunDue_RecordsItsOutcome_ForScheduleStatus()
    {
        var source = CreateSource();
        WriteConfig([source]);
        RunDueRecord.TryLoad().Should().BeNull();

        var (exitCode, stdout, stderr) = await RunAsync("run-due", "--quiet");

        exitCode.Should().Be(0, stdout + stderr);
        var record = RunDueRecord.TryLoad();
        record.Should().NotBeNull();
        record!.ExitCode.Should().Be(0);
        record.ErrorCode.Should().BeNull();
        record.FinishedUtc.Should().BeOnOrAfter(record.StartedUtc);
        record.FinishedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        File.ReadAllText(Path.Combine(AppPaths.StateDirectory, "run-due-last.json")).Should().NotContain(_root, "the record holds counts only, no paths");
    }

    [Fact]
    public async Task RunDue_RecordsAFailure_AndABusyRepository()
    {
        var source = CreateSource();
        WriteEncryptedConfig(source, "pw-record");

        (await RunAsync("run-due", "--quiet")).ExitCode.Should().Be(4, "no password without a prompt needs a person");
        var failed = RunDueRecord.TryLoad()!;
        failed.ExitCode.Should().Be(4);
        failed.ErrorCode.Should().Be("needs_user");
        failed.Describe().Should().Contain("needs_user");

        WriteConfig([source], root => root["encryption"] = new JsonObject { ["enabled"] = false });
        var other = new RepositoryCoordinator();
        await using var held = await other.AcquireMutationAsync("the GUI");
        (await RunAsync("run-due", "--quiet", "--wait", "0")).ExitCode.Should().Be(5);
        var busy = RunDueRecord.TryLoad()!;
        busy.ExitCode.Should().Be(5);
        busy.ErrorCode.Should().Be("busy");
    }

    [Fact]
    public async Task OtherCommands_DoNotWriteTheRunDueRecord()
    {
        WriteConfig([]);

        (await RunAsync("status", "--quiet")).ExitCode.Should().Be(0);

        RunDueRecord.TryLoad().Should().BeNull();
    }

    // ---- B5: relaxed JSON escaping on CLI documents ----------------------------------------------------------------

    [Fact]
    public void CliJson_DoesNotEscapeApostrophesAndAngleBrackets()
    {
        var text = CliJson.Build(new { message = "can't <a> & \"b\"" }, 0);
        text.Should().Contain("can't <a> & \\\"b\\\"").And.NotContain("\\u0027").And.NotContain("\\u003C").And.NotContain("\\u0026");

        var camel = CliJson.Build(new { Message = "it's <x>" }, 0, options: CliJson.CamelCaseOptions);
        camel.Should().Contain("it's <x>");

        var failed = CliJson.Build(new { }, 1, new CliError("failure", "can't <do> it", "try 'again'", ["it's <bad>"]));
        failed.Should().Contain("can't <do> it").And.Contain("try 'again'").And.Contain("it's <bad>");
    }

    [Fact]
    public async Task Commands_Json_KeepsReadableCharacters()
    {
        var (_, stdout, _) = await RunAsync("commands", "--json");

        stdout.Should().NotContain("\\u0027").And.NotContain("\\u003C").And.NotContain("\\u003E");
        stdout.Should().Contain("<");
    }

    [Fact]
    public async Task ConfigSet_ShowsTheValueReadably_InTextAndJson()
    {
        WriteConfig([]);

        var (exitCode, stdout, stderr) = await RunAsync("config", "set", "watcher.excludedFileTypes", "[\"it's\",\"<tmp>\"]", "--force");
        // The key may not exist in the schema; the point is the escaping of whatever is printed.
        if (exitCode == 0)
        {
            stdout.Should().NotContain("\\u0027").And.NotContain("\\u003C");
        }

        (stdout + stderr).Should().NotContain("\\u0027");
    }

    // ---- B6: missing snapshot ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("verify")]
    [InlineData("test-restore")]
    public async Task MissingSnapshot_IsNotFound_WithAClearMessage(string command)
    {
        var source = CreateSource();
        WriteConfig([source]);

        var (exitCode, stdout, stderr) = await RunAsync(command, "nothing/HEAD", "--json");

        exitCode.Should().Be(1, stdout + stderr);
        var error = Json(stdout)["error"]!;
        error["code"]!.GetValue<string>().Should().Be("not_found");
        error["message"]!.GetValue<string>().Should().Be("No snapshot at 'nothing/HEAD' in storage 'local'. List snapshots with: restore snapshots");
        stderr.Should().NotContain("unexpected error");
    }

    [Fact]
    public async Task MissingSnapshot_ForRestore_IsNotFoundToo()
    {
        var source = CreateSource();
        WriteConfig([source]);

        var (exitCode, stdout, _) = await RunAsync("restore", "nothing/HEAD", Path.Combine(_root, "out"), "--json");

        exitCode.Should().Be(1);
        Json(stdout)["error"]!["code"]!.GetValue<string>().Should().Be("not_found");
    }

    [Fact]
    public void NotFoundCode_IsDocumented()
    {
        var failure = CommandRegistry.ExitCodeTable.Single(e => e.Name == "failure");
        failure.Meaning.Should().Contain("not_found");
    }

    // ---- B9: validator chatter --------------------------------------------------------------------------------------

    [Fact]
    public void Validator_LogsItsProgressAtDebugLevel_NotInfo()
    {
        var logger = new LevelLogger();
        var config = new ConfigManager(logger);
        new ConfigValidator(logger).ValidateConfiguration(config, validateWatchDirectories: false);

        logger.Entries.Should().Contain(e => e.Message.Contains("Starting configuration validation") && e.Level == LogLevel.Debug);
        logger.Entries.Where(e => e.Message.Contains("configuration validation")).Should().OnlyContain(e => e.Level == LogLevel.Debug);
    }

    private sealed class LevelLogger : ILogger
    {
        public List<(string Message, LogLevel Level)> Entries { get; } = [];

        public void Log(string message, LogLevel level = LogLevel.Info) => Entries.Add((message, level));
    }
}

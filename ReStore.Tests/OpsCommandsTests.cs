using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.sharing;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// The operations commands (retention apply, pause/resume, activity, usage, upgrade-format, schedule, diagnostics, share, logs)
/// and the backup guard, run in-process against a temp AppPaths root and a local storage. Nothing here touches the real profile,
/// the network or Task Scheduler (schtasks is a fake <see cref="ProcessInvoker"/>).
/// </summary>
[Collection("GlobalState")]
public class OpsCommandsTests : IDisposable
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreOpsCommands_" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _previousVariables = [];

    private static readonly string[] ManagedVariables =
    [
        "RESTORE_ENCRYPTION_PASSWORD", "RESTORE_ENCRYPTION_PASSWORD_FILE", "RESTORE_NEW_ENCRYPTION_PASSWORD", "RESTORE_NON_INTERACTIVE", "CI"
    ];

    public OpsCommandsTests()
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
        OpsEnvironment.Reset();
        AppPaths.OverrideRoot(_previousRoot);
        foreach (var (name, value) in _previousVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try { Directory.Delete(_root, true); } catch { }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

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

    private string StorageDirectory => Path.Combine(_root, "storage");

    private void WriteConfig(string[] watchDirectories, Action<JsonObject>? customize = null)
    {
        ConfigInitializer.EnsureConfigurationSetup();
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        root["watchDirectories"] = new JsonArray([.. watchDirectories.Select(d => (JsonNode)new JsonObject { ["path"] = d, ["storageType"] = null })]);
        root["globalStorageType"] = "local";
        root["storageSources"] = new JsonObject { ["local"] = new JsonObject { ["path"] = StorageDirectory } };
        root["excludedPaths"] = new JsonArray();
        root["retention"] = new JsonObject { ["enabled"] = false };
        customize?.Invoke(root);
        File.WriteAllText(AppPaths.ConfigFile, root.ToJsonString());
    }

    private void EditConfig(Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        edit(root);
        File.WriteAllText(AppPaths.ConfigFile, root.ToJsonString());
    }

    private string CreateSource(string name = "src")
    {
        var source = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "doc.txt"), "document " + new string('d', 2000));
        File.WriteAllText(Path.Combine(source, "sub", "note.txt"), "note " + new string('n', 2000));
        return source;
    }

    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    /// <summary>Makes <paramref name="count"/> snapshots of the source, changing a file between them.</summary>
    private int _revision;

    private async Task BackupRepeatedlyAsync(string source, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var revision = ++_revision;
            File.WriteAllText(Path.Combine(source, "doc.txt"), $"version {revision} " + new string('x', 500 + revision));
            var (exit, _, error) = await RunAsync("backup", source, "--non-interactive");
            exit.Should().Be(0, error);
            await Task.Delay(30);
        }
    }

    private int ManifestCount() =>
        Directory.Exists(StorageDirectory) ? Directory.GetFiles(StorageDirectory, "*.manifest.json", SearchOption.AllDirectories).Length : 0;

    private void EnableRetention(int keepLast) => EditConfig(root => root["retention"] = new JsonObject
    {
        ["enabled"] = true,
        ["keepLast"] = keepLast,
        ["keepWithinDays"] = 0
    });

    // ---- retention apply -------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetentionApply_DeletesExactlyWhatTheDryRunListed()
    {
        var source = CreateSource();
        WriteConfig([source]);
        await BackupRepeatedlyAsync(source, 3);
        ManifestCount().Should().Be(3);
        EnableRetention(1);

        var (planExit, planOut, _) = await RunAsync("retention", "--dry-run", "--json");
        planExit.Should().Be(0);
        var planned = Json(planOut)["groups"]!.AsArray().SelectMany(g => g!["snapshots"]!.AsArray())
            .Where(s => s!["action"]!.GetValue<string>() == "delete").Select(s => s!["path"]!.GetValue<string>()).ToList();
        planned.Should().HaveCount(2);

        var (exit, stdout, stderr) = await RunAsync("retention", "apply", "--yes", "--json");

        exit.Should().Be(0, stderr);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["applied"]!.GetValue<bool>().Should().BeTrue();
        doc["planned"]!.GetValue<int>().Should().Be(2);
        doc["deleted"]!.GetValue<int>().Should().Be(2);
        doc["failed"]!.GetValue<int>().Should().Be(0);
        var applied = doc["groups"]!.AsArray().SelectMany(g => g!["snapshots"]!.AsArray()).ToList();
        applied.Select(s => s!["path"]!.GetValue<string>()).Should().BeEquivalentTo(planned);
        applied.Should().OnlyContain(s => s!["deleted"]!.GetValue<bool>());
        ManifestCount().Should().Be(1, "the manifests the plan named are gone, the kept one is not");

        var (afterExit, afterOut, _) = await RunAsync("retention", "plan", "--json");
        afterExit.Should().Be(0);
        Json(afterOut)["groups"]!.AsArray().SelectMany(g => g!["snapshots"]!.AsArray())
            .Should().ContainSingle().Which!["action"]!.GetValue<string>().Should().Be("keep");
    }

    [Fact]
    public async Task RetentionApply_NonInteractiveWithoutYes_ExitsFourAndDeletesNothing()
    {
        var source = CreateSource();
        WriteConfig([source]);
        await BackupRepeatedlyAsync(source, 3);
        EnableRetention(1);

        var (exit, stdout, _) = await RunAsync("retention", "apply", "--non-interactive", "--json");

        exit.Should().Be(4);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["error"]!["code"]!.GetValue<string>().Should().Be("needs_user");
        doc["error"]!["hint"]!.GetValue<string>().Should().Contain("--yes");
        ManifestCount().Should().Be(3);
    }

    [Fact]
    public async Task RetentionApply_WithNothingToDelete_NeedsNoConfirmation()
    {
        var source = CreateSource();
        WriteConfig([source]);
        await BackupRepeatedlyAsync(source, 2);
        EnableRetention(5);

        var (exit, stdout, _) = await RunAsync("retention", "apply", "--non-interactive", "--json");

        exit.Should().Be(0);
        var doc = Json(stdout);
        doc["applied"]!.GetValue<bool>().Should().BeFalse();
        doc["planned"]!.GetValue<int>().Should().Be(0);
        ManifestCount().Should().Be(2);
    }

    [Fact]
    public async Task RetentionApply_WhenRetentionIsDisabled_FailsAndExplains()
    {
        var source = CreateSource();
        WriteConfig([source]);
        await BackupRepeatedlyAsync(source, 2);

        var (exit, stdout, _) = await RunAsync("retention", "apply", "--yes", "--json");

        exit.Should().Be(1);
        Json(stdout)["error"]!["message"]!.GetValue<string>().Should().Contain("disabled");
        ManifestCount().Should().Be(2);
    }

    [Fact]
    public async Task RetentionApply_StorageFilter_OnlyTouchesThatStorage()
    {
        var source = CreateSource();
        WriteConfig([source], root =>
        {
            var sources = root["storageSources"]!.AsObject();
            sources["other"] = new JsonObject { ["path"] = Path.Combine(_root, "other-storage") };
        });
        await BackupRepeatedlyAsync(source, 3);
        EnableRetention(1);

        var (exit, stdout, _) = await RunAsync("retention", "apply", "--storage", "other", "--yes", "--json");

        exit.Should().Be(0);
        Json(stdout)["planned"]!.GetValue<int>().Should().Be(0, "no snapshot lives in the 'other' storage");
        ManifestCount().Should().Be(3);

        var (unknownExit, _, _) = await RunAsync("retention", "apply", "--storage", "nope", "--yes", "--json");
        unknownExit.Should().Be(2);
    }

    [Fact]
    public async Task Retention_DryRunStillWorks_AndAWordlessCallIsAUsageError()
    {
        WriteConfig([]);

        var (previewExit, _, _) = await RunAsync("retention", "--dry-run", "--json");
        var (bareExit, _, bareError) = await RunAsync("retention");
        var (badExit, _, _) = await RunAsync("retention", "purge");

        previewExit.Should().Be(0);
        bareExit.Should().Be(2);
        bareError.Should().Contain("apply");
        badExit.Should().Be(2);
    }

    [Fact]
    public void Confirm_AcceptsOnlyYes()
    {
        OpsCommandHelpers.Confirm("Go?", new StringReader("y\n"), TextWriter.Null).Should().BeTrue();
        OpsCommandHelpers.Confirm("Go?", new StringReader("YES\n"), TextWriter.Null).Should().BeTrue();
        OpsCommandHelpers.Confirm("Go?", new StringReader("n\n"), TextWriter.Null).Should().BeFalse();
        OpsCommandHelpers.Confirm("Go?", new StringReader("\n"), TextWriter.Null).Should().BeFalse();
        OpsCommandHelpers.Confirm("Go?", new StringReader(""), TextWriter.Null).Should().BeFalse();

        var prompt = new StringWriter();
        OpsCommandHelpers.Confirm("Delete 3?", new StringReader("n"), prompt);
        prompt.ToString().Should().Contain("Delete 3?").And.Contain("[y/N]");
    }

    // ---- pause / resume --------------------------------------------------------------------------------------------

    [Fact]
    public async Task PauseResume_RoundTrip_WritesAndRemovesTheSharedPauseFile()
    {
        var file = Path.Combine(AppPaths.StateDirectory, "protection_pause.json");

        var (exit, stdout, _) = await RunAsync("pause", "--for", "2h", "--json");

        exit.Should().Be(0);
        var doc = Json(stdout);
        doc["paused"]!.GetValue<bool>().Should().BeTrue();
        doc["indefinite"]!.GetValue<bool>().Should().BeFalse();
        doc["untilUtc"]!.GetValue<DateTime>().Should().BeCloseTo(DateTime.UtcNow.AddHours(2), TimeSpan.FromMinutes(1));
        File.Exists(file).Should().BeTrue();
        new ProtectionPauseStore().LoadActive(DateTime.UtcNow).Should().NotBeNull("the app, --service and run-due read this very file");

        var (_, statusOut, _) = await RunAsync("pause", "status", "--json");
        Json(statusOut)["paused"]!.GetValue<bool>().Should().BeTrue();

        var (resumeExit, resumeOut, _) = await RunAsync("resume", "--json");

        resumeExit.Should().Be(0);
        Json(resumeOut)["wasPaused"]!.GetValue<bool>().Should().BeTrue();
        File.Exists(file).Should().BeFalse();
        var (_, afterOut, _) = await RunAsync("pause", "status", "--json");
        Json(afterOut)["paused"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Pause_DefaultAndIndefinitely_PauseUntilResumed()
    {
        var (exit, stdout, _) = await RunAsync("pause", "--json");
        var (_, indefiniteOut, _) = await RunAsync("pause", "--indefinitely", "--json");

        exit.Should().Be(0);
        var doc = Json(stdout);
        doc["indefinite"]!.GetValue<bool>().Should().BeTrue();
        doc["untilUtc"].Should().BeNull();
        Json(indefiniteOut)["indefinite"]!.GetValue<bool>().Should().BeTrue();
        new ProtectionPauseStore().Load().Should().Be(ProtectionPause.Indefinite);
    }

    [Fact]
    public async Task Pause_UntilTomorrow_UsesTheTrayRule()
    {
        var before = DateTime.UtcNow;

        var (exit, stdout, _) = await RunAsync("pause", "--until-tomorrow", "--json");

        exit.Should().Be(0);
        var until = Json(stdout)["untilUtc"]!.GetValue<DateTime>();
        until.Should().BeOnOrAfter(before.AddHours(1).AddSeconds(-1));
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(until, DateTimeKind.Utc), TimeZoneInfo.Local).TimeOfDay.Should().Be(TimeSpan.FromHours(8));
    }

    [Fact]
    public async Task Pause_BadArguments_AreUsageErrors()
    {
        (await RunAsync("pause", "--for", "soon", "--json")).ExitCode.Should().Be(2);
        (await RunAsync("pause", "--for", "0m", "--json")).ExitCode.Should().Be(2);
        (await RunAsync("pause", "--for", "1h", "--until-tomorrow", "--json")).ExitCode.Should().Be(2);
        (await RunAsync("pause", "stop", "--json")).ExitCode.Should().Be(2);
        (await RunAsync("pause", "status", "--for", "1h", "--json")).ExitCode.Should().Be(2);
        File.Exists(Path.Combine(AppPaths.StateDirectory, "protection_pause.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Resume_WhenNothingIsPaused_IsHarmless()
    {
        var (exit, stdout, _) = await RunAsync("resume", "--json");

        exit.Should().Be(0);
        Json(stdout)["wasPaused"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Pause_WorksWithoutAnyConfiguration()
    {
        File.Exists(AppPaths.ConfigFile).Should().BeFalse();

        var (exit, _, _) = await RunAsync("pause", "--for", "30m", "--json");

        exit.Should().Be(0);
        File.Exists(AppPaths.ConfigFile).Should().BeFalse("pausing must not create a configuration");
    }

    [Fact]
    public async Task RunDue_WhilePaused_DoesNothing_UntilResumed()
    {
        var source = CreateSource();
        WriteConfig([source]);
        EditConfig(root => root["backupInterval"] = "00:30:00");

        await RunAsync("pause", "--json");
        var (pausedExit, pausedOut, pausedErr) = await RunAsync("run-due", "--json");

        pausedExit.Should().Be(0, pausedErr);
        var paused = Json(pausedOut);
        paused["paused"]!.GetValue<bool>().Should().BeTrue();
        paused["backups"]!["succeeded"]!.GetValue<int>().Should().Be(0);
        ManifestCount().Should().Be(0);

        await RunAsync("resume", "--json");
        var (exit, stdout, stderr) = await RunAsync("run-due", "--json");

        exit.Should().Be(0, stderr);
        var resumed = Json(stdout);
        resumed["paused"]!.GetValue<bool>().Should().BeFalse();
        resumed["backups"]!["succeeded"]!.GetValue<int>().Should().Be(1);
        ManifestCount().Should().Be(1);
    }

    // ---- activity --------------------------------------------------------------------------------------------------

    private async Task SeedActivityAsync()
    {
        var state = new SystemState(new TestLogger());
        await state.LoadStateAsync();
        var now = DateTime.UtcNow;
        state.RecordActivity(new ActivityEntry { Type = ActivityType.Verify, Target = "snapshots/a/HEAD", StartUtc = now.AddMinutes(-30), EndUtc = now.AddMinutes(-29), Result = ActivityResult.Succeeded, Message = "ok" });
        state.RecordActivity(new ActivityEntry { Type = ActivityType.Verify, Target = "snapshots/b/HEAD", StartUtc = now.AddMinutes(-20), EndUtc = now.AddMinutes(-19), Result = ActivityResult.Failed, Message = "chunk missing" });
        state.RecordActivity(new ActivityEntry { Type = ActivityType.Restore, Target = "snapshots/a/HEAD", StartUtc = now.AddMinutes(-10), EndUtc = now.AddMinutes(-9), Result = ActivityResult.Failed, Message = "disk full" });
        state.RecordActivity(new ActivityEntry { Type = ActivityType.SystemBackup, Target = "programs", StartUtc = now.AddMinutes(-5), EndUtc = now.AddMinutes(-4), Result = ActivityResult.Succeeded, Message = "" });
        await state.SaveStateAsync();
    }

    [Fact]
    public async Task Activity_FiltersByTypeFailureAndLimit_NewestFirst()
    {
        WriteConfig([]);
        await SeedActivityAsync();

        var (allExit, allOut, _) = await RunAsync("activity", "--json");
        var all = Json(allOut);
        var (_, failedOut, _) = await RunAsync("activity", "--failed", "--json");
        var (_, verifyOut, _) = await RunAsync("activity", "--type", "verify", "--json");
        var (_, limitOut, _) = await RunAsync("activity", "--limit", "1", "--json");
        var (_, sysOut, _) = await RunAsync("activity", "--type", "system-backup", "--json");

        allExit.Should().Be(0);
        all["count"]!.GetValue<int>().Should().Be(4);
        all["items"]!.AsArray().Select(i => i!["type"]!.GetValue<string>()).Should().Equal("system-backup", "restore", "verify", "verify");
        Json(failedOut)["items"]!.AsArray().Select(i => i!["message"]!.GetValue<string>()).Should().Equal("disk full", "chunk missing");
        Json(verifyOut)["count"]!.GetValue<int>().Should().Be(2);
        Json(limitOut)["items"]!.AsArray().Should().ContainSingle().Which!["type"]!.GetValue<string>().Should().Be("system-backup");
        Json(sysOut)["items"]!.AsArray().Should().ContainSingle();
        var first = all["items"]![0]!;
        first["result"]!.GetValue<string>().Should().Be("succeeded");
        first["durationSeconds"]!.GetValue<int>().Should().Be(60);
    }

    [Fact]
    public async Task Activity_IncludesBackupRuns_AndRejectsBadArguments()
    {
        var source = CreateSource();
        WriteConfig([source]);
        await BackupRepeatedlyAsync(source, 1);

        var (_, stdout, _) = await RunAsync("activity", "--type", "backup", "--json");

        var item = Json(stdout)["items"]!.AsArray().Should().ContainSingle().Subject!;
        item["target"]!.GetValue<string>().Should().Be(Path.GetFullPath(source));
        item["storage"]!.GetValue<string>().Should().Be("local");
        (await RunAsync("activity", "--type", "gardening")).ExitCode.Should().Be(2);
        (await RunAsync("activity", "--type", "3")).ExitCode.Should().Be(2);
        (await RunAsync("activity", "--limit", "0")).ExitCode.Should().Be(2);
    }

    [Fact]
    public async Task Activity_WithNoHistory_PrintsAMessage()
    {
        WriteConfig([]);

        var (exit, stdout, _) = await RunAsync("activity");

        exit.Should().Be(0);
        stdout.Should().Contain("No activity");
    }

    // ---- usage -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Usage_ReportsTheNumbersOfASmallRepository_AndRefreshMeasuresTheStorage()
    {
        var source = CreateSource();
        WriteConfig([source]);
        await BackupRepeatedlyAsync(source, 2);

        var (exit, stdout, stderr) = await RunAsync("usage", "--json");

        exit.Should().Be(0, stderr);
        var doc = Json(stdout);
        doc["totals"]!["snapshots"]!.GetValue<int>().Should().Be(2);
        doc["totals"]!["storedBytes"]!.GetValue<long>().Should().BeGreaterThan(0);
        doc["totals"]!["latestLogicalBytes"]!.GetValue<long>().Should().BeGreaterThan(2000);
        var group = doc["groups"]!.AsArray().Should().ContainSingle().Subject!;
        group["group"]!.GetValue<string>().Should().Be(Path.GetFullPath(source));
        group["storage"]!.GetValue<string>().Should().Be("local");
        group["snapshots"]!.GetValue<int>().Should().Be(2);
        doc["measured"]!.AsArray().Should().BeEmpty();

        var (refreshExit, refreshOut, _) = await RunAsync("usage", "--refresh", "--json");

        refreshExit.Should().Be(0);
        var measured = Json(refreshOut)["measured"]!.AsArray().Should().ContainSingle().Subject!;
        measured["storage"]!.GetValue<string>().Should().Be("local");
        measured["chunkCount"]!.GetValue<long>().Should().BeGreaterThan(0);
        var onDisk = Directory.GetFiles(StorageDirectory, "*.chunk", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        measured["bytes"]!.GetValue<long>().Should().Be(onDisk, "the refresh lists the real chunk objects");
    }

    [Fact]
    public async Task Usage_StorageFilter_AndEmptyRepository()
    {
        WriteConfig([]);

        var (exit, stdout, _) = await RunAsync("usage");
        var (_, jsonOut, _) = await RunAsync("usage", "--storage", "local", "--json");
        var (unknownExit, _, _) = await RunAsync("usage", "--storage", "nope", "--json");

        exit.Should().Be(0);
        stdout.Should().Contain("No backups yet");
        Json(jsonOut)["totals"]!["snapshots"]!.GetValue<int>().Should().Be(0);
        unknownExit.Should().Be(2);
    }

    // ---- upgrade-format --------------------------------------------------------------------------------------------

    private int LatestManifestVersion()
    {
        var manifest = Directory.GetFiles(StorageDirectory, "*.manifest.json", SearchOption.AllDirectories)
            .OrderBy(File.GetLastWriteTimeUtc).Last();
        return ManifestIo.Read(manifest).Version;
    }

    [Fact]
    public async Task UpgradeFormat_SwitchesTheNextBackupToV3_AndIsIdempotent()
    {
        var source = CreateSource();
        WriteConfig([source], root => root["chunkDiffing"] = new JsonObject { ["manifestVersion"] = 2 });
        await BackupRepeatedlyAsync(source, 1);
        LatestManifestVersion().Should().Be(2);

        var (exit, stdout, stderr) = await RunAsync("upgrade-format", "--json");

        exit.Should().Be(0, stderr);
        var doc = Json(stdout);
        doc["changed"]!.GetValue<bool>().Should().BeTrue();
        doc["manifestVersion"]!.GetValue<int>().Should().Be(3);
        doc["explanation"]!.GetValue<string>().Should().Contain("newest format");
        JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!["chunkDiffing"]!["manifestVersion"]!.GetValue<int>().Should().Be(3);

        await BackupRepeatedlyAsync(source, 1);
        LatestManifestVersion().Should().Be(3, "the next backup writes the new format");

        var (againExit, againOut, _) = await RunAsync("upgrade-format", "--json");
        againExit.Should().Be(0);
        var again = Json(againOut);
        again["changed"]!.GetValue<bool>().Should().BeFalse();
        again["alreadyUpgraded"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task UpgradeFormat_ChecksAnOptionalGroup()
    {
        var source = CreateSource();
        WriteConfig([source], root => root["chunkDiffing"] = new JsonObject { ["manifestVersion"] = 2 });
        await BackupRepeatedlyAsync(source, 1);

        var (missingExit, _, _) = await RunAsync("upgrade-format", Path.Combine(_root, "elsewhere"), "--json");
        (JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!["chunkDiffing"]!["manifestVersion"]!.GetValue<int>()).Should().Be(2);
        var (exit, stdout, _) = await RunAsync("upgrade-format", source, "--json");

        missingExit.Should().Be(1);
        exit.Should().Be(0);
        Json(stdout)["group"]!.GetValue<string>().Should().Be(Path.GetFullPath(source));
    }

    // ---- diagnostics / logs ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Diagnostics_IsRedacted_AndStructuredInJson()
    {
        WriteConfig([], root => root["storageSources"] = new JsonObject
        {
            ["s3"] = new JsonObject { ["options"] = new JsonObject { ["bucketName"] = "b", ["secretAccessKey"] = "TOP-SECRET-VALUE" } }
        });
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        File.WriteAllLines(AppPaths.LogFile, ["start", "connecting with password=hunter2 now"]);

        var (exit, stdout, _) = await RunAsync("diagnostics", "--json");
        var (textExit, text, _) = await RunAsync("diagnostics");

        exit.Should().Be(0);
        stdout.Should().NotContain("TOP-SECRET-VALUE").And.NotContain("hunter2");
        var doc = Json(stdout);
        doc["configProblem"]?.ToString().Should().BeNull();
        doc["config"]!["storageSources"].Should().NotBeNull();
        doc["logLines"]!.AsArray().Select(l => l!.GetValue<string>()).Should().Contain("start");
        doc["report"]!.GetValue<string>().Should().StartWith("ReStore diagnostics");
        doc["version"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        textExit.Should().Be(0);
        text.Should().StartWith("ReStore diagnostics").And.NotContain("TOP-SECRET-VALUE").And.NotContain("hunter2");
    }

    [Fact]
    public async Task Diagnostics_WorksWithABrokenConfig()
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        File.WriteAllText(AppPaths.ConfigFile, "{ not json");

        var (exit, stdout, _) = await RunAsync("diagnostics", "--json");

        exit.Should().Be(0);
        var doc = Json(stdout);
        doc["config"].Should().BeNull();
        doc["configProblem"]!.GetValue<string>().Should().Contain("not valid JSON");
    }

    [Fact]
    public async Task Logs_ShowsThePath_AndTheLastLinesMasked()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        File.WriteAllLines(AppPaths.LogFile, Enumerable.Range(1, 10).Select(i => $"line {i}").Append("token=abc123secret"));

        var (pathExit, pathOut, _) = await RunAsync("logs", "--path");
        var (jsonExit, jsonOut, _) = await RunAsync("logs", "--tail", "3", "--json");
        var (_, pathJson, _) = await RunAsync("logs", "--path", "--json");
        var (textExit, text, _) = await RunAsync("logs", "--tail", "2");

        pathExit.Should().Be(0);
        pathOut.Trim().Should().Be(AppPaths.LogFile);
        jsonExit.Should().Be(0);
        var doc = Json(jsonOut);
        doc["path"]!.GetValue<string>().Should().Be(AppPaths.LogFile);
        doc["exists"]!.GetValue<bool>().Should().BeTrue();
        doc["lines"]!.AsArray().Select(l => l!.GetValue<string>()).Should().Equal("line 9", "line 10", "token=***");
        Json(pathJson)["lines"].Should().BeNull();
        textExit.Should().Be(0);
        text.Should().Contain("line 10").And.NotContain("abc123secret").And.NotContain("line 9");
        (await RunAsync("logs", "--tail", "0")).ExitCode.Should().Be(2);
    }

    [Fact]
    public async Task Logs_WithoutALogFile_SucceedsWithNoLines()
    {
        var (exit, stdout, _) = await RunAsync("logs", "--json");

        exit.Should().Be(0);
        var doc = Json(stdout);
        doc["exists"]!.GetValue<bool>().Should().BeFalse();
        doc["lines"]!.AsArray().Should().BeEmpty();
    }

    // ---- share -----------------------------------------------------------------------------------------------------

    private string CreateFileToShare()
    {
        var file = Path.Combine(_root, "report.txt");
        File.WriteAllText(file, "hello");
        return file;
    }

    [Fact]
    public async Task Share_UploadsAndPrintsTheLink_WithTheRequestedExpiry()
    {
        var file = CreateFileToShare();
        WriteConfig([], root =>
        {
            var sources = root["storageSources"]!.AsObject();
            sources["s3"] = new JsonObject { ["options"] = new JsonObject { ["bucketName"] = "b" } };
        });
        TimeSpan? requestedExpiry = null;
        var storage = new Mock<IStorage>();
        storage.SetupGet(s => s.SupportsSharing).Returns(true);
        storage.Setup(s => s.UploadAsync(file, It.Is<string>(p => p.StartsWith("shared/") && p.EndsWith("/report.txt")), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        storage.Setup(s => s.GenerateShareLinkAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Callback<string, TimeSpan>((_, expiry) => requestedExpiry = expiry)
            .ReturnsAsync("https://example.test/share/abc");
        var configMock = new Mock<IConfigManager>();
        string? requestedStorage = null;
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Callback<string>(s => requestedStorage = s).ReturnsAsync(storage.Object);
        OpsEnvironment.CreateShareService = (_, logger) => new ShareService(configMock.Object, logger);

        var (exit, stdout, stderr) = await RunAsync("share", file, "--expires", "2d", "--json");
        var (textExit, text, textError) = await RunAsync("share", file);

        exit.Should().Be(0, stderr);
        var doc = Json(stdout);
        doc["link"]!.GetValue<string>().Should().Be("https://example.test/share/abc");
        doc["storage"]!.GetValue<string>().Should().Be("s3", "global local cannot share, so the first sharing-capable storage is used");
        doc["expires"]!.GetValue<string>().Should().Be("2d");
        requestedStorage.Should().Be("s3");
        textExit.Should().Be(0);
        text.Trim().Should().Be("https://example.test/share/abc");
        textError.Should().Contain("7d");
        requestedExpiry.Should().Be(TimeSpan.FromDays(7), "the last call used the GUI default");
    }

    [Fact]
    public async Task Share_Validation_UsesGuiRules()
    {
        var file = CreateFileToShare();
        WriteConfig([]);

        var (noSharingExit, noSharingOut, _) = await RunAsync("share", file, "--json");
        var (localExit, localOut, _) = await RunAsync("share", file, "--storage", "local", "--json");
        var (missingExit, _, _) = await RunAsync("share", Path.Combine(_root, "nope.txt"), "--json");
        var (badExpiry, _, _) = await RunAsync("share", file, "--expires", "forever", "--json");

        noSharingExit.Should().Be(1);
        Json(noSharingOut)["error"]!["message"]!.GetValue<string>().Should().Contain("No configured storage supports share links");
        localExit.Should().Be(2);
        Json(localOut)["error"]!["message"]!.GetValue<string>().Should().Contain("s3, azure, gcp, dropbox, b2");
        missingExit.Should().Be(1);
        badExpiry.Should().Be(2);
    }

    // ---- backup guard ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Backup_InsideAnExcludedPath_FailsWithExitThree_NamingTheRule()
    {
        var excluded = Path.Combine(_root, "excluded");
        var source = Path.Combine(excluded, "project");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "content");
        WriteConfig([], root => root["excludedPaths"] = new JsonArray(JsonValue.Create(excluded)));

        var (exit, stdout, stderr) = await RunAsync("backup", source, "--json", "--non-interactive");

        exit.Should().Be(3);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["error"]!["code"]!.GetValue<string>().Should().Be("config");
        doc["error"]!["message"]!.GetValue<string>().Should().Contain("excludedPaths entry").And.Contain(excluded);
        doc["error"]!["hint"]!.GetValue<string>().Should().Contain("--allow-excluded").And.Contain("config.json");
        stderr.Should().Contain("excludedPaths");
        ManifestCount().Should().Be(0);
    }

    [Fact]
    public async Task Backup_AllowExcluded_BacksTheFolderUpAnyway_WithAWarning()
    {
        var excluded = Path.Combine(_root, "excluded");
        var source = Path.Combine(excluded, "project");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "content " + new string('c', 500));
        WriteConfig([], root => root["excludedPaths"] = new JsonArray(JsonValue.Create(excluded)));

        var (exit, stdout, stderr) = await RunAsync("backup", source, "--allow-excluded", "--json", "--non-interactive");

        exit.Should().Be(0, stderr);
        var doc = Json(stdout);
        doc["snapshotId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        doc["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().ContainSingle(w => w.Contains("excludedPaths entry"));
        ManifestCount().Should().Be(1);
    }

    [Fact]
    public async Task Backup_MatchedByAnExcludeGlob_IsRefusedToo()
    {
        var source = Path.Combine(_root, "node_modules");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "content");
        WriteConfig([], root => root["excludeGlobs"] = new JsonArray(JsonValue.Create("node_modules/")));

        var (exit, stdout, _) = await RunAsync("backup", source, "--json", "--non-interactive");

        exit.Should().Be(3);
        Json(stdout)["error"]!["message"]!.GetValue<string>().Should().Contain("excludeGlobs pattern 'node_modules/'");
    }

    [Fact]
    public async Task Backup_OfAnEmptyFolder_WarnsThatNoFilesWereFound()
    {
        var source = Path.Combine(_root, "empty");
        Directory.CreateDirectory(source);
        WriteConfig([]);

        var (exit, stdout, _) = await RunAsync("backup", source, "--json", "--non-interactive");

        exit.Should().Be(0);
        Json(stdout)["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().ContainSingle(w => w.Contains("No files were found"));
    }

    [Fact]
    public async Task Backup_OfAnOrdinaryFolder_HasNoWarnings()
    {
        var source = CreateSource();
        WriteConfig([]);

        var (exit, stdout, _) = await RunAsync("backup", source, "--json", "--non-interactive");

        exit.Should().Be(0);
        Json(stdout)["warnings"]!.AsArray().Should().BeEmpty();
    }

    // ---- schedule (fake schtasks) ----------------------------------------------------------------------------------

    private sealed record Call(string File, string[] Arguments, string? XmlAtCallTime);

    private List<Call> UseFakeSchtasks(Func<IReadOnlyList<string>, ProcessResult>? respond = null)
    {
        var calls = new List<Call>();
        OpsEnvironment.IsWindows = () => true;
        OpsEnvironment.TaskExecutable = () => @"C:\Tools\ReStore\restore.exe";
        OpsEnvironment.Runner = (file, arguments, _, _) =>
        {
            var xmlIndex = arguments.ToList().IndexOf("/XML");
            string? xml = null;
            if (xmlIndex >= 0 && arguments[0] == "/Create")
            {
                xml = File.ReadAllText(arguments[xmlIndex + 1]);
            }

            calls.Add(new Call(file, [.. arguments], xml));
            return Task.FromResult(respond?.Invoke(arguments) ?? new ProcessResult(0, "", "", false));
        };
        return calls;
    }

    [Fact]
    public async Task ScheduleInstall_CreatesTheTaskThroughSchtasks_WithTheIntervalAndExecutable()
    {
        WriteConfig([]);
        var calls = UseFakeSchtasks();

        var (exit, stdout, stderr) = await RunAsync("schedule", "install", "--every", "30", "--json");

        exit.Should().Be(0, stderr);
        var doc = Json(stdout);
        doc["installed"]!.GetValue<bool>().Should().BeTrue();
        doc["intervalMinutes"]!.GetValue<int>().Should().Be(30);
        doc["executable"]!.GetValue<string>().Should().Be(@"C:\Tools\ReStore\restore.exe");
        doc["arguments"]!.GetValue<string>().Should().Be("run-due --quiet");
        var call = calls.Should().ContainSingle().Subject;
        call.File.Should().Be("schtasks.exe");
        call.Arguments[..4].Should().Equal("/Create", "/TN", @"\ReStore\run-due", "/XML");
        call.Arguments[^1].Should().Be("/F");
        call.XmlAtCallTime.Should().Contain("<Interval>PT30M</Interval>").And.Contain("restore.exe");
        File.Exists(call.Arguments[4]).Should().BeFalse("the temporary definition is removed");
    }

    [Fact]
    public async Task ScheduleInstall_DefaultsToFifteenMinutes_AndValidatesTheInterval()
    {
        WriteConfig([]);
        var calls = UseFakeSchtasks();

        var (exit, stdout, _) = await RunAsync("schedule", "install", "--json");

        exit.Should().Be(0);
        Json(stdout)["intervalMinutes"]!.GetValue<int>().Should().Be(15);
        calls.Single().XmlAtCallTime.Should().Contain("PT15M");
        (await RunAsync("schedule", "install", "--every", "0", "--json")).ExitCode.Should().Be(2);
        (await RunAsync("schedule", "install", "--every", "1441", "--json")).ExitCode.Should().Be(2);
        (await RunAsync("schedule", "install", "--every", "often", "--json")).ExitCode.Should().Be(2);
        calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task ScheduleInstall_SchtasksFailure_ExitsOneWithItsMessage()
    {
        WriteConfig([]);
        UseFakeSchtasks(_ => new ProcessResult(1, "", "ERROR: Access is denied.", false));

        var (exit, stdout, _) = await RunAsync("schedule", "install", "--json");

        exit.Should().Be(1);
        Json(stdout)["error"]!["message"]!.GetValue<string>().Should().Contain("Access is denied");
    }

    [Fact]
    public async Task ScheduleInstall_WarnsWhenEncryptionHasNoUnattendedPassword()
    {
        var source = CreateSource();
        var salt = EncryptionService.GenerateSalt();
        var token = new EncryptionService(new TestLogger()).CreatePasswordVerificationToken("pw", salt, 1000);
        WriteConfig([source], root => root["encryption"] = new JsonObject
        {
            ["enabled"] = true, ["salt"] = Convert.ToBase64String(salt), ["keyDerivationIterations"] = 1000, ["verificationToken"] = token
        });
        UseFakeSchtasks();

        var (exit, stdout, _) = await RunAsync("schedule", "install", "--json");
        var warnings = Json(stdout)["warnings"]!.AsArray();

        exit.Should().Be(0, "a missing password is a warning, not a failure");
        warnings.Should().ContainSingle().Which!.GetValue<string>().Should().Contain("RESTORE_ENCRYPTION_PASSWORD");

        Directory.CreateDirectory(AppPaths.StateDirectory);
        File.WriteAllBytes(Path.Combine(AppPaths.StateDirectory, "remembered-password.bin"), [1, 2, 3]);
        var (_, withRememberedOut, _) = await RunAsync("schedule", "install", "--json");
        Json(withRememberedOut)["warnings"]!.AsArray().Should().BeEmpty("a remembered password file is a password source");
    }

    [Fact]
    public async Task ScheduleRemove_DeletesAnInstalledTask_AndIgnoresAMissingOne()
    {
        WriteConfig([]);
        var installed = true;
        var calls = UseFakeSchtasks(arguments => arguments[0] switch
        {
            "/Query" => installed ? new ProcessResult(0, "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"/>", "", false) : new ProcessResult(1, "", "ERROR: The system cannot find the file specified.", false),
            "/Delete" => Delete(),
            _ => new ProcessResult(0, "", "", false)
        });
        ProcessResult Delete()
        {
            installed = false;
            return new ProcessResult(0, "SUCCESS", "", false);
        }

        var (exit, stdout, _) = await RunAsync("schedule", "remove", "--json");
        var (secondExit, secondOut, _) = await RunAsync("schedule", "remove", "--json");

        exit.Should().Be(0);
        Json(stdout)["removed"]!.GetValue<bool>().Should().BeTrue();
        calls.Select(c => c.Arguments[0]).Should().Contain("/Delete");
        calls.First(c => c.Arguments[0] == "/Delete").Arguments.Should().Equal("/Delete", "/TN", @"\ReStore\run-due", "/F");
        secondExit.Should().Be(0);
        Json(secondOut)["removed"]!.GetValue<bool>().Should().BeFalse();
        calls.Count(c => c.Arguments[0] == "/Delete").Should().Be(1);
    }

    [Fact]
    public async Task ScheduleStatus_ReportsIntervalExecutableAndLastRun()
    {
        WriteConfig([]);
        const string DefinitionXml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><TimeTrigger><Repetition><Interval>PT15M</Interval></Repetition></TimeTrigger></Triggers>
              <Settings><Enabled>true</Enabled><Hidden>true</Hidden></Settings>
              <Actions Context="Author"><Exec><Command>"C:\Program Files\ReStore\restore.exe"</Command><Arguments>run-due --quiet</Arguments></Exec></Actions>
            </Task>
            """;
        var verbose = "\"HostName\",\"TaskName\",\"Next Run Time\",\"Status\",\"Logon Mode\",\"Last Run Time\",\"Last Result\",\"Author\",\"Task To Run\"\r\n"
            + "\"PC\",\"\\ReStore\\run-due\",\"1/2/2030 10:15:00 AM\",\"Ready\",\"Interactive only\",\"1/2/2030 10:00:00 AM\",\"0\",\"me\",\"\"\"C:\\x\\restore.exe\"\" run-due --quiet\"\r\n";
        UseFakeSchtasks(arguments => arguments.Contains("/XML") ? new ProcessResult(0, DefinitionXml, "", false) : new ProcessResult(0, verbose, "", false));

        var (exit, stdout, stderr) = await RunAsync("schedule", "status", "--json");

        exit.Should().Be(0, stderr);
        var doc = Json(stdout);
        doc["installed"]!.GetValue<bool>().Should().BeTrue();
        doc["enabled"]!.GetValue<bool>().Should().BeTrue();
        doc["intervalMinutes"]!.GetValue<int>().Should().Be(15);
        doc["executable"]!.GetValue<string>().Should().Be(@"C:\Program Files\ReStore\restore.exe");
        doc["arguments"]!.GetValue<string>().Should().Be("run-due --quiet");
        doc["state"]!.GetValue<string>().Should().Be("Ready");
        doc["lastResult"]!.GetValue<int>().Should().Be(0);
        doc["lastResultText"]!.GetValue<string>().Should().Be("success");
        doc["lastRun"]!.GetValue<string>().Should().Be("1/2/2030 10:00:00 AM");
    }

    [Fact]
    public async Task ScheduleInstall_RunsThroughConhostHeadless_WhenItExists()
    {
        WriteConfig([]);
        var calls = UseFakeSchtasks();
        OpsEnvironment.ConhostAvailable = () => true;

        var (exit, stdout, stderr) = await RunAsync("schedule", "install", "--json");

        exit.Should().Be(0, stderr);
        var doc = Json(stdout);
        doc["hidden"]!.GetValue<bool>().Should().BeTrue();
        doc["launcher"]!.GetValue<string>().Should().Be(@"%SystemRoot%\System32\conhost.exe");
        doc["executable"]!.GetValue<string>().Should().Be(@"C:\Tools\ReStore\restore.exe");
        doc["arguments"]!.GetValue<string>().Should().Be("run-due --quiet");
        var xml = System.Xml.Linq.XDocument.Parse(calls.Single().XmlAtCallTime!);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        xml.Descendants(ns + "Command").Single().Value.Should().Be(@"%SystemRoot%\System32\conhost.exe");
        xml.Descendants(ns + "Arguments").Single().Value.Should().Be("--headless \"C:\\Tools\\ReStore\\restore.exe\" run-due --quiet");
    }

    [Fact]
    public async Task ScheduleInstall_FallsBackToTheDirectExe_WithoutConhost()
    {
        WriteConfig([]);
        var calls = UseFakeSchtasks();
        OpsEnvironment.ConhostAvailable = () => false;

        var (exit, stdout, _) = await RunAsync("schedule", "install", "--json");

        exit.Should().Be(0);
        Json(stdout)["hidden"]!.GetValue<bool>().Should().BeFalse();
        var xml = System.Xml.Linq.XDocument.Parse(calls.Single().XmlAtCallTime!);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        xml.Descendants(ns + "Command").Single().Value.Should().Be("\"C:\\Tools\\ReStore\\restore.exe\"");
        xml.Descendants(ns + "Arguments").Single().Value.Should().Be("run-due --quiet");
    }

    [Fact]
    public async Task ScheduleStatus_ReportsRunDuesOwnLastResult_NotTheAlwaysZeroOneOfTaskScheduler()
    {
        WriteConfig([]);
        const string DefinitionXml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><TimeTrigger><Repetition><Interval>PT15M</Interval></Repetition></TimeTrigger></Triggers>
              <Settings><Enabled>true</Enabled></Settings>
              <Actions Context="Author"><Exec><Command>%SystemRoot%\System32\conhost.exe</Command><Arguments>--headless "C:\Program Files\ReStore\restore.exe" run-due --quiet</Arguments></Exec></Actions>
            </Task>
            """;
        var verbose = "\"HostName\",\"TaskName\",\"Next Run Time\",\"Status\",\"Logon Mode\",\"Last Run Time\",\"Last Result\",\"Author\",\"Task To Run\"\r\n"
            + "\"PC\",\"\\ReStore\\run-due\",\"1/2/2030 10:15:00 AM\",\"Ready\",\"Interactive only\",\"1/2/2030 10:00:00 AM\",\"0\",\"me\",\"x\"\r\n";
        UseFakeSchtasks(arguments => arguments.Contains("/XML") ? new ProcessResult(0, DefinitionXml, "", false) : new ProcessResult(0, verbose, "", false));
        new RunDueRecord
        {
            StartedUtc = new DateTime(2030, 1, 2, 9, 0, 0, DateTimeKind.Utc),
            FinishedUtc = new DateTime(2030, 1, 2, 9, 1, 0, DateTimeKind.Utc),
            ExitCode = 1,
            ErrorCode = "failure",
            BackupsSucceeded = 1,
            BackupsFailed = 2
        }.TrySave();

        var (exit, stdout, stderr) = await RunAsync("schedule", "status", "--json");
        var (_, text, _) = await RunAsync("schedule", "status");

        exit.Should().Be(0, stderr);
        var doc = Json(stdout);
        doc["executable"]!.GetValue<string>().Should().Be(@"C:\Program Files\ReStore\restore.exe", "the conhost wrapper is looked through");
        doc["arguments"]!.GetValue<string>().Should().Be("run-due --quiet");
        doc["lastResult"]!.GetValue<int>().Should().Be(1);
        doc["lastResultSource"]!.GetValue<string>().Should().Be("run-due");
        doc["lastResultText"]!.GetValue<string>().Should().Contain("failed").And.Contain("2 failed");
        doc["lastRun"]!.GetValue<string>().Should().Be("1/2/2030 10:00:00 AM", "Task Scheduler's own time is kept");
        var last = doc["lastRunResult"]!;
        last["ok"]!.GetValue<bool>().Should().BeFalse();
        last["errorCode"]!.GetValue<string>().Should().Be("failure");
        last["backups"]!["failed"]!.GetValue<int>().Should().Be(2);
        text.Should().Contain("failed (exit code 1, failure)");
    }

    [Fact]
    public async Task ScheduleStatus_WithoutARunDueRecord_FallsBackToTaskScheduler()
    {
        WriteConfig([]);
        var verbose = "\"HostName\",\"TaskName\",\"Next Run Time\",\"Status\",\"Logon Mode\",\"Last Run Time\",\"Last Result\",\"Author\",\"Task To Run\"\r\n"
            + "\"PC\",\"\\ReStore\\run-due\",\"N/A\",\"Ready\",\"Interactive only\",\"11/30/1999 12:00:00 AM\",\"267011\",\"me\",\"x\"\r\n";
        UseFakeSchtasks(arguments => arguments.Contains("/XML")
            ? new ProcessResult(0, "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"/>", "", false)
            : new ProcessResult(0, verbose, "", false));

        var (_, stdout, _) = await RunAsync("schedule", "status", "--json");

        var doc = Json(stdout);
        doc["lastResultSource"]!.GetValue<string>().Should().Be("task-scheduler");
        doc["lastResult"]!.GetValue<int>().Should().Be(267011);
        doc["lastRunResult"].Should().BeNull();
    }

    [Fact]
    public async Task ScheduleStatus_WhenNotInstalled_SaysSo()
    {
        WriteConfig([]);
        UseFakeSchtasks(_ => new ProcessResult(1, "", "ERROR: The system cannot find the file specified.", false));

        var (exit, stdout, _) = await RunAsync("schedule", "status", "--json");
        var (_, text, _) = await RunAsync("schedule", "status");

        exit.Should().Be(0);
        Json(stdout)["installed"]!.GetValue<bool>().Should().BeFalse();
        text.Should().Contain("not installed");
    }

    [Fact]
    public async Task Schedule_OffWindows_FailsClearly_AndNeverStartsAProcess()
    {
        WriteConfig([]);
        var ran = false;
        OpsEnvironment.IsWindows = () => false;
        OpsEnvironment.Runner = (_, _, _, _) =>
        {
            ran = true;
            return Task.FromResult(new ProcessResult(0, "", "", false));
        };

        foreach (var sub in new[] { "install", "remove", "status" })
        {
            var (exit, stdout, _) = await RunAsync("schedule", sub, "--json");
            exit.Should().Be(1, sub);
            Json(stdout)["error"]!["message"]!.GetValue<string>().Should().Contain("Windows");
        }

        ran.Should().BeFalse();
    }

    [Fact]
    public async Task ScheduleInstall_FromTheDotnetHost_IsRefused()
    {
        WriteConfig([]);
        UseFakeSchtasks();
        OpsEnvironment.TaskExecutable = () => null;

        var (exit, _, _) = await RunAsync("schedule", "install", "--json");

        exit.Should().Be(1);
    }

    // ---- registry --------------------------------------------------------------------------------------------------

    [Fact]
    public void Registry_DescribesTheOperationsCommands()
    {
        var names = CommandRegistry.All.Select(s => s.Name).ToList();

        names.Should().Contain(["pause", "resume", "activity", "usage", "upgrade-format", "schedule install", "schedule remove", "schedule status", "diagnostics", "share", "logs", "retention"]);
        CommandRegistry.Find("schedule install")!.WindowsOnly.Should().BeTrue();
        CommandRegistry.Find("retention")!.Destructive.Should().BeTrue();
        CommandRegistry.Find("backup")!.Options.Select(o => o.Name).Should().Contain("--allow-excluded");
        foreach (var name in new[] { "pause", "resume", "activity", "usage", "upgrade-format", "schedule install", "schedule remove", "schedule status", "diagnostics", "share", "logs" })
        {
            CommandRegistry.Find(name)!.SupportsJson.Should().BeTrue(name);
        }
    }

    [Fact]
    public void Helpers_ResolveTheTaskExecutablePerChannel()
    {
        OpsCommandHelpers.ResolveTaskExecutable(AppChannel.Msix, @"C:\Program Files\WindowsApps\pkg\ReStore.Core.exe", @"C:\Users\me\AppData\Local")
            .Should().Be(Path.Combine(@"C:\Users\me\AppData\Local", "Microsoft", "WindowsApps", "restore.exe"));
        OpsCommandHelpers.ResolveTaskExecutable(AppChannel.Portable, @"D:\Tools\restore.exe", "x").Should().Be(@"D:\Tools\restore.exe");
        OpsCommandHelpers.ResolveTaskExecutable(AppChannel.Dev, "/usr/share/dotnet/dotnet", "x").Should().BeNull();
        OpsCommandHelpers.ResolveTaskExecutable(AppChannel.Dev, null, "x").Should().BeNull();
    }
}

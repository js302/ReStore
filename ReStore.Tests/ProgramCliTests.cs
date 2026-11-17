using FluentAssertions;
using Moq;
using ReStore.Core;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace ReStore.Tests;

/// <summary>CLI robustness: stable exit codes, strict flag parsing, config-error handling, scoped validation.</summary>
[Collection("GlobalState")]
public class ProgramCliTests : IDisposable
{
    private const string PasswordVariable = "RESTORE_ENCRYPTION_PASSWORD";

    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreCliTests_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousPassword = Environment.GetEnvironmentVariable(PasswordVariable);

    public ProgramCliTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot(_root);
        Environment.SetEnvironmentVariable(PasswordVariable, null);
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(_previousRoot);
        Environment.SetEnvironmentVariable(PasswordVariable, _previousPassword);
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MissingWatchDirectory => Path.Combine(_root, "no-such-dir");

    /// <summary>Starts from the shipped example config and points it at a temp local storage and one watch directory.</summary>
    private void WriteConfig(string watchDirectory)
    {
        ConfigInitializer.EnsureConfigurationSetup();
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = watchDirectory, ["storageType"] = null });
        root["globalStorageType"] = "local";
        root["storageSources"] = new JsonObject { ["local"] = new JsonObject { ["path"] = Path.Combine(_root, "storage") } };
        File.WriteAllText(AppPaths.ConfigFile, root.ToJsonString());
    }

    [Fact]
    public async Task Main_UnknownFlag_IsUsageError_WithDidYouMeanHint_AndDoesNotRestore()
    {
        // Config is deliberately absent: a usage error must be reported before anything is loaded or run.
        var (exitCode, stdout, stderr) = await RunMainAsync("restore", "snap/HEAD", Path.Combine(_root, "target"), "--dryrun");

        exitCode.Should().Be(2);
        stderr.Should().Contain("Unknown option '--dryrun'").And.Contain("Did you mean '--dry-run'?");
        stdout.Should().Contain("Usage:");
        Directory.Exists(Path.Combine(_root, "target")).Should().BeFalse();
        File.Exists(AppPaths.ConfigFile).Should().BeFalse("usage errors must not touch the config");
    }

    [Fact]
    public async Task Main_NoArguments_IsUsageError()
    {
        var (exitCode, stdout, _) = await RunMainAsync();

        exitCode.Should().Be(2);
        stdout.Should().Contain("Usage:");
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public async Task Main_Help_PrintsUsageAndSucceeds(string flag)
    {
        var (exitCode, stdout, _) = await RunMainAsync(flag);

        exitCode.Should().Be(0);
        stdout.Should().Contain("Exit codes:");
        File.Exists(AppPaths.ConfigFile).Should().BeFalse("help must not touch config or state");
    }

    [Fact]
    public async Task Main_UnknownCommand_SuggestsClosestCommand()
    {
        var (exitCode, _, stderr) = await RunMainAsync("bakcup", "C:/x");

        exitCode.Should().Be(2);
        stderr.Should().Contain("Did you mean 'backup'?");
    }

    [Fact]
    public async Task Main_InvalidConflictValue_IsUsageError()
    {
        var (exitCode, _, stderr) = await RunMainAsync("restore", "a", "b", "--conflict", "clobber");

        exitCode.Should().Be(2);
        stderr.Should().Contain("--conflict");
    }

    [Fact]
    public async Task Main_UnreadableConfig_ReturnsConfigError_WithoutThrowing()
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        File.WriteAllText(AppPaths.ConfigFile, "{ this is not json");

        var (exitCode, _, stderr) = await RunMainAsync("backup", _root);

        exitCode.Should().Be(3);
        stderr.Should().Contain("Could not load the configuration");
    }

    [Fact]
    public async Task Main_InvalidConfig_ForBackup_ReturnsConfigError()
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        WriteConfig(MissingWatchDirectory);
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        root["backupInterval"] = "00:00:00";
        File.WriteAllText(AppPaths.ConfigFile, root.ToJsonString());

        var (exitCode, _, _) = await RunMainAsync("backup", _root);

        exitCode.Should().Be(3, "backup validates the whole configuration except the watch directories");
    }

    [Fact]
    public async Task Main_ValidateConfig_ReturnsConfigError_WhenInvalid()
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        WriteConfig(MissingWatchDirectory);

        var (exitCode, _, _) = await RunMainAsync("--validate-config");

        exitCode.Should().Be(3);
    }

    [Fact]
    public async Task Main_Verify_IgnoresUnrelatedConfigProblems_AndDoesNotSaveUnchangedState()
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        WriteConfig(MissingWatchDirectory); // watch directory missing: fatal for backup, irrelevant to verify

        var (exitCode, so, se) = await RunMainAsync("verify", "snapshots/missing/HEAD");

        exitCode.Should().Be(1, so + se);
        File.Exists(AppPaths.SystemStateFile).Should().BeFalse("a read-only command that changed nothing must not write state");
    }

    [Fact]
    public async Task Main_Verify_WithUnconfiguredStorage_ReturnsConfigError()
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        WriteConfig(MissingWatchDirectory);

        var (exitCode, _, _) = await RunMainAsync("verify", "snapshots/missing/HEAD", "--storage", "s3");

        exitCode.Should().Be(3);
    }

    [Theory]
    [InlineData(new[] { "backup", "C:/data" }, true)]
    [InlineData(new[] { "backup", "C:/data", "--storage", "s3" }, true)]
    [InlineData(new[] { "restore", "snap/HEAD", "out", "--dry-run", "--include", "a/**", "--include", "b" }, true)]
    [InlineData(new[] { "system-backup" }, true)]
    [InlineData(new[] { "system-backup", "programs", "--storage", "local" }, true)]
    [InlineData(new[] { "system-restore", "path.zip", "programs" }, true)]
    [InlineData(new[] { "--service" }, true)]
    [InlineData(new[] { "--validate-config" }, true)]
    [InlineData(new[] { "backup", "C:/data", "--bogus" }, false)]
    [InlineData(new[] { "backup", "C:/my", "documents" }, false)]
    [InlineData(new[] { "backup", "C:/data", "--storage" }, false)]
    [InlineData(new[] { "backup", "C:/data", "--storage", "--dry-run" }, false)]
    [InlineData(new[] { "verify", "x", "--dry-run" }, false)]
    [InlineData(new[] { "system-backup", "everything" }, false)]
    [InlineData(new[] { "--service", "extra" }, false)]
    [InlineData(new[] { "--service", "--now" }, false)]
    [InlineData(new[] { "snapshots" }, true)]
    [InlineData(new[] { "snapshots", "--storage", "s3", "--group", "docs_abc", "--json" }, true)]
    [InlineData(new[] { "snapshots", "extra" }, false)]
    [InlineData(new[] { "ls", "snapshots/k/HEAD" }, true)]
    [InlineData(new[] { "ls", "snapshots/k/HEAD", "--path", "a/b", "--json", "--storage", "s3" }, true)]
    [InlineData(new[] { "ls" }, false)]
    [InlineData(new[] { "ls", "a", "b" }, false)]
    [InlineData(new[] { "ls", "a", "--pat", "x" }, false)]
    [InlineData(new[] { "ls", "a", "--path" }, false)]
    [InlineData(new[] { "versions", "C:/docs", "a/b.txt", "--json" }, true)]
    [InlineData(new[] { "versions", "C:/docs" }, false)]
    [InlineData(new[] { "versions", "C:/docs", "a.txt", "--group", "x" }, false)]
    [InlineData(new[] { "diff", "a.manifest.json", "b.manifest.json", "--storage", "s3", "--json" }, true)]
    [InlineData(new[] { "diff", "a.manifest.json" }, false)]
    [InlineData(new[] { "diff", "a", "b", "c" }, false)]
    [InlineData(new[] { "adopt", "--map", "C:\\old=D:\\new", "--map", "E:\\a=F:\\b" }, true)]
    [InlineData(new[] { "adopt", "--map", "nomapping" }, false)]
    [InlineData(new[] { "restore", "snap/HEAD", "@original", "--map", "C:\\old=D:\\new" }, true)]
    [InlineData(new[] { "repair", "--dry-run", "--grace-hours", "0.5" }, true)]
    [InlineData(new[] { "change-password", "--storage", "s3" }, true)]
    [InlineData(new[] { "recovery-key" }, true)]
    [InlineData(new[] { "export-key", "keys.json", "--storage", "s3" }, true)]
    [InlineData(new[] { "import-key", "keys.json" }, true)]
    [InlineData(new[] { "export-key" }, false)]
    [InlineData(new[] { "change-password", "newpass" }, false)]
    [InlineData(new[] { "change-password", "--password", "x" }, false)]
    [InlineData(new[] { "repair", "--grace-hours", "-1" }, false)]
    [InlineData(new[] { "repair", "--grace-hours", "soon" }, false)]
    [InlineData(new[] { "repair", "--dryrun" }, false)]
    [InlineData(new[] { "probe" }, true)]
    [InlineData(new[] { "probe", "--storage", "s3", "--json" }, true)]
    [InlineData(new[] { "probe", "extra" }, false)]
    [InlineData(new[] { "config", "export", "out.json" }, true)]
    [InlineData(new[] { "config", "export" }, false)]
    [InlineData(new[] { "config", "export", "out.json", "--merge" }, false)]
    [InlineData(new[] { "config", "import", "in.json" }, true)]
    [InlineData(new[] { "config", "import", "in.json", "--merge" }, true)]
    [InlineData(new[] { "config", "import", "in.json", "--replace" }, true)]
    [InlineData(new[] { "config", "import", "in.json", "--merge", "--replace" }, false)]
    [InlineData(new[] { "config", "import" }, false)]
    [InlineData(new[] { "config", "import", "in.json", "--redact" }, false)]
    [InlineData(new[] { "config", "show", "extra" }, false)]
    [InlineData(new[] { "config", "show", "--merge" }, false)]
    [InlineData(new[] { "config", "show" }, true)]
    [InlineData(new[] { "config", "show", "--reveal" }, true)]
    [InlineData(new[] { "config", "show", "--redact" }, true)]
    [InlineData(new[] { "config", "show", "--redact", "--reveal" }, false)]
    [InlineData(new[] { "config", "protect", "--reveal" }, false)]
    [InlineData(new[] { "config", "protect", "--redact" }, false)]
    [InlineData(new[] { "retention", "--dry-run" }, true)]
    [InlineData(new[] { "retention", "--dry-run", "--group", "C:\\data", "--json" }, true)]
    [InlineData(new[] { "retention" }, false)]
    [InlineData(new[] { "retention", "--dryrun" }, false)]
    [InlineData(new[] { "retention", "--dry-run", "extra" }, false)]
    [InlineData(new[] { "verify", "snap/HEAD", "--mode", "quick" }, true)]
    [InlineData(new[] { "verify", "snap/HEAD", "--mode", "sample", "--sample-percent", "10", "--seed", "5" }, true)]
    [InlineData(new[] { "verify", "snap/HEAD", "--mode", "full" }, true)]
    [InlineData(new[] { "verify", "snap/HEAD", "--mode", "partial" }, false)]
    [InlineData(new[] { "verify", "snap/HEAD", "--mode" }, false)]
    [InlineData(new[] { "verify", "snap/HEAD", "--mode", "sample", "--sample-percent", "0" }, false)]
    [InlineData(new[] { "verify", "snap/HEAD", "--mode", "sample", "--sample-percent", "101" }, false)]
    [InlineData(new[] { "verify", "snap/HEAD", "--mode", "sample", "--sample-percent", "five" }, false)]
    [InlineData(new[] { "verify", "snap/HEAD", "--sample-percent", "10" }, false)]
    [InlineData(new[] { "verify", "snap/HEAD", "--mode", "quick", "--seed", "1" }, false)]
    [InlineData(new[] { "test-restore", "snap/HEAD" }, true)]
    [InlineData(new[] { "test-restore", "snap/HEAD", "--files", "10", "--seed", "3", "--json", "--storage", "s3" }, true)]
    [InlineData(new[] { "test-restore" }, false)]
    [InlineData(new[] { "test-restore", "snap/HEAD", "--files", "0" }, false)]
    [InlineData(new[] { "test-restore", "snap/HEAD", "--files", "many" }, false)]
    [InlineData(new[] { "test-restore", "snap/HEAD", "--seed", "-1" }, false)]
    [InlineData(new[] { "status" }, true)]
    [InlineData(new[] { "status", "--json" }, true)]
    [InlineData(new[] { "status", "extra" }, false)]
    [InlineData(new[] { "status", "--storage", "s3" }, false)]
    [InlineData(new[] { "run-due" }, true)]
    [InlineData(new[] { "run-due", "--json" }, true)]
    [InlineData(new[] { "run-due", "now" }, false)]
    [InlineData(new[] { "run-due", "--dry-run" }, false)]
    [InlineData(new[] { "--quiet", "status" }, true)]
    [InlineData(new[] { "run-due", "--quiet", "--json" }, true)]
    [InlineData(new[] { "backup", "C:\\data", "--quiet" }, true)]
    [InlineData(new[] { "--quiet" }, false)]
    public void TryParseArguments_AcceptsOnlyKnownShapes(string[] args, bool valid)
    {
        var parameters = new object?[] { args, null, null };
        var result = (bool)typeof(Program).GetMethod("TryParseArguments", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, parameters)!;

        result.Should().Be(valid, parameters[2] as string);
    }

    [Fact]
    public async Task Main_Probe_RoundTripsLocalStorage_AndReportsJson()
    {
        var watch = Path.Combine(_root, "w");
        Directory.CreateDirectory(watch);
        WriteConfig(watch);

        var (exit, stdout, stderr) = await RunMainAsync("probe", "--json");

        exit.Should().Be(0, stdout + stderr);
        var document = JsonNode.Parse(stdout)!.AsObject();
        document["success"]!.GetValue<bool>().Should().BeTrue();
        document["steps"]!.AsArray().Should().HaveCount(6);

        var (textExit, textOut, _) = await RunMainAsync("probe");
        textExit.Should().Be(0);
        textOut.Should().Contain("passed");
    }

    [Fact]
    public async Task Main_Probe_UnusableStorage_IsAConfigError()
    {
        var watch = Path.Combine(_root, "w");
        Directory.CreateDirectory(watch);
        WriteConfig(watch);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file where the storage directory should be");
        var config = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        config["storageSources"]!["local"]!["path"] = Path.Combine(blocker, "sub");
        File.WriteAllText(AppPaths.ConfigFile, config.ToJsonString());

        var (exit, _, _) = await RunMainAsync("probe");

        exit.Should().Be(3);
    }

    [Fact]
    public async Task Main_ConfigExportAndImport_RoundTrip_AndRejectInvalidFiles()
    {
        var watch = Path.Combine(_root, "w");
        Directory.CreateDirectory(watch);
        WriteConfig(watch);
        var export = Path.Combine(_root, "export.json");

        var (exportExit, exportOut, exportErr) = await RunMainAsync("config", "export", export);
        exportExit.Should().Be(0, exportOut + exportErr);
        File.ReadAllText(export).Should().Contain("restoreSettingsExport");

        var (importExit, importOut, importErr) = await RunMainAsync("config", "import", export, "--replace");
        importExit.Should().Be(0, importOut + importErr);

        var bad = JsonNode.Parse(File.ReadAllText(export))!.AsObject();
        bad["config"]!["logging"] = new JsonObject { ["level"] = "Nonsense" };
        var badFile = Path.Combine(_root, "bad.json");
        File.WriteAllText(badFile, bad.ToJsonString());
        var before = File.ReadAllText(AppPaths.ConfigFile);

        var (badExit, badOut, _) = await RunMainAsync("config", "import", badFile);

        badExit.Should().Be(3);
        badOut.Should().Contain("unchanged");
        File.ReadAllText(AppPaths.ConfigFile).Should().Be(before);
    }

    [Fact]
    public async Task Main_RecoveryCommands_WorkFromStorageAloneAfterTheLocalStateIsLost()
    {
        // Outside the ReStore data root, which is never backed up.
        var outside = Path.Combine(Path.GetTempPath(), "ReStoreCliRecovery_" + Guid.NewGuid().ToString("N"));
        try
        {
            await RecoveryScenarioAsync(outside);
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    private async Task RecoveryScenarioAsync(string outside)
    {
        var source = Path.Combine(outside, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "doc.txt"), "hello " + new string('d', 3000));
        WriteConfig(source);

        // The shipped exclusions (%TEMP%, an unset %WINDIR%, ...) would swallow a source under the temp directory on Linux.
        var configJson = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        configJson["excludedPaths"] = new JsonArray();
        File.WriteAllText(AppPaths.ConfigFile, configJson.ToJsonString());

        var (backupExit, bo, be) = await RunMainAsync("backup", source);
        backupExit.Should().Be(0, bo + be);

        // The PC is lost: the local state goes, the storage stays.
        File.Delete(AppPaths.SystemStateFile);
        var sidecars = Path.Combine(AppPaths.StateDirectory, "snapshots");
        if (Directory.Exists(sidecars))
        {
            Directory.Delete(sidecars, true);
        }

        var (listExit, listOut, listErr) = await RunMainAsync("snapshots", "--json");
        listExit.Should().Be(0, listOut + listErr);
        var document = JsonNode.Parse(listOut)!.AsObject();
        var group = document["groups"]!.AsArray().Single()!.AsObject();
        group["originalPath"]!.GetValue<string>().Should().Be(Path.GetFullPath(source));
        group["snapshots"]!.AsArray().Should().HaveCount(1);

        var (unknownGroupExit, _, _) = await RunMainAsync("snapshots", "--group", "nope");
        unknownGroupExit.Should().Be(1);

        var remapped = Path.Combine(outside, "new-docs");
        var (adoptExit, adoptOut, adoptErr) = await RunMainAsync("adopt", "--map", $"{source}={remapped}");
        adoptExit.Should().Be(0, adoptOut + adoptErr);
        adoptOut.Should().Contain("Adopted 1 snapshot");
        var (_, again, _) = await RunMainAsync("adopt", "--map", $"{source}={remapped}");
        again.Should().Contain("Adopted 0 snapshot").And.Contain("1 were already known");

        var restored = Path.Combine(outside, "restored");
        var head = SnapshotStoragePaths.GetHeadPath(source);
        var (restoreExit, restoreOut, restoreErr) = await RunMainAsync("restore", head, "@original", "--map", $"{source}={restored}");
        restoreExit.Should().Be(0, restoreOut + restoreErr);
        File.ReadAllText(Path.Combine(restored, "doc.txt")).Should().StartWith("hello");

        var (repairExit, repairOut, repairErr) = await RunMainAsync("repair", "--dry-run");
        repairExit.Should().Be(0, repairOut + repairErr);
        repairOut.Should().Contain("Repair preview").And.Contain("Orphan chunks          : 0");
    }

    [Fact]
    public void Parse_Quiet_IsGlobal_AndKeptOutOfThePositionals()
    {
        var before = Parse("--quiet", "status");
        before.Command.Should().Be("status");
        before.HasOption("--quiet").Should().BeTrue();

        var after = Parse("restore", "snap/HEAD", "out", "--quiet", "--dry-run");
        after.Positionals.Should().Equal("snap/HEAD", "out");
        after.HasOption("--quiet").Should().BeTrue();

        Parse("status").HasOption("--quiet").Should().BeFalse();
    }

    [Fact]
    public void Parse_VerifyModeOptions_AreExposed()
    {
        var verify = Parse("verify", "snap/HEAD", "--mode", "sample", "--sample-percent", "20", "--seed", "9");

        verify.GetOption("--mode").Should().Be("sample");
        verify.GetOption("--sample-percent").Should().Be("20");
        verify.GetOption("--seed").Should().Be("9");
    }

    [Fact]
    public async Task Main_Quiet_WritesNothingToStdout_ButKeepsJsonAndErrors()
    {
        var watch = Path.Combine(_root, "w");
        Directory.CreateDirectory(watch);
        WriteConfig(watch);

        var (quietExit, quietOut, quietErr) = await RunMainAsync("status", "--quiet");
        quietExit.Should().Be(0);
        quietOut.Should().BeEmpty();
        quietErr.Should().BeEmpty();

        var (jsonExit, jsonOut, _) = await RunMainAsync("status", "--json", "--quiet");
        jsonExit.Should().Be(0);
        JsonNode.Parse(jsonOut)!.AsObject().ContainsKey("groups").Should().BeTrue();

        var (errExit, errOut, errErr) = await RunMainAsync("verify", "snapshots/none/HEAD", "--quiet");
        errExit.Should().Be(1);
        errOut.Should().BeEmpty();
        errErr.Should().Contain("[Error]");
        ReStore.Core.src.utils.Logger.QuietConsole.Should().BeFalse("--quiet is scoped to one command");
    }

    [Fact]
    public async Task Main_StatusAndRunDue_ReportProtectionAndRunTheSchedulerPath()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ReStoreCliAutomation_" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(outside, "src");
            Directory.CreateDirectory(source);
            for (var i = 0; i < 4; i++)
            {
                File.WriteAllText(Path.Combine(source, $"doc{i}.txt"), $"document {i} " + new string((char)('a' + i), 2500));
            }

            WriteConfig(source);
            var configJson = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
            configJson["excludedPaths"] = new JsonArray();
            configJson["backupInterval"] = "01:00:00";
            File.WriteAllText(AppPaths.ConfigFile, configJson.ToJsonString());

            // Never backed up: status says due now, and run-due performs the backup through the scheduler.
            var (s0Exit, s0Out, s0Err) = await RunMainAsync("status", "--json");
            s0Exit.Should().Be(0, s0Out + s0Err);
            var before = JsonNode.Parse(s0Out)!.AsObject();
            before["repository"]!["busy"]!.GetValue<bool>().Should().BeFalse();
            var beforeGroup = before["groups"]!.AsArray().Single()!.AsObject();
            beforeGroup["lastSnapshotUtc"].Should().BeNull();
            beforeGroup["overdue"]!.GetValue<bool>().Should().BeTrue();

            var (dueExit, dueOut, dueErr) = await RunMainAsync("run-due", "--json");
            dueExit.Should().Be(0, dueOut + dueErr);
            var due = JsonNode.Parse(dueOut)!.AsObject();
            due["ok"]!.GetValue<bool>().Should().BeTrue();
            due["nothingDue"]!.GetValue<bool>().Should().BeFalse();
            due["backups"]!["succeeded"]!.GetValue<int>().Should().Be(1);
            due["exitCode"]!.GetValue<int>().Should().Be(0);

            var (again, againOut, againErr) = await RunMainAsync("run-due", "--json");
            again.Should().Be(0, againOut + againErr);
            JsonNode.Parse(againOut)!["nothingDue"]!.GetValue<bool>().Should().BeTrue("the folder was just backed up");

            var (s1Exit, s1Out, _) = await RunMainAsync("status", "--json");
            s1Exit.Should().Be(0);
            var afterGroup = JsonNode.Parse(s1Out)!["groups"]!.AsArray().Single()!.AsObject();
            afterGroup["lastSnapshotUtc"].Should().NotBeNull();
            afterGroup["snapshotCount"]!.GetValue<int>().Should().Be(1);
            afterGroup["overdue"]!.GetValue<bool>().Should().BeFalse();
            afterGroup["lastRun"]!["filesAdded"]!.GetValue<int>().Should().Be(4);

            var (textExit, textOut, _) = await RunMainAsync("status");
            textExit.Should().Be(0);
            textOut.Should().Contain(source).And.Contain("Repository: idle");

            // Verify modes and test-restore through the CLI against the real snapshot.
            var head = SnapshotStoragePaths.GetHeadPath(source);
            var (quickExit, quickOut, quickErr) = await RunMainAsync("verify", head, "--mode", "quick");
            quickExit.Should().Be(0, quickOut + quickErr);
            quickOut.Should().Contain("(quick) passed");

            var (sampleExit, sampleOut, sampleErr) = await RunMainAsync("verify", head, "--mode", "sample", "--sample-percent", "50", "--seed", "4");
            sampleExit.Should().Be(0, sampleOut + sampleErr);
            sampleOut.Should().Contain("(sample) passed").And.Contain("--seed 4");

            var (fullExit, fullOut, _) = await RunMainAsync("verify", head);
            fullExit.Should().Be(0);
            fullOut.Should().Contain("(full) passed");

            var (trExit, trOut, trErr) = await RunMainAsync("test-restore", head, "--files", "3", "--seed", "2", "--json");
            trExit.Should().Be(0, trOut + trErr);
            var tr = JsonNode.Parse(trOut)!.AsObject();
            tr["passed"]!.GetValue<bool>().Should().BeTrue();
            tr["filesTested"]!.GetValue<int>().Should().Be(3);
            tr["seed"]!.GetValue<int>().Should().Be(2);

            // Break a chunk: quick notices the missing object, test-restore reports failure with exit 1.
            var chunk = Directory.GetFiles(Path.Combine(_root, "storage", "chunks"), "*", SearchOption.AllDirectories).First();
            File.Delete(chunk);
            var (badQuick, _, _) = await RunMainAsync("verify", head, "--mode", "quick");
            badQuick.Should().Be(1);
            var (badRestore, badOut, _) = await RunMainAsync("test-restore", head, "--files", "10", "--json");
            badRestore.Should().Be(1);
            JsonNode.Parse(badOut)!["passed"]!.GetValue<bool>().Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    [Fact]
    public async Task Main_ValidateConfig_AcceptsVerificationModes_AndRejectsUnknownOnes()
    {
        var watch = Path.Combine(_root, "w");
        Directory.CreateDirectory(watch);
        WriteConfig(watch);

        void SetVerification(string mode, int percent)
        {
            var config = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
            config["verification"]!["enabled"] = true;
            config["verification"]!["mode"] = mode;
            config["verification"]!["samplePercent"] = percent;
            File.WriteAllText(AppPaths.ConfigFile, config.ToJsonString());
        }

        SetVerification("sample", 20);
        (await RunMainAsync("--validate-config")).ExitCode.Should().Be(0);

        SetVerification("partial", 20);
        var (badMode, badModeOut, _) = await RunMainAsync("--validate-config");
        badMode.Should().Be(3);
        badModeOut.Should().Contain("verification.mode");

        SetVerification("quick", 0);
        var (badPercent, badPercentOut, _) = await RunMainAsync("--validate-config");
        badPercent.Should().Be(3);
        badPercentOut.Should().Contain("verification.samplePercent");
    }

    [Fact]
    public void GetServicePasswordError_ReportsMissingPassword_WhenEncryptionEnabled()
    {
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = true });

        Program.GetServicePasswordError(config.Object).Should().Contain(PasswordVariable);

        Environment.SetEnvironmentVariable(PasswordVariable, "secret");
        Program.GetServicePasswordError(config.Object).Should().BeNull();
    }

    [Fact]
    public void GetServicePasswordError_IsNull_WhenEncryptionDisabled()
    {
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });

        Program.GetServicePasswordError(config.Object).Should().BeNull();
    }

    [Fact]
    public async Task PromptForPassword_WhenNotInteractive_NeverReadsConsole()
    {
        var request = new PasswordPromptRequest(PasswordPurpose.Encrypt, false);

        (await Program.PromptForPasswordAsync(request, allowPrompt: false)).Should().BeNull();

        Environment.SetEnvironmentVariable(PasswordVariable, "from-env");
        (await Program.PromptForPasswordAsync(request, allowPrompt: false)).Should().Be("from-env");
    }

    [Fact]
    public void SystemBackupManager_HonoursStorageOverride_ForEveryComponent()
    {
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.GlobalStorageType).Returns("local");
        config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig { ProgramsStorageType = "gdrive" });
        var logger = new TestLogger();
        var state = new Mock<SystemState>(logger).Object;
        var method = typeof(SystemBackupManager).GetMethod("GetStorageTypeForComponent", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var configured = new SystemBackupManager(logger, config.Object, state);
        var overridden = new SystemBackupManager(logger, config.Object, state) { StorageTypeOverride = "s3" };

        method.Invoke(configured, ["programs"]).Should().Be("gdrive");
        foreach (var component in new[] { "programs", "environment", "settings" })
        {
            method.Invoke(overridden, [component]).Should().Be("s3");
        }
    }

    [Fact]
    public async Task Main_RetentionDryRunJson_PrintsPlanAndDeletesNothing()
    {
        WriteConfig(_root);
        var now = DateTime.UtcNow;
        var state = new SystemState(new Mock<ILogger>().Object);
        foreach (var days in new[] { 0, 1, 2 })
        {
            state.AddBackup("C:\\data", $"snapshots/data/snap{days}.manifest.json", false, "local");
        }

        var backups = state.GetBackupsForGroup("C:\\data");
        backups.Should().HaveCount(3);
        await state.SaveStateAsync();
        var stateText = File.ReadAllText(AppPaths.SystemStateFile);
        var stateRoot = JsonNode.Parse(stateText)!;
        var i = 0;
        foreach (var node in FindBackupNodes(stateRoot))
        {
            node["Timestamp"] = now.AddDays(-i++).ToString("o");
        }

        File.WriteAllText(AppPaths.SystemStateFile, stateRoot.ToJsonString());
        var config = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        config["retention"] = new JsonObject { ["enabled"] = true, ["keepLastPerDirectory"] = 1, ["maxAgeDays"] = 0 };
        File.WriteAllText(AppPaths.ConfigFile, config.ToJsonString());

        var (exitCode, stdout, _) = await RunMainAsync("retention", "--dry-run", "--json");

        exitCode.Should().Be(0);
        var doc = JsonNode.Parse(stdout)!;
        var snaps = doc["groups"]![0]!["snapshots"]!.AsArray();
        snaps.Should().HaveCount(3);
        snaps.Count(s => s!["action"]!.GetValue<string>() == "delete").Should().Be(2);
        snaps.Count(s => s!["action"]!.GetValue<string>() == "keep").Should().Be(1);
        File.ReadAllText(AppPaths.SystemStateFile).Should().Be(stateRoot.ToJsonString(), "a dry run must not change the state");
    }

    private static IEnumerable<JsonNode> FindBackupNodes(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.ContainsKey("Timestamp") && obj.ContainsKey("Path")) yield return obj;
                foreach (var child in obj.Select(kv => kv.Value).Where(v => v != null))
                    foreach (var found in FindBackupNodes(child!)) yield return found;
                break;
            case JsonArray arr:
                foreach (var child in arr.Where(v => v != null))
                    foreach (var found in FindBackupNodes(child!)) yield return found;
                break;
        }
    }

    private static Program.ParsedCommand Parse(params string[] args)
    {
        Program.TryParseArguments(args, out var parsed, out var error).Should().BeTrue(error);
        return parsed!;
    }

    [Fact]
    public void Parse_FlagsBeforePositionals_StillYieldTheRightPositionals()
    {
        var restore = Parse("restore", "--conflict", "overwrite", "--dry-run", "snap/HEAD", "out");
        restore.Positionals.Should().Equal("snap/HEAD", "out");
        restore.GetOption("--conflict").Should().Be("overwrite");
        restore.HasOption("--dry-run").Should().BeTrue();

        Parse("backup", "--storage", "s3", @"C:\dir").Positionals.Should().Equal(@"C:\dir");
        Parse("backup", "--storage", "s3", @"C:\dir").GetOption("--storage").Should().Be("s3");
        Parse("verify", "--storage", "s3", "snap/HEAD").Positionals.Should().Equal("snap/HEAD");

        var systemBackup = Parse("system-backup", "--storage", "local", "programs");
        systemBackup.Positionals.Should().Equal("programs");
        systemBackup.GetOption("--storage").Should().Be("local");

        var systemRestore = Parse("system-restore", "--storage", "s3", "path.zip", "programs");
        systemRestore.Positionals.Should().Equal("path.zip", "programs");
        systemRestore.GetOption("--storage").Should().Be("s3");
    }

    [Fact]
    public void Parse_FlagNamesAreCaseInsensitive_AndIncludeIsRepeatable()
    {
        var restore = Parse("restore", "a", "b", "--DRY-RUN", "--INCLUDE", "x/**", "--Include", "y", "--Storage", "s3");

        restore.HasOption("--dry-run").Should().BeTrue();
        restore.Options["--include"].Should().Equal("x/**", "y");
        restore.GetOption("--storage").Should().Be("s3");
    }

    [Fact]
    public async Task Main_Verify_WithFlagBeforePositional_UsesThePositionalAsManifestPath()
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        WriteConfig(MissingWatchDirectory);

        var (exitCode, stdout, stderr) = await RunMainAsync("verify", "--storage", "local", "snapshots/missing/HEAD");

        exitCode.Should().Be(1);
        (stdout + stderr).Should().Contain("snapshots/missing/HEAD");
    }

    [Fact]
    public async Task Main_Service_WithEncryptionAndNoPassword_FailsWithoutPrompting()
    {
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        WriteConfig(_root);
        var salt = EncryptionService.GenerateSalt();
        var token = new EncryptionService(new TestLogger()).CreatePasswordVerificationToken("pw", salt, 1000);
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        root["encryption"] = new JsonObject
        {
            ["enabled"] = true,
            ["salt"] = Convert.ToBase64String(salt),
            ["keyDerivationIterations"] = 1000,
            ["verificationToken"] = token
        };
        File.WriteAllText(AppPaths.ConfigFile, root.ToJsonString());

        var (exitCode, _, stderr) = await RunMainAsync("--service");

        exitCode.Should().Be(4, "a service that cannot ask for the password needs a person (needs_user): " + stderr);
        stderr.Should().Contain(PasswordVariable);
    }

    [Fact]
    public async Task Main_LsVersionsDiff_BrowseSnapshotsFromManifestsAlone()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ReStoreCliBrowse_" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(outside, "src");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            Directory.CreateDirectory(Path.Combine(source, "empty-dir"));
            File.WriteAllText(Path.Combine(source, "doc.txt"), "version one " + new string('1', 3000));
            File.WriteAllText(Path.Combine(source, "sub", "keep.txt"), "kept " + new string('k', 3000));
            WriteConfig(source);
            var configJson = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
            configJson["excludedPaths"] = new JsonArray();
            File.WriteAllText(AppPaths.ConfigFile, configJson.ToJsonString());

            var (firstExit, fo, fe) = await RunMainAsync("backup", source);
            firstExit.Should().Be(0, fo + fe);
            File.WriteAllText(Path.Combine(source, "doc.txt"), "version two, longer " + new string('2', 4000));
            File.WriteAllText(Path.Combine(source, "added.txt"), "new " + new string('n', 3000));
            var (secondExit, so, se) = await RunMainAsync("backup", source);
            secondExit.Should().Be(0, so + se);

            var (listExit, listOut, listErr) = await RunMainAsync("snapshots", "--json");
            listExit.Should().Be(0, listOut + listErr);
            var group = JsonNode.Parse(listOut)!["groups"]!.AsArray().Single()!.AsObject();
            var snapshots = group["snapshots"]!.AsArray().OrderBy(s => s!["createdUtc"]!.GetValue<DateTime>()).ToList();
            snapshots.Should().HaveCount(2);
            var oldManifest = snapshots[0]!["manifestPath"]!.GetValue<string>();
            var newManifest = snapshots[1]!["manifestPath"]!.GetValue<string>();
            var head = SnapshotStoragePaths.GetHeadPath(source);

            // ls: top level of HEAD, then a sub folder, then a missing path.
            var (lsExit, lsOut, lsErr) = await RunMainAsync("ls", head, "--json");
            lsExit.Should().Be(0, lsOut + lsErr);
            var ls = JsonNode.Parse(lsOut)!.AsObject();
            ls["entries"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()).Should().Equal("empty-dir", "sub", "added.txt", "doc.txt");
            ls["entries"]![0]!["type"]!.GetValue<string>().Should().Be("directory");

            var (subExit, subOut, _) = await RunMainAsync("ls", head, "--path", "sub");
            subExit.Should().Be(0);
            subOut.Should().Contain("keep.txt");

            var (missingExit, missingOut, missingErr) = await RunMainAsync("ls", head, "--path", "nope");
            missingExit.Should().Be(1);
            (missingOut + missingErr).Should().Contain("nope");

            // versions: the edited file has two distinct contents, the untouched one a single collapsed run.
            var (verExit, verOut, verErr) = await RunMainAsync("versions", source, "doc.txt", "--json");
            verExit.Should().Be(0, verOut + verErr);
            var versions = JsonNode.Parse(verOut)!["versions"]!.AsArray();
            versions.Should().HaveCount(2);
            versions[0]!["manifestPath"]!.GetValue<string>().Should().Be(newManifest);
            versions[1]!["manifestPath"]!.GetValue<string>().Should().Be(oldManifest);

            var (keepExit, keepOut, keepErr) = await RunMainAsync("versions", source, "sub/keep.txt", "--json");
            keepExit.Should().Be(0, keepOut + keepErr);
            var keep = JsonNode.Parse(keepOut)!["versions"]!.AsArray().Single()!;
            keep["snapshotCount"]!.GetValue<int>().Should().Be(2);
            keep["manifestPath"]!.GetValue<string>().Should().Be(oldManifest);

            var (noFileExit, _, _) = await RunMainAsync("versions", source, "never-existed.txt");
            noFileExit.Should().Be(1);

            // diff: old -> new.
            var (diffExit, diffOut, diffErr) = await RunMainAsync("diff", oldManifest, newManifest, "--json");
            diffExit.Should().Be(0, diffOut + diffErr);
            var diff = JsonNode.Parse(diffOut)!.AsObject();
            diff["added"]!.AsArray().Select(e => e!["relativePath"]!.GetValue<string>()).Should().Equal("added.txt");
            diff["changed"]!.AsArray().Select(e => e!["relativePath"]!.GetValue<string>()).Should().Equal("doc.txt");
            diff["removed"]!.AsArray().Should().BeEmpty();
            diff["unchangedCount"]!.GetValue<int>().Should().Be(1);

            var (plainExit, plainOut, _) = await RunMainAsync("diff", oldManifest, head);
            plainExit.Should().Be(0);
            plainOut.Should().Contain("+ added.txt").And.Contain("~ doc.txt");
        }
        finally
        {
            try { Directory.Delete(outside, true); } catch { }
        }
    }

    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    private static void SeedSecretConfig()
    {
        ConfigInitializer.EnsureConfigurationSetup();
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        root["storageSources"] = new JsonObject
        {
            ["s3"] = new JsonObject { ["options"] = new JsonObject { ["accessKeyId"] = "AKIA", ["secretAccessKey"] = "literal-secret" } }
        };
        File.WriteAllText(AppPaths.ConfigFile, root.ToJsonString());
    }

    [Theory]
    [InlineData("config", "show")]
    [InlineData("config", "show", "--redact")]
    public async Task Main_ConfigShow_MasksSecretsOnStdoutByDefault(params string[] args)
    {
        SeedSecretConfig();

        var (exitCode, stdout, _) = await RunMainAsync(args);

        exitCode.Should().Be(0);
        stdout.Should().Contain("***").And.Contain("AKIA").And.NotContain("literal-secret");
    }

    [Fact]
    public async Task Main_ConfigShowReveal_PrintsSecrets()
    {
        SeedSecretConfig();

        var (exitCode, stdout, _) = await RunMainAsync("config", "show", "--reveal");

        exitCode.Should().Be(0);
        stdout.Should().Contain("literal-secret");
    }

    [Theory]
    [InlineData("config")]
    [InlineData("config", "bogus")]
    [InlineData("config", "protect", "--redact")]
    [InlineData("config", "protect", "--reveal")]
    public async Task Main_ConfigBadUsage_IsUsageError(params string[] args)
    {
        var (exitCode, _, stderr) = await RunMainAsync(args);

        exitCode.Should().Be(2);
        stderr.Should().NotBeEmpty();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunMainAsync(params string[] args)
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
}

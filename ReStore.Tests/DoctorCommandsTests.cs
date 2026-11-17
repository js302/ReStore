using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary><c>restore doctor</c>, run in-process against a temp data root with fakes for everything outside the process.</summary>
public class DoctorCommandsTests : CliConfigTestBase
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A healthy machine on Windows: the app runs, plenty of disk, nothing busy, telemetry off, one PATH entry.</summary>
    private static DoctorEnvironment HealthyEnvironment() => new()
    {
        UtcNow = () => Now,
        IsWindows = true,
        Channel = "portable",
        Architecture = "x64",
        FreeBytes = _ => 50L << 30,
        IsGuiRunning = () => true,
        QueryScheduledTask = _ => Task.FromResult(new ScheduledTaskStatus(false)),
        RestoreOnPath = () => [@"C:\Users\me\AppData\Local\Programs\ReStore\cli\restore.exe"],
        LoadState = logger => Task.FromResult(new SystemState(logger)),
        ProbeBusy = _ => Task.FromResult<(bool, string?)>((false, null)),
        ProbeStorage = (_, _, _) => throw new InvalidOperationException("The probe must not run without --probe."),
        Telemetry = () => new DoctorTelemetryInfo(false, "off (not enabled)", "ask", 0, 0),
        TryGetPassword = _ => null
    };

    private async Task<(int ExitCode, JsonObject Doc, string Stdout, string Stderr)> DoctorAsync(DoctorEnvironment? environment = null, params string[] extra)
    {
        DoctorCommands.CreateEnvironment = () => environment ?? HealthyEnvironment();
        try
        {
            var (exitCode, stdout, stderr) = await RunAsync(["doctor", "--json", .. extra]);
            return (exitCode, Json(stdout), stdout, stderr);
        }
        finally
        {
            DoctorCommands.CreateEnvironment = () => new DoctorEnvironment();
        }
    }

    private static JsonObject Check(JsonObject doc, string id) =>
        doc["checks"]!.AsArray().Select(c => c!.AsObject()).Single(c => c["id"]!.GetValue<string>() == id);

    private static string StatusOf(JsonObject doc, string id) => Check(doc, id)["status"]!.GetValue<string>();

    private static string DetailOf(JsonObject doc, string id) => Check(doc, id)["detail"]!.GetValue<string>();

    // ---- shape and exit codes --------------------------------------------------------------------------------------

    [Fact]
    public async Task Healthy_Setup_Exits0_WithTheDocumentedShape()
    {
        WriteConfig();
        Directory.CreateDirectory(StorageDirectory);

        var (exitCode, doc, stdout, stderr) = await DoctorAsync();

        exitCode.Should().Be(0, stdout + stderr);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["version"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        doc["channel"]!.GetValue<string>().Should().Be("portable");
        var summary = doc["summary"]!.AsObject();
        summary["fail"]!.GetValue<int>().Should().Be(0);
        var checks = doc["checks"]!.AsArray().Select(c => c!.AsObject()).ToList();
        checks.Select(c => c["id"]!.GetValue<string>()).Should().OnlyHaveUniqueItems().And.Contain(
            ["version", "update", "data-root", "disk-space", "config", "watch-directories", "watch-exclusions", "storage", "storage-probe",
             "encryption", "oauth", "state", "repository-lock", "pause", "protection", "staleness", "failures", "path", "msix-virtualization", "telemetry"]);
        foreach (var check in checks)
        {
            check["title"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            check["status"]!.GetValue<string>().Should().BeOneOf("ok", "warn", "fail", "skip");
            check["detail"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        }

        summary["ok"]!.GetValue<int>().Should().Be(checks.Count(c => c["status"]!.GetValue<string>() == "ok"));
        summary["skip"]!.GetValue<int>().Should().Be(checks.Count(c => c["status"]!.GetValue<string>() == "skip"));
        StatusOf(doc, "storage-probe").Should().Be("skip");
        StatusOf(doc, "protection").Should().Be("ok");
        StatusOf(doc, "config").Should().Be("ok");
    }

    [Fact]
    public async Task Warnings_DoNotFail_ButAFailureExits1WithAnErrorObject()
    {
        WriteConfig();
        Directory.CreateDirectory(StorageDirectory);
        var nobodyRuns = HealthyEnvironment();
        nobodyRuns.IsGuiRunning = () => false;
        var warnOnly = await DoctorAsync(nobodyRuns);
        warnOnly.ExitCode.Should().Be(0, warnOnly.Stdout);
        StatusOf(warnOnly.Doc, "protection").Should().Be("warn");

        var environment = HealthyEnvironment();
        environment.LoadState = _ => throw new InvalidDataException("bad state");
        var failed = await DoctorAsync(environment);

        failed.ExitCode.Should().Be(1);
        failed.Doc["ok"]!.GetValue<bool>().Should().BeFalse();
        failed.Doc["exitCode"]!.GetValue<int>().Should().Be(1);
        failed.Doc["error"]!["code"]!.GetValue<string>().Should().Be("failure");
        failed.Doc["error"]!["message"]!.GetValue<string>().Should().Contain("state");
        StatusOf(failed.Doc, "state").Should().Be("fail");
        StatusOf(failed.Doc, "staleness").Should().Be("skip");
        Check(failed.Doc, "state")["fix"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task NoConfigFile_FailsConfig_SkipsTheDependentChecks_AndCreatesNothing()
    {
        File.Exists(AppPaths.ConfigFile).Should().BeFalse();

        var (exitCode, doc, stdout, _) = await DoctorAsync();

        exitCode.Should().Be(1, stdout);
        StatusOf(doc, "config").Should().Be("fail");
        DetailOf(doc, "config").Should().Contain("No configuration file");
        foreach (var id in new[] { "watch-directories", "watch-exclusions", "storage", "encryption", "oauth" })
        {
            StatusOf(doc, id).Should().Be("skip", id);
        }

        File.Exists(AppPaths.ConfigFile).Should().BeFalse("doctor must not create the configuration");
    }

    [Fact]
    public async Task UnparsableConfig_IsReportedAsAFailedCheck_NotAnExit3()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigFile)!);
        File.WriteAllText(AppPaths.ConfigFile, "{ not json");

        var (exitCode, doc, stdout, _) = await DoctorAsync();

        exitCode.Should().Be(1, stdout);
        StatusOf(doc, "config").Should().Be("fail");
        DetailOf(doc, "config").Should().Contain("could not be loaded");
    }

    [Fact]
    public async Task HumanOutput_ListsEveryCheck_WithFixesForProblems()
    {
        WriteConfig();
        var environment = HealthyEnvironment();
        environment.IsGuiRunning = () => false;
        DoctorCommands.CreateEnvironment = () => environment;
        try
        {
            var (exitCode, stdout, _) = await RunAsync("doctor");

            exitCode.Should().Be(0, stdout);
            stdout.Should().Contain("[ok  ] Version").And.Contain("[warn] Automatic backups").And.Contain("fix: restore schedule install");
            stdout.Should().MatchRegex(@"\d+ checks: \d+ ok, \d+ warning\(s\), 0 failed\.");
        }
        finally
        {
            DoctorCommands.CreateEnvironment = () => new DoctorEnvironment();
        }
    }

    [Fact]
    public async Task Doctor_ChangesNothing_NotEvenAMissingLocalStorageFolder()
    {
        WriteConfig();
        Directory.Exists(StorageDirectory).Should().BeFalse();
        var before = File.ReadAllBytes(AppPaths.ConfigFile);

        var (_, doc, _, _) = await DoctorAsync();

        StatusOf(doc, "config").Should().Be("ok");
        Directory.Exists(StorageDirectory).Should().BeFalse("the validator's side effect must be undone");
        File.ReadAllBytes(AppPaths.ConfigFile).Should().Equal(before);
        Directory.EnumerateFiles(AppPaths.Root, ".doctor-write-test-*").Should().BeEmpty();
    }

    // ---- individual checks -------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateCheck_UsesTheCacheOnly_AndSaysWhenItWasChecked()
    {
        WriteConfig();
        var (_, never, _, _) = await DoctorAsync();
        StatusOf(never, "update").Should().Be("ok");
        DetailOf(never, "update").Should().Contain("Not checked yet");

        new ReStore.Core.src.updates.UpdateCache().Save(new ReStore.Core.src.updates.UpdateCacheData
        {
            LastCheckUtc = new DateTime(2026, 11, 16, 8, 30, 0, DateTimeKind.Utc),
            Result = new ReStore.Core.src.updates.UpdateCheckResult
            {
                Status = ReStore.Core.src.updates.UpdateStatus.UpdateAvailable,
                Current = "1.0.0",
                Latest = "99.0.0",
                CheckedUtc = new DateTime(2026, 11, 16, 8, 30, 0, DateTimeKind.Utc)
            }
        });
        var (_, available, _, _) = await DoctorAsync();

        StatusOf(available, "update").Should().Be("warn");
        DetailOf(available, "update").Should().Contain("99.0.0").And.Contain("2026-11-16 08:30 UTC");
        Check(available, "update")["fix"]!.GetValue<string>().Should().Be("restore update install");
    }

    [Fact]
    public async Task DiskSpace_WarnsBelow1GB_ForTheDataFolderAndLocalStorage()
    {
        WriteConfig();
        var environment = HealthyEnvironment();
        var asked = new List<string>();
        environment.FreeBytes = path =>
        {
            asked.Add(path);
            return path.StartsWith(StorageDirectory, StringComparison.Ordinal) ? 100L << 20 : 5L << 30;
        };

        var (exitCode, doc, _, _) = await DoctorAsync(environment);

        exitCode.Should().Be(0);
        StatusOf(doc, "disk-space").Should().Be("warn");
        DetailOf(doc, "disk-space").Should().Contain("local storage").And.Contain("data folder");
        asked.Should().Contain(AppPaths.Root).And.Contain(StorageDirectory);
    }

    [Fact]
    public async Task DataRoot_Missing_WarnsInsteadOfCreatingIt()
    {
        var gone = Path.Combine(Root, "not-created-yet");
        var previous = AppPaths.Root;
        AppPaths.OverrideRoot(gone);
        try
        {
            var checks = await new DoctorRunner(HealthyEnvironment(), new TestLogger(), null, "no config").RunAsync(false, CancellationToken.None);

            checks.Single(c => c.Id == "data-root").Status.Should().Be("warn");
            Directory.Exists(gone).Should().BeFalse();
        }
        finally
        {
            AppPaths.OverrideRoot(previous);
        }
    }

    [Fact]
    public async Task Config_ValidationErrors_Fail_WithTheFirstError()
    {
        WriteConfig(root => root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = Path.Combine(Root, "gone") }));
        var (exitCode, doc, _, _) = await DoctorAsync();
        exitCode.Should().Be(1);
        StatusOf(doc, "config").Should().Be("fail");
        DetailOf(doc, "config").Should().Contain("1 error(s)").And.Contain("No valid watch directories");
        Check(doc, "config")["fix"]!.GetValue<string>().Should().Contain("config validate");
        StatusOf(doc, "watch-directories").Should().Be("warn");
    }

    [Fact]
    public async Task WatchDirectories_Missing_NoneConfigured_AndInsideAnExcludedPath()
    {
        var excluded = Path.Combine(Root, "excluded");
        var inside = Path.Combine(excluded, "work");
        Directory.CreateDirectory(inside);
        WriteConfig(root =>
        {
            root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = inside }, new JsonObject { ["path"] = Path.Combine(Root, "missing") });
            root["excludedPaths"] = new JsonArray(excluded);
        });

        var (_, doc, _, _) = await DoctorAsync();

        StatusOf(doc, "watch-directories").Should().Be("warn");
        DetailOf(doc, "watch-directories").Should().Contain("1 of 2");
        StatusOf(doc, "watch-exclusions").Should().Be("warn");
        DetailOf(doc, "watch-exclusions").Should().Contain(inside).And.Contain("excludedPaths");

        WriteConfig(root => root["watchDirectories"] = new JsonArray());
        var (_, none, _, _) = await DoctorAsync();
        StatusOf(none, "watch-directories").Should().Be("warn");
        DetailOf(none, "watch-directories").Should().Contain("No folders");
    }

    [Fact]
    public async Task Storage_RemoteWithoutRequiredOptions_Fails_WithAFix()
    {
        WriteConfig(root =>
        {
            root["globalStorageType"] = "s3";
            root["storageSources"] = new JsonObject
            {
                ["s3"] = new JsonObject { ["path"] = "", ["options"] = new JsonObject { ["bucketName"] = "b", ["region"] = "eu-west-1" } }
            };
        });

        var (exitCode, doc, _, _) = await DoctorAsync();

        exitCode.Should().Be(1);
        StatusOf(doc, "storage").Should().Be("fail");
        DetailOf(doc, "storage").Should().Contain("accessKeyId").And.Contain("secretAccessKey");
        Check(doc, "storage")["fix"]!.GetValue<string>().Should().Contain("config set-storage s3");
    }

    [Fact]
    public async Task StorageProbe_RunsOnlyWithTheFlag_AndReportsEachStorage()
    {
        WriteConfig();
        Directory.CreateDirectory(StorageDirectory);
        var environment = HealthyEnvironment();
        var probed = new List<string>();
        environment.ProbeStorage = (_, type, _) =>
        {
            probed.Add(type);
            return Task.FromResult(new StorageProbeResult("restore-probe/x", [new StorageProbeStep("upload", true, false, 3, null)], true));
        };

        var (exitCode, doc, _, _) = await DoctorAsync(environment, "--probe");

        exitCode.Should().Be(0);
        probed.Should().Equal("local");
        StatusOf(doc, "storage-probe").Should().Be("ok");

        environment.ProbeStorage = (_, _, _) => Task.FromResult(new StorageProbeResult(
            "k", [new StorageProbeStep("upload", true, false, 1, null), new StorageProbeStep("download", false, false, 1, "boom Bearer abc123secret")], true));
        var failed = await DoctorAsync(environment, "--probe");

        failed.ExitCode.Should().Be(1);
        StatusOf(failed.Doc, "storage-probe").Should().Be("fail");
        DetailOf(failed.Doc, "storage-probe").Should().Contain("download failed").And.NotContain("abc123secret");

        environment.ProbeStorage = (_, _, _) => throw new StorageAuthRequiredException("sign in");
        var auth = await DoctorAsync(environment, "--probe");
        DetailOf(auth.Doc, "storage-probe").Should().Contain("needs a sign-in");
    }

    [Fact]
    public async Task Encryption_WarnsWhenNoPasswordIsAvailable_AndOkWhenOneIs()
    {
        WriteConfig(root => root["encryption"] = new JsonObject
        {
            ["enabled"] = true, ["salt"] = "AAAA", ["keyDerivationIterations"] = 1000, ["verificationToken"] = "BBBB"
        });

        var (exitCode, doc, _, _) = await DoctorAsync();
        exitCode.Should().Be(0, "a missing password is a warning");
        StatusOf(doc, "encryption").Should().Be("warn");
        Check(doc, "encryption")["fix"]!.GetValue<string>().Should().Contain("RESTORE_ENCRYPTION_PASSWORD").And.Contain("unlock --remember");

        var environment = HealthyEnvironment();
        environment.TryGetPassword = _ => "pw";
        StatusOf((await DoctorAsync(environment)).Doc, "encryption").Should().Be("ok");

        WriteConfig();
        DetailOf((await DoctorAsync()).Doc, "encryption").Should().Contain("Not enabled");
    }

    [Fact]
    public async Task Encryption_DefaultPasswordLookup_SeesTheEnvironmentVariable()
    {
        WriteConfig(root => root["encryption"] = new JsonObject
        {
            ["enabled"] = true, ["salt"] = "AAAA", ["keyDerivationIterations"] = 1000, ["verificationToken"] = "BBBB"
        });
        var environment = HealthyEnvironment();
        environment.TryGetPassword = config => CliPasswordSources.TryGetConfiguredPassword(config);
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "something long enough");
        try
        {
            StatusOf((await DoctorAsync(environment)).Doc, "encryption").Should().Be("ok");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", null);
        }
    }

    [Fact]
    public async Task OAuth_GdriveInUseWithoutToken_Fails_AndIsSkippedWhenUnused()
    {
        WriteConfig();
        StatusOf((await DoctorAsync()).Doc, "oauth").Should().Be("skip");

        var tokens = Path.Combine(Root, "tokens", "Drive.Storage.Token");
        WriteConfig(root =>
        {
            root["globalStorageType"] = "gdrive";
            root["storageSources"] = new JsonObject
            {
                ["gdrive"] = new JsonObject { ["path"] = "", ["options"] = new JsonObject { ["client_id"] = "id", ["client_secret"] = "secret", ["token_folder"] = tokens } }
            };
        });
        var (exitCode, doc, _, _) = await DoctorAsync();

        exitCode.Should().Be(1);
        StatusOf(doc, "oauth").Should().Be("fail");
        DetailOf(doc, "oauth").Should().Contain("gdrive: not signed in");
        Check(doc, "oauth")["fix"]!.GetValue<string>().Should().Contain("restore auth gdrive");

        WriteConfig(root =>
        {
            root["globalStorageType"] = "dropbox";
            root["storageSources"] = new JsonObject
            {
                ["dropbox"] = new JsonObject { ["path"] = "", ["options"] = new JsonObject { ["appKey"] = "k", ["appSecret"] = "s", ["refreshToken"] = "tok" } }
            };
        });
        DetailOf((await DoctorAsync()).Doc, "oauth").Should().Be("dropbox: token present.");
    }

    [Fact]
    public async Task RepositoryLock_Busy_Warns_WithTheReason()
    {
        WriteConfig();
        var environment = HealthyEnvironment();
        environment.ProbeBusy = _ => Task.FromResult<(bool, string?)>((true, "Backing up C:\\Data"));

        var (exitCode, doc, _, _) = await DoctorAsync(environment);

        exitCode.Should().Be(0);
        StatusOf(doc, "repository-lock").Should().Be("warn");
        DetailOf(doc, "repository-lock").Should().Contain("Backing up");
    }

    [Fact]
    public async Task Protection_NothingRunning_Warns_AndEachSourceCounts()
    {
        WriteConfig();
        var none = HealthyEnvironment();
        none.IsGuiRunning = () => false;
        var nothing = await DoctorAsync(none);
        StatusOf(nothing.Doc, "protection").Should().Be("warn");
        DetailOf(nothing.Doc, "protection").Should().Contain("Nothing runs backups automatically");
        Check(nothing.Doc, "protection")["fix"]!.GetValue<string>().Should().Be("restore schedule install");

        var scheduled = HealthyEnvironment();
        scheduled.IsGuiRunning = () => false;
        scheduled.QueryScheduledTask = _ => Task.FromResult(new ScheduledTaskStatus(true, true, 15));
        var task = await DoctorAsync(scheduled);
        StatusOf(task.Doc, "protection").Should().Be("ok");
        DetailOf(task.Doc, "protection").Should().Contain(@"\ReStore\run-due").And.Contain("15");

        var disabled = HealthyEnvironment();
        disabled.IsGuiRunning = () => false;
        disabled.QueryScheduledTask = _ => Task.FromResult(new ScheduledTaskStatus(true, false, 15));
        var off = await DoctorAsync(disabled);
        StatusOf(off.Doc, "protection").Should().Be("warn");
        DetailOf(off.Doc, "protection").Should().Contain("disabled");

        var linux = HealthyEnvironment();
        linux.IsWindows = false;
        linux.IsGuiRunning = () => false;
        linux.QueryScheduledTask = _ => throw new InvalidOperationException("must not query a Windows task off Windows");
        var other = await DoctorAsync(linux);
        Check(other.Doc, "protection")["fix"]!.GetValue<string>().Should().Contain("cron");
        StatusOf(other.Doc, "path").Should().Be("skip");
    }

    [Fact]
    public async Task Pause_WarnsWhilePaused()
    {
        WriteConfig();
        new ProtectionPauseStore().Save(Now.AddHours(2));
        StatusOf((await DoctorAsync()).Doc, "pause").Should().Be("warn");

        new ProtectionPauseStore().Save(Now.AddHours(-2));
        StatusOf((await DoctorAsync()).Doc, "pause").Should().Be("ok");

        new ProtectionPauseStore().Save(ProtectionPause.Indefinite);
        var (_, doc, _, _) = await DoctorAsync();
        StatusOf(doc, "pause").Should().Be("warn");
        Check(doc, "pause")["fix"]!.GetValue<string>().Should().Be("restore resume");
    }

    [Fact]
    public async Task Staleness_And_RecentFailures_ComeFromTheState()
    {
        WriteConfig();
        var state = new SystemState(new TestLogger());
        state.RecordGroupRunSucceeded(WatchedDirectory, Now.AddDays(-10));
        state.RecordActivity(new ActivityEntry
        {
            Type = ActivityType.Backup, Target = WatchedDirectory, StartUtc = Now.AddHours(-3), EndUtc = Now.AddHours(-3), Result = ActivityResult.Failed,
            Message = "upload failed, Authorization: Bearer abcdef123456 was rejected"
        });
        state.RecordActivity(new ActivityEntry
        {
            Type = ActivityType.Restore, Target = "old", StartUtc = Now.AddDays(-30), EndUtc = Now.AddDays(-30), Result = ActivityResult.Failed, Message = "too old to count"
        });
        var environment = HealthyEnvironment();
        environment.LoadState = _ => Task.FromResult(state);

        var (exitCode, doc, stdout, _) = await DoctorAsync(environment);

        exitCode.Should().Be(0);
        StatusOf(doc, "staleness").Should().Be("warn");
        DetailOf(doc, "staleness").Should().Contain("10 day(s)").And.Contain(WatchedDirectory);
        StatusOf(doc, "failures").Should().Be("warn");
        DetailOf(doc, "failures").Should().Contain("1 failed operation").And.Contain("upload failed").And.NotContain("abcdef123456");
        stdout.Should().NotContain("abcdef123456");
    }

    [Fact]
    public async Task Staleness_OkWhenRecent_AndFailuresOkWhenNone()
    {
        WriteConfig();
        var state = new SystemState(new TestLogger());
        state.RecordGroupRunSucceeded(WatchedDirectory, Now.AddMinutes(-5));
        var environment = HealthyEnvironment();
        environment.LoadState = _ => Task.FromResult(state);

        var (_, doc, _, _) = await DoctorAsync(environment);

        StatusOf(doc, "staleness").Should().Be("ok");
        StatusOf(doc, "failures").Should().Be("ok");
    }

    [Theory]
    [InlineData(new string[0], "warn")]
    [InlineData(new[] { @"C:\Users\me\AppData\Local\Programs\ReStore\cli\restore.exe" }, "ok")]
    [InlineData(new[] { @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\restore.exe" }, "ok")]
    public void PathClassification_SingleOrMissing(string[] found, string expected) =>
        DoctorRunner.ClassifyPath(found).Status.Should().Be(expected);

    [Fact]
    public void PathClassification_BothCopies_NamesTheWinner()
    {
        var alias = @"C:\Users\me\AppData\Local\Microsoft\WindowsApps\restore.exe";
        var portable = @"C:\Users\me\AppData\Local\Programs\ReStore\cli\restore.exe";

        var portableFirst = DoctorRunner.ClassifyPath([portable, alias]);
        portableFirst.Status.Should().Be("warn");
        portableFirst.Detail.Should().Contain("the portable copy wins");

        DoctorRunner.ClassifyPath([alias, portable]).Detail.Should().Contain("the MSIX alias wins");
    }

    [Fact]
    public async Task Path_OnWindows_UsesTheEnvironment()
    {
        WriteConfig();
        var environment = HealthyEnvironment();
        environment.RestoreOnPath = () => [];

        var (exitCode, doc, _, _) = await DoctorAsync(environment);

        exitCode.Should().Be(0);
        StatusOf(doc, "path").Should().Be("warn");
    }

    [Fact]
    public async Task MsixVirtualization_WarnsOnlyForMsixWithDevTools()
    {
        WriteConfig(root => root["systemBackup"] = new JsonObject { ["enabled"] = true, ["includeDevTools"] = true });
        StatusOf((await DoctorAsync()).Doc, "msix-virtualization").Should().Be("skip");

        var msix = HealthyEnvironment();
        msix.Channel = "msix";
        var warned = await DoctorAsync(msix);
        StatusOf(warned.Doc, "msix-virtualization").Should().Be("warn");
        Check(warned.Doc, "msix-virtualization")["fix"]!.GetValue<string>().Should().Contain("install.ps1");

        WriteConfig(root => root["systemBackup"] = new JsonObject { ["enabled"] = true, ["includeDevTools"] = false });
        StatusOf((await DoctorAsync(msix)).Doc, "msix-virtualization").Should().Be("ok");
    }

    [Fact]
    public async Task Telemetry_ReportsTheStatus_AndPendingCrashReports()
    {
        WriteConfig();
        var (_, plain, _, _) = await DoctorAsync();
        StatusOf(plain, "telemetry").Should().Be("ok");
        DetailOf(plain, "telemetry").Should().Contain("off").And.Contain("crash reports: ask");

        var environment = HealthyEnvironment();
        environment.Telemetry = () => new DoctorTelemetryInfo(true, "on", "ask", 2, 1);
        var (exitCode, pending, _, _) = await DoctorAsync(environment);

        exitCode.Should().Be(0);
        StatusOf(pending, "telemetry").Should().Be("warn");
        DetailOf(pending, "telemetry").Should().Contain("2 crash report(s) pending").And.Contain("1 queued");
        Check(pending, "telemetry")["fix"]!.GetValue<string>().Should().Contain("telemetry crashes list").And.Contain("ask the user");
    }

    [Fact]
    public async Task DefaultTelemetrySeam_ReadsTheRealClient_WithoutTouchingTheNetwork()
    {
        WriteConfig();
        var environment = HealthyEnvironment();
        environment.Telemetry = new DoctorEnvironment().Telemetry;

        var (_, doc, _, _) = await DoctorAsync(environment);

        StatusOf(doc, "telemetry").Should().BeOneOf("ok", "warn");
    }

    [Fact]
    public void DoctorIsRegistered_AsAnInfoCommand_ThatNeedsNoConfiguration()
    {
        var spec = CommandRegistry.Find("doctor")!;
        spec.Category.Should().Be(CommandCategory.Info);
        spec.Config.Should().Be(ConfigRequirement.None);
        spec.Mutates.Should().BeFalse();
        spec.Options.Select(o => o.Name).Should().Equal("--probe");
    }
}

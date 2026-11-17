using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using ReStore.Core.src.cli;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Pure pieces of the v1.8 review fixes: masked restore plans, the headless scheduled task, the MSIX helper launch.</summary>
public class CliFixSupportTests
{
    // ---- I2: restore plans do not leak environment values ---------------------------------------------------------

    private static PlannedAction Env(string name, string? oldValue, string? newValue, PlannedChange change = PlannedChange.Change) => new()
    {
        Id = "env:User:" + name.ToUpperInvariant(), Component = "environment", Change = change, Name = name, Scope = "User",
        OldValue = oldValue, NewValue = newValue
    };

    [Theory]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("AWS_SECRET_ACCESS_KEY", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("MYSQL_PASSWD", true)]
    [InlineData("api_key", true)]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS", true)]
    [InlineData("OAUTH_AUTH_HEADER", true)]
    [InlineData("AZURE_STORAGE_CONNECTION_STRING", true)]
    [InlineData("JAVA_HOME", false)]
    [InlineData("EDITOR", false)]
    public void SecretNames_AreRecognised(string name, bool secret) =>
        SystemRestoreValueView.IsSecretName(name).Should().Be(secret);

    [Fact]
    public void MaskedView_ShowsNameChangeAndLength_ButNeverTheValue()
    {
        var view = SystemRestoreValueView.Describe(Env("JAVA_HOME", @"C:\old\jdk", @"C:\new\jdk17"), reveal: false);

        view.Masked.Should().BeTrue();
        view.OldValue.Should().NotContain("jdk").And.StartWith("***");
        view.NewValue.Should().NotContain("jdk");
        view.OldLength.Should().Be(10);
        view.NewLength.Should().Be(12);
        view.Changed.Should().BeTrue();
    }

    [Fact]
    public void SecretNamedVariables_GetNoLengthEither()
    {
        var view = SystemRestoreValueView.Describe(Env("GITHUB_TOKEN", "ghp_old_secret_value", "ghp_new_secret_value"), reveal: false);

        view.OldValue.Should().Be("***");
        view.NewValue.Should().Be("***");
        view.OldLength.Should().BeNull();
        view.NewLength.Should().BeNull();
        view.Changed.Should().BeTrue();
    }

    [Fact]
    public void PathLikeVariables_ListTheAddedAndRemovedEntries()
    {
        var view = SystemRestoreValueView.Describe(Env("Path", @"C:\a;C:\b;C:\c", @"C:\a;C:\c;D:\tools"), reveal: false);

        view.PathAdded.Should().Equal(@"D:\tools");
        view.PathRemoved.Should().Equal(@"C:\b");
        view.OldValue.Should().NotContain(@"C:\a");

        var secretPath = SystemRestoreValueView.Describe(Env("TOKEN_PATH", "a;b", "a;c"), reveal: false);
        secretPath.PathAdded.Should().BeEmpty("a secret-looking name never lists entries");
    }

    [Fact]
    public void Reveal_ShowsTheValuesAsTheyAre()
    {
        var view = SystemRestoreValueView.Describe(Env("GITHUB_TOKEN", "old", "new"), reveal: true);

        view.Masked.Should().BeFalse();
        view.OldValue.Should().Be("old");
        view.NewValue.Should().Be("new");
    }

    [Fact]
    public void OtherComponents_AreNotMasked()
    {
        var action = new PlannedAction { Id = "p", Component = "programs", Change = PlannedChange.Install, Name = "Git", NewValue = "2.45.0" };

        SystemRestoreValueView.Describe(action, reveal: false).NewValue.Should().Be("2.45.0");
    }

    [Fact]
    public void PlanText_MasksEnvironmentValuesByDefault_AndRevealsOnRequest()
    {
        var plan = new SystemRestorePlan("environment");
        plan.Actions.Add(Env("GITHUB_TOKEN", "ghp_old_secret", "ghp_new_secret"));
        plan.Actions.Add(Env("Path", @"C:\a", @"C:\a;D:\tools"));

        var masked = SystemRestorePlanFormatter.Format(plan);
        masked.Should().NotContain("ghp_").And.NotContain(@"C:\a");
        masked.Should().Contain("GITHUB_TOKEN").And.Contain(@"+ D:\tools").And.Contain("--reveal");

        var revealed = SystemRestorePlanFormatter.Format(plan, reveal: true);
        revealed.Should().Contain("ghp_old_secret").And.Contain("ghp_new_secret");
    }

    [Fact]
    public void SystemRestore_HasARevealOption()
    {
        CommandRegistry.Find("system-restore")!.Options.Should().Contain(o => o.Name == "--reveal");
    }

    // ---- B1: the scheduled task runs hidden through conhost --------------------------------------------------------

    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void TaskXml_WithConhost_RunsTheExeHeadless()
    {
        var xml = ScheduledTaskManager.BuildTaskXml(@"C:\Program Files\ReStore\restore.exe", 15, null, DateTime.Now, useConhost: true);

        var doc = XDocument.Parse(xml);
        doc.Descendants(TaskNs + "Command").Single().Value.Should().Be(@"%SystemRoot%\System32\conhost.exe");
        doc.Descendants(TaskNs + "Arguments").Single().Value.Should().Be("--headless \"C:\\Program Files\\ReStore\\restore.exe\" run-due --quiet");
    }

    [Fact]
    public void TaskXml_WithConhost_EscapesXmlInThePath()
    {
        var xml = ScheduledTaskManager.BuildTaskXml(@"C:\a&b<c>\restore.exe", 15, null, DateTime.Now, useConhost: true);

        XDocument.Parse(xml).Descendants(TaskNs + "Arguments").Single().Value.Should().Be("--headless \"C:\\a&b<c>\\restore.exe\" run-due --quiet");
    }

    [Fact]
    public async Task Install_UsesConhostOnlyWhenItExists()
    {
        string? withConhost = null, without = null;
        ProcessInvoker Capture(Action<string> sink) => (_, arguments, _, _) =>
        {
            sink(File.ReadAllText(arguments[4]));
            return Task.FromResult(new ProcessResult(0, "SUCCESS", "", false));
        };

        (await new ScheduledTaskManager(Capture(x => withConhost = x), conhostAvailable: () => true).InstallAsync(@"C:\r\restore.exe", 15, default)).Should().BeNull();
        (await new ScheduledTaskManager(Capture(x => without = x), conhostAvailable: () => false).InstallAsync(@"C:\r\restore.exe", 15, default)).Should().BeNull();

        withConhost.Should().Contain("conhost.exe").And.Contain("--headless");
        without.Should().NotContain("conhost").And.Contain("<Arguments>run-due --quiet</Arguments>");
    }

    [Fact]
    public void ParseStatus_LooksThroughTheConhostWrapper()
    {
        var definition = ScheduledTaskManager.BuildTaskXml(@"C:\Program Files\ReStore\restore.exe", 20, null, DateTime.Now, useConhost: true);

        var status = ScheduledTaskManager.ParseStatus(definition, null);

        status.Executable.Should().Be(@"C:\Program Files\ReStore\restore.exe");
        status.Arguments.Should().Be("run-due --quiet");
        status.IntervalMinutes.Should().Be(20);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\conhost.exe", "--headless C:\\x\\restore.exe run-due --quiet", @"C:\x\restore.exe", "run-due --quiet")]
    [InlineData(@"C:\r\restore.exe", "run-due --quiet", @"C:\r\restore.exe", "run-due --quiet")]
    public void UnwrapConhost_HandlesQuotedUnquotedAndPlainTasks(string command, string arguments, string expectedExe, string expectedArguments) =>
        ScheduledTaskManager.UnwrapConhost(command, arguments).Should().Be((expectedExe, expectedArguments));

    [Fact]
    public void RunDueRecord_RoundTrips_HoldsNoPathsOrMessages_AndSurvivesGarbage()
    {
        var root = Path.Combine(Path.GetTempPath(), "RunDueRecord_" + Guid.NewGuid().ToString("N"));
        var previous = AppPaths.Root;
        AppPaths.OverrideRoot(root);
        try
        {
            RunDueRecord.TryLoad().Should().BeNull();
            var record = new RunDueRecord
            {
                StartedUtc = new DateTime(2030, 5, 6, 7, 8, 9, DateTimeKind.Utc), FinishedUtc = new DateTime(2030, 5, 6, 7, 9, 9, DateTimeKind.Utc),
                ExitCode = 5, ErrorCode = "busy", Deferred = 2, BackupsSucceeded = 3
            };
            record.TrySave();

            var loaded = RunDueRecord.TryLoad()!;
            loaded.ExitCode.Should().Be(5);
            loaded.ErrorCode.Should().Be("busy");
            loaded.Deferred.Should().Be(2);
            loaded.Describe().Should().Contain("failed (exit code 5, busy)").And.Contain("3 backup(s) succeeded").And.Contain("2 deferred");
            File.ReadAllText(RunDueRecord.FilePath).Should().NotContain(root);

            File.WriteAllText(RunDueRecord.FilePath, "{ not json");
            RunDueRecord.TryLoad().Should().BeNull();
        }
        finally
        {
            AppPaths.OverrideRoot(previous);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ---- B4: the MSIX helper starts outside the package's process tree ---------------------------------------------

    private const string Msix = @"C:\Users\O'Brien\ReStore\updates\1.8.0\p.msix";
    private const string Log = @"C:\Users\O'Brien\logs\update-install.log";

    [Fact]
    public void HelperCommandLine_CarriesTheInstallScriptAsEncodedCommand()
    {
        var script = PowerShellMsixInstallLauncher.BuildScript(Msix, Log, "Fam_1!ReStore");
        var commandLine = PowerShellMsixInstallLauncher.BuildHelperCommandLine(Msix, Log, "Fam_1!ReStore");

        commandLine.Should().StartWith("powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand ");
        var encoded = commandLine[(commandLine.LastIndexOf(' ') + 1)..];
        Encoding.Unicode.GetString(Convert.FromBase64String(encoded)).Should().Be(script);
    }

    [Fact]
    public void LauncherScript_AsksWmiToCreateTheHelper_AndFallsBackToStartProcess()
    {
        var launcher = PowerShellMsixInstallLauncher.BuildLauncherScript(Msix, Log, "Fam_1!ReStore");
        var commandLine = PowerShellMsixInstallLauncher.BuildHelperCommandLine(Msix, Log, "Fam_1!ReStore");

        launcher.Should().Contain("Invoke-CimMethod -ClassName Win32_Process -MethodName Create");
        launcher.Should().Contain("@{ CommandLine = '" + commandLine + "' }");
        launcher.Should().Contain("catch { Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(");
        launcher.Should().NotContain("Add-AppxPackage", "the install script is only inside the encoded helper command");
        launcher.Should().NotContain("O'Brien", "paths are only inside the encoded helper command");
        launcher.Should().NotContain("\"");
    }

    [Fact]
    public void LauncherArguments_RunTheLauncherScript_AndStartInfoIsHidden()
    {
        var args = PowerShellMsixInstallLauncher.BuildArguments(Msix, Log, null);

        args.Should().ContainInOrder("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command");
        args[^1].Should().Be(PowerShellMsixInstallLauncher.BuildLauncherScript(Msix, Log, null));
        var info = PowerShellMsixInstallLauncher.BuildStartInfo(Msix, Log, null);
        info.FileName.Should().Be("powershell.exe");
        info.CreateNoWindow.Should().BeTrue();
        info.ArgumentList.Should().HaveCount(args.Length);
    }

    [Fact]
    public void HelperScript_KeepsTheLogAndRelaunchBehaviour()
    {
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(
            PowerShellMsixInstallLauncher.BuildHelperArguments(Msix, Log, "Fam_1!ReStore")[^1]));

        script.Should().Contain("Add-AppxPackage -Path $pkg -ForceApplicationShutdown")
            .And.Contain("Log 'Update installed.'")
            .And.Contain(@"-ArgumentList 'shell:AppsFolder\Fam_1!ReStore'");
    }
}

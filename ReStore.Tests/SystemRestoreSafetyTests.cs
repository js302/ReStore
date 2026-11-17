using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class EnvironmentVariableMergerTests
{
    [Fact]
    public void Merge_KeepsExistingEntriesAndAppendsNewOnesInOrder()
    {
        EnvironmentVariableMerger.Merge(@"C:\a;C:\b", @"C:\c;C:\a").Should().Be(@"C:\a;C:\b;C:\c");
    }

    [Fact]
    public void Merge_DedupesCaseInsensitivelyAndIgnoresTrailingSlashes()
    {
        EnvironmentVariableMerger.Merge(@"C:\Tools\", @"c:\tools;C:\TOOLS\;D:\x").Should().Be(@"C:\Tools\;D:\x");
    }

    [Fact]
    public void Merge_DropsEmptyEntriesAndHandlesNulls()
    {
        EnvironmentVariableMerger.Merge(null, ";C:\\a;;").Should().Be(@"C:\a");
        EnvironmentVariableMerger.Merge("C:\\a", null).Should().Be(@"C:\a");
        EnvironmentVariableMerger.Merge(null, null).Should().BeEmpty();
    }

    [Fact]
    public void Merge_KeepsPercentVariableEntriesVerbatim()
    {
        EnvironmentVariableMerger.Merge(@"%SystemRoot%\system32", @"%SystemRoot%\System32\;C:\x")
            .Should().Be(@"%SystemRoot%\system32;C:\x");
    }

    [Theory]
    [InlineData("PATH", "x", true)]
    [InlineData("pathext", "x", true)]
    [InlineData("PSModulePath", "x", true)]
    [InlineData("MYLIST", "a;b", true)]
    [InlineData("JAVA_HOME", @"C:\jdk", false)]
    public void IsListVariable_DetectsPathLikeVariables(string name, string value, bool expected)
    {
        EnvironmentVariableMerger.IsListVariable(name, value).Should().Be(expected);
    }
}

public class EnvironmentRestoreTests
{
    private sealed class FakeStore : IEnvironmentVariableStore
    {
        public Dictionary<(string, EnvironmentVariableTarget), StoredEnvironmentValue> Values { get; } = [];
        public HashSet<EnvironmentVariableTarget> Unwritable { get; } = [];
        public int Notified { get; private set; }

        public StoredEnvironmentValue? Get(string name, EnvironmentVariableTarget target)
            => Values.TryGetValue((name.ToUpperInvariant(), target), out var v) ? v : null;

        public void Set(string name, string value, EnvironmentVariableTarget target, string kind)
        {
            if (Unwritable.Contains(target))
            {
                throw new UnauthorizedAccessException("Access denied");
            }

            Values[(name.ToUpperInvariant(), target)] = new StoredEnvironmentValue(value, kind);
        }

        public void NotifyChanged() => Notified++;
    }

    private static EnvironmentVariablesManager Manager() => new(new TestLogger());

    [Fact]
    public void Path_IsMergedNotReplaced_AndKeepsExpandString()
    {
        var store = new FakeStore();
        store.Values[("PATH", EnvironmentVariableTarget.User)] = new(@"%USERPROFILE%\bin;C:\existing", EnvironmentVariableKinds.ExpandString);

        var result = Manager().ApplyVariables(
            [new EnvironmentVariableEntry { Name = "Path", Value = @"C:\backedup;C:\existing", Target = EnvironmentVariableTarget.User }],
            store);

        result.Failed.Should().Be(0);
        result.Merged.Should().Be(1);
        var stored = store.Values[("PATH", EnvironmentVariableTarget.User)];
        stored.Value.Should().Be(@"%USERPROFILE%\bin;C:\existing;C:\backedup");
        stored.Kind.Should().Be(EnvironmentVariableKinds.ExpandString);
        store.Notified.Should().Be(1);
    }

    [Fact]
    public void ExpandStringKind_FromBackup_IsPreserved()
    {
        var store = new FakeStore();

        Manager().ApplyVariables(
            [new EnvironmentVariableEntry { Name = "MY_HOME", Value = @"%USERPROFILE%\x", Target = EnvironmentVariableTarget.User, Kind = "ExpandString" }],
            store);

        store.Values[("MY_HOME", EnvironmentVariableTarget.User)].Kind.Should().Be(EnvironmentVariableKinds.ExpandString);
    }

    [Fact]
    public void MissingKind_FromOldFiles_RestoresAsPlainString()
    {
        var store = new FakeStore();

        Manager().ApplyVariables(
            [new EnvironmentVariableEntry { Name = "MY_VAR", Value = "v", Target = EnvironmentVariableTarget.User }],
            store);

        store.Values[("MY_VAR", EnvironmentVariableTarget.User)].Kind.Should().Be(EnvironmentVariableKinds.String);
    }

    [Fact]
    public void MachineScopeWithoutElevation_IsReportedAsFailure_AndOthersStillRestore()
    {
        var store = new FakeStore();
        store.Unwritable.Add(EnvironmentVariableTarget.Machine);

        var result = Manager().ApplyVariables(
        [
            new EnvironmentVariableEntry { Name = "M_VAR", Value = "1", Target = EnvironmentVariableTarget.Machine },
            new EnvironmentVariableEntry { Name = "U_VAR", Value = "2", Target = EnvironmentVariableTarget.User }
        ], store);

        result.Failed.Should().Be(1);
        result.Failures.Single().Should().Contain("M_VAR").And.Contain("administrator");
        result.Restored.Should().Be(1);
        store.Values.Should().ContainKey(("U_VAR", EnvironmentVariableTarget.User));
    }

    [Fact]
    public void SystemManagedVariables_AreNotRestored()
    {
        var store = new FakeStore();

        var result = Manager().ApplyVariables(
            [new EnvironmentVariableEntry { Name = "COMSPEC", Value = @"C:\evil.exe", Target = EnvironmentVariableTarget.User }],
            store);

        result.Skipped.Should().Be(1);
        store.Values.Should().BeEmpty();
    }
}

public class RegistryImportPolicyTests
{
    private const string Header = "Windows Registry Editor Version 5.00\r\n\r\n";

    [Fact]
    public void AllowList_IsDerivedFromExportDefinitions()
    {
        var exported = WindowsSettingsDefinitions.CreateCategories().Values.SelectMany(v => v).Select(k => k.Path);
        RegistryImportPolicy.AllowedKeyPrefixes.Should().BeEquivalentTo(exported.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AcceptsExportedKeyAndSubkeys()
    {
        var text = Header +
            "[HKEY_CURRENT_USER\\Control Panel\\Mouse]\r\n\"Speed\"=\"1\"\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Control Panel\\Mouse\\Sub]\r\n@=\"x\"\r\n";

        var result = RegistryImportPolicy.Validate(text, @"HKEY_CURRENT_USER\Control Panel\Mouse");

        result.IsValid.Should().BeTrue(string.Join(";", result.Errors));
        result.Keys.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"HKEY_CURRENT_USER\Control PanelEvil")]
    [InlineData(@"HKEY_CLASSES_ROOT\exefile\shell\open\command")]
    [InlineData(@"-HKEY_CURRENT_USER\Control Panel\Mouse")]
    public void Validate_RejectsKeysOutsideAllowList(string key)
    {
        var result = RegistryImportPolicy.Validate(Header + $"[{key}]\r\n\"a\"=\"b\"\r\n");

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsFileWhenAnyKeyIsDisallowed()
    {
        var text = Header +
            "[HKEY_CURRENT_USER\\Control Panel\\Mouse]\r\n\"Speed\"=\"1\"\r\n" +
            "[HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run]\r\n\"x\"=\"calc.exe\"\r\n";

        RegistryImportPolicy.Validate(text).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsKeyOutsideTheManifestEntry()
    {
        var text = Header + "[HKEY_CURRENT_USER\\Control Panel\\Keyboard]\r\n\"a\"=\"b\"\r\n";

        RegistryImportPolicy.Validate(text, @"HKEY_CURRENT_USER\Control Panel\Mouse").IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsMissingHeaderAndEmptyFiles()
    {
        RegistryImportPolicy.Validate("[HKEY_CURRENT_USER\\Control Panel\\Mouse]\r\n").IsValid.Should().BeFalse();
        RegistryImportPolicy.Validate(Header).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("\"v\"=dword:1\r[HKEY_CURRENT_USER\\Software\\Evil]\r\n")]
    [InlineData("\"v\"=\"a\0b\"\r\n")]
    public void Validate_RejectsLoneCarriageReturnAndNul(string body)
    {
        var text = Header + "[HKEY_CURRENT_USER\\Control Panel\\Mouse]\r\n" + body;

        RegistryImportPolicy.Validate(text).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsKeyLineSmuggledAfterContinuation()
    {
        var text = Header + "[HKEY_CURRENT_USER\\Control Panel\\Mouse]\r\n\"v\"=hex:01,\\\r\n[HKEY_CURRENT_USER\\Software\\Evil]\r\n01\r\n";

        RegistryImportPolicy.Validate(text).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_AcceptsMultiLineHexValues_AndMixedCaseKeys()
    {
        var text = Header + "[hkey_current_user\\control panel\\mouse]\r\n\"v\"=hex:01,02,\\\r\n  03,04,\\\r\n  05\r\n\"w\"=\"x\"\r\n";

        var result = RegistryImportPolicy.Validate(text, @"HKEY_CURRENT_USER\Control Panel\Mouse");

        result.IsValid.Should().BeTrue(string.Join(";", result.Errors));
    }

    [Fact]
    public void IsMachineKey_IdentifiesHklm()
    {
        RegistryImportPolicy.IsMachineKey(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power").Should().BeTrue();
        RegistryImportPolicy.IsMachineKey(@"HKEY_CURRENT_USER\Control Panel\Mouse").Should().BeFalse();
    }
}

public class WindowsSettingsRestorePlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReStorePlanner_" + Guid.NewGuid().ToString("N"));

    public WindowsSettingsRestorePlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void WriteReg(string name, string key, string body = "\"a\"=\"b\"")
        => File.WriteAllText(Path.Combine(_dir, name), $"Windows Registry Editor Version 5.00\r\n\r\n[{key}]\r\n{body}\r\n", Encoding.Unicode);

    [Fact]
    public void BuildPlan_AcceptsValidFiles_SplitsUserAndMachine_AndRejectsTheRest()
    {
        const string mouse = @"HKEY_CURRENT_USER\Control Panel\Mouse";
        const string power = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power";
        const string run = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run";
        WriteReg("Mouse_Mouse.reg", mouse);
        WriteReg("System_Power.reg", power);
        WriteReg("Evil.reg", run);                                     // key not on allow-list
        WriteReg("Mismatch.reg", @"HKEY_CURRENT_USER\Control Panel\Keyboard"); // file content outside manifest key
        var manifest = new WindowsSettingsExport
        {
            ExportedFiles = new()
            {
                [mouse] = "Mouse_Mouse.reg",
                [power] = "System_Power.reg",
                [run] = "Evil.reg",
                [@"HKEY_CURRENT_USER\Control Panel\Desktop"] = "Mismatch.reg",
                [@"HKEY_CURRENT_USER\Control Panel\Cursors"] = @"..\outside.reg",
                [@"HKEY_CURRENT_USER\Control Panel\Keyboard"] = "missing.reg"
            }
        };

        var plan = WindowsSettingsRestorePlanner.BuildPlan(manifest, _dir);

        plan.Items.Select(i => (i.RegistryKey, i.RequiresElevation)).Should().BeEquivalentTo(
            new[] { (mouse, false), (power, true) });
        plan.Rejected.Should().HaveCount(4);
    }
}

public class WingetHelperTests
{
    [Theory]
    [InlineData("Microsoft.PowerToys", true)]
    [InlineData("Git.Git", true)]
    [InlineData("7zip.7zip", true)]
    [InlineData("Vendor.App_2+x-y", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("a;calc", false)]
    [InlineData("a\"--source evil", false)]
    [InlineData("ARP\\Machine\\X64\\Foo", false)]
    [InlineData("a&b", false)]
    [InlineData("Git.Git\n", false)]
    [InlineData("-Git.Git", false)]
    public void IsValidId_FollowsAllowedCharacterSet(string id, bool expected)
    {
        WingetHelper.IsValidId(id).Should().Be(expected);
    }

    [Fact]
    public void BuildInstallArguments_UsesExactIdAndKeepsIdAsOneArgument()
    {
        var args = WingetHelper.BuildInstallArguments("Git.Git");

        args.Should().StartWith(["install", "--exact", "--id", "Git.Git"]);
        args.Should().Contain(["--accept-package-agreements", "--accept-source-agreements"]);
    }

    [Fact]
    public void BuildInstallArguments_RejectsInvalidIds()
    {
        var act = () => WingetHelper.BuildInstallArguments("Git.Git --source evil");

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, WingetInstallOutcome.Installed, true)]
    [InlineData(unchecked((int)0x8A150061), WingetInstallOutcome.AlreadyInstalled, true)]
    [InlineData(unchecked((int)0x8A150109), WingetInstallOutcome.RebootRequired, true)]
    [InlineData(unchecked((int)0x8A150014), WingetInstallOutcome.NotFound, false)]
    [InlineData(1, WingetInstallOutcome.Failed, false)]
    public void DescribeInstallExitCode_MapsKnownCodes(int code, WingetInstallOutcome outcome, bool success)
    {
        var status = WingetHelper.DescribeInstallExitCode(code);

        status.Outcome.Should().Be(outcome);
        status.IsSuccess.Should().Be(success);
    }

    [Fact]
    public void ParseExportJson_ReadsIdsAndSources_AndDropsInvalidIds()
    {
        const string json = """
        {
          "$schema": "https://aka.ms/winget-packages.schema.2.0.json",
          "CreationDate": "2026-01-01T00:00:00.000-00:00",
          "Sources": [
            {
              "Packages": [
                { "PackageIdentifier": "Git.Git" },
                { "PackageIdentifier": "Bad Id; calc" },
                { "PackageIdentifier": "Microsoft.PowerToys" }
              ],
              "SourceDetails": { "Argument": "https://cdn.winget.microsoft.com/cache", "Identifier": "Microsoft.Winget.Source_8wekyb3d8bbwe", "Name": "winget", "Type": "Microsoft.PreIndexed.Package" }
            },
            {
              "Packages": [ { "PackageIdentifier": "9NBLGGH4NNS1" } ],
              "SourceDetails": { "Name": "msstore" }
            }
          ]
        }
        """;

        var entries = WingetHelper.ParseExportJson(json);

        entries.Should().BeEquivalentTo(new[]
        {
            new WingetExportEntry("Git.Git", "winget"),
            new WingetExportEntry("Microsoft.PowerToys", "winget"),
            new WingetExportEntry("9NBLGGH4NNS1", "msstore")
        });
    }

    [Fact]
    public void ParseExportJson_ReturnsEmpty_WhenNoSources()
    {
        WingetHelper.ParseExportJson("{}").Should().BeEmpty();
    }

    [Fact]
    public void ParseListOutput_VersionColumnStopsAtSource_WhenAvailableColumnIsAbsent()
    {
        const string output = "  \\\r   \r\nName       Id             Version Source\r\n------------------------------------\r\nGit        Git.Git        2.45.1  winget\r\nLocal App  ARP\\Machine\\X  1.0\r\n";

        var programs = WingetHelper.ParseListOutput(output);

        programs.Should().HaveCount(2);
        programs[0].Should().Match<InstalledProgram>(p => p.Name == "Git" && p.WingetId == "Git.Git" && p.Version == "2.45.1" && p.IsWingetAvailable);
        programs[1].WingetId.Should().BeEmpty();
        programs[1].IsWingetAvailable.Should().BeFalse();
    }

    [Fact]
    public void ApplyExportedIds_MarksExportedAndAddsLeftovers()
    {
        var programs = new List<InstalledProgram>
        {
            new() { Name = "Git", WingetId = "Git.Git", Source = "winget", IsWingetAvailable = true },
            new() { Name = "Odd", WingetId = "Odd.App", Source = "winget", IsWingetAvailable = true }
        };

        SystemProgramDiscovery.ApplyExportedIds(programs, [new("Git.Git", "winget"), new("Only.InExport", "winget")]);

        programs.Single(p => p.Name == "Git").IsWingetAvailable.Should().BeTrue();
        programs.Single(p => p.Name == "Odd").IsWingetAvailable.Should().BeFalse();
        programs.Should().Contain(p => p.WingetId == "Only.InExport" && p.IsWingetAvailable);
    }
}

public class ProgramMergerTests
{
    [Fact]
    public void CreateKey_NormalisesCaseWhitespacePunctuationAndTrademarks()
    {
        ProgramMerger.CreateKey("  Foo  Bar® ", "Contoso, Inc.").Should().Be(ProgramMerger.CreateKey("foo bar", "contoso inc"));
    }

    [Fact]
    public void CreateKey_DoesNotConfuseNameAndPublisherBoundary()
    {
        ProgramMerger.CreateKey("a_b", "c").Should().NotBe(ProgramMerger.CreateKey("a", "b_c"));
    }

    [Fact]
    public void Merge_DedupesWingetAndRegistryEntries_FillingGaps()
    {
        var winget = new[] { new InstalledProgram { Name = "Git", Version = "2.45", WingetId = "Git.Git", IsWingetAvailable = true, Source = "winget" } };
        var registry = new[]
        {
            new InstalledProgram { Name = "git", Publisher = "The Git Development Community", InstallLocation = @"C:\Git", Source = "registry" },
            new InstalledProgram { Name = "Git", Publisher = "The Git Development Community", UninstallString = "unins.exe", Source = "registry" },
            new InstalledProgram { Name = "Other", Publisher = "X", Source = "registry" }
        };

        var merged = ProgramMerger.Merge(winget, registry);

        merged.Should().HaveCount(2);
        var git = merged.Single(p => p.Name == "Git");
        git.WingetId.Should().Be("Git.Git");
        git.Publisher.Should().Be("The Git Development Community");
        git.InstallLocation.Should().Be(@"C:\Git");
        git.UninstallString.Should().Be("unins.exe");
    }

    [Fact]
    public void Merge_KeepsSameNameFromDifferentPublishersSeparate()
    {
        var merged = ProgramMerger.Merge([], new[]
        {
            new InstalledProgram { Name = "Setup", Publisher = "A" },
            new InstalledProgram { Name = "Setup", Publisher = "B" }
        });

        merged.Should().HaveCount(2);
    }
}

public class ProcessRunnerTests
{
    [NonWindowsFact]
    public async Task RunAsync_PassesArgumentsAsSeparateEntries_AndReturnsExitCode()
    {
        var result = await ProcessRunner.RunAsync("/bin/sh", ["-c", "printf '%s|' \"$0\" \"$1\"; exit 3", "a b;c", "d\"e"], TimeSpan.FromSeconds(10));

        result.ExitCode.Should().Be(3);
        result.StandardOutput.Should().Be("a b;c|d\"e|");
        result.TimedOut.Should().BeFalse();
    }

    [NonWindowsFact]
    public async Task RunAsync_KillsTheProcess_OnTimeout()
    {
        var result = await ProcessRunner.RunAsync("/bin/sh", ["-c", "sleep 30"], TimeSpan.FromMilliseconds(300));

        result.TimedOut.Should().BeTrue();
    }

    [NonWindowsFact]
    public async Task RunAsync_ThrowsOperationCanceled_WhenCancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var act = () => ProcessRunner.RunAsync("/bin/sh", ["-c", "sleep 30"], TimeSpan.FromSeconds(60), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

public class ScriptFileTests
{
    [Fact]
    public void EscapeSingleQuoted_DoublesAsciiAndTypographicQuotes()
    {
        ScriptFile.EscapeSingleQuoted("it's ‘x’").Should().Be("it''s ‘‘x’’");
    }

    [Fact]
    public async Task WriteAllTextAsync_WritesUtf8Bom()
    {
        var path = Path.Combine(Path.GetTempPath(), "ReStoreBom_" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            await ScriptFile.WriteAllTextAsync(path, "é");
            var bytes = await File.ReadAllBytesAsync(path);
            bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EnsureBom_AddsBomOnce_AndLeavesInvalidUtf8Alone()
    {
        var path = Path.Combine(Path.GetTempPath(), "ReStoreBom_" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes("é"));
            ScriptFile.EnsureBom(path);
            ScriptFile.EnsureBom(path);
            File.ReadAllBytes(path).Should().Equal(0xEF, 0xBB, 0xBF, 0xC3, 0xA9);

            File.WriteAllBytes(path, [0xE9]);
            ScriptFile.EnsureBom(path);
            File.ReadAllBytes(path).Should().Equal(0xE9);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

[Collection("GlobalState")]
public class SystemBackupResilienceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreSysResilience_" + Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = AppPaths.Root;
    private readonly TestLogger _logger = new();

    public SystemBackupResilienceTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot(_root);
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(_previousRoot);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static Mock<IConfigManager> Config(Action<Mock<IConfigManager>> setup)
    {
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.GlobalStorageType).Returns("global");
        config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig
        {
            ProgramsStorageType = "programs-storage",
            EnvironmentStorageType = "environment-storage",
            SettingsStorageType = "settings-storage"
        });
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        setup(config);
        return config;
    }

    [Fact]
    public async Task BackupSystemAsync_ContinuesPastFailingComponent_AndReportsEachResult()
    {
        var config = Config(c =>
        {
            c.Setup(x => x.CreateStorageAsync("programs-storage")).ThrowsAsync(new InvalidOperationException("programs boom"));
            c.Setup(x => x.CreateStorageAsync("environment-storage")).ThrowsAsync(new InvalidOperationException("env boom"));
            c.Setup(x => x.CreateStorageAsync("settings-storage")).ThrowsAsync(new InvalidOperationException("settings boom"));
        });
        var manager = new SystemBackupManager(_logger, config.Object, new Mock<SystemState>(_logger).Object);

        var act = () => manager.BackupSystemAsync();

        var thrown = (await act.Should().ThrowAsync<SystemBackupException>()).Which;
        thrown.Result.Components.Select(c => c.Component).Should().Equal("programs", "environment", "settings");
        thrown.Result.Components.Should().OnlyContain(c => !c.Succeeded);
        thrown.Message.Should().Contain("programs boom").And.Contain("env boom").And.Contain("settings boom");
        config.Verify(c => c.CreateStorageAsync("environment-storage"), Times.Once);
        config.Verify(c => c.CreateStorageAsync("settings-storage"), Times.Once);
    }

    [Fact]
    public async Task BackupSystemAsync_PropagatesCancellation_WithoutRunningFurtherComponents()
    {
        var config = Config(_ => { });
        var manager = new SystemBackupManager(_logger, config.Object, new Mock<SystemState>(_logger).Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => manager.BackupSystemAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        config.Verify(c => c.CreateStorageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BackupSystemAsync_TimesOutAComponent_AsAFailure()
    {
        var slowStorage = new TaskCompletionSource<IStorage>();
        var config = Config(c =>
        {
            c.Setup(x => x.CreateStorageAsync("programs-storage")).Returns(slowStorage.Task);
            c.Setup(x => x.CreateStorageAsync("environment-storage")).ThrowsAsync(new InvalidOperationException("env boom"));
            c.Setup(x => x.CreateStorageAsync("settings-storage")).ThrowsAsync(new InvalidOperationException("settings boom"));
        });
        var manager = new SystemBackupManager(_logger, config.Object, new Mock<SystemState>(_logger).Object);
        manager.ComponentTimeout = TimeSpan.FromMilliseconds(200);

        var act = () => manager.BackupSystemAsync();

        var thrown = (await act.Should().ThrowAsync<SystemBackupException>()).Which;
        thrown.Result.Components.Should().HaveCount(3);
        thrown.Result.Components[0].Error.Should().Contain("timed out");
        thrown.Result.Components[1].Error.Should().Contain("env boom");
    }

    [Fact]
    public async Task RestoreSystemAsync_UsesPrivateWorkDirectoryUnderAppPaths_AndRemovesIt()
    {
        var payload = Path.Combine(_root, "payload");
        Directory.CreateDirectory(payload);
        await File.WriteAllTextAsync(Path.Combine(payload, "restore_programs.ps1"), "Write-Host hi");
        await File.WriteAllTextAsync(Path.Combine(payload, "installed_programs.json"), "{}");
        var zip = Path.Combine(_root, "src.zip");
        ZipFile.CreateFromDirectory(payload, zip);

        string? downloadedTo = null;
        var storage = new Mock<IStorage>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, dest, _) =>
            {
                downloadedTo = dest;
                File.Copy(zip, dest, overwrite: true);
                return Task.CompletedTask;
            });
        var config = Config(_ => { });
        var manager = new SystemBackupManager(_logger, config.Object, new Mock<SystemState>(_logger).Object);

        var result = await manager.RestoreSystemAsync(storage.Object, "system_programs", "system_backups/programs/x.zip");

        downloadedTo.Should().StartWith(AppPaths.RestoreWorkDirectory);
        AppPaths.RestoreWorkDirectory.Should().StartWith(_root);
        Directory.GetDirectories(AppPaths.RestoreWorkDirectory).Should().BeEmpty();
        File.Exists(Path.Combine(result.ScriptsFolder!, "restore_programs.ps1")).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(result.ScriptsFolder!, "restore_programs.ps1")).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
    }

    [Fact]
    public async Task RestoreSystemAsync_Settings_NeverImportsDisallowedKeys_AndDoesNotRunScripts()
    {
        var payload = Path.Combine(_root, "payload-settings");
        Directory.CreateDirectory(payload);
        const string run = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run";
        await File.WriteAllTextAsync(Path.Combine(payload, "Evil_Run.reg"),
            $"Windows Registry Editor Version 5.00\r\n\r\n[{run}]\r\n\"x\"=\"calc.exe\"\r\n");
        await File.WriteAllTextAsync(Path.Combine(payload, "restore_windows_settings.ps1"), "throw 'must never run'");
        await File.WriteAllTextAsync(Path.Combine(payload, "settings_manifest.json"),
            $$"""{ "ExportedCategories": ["Evil"], "ExportedFiles": { "{{run.Replace("\\", "\\\\")}}": "Evil_Run.reg" } }""");
        var zip = Path.Combine(_root, "settings.zip");
        ZipFile.CreateFromDirectory(payload, zip);

        var storage = new Mock<IStorage>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((_, dest, _) => { File.Copy(zip, dest, true); return Task.CompletedTask; });
        var prompted = false;
        var options = new SystemRestoreOptions { Confirm = _ => { prompted = true; return Task.FromResult(true); } };
        var manager = new SystemBackupManager(_logger, Config(_ => { }).Object, new Mock<SystemState>(_logger).Object);

        var result = await manager.RestoreSystemAsync(storage.Object, "system_settings", "system_backups/settings/x.zip", options);

        prompted.Should().BeFalse("nothing importable remains after validation");
        result.Succeeded.Should().Be(0);
        result.Skipped.Should().Be(1);
        result.Messages.Should().Contain(m => m.Contains("allow-list"));
    }
}

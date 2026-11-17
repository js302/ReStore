using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Moq;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Units behind the operations commands: durations, the scheduled-task definition and parsing, the pause store and controller pickup.</summary>
public class OpsSupportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "restore-ops-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ---- durations -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("30m", 1800)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    [InlineData("1h30m", 5400)]
    [InlineData("45S", 45)]
    [InlineData("2w", 1209600)]
    [InlineData(" 7d ", 604800)]
    public void Durations_ParseShortForms(string text, int seconds)
    {
        CliDurations.TryParse(text, out var duration).Should().BeTrue();
        duration.Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0m")]
    [InlineData("-5m")]
    [InlineData("5")]
    [InlineData("m5")]
    [InlineData("1.5h")]
    [InlineData("soon")]
    [InlineData("1h 30m")]
    [InlineData("99999999d")]
    public void Durations_RejectEverythingElse(string text) => CliDurations.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void Durations_Format_IsShort()
    {
        CliDurations.Format(TimeSpan.FromDays(7)).Should().Be("7d");
        CliDurations.Format(TimeSpan.FromMinutes(90)).Should().Be("1h 30m");
        CliDurations.Format(TimeSpan.FromSeconds(20)).Should().Be("20s");
    }

    // ---- scheduled task definition ---------------------------------------------------------------------------------

    [Fact]
    public void TaskXml_RunsHiddenAsTheLoggedOnUser_WithoutAStoredPassword()
    {
        var xml = ScheduledTaskManager.BuildTaskXml(@"C:\Program Files\ReStore\restore.exe", 20, @"PC\me & co", new DateTime(2030, 1, 2, 3, 4, 5));

        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        doc.Descendants(ns + "Interval").Single().Value.Should().Be("PT20M");
        doc.Descendants(ns + "StartBoundary").Single().Value.Should().Be("2030-01-02T03:04:05");
        doc.Descendants(ns + "LogonType").Single().Value.Should().Be("InteractiveToken", "only when logged on: no password is stored");
        doc.Descendants(ns + "RunLevel").Single().Value.Should().Be("LeastPrivilege");
        doc.Descendants(ns + "UserId").Single().Value.Should().Be(@"PC\me & co");
        doc.Descendants(ns + "Hidden").Single().Value.Should().Be("true");
        doc.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value.Should().Be("false");
        doc.Descendants(ns + "StopIfGoingOnBatteries").Single().Value.Should().Be("false");
        doc.Descendants(ns + "MultipleInstancesPolicy").Single().Value.Should().Be("IgnoreNew");
        doc.Descendants(ns + "Command").Single().Value.Should().Be("\"C:\\Program Files\\ReStore\\restore.exe\"", "the path is quoted");
        doc.Descendants(ns + "Arguments").Single().Value.Should().Be("run-due --quiet");
        xml.Should().NotContain("<Password>");
    }

    [Fact]
    public void TaskXml_EscapesXmlInThePath_AndOmitsAnUnknownUser()
    {
        var xml = ScheduledTaskManager.BuildTaskXml(@"C:\a&b<c>\restore.exe", 15, null, DateTime.Now);

        XDocument.Parse(xml).Descendants(XName.Get("Command", "http://schemas.microsoft.com/windows/2004/02/mit/task")).Single().Value
            .Should().Be("\"C:\\a&b<c>\\restore.exe\"");
        xml.Should().NotContain("<UserId>");
    }

    [Fact]
    public void Arguments_AreTheSchtasksCommandLines()
    {
        ScheduledTaskManager.BuildCreateArguments(@"C:\t\x.xml").Should().Equal("/Create", "/TN", @"\ReStore\run-due", "/XML", @"C:\t\x.xml", "/F");
        ScheduledTaskManager.BuildDeleteArguments().Should().Equal("/Delete", "/TN", @"\ReStore\run-due", "/F");
        ScheduledTaskManager.BuildQueryXmlArguments().Should().Equal("/Query", "/TN", @"\ReStore\run-due", "/XML");
        ScheduledTaskManager.BuildQueryVerboseArguments().Should().Equal("/Query", "/TN", @"\ReStore\run-due", "/V", "/FO", "CSV");
    }

    [Fact]
    public async Task Install_WritesUtf16WithBom_AndRemovesTheTempFile()
    {
        byte[]? written = null;
        string? file = null;
        var manager = new ScheduledTaskManager((_, arguments, _, _) =>
        {
            file = arguments[4];
            written = File.ReadAllBytes(file);
            return Task.FromResult(new ProcessResult(0, "SUCCESS", "", false));
        }, () => @"PC\me");

        var error = await manager.InstallAsync(@"C:\r\restore.exe", 15, CancellationToken.None);

        error.Should().BeNull();
        written.Should().StartWith(new byte[] { 0xFF, 0xFE }, "schtasks expects UTF-16 LE with a byte order mark");
        Encoding.Unicode.GetString(written!).Should().Contain("PT15M").And.Contain(@"PC\me");
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task Install_ReportsTimeoutsAndFailures()
    {
        (await new ScheduledTaskManager((_, _, _, _) => Task.FromResult(new ProcessResult(-1, "", "", true))).InstallAsync("x", 15, CancellationToken.None))
            .Should().Contain("did not finish");
        (await new ScheduledTaskManager((_, _, _, _) => Task.FromResult(new ProcessResult(5, "", "ERROR: denied", false))).InstallAsync("x", 15, CancellationToken.None))
            .Should().Contain("5").And.Contain("denied");
    }

    // ---- status parsing --------------------------------------------------------------------------------------------

    private const string Definition = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><TimeTrigger><Repetition><Interval>PT1H30M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition></TimeTrigger></Triggers>
          <Settings><Enabled>false</Enabled></Settings>
          <Actions><Exec><Command>C:\r\restore.exe</Command><Arguments>run-due --quiet</Arguments></Exec></Actions>
        </Task>
        """;

    [Fact]
    public void Status_ReadsTheDefinition_AndALocalizedCsvByPosition()
    {
        // German headers: the titles differ, the column order does not.
        var csv = "\"Hostname\",\"Aufgabenname\",\"Nächste Laufzeit\",\"Status\",\"Anmeldemodus\",\"Letzte Laufzeit\",\"Letztes Ergebnis\",\"Autor\",\"Auszuführende Aufgabe\"\r\n"
            + "\"PC\",\"\\ReStore\\run-due\",\"02.01.2030 10:15:00\",\"Bereit\",\"Nur interaktiv\",\"02.01.2030 10:00:00\",\"267011\",\"me\",\"x\"";

        var status = ScheduledTaskManager.ParseStatus(Definition, csv);

        status.Installed.Should().BeTrue();
        status.Enabled.Should().BeFalse();
        status.IntervalMinutes.Should().Be(90);
        status.Executable.Should().Be(@"C:\r\restore.exe");
        status.Arguments.Should().Be("run-due --quiet");
        status.State.Should().Be("Bereit");
        status.LastRun.Should().Be("02.01.2030 10:00:00");
        status.NextRun.Should().Be("02.01.2030 10:15:00");
        status.LastResult.Should().Be(267011);
        status.LastResultText.Should().Be("the task has not run yet");
    }

    [Fact]
    public void Status_ToleratesGarbage()
    {
        var status = ScheduledTaskManager.ParseStatus("not xml", "\"a\"\r\n");

        status.Installed.Should().BeTrue("schtasks answered, so the task exists");
        status.IntervalMinutes.Should().BeNull();
        status.LastResult.Should().BeNull();
        ScheduledTaskManager.ParseStatus(Definition, null).LastRun.Should().BeNull();
    }

    [Theory]
    [InlineData("PT15M", 15)]
    [InlineData("PT1H", 60)]
    [InlineData("P1D", 1440)]
    [InlineData("nonsense", null)]
    [InlineData("", null)]
    public void IsoDurations_AreReadInMinutes(string iso, int? minutes) => ScheduledTaskManager.ParseIsoMinutes(iso).Should().Be(minutes);

    [Fact]
    public void Csv_HandlesQuotesCommasAndNewlines()
    {
        var rows = ScheduledTaskManager.ParseCsv("\"a\",\"b,c\",\"say \"\"hi\"\"\"\r\n\"1\",\"\",\"3\"\r\n");

        rows.Should().HaveCount(2);
        rows[0].Should().Equal("a", "b,c", "say \"hi\"");
        rows[1].Should().Equal("1", "", "3");
    }

    [Fact]
    public async Task Query_TreatsAFailingXmlQueryAsNotInstalled_AndSkipsTheSecondCall()
    {
        var calls = 0;
        var manager = new ScheduledTaskManager((_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessResult(1, "", "ERROR: not found", false));
        });

        var status = await manager.QueryAsync(CancellationToken.None);

        status.Installed.Should().BeFalse();
        calls.Should().Be(1);
    }

    // ---- password warning ------------------------------------------------------------------------------------------

    [Fact]
    public void PasswordWarning_OnlyWhenEncryptedAndNoSource()
    {
        var plain = new Mock<IConfigManager>();
        plain.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        var encrypted = new Mock<IConfigManager>();
        encrypted.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = true });
        Directory.CreateDirectory(_dir);
        var remembered = Path.Combine(_dir, "remembered-password.bin");
        var passwordFile = Path.Combine(_dir, "pw.txt");

        OpsCommandHelpers.PasswordWarning(plain.Object, _ => null, remembered).Should().BeNull();
        OpsCommandHelpers.PasswordWarning(encrypted.Object, _ => null, remembered).Should().Contain("RESTORE_ENCRYPTION_PASSWORD");
        OpsCommandHelpers.PasswordWarning(encrypted.Object, name => name == "RESTORE_ENCRYPTION_PASSWORD" ? "secret" : null, remembered).Should().BeNull();
        OpsCommandHelpers.PasswordWarning(encrypted.Object, name => name == "RESTORE_ENCRYPTION_PASSWORD_FILE" ? passwordFile : null, remembered)
            .Should().NotBeNull("the file the variable names does not exist");
        File.WriteAllText(passwordFile, "secret");
        OpsCommandHelpers.PasswordWarning(encrypted.Object, name => name == "RESTORE_ENCRYPTION_PASSWORD_FILE" ? passwordFile : null, remembered).Should().BeNull();
        File.WriteAllBytes(remembered, [1]);
        OpsCommandHelpers.PasswordWarning(encrypted.Object, _ => null, remembered).Should().BeNull();
    }

    // ---- the rule that excludes a folder ---------------------------------------------------------------------------

    private static Mock<IConfigManager> ExcludingConfig(string[] excludedPaths, string[]? globs = null, string[]? presets = null)
    {
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.ExcludedPaths).Returns([.. excludedPaths]);
        config.SetupGet(c => c.ExcludedPatterns).Returns([]);
        config.SetupGet(c => c.ExcludeGlobs).Returns([.. globs ?? []]);
        config.SetupGet(c => c.ExcludePresets).Returns([.. presets ?? []]);
        config.SetupGet(c => c.WatchDirectories).Returns([]);
        config.SetupGet(c => c.StorageSources).Returns([]);
        config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        return config;
    }

    [Fact]
    public void FindExcludingRule_NamesTheExcludedPathThatContainsTheFolder()
    {
        Environment.SetEnvironmentVariable("RESTORE_TEST_EXCLUDED", Path.Combine(_dir, "tmp"));
        try
        {
            var service = new FileSelectionService(new TestLogger(), ExcludingConfig(["%RESTORE_TEST_EXCLUDED%"]).Object);

            service.FindExcludingRule(Path.Combine(_dir, "tmp")).Should().Be("excludedPaths entry '%RESTORE_TEST_EXCLUDED%'");
            service.FindExcludingRule(Path.Combine(_dir, "tmp", "deep", "er")).Should().Contain("%RESTORE_TEST_EXCLUDED%");
            service.FindExcludingRule(Path.Combine(_dir, "tmp2")).Should().BeNull("a sibling with the same prefix is not inside");
            service.FindExcludingRule(_dir).Should().BeNull("the parent of an excluded folder is not excluded");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESTORE_TEST_EXCLUDED", null);
        }
    }

    [Fact]
    public void FindExcludingRule_NamesGlobsAndPresets()
    {
        var service = new FileSelectionService(new TestLogger(), ExcludingConfig([], ["**/secret-*/"], ["dev"]).Object);

        service.FindExcludingRule(Path.Combine(_dir, "secret-stuff")).Should().Be("excludeGlobs pattern '**/secret-*/'");
        service.FindExcludingRule(Path.Combine(_dir, "node_modules")).Should().Be("pattern 'node_modules/' of excludePresets 'dev'");
        service.FindExcludingRule(Path.Combine(_dir, "documents")).Should().BeNull();
    }

    [Fact]
    public void LiftExcludedPaths_LetsAnExplicitFolderThroughButKeepsInnerExclusions()
    {
        var excluded = Path.Combine(_dir, "excluded");
        var project = Path.Combine(excluded, "project");
        Directory.CreateDirectory(Path.Combine(project, "inner"));
        File.WriteAllText(Path.Combine(project, "a.txt"), "a");
        File.WriteAllText(Path.Combine(project, "inner", "b.txt"), "b");
        var service = new FileSelectionService(new TestLogger(), ExcludingConfig([excluded, Path.Combine(project, "inner")]).Object);

        service.ScanFiles([project]).Should().BeEmpty("the folder lies inside an excluded path");

        service.LiftExcludedPathsContaining = project;
        service.ScanFiles([project]).Select(f => Path.GetFileName(f.Path)).Should().Equal(["a.txt"], "an excluded folder inside the backed-up one stays excluded");
    }

    // ---- pause store and controller pickup -------------------------------------------------------------------------

    private ProtectionPauseStore NewStore() => new(() => Path.Combine(_dir, "pause.json"));

    [Fact]
    public void Store_LoadActive_IgnoresAnExpiredPause()
    {
        var store = NewStore();
        var now = DateTime.UtcNow;

        store.LoadActive(now).Should().BeNull();
        store.Save(now.AddMinutes(-1));
        store.LoadActive(now).Should().BeNull("a pause that ended is not in force even though the file is still there");
        store.Save(now.AddMinutes(5));
        store.LoadActive(now).Should().NotBeNull();
        store.Save(ProtectionPause.Indefinite);
        store.LoadActive(now.AddYears(50)).Should().Be(ProtectionPause.Indefinite);
    }

    private sealed class Worker : IProtectionWorker
    {
        public int Started;
        public int Stopped;
        public Task StartAsync() { Started++; return Task.CompletedTask; }
        public Task StopAsync() { Stopped++; return Task.CompletedTask; }
    }

    private sealed class Harness
    {
        public readonly List<Worker> Watchers = [];
        public readonly ProtectionController Controller;

        public Harness(ProtectionPauseStore store)
        {
            Controller = new ProtectionController(
                () => { var w = new Worker(); Watchers.Add(w); return w; }, () => new Worker(), new TestLogger(), pauseStore: store);
        }
    }

    [Fact]
    public async Task Controller_PicksUpAPauseAndAResumeWrittenByAnotherProcess()
    {
        var store = NewStore();
        var h = new Harness(store);
        await h.Controller.StartAsync();
        h.Controller.State.Should().Be(ProtectionState.Running);

        // "Another process" (restore pause) writes the file through its own store instance.
        NewStore().Save(DateTime.UtcNow.AddHours(1));
        await h.Controller.SyncPauseFromStoreAsync();

        h.Controller.State.Should().Be(ProtectionState.Stopped);
        h.Controller.PausedUntilUtc.Should().NotBeNull();
        h.Watchers[0].Stopped.Should().Be(1);

        // Syncing again with the same pause changes nothing.
        await h.Controller.SyncPauseFromStoreAsync();
        h.Watchers.Should().HaveCount(1);

        NewStore().Clear();
        await h.Controller.SyncPauseFromStoreAsync();

        h.Controller.PausedUntilUtc.Should().BeNull();
        h.Controller.State.Should().Be(ProtectionState.Running, "restore resume starts protection again");
        h.Watchers.Should().HaveCount(2);
    }

    [Fact]
    public async Task Controller_PicksUpAChangedPauseEnd()
    {
        var store = NewStore();
        var h = new Harness(store);
        await h.Controller.PauseFor(TimeSpan.FromHours(1));
        var first = h.Controller.PausedUntilUtc!.Value;

        var later = DateTime.UtcNow.AddHours(5);
        NewStore().Save(later);
        await h.Controller.SyncPauseFromStoreAsync();

        h.Controller.PausedUntilUtc!.Value.Should().BeCloseTo(later, TimeSpan.FromSeconds(1));
        h.Controller.PausedUntilUtc.Should().NotBe(first);
    }

    [Fact]
    public async Task Controller_OwnPause_IsNotRestartedByTheSync()
    {
        var store = NewStore();
        var h = new Harness(store);
        await h.Controller.StartAsync();
        await h.Controller.PauseFor(TimeSpan.FromHours(1));

        await h.Controller.SyncPauseFromStoreAsync();
        await h.Controller.SyncPauseFromStoreAsync();

        h.Controller.PausedUntilUtc.Should().NotBeNull();
        h.Controller.State.Should().Be(ProtectionState.Stopped);
        h.Watchers.Should().HaveCount(1, "nothing was started or stopped again");
    }

    [Fact]
    public async Task Controller_ResumeFromAnotherProcess_DoesNotOverrideAUserStop()
    {
        var store = NewStore();
        var h = new Harness(store);
        await h.Controller.StartAsync();
        await h.Controller.StopAsync();
        NewStore().Save(ProtectionPause.Indefinite);
        await h.Controller.SyncPauseFromStoreAsync();
        h.Controller.PausedUntilUtc.Should().NotBeNull();

        NewStore().Clear();
        await h.Controller.SyncPauseFromStoreAsync();

        h.Controller.PausedUntilUtc.Should().BeNull();
        h.Controller.State.Should().Be(ProtectionState.Stopped, "the user stopped protection; resume only ends the pause");
    }

    [Fact]
    public async Task Controller_WithoutAStore_IgnoresTheSync()
    {
        var controller = new ProtectionController(() => new Worker(), () => new Worker(), new TestLogger());
        await controller.StartAsync();

        await controller.SyncPauseFromStoreAsync();

        controller.State.Should().Be(ProtectionState.Running);
    }

    [Fact]
    public void ParseStatus_TreatsAMissingEnabledElementAsEnabled_LikeTheTaskSchedulerExportsIt()
    {
        // Shape of a real `schtasks /Query /XML` export of an enabled task: <Enabled> is left out because true is the default.
        const string exported = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Repetition><Interval>PT30M</Interval></Repetition></LogonTrigger></Triggers>
              <Settings><Hidden>true</Hidden><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries></Settings>
              <Actions Context="Author"><Exec><Command>C:\\Tools\\restore.exe</Command><Arguments>run-due --quiet</Arguments></Exec></Actions>
            </Task>
            """;

        ScheduledTaskManager.ParseStatus(exported, null).Enabled.Should().BeTrue();
        ScheduledTaskManager.ParseStatus(exported.Replace("<Hidden>true</Hidden>", "<Enabled>false</Enabled><Hidden>true</Hidden>"), null).Enabled.Should().BeFalse();
    }
}

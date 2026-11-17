using System.Text.Json;

namespace ReStore.E2E;

/// <summary>`run-due` (what the scheduled task, cron or an agent calls): it honours schedules, the pause file and the backup window.</summary>
[Trait("Category", "E2E")]
public class ScheduleTests
{
    private static int SnapshotManifests(Workspace ws) =>
        Directory.Exists(ws.StoragePath("snapshots"))
            ? Directory.EnumerateFiles(ws.StoragePath("snapshots"), "*.manifest.json", SearchOption.AllDirectories).Count()
            : 0;

    [Fact]
    public void RunDue_BacksUpWhatIsDue_AndNotAgainUntilTheScheduleSaysSo()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.RunJson("config", "set-schedule", ws.Src, "every 5m").Ok();

        var next = ws.RunJson("schedule", "next").Ok();
        var target = next.GetProperty("targets").EnumerateArray().First(t => t.GetProperty("kind").GetString() == "folder");
        Assert.Equal("due", target.GetProperty("state").GetString());
        Assert.Equal("every 5m", target.GetProperty("schedule").GetProperty("expression").GetString());

        var first = ws.RunJson("run-due").Ok();
        Assert.False(first.GetProperty("nothingDue").GetBoolean());
        Assert.Equal(1, first.GetProperty("backups").GetProperty("succeeded").GetInt32());
        Assert.Equal(1, SnapshotManifests(ws));

        // Five minutes have not passed: nothing is due.
        var second = ws.RunJson("run-due").Ok();
        Assert.True(second.GetProperty("nothingDue").GetBoolean());
        Assert.Equal(0, second.GetProperty("backups").GetProperty("succeeded").GetInt32());
        Assert.Equal(1, SnapshotManifests(ws));

        ws.RunJson("verify", ws.HeadPath()).Ok();
    }

    [Fact]
    public void RunDue_HonoursThePauseFile_AlsoWhenAnotherProcessWritesOrRemovesIt()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.RunJson("config", "set-schedule", ws.Src, "every 5m").Ok();

        ws.RunJson("pause", "--for", "2h").Ok();
        var pauseFile = Path.Combine(ws.Home, "state", "protection_pause.json");
        Assert.True(File.Exists(pauseFile), "the pause is stored in state/protection_pause.json");
        Assert.True(ws.RunJson("schedule", "next").Ok().GetProperty("paused").GetBoolean());

        var paused = ws.RunJson("run-due").Ok();
        Assert.True(paused.GetProperty("paused").GetBoolean());
        Assert.True(paused.GetProperty("nothingDue").GetBoolean());
        Assert.Equal(0, SnapshotManifests(ws));

        // The app (or another CLI) resumes by removing the file: the very next run-due sees it.
        var content = File.ReadAllText(pauseFile);
        File.Delete(pauseFile);
        var resumed = ws.RunJson("run-due").Ok();
        Assert.False(resumed.GetProperty("paused").GetBoolean());
        Assert.Equal(1, resumed.GetProperty("backups").GetProperty("succeeded").GetInt32());

        // Put the file back (another process pauses): seen by the next process; `resume` ends it.
        File.WriteAllText(pauseFile, content);
        Assert.True(ws.RunJson("pause", "status").Ok().GetProperty("paused").GetBoolean());
        ws.RunJson("resume").Ok();
        Assert.False(ws.RunJson("pause", "status").Ok().GetProperty("paused").GetBoolean());
    }

    [Fact]
    public void RunDue_DefersWork_WhileTheBackupWindowIsClosed()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.RunJson("config", "set-schedule", ws.Src, "every 5m").Ok();

        // A one-hour window that opens two hours from now (local time, as the scheduler reads it): closed now.
        var open = DateTime.Now.AddHours(2);
        var close = open.AddHours(1);
        ws.RunJson("config", "set-window", open.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), close.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Ok();

        var deferred = ws.RunJson("run-due").Ok();
        Assert.True(deferred.GetProperty("backupWindow").GetProperty("closed").GetBoolean(), deferred.ToString());
        Assert.True(deferred.GetProperty("deferred").GetInt32() + deferred.GetProperty("backupWindow").GetProperty("deferred").GetInt32() >= 1, deferred.ToString());
        Assert.Equal(0, SnapshotManifests(ws));

        // Manual backups are not subject to the window.
        ws.Backup();
        Assert.Equal(1, SnapshotManifests(ws));

        ws.RunJson("config", "clear-window").Ok();
        Assert.False(ws.RunJson("schedule", "next").Ok().GetProperty("backupWindow").GetProperty("active").GetBoolean());
    }

    [Fact]
    public void RunDue_Quiet_PrintsNothingOnStdout_AndRecordsItsOutcome()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.RunJson("config", "set-schedule", ws.Src, "every 5m").Ok();

        // Exactly how the scheduled task calls it.
        var result = ws.Run("run-due", "--quiet");
        Assert.True(result.ExitCode == 0 && result.Stdout.Trim().Length == 0, result.Describe());
        Assert.Equal(1, SnapshotManifests(ws));

        var record = Path.Combine(ws.Home, "state", "run-due-last.json");
        Assert.True(File.Exists(record), "run-due records its own outcome in state/run-due-last.json");
        using var document = JsonDocument.Parse(File.ReadAllText(record));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }
}

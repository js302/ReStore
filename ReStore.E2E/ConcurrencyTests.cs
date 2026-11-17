using System.Diagnostics;

namespace ReStore.E2E;

/// <summary>Several real ReStore processes against one repository: the cross-process lock, `--wait`, exit 5 and what readers may do meanwhile.</summary>
[Trait("Category", "E2E")]
public class ConcurrencyTests
{
    [Fact]
    public void Wait_RunsOnceTheOtherProcessLetsGo()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();

        var held = ws.HoldRepositoryLock();
        using var waiting = ws.Start("backup", ws.Src, "--wait", "60", "--json", "--non-interactive");
        waiting.WaitForStderr("Waiting for the repository");
        Assert.False(waiting.HasExited, "it must still be waiting while the lock is held");

        held.Dispose(); // the "other process" finishes
        var result = waiting.WaitForExit(TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0 && result.Json().GetProperty("ok").GetBoolean(), result.Describe());
        ws.RunJson("verify", ws.HeadPath()).Ok();
    }

    [Fact]
    public void Wait_GivesUpWithExitFive_WhenTheOtherProcessNeverLetsGo()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        using var held = ws.HoldRepositoryLock();

        var clock = Stopwatch.StartNew();
        var result = ws.RunJson("backup", ws.Src, "--wait", "2");
        result.Failed(5, "busy");
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 30);
    }

    [Fact]
    public void ARunningBackup_HoldsTheRepository_ASecondOneFailsFast_AThirdWaitsAndSucceeds_ReadersAreNotBlocked()
    {
        TimingRetry.Run(ws =>
        {
            var other = Path.Combine(ws.Root, "other-source");
            TestFiles.CreateSampleTree(other);
            TestFiles.CreateSampleTree(ws.Src);
            ws.Init();
            ws.Backup(); // a snapshot readers can look at meanwhile
            var head = ws.HeadPath();
            BigData.CopyInto(Path.Combine(ws.Src, "big"));

            using var first = ws.Start("backup", ws.Src, "--json", "--non-interactive");
            first.WaitForStderr(@"\[Uploading\] 2/\d+");

            // Second process: `--wait 0` fails at once with the documented busy envelope.
            var second = ws.RunJson("backup", other, "--wait", "0");
            if (second.ExitCode == 0)
            {
                return false; // the first backup was already over
            }

            second.Failed(5, "busy");

            // Third: waits (and says so on stderr), then succeeds after the first has finished.
            using var third = ws.Start("backup", other, "--wait", "120", "--json", "--non-interactive");
            // On a fast machine the first backup may already be over when the third starts, so it never has to wait. That is fine
            // here: waiting itself is pinned deterministically by the Wait_* tests (which hold the lock from the test process); this
            // scenario is about a REAL running backup as the holder, and the third must succeed either way.
            third.TryWaitForStderr("Waiting for the repository|\"ok\"");

            // A reader is not blocked by a running backup.
            var reader = ws.RunJson("verify", head);
            Assert.True(reader.ExitCode == 0, "a reader must not wait for a running backup.\n" + reader.Describe());

            var firstResult = first.WaitForExit(TimeSpan.FromSeconds(120));
            Assert.True(firstResult.ExitCode == 0, firstResult.Describe());
            var thirdResult = third.WaitForExit(TimeSpan.FromSeconds(120));
            Assert.True(thirdResult.ExitCode == 0 && thirdResult.Json().GetProperty("ok").GetBoolean(), thirdResult.Describe());

            // Both repositories' HEADs verify.
            ws.RunJson("verify", head, "--mode", "full").Ok();
            ws.RunJson("verify", ws.HeadPath(other), "--mode", "full").Ok();
            return true;
        });
    }

    [Fact]
    public void ManyBackupsAtOnce_AreSerialised_AndEveryOneSucceedsOrSaysBusy()
    {
        using var ws = Workspace.Create();
        var folders = Enumerable.Range(0, 4).Select(i => Path.Combine(ws.Root, $"folder-{i}")).ToArray();
        foreach (var (folder, index) in folders.Select((f, i) => (f, i)))
        {
            TestFiles.CreateSampleTree(folder);
            TestFiles.WriteText(Path.Combine(folder, "unique.txt"), $"folder {index}\n");
        }

        ws.Init(watch: folders[0]);
        var running = folders.Select(f => ws.Start("backup", f, "--wait", "120", "--json", "--non-interactive")).ToList();
        try
        {
            foreach (var process in running)
            {
                var result = process.WaitForExit(TimeSpan.FromSeconds(180));
                Assert.True(result.ExitCode is 0 or 5, "documented outcomes are 0 and 5 (busy).\n" + result.Describe());
                result.Json();
            }
        }
        finally
        {
            running.ForEach(r => r.Dispose());
        }

        foreach (var folder in folders)
        {
            ws.RunJson("verify", ws.HeadPath(folder), "--mode", "full").Ok();
        }

        // The shared state file is intact: every folder is listed.
        var groups = ws.RunJson("snapshots").Ok().GetProperty("groups").GetArrayLength();
        Assert.Equal(folders.Length, groups);
        Assert.True(ws.RunJson("doctor").ExitCode is 0 or 1);
    }

    /// <summary>
    /// Real bug found by the E2E suite: a process that loaded state while another process was creating the state file for the first time
    /// read the other's temp file (`system_state.json.&lt;guid&gt;.tmp`) as a "leftover of an interrupted save", the file was renamed under it,
    /// and the load failed with exit 1 ("Could not load the system state").
    /// </summary>
    [Fact]
    public void FirstEverStateFile_CreatedByManyProcessesAtOnce_NeverFailsTheLoad()
    {
        for (var round = 0; round < 4; round++)
        {
            using var ws = Workspace.Create();
            var folders = Enumerable.Range(0, 6).Select(i => Path.Combine(ws.Root, $"tiny-{i}")).ToArray();
            foreach (var (folder, index) in folders.Select((f, i) => (f, i)))
            {
                TestFiles.WriteText(Path.Combine(folder, "file.txt"), $"folder {index} round {round}\n");
            }

            ws.Init(watch: folders[0]);
            Assert.False(File.Exists(Path.Combine(ws.Home, "state", "system_state.json")), "the state file must not exist yet");

            var running = folders.Select(f => ws.Start("backup", f, "--wait", "120", "--json", "--non-interactive")).ToList();
            try
            {
                foreach (var process in running)
                {
                    var result = process.WaitForExit(TimeSpan.FromSeconds(180));
                    Assert.DoesNotContain("Could not load the system state", result.Stderr);
                    Assert.True(result.ExitCode == 0, $"round {round}\n{result.Describe()}");
                }
            }
            finally
            {
                running.ForEach(r => r.Dispose());
            }

            Assert.Equal(folders.Length, ws.RunJson("snapshots").Ok().GetProperty("groups").GetArrayLength());
        }
    }
}

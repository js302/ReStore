using System.Text.Json;

namespace ReStore.E2E;

/// <summary>
/// A backup that is cancelled (Ctrl+C / Ctrl+Break) or killed outright must leave the repository as it was: HEAD unchanged and valid, every
/// referenced chunk present, the next backup and a repair fine. The backup is made long enough to interrupt by adding 32 x 12 MiB of random
/// data to a folder that already has a small snapshot; the signal is sent as soon as stderr shows the upload under way.
/// </summary>
[Trait("Category", "E2E")]
public class InterruptionTests
{
    private const string UploadUnderWay = @"\[Uploading\] 3/\d+";

    private sealed record Baseline(string Group, string Head, string HeadFile, string CopyOfSource, string ManifestPath);

    /// <summary>A completed first backup of a small tree; afterwards the source also holds the big files, so the next backup has real work to do.</summary>
    private static Baseline PrepareBaseline(Workspace ws)
    {
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.Backup();
        var group = ws.GroupKey();
        var copy = Path.Combine(ws.Root, "baseline-source");
        foreach (var file in Directory.EnumerateFiles(ws.Src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(ws.Src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var baseline = new Baseline(group, $"snapshots/{group}/HEAD", ws.ReadHeadFile(group), copy, ws.LatestManifestPath());
        BigData.CopyInto(Path.Combine(ws.Src, "big"));
        return baseline;
    }

    private static RunningCli StartBigBackup(Workspace ws, RunOptions? options = null) =>
        ws.Start(options, "backup", ws.Src, "--json", "--non-interactive");

    /// <summary>
    /// A kill that lands after the commit point (HEAD already moved) is a legitimate outcome, but then there is nothing left to test: whatever HEAD
    /// names must still be a complete, verifiable snapshot; the caller retries the scenario.
    /// </summary>
    private static bool HeadMovedAfterAll(Workspace ws, Baseline baseline)
    {
        if (ws.ReadHeadFile(baseline.Group) == baseline.HeadFile)
        {
            return false;
        }

        Assert.True(ws.RunJson("verify", baseline.Head, "--mode", "full").Ok().GetProperty("valid").GetBoolean(), "HEAD moved and must name a complete snapshot");
        return true;
    }

    private static void AssertRepositoryIntact(Workspace ws, Baseline baseline)
    {
        // HEAD still names the baseline snapshot and the snapshot is complete and verifiable.
        Assert.Equal(baseline.HeadFile, ws.ReadHeadFile(baseline.Group));
        var verify = ws.RunJson("verify", baseline.Head, "--mode", "full").Ok();
        Assert.True(verify.GetProperty("valid").GetBoolean());
        Assert.Equal(0, verify.GetProperty("missingChunks").GetInt32());

        var snapshots = ws.RunJson("snapshots").Ok();
        var group = Assert.Single(snapshots.GetProperty("groups").EnumerateArray());
        Assert.Single(group.GetProperty("snapshots").EnumerateArray());

        var restored = Path.Combine(ws.Root, "restored-baseline-" + Guid.NewGuid().ToString("N")[..6]);
        ws.RunJson("restore", baseline.Head, restored).Ok();
        TestFiles.AssertSameTree(baseline.CopyOfSource, restored);
    }

    private static void AssertNextBackupAndRepairConverge(Workspace ws, Baseline baseline)
    {
        // repair --dry-run reports without deleting; chunks of the interrupted run are orphans (or still inside the grace period).
        var dry = ws.RunJson("repair", "--dry-run").Ok();
        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, dry.GetProperty("chunksDeleted").GetInt32());
        Assert.True(dry.GetProperty("orphanChunks").GetInt32() + dry.GetProperty("chunksKeptInGrace").GetInt32() > 0, "the interrupted backup left chunks behind; repair --dry-run must report them.\n" + dry);
        var referenced = dry.GetProperty("chunksReferenced").GetInt32();
        Assert.True(referenced > 0);

        // A real repair never removes a referenced chunk.
        var repaired = ws.RunJson("repair", "--yes").Ok();
        Assert.True(repaired.GetProperty("chunksReferenced").GetInt32() == referenced, repaired.ToString());
        AssertRepositoryIntact(ws, baseline);

        // A fault-free backup afterwards completes, and everything verifies.
        var next = ws.Backup();
        Assert.NotEqual(baseline.ManifestPath, next.GetProperty("manifestPath").GetString());
        ws.RunJson("verify", baseline.Head, "--mode", "full").Ok();
        ws.RunJson("verify", baseline.Head.Replace("/HEAD", "/" + Path.GetFileName(baseline.ManifestPath)), "--mode", "full").Ok();
    }

    private static bool InterruptBackup(Workspace ws, Baseline baseline, RunOptions? options)
    {
        using var running = StartBigBackup(ws, options);
        running.WaitForStderr(UploadUnderWay);
        running.Interrupt();
        var result = running.WaitForExit(TimeSpan.FromSeconds(60));
        if (result.ExitCode == 0 || HeadMovedAfterAll(ws, baseline))
        {
            return false; // finished (or committed) before the signal was delivered
        }

        // The documented cancelled envelope: exit 130, one JSON document, error.code "cancelled".
        result.Failed(130, "cancelled");
        AssertRepositoryIntact(ws, baseline);
        AssertNextBackupAndRepairConverge(ws, baseline);
        return true;
    }

    /// <summary>Linux/macOS: <c>kill -INT</c>, which is what Ctrl+C delivers.</summary>
    [NonWindowsFact]
    public void Sigint_DuringBackup_ExitsWith130_AndLeavesTheRepositoryIntact()
    {
        TimingRetry.Run(ws => InterruptBackup(ws, PrepareBaseline(ws), options: null));
    }

    /// <summary>
    /// Windows: the child is created in its own process group (CreateProcess + CREATE_NEW_PROCESS_GROUP) and sent
    /// <c>GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT)</c>. .NET raises Console.CancelKeyPress for Ctrl+Break too, so the CLI must treat
    /// it exactly like Ctrl+C (Task Scheduler, agent harnesses). A failure here is a real gap in the CLI.
    /// </summary>
    [WindowsFact]
    public void CtrlBreak_DuringBackup_ExitsWith130_AndLeavesTheRepositoryIntact()
    {
        TimingRetry.Run(ws => InterruptBackup(ws, PrepareBaseline(ws), new RunOptions { NewProcessGroup = true }));
    }

    [Fact]
    public void KillMinusNine_DuringBackup_LeavesHeadValid_RepairReportsOrphans_AndNextBackupSucceeds()
    {
        TimingRetry.Run(ws =>
        {
            var baseline = PrepareBaseline(ws);
            using (var running = StartBigBackup(ws))
            {
                running.WaitForStderr(UploadUnderWay);
                running.KillTree();
                var result = running.WaitForExit(TimeSpan.FromSeconds(60));
                if (result.ExitCode == 0)
                {
                    return false; // finished before the kill
                }
            }

            if (HeadMovedAfterAll(ws, baseline))
            {
                return false; // killed after the commit point
            }

            // No cleanup ran: the OS released the repository lock, HEAD is the baseline, nothing referenced is missing.
            AssertRepositoryIntact(ws, baseline);
            AssertNextBackupAndRepairConverge(ws, baseline);
            return true;
        });
    }

    [Fact]
    public void KillMinusNine_DuringAnEncryptedBackup_IsEquallyHarmless()
    {
        TimingRetry.Run(ws =>
        {
            TestFiles.CreateSampleTree(ws.Src);
            ws.Init();
            ws.EnableEncryption();
            ws.Backup(encrypted: true);
            var group = ws.GroupKey(encrypted: true);
            var head = $"snapshots/{group}/HEAD";
            var headFile = ws.ReadHeadFile(group);
            BigData.CopyInto(Path.Combine(ws.Src, "big"));

            using (var running = ws.Start(new RunOptions { Stdin = Workspace.TestPassword + "\n" }, "backup", ws.Src, "--password-stdin", "--json", "--non-interactive"))
            {
                running.WaitForStderr(UploadUnderWay, TimeSpan.FromSeconds(90));
                running.KillTree();
                if (running.WaitForExit(TimeSpan.FromSeconds(60)).ExitCode == 0)
                {
                    return false; // finished before the kill
                }
            }

            if (ws.ReadHeadFile(group) != headFile)
            {
                // Killed after the commit point: HEAD must still name a complete snapshot; retry the scenario for the interesting case.
                Assert.True(ws.RunJsonWithPassword("verify", head, "--mode", "full").Ok().GetProperty("valid").GetBoolean());
                return false;
            }

            Assert.Equal(headFile, ws.ReadHeadFile(group));
            Assert.True(ws.RunJsonWithPassword("verify", head, "--mode", "full").Ok().GetProperty("valid").GetBoolean());
            ws.RunJsonWithPassword("repair", "--yes").Ok();
            Assert.True(ws.RunJsonWithPassword("verify", head, "--mode", "full").Ok().GetProperty("valid").GetBoolean());
            ws.Backup(encrypted: true);
            return true;
        });
    }
}

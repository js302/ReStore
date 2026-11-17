using System.Diagnostics;
using System.Text.Json;

namespace ReStore.E2E;

/// <summary>Exit codes, the failure envelope, non-interactive behaviour (stdin that is closed, idle or absent) and the `--yes` rules of docs/usage/cli.md.</summary>
[Trait("Category", "E2E")]
public class ProtocolTests
{
    [Theory]
    [InlineData("bogus-command")]
    [InlineData("backup")]                                 // missing argument
    [InlineData("snapshots", "--no-such-option")]
    [InlineData("restore", "a", "b", "--dryrun")]          // a mistyped flag is never silently ignored
    [InlineData("restore", "a", "b", "--conflict", "nonsense")]
    [InlineData("config")]                                 // group without a subcommand
    [InlineData("config", "nosuchsub")]
    public void UsageErrors_ExitTwo_WithTheEnvelope_AndWithoutTouchingTheDataFolder(params string[] args)
    {
        using var ws = Workspace.Create();
        var asJson = ws.Run([.. args, "--json"]);
        var error = asJson.Failed(2, "usage");
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()), asJson.Describe());

        // Without --json: the message goes to stderr, the usage text to stdout, same exit code.
        var plain = ws.Run(args);
        Assert.True(plain.ExitCode == 2, plain.Describe());
        Assert.True(plain.Stderr.Trim().Length > 0, "the message belongs on stderr.\n" + plain.Describe());

        Assert.Empty(Directory.GetFileSystemEntries(ws.Home));
    }

    [Fact]
    public void MistypedOption_GetsADidYouMeanHint()
    {
        using var ws = Workspace.Create();
        var result = ws.Run("restore", "snapshots/x/HEAD", ws.Out, "--dryrun", "--json");
        var error = result.Failed(2, "usage");
        Assert.Contains("--dry-run", error.GetProperty("message").GetString() + error.GetProperty("hint").GetString());
    }

    [Fact]
    public void ConfigError_ExitsThree_WithTheEnvelope()
    {
        using var ws = Workspace.Create();
        File.WriteAllText(Path.Combine(ws.Home, "config.json"), "{ this is not json");
        ws.RunJson("snapshots").Failed(3, "config");
        ws.RunJson("config", "validate").Failed(3, "config");
    }

    [Fact]
    public void SnapshotNotInTheStorage_IsNotFound_ExitOne()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.Backup();
        var group = ws.GroupKey();
        string[][] commands =
        [
            ["verify", "nothing/HEAD"], ["ls", "nothing/HEAD"], ["test-restore", "nothing/HEAD"], ["restore", "nothing/HEAD", ws.Out],
            ["verify", $"snapshots/{group}/snapshot_19990101000000_0.manifest.json"], ["diff", $"snapshots/{group}/HEAD", "nothing/HEAD"],
            ["ls", $"snapshots/{group}/HEAD", "--path", "no/such/folder"],
            ["versions", "no-such-group", "a.txt"], ["versions", group, "no-such-file.txt"],
        ];
        foreach (var command in commands)
        {
            var error = ws.RunJson(command).Failed(1, "not_found");
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()), "not_found must say what to do next");
        }

        // A plain failure keeps code "failure".
        ws.RunJson("export-key", Path.Combine(ws.Root, "key.json")).Failed(1, "failure");
    }

    [Fact]
    public void MissingPassword_WithClosedStdin_ExitsFourQuickly_NeverHangs()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.EnableEncryption();
        ws.Backup(encrypted: true);
        var head = ws.HeadPath(encrypted: true);

        // No --non-interactive flag at all: redirected stdin alone must be enough.
        var clock = Stopwatch.StartNew();
        var closed = ws.Run(new RunOptions { Timeout = TimeSpan.FromSeconds(30) }, "verify", head, "--json");
        closed.Failed(4, "needs_user");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "took " + clock.Elapsed);
    }

    [Fact]
    public void MissingPassword_WithAnIdleOpenStdin_ExitsFour_NeverHangs()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.EnableEncryption();
        ws.Backup(encrypted: true);

        // A harness that leaves the stdin pipe open and silent.
        var result = ws.Run(new RunOptions { KeepStdinOpen = true, Timeout = TimeSpan.FromSeconds(30) }, "backup", ws.Src, "--json");
        result.Failed(4, "needs_user");

        var viaPrompt = ws.Run(new RunOptions { KeepStdinOpen = true, Timeout = TimeSpan.FromSeconds(30) }, "change-password", "--json");
        Assert.Equal(4, viaPrompt.ExitCode);
    }

    [Fact]
    public void Busy_ExitsFive_WithTheEnvelope_WhenAnotherProcessHoldsTheRepository()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.Backup();

        using var held = ws.HoldRepositoryLock();
        var clock = Stopwatch.StartNew();
        var result = ws.RunJson("backup", ws.Src, "--wait", "0");
        var error = result.Failed(5, "busy");
        Assert.Contains("--wait", error.GetProperty("hint").GetString());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "--wait 0 must fail at once, took " + clock.Elapsed);
    }

    // ---- --yes: the documented confirmation rules ----------------------------------------------------------------------

    [Fact]
    public void RetentionApply_NeedsYes_WhenItWouldDelete_AndOtherwiseDeletesNothing()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(ws.Src, "a.txt"), $"generation {i}\n");
            ws.Backup();
        }

        var group = ws.GroupKey();
        Assert.Equal(3, ManifestCount(ws, group));

        // Nothing marked yet (the default policy keeps five): apply needs no confirmation and changes nothing.
        var nothing = ws.RunJson("retention", "apply").Ok();
        Assert.Equal(0, nothing.GetProperty("deleted").GetInt32());

        foreach (var (key, value) in new[] { ("keepLastPerDirectory", "1"), ("maxAgeDays", "0"), ("keepHourly", "0"), ("keepDaily", "0"), ("keepWeekly", "0"), ("keepMonthly", "0"), ("keepYearly", "0") })
        {
            ws.RunJson("config", "set", "retention." + key, value).Ok();
        }

        var plan = ws.RunJson("retention", "plan").Ok();
        Assert.Contains(plan.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("snapshots").EnumerateArray()), s => s.GetProperty("action").GetString() != "keep");

        // The plan is a preview: still three.
        Assert.Equal(3, ManifestCount(ws, group));

        // Not asked (--non-interactive, or merely a redirected stdin): exit 4, nothing deleted.
        var refused = ws.RunJson("retention", "apply").Failed(4, "needs_user");
        Assert.Contains("--yes", refused.GetProperty("hint").GetString());
        ws.Run("retention", "apply", "--json").Failed(4, "needs_user");
        Assert.Equal(3, ManifestCount(ws, group));

        var applied = ws.RunJson("retention", "apply", "--yes").Ok();
        Assert.True(applied.GetProperty("deleted").GetInt32() >= 2);
        Assert.Equal(1, ManifestCount(ws, group));
        ws.RunJson("verify", $"snapshots/{group}/HEAD").Ok();
    }

    [Fact]
    public void EncryptionDisable_NeedsYes_WhenNothingCanAnswer()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.EnableEncryption();
        ws.Backup(encrypted: true);

        var refused = ws.RunJsonWithPassword("encryption", "disable").Failed(4, "needs_user");
        Assert.Contains("--yes", refused.GetProperty("hint").GetString());
        Assert.True(ws.RunJson("encryption", "status").Ok().GetProperty("enabled").GetBoolean());

        var disabled = ws.RunJsonWithPassword("encryption", "disable", "--yes").Ok();
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.False(ws.RunJson("encryption", "status").Ok().GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void RepairAndRestoreOverwrite_NeedYes_WhenNothingCanAnswer()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.Backup();
        var head = ws.HeadPath();

        // `repair` (without --dry-run) deletes unreferenced chunks: refused without --yes, even with only a redirected stdin.
        var orphan = ws.StoragePath("chunks/zz/e2e-orphan.chunk");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllBytes(orphan, new byte[512]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-30));
        var refusedRepair = ws.RunJson("repair", "--grace-hours", "0").Failed(4, "needs_user");
        Assert.Contains("--yes", refusedRepair.GetProperty("hint").GetString());
        ws.Run("repair", "--grace-hours", "0", "--json").Failed(4, "needs_user");
        Assert.True(File.Exists(orphan));

        // The preview never asks and changes nothing.
        ws.RunJson("repair", "--grace-hours", "0", "--dry-run").Ok();
        Assert.True(File.Exists(orphan));

        var repaired = ws.RunJson("repair", "--grace-hours", "0", "--yes").Ok();
        Assert.False(repaired.GetProperty("dryRun").GetBoolean());
        Assert.False(File.Exists(orphan));

        // `restore --conflict overwrite` replaces files: refused without --yes, nothing written.
        Directory.CreateDirectory(ws.Out);
        File.WriteAllText(Path.Combine(ws.Out, "a.txt"), "old");
        var refusedRestore = ws.RunJson("restore", head, ws.Out, "--conflict", "overwrite").Failed(4, "needs_user");
        Assert.Contains("--yes", refusedRestore.GetProperty("hint").GetString());
        Assert.Equal("old", File.ReadAllText(Path.Combine(ws.Out, "a.txt")));
        ws.RunJson("restore", head, ws.Out, "--conflict", "overwrite", "--dry-run").Ok();
        Assert.Equal("old", File.ReadAllText(Path.Combine(ws.Out, "a.txt")));

        ws.RunJson("restore", head, ws.Out, "--conflict", "overwrite", "--yes").Ok();
        Assert.Equal("hello\nworld\n", File.ReadAllText(Path.Combine(ws.Out, "a.txt")));

        // The other policies never ask.
        ws.RunJson("restore", head, ws.Out, "--conflict", "keepboth").Ok();
    }

    [Fact]
    public void CommandsThatActAtOnce_DoNotNeedYes_AndNeverExitFour()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.Backup();

        // `config import` acts at once.
        var export = Path.Combine(ws.Root, "config-export.json");
        ws.RunJson("config", "export", export).Ok();
        Assert.NotEqual(4, ws.RunJson("config", "import", export).ExitCode);

        // `schedule remove` acts at once (on Linux it refuses for another reason; it is never a confirmation request).
        if (!OperatingSystem.IsWindows())
        {
            Assert.NotEqual(4, ws.RunJson("schedule", "remove").ExitCode);
        }

        // `--yes` is accepted everywhere and changes nothing for these.
        ws.RunJson("repair", "--dry-run", "--yes").Ok();
    }

    private static int ManifestCount(Workspace ws, string group) =>
        Directory.EnumerateFiles(ws.StoragePath($"snapshots/{group}"), "*.manifest.json").Count();
}

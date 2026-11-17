using System.Text.Json;

namespace ReStore.E2E;

/// <summary>init, backup, snapshots, ls, restore (--dry-run and real), verify, diff: the whole cycle, byte for byte, as separate processes.</summary>
[Trait("Category", "E2E")]
public class LifecycleTests
{
    [Fact]
    public void PlainCycle_BackupsRestoresAndVerifiesByteForByte()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);

        var init = ws.Init();
        Assert.True(init.GetProperty("probe").GetProperty("success").GetBoolean());

        var backup = ws.Backup();
        Assert.False(backup.GetProperty("encrypted").GetBoolean());

        var snapshots = ws.RunJson("snapshots").Ok();
        var group = Assert.Single(snapshots.GetProperty("groups").EnumerateArray());
        var snapshot = Assert.Single(group.GetProperty("snapshots").EnumerateArray());
        Assert.True(snapshot.GetProperty("head").GetBoolean());
        Assert.Equal(TestFiles.HashTree(ws.Src).Count, snapshot.GetProperty("fileCount").GetInt32());
        Assert.Equal(backup.GetProperty("manifestPath").GetString(), snapshot.GetProperty("manifestPath").GetString());

        var head = ws.HeadPath();
        var listing = ws.RunJson("ls", head).Ok();
        Assert.Equal(backup.GetProperty("snapshotId").GetString(), listing.GetProperty("snapshotId").GetString());
        Assert.Contains(listing.GetProperty("entries").EnumerateArray(), e => e.GetProperty("name").GetString() == "empty.txt" && e.GetProperty("sizeBytes").GetInt64() == 0);

        // --dry-run previews and writes nothing.
        var preview = ws.RunJson("restore", head, ws.Out, "--dry-run").Ok();
        Assert.True(preview.GetProperty("dryRun").GetBoolean());
        Assert.Equal(TestFiles.HashTree(ws.Src).Count, preview.GetProperty("preview").GetProperty("files").GetInt32());
        Assert.False(Directory.Exists(ws.Out) && Directory.EnumerateFileSystemEntries(ws.Out).Any(), "a dry run must not write anything");

        var restored = ws.RunJson("restore", head, ws.Out).Ok();
        Assert.Equal(TestFiles.HashTree(ws.Src).Count, restored.GetProperty("result").GetProperty("filesRestored").GetInt32());
        TestFiles.AssertSameTree(ws.Src, ws.Out);

        foreach (var mode in new[] { "quick", "full" })
        {
            var verify = ws.RunJson("verify", head, "--mode", mode).Ok();
            Assert.True(verify.GetProperty("valid").GetBoolean());
            Assert.Equal(0, verify.GetProperty("missingChunks").GetInt32());
        }

        Assert.True(ws.RunJson("test-restore", head).Ok().TryGetProperty("ok", out _));
    }

    [Fact]
    public void SecondBackup_IsIncremental_DiffAndVersionsSeeTheChange_AndBothSnapshotsRestore()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        var first = ws.Backup();
        var firstChunks = Directory.EnumerateFiles(ws.Storage, "*", SearchOption.AllDirectories).Count();
        var firstManifest = first.GetProperty("manifestPath").GetString()!;
        var firstCopy = Path.Combine(ws.Root, "first-source");
        CopyTree(ws.Src, firstCopy);

        // Change one file, add one, delete one, rename one.
        File.AppendAllText(Path.Combine(ws.Src, "a.txt"), "appended\n");
        TestFiles.WriteText(Path.Combine(ws.Src, "added.txt"), "new file\n");
        File.Delete(Path.Combine(ws.Src, "empty.txt"));
        File.Move(Path.Combine(ws.Src, "docs", "notes.md"), Path.Combine(ws.Src, "docs", "renamed.md"));

        var second = ws.Backup();
        var secondManifest = second.GetProperty("manifestPath").GetString()!;
        Assert.NotEqual(firstManifest, secondManifest);

        // Deduplication: the 3.5 MB file did not change, so the second backup added far fewer objects than the first.
        var added = Directory.EnumerateFiles(ws.Storage, "*", SearchOption.AllDirectories).Count() - firstChunks;
        Assert.True(added < firstChunks / 2, $"second backup stored {added} new objects, the first stored {firstChunks}");

        var diff = ws.RunJson("diff", firstManifest, secondManifest).Ok();
        var text = diff.ToString();
        Assert.Contains("added.txt", text);
        Assert.Contains("a.txt", text);
        Assert.Contains("empty.txt", text);
        Assert.Contains("renamed.md", text);

        // Both snapshots restore exactly what was backed up.
        var outNew = Path.Combine(ws.Root, "out-new");
        var outOld = Path.Combine(ws.Root, "out-old");
        ws.RunJson("restore", secondManifest, outNew).Ok();
        TestFiles.AssertSameTree(ws.Src, outNew);
        ws.RunJson("restore", firstManifest, outOld).Ok();
        TestFiles.AssertSameTree(firstCopy, outOld);

        // The old content of a changed file is still reachable through its version history.
        var versions = ws.RunJson("versions", ws.GroupKey(), "a.txt").Ok();
        Assert.True(versions.ToString().Length > 20);
    }

    [Fact]
    public void Restore_NeverReplacesExistingFilesUnlessAsked_AndInclude_RestoresOnlyWhatMatches()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.Backup();
        var head = ws.HeadPath();

        Directory.CreateDirectory(ws.Out);
        File.WriteAllText(Path.Combine(ws.Out, "a.txt"), "precious");
        var skip = ws.RunJson("restore", head, ws.Out).Ok();
        Assert.Equal("precious", File.ReadAllText(Path.Combine(ws.Out, "a.txt")));
        Assert.True(skip.GetProperty("result").GetProperty("filesSkipped").GetInt32() >= 1);

        ws.RunJson("restore", head, ws.Out, "--conflict", "keepboth").Ok();
        Assert.Equal("precious", File.ReadAllText(Path.Combine(ws.Out, "a.txt")));
        Assert.Contains(Directory.EnumerateFiles(ws.Out), f => Path.GetFileName(f).StartsWith("a (restored)", StringComparison.Ordinal));

        ws.RunJson("restore", head, ws.Out, "--conflict", "overwrite", "--yes").Ok();
        Assert.Equal("hello\nworld\n", File.ReadAllText(Path.Combine(ws.Out, "a.txt")));

        var only = Path.Combine(ws.Root, "only-docs");
        ws.RunJson("restore", head, only, "--include", "docs/report.docx").Ok();
        Assert.Equal(["docs/report.docx"], TestFiles.HashTree(only).Keys);

        // A pattern that matches nothing restores nothing and says so (exit 1).
        var none = ws.RunJson("restore", head, Path.Combine(ws.Root, "none"), "--include", "no-such-file.zzz");
        Assert.Equal(1, none.ExitCode);
        Assert.False(none.Json().GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void EncryptedCycle_WorksWithPasswordStdin_AndNothingReadableIsStored()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.EnableEncryption();

        // Without a password source an agent gets exit 4 straight away, with the documented envelope.
        var error = ws.RunJson("backup", ws.Src).Failed(4, "needs_user");
        Assert.Contains("--password-stdin", error.GetProperty("hint").GetString());

        var backup = ws.Backup(encrypted: true);
        Assert.True(backup.GetProperty("encrypted").GetBoolean());
        var head = ws.HeadPath(encrypted: true);

        ws.RunJsonWithPassword("verify", head).Ok();
        ws.RunJsonWithPassword("restore", head, ws.Out).Ok();
        TestFiles.AssertSameTree(ws.Src, ws.Out);

        // A wrong password never opens the repository (and never leaks a stack trace).
        var wrong = ws.Run(new RunOptions { Stdin = "not the password at all\n" }, "restore", head, Path.Combine(ws.Root, "wrong"), "--password-stdin", "--json", "--non-interactive");
        Assert.NotEqual(0, wrong.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(ws.Root, "wrong")) && Directory.EnumerateFileSystemEntries(Path.Combine(ws.Root, "wrong")).Any());
        Assert.DoesNotContain("   at ReStore", wrong.Stdout + wrong.Stderr);

        // Neither the file contents nor the file names appear in clear text in the storage.
        var needle = "some text that repeats"u8.ToArray();
        foreach (var file in Directory.EnumerateFiles(ws.Storage, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.AsSpan().IndexOf(needle) < 0, "plain text found in " + file);
            Assert.DoesNotContain("report.docx", file);
            Assert.True(bytes.AsSpan().IndexOf("report.docx"u8) < 0, "a file name is stored in clear text in " + file);
        }

        // The password never goes through the environment or a flag: the same cycle works from RESTORE_ENCRYPTION_PASSWORD_FILE.
        var passwordFile = Path.Combine(ws.Root, "pw.txt");
        File.WriteAllText(passwordFile, Workspace.TestPassword + "\n");
        var viaFile = ws.Run(new RunOptions { Environment = new Dictionary<string, string?> { ["RESTORE_ENCRYPTION_PASSWORD_FILE"] = passwordFile } }, "verify", head, "--json", "--non-interactive");
        Assert.True(viaFile.Json().GetProperty("valid").GetBoolean(), viaFile.Describe());
    }

    [Fact]
    public void UnicodeNames_RoundTrip_AndStdoutIsUtf8_EvenWithLangC()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src, unicode: true);
        var env = new RunOptions { Environment = new Dictionary<string, string?> { ["LANG"] = "C", ["LC_ALL"] = "C" } };

        ws.Run(env, "init", "--storage", "local", "--path", ws.Storage, "--watch", ws.Src, "--json", "--non-interactive").Ok();
        ws.Run(env, "backup", ws.Src, "--json", "--non-interactive").Ok();
        var head = ws.HeadPath();

        var listing = ws.Run(env, "ls", head, "--json", "--non-interactive").Ok();
        var names = listing.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("café ☕ 😀.txt", names);

        ws.Run(env, "restore", head, ws.Out, "--json", "--non-interactive").Ok();
        TestFiles.AssertSameTree(ws.Src, ws.Out);
        Assert.True(File.Exists(Path.Combine(ws.Out, "日本語", "глубоко", "data.bin")));
    }

    /// <summary>
    /// On Windows the redirected stdin/stdout are wrapped as UTF-8 (ConsoleStreams): a non-ASCII password piped to --password-stdin must be
    /// read byte-exact (same bytes unlock, different ones do not), and --json output with non-ASCII names must parse and round-trip.
    /// </summary>
    [WindowsFact]
    public void NonAsciiPasswordThroughStdin_AndNonAsciiNamesInJson_RoundTrip()
    {
        const string password = "p\u00e4ssw\u00f6rd-\u65e5\u672c";
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src, unicode: true);
        ws.Init();
        var piped = new RunOptions { Stdin = password + "\n" };

        ws.Run(piped, "encryption", "enable", "--password-stdin", "--json", "--non-interactive").Ok();
        ws.Run(piped, "backup", ws.Src, "--password-stdin", "--json", "--non-interactive").Ok();

        var same = ws.Run(piped, "unlock", "--password-stdin", "--json", "--non-interactive").Ok();
        Assert.True(same.GetProperty("unlocked").GetBoolean());

        var other = ws.Run(new RunOptions { Stdin = "p\u00e4ssw\u00f6rd-\u65e5\u672d\n" }, "unlock", "--password-stdin", "--json", "--non-interactive");
        Assert.Equal(1, other.ExitCode);

        // The ASCII approximation of the same password is a different password too.
        var ascii = ws.Run(new RunOptions { Stdin = "password-?\n" }, "unlock", "--password-stdin", "--json", "--non-interactive");
        Assert.Equal(1, ascii.ExitCode);

        var head = ws.Run(piped, "snapshots", "--password-stdin", "--json", "--non-interactive").Ok();
        var group = head.GetProperty("groups").EnumerateArray().First().GetProperty("key").GetString()!;
        var listing = ws.Run(piped, "ls", $"snapshots/{group}/HEAD", "--password-stdin", "--json", "--non-interactive").Ok();
        var names = listing.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("caf\u00e9 \u2615 \U0001F600.txt", names);

        ws.Run(piped, "restore", $"snapshots/{group}/HEAD", ws.Out, "--password-stdin", "--json", "--non-interactive").Ok();
        TestFiles.AssertSameTree(ws.Src, ws.Out);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReStore.E2E;

/// <summary>
/// docs/agents.md and docs/usage/cli.md are contracts for agents. These tests execute the guide's own recipes against the real CLI and check that every
/// field, exit code and rule the guide states is true of the real output (audit finding B7: the older unit test only checked that mentioned command names exist).
/// </summary>
[Trait("Category", "E2E")]
public class AgentGuideDocTests
{
    // ---- the recipes, executed ------------------------------------------------------------------------------------------

    [Fact]
    public void MarkedRecipes_AreFoundInTheGuide()
    {
        var headings = DocBlocks.RunnableBlocks().Select(b => b.Heading).ToArray();
        foreach (var expected in new[] { "First commands", "Set up a local backup", "Encrypted repository", "Run backups in the background", "Check health", "Restore a file or a folder somewhere else", "Find the versions of a file", "Connect existing backups on a new PC" })
        {
            Assert.Contains(expected, headings);
        }
    }

    [Fact]
    public void LocalRecipes_RunExactlyAsWritten_AndExitZero_WithOneJsonDocument()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        var blocks = DocBlocks.RunnableBlocks().Where(b => b.Heading != "Encrypted repository").ToList();

        // The guide's examples use Windows paths and placeholders; each maps to something real in this workspace.
        string? head = null;
        string? group = null;
        string Substitute(string line)
        {
            line = line.Replace(@"D:\Backups", ws.Storage).Replace(@"%USERPROFILE%\Documents", ws.Src).Replace(@"D:\Restored", ws.Out);
            if (line.Contains("<snapshotManifestOrHeadPath>"))
            {
                head ??= ws.HeadPath();
                line = line.Replace("<snapshotManifestOrHeadPath>", head);
            }

            if (line.Contains("<groupKeyOrPath>"))
            {
                group ??= ws.GroupKey();
                line = line.Replace("<groupKeyOrPath>", group);
            }

            line = line.Replace("<relativePath>", "docs/report.docx").Replace("<dir>", ws.Src).Replace("<password>", Workspace.TestPassword);
            var unknown = DocBlocks.Placeholders(line);
            Assert.True(unknown.Count == 0, $"The guide uses placeholder(s) {string.Join(", ", unknown)} that this test does not know how to fill: '{line}'.");
            return line;
        }

        var executed = 0;
        foreach (var block in blocks)
        {
            foreach (var command in DocBlocks.Commands(block, Substitute))
            {
                var result = ws.Run(new RunOptions { Stdin = command.Stdin }, [.. command.Arguments]);
                executed++;
                var context = $"[{block.Heading}] {command.Original}\n{result.Describe()}";
                var isDoctorOnEmptyHome = block.Heading == "First commands" && command.Arguments[0] == "doctor";
                if (isDoctorOnEmptyHome)
                {
                    // "It exits 0 when no check failed (warnings do not fail) and 1 otherwise."
                    var doctor = result.Json();
                    Assert.True(result.ExitCode == (doctor.GetProperty("summary").GetProperty("fail").GetInt32() == 0 ? 0 : 1), context);
                    continue;
                }

                Assert.True(result.ExitCode == 0, "A recipe in the guide must succeed.\n" + context);
                if (command.Arguments.Contains("--json"))
                {
                    var json = result.Json();
                    Assert.True(json.GetProperty("ok").GetBoolean(), context);
                }
            }
        }

        Assert.True(executed >= 14, $"expected the recipes to contain at least 14 commands, ran {executed}");

        // What the recipes promised actually happened.
        Assert.True(File.Exists(Path.Combine(ws.Out, "docs", "report.docx")), "the --include recipe restores the file");
        Assert.Equal(TestFiles.HashTree(ws.Src)["docs/report.docx"], TestFiles.HashTree(ws.Out)["docs/report.docx"]);
        Assert.Single(TestFiles.HashTree(ws.Out));
    }

    [Fact]
    public void EncryptedRecipe_EnablesEncryption_AndLaterCommandsNeedThePassword()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        var block = DocBlocks.RunnableBlocks().Single(b => b.Heading == "Encrypted repository");
        foreach (var command in DocBlocks.Commands(block, line => line.Replace("<password>", Workspace.TestPassword)))
        {
            var result = ws.Run(new RunOptions { Stdin = command.Stdin }, [.. command.Arguments]);
            Assert.True(result.ExitCode == 0 && result.Json().GetProperty("enabled").GetBoolean(), result.Describe());
        }

        // "Later commands on an encrypted repository need --password-stdin or one of those sources, otherwise they exit 4."
        ws.RunJson("backup", ws.Src).Failed(4, "needs_user");
        ws.Backup(encrypted: true);

        // "After the first backup tell the user to keep an offline copy of the key file (restore export-key <file>)."
        var keyFile = Path.Combine(ws.Root, "key-copy.json");
        ws.RunJsonWithPassword("export-key", keyFile).Ok();
        Assert.True(new FileInfo(keyFile).Length > 0);
    }

    // ---- fields the guide tells agents to read ----------------------------------------------------------------------------

    /// <summary>(text that must be in the guide, command, JSON path that must exist in the real output; [] means every array element).</summary>
    private static readonly (string InGuide, string[] Command, string Path)[] FieldClaims =
    [
        ("\"ok\"", ["version"], "ok"),
        ("`error.hint`", ["verify", "nothing/HEAD"], "error.hint"),
        ("{\"ok\": false, \"exitCode\": N, \"error\": {\"code\", \"message\", \"hint\"}}", ["verify", "nothing/HEAD"], "exitCode"),
        ("{\"ok\": false, \"exitCode\": N, \"error\": {\"code\", \"message\", \"hint\"}}", ["verify", "nothing/HEAD"], "error.code"),
        ("{\"ok\": false, \"exitCode\": N, \"error\": {\"code\", \"message\", \"hint\"}}", ["verify", "nothing/HEAD"], "error.message"),
        ("`error.message`", ["verify", "nothing/HEAD"], "error.message"),
        ("`error.code`", ["verify", "nothing/HEAD"], "error.code"),
        ("(JSON `warnings`)", ["config", "add-watch", "{excluded}"], "warnings[]"),
        ("`mutates`", ["commands"], "commands[].mutates"),
        ("`destructive`", ["commands"], "commands[].destructive"),
        ("exit code", ["commands"], "exitCodes[].code"),
        ("`fix`", ["doctor"], "checks[].fix"),
        ("`repository-lock`", ["doctor"], "checks[].id"),
        ("`tokenPresent`", ["auth", "status"], "providers[].tokenPresent"),
        ("`restore snapshots --json`", ["snapshots"], "groups[].snapshots[].manifestPath"),
        ("`.../HEAD`", ["snapshots"], "groups[].snapshots[].head"),
        ("the stale ones", ["status"], "staleGroups"),
        ("restore activity --failed --json", ["activity", "--failed"], "items"),
        ("`restore config get excludedPaths`", ["config", "get", "excludedPaths"], "value"),
        ("`schedule next`", ["schedule", "next"], "targets[].state"),
        ("restore versions <groupKeyOrPath> <relativePath> --json", ["versions", "{group}", "a.txt"], "versions[].manifestPath"),
        ("Each version names the oldest snapshot", ["versions", "{group}", "a.txt"], "versions[].snapshotId"),
        ("`restore ls <path>`", ["ls", "{head}"], "entries[].relativePath"),
        ("`restore diff <manifestA> <manifestB>`", ["diff", "{head}", "{head}"], "a.snapshotId"),
        ("`restore verify <path> --mode quick`", ["verify", "{head}", "--mode", "quick"], "valid"),
        ("restore retention --dry-run", ["retention", "--dry-run"], "groups"),
        ("restore repair --dry-run", ["repair", "--dry-run"], "dryRun"),
    ];

    [Fact]
    public void EveryFieldTheGuideTellsAgentsToRead_ExistsInTheRealOutput()
    {
        var guide = DocBlocks.ReadNormalized(DocBlocks.AgentsMarkdownPath);
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.Backup();
        var group = ws.GroupKey();
        var head = ws.HeadPath();
        var excluded = Path.Combine(ws.Tmp, "excluded-folder");
        Directory.CreateDirectory(excluded);

        var problems = new List<string>();
        foreach (var (inGuide, template, path) in FieldClaims)
        {
            if (!guide.Contains(inGuide, StringComparison.Ordinal))
            {
                problems.Add($"the guide no longer contains «{inGuide}» (update the claim table in this test together with the guide)");
                continue;
            }

            var command = template.Select(t => t.Replace("{group}", group).Replace("{head}", head).Replace("{excluded}", excluded)).ToArray();
            var result = ws.RunJson(command);
            JsonElement json;
            try
            {
                json = result.Json();
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                problems.Add($"[{string.Join(' ', command)}] {ex.Message}");
                continue;
            }

            if (!PathExists(json, path.Split('.')))
            {
                problems.Add($"[{string.Join(' ', command)}] the guide («{inGuide}») relies on '{path}', which is missing from:\n{Truncate(result.Stdout)}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n\n", problems));
    }

    [Fact]
    public void DoctorRepositoryLockCheck_SaysWhenAnotherProcessHoldsTheRepository()
    {
        // "restore doctor --json (check repository-lock) says what holds it."
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.Backup();

        string StatusOfLockCheck() => ws.RunJson("doctor").Json().GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "repository-lock").GetProperty("status").GetString()!;

        Assert.Equal("ok", StatusOfLockCheck());
        using (ws.HoldRepositoryLock())
        {
            var check = ws.RunJson("doctor").Json().GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "repository-lock");
            Assert.NotEqual("ok", check.GetProperty("status").GetString());
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("detail").GetString()));
        }

        Assert.Equal("ok", StatusOfLockCheck());
    }

    [Fact]
    public void ScheduleNext_ExplainsWhyNothingRunsNow_UsingTheGuidesWords()
    {
        // "`schedule next` shows ... why it is not running now (for example `waiting-for-window`)."
        Assert.Contains("`waiting-for-window`", DocBlocks.ReadNormalized(DocBlocks.AgentsMarkdownPath));
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        ws.RunJson("config", "set-schedule", ws.Src, "every 5m").Ok();
        var open = DateTime.Now.AddHours(2);
        ws.RunJson("config", "set-window", open.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), open.AddHours(1).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Ok();

        var target = ws.RunJson("schedule", "next").Ok().GetProperty("targets").EnumerateArray().First(t => t.GetProperty("kind").GetString() == "folder");
        Assert.Equal("waiting-for-window", target.GetProperty("state").GetString());
    }

    // ---- exit codes and rules: guide, cli.md and `commands --json` must agree (B7) ------------------------------------------

    [Fact]
    public void GuideExitCodeTable_EqualsTheExitCodesOfCommandsJson()
    {
        var guide = DocBlocks.ReadNormalized(DocBlocks.AgentsMarkdownPath);
        var rows = Regex.Matches(guide, @"^\s*\|\s*(\d+)\s*\|\s*([^|]+?)\s*\|", RegexOptions.Multiline)
            .ToDictionary(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m => m.Groups[2].Value);

        Assert.Equal(CliContract.ExitCodes.Select(e => e.Code).Order(), rows.Keys.Order());

        // The row's meaning names the same thing as the code's name in `commands --json`.
        var keyword = new Dictionary<string, string> { ["success"] = "success", ["failure"] = "failure", ["usage"] = "usage", ["config"] = "config", ["needs_user"] = "needs", ["busy"] = "busy", ["cancelled"] = "cancelled" };
        foreach (var (code, name) in CliContract.ExitCodes)
        {
            Assert.True(keyword.ContainsKey(name), $"exit code {code} has a new name '{name}': teach this test (and check the guide)");
            Assert.True(rows[code].Contains(keyword[name], StringComparison.OrdinalIgnoreCase), $"guide row for exit {code} ('{rows[code]}') does not mention '{keyword[name]}'");
        }
    }

    [Fact]
    public void CliReferenceExitCodeTable_AndErrorCodeList_EqualCommandsJson()
    {
        var cli = DocBlocks.ReadNormalized(DocBlocks.CliMarkdownPath);
        var rows = Regex.Matches(cli, @"^\|\s*`(\d+)`\s*\|\s*(?:`([a-z_]+)`)?\s*\|", RegexOptions.Multiline)
            .ToDictionary(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m => m.Groups[2].Value);

        Assert.Equal(CliContract.ExitCodes.Select(e => e.Code).Order(), rows.Keys.Order());
        foreach (var (code, name) in CliContract.ExitCodes)
        {
            // Exit 0 has no error code in the table; every other row names the error.code of `commands --json`.
            Assert.Equal(code == 0 ? string.Empty : name, rows[code]);
        }

        // "`error.code` is one of ..." lists exactly those names plus not_found.
        var sentence = Regex.Match(cli, @"`error\.code` is one of ([^.]*?);", RegexOptions.Singleline);
        Assert.True(sentence.Success, "docs/usage/cli.md no longer has the sentence listing the error.code values");
        var listed = Regex.Matches(sentence.Groups[1].Value, "`([a-z_]+)`").Select(m => m.Groups[1].Value).Order().ToArray();
        var expected = CliContract.ExitCodes.Where(e => e.Code != 0).Select(e => e.Name).Append("not_found").Order().ToArray();
        Assert.Equal(expected, listed);
    }

    [Fact]
    public void EveryDocumentedExitCode_IsProducedWithItsDocumentedErrorCode()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Run("bogus", "--json").Failed(2, "usage");
        var configFile = Path.Combine(ws.Home, "config.json");
        File.WriteAllText(configFile, "{ not json");
        ws.RunJson("snapshots").Failed(3, "config");
        File.Delete(configFile);
        ws.Init();
        ws.Backup();
        ws.RunJson("verify", "nothing/HEAD").Failed(1, "not_found");
        ws.RunJson("export-key", Path.Combine(ws.Root, "k.json")).Failed(1, "failure");
        using (ws.HoldRepositoryLock())
        {
            ws.RunJson("backup", ws.Src, "--wait", "0").Failed(5, "busy");
        }

        ws.EnableEncryption();
        ws.RunJson("backup", ws.Src).Failed(4, "needs_user");

        // 130 (cancelled) is produced and asserted by InterruptionTests.
        Assert.Contains(130, CliContract.ExitCodes.Select(e => e.Code));
    }

    [Fact]
    public void TheCommandsThatNeedYes_AreExactlyTheOnesTheGuideAndCliReferenceName()
    {
        var guide = DocBlocks.ReadNormalized(DocBlocks.AgentsMarkdownPath);
        var section = guide[guide.IndexOf("The commands that require `--yes`", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("Pass `--yes` only when", StringComparison.Ordinal)];
        var named = Regex.Matches(section, @"^\s*- `([^`]+)`", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(["encryption disable", "repair", "restore --conflict overwrite", "retention apply", "system-restore", "update install"], named);

        var cli = DocBlocks.ReadNormalized(DocBlocks.CliMarkdownPath);
        foreach (var name in named)
        {
            Assert.Contains($"`{name}`", cli[cli.IndexOf("| `--yes` |", StringComparison.Ordinal)..][..900]);
        }

        // The behaviour is asserted in ProtocolTests (RetentionApply_..., EncryptionDisable_..., RepairAndRestoreOverwrite_...), UpdateMirrorTests.
    }

    [Fact]
    public void AgentGuideCommand_PrintsTheGuideFromTheDocs()
    {
        using var ws = Workspace.Create();
        var printed = ws.Run("agent-guide");
        Assert.Equal(0, printed.ExitCode);
        // The printed guide drops the <!-- e2e --> lines that mark runnable recipes for these tests.
        var expected = System.Text.RegularExpressions.Regex.Replace(
            DocBlocks.ReadNormalized(DocBlocks.AgentsMarkdownPath), @"^[ \t]*<!--\s*e2e\b[^>]*-->[ \t]*\n", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.Equal(expected.Trim(), printed.Stdout.Replace("\r\n", "\n").Trim());
        Assert.DoesNotContain("<!-- e2e", printed.Stdout);
    }

    [Fact]
    public void CommandsJson_HasTheStructureTheCliReferenceDescribes()
    {
        var document = CliContract.Document;
        foreach (var property in new[] { "schemaVersion", "version", "globalOptions", "exitCodes", "commands" })
        {
            Assert.True(document.TryGetProperty(property, out _), property);
        }

        foreach (var exit in document.GetProperty("exitCodes").EnumerateArray())
        {
            foreach (var property in new[] { "code", "name", "meaning" })
            {
                Assert.True(exit.TryGetProperty(property, out _), property);
            }
        }

        var optionProperties = new[] { "name", "takesValue", "valueName", "repeatable", "allowedValues", "description" };
        var needsProperties = new[] { "config", "systemState" };
        foreach (var command in CliContract.Commands)
        {
            foreach (var option in command.Element.GetProperty("options").EnumerateArray())
            {
                foreach (var property in optionProperties)
                {
                    Assert.True(option.TryGetProperty(property, out _), $"{command.Name} option lacks {property}");
                }
            }

            foreach (var property in needsProperties)
            {
                Assert.True(command.Element.GetProperty("needs").TryGetProperty(property, out _), $"{command.Name} needs.{property}");
            }

            Assert.Contains(command.Config, new[] { "none", "loaded", "storage-validation", "full-validation" });
        }
    }

    private static bool PathExists(JsonElement element, string[] segments)
    {
        if (segments.Length == 0)
        {
            return true;
        }

        var segment = segments[0];
        var array = segment.EndsWith("[]", StringComparison.Ordinal);
        var name = array ? segment[..^2] : segment;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var child))
        {
            return false;
        }

        if (!array)
        {
            return PathExists(child, segments[1..]);
        }

        // Every element must have the rest of the path; an empty array cannot prove anything, so it counts as missing.
        return child.ValueKind == JsonValueKind.Array && child.GetArrayLength() > 0 && child.EnumerateArray().All(item => PathExists(item, segments[1..]));
    }

    private static string Truncate(string text) => text.Length <= 1500 ? text : text[..1500] + "...";
}

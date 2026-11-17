using System.Text.Json;

namespace ReStore.E2E;

/// <summary>
/// Under <c>--json</c> stdout is exactly one JSON document for EVERY command that can run without external services, success or failure.
/// The sweep is driven by <c>commands --json</c>: a new command must be added to <see cref="Sweep"/> or to <see cref="Excluded"/> with a reason.
/// </summary>
[Trait("Category", "E2E")]
public class JsonContractTests
{
    private static readonly string[] KnownErrorCodes = ["usage", "config", "failure", "not_found", "busy", "needs_user", "cancelled"];

    /// <summary>Commands that are not swept, and why.</summary>
    private static readonly Dictionary<string, string> Excluded = new(StringComparer.Ordinal)
    {
        ["--service"] = "runs until Ctrl+C and has no JSON form (asserted separately: --json is a usage error)",
        ["auth gdrive"] = "starts a browser sign-in",
        ["auth dropbox"] = "needs a Dropbox app and the network",
    };

    /// <summary>Command name to its arguments (placeholders in braces). They run in this order against one workspace.</summary>
    private static readonly (string Name, string[] Args)[] Sweep =
    [
        ("help", []), ("version", []), ("init", ["--storage", "local", "--path", "{storage}", "--watch", "{src}", "--probe"]), ("commands", []), ("agent-guide", []), ("doctor", []), ("logs", []), ("diagnostics", []),
        ("config path", []), ("config schema", []),
        ("schedule preview", ["daily 02:00", "--count", "2"]),
        ("status", []), ("activity", []), ("usage", []), ("snapshots", []),
        ("backup", ["{src}"]),
        ("snapshots", []), // again: now with a snapshot
        ("ls", ["{head}"]), ("verify", ["{head}"]), ("test-restore", ["{head}"]),
        ("versions", ["{group}", "a.txt"]), ("diff", ["{head}", "{head}"]),
        ("restore", ["{head}", "{out}", "--dry-run"]), ("restore", ["{head}", "{out}"]),
        ("adopt", []), ("repair", ["--dry-run"]), ("retention", ["plan"]), ("probe", []),
        ("config show", []), ("config validate", []), ("config get", ["retention.keepDaily"]), ("config list-watch", []),
        ("config set", ["retention.keepDaily", "3"]), ("config unset", ["retention.keepDaily"]),
        ("config add-watch", ["{extra}"]), ("config remove-watch", ["{extra}"]),
        ("config set-schedule", ["{src}", "daily 02:00"]), ("config clear-schedule", ["{src}"]),
        ("config set-window", ["22:00", "06:00"]), ("config clear-window", []),
        ("config set-storage", ["local", "--path", "{storage}"]),
        ("config export", ["{tmp}/exported.json"]), ("config import", ["{tmp}/exported.json"]), ("config protect", []),
        ("schedule next", []), ("schedule status", []),
        ("encryption status", []), ("encryption enable", []), ("encryption disable", []),
        ("unlock", []), ("lock", []), ("auth status", []),
        ("recovery-key", []), ("export-key", ["{tmp}/key.json"]), ("import-key", ["{tmp}/missing-key.json"]), ("change-password", []),
        ("telemetry status", []), ("telemetry preview", []), ("telemetry enable", []), ("telemetry disable", []),
        ("telemetry reset-id", []), ("telemetry clear", []), ("telemetry crash-reports", []), ("telemetry crashes", ["list"]),
        ("update check", []), ("update install", []), ("update skip", ["9.9.9"]),
        ("pause", ["--for", "1h"]), ("pause", ["status"]), ("resume", []),
        ("run-due", []), ("system-undo", ["--list"]), ("share", ["{src}/a.txt"]), ("upgrade-format", []),

        // Windows-only commands that change the machine run only where they are expected to refuse cleanly.
        ("system-backup", []), ("system-restore", ["snapshots/none/system.zip"]), ("schedule install", []), ("schedule remove", []),
    ];

    [Fact]
    public void EveryCommandIsSweptOrExcludedWithAReason_AndNothingIsStale()
    {
        var known = CliContract.Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var covered = Sweep.Select(s => s.Name).Concat(Excluded.Keys).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(known.Except(covered).Order());
        Assert.Empty(covered.Except(known).Order());
    }

    [Fact]
    public void JsonStdout_IsExactlyOneDocument_ForEveryCommand()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        Directory.CreateDirectory(Path.Combine(ws.Root, "extra"));

        var commands = CliContract.Commands.ToDictionary(c => c.Name);
        var group = string.Empty;
        var head = string.Empty;
        var problems = new List<string>();
        foreach (var (name, template) in Sweep)
        {
            var command = commands[name];
            if (OperatingSystem.IsWindows() && command.WindowsOnly && command.Mutates)
            {
                continue; // would change this machine; the Linux run covers the refusal
            }

            string[] args =
            [
                .. name.Split(' '),
                .. template.Select(t => t.Replace("{src}", ws.Src).Replace("{head}", head).Replace("{group}", group).Replace("{out}", ws.Out)
                    .Replace("{extra}", Path.Combine(ws.Root, "extra")).Replace("{storage}", ws.Storage).Replace("{tmp}", ws.Tmp)),
                "--json", "--non-interactive"
            ];

            var result = ws.Run(args);
            if (name == "backup" && result.ExitCode == 0)
            {
                group = ws.GroupKey();
                head = $"snapshots/{group}/HEAD";
            }

            var label = $"{name} {string.Join(' ', template)}".Trim();
            try
            {
                var json = result.Json();
                Assert.Equal(JsonValueKind.Object, json.ValueKind);
                Assert.True(result.ExitCode is >= 0 and <= 5, $"exit code {result.ExitCode} is not one of the documented codes");
                Assert.Equal("ok", json.EnumerateObject().First().Name);
                var ok = json.GetProperty("ok").GetBoolean();
                Assert.Equal(result.ExitCode == 0, ok);
                if (!ok)
                {
                    Assert.Equal(result.ExitCode, json.GetProperty("exitCode").GetInt32());
                    var error = json.GetProperty("error");
                    Assert.Contains(error.GetProperty("code").GetString(), KnownErrorCodes);
                    Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
                }

                // Nothing may reach stdout that is not the document (the parse above also proves that); diagnostics are for stderr.
                Assert.StartsWith("{", result.Stdout.TrimStart());
            }
            catch (Exception ex) when (ex is Xunit.Sdk.XunitException or InvalidOperationException or KeyNotFoundException)
            {
                problems.Add($"[{label}] {ex.Message}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n\n", problems));
    }

    [Fact]
    public void Service_RejectsJson_AsAUsageError()
    {
        using var ws = Workspace.Create();
        ws.Run("--service", "--json").Failed(2, "usage");
    }

    [Fact]
    public void Quiet_WithJson_StillPrintsTheDocument_AndNothingElse()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        var backup = ws.Run("backup", ws.Src, "--json", "--quiet", "--non-interactive");
        Assert.True(backup.Ok().GetProperty("ok").GetBoolean());
        Assert.True(backup.Stderr.Trim().Length == 0, "--quiet leaves stderr empty on success:\n" + backup.Describe());
    }

    [Fact]
    public void Stderr_CarriesTheLog_StdoutOnlyTheDocument()
    {
        using var ws = Workspace.Create();
        TestFiles.CreateSampleTree(ws.Src);
        ws.Init();
        var result = ws.RunJson("backup", ws.Src);
        result.Ok();
        Assert.Contains("[Info]", result.Stderr);
        Assert.DoesNotContain("[Info]", result.Stdout);
        Assert.DoesNotContain("[Uploading]", result.Stdout);
    }
}

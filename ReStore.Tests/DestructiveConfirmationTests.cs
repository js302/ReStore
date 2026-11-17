using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.cli;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// <c>repair</c>, <c>restore --conflict overwrite</c> and <c>system-restore</c> ask like <c>retention apply</c>: y/N on stderr when
/// interactive, exit 4 (needs_user) without --yes when there is no terminal, exit 1 when declined; previews never ask.
/// </summary>
[Collection("GlobalState")]
public class DestructiveConfirmationTests : IDisposable
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    private static readonly string[] ManagedVariables =
    [
        "RESTORE_ENCRYPTION_PASSWORD", "RESTORE_ENCRYPTION_PASSWORD_FILE", "RESTORE_NEW_ENCRYPTION_PASSWORD", "RESTORE_NON_INTERACTIVE", "CI"
    ];

    private readonly string _previousRoot = AppPaths.Root;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreDestructiveConfirm_" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _previousVariables = [];

    public DestructiveConfirmationTests()
    {
        Directory.CreateDirectory(_root);
        AppPaths.OverrideRoot(_root);
        foreach (var name in ManagedVariables)
        {
            _previousVariables[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        Program.InputRedirectedOverride = null;
        CliRuntime.Reset();
        OpsEnvironment.Reset();
        AppPaths.OverrideRoot(_previousRoot);
        foreach (var (name, value) in _previousVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try { Directory.Delete(_root, true); } catch { }
    }

    private string StorageDirectory => Path.Combine(_root, "storage");

    /// <summary>Runs the CLI. A non-null <paramref name="answer"/> simulates a terminal that types it; null keeps stdin redirected.</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWithInputAsync(string? answer, params string[] args)
    {
        await ConsoleLock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalIn = Console.In;
        try
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var outWriter = new StringWriter(stdout);
            using var errWriter = new StringWriter(stderr);
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            if (answer != null)
            {
                Console.SetIn(new StringReader(answer + "\n"));
                Program.InputRedirectedOverride = false;
            }

            var exitCode = await Program.Main(args);
            outWriter.Flush();
            errWriter.Flush();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Program.InputRedirectedOverride = null;
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleLock.Release();
        }
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args) => RunWithInputAsync(null, args);

    private async Task<(string Source, string Head)> BackedUpSourceAsync()
    {
        ConfigInitializer.EnsureConfigurationSetup();
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "doc.txt"), "original " + new string('d', 2000));
        var root = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = source, ["storageType"] = null });
        root["globalStorageType"] = "local";
        root["storageSources"] = new JsonObject { ["local"] = new JsonObject { ["path"] = StorageDirectory } };
        root["excludedPaths"] = new JsonArray();
        root["retention"] = new JsonObject { ["enabled"] = false };
        File.WriteAllText(AppPaths.ConfigFile, root.ToJsonString());

        var (backupExit, _, backupError) = await RunAsync("backup", source, "--non-interactive");
        backupExit.Should().Be(0, backupError);
        var (listExit, listOut, _) = await RunAsync("snapshots", "--json");
        listExit.Should().Be(0);
        var head = JsonNode.Parse(listOut)!["groups"]!.AsArray().SelectMany(g => g!["snapshots"]!.AsArray())
            .Select(s => s!["manifestPath"]!.GetValue<string>()).First();
        return (source, head);
    }

    private string PlantOrphanChunk()
    {
        var path = Path.Combine(StorageDirectory, "chunks", "zz", "orphan-chunk.chunk");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[1234]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        return path;
    }

    private string PreparedTarget(string existingContent = "old")
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "doc.txt"), existingContent);
        return target;
    }

    // ---- repair ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Repair_NonInteractiveWithoutYes_ExitsFourAndDeletesNothing()
    {
        await BackedUpSourceAsync();
        var orphan = PlantOrphanChunk();

        var (exit, stdout, _) = await RunAsync("repair", "--grace-hours", "0", "--non-interactive", "--json");

        exit.Should().Be(4);
        var doc = JsonNode.Parse(stdout)!;
        doc["error"]!["code"]!.GetValue<string>().Should().Be("needs_user");
        doc["error"]!["hint"]!.GetValue<string>().Should().Contain("--yes").And.Contain("--dry-run");
        File.Exists(orphan).Should().BeTrue();
    }

    [Fact]
    public async Task Repair_WithRedirectedStdinAndNoFlags_ExitsFour()
    {
        await BackedUpSourceAsync();
        var orphan = PlantOrphanChunk();

        var (exit, _, _) = await RunAsync("repair", "--grace-hours", "0", "--json");

        exit.Should().Be(4);
        File.Exists(orphan).Should().BeTrue();
    }

    [Fact]
    public async Task Repair_WithYes_Deletes()
    {
        await BackedUpSourceAsync();
        var orphan = PlantOrphanChunk();

        var (exit, stdout, stderr) = await RunAsync("repair", "--grace-hours", "0", "--yes", "--json");

        exit.Should().Be(0, stderr);
        File.Exists(orphan).Should().BeFalse();
        JsonNode.Parse(stdout)!["chunksDeleted"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Repair_InteractiveNo_ExitsOneAndDeletesNothing_NamingTheCount()
    {
        await BackedUpSourceAsync();
        var orphan = PlantOrphanChunk();

        var (exit, _, stderr) = await RunWithInputAsync("n", "repair", "--grace-hours", "0");

        exit.Should().Be(1);
        stderr.Should().Contain("Delete 1 unreferenced chunk(s)").And.Contain("[y/N]");
        File.Exists(orphan).Should().BeTrue();
    }

    [Fact]
    public async Task Repair_InteractiveYes_Deletes()
    {
        await BackedUpSourceAsync();
        var orphan = PlantOrphanChunk();

        var (exit, _, stderr) = await RunWithInputAsync("y", "repair", "--grace-hours", "0");

        exit.Should().Be(0, stderr);
        File.Exists(orphan).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("n")]
    public async Task Repair_DryRun_NeverAsks(string? answer)
    {
        await BackedUpSourceAsync();
        var orphan = PlantOrphanChunk();

        var (exit, _, stderr) = await RunWithInputAsync(answer, "repair", "--dry-run", "--grace-hours", "0");

        exit.Should().Be(0, stderr);
        stderr.Should().NotContain("[y/N]");
        File.Exists(orphan).Should().BeTrue();
    }

    // ---- restore --conflict overwrite --------------------------------------------------------------------------------

    [Fact]
    public async Task RestoreOverwrite_NonInteractiveWithoutYes_ExitsFourAndWritesNothing()
    {
        var (_, head) = await BackedUpSourceAsync();
        var target = PreparedTarget();

        var (exit, stdout, _) = await RunAsync("restore", head, target, "--conflict", "overwrite", "--non-interactive", "--json");

        exit.Should().Be(4);
        var doc = JsonNode.Parse(stdout)!;
        doc["error"]!["code"]!.GetValue<string>().Should().Be("needs_user");
        doc["error"]!["hint"]!.GetValue<string>().Should().Contain("--yes").And.Contain("--dry-run");
        File.ReadAllText(Path.Combine(target, "doc.txt")).Should().Be("old");
    }

    [Fact]
    public async Task RestoreOverwrite_WithYes_Overwrites()
    {
        var (_, head) = await BackedUpSourceAsync();
        var target = PreparedTarget();

        var (exit, _, stderr) = await RunAsync("restore", head, target, "--conflict", "overwrite", "--yes", "--json");

        exit.Should().Be(0, stderr);
        File.ReadAllText(Path.Combine(target, "doc.txt")).Should().StartWith("original");
    }

    [Fact]
    public async Task RestoreOverwrite_InteractiveNo_ExitsOneAndWritesNothing_NamingTheCountAndTarget()
    {
        var (_, head) = await BackedUpSourceAsync();
        var target = PreparedTarget();

        var (exit, _, stderr) = await RunWithInputAsync("n", "restore", head, target, "--conflict", "overwrite");

        exit.Should().Be(1);
        stderr.Should().Contain("Overwrite up to 1 existing file(s) in").And.Contain(target).And.Contain("[y/N]");
        File.ReadAllText(Path.Combine(target, "doc.txt")).Should().Be("old");
    }

    [Fact]
    public async Task RestoreOverwrite_InteractiveYes_Overwrites()
    {
        var (_, head) = await BackedUpSourceAsync();
        var target = PreparedTarget();

        var (exit, _, stderr) = await RunWithInputAsync("y", "restore", head, target, "--conflict", "overwrite");

        exit.Should().Be(0, stderr);
        File.ReadAllText(Path.Combine(target, "doc.txt")).Should().StartWith("original");
    }

    [Theory]
    [InlineData("overwrite", true)]
    [InlineData("skip", false)]
    [InlineData("keepboth", false)]
    public async Task Restore_NeverAsksForDryRunOrOtherPolicies(string policy, bool dryRun)
    {
        var (_, head) = await BackedUpSourceAsync();
        var target = PreparedTarget();
        string[] args = dryRun
            ? ["restore", head, target, "--conflict", policy, "--dry-run", "--non-interactive"]
            : ["restore", head, target, "--conflict", policy, "--non-interactive"];

        var (exit, _, stderr) = await RunAsync(args);

        exit.Should().Be(0, stderr);
        stderr.Should().NotContain("[y/N]");
        if (dryRun || policy == "skip")
        {
            File.ReadAllText(Path.Combine(target, "doc.txt")).Should().Be("old");
        }
    }

    // ---- system-restore ----------------------------------------------------------------------------------------------

    [Fact]
    public void SystemRestore_NonInteractiveWithoutYes_ExitsFourBeforeAnythingHappens()
    {
        CliRuntime.Reset();
        CliRuntime.NonInteractive = true;

        var thrown = FluentActions.Invoking(Program.RequireSystemRestoreConfirmationWithoutTerminal).Should().Throw<Exception>().Which;

        thrown.Message.Should().Contain("system-restore");
        CliRuntime.Yes = true;
        FluentActions.Invoking(Program.RequireSystemRestoreConfirmationWithoutTerminal).Should().NotThrow();
    }

    private static SystemRestorePlan PlanWithEnvironmentChanges(int changes)
    {
        var plan = new SystemRestorePlan("environment");
        for (var i = 0; i < changes; i++)
        {
            plan.Actions.Add(new PlannedAction { Id = "env-" + i, Component = "environment", Change = PlannedChange.Add, Name = "VAR" + i });
        }

        plan.Actions.Add(new PlannedAction { Id = "prog-1", Component = "programs", Change = PlannedChange.Install, Name = "tool" });
        return plan;
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData("n", false)]
    public async Task SystemRestore_InteractivePrompt_NamesComponentAndCount_AndOnlyAppliesEnvironmentRows(string answer, bool applies)
    {
        await ConsoleLock.WaitAsync();
        var originalIn = Console.In;
        var originalError = Console.Error;
        try
        {
            var stderr = new StringWriter();
            Console.SetError(stderr);
            Console.SetIn(new StringReader(answer + "\n"));
            CliRuntime.Reset();
            CliRuntime.NonInteractive = false;

            if (applies)
            {
                var ids = await Program.SelectSystemRestoreActionsAsync(PlanWithEnvironmentChanges(2));
                ids.Should().BeEquivalentTo(["env-0", "env-1"]);
            }
            else
            {
                var thrown = await FluentActions.Awaiting(() => Program.SelectSystemRestoreActionsAsync(PlanWithEnvironmentChanges(2)))
                    .Should().ThrowAsync<CliCommandException>();
                thrown.Which.ExitCode.Should().Be(1);
            }

            stderr.ToString().Should().Contain("Apply 2 environment variable change(s) from the environment backup").And.Contain("[y/N]");
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetError(originalError);
            CliRuntime.Reset();
            ConsoleLock.Release();
        }
    }

    [Fact]
    public async Task SystemRestore_WithYes_AppliesWithoutPrompting()
    {
        CliRuntime.Reset();
        CliRuntime.Yes = true;

        var ids = await Program.SelectSystemRestoreActionsAsync(PlanWithEnvironmentChanges(1));

        ids.Should().BeEquivalentTo(["env-0"]);
    }

    [Fact]
    public void Registry_MarksTheThreeCommandsDestructive()
    {
        foreach (var name in new[] { "repair", "restore", "system-restore" })
        {
            CommandRegistry.Find(name)!.Destructive.Should().BeTrue(name);
        }
    }
}

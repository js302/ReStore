using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>A dry run (and any preview) must leave <c>state/system_state.json</c> byte-identical and its mtime untouched.</summary>
public class CliDryRunStateTests : CliConfigTestBase
{
    private static readonly DateTime OldTime = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private async Task PrepareBackedUpRepositoryAsync()
    {
        WriteConfig(root => root["retention"] = new JsonObject { ["enabled"] = true, ["keepLast"] = 1, ["keepWithinDays"] = 1 });
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(WatchedDirectory, "a.txt"), "hello dry run " + i);
            var (exit, stdout, stderr) = await RunAsync("backup", WatchedDirectory);
            exit.Should().Be(0, stdout + stderr);
            await Task.Delay(1100);
        }

        File.Exists(AppPaths.SystemStateFile).Should().BeTrue();
        File.SetLastWriteTimeUtc(AppPaths.SystemStateFile, OldTime);
    }

    [Theory]
    [InlineData("retention", "--dry-run")]
    [InlineData("retention", "plan")]
    [InlineData("retention", "plan", "--json")]
    [InlineData("repair", "--dry-run")]
    [InlineData("repair", "--dry-run", "--json")]
    public async Task DryRun_LeavesTheStateFileUntouched(params string[] args)
    {
        await PrepareBackedUpRepositoryAsync();
        var hashBefore = SHA256.HashData(File.ReadAllBytes(AppPaths.SystemStateFile));

        var (exit, stdout, stderr) = await RunAsync(args);

        exit.Should().Be(0, stdout + stderr);
        SHA256.HashData(File.ReadAllBytes(AppPaths.SystemStateFile)).Should().Equal(hashBefore);
        File.GetLastWriteTimeUtc(AppPaths.SystemStateFile).Should().Be(OldTime);
        Directory.GetFiles(AppPaths.StateDirectory).Select(Path.GetFileName)
            .Should().NotContain(n => n!.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestoreDryRun_LeavesTheStateFileUntouched()
    {
        await PrepareBackedUpRepositoryAsync();
        var snapshot = Directory.GetDirectories(Path.Combine(StorageDirectory, "snapshots")).Single();
        var head = "snapshots/" + Path.GetFileName(snapshot) + "/HEAD";
        var hashBefore = SHA256.HashData(File.ReadAllBytes(AppPaths.SystemStateFile));

        var (exit, stdout, stderr) = await RunAsync("restore", head, Path.Combine(Root, "restored"), "--dry-run");

        exit.Should().Be(0, stdout + stderr);
        SHA256.HashData(File.ReadAllBytes(AppPaths.SystemStateFile)).Should().Equal(hashBefore);
        File.GetLastWriteTimeUtc(AppPaths.SystemStateFile).Should().Be(OldTime);
    }
}

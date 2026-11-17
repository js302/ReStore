using FluentAssertions;
using ReStore.Core.src.core;

namespace ReStore.Tests;

public class StatePendingUnsavedTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestEnvironment.RootDirectory, "pend_" + Guid.NewGuid().ToString("N"));

    public StatePendingUnsavedTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<SystemState> NewStateAsync()
    {
        var state = new SystemState();
        state.SetStateFilePath(Path.Combine(_dir, "system_state.json"));
        await state.LoadStateAsync();
        return state;
    }

    [Fact]
    public async Task UnsavedResults_SurviveALeaseReload_AndMergeWithTheOtherProcessesChanges()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_dir, "repository.lock"));
        var a = await NewStateAsync();
        var b = await NewStateAsync();

        var snapA = Path.Combine(_dir, "snapA");
        a.RecordActivity(new ActivityEntry { Type = ActivityType.Restore, Target = "t-a", StartUtc = DateTime.UtcNow.AddMinutes(-1), EndUtc = DateTime.UtcNow, Result = ActivityResult.Succeeded });
        a.RecordRestoreTelemetry(false, 3, 1, 4, 2, 1, 1, "cat", 1);
        a.RecordVerificationTelemetry(true, 1, 1, 1, 1, 0, 0, 0, 0);
        a.RecordSnapshotVerified(snapA, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        b.RecordActivity(new ActivityEntry { Type = ActivityType.Verify, Target = "t-b", StartUtc = DateTime.UtcNow.AddMinutes(-2), EndUtc = DateTime.UtcNow.AddSeconds(-30), Result = ActivityResult.Succeeded });
        b.RecordRestoreTelemetry(true, 2, 2, 2, 2, 2, 0, null, 0);
        b.RecordSnapshotVerified(snapA, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        await b.SaveStateAsync();

        (await coordinator.MutateStateAsync(a, "exit save", () => true)).Should().BeTrue();

        var check = await NewStateAsync();
        check.GetRecentActivity().Select(e => e.Target).Should().BeEquivalentTo(["t-a", "t-b"]);
        check.Telemetry.Restore.AttemptCount.Should().Be(2);
        check.Telemetry.Restore.SuccessCount.Should().Be(1);
        check.Telemetry.Restore.FailureCategoryCounts["cat"].Should().Be(1);
        check.Telemetry.Verification.RunCount.Should().Be(1);
        check.GetSnapshotVerifiedUtc(snapA).Should().Be(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), "the later time wins");

        // Saved results are no longer pending: a second reload must not duplicate them.
        (await a.ReloadIfChangedAsync()).Should().BeFalse();
        a.Telemetry.Restore.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task RecordActivity_MasksSecretsInMessageAndTarget()
    {
        var state = await NewStateAsync();
        state.RecordActivity(new ActivityEntry { Target = "t", Message = "failed: accessToken=abc123def456", StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow });

        var message = state.GetRecentActivity().Single().Message;
        message.Should().Contain("failed").And.NotContain("abc123def456");
    }

    [Fact]
    public async Task PendingChunkDeletes_IgnoreDuplicates_AreCapped_AndRemoveByIdentity()
    {
        var state = await NewStateAsync();
        state.QueuePendingChunkDeletes("local", ["AA", "aa", "bb"], null);
        state.QueuePendingChunkDeletes("local", ["aa"], "");
        state.GetPendingChunkDeletes().Should().HaveCount(2);

        state.RemovePendingChunkDeletes([new PendingChunkDelete { StorageType = "LOCAL", ChunkId = "aa" }]);
        state.GetPendingChunkDeletes().Select(p => p.ChunkId).Should().Equal("bb");
        state.QueuePendingChunkDeletes("local", ["aa"], null);
        state.GetPendingChunkDeletes().Should().HaveCount(2, "a removed id can be queued again");

        var capped = await NewStateAsync();
        capped.QueuePendingChunkDeletes("local", Enumerable.Range(0, SystemState.MaxPendingChunkDeletes + 50).Select(i => i.ToString("x8")), null);
        capped.GetPendingChunkDeletes().Should().HaveCount(SystemState.MaxPendingChunkDeletes);
    }

    [Fact]
    public async Task CorruptStateCopies_AreCappedToTheNewestFive()
    {
        var statePath = Path.Combine(_dir, "system_state.json");
        for (var i = 0; i < 8; i++)
        {
            File.WriteAllText($"{statePath}.corrupt-2020010100000{i}", "x");
        }

        File.WriteAllText(statePath, "{ not json");
        var state = new SystemState();
        state.SetStateFilePath(statePath);
        await state.LoadStateAsync();

        var kept = Directory.GetFiles(_dir, "system_state.json.corrupt-*");
        kept.Should().HaveCount(SystemState.MaxCorruptCopies);
        kept.Select(Path.GetFileName).Should().NotContain("system_state.json.corrupt-20200101000000", "the oldest are pruned");
    }

    [Fact]
    public async Task RemovingTheLastSnapshotOfAGroup_DropsItsDeepScanAndSuccessBookkeeping()
    {
        var state = await NewStateAsync();
        var group = Path.Combine(_dir, "data");
        state.AddSnapshotBackup(group, "snap", "a/snap.manifest.json", "local", ["aa"], 1);
        state.RecordDeepScan(group, "local", DateTime.UtcNow);
        state.RecordGroupRunSucceeded(group, DateTime.UtcNow);
        state.IsDeepScanDue(group, "local", 30, DateTime.UtcNow).Should().BeFalse();

        var path = state.GetBackupsForGroup(Path.GetFullPath(group)).Single().Path;
        state.RemoveBackupsFromGroup(Path.GetFullPath(group), [path]);

        state.IsDeepScanDue(group, "local", 30, DateTime.UtcNow).Should().BeTrue();
        state.GetLastSuccessfulRunUtc(group).Should().Be(DateTime.MinValue);
    }
}

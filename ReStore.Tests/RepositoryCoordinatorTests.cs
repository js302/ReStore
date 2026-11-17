using FluentAssertions;
using ReStore.Core.src.core;

namespace ReStore.Tests;

public class RepositoryCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestEnvironment.RootDirectory, "coord_" + Guid.NewGuid().ToString("N"));
    private readonly string _lockFile;

    public RepositoryCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        _lockFile = Path.Combine(_dir, "repository.lock");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private RepositoryCoordinator NewCoordinator(TimeSpan? crossProcessTimeout = null) => new(_lockFile, crossProcessTimeout);

    [Fact]
    public async Task ParallelMutationLeases_AreSerialised()
    {
        var coordinator = NewCoordinator();
        var active = 0;
        var maxActive = 0;

        async Task RunAsync()
        {
            await using var lease = await coordinator.AcquireMutationAsync("test");
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RunAsync()));

        maxActive.Should().Be(1);
    }

    [Fact]
    public async Task TryAcquireMutation_ReturnsNull_WhenAnotherLeaseIsHeld_AndWorksAfterRelease()
    {
        var coordinator = NewCoordinator();

        await using (var held = await coordinator.AcquireMutationAsync("first"))
        {
            var second = await coordinator.TryAcquireMutationAsync(TimeSpan.Zero, "second");
            second.Should().BeNull();
        }

        await using var third = await coordinator.TryAcquireMutationAsync(TimeSpan.Zero, "third");
        third.Should().NotBeNull();
    }

    [Fact]
    public async Task TryAcquireMutation_ReturnsNull_WhenAnotherProcessHoldsTheLockFile()
    {
        var coordinator = NewCoordinator();

        await using (new FileStream(_lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var lease = await coordinator.TryAcquireMutationAsync(TimeSpan.Zero, "try");
            lease.Should().BeNull();
            coordinator.IsBusy.Should().BeFalse("a failed attempt must not leave the in-process lease taken");
        }

        await using var afterwards = await coordinator.TryAcquireMutationAsync(TimeSpan.Zero, "try again");
        afterwards.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireMutation_TimesOutWithClearMessage_WhenLockFileIsHeldElsewhere()
    {
        var coordinator = NewCoordinator(TimeSpan.FromMilliseconds(300));

        await using (new FileStream(_lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var acquire = () => coordinator.AcquireMutationAsync("blocked");
            await acquire.Should().ThrowAsync<RepositoryBusyException>()
                .WithMessage("*Another ReStore process (CLI or GUI) is running an operation*");
        }

        // The failed attempt released the in-process gate, so the next one succeeds.
        await using var lease = await coordinator.AcquireMutationAsync("after");
        lease.Kind.Should().Be(RepositoryLeaseKind.Mutation);
    }

    [Fact]
    public async Task AcquireMutation_Waits_UntilLockFileIsReleased()
    {
        var coordinator = NewCoordinator(TimeSpan.FromSeconds(10));
        var other = new FileStream(_lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var acquiring = coordinator.AcquireMutationAsync("waiting");
        await Task.Delay(150);
        acquiring.IsCompleted.Should().BeFalse();

        await other.DisposeAsync();
        await using var lease = await acquiring.WaitAsync(TimeSpan.FromSeconds(5));
        lease.Reason.Should().Be("waiting");
    }

    [Fact]
    public async Task LockFile_IsReleasedOnDispose()
    {
        var coordinator = NewCoordinator();

        var lease = await coordinator.AcquireMutationAsync("hold");
        var whileHeld = () => new FileStream(_lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        whileHeld.Should().Throw<IOException>();

        await lease.DisposeAsync();
        await lease.DisposeAsync(); // idempotent

        using var afterwards = new FileStream(_lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task IsBusy_AndBusyChanged_FollowTheMutationLease()
    {
        var coordinator = NewCoordinator();
        var changes = new List<bool>();
        coordinator.BusyChanged += (_, _) => changes.Add(coordinator.IsBusy);

        coordinator.IsBusy.Should().BeFalse();
        await using (var lease = await coordinator.AcquireMutationAsync("Backing up docs"))
        {
            coordinator.IsBusy.Should().BeTrue();
            coordinator.CurrentReason.Should().Be("Backing up docs");
        }

        coordinator.IsBusy.Should().BeFalse();
        coordinator.CurrentReason.Should().BeNull();
        changes.Should().Equal(true, false);
    }

    [Fact]
    public async Task ReadLeases_Share_ButExcludeDeleteLease()
    {
        var coordinator = NewCoordinator();

        await using var read1 = await coordinator.AcquireReadAsync("restore");
        await using var read2 = await coordinator.AcquireReadAsync("verify");

        var deleting = coordinator.AcquireDeleteAsync("retention");
        await Task.Delay(100);
        deleting.IsCompleted.Should().BeFalse("a delete must wait for running readers");

        // A reader that arrives while a delete is queued waits behind it (no writer starvation).
        var lateReader = coordinator.AcquireReadAsync("late");
        await Task.Delay(50);
        lateReader.IsCompleted.Should().BeFalse();

        await read1.DisposeAsync();
        await Task.Delay(50);
        deleting.IsCompleted.Should().BeFalse();

        await read2.DisposeAsync();
        await using var delete = await deleting.WaitAsync(TimeSpan.FromSeconds(5));
        lateReader.IsCompleted.Should().BeFalse("readers are excluded while the delete lease is held");

        await delete.DisposeAsync();
        await using var late = await lateReader.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeleteLease_BlocksReaders_UntilReleased()
    {
        var coordinator = NewCoordinator();

        var delete = await coordinator.AcquireDeleteAsync("retention");
        var reading = coordinator.AcquireReadAsync("restore");
        await Task.Delay(100);
        reading.IsCompleted.Should().BeFalse();

        await delete.DisposeAsync();
        await using var read = await reading.WaitAsync(TimeSpan.FromSeconds(5));
        read.Kind.Should().Be(RepositoryLeaseKind.Read);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotBlockLaterAcquires()
    {
        var coordinator = NewCoordinator();
        await using var read = await coordinator.AcquireReadAsync("restore");

        using var cts = new CancellationTokenSource();
        var cancelledDelete = coordinator.AcquireDeleteAsync("retention", cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await cancelledDelete.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        // The cancelled writer must not keep new readers queued behind it.
        await using var another = await coordinator.AcquireReadAsync("another").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadLease_DoesNotBlockMutationLease()
    {
        var coordinator = NewCoordinator();

        await using var read = await coordinator.AcquireReadAsync("restore");
        await using var mutation = await coordinator.AcquireMutationAsync("backup").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadLease_IsSharedAcrossProcesses_AndAnotherProcessReaderBlocksDelete()
    {
        var coordinator = NewCoordinator(TimeSpan.FromMilliseconds(300));
        var readLock = _lockFile + ".read";

        // Another process is reading (shared lock): a read lease here is fine, a delete lease times out.
        await using (new FileStream(readLock, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read))
        {
            await using (await coordinator.AcquireReadAsync("restore"))
            {
            }

            var delete = () => coordinator.AcquireDeleteAsync("retention");
            await delete.Should().ThrowAsync<RepositoryBusyException>()
                .WithMessage("*A restore or verify is running in another ReStore process*");
        }

        // The failed delete released the in-process gate, so a delete works once the reader is gone.
        await using var afterwards = await coordinator.AcquireDeleteAsync("retention").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeleteLease_HoldsExclusiveFileLock_ThatBlocksReadersInOtherProcesses()
    {
        var coordinator = NewCoordinator();
        var readLock = _lockFile + ".read";

        await using (await coordinator.AcquireDeleteAsync("retention"))
        {
            var otherReader = () => new FileStream(readLock, FileMode.Open, FileAccess.Read, FileShare.Read);
            otherReader.Should().Throw<IOException>();
        }

        using var afterwards = new FileStream(readLock, FileMode.Open, FileAccess.Read, FileShare.Read);

        // And a reader here is blocked while another process holds the exclusive lock.
        var shortCoordinator = NewCoordinator(TimeSpan.FromMilliseconds(300));
        afterwards.Dispose();
        await using (new FileStream(readLock, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var read = () => shortCoordinator.AcquireReadAsync("restore");
            await read.Should().ThrowAsync<RepositoryBusyException>()
                .WithMessage("*A delete or retention is running in another ReStore process*");
        }
    }

    [Fact]
    public async Task ReadLease_DoesNotWaitBehindTheMutationLockFile()
    {
        var coordinator = NewCoordinator(TimeSpan.FromMilliseconds(300));

        // Another process is mid-backup (holds the mutation lock file): readers there still run.
        await using (new FileStream(_lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await using var read = await coordinator.AcquireReadAsync("restore").WaitAsync(TimeSpan.FromSeconds(5));
            read.Kind.Should().Be(RepositoryLeaseKind.Read);
        }
    }

    [Fact]
    public async Task ThrowingBusyChangedSubscriber_DoesNotLeaveTheRepositoryLocked()
    {
        var coordinator = NewCoordinator();
        var calls = 0;
        coordinator.BusyChanged += (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("subscriber bug");
        };

        var lease = await coordinator.AcquireMutationAsync("first");
        await lease.DisposeAsync();

        calls.Should().Be(2);
        await using var second = await coordinator.TryAcquireMutationAsync(TimeSpan.Zero, "second");
        second.Should().NotBeNull("the gate must be released even if a subscriber throws");
    }

    [Fact]
    public async Task TwoSystemStatesOnOneFile_LoseNoHistory_BecauseTheLeaseReloadsFirst()
    {
        var coordinator = NewCoordinator();
        var statePath = Path.Combine(_dir, "system_state.json");

        var cli = new SystemState();
        cli.SetStateFilePath(statePath);
        await cli.LoadStateAsync();

        var gui = new SystemState();
        gui.SetStateFilePath(statePath);
        await gui.LoadStateAsync();

        await coordinator.MutateStateAsync(cli, "cli backup", () =>
        {
            cli.AddSnapshotBackup("/data/a", "snap-a", "a/snap-a.manifest.json", "local", ["aa"], 1);
            return true;
        });

        // gui's in-memory copy predates the CLI's save; taking the lease must reload it first.
        await coordinator.MutateStateAsync(gui, "gui backup", () =>
        {
            gui.GetBackupsForGroup("/data/a").Should().HaveCount(1, "the lease reloads the CLI's snapshot");
            gui.AddSnapshotBackup("/data/b", "snap-b", "b/snap-b.manifest.json", "local", ["bb"], 1);
            return true;
        });

        var verify = new SystemState();
        verify.SetStateFilePath(statePath);
        await verify.LoadStateAsync();
        verify.GetBackupGroups().Should().HaveCount(2);
        verify.ChunkReferenceCounts.Keys.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReloadIfChanged_IsANoOp_WhenFileUnchanged_AndKeepsUnsavedChanges()
    {
        var statePath = Path.Combine(_dir, "system_state.json");
        var state = new SystemState();
        state.SetStateFilePath(statePath);
        await state.LoadStateAsync();
        state.AddSnapshotBackup("/data/a", "snap-a", "a/snap-a.manifest.json", "local", ["aa"], 1);

        (await state.ReloadIfChangedAsync()).Should().BeFalse();
        state.GetBackupGroups().Should().HaveCount(1);

        await state.SaveStateAsync();
        (await state.ReloadIfChangedAsync()).Should().BeFalse("its own save is not an external change");
    }

    [Fact]
    public async Task PinnedChunk_IsNotReportedUnreferenced_UntilUnpinned()
    {
        var state = new SystemState();
        state.RegisterChunkReferences("local", ["abc", "def"]);
        var token = new object();

        state.PinChunks(token, ["ABC"]);

        var unreferenced = state.UnregisterChunkReferences("local", ["abc", "def"]);
        unreferenced.Should().Equal("def");

        // The pinned chunk's count was dropped, so a backup committing it registers a fresh reference.
        state.UnpinChunks(token);
        state.RegisterChunkReferences("local", ["abc"]);
        state.UnregisterChunkReferences("local", ["abc"]).Should().Equal("abc");
        await Task.CompletedTask;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                break;
            }
        }
    }
}

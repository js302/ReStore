using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.backup;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// C1: a backup that has decided to reuse a chunk must not lose it to another group's retention.
/// Backup A is held (by a gated storage) right after <c>ExistsAsync</c> said a shared chunk exists,
/// while backup B's retention removes the last snapshot that referenced it.
/// </summary>
public class BackupCoordinationTests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "coordination_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly string _storageDir;
    private readonly string _sourceA;
    private readonly string _sourceB;
    private readonly SystemState _state;
    private readonly byte[] _sharedContent = new byte[400 * 1024];

    public BackupCoordinationTests()
    {
        _storageDir = Path.Combine(_root, "storage");
        _sourceA = Path.GetFullPath(Path.Combine(_root, "a"));
        _sourceB = Path.GetFullPath(Path.Combine(_root, "b"));
        Directory.CreateDirectory(_storageDir);
        Directory.CreateDirectory(_sourceA);
        Directory.CreateDirectory(_sourceB);

        new Random(1234).NextBytes(_sharedContent);
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "state.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task RetentionInAnotherBackup_WaitsForTheInFlightBackup_SoReusedChunksSurvive()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        var gate = await ArrangeAsync(coordinator, coordinator);

        var backupA = Task.Run(() => gate.BackupA.BackupDirectoryAsync(_sourceA));
        await gate.Storage.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        File.WriteAllBytes(Path.Combine(_sourceB, "data.bin"), [1, 2, 3, 4, 5]);
        var backupB2 = Task.Run(() => gate.BackupB.BackupDirectoryAsync(_sourceB));

        await Task.Delay(300);
        backupB2.IsCompleted.Should().BeFalse("B waits for A's mutation lease, so its retention cannot run mid-backup");

        gate.Storage.Release.SetResult();
        await Task.WhenAll(backupA, backupB2).WaitAsync(TimeSpan.FromSeconds(30));

        await AssertSnapshotRestoresAsync(coordinator, _sourceA);
    }

    [Fact]
    public async Task PinnedChunks_SurviveAnotherBackupsRetention_EvenWithoutSharedCoordinator()
    {
        // Separate coordinators (and lock files) model a path that is not serialised with A: only the
        // pins stand between B's retention and the chunk A decided to reuse.
        var coordinatorA = new RepositoryCoordinator(Path.Combine(_root, "a.lock"));
        var coordinatorB = new RepositoryCoordinator(Path.Combine(_root, "b.lock"));
        var gate = await ArrangeAsync(coordinatorA, coordinatorB);

        var backupA = Task.Run(() => gate.BackupA.BackupDirectoryAsync(_sourceA));
        await gate.Storage.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        File.WriteAllBytes(Path.Combine(_sourceB, "data.bin"), [1, 2, 3, 4, 5]);
        await gate.BackupB.BackupDirectoryAsync(_sourceB).WaitAsync(TimeSpan.FromSeconds(30));

        gate.Storage.Release.SetResult();
        await backupA.WaitAsync(TimeSpan.FromSeconds(30));

        await AssertSnapshotRestoresAsync(coordinatorA, _sourceA);
    }

    [Fact]
    public async Task WatcherStyleBackup_FailsFast_WhenRepositoryIsBusy()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        // The storage must be initialised: an uninitialised LocalStorage has no root (it used to resolve against the
        // process's working directory, which only "worked" on Linux and littered the test output folder with chunks).
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageDir });
        var config = CreateConfig(storage, retentionEnabled: false);
        await File.WriteAllBytesAsync(Path.Combine(_sourceA, "data.bin"), _sharedContent);

        var failFast = new Backup(_logger, _state, new SizeAnalyzer(), config, null, coordinator) { FailFastWhenBusy = true };

        await using (await coordinator.AcquireMutationAsync("someone else"))
        {
            var act = () => failFast.BackupDirectoryAsync(_sourceA);
            await act.Should().ThrowAsync<RepositoryBusyException>().WithMessage("*someone else*");
        }

        await failFast.BackupDirectoryAsync(_sourceA);
        _state.GetBackupsForGroup(_sourceA).Should().HaveCount(1);
    }

    [Fact]
    public async Task DeleteBackupAsync_IsIdempotent_SoSharedChunksAreNotDoubleDecremented()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageDir });
        var config = CreateConfig(storage, retentionEnabled: false);

        // Two groups sharing every chunk: each chunk has a reference count of 2.
        await File.WriteAllBytesAsync(Path.Combine(_sourceA, "data.bin"), _sharedContent);
        await File.WriteAllBytesAsync(Path.Combine(_sourceB, "data.bin"), _sharedContent);
        await new Backup(_logger, _state, new SizeAnalyzer(), config, null, coordinator).BackupDirectoryAsync(_sourceA);
        await new Backup(_logger, _state, new SizeAnalyzer(), config, null, coordinator).BackupDirectoryAsync(_sourceB);

        var snapshotA = _state.GetBackupsForGroup(_sourceA).Single();
        var retention = new RetentionManager(_logger, config, _state, coordinator);

        var results = await Task.WhenAll(
            retention.DeleteBackupAsync(_sourceA, snapshotA.Path),
            retention.DeleteBackupAsync(_sourceA, snapshotA.Path));

        results.Count(r => r).Should().Be(1, "the second delete finds the snapshot already gone");
        await AssertSnapshotRestoresAsync(coordinator, _sourceB);
    }

    [Fact]
    public async Task CarriedForwardChunks_ArePinnedWhileBackupRuns_AndUnpinnedAfterwards()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        var storage = await CreateStorageAsync();
        var hook = new HookStorage(storage);
        var config = CreateConfig(hook, retentionEnabled: false);

        // Snapshot 1 holds only a.bin, so its chunk ids are exactly the ones snapshot 2 carries forward.
        var random = new Random(7);
        var aContent = new byte[200 * 1024];
        random.NextBytes(aContent);
        await File.WriteAllBytesAsync(Path.Combine(_sourceA, "a.bin"), aContent);
        await new Backup(_logger, _state, new SizeAnalyzer(), config, null, coordinator).BackupDirectoryAsync(_sourceA);
        var carriedIds = (await _state.TryGetSnapshotChunkIdsAsync(_state.GetBackupsForGroup(_sourceA).Single()))!.ToList();
        carriedIds.Should().NotBeEmpty();

        // a.bin sorts first and is carried forward; b.bin is new, and its first Exists check is the gate.
        var bContent = new byte[100 * 1024];
        random.NextBytes(bContent);
        await File.WriteAllBytesAsync(Path.Combine(_sourceA, "b.bin"), bContent);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hook.OnExists = async path =>
        {
            if (!path.StartsWith("chunks/", StringComparison.Ordinal))
            {
                return;
            }

            reached.TrySetResult();
            await release.Task;
        };

        var second = Task.Run(() => new Backup(_logger, _state, new SizeAnalyzer(), config, null, coordinator).BackupDirectoryAsync(_sourceA));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Simulates another path dropping the last reference to the carried chunks: pinned, so not reported.
        _state.UnregisterChunkReferences("local", carriedIds).Should().BeEmpty("carried-forward chunks are pinned for the run");

        hook.OnExists = null;
        release.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(30));

        // Pins are gone once the backup finished: snapshot 2 re-registered the chunks (one reference),
        // so dropping it is reported.
        _state.UnregisterChunkReferences("local", carriedIds).Should().BeEquivalentTo(carriedIds);
    }

    [Fact]
    public async Task Pins_AreClearedAfterAFailedBackup()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        var storage = await CreateStorageAsync();
        var hook = new HookStorage(storage);
        var config = CreateConfig(hook, retentionEnabled: false);

        await File.WriteAllBytesAsync(Path.Combine(_sourceA, "data.bin"), _sharedContent);
        await new Backup(_logger, _state, new SizeAnalyzer(), config, null, coordinator).BackupDirectoryAsync(_sourceA);
        var ids = (await _state.TryGetSnapshotChunkIdsAsync(_state.GetBackupsForGroup(_sourceA).Single()))!.ToList();

        // Changing the file re-chunks it; the failing upload aborts the second backup after pins were taken.
        var changed = (byte[])_sharedContent.Clone();
        changed[0] ^= 0xFF;
        await File.WriteAllBytesAsync(Path.Combine(_sourceA, "data.bin"), changed);
        hook.OnUpload = _ => throw new IOException("upload failed");

        var act = () => new Backup(_logger, _state, new SizeAnalyzer(), config, null, coordinator).BackupDirectoryAsync(_sourceA);
        await act.Should().ThrowAsync<IOException>();

        _state.UnregisterChunkReferences("local", ids).Should().BeEquivalentTo(ids, "a failed backup must not leave pins behind");
    }

    private async Task<LocalStorage> CreateStorageAsync()
    {
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageDir });
        return storage;
    }

    private sealed class HookStorage(IStorage inner) : DelegatingStorage(inner)
    {
        public Func<string, Task>? OnExists { get; set; }
        public Func<string, Task>? OnUpload { get; set; }

        public override async Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default)
        {
            if (OnUpload is { } hook)
            {
                await hook(remotePath);
            }

            await Inner.UploadAsync(localPath, remotePath, ct);
        }

        public override async Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default)
        {
            if (OnExists is { } hook)
            {
                await hook(remotePath);
            }

            return await Inner.ExistsAsync(remotePath, ct);
        }
    }

    private async Task AssertSnapshotRestoresAsync(RepositoryCoordinator coordinator, string group)
    {
        var snapshot = _state.GetBackupsForGroup(group).OrderByDescending(b => b.Timestamp).First();
        var target = Path.Combine(_root, "restored-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageDir });

        var restore = new Restore(_logger, storage, null, _state, coordinator);
        await restore.RestoreFromBackupAsync(snapshot.Path, target);

        File.ReadAllBytes(Directory.GetFiles(target, "data.bin", SearchOption.AllDirectories).Single())
            .Should().Equal(_sharedContent);
    }

    private async Task<Arrangement> ArrangeAsync(RepositoryCoordinator coordinatorA, RepositoryCoordinator coordinatorB)
    {
        var plain = new LocalStorage(_logger);
        await plain.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageDir });
        var inner = new LocalStorage(_logger);
        await inner.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageDir });
        var gated = new GatedStorage(inner);

        // B's first snapshot is the only holder of the chunks A will reuse.
        await File.WriteAllBytesAsync(Path.Combine(_sourceB, "data.bin"), _sharedContent);
        var configB = CreateConfig(plain, retentionEnabled: true);
        var backupB = new Backup(_logger, _state, new SizeAnalyzer(), configB, null, coordinatorB);
        await backupB.BackupDirectoryAsync(_sourceB);
        _state.GetBackupsForGroup(_sourceB).Should().HaveCount(1);

        await File.WriteAllBytesAsync(Path.Combine(_sourceA, "data.bin"), _sharedContent);
        var configA = CreateConfig(gated, retentionEnabled: false);
        var backupA = new Backup(_logger, _state, new SizeAnalyzer(), configA, null, coordinatorA);

        return new Arrangement(backupA, backupB, gated);
    }

    private static IConfigManager CreateConfig(IStorage storage, bool retentionEnabled)
    {
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = retentionEnabled, KeepLastPerDirectory = 1, MaxAgeDays = 0 });
        config.SetupGet(c => c.GlobalStorageType).Returns("local");
        config.SetupGet(c => c.SizeThresholdMB).Returns(500);
        config.SetupGet(c => c.ExcludedPatterns).Returns([]);
        config.SetupGet(c => c.ExcludedPaths).Returns([]);
        config.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        config.SetupGet(c => c.WatchDirectories).Returns([]);
        config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);
        return config.Object;
    }

    private sealed record Arrangement(Backup BackupA, Backup BackupB, GatedStorage Storage);

    /// <summary>Blocks the first <c>ExistsAsync</c> that answers true until <see cref="Release"/> is set.</summary>
    private sealed class GatedStorage(IStorage inner) : DelegatingStorage(inner)
    {
        private int _gated;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default)
        {
            var exists = await Inner.ExistsAsync(remotePath, ct);
            if (exists && remotePath.Contains("chunk", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _gated, 1) == 0)
            {
                Reached.SetResult();
                await Release.Task;
            }

            return exists;
        }
    }
}

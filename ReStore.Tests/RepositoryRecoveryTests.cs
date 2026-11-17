using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>C8: discovery, adopt, repair/GC, pending deletes, group.json.</summary>
public class RepositoryRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "recovery_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly InMemoryStorage _storage;
    private readonly RepositoryCoordinator _coordinator;
    private readonly string _docs;
    private readonly string _pics;
    private readonly SystemState _oldPc;

    public RepositoryRecoveryTests()
    {
        Directory.CreateDirectory(_root);
        _storage = new InMemoryStorage(_logger);
        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        _docs = Path.GetFullPath(Path.Combine(_root, "docs"));
        _pics = Path.GetFullPath(Path.Combine(_root, "pics"));
        Directory.CreateDirectory(_docs);
        Directory.CreateDirectory(_pics);
        _oldPc = NewState("old");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private SystemState NewState(string name)
    {
        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_root, name, "system_state.json"));
        return state;
    }

    private Mock<IConfigManager> NewConfig(IStorage storage, bool retention = false, EncryptionConfig? encryption = null)
    {
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = retention, KeepLastPerDirectory = 1, MaxAgeDays = 0 });
        config.SetupGet(c => c.GlobalStorageType).Returns("local");
        config.SetupGet(c => c.SizeThresholdMB).Returns(500);
        config.SetupGet(c => c.ExcludedPatterns).Returns([]);
        config.SetupGet(c => c.ExcludedPaths).Returns([]);
        config.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        config.SetupGet(c => c.WatchDirectories).Returns([]);
        config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = encryption?.Enabled == true ? 2 : 0 }); // encrypted tests here cover the v2 scheme
        config.SetupGet(c => c.Encryption).Returns(encryption ?? new EncryptionConfig { Enabled = false });
        config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);
        return config;
    }

    private async Task BackupAsync(SystemState state, string source, string fileName, string content, IStorage? storage = null, IConfigManager? config = null, IPasswordProvider? passwords = null)
    {
        // Distinct bytes per call so every snapshot has chunks of its own.
        File.WriteAllText(Path.Combine(source, fileName), content + new string('x', 2000));
        var backup = new Backup(_logger, state, new SizeAnalyzer(), config ?? NewConfig(storage ?? _storage).Object, passwords, _coordinator);
        await backup.BackupDirectoryAsync(source);
    }

    private void AgeEverything(TimeSpan age)
    {
        foreach (var key in _storage.Store.Keys)
        {
            _storage.SetLastModified(key, DateTime.UtcNow - age);
        }
    }

    private IEnumerable<string> ChunkKeys() => _storage.Store.Keys.Where(k => k.StartsWith("chunks/", StringComparison.Ordinal));

    [Fact]
    public async Task Backup_WritesGroupIndex_BeforeHead_AndDoesNotRewriteItWhenUnchanged()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "first");

        var indexKey = SnapshotStoragePaths.GetGroupIndexPath(_docs);
        _storage.Store.Should().ContainKey(indexKey);
        var read = await new GroupIndexStore(_logger).ReadAsync(_storage, indexKey, null);
        read.Status.Should().Be(GroupIndexReadStatus.Ok);
        read.Index!.GroupPath.Should().Be(_docs);
        read.Index.MachineName.Should().Be(Environment.MachineName);
        read.Index.InstallId.Should().Be(_oldPc.GetOrCreateInstallId());

        var firstWrite = _storage.Modified[indexKey];
        await Task.Delay(30);
        await BackupAsync(_oldPc, _docs, "b.txt", "second");
        _storage.Modified[indexKey].Should().Be(firstWrite, "an unchanged index is not rewritten");
    }

    [Fact]
    public async Task InstallId_IsStablePersistedAndAdditive()
    {
        var id = _oldPc.GetOrCreateInstallId();
        _oldPc.GetOrCreateInstallId().Should().Be(id);

        await _oldPc.SaveStateAsync();
        var reloaded = NewState("old");
        await reloaded.LoadStateAsync();
        reloaded.GetOrCreateInstallId().Should().Be(id);
    }

    [Fact]
    public async Task Discovery_RunsUnderAReadLease_SoItWaitsForARunningDelete()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        var discovery = new RepositoryDiscovery(_logger, _coordinator);

        await using (await _coordinator.AcquireDeleteAsync("retention"))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var blocked = async () => await discovery.DiscoverAsync(_storage, null, cts.Token);
            await blocked.Should().ThrowAsync<OperationCanceledException>("a delete lease excludes readers");
        }

        (await discovery.DiscoverAsync(_storage, null)).Groups.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Discovery_OnAFreshMachine_ListsBothGroupsWithTheirOriginalPaths()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        await BackupAsync(_oldPc, _docs, "b.txt", "two");
        await BackupAsync(_oldPc, _pics, "p.txt", "pic");

        var result = await new RepositoryDiscovery(_logger).DiscoverAsync(_storage, null);

        result.Groups.Should().HaveCount(2);
        var docs = result.Groups.Single(g => g.OriginalPath == _docs);
        docs.HasIndex.Should().BeTrue();
        docs.MachineName.Should().Be(Environment.MachineName);
        docs.Snapshots.Should().HaveCount(2);
        docs.Snapshots[0].CreatedUtc.Should().BeOnOrAfter(docs.Snapshots[1].CreatedUtc, "newest first");
        docs.Snapshots[0].IsHead.Should().BeTrue();
        docs.Snapshots[0].FileCount.Should().Be(2);
        docs.Snapshots[0].TotalBytes.Should().BeGreaterThan(4000);
        result.Groups.Single(g => g.OriginalPath == _pics).Snapshots.Should().HaveCount(1);
    }

    [Fact]
    public async Task Discovery_WithoutGroupJson_FindsGroupsThroughManifestsAndHead()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        await BackupAsync(_oldPc, _pics, "p.txt", "pic");
        foreach (var key in _storage.Store.Keys.Where(k => k.EndsWith("/group.json")).ToList())
        {
            _storage.Store.TryRemove(key, out _);
        }

        var result = await new RepositoryDiscovery(_logger).DiscoverAsync(_storage, null);

        result.Groups.Select(g => g.OriginalPath).Should().BeEquivalentTo([_docs, _pics]);
        result.Groups.Should().OnlyContain(g => !g.HasIndex && g.Snapshots.Count == 1 && g.Snapshots[0].IsHead);
    }

    [Fact]
    public async Task Discovery_ThrowsAClearError_WhenTheProviderCannotList()
    {
        var storage = new Mock<IStorage>();
        storage.SetupGet(s => s.SupportsListing).Returns(false);

        var act = () => new RepositoryDiscovery(_logger).DiscoverAsync(storage.Object, null);

        (await act.Should().ThrowAsync<NotSupportedException>()).WithMessage("*cannot list*");
    }

    [Fact]
    public async Task Discovery_SkipsAnUnreadableManifest_AndReportsIt()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        await BackupAsync(_oldPc, _docs, "b.txt", "two");
        var victim = _storage.Store.Keys.First(k => k.EndsWith(".manifest.json"));
        _storage.Store[victim] = "{ not json"u8.ToArray();

        var result = await new RepositoryDiscovery(_logger).DiscoverAsync(_storage, null);

        var group = result.Groups.Single();
        group.Snapshots.Should().HaveCount(1);
        group.Warnings.Should().ContainSingle().Which.Should().Contain(victim);
    }

    [Fact]
    public async Task Adopt_RebuildsHistoryChunkListsAndRefcounts_OnAFreshState_AndIsIdempotent()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        await BackupAsync(_oldPc, _docs, "b.txt", "two");
        await BackupAsync(_oldPc, _pics, "p.txt", "pic");
        await _oldPc.SaveStateAsync();

        var fresh = NewState("new");
        var config = NewConfig(_storage);
        var discovered = await new RepositoryDiscovery(_logger).DiscoverAsync(_storage, null);
        var adopter = new RepositoryAdopter(_logger, config.Object, fresh, _coordinator);

        var first = await adopter.AdoptAsync(_storage, "local", discovered.Groups);
        first.SnapshotsAdopted.Should().Be(3);

        fresh.GetBackupsForGroup(_docs).Should().HaveCount(2);
        fresh.GetBackupsForGroup(_pics).Should().HaveCount(1);
        fresh.ChunkReferenceCounts.Should().BeEquivalentTo(_oldPc.ChunkReferenceCounts);
        fresh.ChunkReferenceCountsIncomplete.Should().BeFalse();
        var adoptedDocs = fresh.GetBackupsForGroup(_docs);
        var originals = _oldPc.GetBackupsForGroup(_docs);
        for (var i = 0; i < originals.Count; i++)
        {
            adoptedDocs[i].Timestamp.Should().BeCloseTo(originals[i].Timestamp, TimeSpan.FromSeconds(2), "the manifest's creation time is kept");
            adoptedDocs[i].SnapshotId.Should().Be(originals[i].SnapshotId);
        }
        (await fresh.TryGetSnapshotChunkIdsAsync(adoptedDocs[0]))!.Should().NotBeEmpty();

        var second = await adopter.AdoptAsync(_storage, "local", discovered.Groups);
        second.SnapshotsAdopted.Should().Be(0);
        second.SnapshotsAlreadyKnown.Should().Be(3);
        fresh.ChunkReferenceCounts.Should().BeEquivalentTo(_oldPc.ChunkReferenceCounts, "re-adopting must not double-count");

        // The adopted history survives a restart and can be restored from.
        var reloaded = NewState("new");
        await reloaded.LoadStateAsync();
        reloaded.GetBackupsForGroup(_docs).Should().HaveCount(2);
        reloaded.ChunkReferenceCounts.Should().BeEquivalentTo(_oldPc.ChunkReferenceCounts);
    }

    [Fact]
    public async Task Adopt_CanRemapTheOriginalPath()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        var fresh = NewState("new");
        var discovered = await new RepositoryDiscovery(_logger).DiscoverAsync(_storage, null);
        var newPath = Path.Combine(_root, "elsewhere", "Docs");

        await new RepositoryAdopter(_logger, NewConfig(_storage).Object, fresh, _coordinator)
            .AdoptAsync(_storage, "local", discovered.Groups, PathMapping.Parse([$"{_docs}={newPath}"]));

        fresh.GetBackupGroups().Should().BeEquivalentTo([newPath]);
        fresh.GetBackupsForGroup(newPath).Single().ManifestPath.Should().Contain(SnapshotStoragePathsKey(_docs));
    }

    private static string SnapshotStoragePathsKey(string group) => SnapshotStoragePaths.BuildGroupStorageKey(group);

    [Theory]
    [InlineData("C:\\Users\\old\\Documents", "C:\\Users\\old\\Documents=D:\\Docs", "D:\\Docs")]
    [InlineData("C:\\Users\\old\\Documents\\Work\\x", "c:/users/old=E:\\New", "E:\\New\\Documents\\Work\\x")]
    [InlineData("C:\\Users\\older\\Documents", "C:\\Users\\old=E:\\New", "C:\\Users\\older\\Documents")]
    public void PathMapping_ReplacesWholeSegmentPrefixes(string input, string spec, string expected)
    {
        PathMapping.Parse([spec]).Apply(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("nomapping")]
    [InlineData("=x")]
    [InlineData("x=")]
    public void PathMapping_RejectsMalformedSpecs(string spec)
    {
        var act = () => PathMapping.Parse([spec]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task EncryptedGroupIndex_RoundTripsWithThePassword_AndHidesThePathWithout()
    {
        var store = new GroupIndexStore(_logger);
        var salt = EncryptionService.GenerateSalt();
        var index = new GroupIndex { GroupPath = "C:/secret/Taxes", MachineName = "PC1", InstallId = "abc", Encrypted = true, ChunkStorageNamespace = "enc_0123456789" };

        await store.EnsureAsync(_storage, index, "correct horse", salt, 1000);

        var key = SnapshotStoragePaths.GetGroupIndexPath("C:/secret/Taxes");
        var stored = System.Text.Encoding.UTF8.GetString(_storage.Store[key]);
        stored.Should().NotContain("Taxes").And.NotContain("PC1").And.Contain("\"Salt\"").And.Contain("1000");

        (await store.ReadAsync(_storage, key, null)).Status.Should().Be(GroupIndexReadStatus.PasswordRequired);
        (await store.ReadAsync(_storage, key, "wrong password")).Status.Should().Be(GroupIndexReadStatus.Unreadable);
        var ok = await store.ReadAsync(_storage, key, "correct horse");
        ok.Status.Should().Be(GroupIndexReadStatus.Ok);
        ok.Index!.GroupPath.Should().Be("C:/secret/Taxes");
        ok.Index.MachineName.Should().Be("PC1");
        ok.Index.ChunkStorageNamespace.Should().Be("enc_0123456789");

        // Unchanged content is not rewritten, so the stored bytes stay put.
        var before = _storage.Store[key];
        (await store.EnsureAsync(_storage, index, "correct horse", salt, 1000)).Should().BeFalse();
        _storage.Store[key].Should().Equal(before);
    }

    [Fact]
    public async Task Discovery_OfAnEncryptedRepository_UsesThePasswordForGroupJson_AndStillListsGroupsWithout()
    {
        var salt = EncryptionService.GenerateSalt();
        var encryption = new EncryptionConfig { Enabled = true, Salt = Convert.ToBase64String(salt), KeyDerivationIterations = 1000 };
        var passwords = new Mock<IPurposePasswordProvider>();
        passwords.Setup(p => p.GetPasswordAsync()).ReturnsAsync("hunter2hunter2");
        passwords.Setup(p => p.GetPasswordAsync(It.IsAny<PasswordPurpose>())).ReturnsAsync("hunter2hunter2");

        await BackupAsync(_oldPc, _docs, "a.txt", "secret", config: NewConfig(_storage, encryption: encryption).Object, passwords: passwords.Object);

        var indexKey = SnapshotStoragePaths.GetGroupIndexPath(_docs);
        System.Text.Encoding.UTF8.GetString(_storage.Store[indexKey]).Should().NotContain(_docs);

        var withPassword = await new RepositoryDiscovery(_logger).DiscoverAsync(_storage, passwords.Object);
        var group = withPassword.Groups.Single();
        group.HasIndex.Should().BeTrue();
        group.OriginalPath.Should().Be(_docs);
        group.Encrypted.Should().BeTrue();

        var noPassword = await new RepositoryDiscovery(_logger).DiscoverAsync(_storage, null);
        var locked = noPassword.Groups.Single();
        locked.IndexUnreadable.Should().BeTrue();
        locked.HasIndex.Should().BeFalse();
        locked.OriginalPath.Should().Be(_docs, "the manifest still names the folder");
        locked.Snapshots.Should().HaveCount(1);
    }

    /// <summary>A backup that uploads its chunks and is then cancelled, leaving chunks no manifest references.</summary>
    private async Task<List<string>> LeaveOrphanChunksFromACancelledBackupAsync()
    {
        var before = ChunkKeys().ToHashSet();
        var cancelling = new CancelOnManifestStorage(_storage);
        var backup = new Backup(_logger, _oldPc, new SizeAnalyzer(), NewConfig(cancelling).Object, null, _coordinator);
        File.WriteAllText(Path.Combine(_pics, "cancelled.txt"), "this backup never commits " + new string('z', 3000));
        var act = () => backup.BackupDirectoryAsync(_pics);
        await act.Should().ThrowAsync<OperationCanceledException>();
        return [.. ChunkKeys().Where(k => !before.Contains(k))];
    }

    private sealed class CancelOnManifestStorage(IStorage inner) : DelegatingStorage(inner)
    {
        public override Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default) =>
            remotePath.EndsWith(".manifest.json", StringComparison.Ordinal) ? throw new OperationCanceledException() : Inner.UploadAsync(localPath, remotePath, ct);
    }

    [Fact]
    public async Task Repair_DeletesOnlyTheOrphanChunksOfACancelledBackup_AndOnlyPastTheGraceWindow()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "keep");
        await BackupAsync(_oldPc, _pics, "p.txt", "keep too");
        var referenced = ChunkKeys().ToList();
        var orphans = await LeaveOrphanChunksFromACancelledBackupAsync();
        orphans.Should().NotBeEmpty();
        var config = NewConfig(_storage);
        var repair = new RepositoryRepair(_logger, config.Object, _oldPc, _coordinator);

        // Inside the grace window: nothing may be deleted.
        var early = await repair.RunAsync(_storage, "local");
        early.ChunksDeleted.Should().Be(0);
        early.ChunksKeptInGrace.Should().Be(orphans.Count);
        ChunkKeys().Should().BeEquivalentTo([.. referenced, .. orphans]);

        // Dry run past the window reports but does not delete.
        AgeEverything(TimeSpan.FromHours(30));
        var dry = await repair.RunAsync(_storage, "local", new RepairOptions { DryRun = true });
        dry.OrphanChunks.Should().Be(orphans.Count);
        dry.OrphanBytes.Should().BeGreaterThan(0);
        dry.OrphanChunkPaths.Should().BeEquivalentTo(orphans);
        dry.ChunksDeleted.Should().Be(0);
        ChunkKeys().Should().BeEquivalentTo([.. referenced, .. orphans]);

        var real = await repair.RunAsync(_storage, "local");
        real.ChunksDeleted.Should().Be(orphans.Count);
        real.BytesFreed.Should().Be(dry.OrphanBytes);
        ChunkKeys().Should().BeEquivalentTo(referenced, "referenced chunks are never touched");
        real.RefCountsRebuilt.Should().BeTrue();
        _oldPc.ChunkReferenceCountsIncomplete.Should().BeFalse();

        // The surviving snapshots still restore.
        var target = Path.Combine(_root, "restored");
        await new Restore(_logger, _storage, null, _oldPc, _coordinator)
            .RestoreFromBackupAsync(SnapshotStoragePaths.GetHeadPath(_docs), target);
        Directory.GetFiles(target, "a.txt", SearchOption.AllDirectories).Should().ContainSingle();
    }

    [Fact]
    public async Task Repair_KeepsUnreferencedChunksWhoseAgeIsUnknown()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "keep");
        var unknownAge = "chunks/ab/abcdef0123.chunk";
        _storage.Store[unknownAge] = [1, 2, 3];
        AgeEverything(TimeSpan.FromDays(3));
        _storage.Modified.TryRemove(unknownAge, out _);

        var report = await new RepositoryRepair(_logger, NewConfig(_storage).Object, _oldPc, _coordinator).RunAsync(_storage, "local");

        report.ChunksDeleted.Should().Be(0);
        _storage.Store.Should().ContainKey(unknownAge);
    }

    [Fact]
    public async Task Repair_AbortsWithoutDeletingAnything_WhenAManifestCannotBeRead()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        await BackupAsync(_oldPc, _docs, "b.txt", "two");
        var orphan = "chunks/ab/abcdef0123.chunk";
        _storage.Store[orphan] = [1, 2, 3];
        AgeEverything(TimeSpan.FromDays(3));
        var victim = _storage.Store.Keys.First(k => k.EndsWith(".manifest.json"));
        _storage.Store[victim] = "garbage"u8.ToArray();
        var before = _storage.Store.Keys.OrderBy(k => k).ToList();

        var act = () => new RepositoryRepair(_logger, NewConfig(_storage).Object, _oldPc, _coordinator).RunAsync(_storage, "local");

        (await act.Should().ThrowAsync<RepositoryRepairAbortedException>()).WithMessage($"*{victim}*nothing was deleted*");
        _storage.Store.Keys.OrderBy(k => k).Should().Equal(before);
    }

    [Fact]
    public async Task Repair_AbortsWithoutDeletingAnything_WhenListingFailsMidway()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        _storage.Store["chunks/ab/abcdef0123.chunk"] = [1];
        AgeEverything(TimeSpan.FromDays(3));
        var flaky = new FlakyListingStorage(_storage, "chunks/");
        var before = _storage.Store.Keys.OrderBy(k => k).ToList();

        var act = () => new RepositoryRepair(_logger, NewConfig(flaky).Object, _oldPc, _coordinator).RunAsync(flaky, "local");

        await act.Should().ThrowAsync<RepositoryRepairAbortedException>();
        _storage.Store.Keys.OrderBy(k => k).Should().Equal(before);
    }

    private sealed class FlakyListingStorage(IStorage inner, string failingPrefix) : DelegatingStorage(inner)
    {
        public override async IAsyncEnumerable<StorageEntry> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var count = 0;
            await foreach (var entry in Inner.ListAsync(prefix, ct))
            {
                if (prefix == failingPrefix && ++count > 1)
                {
                    throw new IOException("connection reset");
                }

                yield return entry;
            }
        }
    }

    [Fact]
    public async Task Repair_RefusesWhenNoManifestIsVisibleButLocalHistoryHasSnapshots()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        AgeEverything(TimeSpan.FromDays(3));
        var empty = new InMemoryStorage(_logger);
        empty.Store["chunks/ab/abcdef0123.chunk"] = [1];
        empty.SetLastModified("chunks/ab/abcdef0123.chunk", DateTime.UtcNow.AddDays(-9));

        var act = () => new RepositoryRepair(_logger, NewConfig(empty).Object, _oldPc, _coordinator).RunAsync(empty, "local");

        (await act.Should().ThrowAsync<RepositoryRepairAbortedException>()).WithMessage("*nothing was deleted*");
        empty.Store.Should().ContainKey("chunks/ab/abcdef0123.chunk");
    }

    [Fact]
    public async Task Repair_RequiresListing()
    {
        var storage = new Mock<IStorage>();
        storage.SetupGet(s => s.SupportsListing).Returns(false);

        var act = () => new RepositoryRepair(_logger, NewConfig(storage.Object).Object, _oldPc, _coordinator).RunAsync(storage.Object, "local");

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Repair_ReAdoptsAnOrphanSnapshot_AndRebuildsCounts()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        await BackupAsync(_oldPc, _docs, "b.txt", "two");
        await _oldPc.SaveStateAsync();

        // A crash after HEAD moved but before the state save: the newest snapshot is missing locally.
        var newest = _oldPc.GetBackupsForGroup(_docs)[0];
        _oldPc.RemoveBackupsFromGroup(_docs, [newest.Path]);
        await _oldPc.SaveStateAsync();
        _oldPc.GetBackupsForGroup(_docs).Should().HaveCount(1);
        AgeEverything(TimeSpan.FromDays(3));

        var report = await new RepositoryRepair(_logger, NewConfig(_storage).Object, _oldPc, _coordinator).RunAsync(_storage, "local");

        report.OrphanSnapshotsAdopted.Should().Be(1);
        report.ChunksDeleted.Should().Be(0, "the orphan snapshot's chunks are referenced by its manifest");
        _oldPc.GetBackupsForGroup(_docs).Select(b => b.Path).Should().Contain(newest.Path);
        _oldPc.ChunkReferenceCountsIncomplete.Should().BeFalse();
    }

    [Fact]
    public async Task Repair_ClearsIncompleteReferenceCounts()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        await _oldPc.SaveStateAsync();
        foreach (var sidecar in Directory.GetFiles(Path.Combine(_root, "old", "snapshots")))
        {
            File.Delete(sidecar);
        }

        var statePath = Path.Combine(_root, "old", "system_state.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        json["ChunkReferenceCounts"] = new System.Text.Json.Nodes.JsonObject();
        File.WriteAllText(statePath, json.ToJsonString());

        var reloaded = NewState("old");
        await reloaded.LoadStateAsync();
        reloaded.ChunkReferenceCountsIncomplete.Should().BeTrue();

        var report = await new RepositoryRepair(_logger, NewConfig(_storage).Object, reloaded, _coordinator).RunAsync(_storage, "local");

        report.RefCountsRebuilt.Should().BeTrue();
        reloaded.ChunkReferenceCountsIncomplete.Should().BeFalse();
        reloaded.ChunkReferenceCounts.Should().NotBeEmpty();
    }

    private sealed class FailChunkDeletesStorage(IStorage inner) : DelegatingStorage(inner)
    {
        public bool Fail { get; set; } = true;

        public override Task DeleteAsync(string remotePath, CancellationToken ct = default) =>
            Fail && remotePath.StartsWith("chunks/", StringComparison.Ordinal) ? throw new IOException("provider unavailable") : Inner.DeleteAsync(remotePath, ct);
    }

    [Fact]
    public async Task FailedChunkDeletes_AreQueuedPersistently_AndRetriedByTheNextRetentionPass()
    {
        var flaky = new FailChunkDeletesStorage(_storage);
        var config = NewConfig(flaky, retention: true);
        await BackupAsync(_oldPc, _docs, "a.txt", "first", config: config.Object);
        var firstChunks = ChunkKeys().ToList();

        // The second snapshot has no file a.txt content change: rewrite it so the first snapshot's chunks become unreferenced.
        File.WriteAllText(Path.Combine(_docs, "a.txt"), "completely different " + new string('q', 3000));
        var backup = new Backup(_logger, _oldPc, new SizeAnalyzer(), config.Object, null, _coordinator);
        await backup.BackupDirectoryAsync(_docs);

        var pending = _oldPc.GetPendingChunkDeletes("local");
        pending.Should().NotBeEmpty("retention could not delete the first snapshot's chunks");
        firstChunks.Should().Contain(k => pending.Any(p => k.EndsWith(p.ChunkId + ".chunk")));
        _oldPc.GetBackupsForGroup(_docs).Should().HaveCount(1);

        // The queue is part of the persisted state.
        await _oldPc.SaveStateAsync();
        var restarted = NewState("old");
        await restarted.LoadStateAsync();
        restarted.GetPendingChunkDeletes("local").Select(p => p.ChunkId).Should().BeEquivalentTo(pending.Select(p => p.ChunkId));

        flaky.Fail = false;
        var retention = new RetentionManager(_logger, config.Object, restarted, _coordinator);
        await using (await _coordinator.AcquireDeleteAsync("test"))
        {
            (await retention.RetryPendingChunkDeletesAsync()).Should().Be(pending.Count);
        }

        restarted.GetPendingChunkDeletes().Should().BeEmpty();
        foreach (var item in pending)
        {
            _storage.Store.Keys.Should().NotContain(k => k.EndsWith(item.ChunkId + ".chunk"));
        }
    }

    [Fact]
    public async Task PendingDelete_IsDropped_WhenTheChunkIsReferencedAgain()
    {
        var config = NewConfig(_storage, retention: true);
        _oldPc.AddSnapshotBackup(_docs, "s1", "snapshots/x/s1.manifest.json", "local", ["abc123"]);
        _oldPc.QueuePendingChunkDeletes("local", ["abc123", "def456"], null);
        _storage.Store[SnapshotStoragePaths.GetChunkPath("abc123")] = [1];
        _storage.Store[SnapshotStoragePaths.GetChunkPath("def456")] = [2];

        var retention = new RetentionManager(_logger, config.Object, _oldPc, _coordinator);
        await using (await _coordinator.AcquireDeleteAsync("test"))
        {
            (await retention.RetryPendingChunkDeletesAsync()).Should().Be(1);
        }

        _storage.Store.Should().ContainKey(SnapshotStoragePaths.GetChunkPath("abc123"), "still referenced");
        _storage.Store.Should().NotContainKey(SnapshotStoragePaths.GetChunkPath("def456"));
        _oldPc.GetPendingChunkDeletes().Should().BeEmpty();
    }

    [Fact]
    public async Task PendingDelete_ForAStorageTypeNoLongerConfigured_IsDropped()
    {
        var config = NewConfig(_storage, retention: true);
        config.SetupGet(c => c.StorageSources).Returns(new Dictionary<string, StorageConfig> { ["local"] = new() });
        _oldPc.QueuePendingChunkDeletes("gone-storage", ["abc123"], null);
        _oldPc.QueuePendingChunkDeletes("local", ["def456"], null);
        _storage.Store[SnapshotStoragePaths.GetChunkPath("def456")] = [2];

        var retention = new RetentionManager(_logger, config.Object, _oldPc, _coordinator);
        await using (await _coordinator.AcquireDeleteAsync("test"))
        {
            (await retention.RetryPendingChunkDeletesAsync()).Should().Be(1);
        }

        _oldPc.GetPendingChunkDeletes().Should().BeEmpty();
    }

    [Fact]
    public async Task AdoptedSnapshots_CanBeRestoredOnTheNewMachine()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "precious");

        var fresh = NewState("new");
        var discovered = await new RepositoryDiscovery(_logger).DiscoverAsync(_storage, null);
        await new RepositoryAdopter(_logger, NewConfig(_storage).Object, fresh, _coordinator).AdoptAsync(_storage, "local", discovered.Groups);

        var snapshot = discovered.Groups.Single().Snapshots.Single();
        var target = Path.Combine(_root, "restored-new");
        await new Restore(_logger, _storage, null, fresh, _coordinator).RestoreFromBackupAsync(snapshot.ManifestPath, target);

        File.ReadAllText(Directory.GetFiles(target, "a.txt", SearchOption.AllDirectories).Single()).Should().StartWith("precious");
    }

    private const string OrphanChunk = "chunks/ab/abcdef0123.chunk";

    private static string ChunkIdOf(string chunkKey) => Path.GetFileNameWithoutExtension(chunkKey);

    private RepositoryRepair NewRepair(SystemState state, IStorage storage) =>
        new(_logger, NewConfig(storage).Object, state, _coordinator);

    /// <summary>A manifest as another backup would have committed it, referencing the given chunk ids.</summary>
    private static byte[] ManifestBytes(string group, string snapshotId, params string[] chunkIds)
    {
        var manifest = new SnapshotManifest
        {
            SnapshotId = snapshotId,
            Group = group,
            Files = [new SnapshotFileManifestEntry
            {
                RelativePath = "f.txt",
                SizeBytes = 1,
                Chunks = [.. chunkIds.Select(id => new SnapshotChunkManifestEntry { ChunkId = id, PlainSizeBytes = 1, StoredSizeBytes = 1 })]
            }]
        };
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest);
    }

    [Fact]
    public async Task Repair_AbortsWithoutDeleting_WhenHeadNamesAMissingManifest()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        _storage.Store[OrphanChunk] = [1];
        AgeEverything(TimeSpan.FromDays(3));
        var head = SnapshotStoragePaths.GetHeadPath(_docs);
        _storage.Store[head] = "snapshots/x/snapshot_gone.manifest.json\nabc\n"u8.ToArray();

        var act = () => NewRepair(_oldPc, _storage).RunAsync(_storage, "local");

        (await act.Should().ThrowAsync<RepositoryRepairAbortedException>()).WithMessage("*HEAD*not among the listed manifests*nothing was deleted*");
        _storage.Store.Should().ContainKey(OrphanChunk);
    }

    [Fact]
    public async Task Repair_AbortsWithoutDeleting_WhenHeadIsUnreadable()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        _storage.Store[OrphanChunk] = [1];
        AgeEverything(TimeSpan.FromDays(3));
        _storage.Store[SnapshotStoragePaths.GetHeadPath(_docs)] = [];

        var act = () => NewRepair(_oldPc, _storage).RunAsync(_storage, "local");

        (await act.Should().ThrowAsync<RepositoryRepairAbortedException>()).WithMessage("*could not be read*");
        _storage.Store.Should().ContainKey(OrphanChunk);
    }

    [Fact]
    public async Task Repair_OnAFreshState_KeepsEverythingAnotherInstallationReferences()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "theirs");
        var theirChunks = ChunkKeys().ToList();
        _storage.Store[OrphanChunk] = [1];
        AgeEverything(TimeSpan.FromDays(3));
        var fresh = NewState("fresh");
        fresh.GetOrCreateInstallId().Should().NotBe(_oldPc.GetOrCreateInstallId());

        var report = await NewRepair(fresh, _storage).RunAsync(_storage, "local");

        report.ChunksDeleted.Should().Be(1);
        _storage.Store.Should().NotContainKey(OrphanChunk);
        ChunkKeys().Should().BeEquivalentTo(theirChunks, "chunks of other installations' manifests are marked");
        report.OrphanSnapshotsAdopted.Should().Be(0, "foreign groups are not imported by repair");
        fresh.GetBackupGroups().Should().BeEmpty();
    }

    [Fact]
    public async Task Repair_MarksNamespacedChunkPaths_OfEncryptedSnapshots()
    {
        var salt = EncryptionService.GenerateSalt();
        var encryption = new EncryptionConfig { Enabled = true, Salt = Convert.ToBase64String(salt), KeyDerivationIterations = 1000 };
        var passwords = new Mock<IPurposePasswordProvider>();
        passwords.Setup(p => p.GetPasswordAsync()).ReturnsAsync("hunter2hunter2");
        passwords.Setup(p => p.GetPasswordAsync(It.IsAny<PasswordPurpose>())).ReturnsAsync("hunter2hunter2");
        await BackupAsync(_oldPc, _docs, "a.txt", "secret", config: NewConfig(_storage, encryption: encryption).Object, passwords: passwords.Object);
        var real = ChunkKeys().ToList();
        real.Should().OnlyContain(k => k.StartsWith("chunks/enc_"));
        var namespacedOrphan = real[0][..real[0].IndexOf('/', "chunks/".Length)] + "/ab/abcdef0123.chunk";
        _storage.Store[namespacedOrphan] = [1];
        AgeEverything(TimeSpan.FromDays(3));

        var report = await NewRepair(_oldPc, _storage).RunAsync(_storage, "local");

        report.ChunksDeleted.Should().Be(1);
        _storage.Store.Should().NotContainKey(namespacedOrphan);
        ChunkKeys().Should().BeEquivalentTo(real);
    }

    [Fact]
    public async Task Repair_KeepsChunksReferencedOnlyByTheLocalHistory()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        var localOnly = "chunks/cd/cdef0123.chunk";
        _storage.Store[localOnly] = [1];
        _oldPc.AddSnapshotBackup(_pics, "ghost", "snapshots/ghost/ghost.manifest.json", "local", [ChunkIdOf(localOnly)]);
        AgeEverything(TimeSpan.FromDays(3));

        var report = await NewRepair(_oldPc, _storage).RunAsync(_storage, "local");

        report.ChunksDeleted.Should().Be(0);
        _storage.Store.Should().ContainKey(localOnly);
    }

    private sealed class CommitDuringSweepStorage(InMemoryStorage inner, Action onChunksListed) : DelegatingStorage(inner)
    {
        public override async IAsyncEnumerable<StorageEntry> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var entry in Inner.ListAsync(prefix, ct))
            {
                yield return entry;
            }

            if (prefix == "chunks/")
            {
                onChunksListed();
            }
        }
    }

    [Fact]
    public async Task Repair_ReChecksManifestsBeforeDeleting_SoALateCommitKeepsItsReusedChunk()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        var reused = "chunks/ef/ef01234567.chunk";
        _storage.Store[reused] = [1];
        _storage.Store[OrphanChunk] = [1];
        AgeEverything(TimeSpan.FromDays(3));
        // Another machine commits a manifest reusing the old chunk after the first mark but before the delete.
        var racing = new CommitDuringSweepStorage(_storage, () =>
            _storage.Store["snapshots/other_0123456789abcdef/snapshot_late.manifest.json"] = ManifestBytes("/other", "snapshot_late", ChunkIdOf(reused)));

        var report = await NewRepair(_oldPc, racing).RunAsync(racing, "local");

        _storage.Store.Should().ContainKey(reused, "the re-check sees the new manifest");
        _storage.Store.Should().NotContainKey(OrphanChunk);
        report.ChunksDeleted.Should().Be(1);
    }

    [Fact]
    public async Task Repair_RetryOfPendingDeletes_SkipsChunksSomeManifestReferences()
    {
        await BackupAsync(_oldPc, _docs, "a.txt", "one");
        var referencedElsewhere = "chunks/ef/ef01234567.chunk";
        _storage.Store[referencedElsewhere] = [1];
        _storage.Store["snapshots/other_0123456789abcdef/snapshot_o.manifest.json"] = ManifestBytes("/other", "snapshot_o", ChunkIdOf(referencedElsewhere));
        AgeEverything(TimeSpan.FromDays(3));
        _oldPc.QueuePendingChunkDeletes("local", [ChunkIdOf(referencedElsewhere)], null);

        await NewRepair(_oldPc, _storage).RunAsync(_storage, "local");

        _storage.Store.Should().ContainKey(referencedElsewhere);
        _oldPc.GetPendingChunkDeletes().Should().BeEmpty();
    }

    [Fact]
    public async Task GroupIndex_IsNotRewritten_WhenAnotherMachineWroteIt()
    {
        var store = new GroupIndexStore(_logger);
        await store.EnsureAsync(_storage, new GroupIndex { GroupPath = "/data", MachineName = "PC-A", InstallId = "a" }, null, null, 0);
        var key = SnapshotStoragePaths.GetGroupIndexPath("/data");
        var before = _storage.Store[key];

        var rewritten = await store.EnsureAsync(_storage, new GroupIndex { GroupPath = "/data", MachineName = "PC-B", InstallId = "b" }, null, null, 0);

        rewritten.Should().BeFalse("machine and install id are last-writer information, not a reason to rewrite");
        _storage.Store[key].Should().Equal(before);
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class SnapshotChunkListTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreChunkListTests_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();

    public SnapshotChunkListTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string StatePath => Path.Combine(_root, "system_state.json");
    private string SidecarDir => Path.Combine(_root, "snapshots");

    private SystemState NewState()
    {
        var state = new SystemState(_logger);
        state.SetStateFilePath(StatePath);
        return state;
    }

    private static string RandomId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private sealed class HeavyFactAttribute : FactAttribute
    {
        public HeavyFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("RESTORE_HEAVY_TESTS") != "1")
            {
                Skip = "Heavy test: set RESTORE_HEAVY_TESTS=1 to run.";
            }
        }
    }

    /// <summary>Adds snapshots sharing one id corpus; returns the state-file growth per snapshot between 20 and 100 snapshots.</summary>
    private async Task<(double GrowthPerSnapshot, string StatePath, string SidecarDir, List<string> Corpus)> MeasureAsync(string directory, int chunksPerSnapshot, int snapshots = 100)
    {
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "system_state.json");
        var state = new SystemState(_logger);
        state.SetStateFilePath(statePath);
        var corpus = Enumerable.Range(0, chunksPerSnapshot).Select(_ => RandomId()).ToList();

        long sizeEarly = 0;
        for (var i = 0; i < snapshots; i++)
        {
            state.AddSnapshotBackup("C:/Data", $"snap-{i:D4}", $"snapshots/snap-{i:D4}.manifest.json", "local", corpus, 1, "root", false, "nsx");
            if (i == 19 || i == snapshots - 1)
            {
                await state.SaveStateAsync();
                if (i == 19)
                {
                    sizeEarly = new FileInfo(statePath).Length;
                }
            }
        }

        var growth = (new FileInfo(statePath).Length - sizeEarly) / (double)(snapshots - 20);
        return (growth, statePath, Path.Combine(directory, "snapshots"), corpus);
    }

    [Fact]
    public async Task StateGrowthPerSnapshot_DoesNotDependOnTheChunkCount()
    {
        var small = await MeasureAsync(Path.Combine(_root, "small"), 2_000);
        var large = await MeasureAsync(Path.Combine(_root, "large"), 4_000);

        large.GrowthPerSnapshot.Should().BeInRange(small.GrowthPerSnapshot * 0.9, small.GrowthPerSnapshot * 1.1);
        small.GrowthPerSnapshot.Should().BeLessThan(1000);

        Directory.GetFiles(small.SidecarDir, "*.chunks").Should().HaveCount(100);
        new FileInfo(Path.Combine(small.SidecarDir, "snap-0000.chunks")).Length.Should().Be(2_000 * 32);
        new FileInfo(Path.Combine(large.SidecarDir, "snap-0099.chunks")).Length.Should().Be(4_000 * 32);
        File.ReadAllText(small.StatePath).Should().NotContain("ChunkIds");

        var reloaded = new SystemState(_logger);
        reloaded.SetStateFilePath(small.StatePath);
        await reloaded.LoadStateAsync();
        var one = reloaded.GetBackupsForGroup("C:/Data").Single(b => b.SnapshotId == "snap-0050");
        one.ChunkIds.Should().BeEmpty("lists live in sidecars, not in memory or state");
        (await reloaded.TryGetSnapshotChunkIdsAsync(one))!.Should().BeEquivalentTo(small.Corpus);
    }

    [Trait("Category", "Heavy")]
    [HeavyFact]
    public async Task StateFile_1000Snapshots_10kChunks_StaysSmall()
    {
        var result = await MeasureAsync(Path.Combine(_root, "heavy"), 10_000, 1000);

        new FileInfo(result.StatePath).Length.Should().BeLessThan(2_000_000);
        result.GrowthPerSnapshot.Should().BeLessThan(1000);
        Directory.GetFiles(result.SidecarDir, "*.chunks").Should().HaveCount(1000);
    }

    [Fact]
    public async Task SaveState_IsCompact()
    {
        var state = NewState();
        state.AddSnapshotBackup("C:/Data", "s1", "m1", "local", [RandomId()]);
        await state.SaveStateAsync();

        File.ReadAllText(StatePath).Should().NotContain("\n");
    }

    [Fact]
    public async Task OldIndentedStateWithInlineChunkIds_LoadsMigratesAndStillDeletes()
    {
        var a = RandomId();
        var b = RandomId();
        var c = RandomId();
        var old = $$"""
        {
          "LastBackupTime": "2026-01-01T00:00:00Z",
          "BackupHistory": {
            "C:/Data": [
              { "Path": "m1", "Timestamp": "2026-01-01T00:00:00Z", "StorageType": "local", "ArtifactType": 1,
                "SnapshotId": "s1", "ManifestPath": "m1", "ChunkIds": ["{{a}}", "{{b}}"], "ChunkStorageNamespace": "nsx" },
              { "Path": "m2", "Timestamp": "2026-01-02T00:00:00Z", "StorageType": "local", "ArtifactType": 1,
                "SnapshotId": "s2", "ManifestPath": "m2", "ChunkIds": ["{{b}}", "{{c}}"], "ChunkStorageNamespace": "nsx" }
            ]
          },
          "FileMetadata": {},
          "ChunkReferenceCounts": { "local|nsx|{{a}}": 1, "local|nsx|{{b}}": 2, "local|nsx|{{c}}": 1 }
        }
        """;
        await File.WriteAllTextAsync(StatePath, old);

        var state = NewState();
        await state.LoadStateAsync();

        File.Exists(Path.Combine(SidecarDir, "s1.chunks")).Should().BeTrue();
        File.Exists(Path.Combine(SidecarDir, "s2.chunks")).Should().BeTrue();

        await state.SaveStateAsync();
        File.ReadAllText(StatePath).Should().NotContain("ChunkIds");

        var reloaded = NewState();
        await reloaded.LoadStateAsync();
        var s1 = reloaded.GetBackupsForGroup("C:/Data").Single(x => x.SnapshotId == "s1");
        var ids = await reloaded.TryGetSnapshotChunkIdsAsync(s1);
        ids.Should().BeEquivalentTo([a, b]);

        // Refcount-based delete still works off the sidecar: b is shared, a is not.
        reloaded.UnregisterChunkReferences("local", ids!, "nsx").Should().BeEquivalentTo([a]);
    }

    [Fact]
    public async Task NonHexChunkIds_UseTheLengthPrefixedFallback()
    {
        var state = NewState();
        state.AddSnapshotBackup("g", "s1", "m1", "local", ["chunk-a", "Chunk-B"]);
        await state.SaveStateAsync();

        File.Exists(Path.Combine(SidecarDir, "s1.chunklist")).Should().BeTrue();
        var reloaded = NewState();
        await reloaded.LoadStateAsync();
        var info = reloaded.GetBackupsForGroup("g").Single();
        (await reloaded.TryGetSnapshotChunkIdsAsync(info))!.Should().BeEquivalentTo(["chunk-a", "chunk-b"]);
    }

    [Fact]
    public async Task FailedSave_LeavesNoSidecarAndKeepsTheListForARetry()
    {
        var state = NewState();
        Directory.CreateDirectory(StatePath); // a directory where the file belongs: the commit fails
        state.AddSnapshotBackup("g", "s1", "m1", "local", [RandomId()]);

        var act = () => state.SaveStateAsync();
        await act.Should().ThrowAsync<Exception>();

        (Directory.Exists(SidecarDir) ? Directory.GetFiles(SidecarDir) : []).Should().BeEmpty();

        Directory.Delete(StatePath);
        await state.SaveStateAsync();
        Directory.GetFiles(SidecarDir, "*.chunks").Should().ContainSingle();
    }

    [Fact]
    public async Task RemovedSnapshot_SidecarIsDeletedOnlyAfterTheSaveThatPersistsTheRemoval()
    {
        var state = NewState();
        state.AddSnapshotBackup("g", "s1", "m1", "local", [RandomId()]);
        await state.SaveStateAsync();
        var sidecar = Path.Combine(SidecarDir, "s1.chunks");
        File.Exists(sidecar).Should().BeTrue();

        state.RemoveBackupsFromGroup("g", ["m1"]);
        File.Exists(sidecar).Should().BeTrue("the removal is not durable until the state is saved");

        await state.SaveStateAsync();
        File.Exists(sidecar).Should().BeFalse();
    }

    private sealed class ManifestStorage : IStorage
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Unavailable { get; set; }
        public HashSet<string> FailingDownloads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Deleted { get; } = [];

        public Task InitializeAsync(Dictionary<string, string> options) => Task.CompletedTask;
        public Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default) => Task.CompletedTask;

        public async Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default)
        {
            if (Unavailable || FailingDownloads.Contains(remotePath))
            {
                throw new IOException("storage unavailable");
            }

            if (!Files.TryGetValue(remotePath, out var content))
            {
                throw new StorageNotFoundException(remotePath);
            }

            await File.WriteAllTextAsync(localPath, content);
        }

        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default) => Task.FromResult(!Unavailable && Files.ContainsKey(remotePath));

        public Task DeleteAsync(string remotePath, CancellationToken ct = default)
        {
            Deleted.Add(remotePath);
            Files.Remove(remotePath);
            return Task.CompletedTask;
        }

        public Task<long?> GetSizeAsync(string remotePath, CancellationToken ct = default) =>
            Task.FromResult(Files.TryGetValue(remotePath, out var c) ? (long?)c.Length : null);
        public IAsyncEnumerable<StorageEntry> ListAsync(string prefix, CancellationToken ct = default) => throw new NotSupportedException();
        public bool SupportsListing => false;
        public int MaxConcurrency => 1;
        public bool SupportsSharing => false;
        public Task<string> GenerateShareLinkAsync(string remotePath, TimeSpan expiration) => throw new NotImplementedException();
        public void Dispose() { }
    }

    private (SystemState State, RetentionManager Manager, ManifestStorage Storage, string Id1, string Id2) SetUpDeletion()
    {
        var id1 = RandomId();
        var id2 = RandomId();
        var storage = new ManifestStorage();
        var manifest = new SnapshotManifest
        {
            SnapshotId = "s1",
            Files = [new SnapshotFileManifestEntry { RelativePath = "a", Chunks = [new SnapshotChunkManifestEntry { ChunkId = id1 }, new SnapshotChunkManifestEntry { ChunkId = id2 }] }]
        };
        storage.Files["m1"] = JsonSerializer.Serialize(manifest);
        storage.Files[SnapshotStoragePaths.GetChunkPath(id1, "nsx")] = "x";
        storage.Files[SnapshotStoragePaths.GetChunkPath(id2, "nsx")] = "x";

        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = true });
        configMock.Setup(c => c.GlobalStorageType).Returns("local");
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = NewState();
        state.AddSnapshotBackup("g", "s1", "m1", "local", [id1, id2], 1, "r", false, "nsx");
        var manager = new RetentionManager(_logger, configMock.Object, state, new RepositoryCoordinator(Path.Combine(_root, "repo.lock")));
        return (state, manager, storage, id1, id2);
    }

    [Fact]
    public async Task Delete_WithMissingSidecar_FallsBackToTheManifest()
    {
        var (state, manager, storage, id1, id2) = SetUpDeletion();
        await state.SaveStateAsync();
        File.Delete(Path.Combine(SidecarDir, "s1.chunks"));

        (await manager.DeleteBackupAsync("g", "m1")).Should().BeTrue();

        storage.Deleted.Should().Contain([SnapshotStoragePaths.GetChunkPath(id1, "nsx"), SnapshotStoragePaths.GetChunkPath(id2, "nsx")]);
        state.ChunkReferenceCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_WithMissingSidecar_AndStorageUnavailable_ChangesNothing()
    {
        var (state, manager, storage, _, _) = SetUpDeletion();
        await state.SaveStateAsync();
        File.Delete(Path.Combine(SidecarDir, "s1.chunks"));
        var countsBefore = state.ChunkReferenceCounts.ToDictionary(p => p.Key, p => p.Value);
        storage.Unavailable = true;

        (await manager.DeleteBackupAsync("g", "m1")).Should().BeFalse();

        state.GetBackupsForGroup("g").Should().ContainSingle();
        state.ChunkReferenceCounts.Should().BeEquivalentTo(countsBefore);
        storage.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_UsesTheSidecar_WhenPresent()
    {
        var (state, manager, storage, id1, id2) = SetUpDeletion();
        await state.SaveStateAsync();
        storage.Unavailable = false;
        // Break the manifest: only the sidecar can supply the ids.
        storage.Files["m1"] = "not json";

        (await manager.DeleteBackupAsync("g", "m1")).Should().BeTrue();
        storage.Deleted.Should().Contain(SnapshotStoragePaths.GetChunkPath(id1, "nsx"));
        storage.Deleted.Should().Contain(SnapshotStoragePaths.GetChunkPath(id2, "nsx"));
        File.Exists(Path.Combine(SidecarDir, "s1.chunks")).Should().BeFalse();
    }

    private (SystemState State, RetentionManager Manager, ManifestStorage Storage, string[] Ids) SetUpTwoSnapshots()
    {
        var ids = new[] { RandomId(), RandomId(), RandomId() };
        var storage = new ManifestStorage();
        void AddManifest(string path, string id, params string[] chunks)
        {
            var manifest = new SnapshotManifest
            {
                SnapshotId = id,
                Files = [new SnapshotFileManifestEntry { RelativePath = "a", Chunks = [.. chunks.Select(c => new SnapshotChunkManifestEntry { ChunkId = c })] }]
            };
            storage.Files[path] = JsonSerializer.Serialize(manifest);
        }

        AddManifest("m1", "s1", ids[0], ids[1]);
        AddManifest("m2", "s2", ids[1], ids[2]);
        foreach (var id in ids)
        {
            storage.Files[SnapshotStoragePaths.GetChunkPath(id, "nsx")] = "x";
        }

        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = true });
        configMock.Setup(c => c.GlobalStorageType).Returns("local");
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = NewState();
        state.AddSnapshotBackup("g", "s1", "m1", "local", [ids[0], ids[1]], 1, "r", false, "nsx");
        state.AddSnapshotBackup("g", "s2", "m2", "local", [ids[1], ids[2]], 1, "r", false, "nsx");
        var manager = new RetentionManager(_logger, configMock.Object, state, new RepositoryCoordinator(Path.Combine(_root, "repo.lock")));
        return (state, manager, storage, ids);
    }

    /// <summary>Saves, drops s1's sidecar and empties the stored counts, then loads a fresh state (which must rebuild).</summary>
    private async Task<SystemState> ReloadWithLostSidecarAndCountsAsync(SystemState state)
    {
        await state.SaveStateAsync();
        File.Delete(Path.Combine(SidecarDir, "s1.chunks"));
        var json = JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject();
        json["ChunkReferenceCounts"] = new JsonObject();
        File.WriteAllText(StatePath, json.ToJsonString());

        var reloaded = NewState();
        await reloaded.LoadStateAsync();
        return reloaded;
    }

    [Fact]
    public async Task Rebuild_WithAMissingSidecar_FlagsCountsIncomplete_AndThatSurvivesRestart()
    {
        var (state, _, _, _) = SetUpTwoSnapshots();
        var reloaded = await ReloadWithLostSidecarAndCountsAsync(state);

        reloaded.ChunkReferenceCountsIncomplete.Should().BeTrue();
        _logger.Messages.Should().Contain(m => m.Contains("[Error]") && m.Contains("incomplete"));

        await reloaded.SaveStateAsync();
        var again = NewState();
        await again.LoadStateAsync();
        again.ChunkReferenceCountsIncomplete.Should().BeTrue("the flag is persisted, so a restart cannot forget it");
        again.UnregisterChunkReferences("local", ["anything"], "nsx").Should().BeEmpty();
    }

    [Fact]
    public async Task Retention_WhileCountsIncomplete_KeepsChunks_ThenRepairsFromManifestsWhenStorageAllows()
    {
        var (state, manager, storage, ids) = SetUpTwoSnapshots();
        var reloaded = await ReloadWithLostSidecarAndCountsAsync(state);

        // Same storage/config, but the reloaded (incomplete) state.
        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = true });
        configMock.Setup(c => c.GlobalStorageType).Returns("local");
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);
        var incompleteManager = new RetentionManager(_logger, configMock.Object, reloaded, new RepositoryCoordinator(Path.Combine(_root, "repo2.lock")));

        // s1's manifest cannot be read, so repair is impossible: deleting s2 must drop the manifest only.
        storage.FailingDownloads.Add("m1");
        (await incompleteManager.DeleteBackupAsync("g", "m2")).Should().BeTrue();
        storage.Deleted.Should().BeEquivalentTo(["m2"], "no chunk may be deleted on an incomplete count");
        reloaded.ChunkReferenceCountsIncomplete.Should().BeTrue();

        // Storage back: the next retention pass re-reads s1's list from its manifest, rebuilds the counts and clears the flag.
        storage.FailingDownloads.Clear();
        await incompleteManager.ApplyGroupAsync("g");
        reloaded.ChunkReferenceCountsIncomplete.Should().BeFalse();
        File.Exists(Path.Combine(SidecarDir, "s1.chunks")).Should().BeTrue();
        reloaded.ChunkReferenceCounts.Keys.Should().BeEquivalentTo(
            [$"local|nsx|{ids[0]}", $"local|nsx|{ids[1]}"], "only s1 remains");

        (await incompleteManager.DeleteBackupAsync("g", "m1")).Should().BeTrue();
        storage.Deleted.Should().Contain([SnapshotStoragePaths.GetChunkPath(ids[0], "nsx"), SnapshotStoragePaths.GetChunkPath(ids[1], "nsx")]);
    }

    [Fact]
    public async Task Reload_ThatResurrectsARemovedSnapshot_KeepsItsSidecar()
    {
        var state = NewState();
        state.AddSnapshotBackup("g", "s1", "m1", "local", [RandomId()]);
        await state.SaveStateAsync();
        var sidecar = Path.Combine(SidecarDir, "s1.chunks");

        // Removal in memory only (not saved), then the file on disk (which still lists s1) is reloaded.
        state.RemoveBackupsFromGroup("g", ["m1"]);
        await state.LoadStateAsync();
        state.GetBackupsForGroup("g").Should().ContainSingle();

        await state.SaveStateAsync();
        File.Exists(sidecar).Should().BeTrue("the snapshot is back in the history, so its list must stay");
    }

    [Fact]
    public async Task Rebuild_SweepsOldOrphanSidecars_ButKeepsFreshOnes()
    {
        var (state, _, _, _) = SetUpTwoSnapshots();
        await state.SaveStateAsync();
        var oldOrphan = Path.Combine(SidecarDir, "gone.chunks");
        var freshOrphan = Path.Combine(SidecarDir, "just-written.chunks");
        File.WriteAllBytes(oldOrphan, new byte[32]);
        File.WriteAllBytes(freshOrphan, new byte[32]);
        File.SetLastWriteTimeUtc(oldOrphan, DateTime.UtcNow.AddDays(-2));

        var json = JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject();
        json["ChunkReferenceCounts"] = new JsonObject();
        File.WriteAllText(StatePath, json.ToJsonString());
        await NewState().LoadStateAsync();

        File.Exists(oldOrphan).Should().BeFalse();
        File.Exists(freshOrphan).Should().BeTrue("it may belong to a snapshot another process has not saved yet");
        File.Exists(Path.Combine(SidecarDir, "s1.chunks")).Should().BeTrue();
    }
}

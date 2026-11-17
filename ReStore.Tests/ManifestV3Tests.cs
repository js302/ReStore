using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Manifest v3 (P4-1): container, opt-in writer rule, file metadata (H6), empty directories, consumers.</summary>
public class ManifestV3Tests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "v3_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly string _storageDir;
    private readonly string _source;
    private readonly SystemState _state;
    private readonly Mock<IConfigManager> _config = new();
    private readonly ChunkDiffingConfig _chunk = new();
    private readonly RepositoryCoordinator _coordinator;

    public ManifestV3Tests()
    {
        _storageDir = Path.Combine(_root, "storage");
        _source = Path.GetFullPath(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_storageDir);
        Directory.CreateDirectory(_source);

        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "state.json"));

        _config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        _config.Setup(c => c.GlobalStorageType).Returns("local");
        _config.Setup(c => c.SizeThresholdMB).Returns(100);
        _config.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        _config.Setup(c => c.ExcludedPatterns).Returns([]);
        _config.Setup(c => c.ExcludedPaths).Returns([]);
        _config.Setup(c => c.BackupType).Returns(BackupType.Incremental);
        _config.Setup(c => c.WatchDirectories).Returns([]);
        _config.Setup(c => c.MaxFileSizeMB).Returns(100);
        _config.SetupGet(c => c.ChunkDiffing).Returns(_chunk);
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async () => await OpenStorageAsync());
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }

            Directory.Delete(_root, true);
        }
        catch { }
    }

    private async Task<LocalStorage> OpenStorageAsync()
    {
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { { "path", _storageDir } });
        return storage;
    }

    private Backup NewBackup() => new(_logger, _state, new SizeAnalyzer(), _config.Object, null, _coordinator);

    private Restore NewRestore(IStorage storage) => new(_logger, storage, null, _state, _coordinator);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private List<string> ManifestPaths() =>
        [.. Directory.GetFiles(_storageDir, "*.manifest.json", SearchOption.AllDirectories)
            .OrderBy(p => SnapshotManifestSerializer.Deserialize(File.ReadAllBytes(p)).CreatedUtc)];

    private string LatestManifestPath() => ManifestPaths().Last();

    private string HeadPath() => Directory.GetFiles(_storageDir, "HEAD", SearchOption.AllDirectories).Single();

    private static string RemoteOf(string storageRoot, string localPath) =>
        Path.GetRelativePath(storageRoot, localPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Rewrites the latest snapshot as v1.7 would have written it, and repoints HEAD.</summary>
    private void DowngradeLatestToV2()
    {
        var path = LatestManifestPath();
        var manifest = ManifestIo.Read(path);
        manifest.Version = 2;
        manifest.Directories = null;
        foreach (var file in manifest.Files)
        {
            file.Attributes = null;
            file.CreatedUtc = null;
        }

        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);
        ManifestIo.Write(path, manifest);
        File.WriteAllText(HeadPath(), $"{RemoteOf(_storageDir, path)}\n{manifest.RootHash}\n");
    }

    // ---- container ----------------------------------------------------------------------------------------------

    [Fact]
    public void V2_Serialization_IsPlainJson_WithoutAnyV3Field()
    {
        var manifest = new SnapshotManifest { Version = 2, SnapshotId = "s", Files = [new SnapshotFileManifestEntry { RelativePath = "a", Chunks = [new SnapshotChunkManifestEntry { ChunkId = "ab" }] }] };

        var bytes = SnapshotManifestSerializer.Serialize(manifest);

        SnapshotManifestSerializer.IsGzip(bytes).Should().BeFalse();
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().NotContain("Attributes").And.NotContain("CreatedUtc\": \"0").And.NotContain("Directories").And.NotContain("Codec");
        text.Should().Be(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public void V3_Serialization_IsGzip_AndRoundTrips()
    {
        var manifest = new SnapshotManifest
        {
            Version = 3,
            SnapshotId = "s",
            Profile = new ChunkingProfile { AlgorithmVersion = 2 },
            Directories = [new SnapshotDirectoryManifestEntry { RelativePath = "empty", Attributes = 32, LastWriteUtc = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc) }],
            Files = [new SnapshotFileManifestEntry { RelativePath = "a", Attributes = 1, CreatedUtc = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc) }]
        };
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);

        var bytes = SnapshotManifestSerializer.Serialize(manifest);
        var loaded = SnapshotManifestSerializer.Deserialize(bytes);

        SnapshotManifestSerializer.IsGzip(bytes).Should().BeTrue();
        loaded.Version.Should().Be(3);
        loaded.Directories.Should().ContainSingle().Which.RelativePath.Should().Be("empty");
        loaded.Files[0].Attributes.Should().Be(1);
        SnapshotManifestHasher.IsValid(loaded).Should().BeTrue();
    }

    [Fact]
    public void Deserialize_DetectsGzipByMagicBytes_NotByVersionOrName()
    {
        var plainV3 = new SnapshotManifest { Version = 3, SnapshotId = "plain-v3" };
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(plainV3);
        SnapshotManifestSerializer.IsGzip(plainBytes).Should().BeFalse();
        SnapshotManifestSerializer.Deserialize(plainBytes).SnapshotId.Should().Be("plain-v3");

        var gzippedV2 = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(gzippedV2, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(JsonSerializer.SerializeToUtf8Bytes(new SnapshotManifest { Version = 2, SnapshotId = "gz-v2" }));
        }

        SnapshotManifestSerializer.Deserialize(gzippedV2.ToArray()).SnapshotId.Should().Be("gz-v2");
    }

    [Fact]
    public void Deserialize_RefusesNewerVersionsAndUnknownCodecs()
    {
        var newer = JsonSerializer.SerializeToUtf8Bytes(new SnapshotManifest { Version = 4 });
        var act = () => SnapshotManifestSerializer.Deserialize(newer);
        act.Should().Throw<NotSupportedException>().WithMessage("*newer ReStore*");

        var coded = new SnapshotManifest
        {
            Version = 3,
            Files = [new SnapshotFileManifestEntry { RelativePath = "a", Chunks = [new SnapshotChunkManifestEntry { ChunkId = "abcd", Codec = "zstd" }] }]
        };
        var act2 = () => SnapshotManifestSerializer.Deserialize(SnapshotManifestSerializer.Serialize(coded));
        act2.Should().Throw<NotSupportedException>().WithMessage("*codec*");
    }

    [Fact]
    public void V3_RootHash_CoversAlgorithmVersionMetadataDirectoriesAndCodec()
    {
        SnapshotManifest Make() => new()
        {
            Version = 3,
            SnapshotId = "s",
            CreatedUtc = DateTime.UnixEpoch,
            Profile = new ChunkingProfile { AlgorithmVersion = 2, MinChunkSizeBytes = 1 },
            Directories = [new SnapshotDirectoryManifestEntry { RelativePath = "d", Attributes = 0, LastWriteUtc = DateTime.UnixEpoch }],
            Files = [new SnapshotFileManifestEntry { RelativePath = "a", Attributes = 0, CreatedUtc = DateTime.UnixEpoch, Chunks = [new SnapshotChunkManifestEntry { ChunkId = "abcd" }] }]
        };

        var baseHash = SnapshotManifestHasher.ComputeRootHash(Make());
        SnapshotManifestHasher.ComputeRootHash(Make()).Should().Be(baseHash);

        var a = Make(); a.Profile.AlgorithmVersion = 3;
        var b = Make(); b.Directories![0].Attributes = 32;
        var c = Make(); c.Directories![0].LastWriteUtc = DateTime.UnixEpoch.AddSeconds(1);
        var d = Make(); d.Files[0].Attributes = 1;
        var e = Make(); e.Files[0].CreatedUtc = DateTime.UnixEpoch.AddSeconds(1);
        var f = Make(); f.Files[0].Chunks[0].Codec = "raw";
        var g = Make(); g.Directories!.Clear();
        new[] { a, b, c, d, e, f, g }.Select(SnapshotManifestHasher.ComputeRootHash).Should().OnlyContain(h => h != baseHash);
    }

    [Fact]
    public void V2_RootHash_IgnoresAlgorithmVersion_AsBefore()
    {
        var manifest = new SnapshotManifest { Version = 2, SnapshotId = "s", CreatedUtc = DateTime.UnixEpoch, Profile = new ChunkingProfile { AlgorithmVersion = 2 } };
        var hash = SnapshotManifestHasher.ComputeRootHash(manifest);
        manifest.Profile.AlgorithmVersion = 0;
        SnapshotManifestHasher.ComputeRootHash(manifest).Should().Be(hash);
    }

    // ---- opt-in rule ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NewRepository_WritesGzipV3_WithMetadataAndEmptyDirectories()
    {
        Write("a.txt", "hello");
        Directory.CreateDirectory(Path.Combine(_source, "empty", "nested"));
        Write(Path.Combine("sub", "b.txt"), "world");

        await NewBackup().BackupDirectoryAsync(_source);

        var path = LatestManifestPath();
        SnapshotManifestSerializer.IsGzip(File.ReadAllBytes(path)).Should().BeTrue();
        var manifest = ManifestIo.Read(path);
        manifest.Version.Should().Be(3);
        manifest.Profile.AlgorithmVersion.Should().Be(ChunkingProfile.FastChunkerAlgorithmVersion);
        manifest.Files.Should().OnlyContain(f => f.Attributes != null && f.CreatedUtc != null);
        manifest.Directories.Should().ContainSingle().Which.RelativePath.Should().Be("empty/nested");
        SnapshotManifestHasher.IsValid(manifest).Should().BeTrue();
    }

    [Fact]
    public async Task ExistingV2Repository_StaysV2_UntilTheUserOptsIn_ThenMixedGroupWorks()
    {
        Write("a.txt", "one");
        Write("keep.txt", "unchanged");
        await NewBackup().BackupDirectoryAsync(_source);
        DowngradeLatestToV2();
        var v2Path = LatestManifestPath();
        var v2Remote = RemoteOf(_storageDir, v2Path);

        // Config says 2 (what migration writes for existing configs) or is absent (auto): the existing repository keeps writing v2.
        Write("a.txt", "two");
        await NewBackup().BackupDirectoryAsync(_source);
        var second = ManifestPaths().Last(p => p != v2Path);
        SnapshotManifestSerializer.IsGzip(File.ReadAllBytes(second)).Should().BeFalse();
        var secondManifest = ManifestIo.Read(second);
        secondManifest.Version.Should().Be(2);
        secondManifest.Directories.Should().BeNull();
        secondManifest.Files.Should().OnlyContain(f => f.Attributes == null);

        // The user opts in (config 3 / "Upgrade backup format"): v3, and unchanged files gain their metadata.
        _chunk.ManifestVersion = 3;
        Write("a.txt", "three");
        await NewBackup().BackupDirectoryAsync(_source);
        var third = ManifestIo.Read(LatestManifestPath());
        third.Version.Should().Be(3);
        third.Files.Should().OnlyContain(f => f.Attributes != null && f.CreatedUtc != null);
        third.Files.Single(f => f.RelativePath == "keep.txt").Chunks.Select(c => c.ChunkId)
            .Should().Equal(secondManifest.Files.Single(f => f.RelativePath == "keep.txt").Chunks.Select(c => c.ChunkId), "dedup across v2 and v3 snapshots");

        // An explicit 2 always writes v2, even after a v3 snapshot exists.
        var v3Path = LatestManifestPath();
        _chunk.ManifestVersion = 2;
        Write("a.txt", "four");
        await NewBackup().BackupDirectoryAsync(_source);
        var fourth = ManifestIo.Read(LatestManifestPath());
        fourth.Version.Should().Be(2);
        fourth.Directories.Should().BeNull();

        // Every snapshot of the mixed v2/v3/v2 group restores, including the v3 one and HEAD (v2).
        using var storage = await OpenStorageAsync();
        foreach (var (remote, expected) in new[] { (v2Remote, "one"), (RemoteOf(_storageDir, second), "two"), (RemoteOf(_storageDir, v3Path), "three"), (RemoteOf(_storageDir, HeadPath()), "four") })
        {
            var target = Path.Combine(_root, "restore_" + Guid.NewGuid().ToString("N"));
            await NewRestore(storage).RestoreFromBackupAsync(remote, target);
            File.ReadAllText(Path.Combine(target, "a.txt")).Should().Be(expected);
            File.ReadAllText(Path.Combine(target, "keep.txt")).Should().Be("unchanged");
        }

        // Verify handles both formats.
        var verifier = new SnapshotIntegrityVerifier(_logger, storage, null, _state, coordinator: _coordinator);
        foreach (var manifestPath in ManifestPaths())
        {
            var result = await verifier.VerifyAsync(RemoteOf(_storageDir, manifestPath));
            result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        }
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(0, 3)]
    [InlineData(2, 2)]
    public async Task NewGroup_FollowsTheExplicitConfigValue_AndAutoMeansV3(int configured, int expected)
    {
        _chunk.ManifestVersion = configured;
        Write("a.txt", "x");
        await NewBackup().BackupDirectoryAsync(_source);
        ManifestIo.Read(LatestManifestPath()).Version.Should().Be(expected);
    }

    [Fact]
    public async Task Restore_Overwrite_ReplacesAnExistingReadOnlyDestination()
    {
        Write("a.txt", "from backup");
        await NewBackup().BackupDirectoryAsync(_source);
        var target = Path.Combine(_root, "ro-target");
        Directory.CreateDirectory(target);
        var existing = Path.Combine(target, "a.txt");
        File.WriteAllText(existing, "old");
        File.SetAttributes(existing, FileAttributes.ReadOnly);

        using var storage = await OpenStorageAsync();
        await NewRestore(storage).RestoreFromBackupAsync(RemoteOf(_storageDir, HeadPath()), target, RestoreOptions.Overwrite);

        File.ReadAllText(existing).Should().Be("from backup");
    }

    [Fact]
    public void ClearReadOnly_RemovesOnlyTheReadOnlyFlag_AndIgnoresMissingFiles()
    {
        var path = Path.Combine(_root, "ro.txt");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Archive);

        Restore.ClearReadOnly(path);

        File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly).Should().BeFalse();
        Restore.ClearReadOnly(Path.Combine(_root, "missing.txt"));
    }

    // ---- restore (H6) --------------------------------------------------------------------------------------------

    [Fact]
    public async Task V3_Restore_RecreatesEmptyDirectoriesAndRestoresTimesAndAttributes()
    {
        var file = Write("a.txt", "timed");
        var old = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, old);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var emptyDir = Path.Combine(_source, "empty");
        Directory.CreateDirectory(emptyDir);
        var emptyTime = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(emptyDir, emptyTime);

        await NewBackup().BackupDirectoryAsync(_source);

        using var storage = await OpenStorageAsync();
        var target = Path.Combine(_root, "restored");
        var outcome = await NewRestore(storage).RestoreFromBackupAsync(
            RemoteOf(_storageDir, HeadPath()), target, RestoreOptions.Overwrite);

        var restored = new FileInfo(Path.Combine(target, "a.txt"));
        restored.LastWriteTimeUtc.Should().BeCloseTo(old, TimeSpan.FromSeconds(2));
        restored.Attributes.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        Directory.Exists(Path.Combine(target, "empty")).Should().BeTrue();
        Directory.GetLastWriteTimeUtc(Path.Combine(target, "empty")).Should().BeCloseTo(emptyTime, TimeSpan.FromSeconds(2));
        outcome.DirectoriesRestored.Should().Be(1);
    }

    [Fact]
    public async Task V2_Restore_DoesNotSetTimesOrCreateDirectories_AsToday()
    {
        var file = Write("a.txt", "plain");
        File.SetLastWriteTimeUtc(file, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.Combine(_source, "empty"));
        await NewBackup().BackupDirectoryAsync(_source);
        DowngradeLatestToV2();

        using var storage = await OpenStorageAsync();
        var target = Path.Combine(_root, "restored-v2");
        await NewRestore(storage).RestoreFromBackupAsync(RemoteOf(_storageDir, HeadPath()), target);

        new FileInfo(Path.Combine(target, "a.txt")).LastWriteTimeUtc.Year.Should().BeGreaterThan(2020);
        Directory.Exists(Path.Combine(target, "empty")).Should().BeFalse();
    }

    [Fact]
    public async Task V3_SelectiveRestore_CreatesOnlyTheSelectedEmptyDirectories_AndConflictPoliciesStillApply()
    {
        Write("a.txt", "alpha");
        Write("b.txt", "beta");
        Directory.CreateDirectory(Path.Combine(_source, "emptyA"));
        Directory.CreateDirectory(Path.Combine(_source, "emptyB"));
        await NewBackup().BackupDirectoryAsync(_source);
        var head = RemoteOf(_storageDir, HeadPath());
        using var storage = await OpenStorageAsync();

        var target = Path.Combine(_root, "selective");
        var selection = new RestoreOptions { RelativePaths = new HashSet<string> { "a.txt", "emptyA" }, ConflictPolicy = RestoreConflictPolicy.Overwrite };
        await NewRestore(storage).RestoreFromBackupAsync(head, target, selection);
        File.Exists(Path.Combine(target, "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(target, "b.txt")).Should().BeFalse();
        Directory.Exists(Path.Combine(target, "emptyA")).Should().BeTrue();
        Directory.Exists(Path.Combine(target, "emptyB")).Should().BeFalse();

        // Skip policy leaves a differing existing file alone.
        File.WriteAllText(Path.Combine(target, "a.txt"), "user edit");
        var outcome = await NewRestore(storage).RestoreFromBackupAsync(head, target, new RestoreOptions { ConflictPolicy = RestoreConflictPolicy.Skip });
        File.ReadAllText(Path.Combine(target, "a.txt")).Should().Be("user edit");
        outcome.FilesSkipped.Should().Be(1);
        File.ReadAllText(Path.Combine(target, "b.txt")).Should().Be("beta");
    }

    [Fact]
    public async Task V3_Restore_RejectsTraversalInDirectoryEntries()
    {
        Write("a.txt", "alpha");
        Directory.CreateDirectory(Path.Combine(_source, "empty"));
        await NewBackup().BackupDirectoryAsync(_source);
        var path = LatestManifestPath();
        var manifest = ManifestIo.Read(path);
        manifest.Directories![0].RelativePath = "../escape";
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);
        ManifestIo.Write(path, manifest);

        using var storage = await OpenStorageAsync();
        var act = () => NewRestore(storage).RestoreFromBackupAsync(RemoteOf(_storageDir, path), Path.Combine(_root, "t"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*outside*");
        Directory.Exists(Path.Combine(_root, "escape")).Should().BeFalse();
    }

    // ---- consumers -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task V3_TamperedManifest_IsDetectedByVerifyAndRestore()
    {
        Write("a.txt", "alpha");
        Directory.CreateDirectory(Path.Combine(_source, "empty"));
        await NewBackup().BackupDirectoryAsync(_source);
        var path = LatestManifestPath();
        var manifest = ManifestIo.Read(path);
        manifest.Directories![0].Attributes = 32; // changed without recomputing the root hash
        ManifestIo.Write(path, manifest);

        using var storage = await OpenStorageAsync();
        var result = await new SnapshotIntegrityVerifier(_logger, storage, null, _state, coordinator: _coordinator).VerifyAsync(RemoteOf(_storageDir, path));
        result.ManifestHashValid.Should().BeFalse();
        result.IsValid.Should().BeFalse();

        var act = () => NewRestore(storage).RestoreFromBackupAsync(RemoteOf(_storageDir, path), Path.Combine(_root, "t2"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*integrity*");
    }

    [Fact]
    public async Task Repair_MarksChunksReferencedByV3Manifests_AndDeletesOnlyOrphans()
    {
        Write("a.txt", "keep me");
        await NewBackup().BackupDirectoryAsync(_source);
        ManifestIo.Read(LatestManifestPath()).Version.Should().Be(3);

        var chunkFiles = Directory.GetFiles(Path.Combine(_storageDir, "chunks"), "*.chunk", SearchOption.AllDirectories);
        chunkFiles.Should().NotBeEmpty();
        var orphanId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var orphanLocal = Path.Combine(_storageDir, SnapshotStoragePaths.GetChunkPath(orphanId).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(orphanLocal)!);
        File.WriteAllBytes(orphanLocal, [1, 2, 3]);

        using var storage = await OpenStorageAsync();
        var report = await new RepositoryRepair(_logger, _config.Object, _state, _coordinator)
            .RunAsync(storage, "local", new RepairOptions { UtcNow = () => DateTime.UtcNow.AddDays(3) });

        report.ChunksDeleted.Should().Be(1);
        File.Exists(orphanLocal).Should().BeFalse();
        chunkFiles.Should().OnlyContain(f => File.Exists(f), "chunks of v3 snapshots are marked and kept");
    }

    [Fact]
    public async Task Discovery_ListsV3AndV2Snapshots_WithTheirFormatVersion()
    {
        Write("a.txt", "one");
        await NewBackup().BackupDirectoryAsync(_source);
        DowngradeLatestToV2();
        _chunk.ManifestVersion = 3;
        Write("a.txt", "two");
        await NewBackup().BackupDirectoryAsync(_source);

        using var storage = await OpenStorageAsync();
        var result = await new RepositoryDiscovery(_logger).DiscoverAsync(storage, null);

        result.Groups.Single().Snapshots.Select(s => s.ManifestVersion).Order().Should().Equal(2, 3);
    }

    [Fact]
    public void FindEmptyDirectories_ReturnsOnlyEmptyOnes_NotTheRootOrParentsOfFiles()
    {
        Directory.CreateDirectory(Path.Combine(_source, "e1"));
        Directory.CreateDirectory(Path.Combine(_source, "full"));
        File.WriteAllText(Path.Combine(_source, "full", "f.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_source, "parent", "e2"));

        var service = new FileSelectionService(_logger, _config.Object);
        var found = service.FindEmptyDirectories(_source).Select(d => Path.GetRelativePath(_source, d.Path).Replace('\\', '/')).Order();

        found.Should().Equal("e1", "parent/e2");
    }
}

using System.Text.Json;
using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// H8: manifests record the chunking algorithm version (not part of the root hash), absent values are
/// inferred from the creation date, and Backup never reuses/carries entries across an algorithm change.
/// </summary>
public class ChunkingAlgorithmVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "algver_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly string _storageDir;
    private readonly string _source;
    private readonly SystemState _state;
    private readonly Mock<IConfigManager> _config = new();
    private readonly RepositoryCoordinator _coordinator;

    public ChunkingAlgorithmVersionTests()
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
        _config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = 2 });
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async () =>
        {
            var storage = new LocalStorage(_logger);
            await storage.InitializeAsync(new Dictionary<string, string> { { "path", _storageDir } });
            return storage;
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private Backup NewBackup(Func<string, Stream>? opener = null) =>
        new(_logger, _state, new SizeAnalyzer(), _config.Object, null, _coordinator) { SourceFileOpener = opener };

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_source, relative);
        File.WriteAllText(path, content);
        return path;
    }

    private string LatestManifestPath() =>
        Directory.GetFiles(_storageDir, "*.manifest.json", SearchOption.AllDirectories)
            .OrderBy(p => ManifestIo.Read(p)!.CreatedUtc)
            .Last();

    /// <summary>Rewrites the latest manifest as an older writer would have: no algorithm field, given creation time.</summary>
    private void RewriteLatestAsLegacy(DateTime createdUtc)
    {
        var path = LatestManifestPath();
        var manifest = ManifestIo.Read(path)!;
        manifest.Profile.AlgorithmVersion = 0;
        manifest.CreatedUtc = createdUtc;
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);
        ManifestIo.Write(path, manifest);

        var head = Directory.GetFiles(_storageDir, "HEAD", SearchOption.AllDirectories).Single();
        var manifestRef = File.ReadAllLines(head)[0];
        File.WriteAllText(head, $"{manifestRef}\n{manifest.RootHash}\n");
    }

    private static Stream OpenShared(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    [Fact]
    public async Task NewManifest_RecordsAlgorithmVersion2_AndRoundTrips()
    {
        Write("a.txt", "hello");
        await NewBackup().BackupDirectoryAsync(_source);

        var manifest = ManifestIo.Read(LatestManifestPath());
        manifest.Profile.AlgorithmVersion.Should().Be(2);
        manifest.GetChunkingAlgorithmVersion().Should().Be(2);
        SnapshotManifestHasher.IsValid(manifest).Should().BeTrue();
    }

    [Fact]
    public void AbsentVersion_IsInferredFromCreationDate()
    {
        new SnapshotManifest { CreatedUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) }
            .GetChunkingAlgorithmVersion().Should().Be(1);
        new SnapshotManifest { CreatedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) }
            .GetChunkingAlgorithmVersion().Should().Be(2);
        new SnapshotManifest { CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Profile = { AlgorithmVersion = 2 } }
            .GetChunkingAlgorithmVersion().Should().Be(2, "an explicit value wins over the date");
    }

    [Fact]
    public void RootHash_IsNotAffectedByAlgorithmVersion_Golden()
    {
        var manifest = new SnapshotManifest
        {
            Version = 2,
            SnapshotId = "snapshot_golden",
            Group = "/golden",
            CreatedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
            BackupMode = "Incremental",
            Profile = { MinChunkSizeBytes = 1024, TargetChunkSizeBytes = 4096, MaxChunkSizeBytes = 8192, RollingHashWindowSize = 48 },
            Files =
            [
                new SnapshotFileManifestEntry
                {
                    RelativePath = "a.txt",
                    SizeBytes = 5,
                    LastModifiedUtc = new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc),
                    ContentHash = "abc",
                    Chunks = [new SnapshotChunkManifestEntry { ChunkId = "c1", ContentHash = "def", PlainSizeBytes = 5, StoredSizeBytes = 9 }]
                }
            ]
        };

        var without = SnapshotManifestHasher.ComputeRootHash(manifest);
        manifest.Profile.AlgorithmVersion = 2;
        var with = SnapshotManifestHasher.ComputeRootHash(manifest);

        with.Should().Be(without);
        with.Should().Be("9712ae07291ef4fc1225683812aee4c217127a5ff031122282a0cef862d8fb8c");
    }

    private void UseManifestVersion(int version) =>
        _config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = version });

    [Fact]
    public async Task V2Writers_KeepAlgorithm2_AndNeverSwitch()
    {
        Write("a.txt", "one");
        await NewBackup().BackupDirectoryAsync(_source);
        Write("b.txt", "two");
        UseManifestVersion(0); // automatic: an existing v2 group stays v2 until the user opts in
        await NewBackup().BackupDirectoryAsync(_source);

        ManifestIo.Read(LatestManifestPath())!.Profile.AlgorithmVersion.Should().Be(2);
        _logger.Messages.Should().NotContain(m => m.Contains("Chunking algorithm changed"));
    }

    [Fact]
    public async Task OptingInToV3_ReChunksOnce_WithAlgorithm3_ThenKeepsReusing()
    {
        Write("a.txt", "one");
        var locked = Write("mail.pst", "version one");
        await NewBackup().BackupDirectoryAsync(_source);

        Write("c.txt", "three");
        UseManifestVersion(3);
        await NewBackup().BackupDirectoryAsync(_source);
        var switched = ManifestIo.Read(LatestManifestPath())!;
        switched.Version.Should().Be(3);
        switched.Profile.AlgorithmVersion.Should().Be(3);
        _logger.Messages.Count(m => m.Contains("Chunking algorithm changed (2 -> 3)")).Should().Be(1);

        // v3 -> v3: entries are carried forward again (no algorithm change).
        File.WriteAllText(locked, "version two, now locked");
        var result = await NewBackup(p => p == locked ? throw new UnauthorizedAccessException("denied") : OpenShared(p))
            .BackupDirectoryAsync(_source);
        result.Skipped.Should().ContainSingle().Which.CarriedForward.Should().BeTrue();
        _logger.Messages.Count(m => m.Contains("Chunking algorithm changed")).Should().Be(1);
    }

    [Fact]
    public async Task LegacyManifest_BeforeV17_IsNotReused_AndLogsOnce()
    {
        Write("good.txt", "good");
        var target = Write("mail.pst", "version one");
        await NewBackup().BackupDirectoryAsync(_source);
        RewriteLatestAsLegacy(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        File.WriteAllText(target, "version two, now locked");
        var result = await NewBackup(p => p == target ? throw new UnauthorizedAccessException("denied") : OpenShared(p))
            .BackupDirectoryAsync(_source);

        result.Skipped.Should().ContainSingle().Which.CarriedForward.Should().BeFalse("entries are not carried across an algorithm change");
        _logger.Messages.Count(m => m.Contains("Chunking algorithm changed")).Should().Be(1);
        var latest = ManifestIo.Read(LatestManifestPath())!;
        latest.Files.Select(f => f.RelativePath).Should().BeEquivalentTo(["good.txt"]);
        latest.Profile.AlgorithmVersion.Should().Be(2);
    }

    [Fact]
    public async Task LegacyManifest_AfterV17_IsReused()
    {
        Write("good.txt", "good");
        var target = Write("mail.pst", "version one");
        await NewBackup().BackupDirectoryAsync(_source);
        RewriteLatestAsLegacy(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        File.WriteAllText(target, "version two, now locked");
        var result = await NewBackup(p => p == target ? throw new UnauthorizedAccessException("denied") : OpenShared(p))
            .BackupDirectoryAsync(_source);

        result.Skipped.Should().ContainSingle().Which.CarriedForward.Should().BeTrue();
        _logger.Messages.Should().NotContain(m => m.Contains("Chunking algorithm changed"));
    }
}

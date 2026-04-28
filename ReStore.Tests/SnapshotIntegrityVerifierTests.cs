using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;
using System.Text.Json;

namespace ReStore.Tests;

public class SnapshotIntegrityVerifierTests : IDisposable
{
    private readonly string _testRoot;
    private readonly ILogger _logger;

    public SnapshotIntegrityVerifierTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreSnapshotVerifierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);

        var loggerMock = new Mock<ILogger>();
        _logger = loggerMock.Object;
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyAsync_ShouldSucceed_ForValidSnapshot()
    {
        var snapshot = await CreateSnapshotAsync(encrypted: false);
        var verifier = new SnapshotIntegrityVerifier(_logger, snapshot.Storage);

        var result = await verifier.VerifyAsync(snapshot.ManifestPath);

        result.IsValid.Should().BeTrue();
        result.ManifestHashValid.Should().BeTrue();
        result.MissingChunks.Should().Be(0);
        result.InvalidChunks.Should().Be(0);
        result.InvalidFiles.Should().Be(0);
    }

    [Fact]
    public async Task VerifyAsync_ShouldFail_WhenChunkObjectIsMissing()
    {
        var snapshot = await CreateSnapshotAsync(encrypted: false);
        var manifest = await LoadManifestAsync(snapshot.StorageRoot, snapshot.ManifestPath);

        var chunkId = manifest.Files
            .SelectMany(file => file.Chunks)
            .Select(chunk => chunk.ChunkId)
            .First();

        var localChunkPath = Path.Combine(
            snapshot.StorageRoot,
            SnapshotStoragePaths.GetChunkPath(chunkId, manifest.ChunkStorageNamespace).Replace('/', Path.DirectorySeparatorChar));

        File.Delete(localChunkPath);

        var verifier = new SnapshotIntegrityVerifier(_logger, snapshot.Storage);
        var result = await verifier.VerifyAsync(snapshot.ManifestPath);

        result.IsValid.Should().BeFalse();
        result.MissingChunks.Should().BeGreaterThan(0);
        result.Errors.Should().Contain(error => error.Contains("Missing chunk object", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_ShouldFail_WhenManifestRootHashIsCorrupted()
    {
        var snapshot = await CreateSnapshotAsync(encrypted: false);
        var manifest = await LoadManifestAsync(snapshot.StorageRoot, snapshot.ManifestPath);

        manifest.RootHash = "deadbeef";

        var manifestLocalPath = Path.Combine(
            snapshot.StorageRoot,
            snapshot.ManifestPath.Replace('/', Path.DirectorySeparatorChar));

        await File.WriteAllTextAsync(manifestLocalPath, JsonSerializer.Serialize(manifest));

        var verifier = new SnapshotIntegrityVerifier(_logger, snapshot.Storage);
        var result = await verifier.VerifyAsync(snapshot.ManifestPath);

        result.IsValid.Should().BeFalse();
        result.ManifestHashValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("Manifest integrity check failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_ShouldFail_WhenHeadRootHashDoesNotMatchManifest()
    {
        var snapshot = await CreateSnapshotAsync(encrypted: false);
        var manifest = await LoadManifestAsync(snapshot.StorageRoot, snapshot.ManifestPath);

        var headPath = SnapshotStoragePaths.GetHeadPath(manifest.Group);
        var localHeadPath = Path.Combine(
            snapshot.StorageRoot,
            headPath.Replace('/', Path.DirectorySeparatorChar));

        await File.WriteAllTextAsync(localHeadPath, $"{snapshot.ManifestPath}\ninvalid-head-root\n");

        var verifier = new SnapshotIntegrityVerifier(_logger, snapshot.Storage);
        var result = await verifier.VerifyAsync(headPath);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("HEAD root hash mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_ShouldFail_WhenManifestContainsInvalidChunkId()
    {
        var snapshot = await CreateSnapshotAsync(encrypted: false);
        var manifest = await LoadManifestAsync(snapshot.StorageRoot, snapshot.ManifestPath);

        var firstChunk = manifest.Files.SelectMany(file => file.Chunks).First();
        firstChunk.ChunkId = "bad/chunk-id";
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);

        var manifestLocalPath = Path.Combine(
            snapshot.StorageRoot,
            snapshot.ManifestPath.Replace('/', Path.DirectorySeparatorChar));

        await File.WriteAllTextAsync(manifestLocalPath, JsonSerializer.Serialize(manifest));

        var verifier = new SnapshotIntegrityVerifier(_logger, snapshot.Storage);
        var result = await verifier.VerifyAsync(snapshot.ManifestPath);

        result.IsValid.Should().BeFalse();
        result.InvalidChunks.Should().BeGreaterThan(0);
        result.Errors.Should().Contain(error => error.Contains("Invalid chunk id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_ShouldFail_WhenManifestContainsInvalidChunkNamespace()
    {
        var snapshot = await CreateSnapshotAsync(encrypted: true);
        var manifest = await LoadManifestAsync(snapshot.StorageRoot, snapshot.ManifestPath);

        manifest.ChunkStorageNamespace = "bad/namespace";

        var manifestLocalPath = Path.Combine(
            snapshot.StorageRoot,
            snapshot.ManifestPath.Replace('/', Path.DirectorySeparatorChar));

        await File.WriteAllTextAsync(manifestLocalPath, JsonSerializer.Serialize(manifest));

        var verifier = new SnapshotIntegrityVerifier(_logger, snapshot.Storage, new StaticPasswordProvider("StrongPassword123!"));
        var result = await verifier.VerifyAsync(snapshot.ManifestPath);

        result.IsValid.Should().BeFalse();
        result.InvalidChunks.Should().BeGreaterThan(0);
        result.Errors.Should().Contain(error => error.Contains("Invalid chunk storage namespace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_ShouldRecordTelemetry_InSystemState()
    {
        var snapshot = await CreateSnapshotAsync(encrypted: false);
        var state = new SystemState(_logger);
        var verifier = new SnapshotIntegrityVerifier(_logger, snapshot.Storage, systemState: state);

        var result = await verifier.VerifyAsync(snapshot.ManifestPath);

        state.Telemetry.Verification.RunCount.Should().Be(1);
        state.Telemetry.Verification.SuccessCount.Should().Be(result.IsValid ? 1 : 0);
        state.Telemetry.Verification.FileCount.Should().Be(result.FileCount);
        state.Telemetry.Verification.ChunkReferences.Should().Be(result.ChunkReferences);
    }

    private async Task<SnapshotTestContext> CreateSnapshotAsync(bool encrypted)
    {
        var sourceDirectory = Path.Combine(_testRoot, "source_" + Guid.NewGuid().ToString("N"));
        var storageDirectory = Path.Combine(_testRoot, "storage_" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(storageDirectory);

        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "alpha.txt"), "alpha-content");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "beta.txt"), "beta-content");

        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string>
        {
            ["path"] = storageDirectory
        });

        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(config => config.Retention).Returns(new RetentionConfig
        {
            Enabled = false,
            KeepLastPerDirectory = 10,
            MaxAgeDays = 30
        });
        configMock.SetupGet(config => config.GlobalStorageType).Returns("local");
        configMock.SetupGet(config => config.SizeThresholdMB).Returns(100);
        configMock.SetupGet(config => config.ExcludedPatterns).Returns([]);
        configMock.SetupGet(config => config.ExcludedPaths).Returns([]);
        configMock.SetupGet(config => config.BackupType).Returns(BackupType.ChunkSnapshot);
        configMock.SetupGet(config => config.WatchDirectories).Returns([]);
        configMock.SetupGet(config => config.MaxFileSizeMB).Returns(100);
        configMock.SetupGet(config => config.ChunkDiffing).Returns(new ChunkDiffingConfig());
        configMock.SetupGet(config => config.Encryption).Returns(new EncryptionConfig
        {
            Enabled = encrypted,
            Salt = encrypted ? Convert.ToBase64String(EncryptionService.GenerateSalt()) : null,
            KeyDerivationIterations = 1000
        });
        configMock.Setup(config => config.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_testRoot, "state_" + Guid.NewGuid().ToString("N") + ".json"));

        IPasswordProvider? passwordProvider = encrypted
            ? new StaticPasswordProvider("StrongPassword123!")
            : null;

        var backup = new Backup(_logger, state, new SizeAnalyzer(), configMock.Object, passwordProvider);
        await backup.BackupDirectoryAsync(sourceDirectory);

        var manifestPath = state.GetPreviousBackupPath(sourceDirectory);
        manifestPath.Should().NotBeNullOrWhiteSpace();

        return new SnapshotTestContext
        {
            Storage = storage,
            StorageRoot = storageDirectory,
            ManifestPath = manifestPath!
        };
    }

    private static async Task<SnapshotManifest> LoadManifestAsync(string storageRoot, string manifestPath)
    {
        var localManifestPath = Path.Combine(storageRoot, manifestPath.Replace('/', Path.DirectorySeparatorChar));
        var json = await File.ReadAllTextAsync(localManifestPath);
        return JsonSerializer.Deserialize<SnapshotManifest>(json)
            ?? throw new InvalidOperationException("Failed to deserialize snapshot manifest from test storage");
    }

    private sealed class SnapshotTestContext
    {
        public LocalStorage Storage { get; init; } = null!;
        public string StorageRoot { get; init; } = string.Empty;
        public string ManifestPath { get; init; } = string.Empty;
    }
}

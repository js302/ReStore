using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// The checked-in bytes under Fixtures/v2 are a compatibility contract: they were produced by the v1.7
/// (manifest v2) code and must keep restoring and verifying after every later format change.
/// Regenerate only deliberately: set RESTORE_REGEN_V2_FIXTURES=&lt;output dir&gt; and run the generator test.
/// </summary>
public class V2FixtureTests : IDisposable
{
    private const string EncryptedPassword = "Fixture-Password-v2!";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreV2Fixtures_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();

    public V2FixtureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    public sealed class FixtureExpectation
    {
        public string ManifestPath { get; set; } = string.Empty;
        public string HeadPath { get; set; } = string.Empty;
        public string? Password { get; set; }
        public Dictionary<string, FixtureFile> Files { get; set; } = [];
    }

    public sealed class FixtureFile
    {
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private static string FixtureDir(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "v2", name);

    [Fact]
    public async Task GenerateV2Fixtures_WhenRequested()
    {
        var output = Environment.GetEnvironmentVariable("RESTORE_REGEN_V2_FIXTURES");
        if (string.IsNullOrWhiteSpace(output))
        {
            return; // skipped by default; the checked-in fixtures are the contract
        }

        await GenerateAsync(Path.Combine(output, "plain"), encrypted: false);
        await GenerateAsync(Path.Combine(output, "encrypted"), encrypted: true);
    }

    private async Task GenerateAsync(string outDir, bool encrypted)
    {
        if (Directory.Exists(outDir))
        {
            Directory.Delete(outDir, true);
        }

        var source = Path.Combine(_root, "src-" + (encrypted ? "enc" : "plain"));
        Directory.CreateDirectory(Path.Combine(source, "docs", "notes"));
        var rng = new Random(20260101);
        var big = new byte[48 * 1024];
        rng.NextBytes(big);
        await File.WriteAllTextAsync(Path.Combine(source, "docs", "readme.txt"), "hello fixture v2\n");
        await File.WriteAllTextAsync(Path.Combine(source, "docs", "notes", "unicode-é.txt"), string.Concat(Enumerable.Repeat("line of text\n", 400)));
        await File.WriteAllBytesAsync(Path.Combine(source, "big.bin"), big);
        await File.WriteAllBytesAsync(Path.Combine(source, "empty.txt"), []);

        var storageDir = Path.Combine(outDir, "storage");
        Directory.CreateDirectory(storageDir);
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDir });

        var configMock = new Mock<IConfigManager>();
        configMock.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        configMock.SetupGet(c => c.GlobalStorageType).Returns("local");
        configMock.SetupGet(c => c.SizeThresholdMB).Returns(100);
        configMock.SetupGet(c => c.ExcludedPatterns).Returns([]);
        configMock.SetupGet(c => c.ExcludedPaths).Returns([]);
        configMock.SetupGet(c => c.BackupType).Returns(BackupType.Full);
        configMock.SetupGet(c => c.WatchDirectories).Returns([]);
        configMock.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { MinChunkSizeKB = 2, TargetChunkSizeKB = 4, MaxChunkSizeKB = 8 });
        configMock.SetupGet(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = encrypted,
            Salt = encrypted ? Convert.ToBase64String(EncryptionService.GenerateSalt()) : null,
            KeyDerivationIterations = 1000
        });
        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);

        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_root, "state-" + Guid.NewGuid().ToString("N") + ".json"));
        IPasswordProvider? provider = encrypted ? new StaticPasswordProvider(EncryptedPassword) : null;
        var backup = new Backup(_logger, state, new SizeAnalyzer(), configMock.Object, provider);
        await backup.BackupDirectoryAsync(source);

        var manifestPath = state.GetPreviousBackupPath(source)!;
        var expectation = new FixtureExpectation
        {
            ManifestPath = manifestPath,
            HeadPath = manifestPath[..manifestPath.LastIndexOf('/')] + "/HEAD",
            Password = encrypted ? EncryptedPassword : null
        };
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            expectation.Files[Path.GetRelativePath(source, file).Replace('\\', '/')] = new FixtureFile
            {
                Size = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
            };
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "expected.json"),
            JsonSerializer.Serialize(expectation, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IEnumerable<object[]> FixtureNames() => [["plain"], ["encrypted"]];

    private async Task<(LocalStorage Storage, FixtureExpectation Expected)> OpenFixtureAsync(string name)
    {
        var fixture = FixtureDir(name);
        Directory.Exists(fixture).Should().BeTrue($"fixture '{name}' must be copied to the output directory");
        var expected = JsonSerializer.Deserialize<FixtureExpectation>(await File.ReadAllTextAsync(Path.Combine(fixture, "expected.json")))!;

        // Work on a copy so restore/verify can never modify the checked-in contract.
        var copy = Path.Combine(_root, "storage-" + name + "-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(fixture, "storage"), copy);
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = copy });
        return (storage, expected);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task V2Fixture_Manifest_IsStillVersion2_AndRootHashValidates(string name)
    {
        var (storage, expected) = await OpenFixtureAsync(name);
        var loaded = await RepositoryDiscovery.LoadManifestAsync(storage, expected.ManifestPath);
        loaded.Manifest.Version.Should().Be(2);
        SnapshotManifestHasher.IsValid(loaded.Manifest).Should().BeTrue();
        loaded.Manifest.EncryptionEnabled.Should().Be(name == "encrypted");
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task V2Fixture_Restores_ExactContent(string name)
    {
        var (storage, expected) = await OpenFixtureAsync(name);
        IPasswordProvider? provider = expected.Password == null ? null : new StaticPasswordProvider(expected.Password);
        var target = Path.Combine(_root, "restored-" + name);

        foreach (var path in new[] { expected.ManifestPath, expected.HeadPath })
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            var restore = new Restore(_logger, storage, provider);
            await restore.RestoreFromBackupAsync(path, target);

            var restored = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(target, f).Replace('\\', '/')).Order().ToList();
            restored.Should().Equal(expected.Files.Keys.Order());
            foreach (var (rel, info) in expected.Files)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar)));
                bytes.Length.Should().Be((int)info.Size);
                Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(info.Sha256, rel);
            }
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task V2Fixture_Verifies(string name)
    {
        var (storage, expected) = await OpenFixtureAsync(name);
        IPasswordProvider? provider = expected.Password == null ? null : new StaticPasswordProvider(expected.Password);
        var verifier = new SnapshotIntegrityVerifier(_logger, storage, provider);

        var result = await verifier.VerifyAsync(expected.ManifestPath);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        result.ManifestHashValid.Should().BeTrue();
        result.FileCount.Should().Be(expected.Files.Count);
        result.InvalidChunks.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task V2Fixture_Manifest_ReserializesToTheExactCheckedInBytes(string name)
    {
        var fixture = FixtureDir(name);
        var expected = JsonSerializer.Deserialize<FixtureExpectation>(await File.ReadAllTextAsync(Path.Combine(fixture, "expected.json")))!;
        var path = Path.Combine(fixture, "storage", expected.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        var original = await File.ReadAllBytesAsync(path);

        var manifest = SnapshotManifestSerializer.Deserialize(original);

        // The fixture was written on Linux (LF); System.Text.Json indents with Environment.NewLine, so the same serializer
        // writes CRLF on Windows (v1.7 did too). Compare against the fixture in this platform's line ending.
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(original).Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
        SnapshotManifestSerializer.Serialize(manifest).Should().Equal(expectedBytes);
    }

    [Fact]
    public async Task V2Fixture_RootHash_GoldenValue_IsUnchanged()
    {
        // Pins the v2 root-hash algorithm itself: recomputing from the fixture must equal the stored hash.
        var (storage, expected) = await OpenFixtureAsync("plain");
        var loaded = await RepositoryDiscovery.LoadManifestAsync(storage, expected.ManifestPath);
        SnapshotManifestHasher.ComputeRootHash(loaded.Manifest).Should().Be(loaded.Manifest.RootHash);
    }
}

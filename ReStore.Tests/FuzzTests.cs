using Moq;
using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.monitoring;
using Bogus;
using Xunit.Abstractions;

namespace ReStore.Tests;

public class FuzzTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _sourceDir;
    private readonly string _backupDir;
    private readonly string _stateDir;
    private readonly Mock<ILogger> _loggerMock;
    private readonly ITestOutputHelper _output;
    private Faker _faker = new();

    public FuzzTests(ITestOutputHelper output)
    {
        _output = output;
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreFuzz_" + Guid.NewGuid());
        _sourceDir = Path.Combine(_testRoot, "Source");
        _backupDir = Path.Combine(_testRoot, "Backups");
        _stateDir = Path.Combine(_testRoot, "State");

        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_backupDir);
        Directory.CreateDirectory(_stateDir);

        _loggerMock = new Mock<ILogger>();
    }

    /// <summary>
    /// Fixed seeds keep the normal run deterministic and replayable: a failure names its seed (also printed for every run), and
    /// setting <c>RESTORE_FUZZ_SEED</c> replays exactly that seed.
    /// </summary>
    public static IEnumerable<object[]> Seeds()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("RESTORE_FUZZ_SEED"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var replay))
        {
            yield return [replay];
            yield break;
        }

        yield return [20260101];
        yield return [7];
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Backup_ShouldHandleChaoticFileStructures(int seed)
    {
        _output.WriteLine($"FuzzTests seed: {seed} (replay with RESTORE_FUZZ_SEED={seed})");
        Randomizer.Seed = new Random(seed);
        _faker = new Faker { Random = new Randomizer(seed) };
        int fileCount = 50;
        var generatedFiles = new List<string>();

        for (int i = 0; i < fileCount; i++)
        {
            var fileName = _faker.System.FileName().Replace(":", "").Replace("?", "").Replace("*", "");
            var subDir = _faker.Random.Bool() ? _faker.Random.AlphaNumeric(5) : "";

            var dirPath = Path.Combine(_sourceDir, subDir);
            Directory.CreateDirectory(dirPath);

            var filePath = Path.Combine(dirPath, fileName);

            var content = _faker.Random.String2(_faker.Random.Int(0, 1000));
            await File.WriteAllTextAsync(filePath, content);
            generatedFiles.Add(filePath);
        }

        var configMock = new Mock<IConfigManager>();
        configMock.Setup(c => c.Retention)
            .Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        configMock.Setup(c => c.GlobalStorageType).Returns("local");
        configMock.Setup(c => c.SizeThresholdMB).Returns(100);
        configMock.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        configMock.Setup(c => c.ExcludedPatterns).Returns(new List<string>());
        configMock.Setup(c => c.ExcludedPaths).Returns(new List<string>());
        configMock.Setup(c => c.BackupType).Returns(BackupType.Full);
        configMock.Setup(c => c.WatchDirectories).Returns(new List<WatchDirectoryConfig>());
        configMock.Setup(c => c.MaxFileSizeMB).Returns(100);
        configMock.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());

        var storage = new LocalStorage(_loggerMock.Object);
        await storage.InitializeAsync(new Dictionary<string, string> { { "path", _backupDir } });

        configMock.Setup(c => c.CreateStorageAsync(It.IsAny<string>()))
            .ReturnsAsync(storage);

        var state = new SystemState(_loggerMock.Object);
        state.SetStateFilePath(Path.Combine(_stateDir, "state.json"));

        var backup = new Backup(
            _loggerMock.Object,
            state,
            new SizeAnalyzer(),
            configMock.Object
        );

        var exception = await Record.ExceptionAsync(() => backup.BackupDirectoryAsync(_sourceDir));

        exception.Should().BeNull();

        var manifests = Directory.GetFiles(_backupDir, "*.manifest.json", SearchOption.AllDirectories);
        manifests.Should().NotBeEmpty();
    }
}

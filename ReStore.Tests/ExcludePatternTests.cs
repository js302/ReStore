using System.Text.Json;
using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>F-15 / M-E1: exclusion patterns, presets, directory pruning, links, cloud-only files, destination exclusion.</summary>
[Collection("GlobalState")]
public class ExcludePatternTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreExclude_" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly TestLogger _logger = new();
    private readonly Mock<IConfigManager> _config = new();
    private List<string> _patterns = [];
    private List<string> _presets = [];
    private List<string> _globs = [];
    private List<string> _paths = [];
    private Dictionary<string, StorageConfig> _storage = [];
    private BackupSettingsConfig _backupSettings = new();

    public ExcludePatternTests()
    {
        _source = Path.Combine(_root, "src");
        Directory.CreateDirectory(_source);
        _config.SetupGet(c => c.ExcludedPatterns).Returns(() => _patterns);
        _config.SetupGet(c => c.ExcludeGlobs).Returns(() => _globs);
        _config.SetupGet(c => c.ExcludePresets).Returns(() => _presets);
        _config.SetupGet(c => c.ExcludedPaths).Returns(() => _paths);
        _config.SetupGet(c => c.StorageSources).Returns(() => _storage);
        _config.SetupGet(c => c.BackupSettings).Returns(() => _backupSettings);
        _config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        _config.SetupGet(c => c.WatchDirectories).Returns(() => [new WatchDirectoryConfig { Path = _source }]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private FileSelectionService Service() => new(_logger, _config.Object);

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(_source, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private List<string> Relative(IEnumerable<string> paths)
        => [.. paths.Select(p => Path.GetRelativePath(_source, p).Replace(Path.DirectorySeparatorChar, '/')).OrderBy(p => p, StringComparer.Ordinal)];

    // ---- glob matcher ----

    [Theory]
    [InlineData("*.tmp", "/a/b/x.tmp", "x.tmp", false, true)]
    [InlineData("*.TMP", "/a/b/x.tmp", "x.tmp", false, true)]
    [InlineData("*.tmp", "/a/b/x.txt", "x.txt", false, false)]
    [InlineData("x?.txt", "/a/xy.txt", "xy.txt", false, true)]
    [InlineData("x?.txt", "/a/x.txt", "x.txt", false, false)]
    [InlineData("node_modules/", "/a/node_modules", "node_modules", true, true)]
    [InlineData("node_modules/", "/a/node_modules", "node_modules", false, false)]
    [InlineData("node_modules/", "/a/node_modules", "node_modules", null, true)]
    [InlineData("node_modules", "/a/node_modules", "node_modules", false, true)]
    [InlineData("**/bin/", "/a/b/bin", "bin", true, true)]
    [InlineData("**/bin/", "/bin", "bin", true, true)]
    [InlineData("**/bin/", "/a/cabin", "cabin", true, false)]
    [InlineData("*/cache/*", "/a/cache/y", "y", false, true)]
    [InlineData("*/cache/*", "/a/b/cache/y", "y", true, true)]
    [InlineData("*/cache/*", "/a/cachex/y", "y", false, false)]
    [InlineData("*/cache/*", "/a/cache/y/z", "z", false, false)]
    [InlineData("a/**/z.txt", "/q/a/z.txt", "z.txt", false, true)]
    [InlineData("a/**/z.txt", "/q/a/b/c/z.txt", "z.txt", false, true)]
    [InlineData("/home/me/skip/", "/home/me/skip", "skip", true, true)]
    [InlineData("/home/me/skip/", "/other/home/me/skip", "skip", true, false)]
    [InlineData("C:\\Data\\skip\\", "C:/Data/skip", "skip", true, true)]
    [InlineData("~$*", "/a/~$doc.docx", "~$doc.docx", false, true)]
    [InlineData("$RECYCLE.BIN/", "/a/$RECYCLE.BIN", "$RECYCLE.BIN", true, true)]
    public void Matcher_FollowsDocumentedSyntax(string pattern, string path, string name, bool? isDirectory, bool expected)
    {
        ExcludePattern.Parse(pattern)!.IsMatch(path, name, isDirectory).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void Matcher_BlankPatternParsesToNull(string pattern) => ExcludePattern.Parse(pattern).Should().BeNull();

    [Fact]
    public void Presets_AreKnownAndNonEmpty()
    {
        foreach (var name in ExcludePresetCatalog.Names)
        {
            ExcludePresetCatalog.IsKnown(name).Should().BeTrue();
            ExcludePresetCatalog.PatternsFor(name).Should().NotBeEmpty();
            ExcludePresetCatalog.PatternsFor(name).Should().OnlyContain(p => ExcludePattern.Parse(p) != null);
        }

        ExcludePresetCatalog.IsKnown("DEV").Should().BeTrue();
        ExcludePresetCatalog.IsKnown("nope").Should().BeFalse();
        ExcludePresetCatalog.PatternsFor("nope").Should().BeEmpty();
    }

    // ---- pruning ----

    [Fact]
    public void DirectoryPattern_PrunesTheWholeSubtree_WithoutListingIt()
    {
        Write("keep.txt");
        Write("src/code.cs");
        Write("node_modules/pkg/index.js");
        Write("node_modules/pkg/deep/more.js");
        Write("sub/node_modules/x.js");
        _globs = ["node_modules/"];
        var listed = new List<string>();
        var service = Service();
        service.DirectoryListed = listed.Add;

        var files = service.ScanFiles([_source]);

        Relative(files.Select(f => f.Path)).Should().BeEquivalentTo(["keep.txt", "src/code.cs"]);
        listed.Should().NotContain(d => d.Contains("node_modules"));
        listed.Should().Contain(_source).And.Contain(Path.Combine(_source, "src")).And.Contain(Path.Combine(_source, "sub"));
    }

    [Fact]
    public void SlashPattern_PrunesMatchingDirectoryAtAnyDepth()
    {
        Write("a/bin/x.dll");
        Write("a/b/bin/y.dll");
        Write("a/binary/z.txt");
        _globs = ["**/bin/"];
        var listed = new List<string>();
        var service = Service();
        service.DirectoryListed = listed.Add;

        Relative(service.ScanFiles([_source]).Select(f => f.Path)).Should().BeEquivalentTo(["a/binary/z.txt"]);
        listed.Should().NotContain(d => d.EndsWith(Path.DirectorySeparatorChar + "bin"));
    }

    [Fact]
    public void FilePattern_StillMatchesFiles_AndNotDirectoriesForDirOnlyPatterns()
    {
        Write("a.tmp");
        Write("tmp/inner.txt");
        _patterns = ["*.tmp"]; _globs = ["tmp/"];

        Relative(Service().ScanFiles([_source]).Select(f => f.Path)).Should().BeEmpty();
    }

    [Fact]
    public void Presets_ExcludeTheirTargets()
    {
        Write("keep.txt");
        Write("~$lock.docx");
        Write("Thumbs.db");
        Write("node_modules/a.js");
        Write("proj/obj/a.dll");
        Write("cache.tmp");
        _presets = ["office-temp", "system", "dev"];

        Relative(Service().ScanFiles([_source]).Select(f => f.Path)).Should().BeEquivalentTo(["cache.tmp", "keep.txt"]);

        _presets = ["caches"];
        Relative(Service().ScanFiles([_source]).Select(f => f.Path)).Should()
            .BeEquivalentTo(["keep.txt", "~$lock.docx", "Thumbs.db", "node_modules/a.js", "proj/obj/a.dll"]);
    }

    [Fact]
    public void ShouldExcludeFile_AppliesSameRulesAsTheScan_ForWatcherAndIncrementalPaths()
    {
        var inPruned = Write("node_modules/pkg/index.js");
        var kept = Write("src/index.js");
        var temp = Write("~$x.docx");
        var deletedInPruned = Path.Combine(_source, "node_modules", "gone.js");
        var prunedDir = Path.Combine(_source, "node_modules");
        _globs = ["node_modules/"];
        _presets = ["office-temp"];
        var service = Service();

        service.ShouldExcludeFile(inPruned).Should().BeTrue();
        service.ShouldExcludeFile(deletedInPruned).Should().BeTrue();
        service.ShouldExcludeFile(prunedDir).Should().BeTrue();
        service.ShouldExcludeFile(temp).Should().BeTrue();
        service.ShouldExcludeFile(kept).Should().BeFalse();
    }

    [Fact]
    public void Patterns_DoNotApplyToDirectoriesAboveTheWatchedRoot()
    {
        var parent = Path.Combine(_root, "bin");
        var watched = Path.Combine(parent, "data");
        Directory.CreateDirectory(watched);
        var file = Path.Combine(watched, "a.txt");
        File.WriteAllText(file, "x");
        _globs = ["bin/"];
        _config.SetupGet(c => c.WatchDirectories).Returns([new WatchDirectoryConfig { Path = watched }]);
        var service = Service();

        service.ShouldExcludeFile(file).Should().BeFalse();
        service.ScanFiles([watched]).Should().ContainSingle();
    }

    [Fact]
    public void ExcludedPath_PrunesTheDirectory()
    {
        Write("keep/a.txt");
        Write("skip/b.txt");
        _paths = [Path.Combine(_source, "skip")];
        var listed = new List<string>();
        var service = Service();
        service.DirectoryListed = listed.Add;

        Relative(service.ScanFiles([_source]).Select(f => f.Path)).Should().BeEquivalentTo(["keep/a.txt"]);
        listed.Should().NotContain(d => d.EndsWith("skip"));
    }

    // ---- legacy excludedPatterns stay file-only, name-only ----

    [Fact]
    public void LegacyPatterns_StayFileOnly_AndNeverPruneDirectories()
    {
        Write("x.log/inner.txt");
        Write("templates/page.html");
        Write("temp1.txt");
        Write("node_modules/a.js");
        Write("a/b.txt");
        Write("keep.txt");
        _patterns = ["*.log", "temp*", "a/b.txt", "node_modules"];
        var listed = new List<string>();
        var service = Service();
        service.DirectoryListed = listed.Add;

        Relative(service.ScanFiles([_source]).Select(f => f.Path)).Should()
            .BeEquivalentTo(["x.log/inner.txt", "templates/page.html", "node_modules/a.js", "a/b.txt", "keep.txt"]);
        listed.Should().Contain(d => d.EndsWith("x.log")).And.Contain(d => d.EndsWith("templates")).And.Contain(d => d.EndsWith("node_modules"));
        service.ShouldExcludeFile(Path.Combine(_source, "x.log", "inner.txt")).Should().BeFalse();
    }

    [Fact]
    public void Globs_PruneWithTrailingSlash()
    {
        Write("node_modules/a.js");
        Write("templates/page.html");
        _globs = ["node_modules/"];

        Relative(Service().ScanFiles([_source]).Select(f => f.Path)).Should().BeEquivalentTo(["templates/page.html"]);
    }

    // ---- links ----

    [Theory]
    [InlineData(FileAttributes.Directory, null, false)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, null, false)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, "C:\\target", true)]
    public void IsLinkDirectory_OnlyNameSurrogatesAreLinks(FileAttributes attributes, string? target, bool expected)
        => FileSelectionService.IsLinkDirectory(attributes, () => target).Should().Be(expected);

    [Fact]
    public void IsLinkDirectory_UnreadableTarget_CountsAsLink()
        => FileSelectionService.IsLinkDirectory(FileAttributes.ReparsePoint, () => throw new IOException()).Should().BeTrue();

    [Fact]
    public void FileSymlink_IsFollowed_ContentIsBackedUp()
    {
        var target = Write("target.txt");
        try { File.CreateSymbolicLink(Path.Combine(_source, "filelink.txt"), target); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }

        Relative(Service().ScanFiles([_source]).Select(f => f.Path)).Should().Contain("filelink.txt");
    }

    [Fact]
    public void DirectorySymlink_IsNotFollowed_AndLoopsTerminate()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
        Write("real/a.txt");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_source, "link"), outside);
            Directory.CreateSymbolicLink(Path.Combine(_source, "real", "loop"), _source);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // symlinks unavailable on this machine
        }

        var listed = new List<string>();
        var service = Service();
        service.DirectoryListed = listed.Add;

        Relative(service.ScanFiles([_source]).Select(f => f.Path)).Should().BeEquivalentTo(["real/a.txt"]);
        listed.Should().NotContain(d => d.EndsWith("link") || d.EndsWith("loop") || d.Contains("outside"));
        _logger.Messages.Should().Contain(m => m.Contains("Not following 2 directory junction"));
    }

    // ---- cloud-only ----

    [Theory]
    [InlineData(0x400000, true)]
    [InlineData(0x1000, true)]
    [InlineData(0x40000, true)]
    [InlineData(0x20, false)]
    [InlineData(0x400, false)]
    public void IsCloudOnly_RecognisesPlaceholderAttributes(int attributes, bool expected)
        => FileSelectionService.IsCloudOnly((FileAttributes)attributes).Should().Be(expected);

    [Fact]
    public void CloudOnlyFiles_AreSkippedAndReported_UnlessHydrationIsOn()
    {
        Write("local.txt");
        var cloud = Write("cloud.docx");
        var service = Service();
        service.ScanInterceptor = f => f.Path == cloud ? f with { Attributes = f.Attributes | (FileAttributes)0x400000 } : f;
        var cloudOnly = new List<ScannedFile>();

        Relative(service.ScanFiles([_source], null, cloudOnly).Select(f => f.Path)).Should().BeEquivalentTo(["local.txt"]);
        cloudOnly.Should().ContainSingle().Which.Path.Should().Be(cloud);

        _backupSettings = new BackupSettingsConfig { HydrateCloudFiles = true };
        cloudOnly.Clear();
        Relative(service.ScanFiles([_source], null, cloudOnly).Select(f => f.Path)).Should().BeEquivalentTo(["cloud.docx", "local.txt"]);
        cloudOnly.Should().BeEmpty();
    }

    [Fact]
    public void CloudOnlyFile_ExcludedByPattern_IsNotReported()
    {
        var cloud = Write("cloud.tmp");
        _patterns = ["*.tmp"];
        var service = Service();
        service.ScanInterceptor = f => f with { Attributes = f.Attributes | (FileAttributes)0x1000 };
        var cloudOnly = new List<ScannedFile>();

        service.ScanFiles([_source], null, cloudOnly).Should().BeEmpty();
        cloudOnly.Should().BeEmpty();
        File.Exists(cloud).Should().BeTrue();
    }

    // ---- Backup integration ----

    private Backup NewBackup(string storageDir, SystemState state)
    {
        var config = new Mock<IConfigManager>();
        config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 10, MaxAgeDays = 30 });
        config.Setup(c => c.GlobalStorageType).Returns("local");
        config.Setup(c => c.SizeThresholdMB).Returns(100);
        config.Setup(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        config.Setup(c => c.ExcludedPatterns).Returns([]);
        config.Setup(c => c.ExcludedPaths).Returns([]);
        config.Setup(c => c.BackupType).Returns(BackupType.Incremental);
        config.Setup(c => c.WatchDirectories).Returns([]);
        config.Setup(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async () =>
        {
            var storage = new LocalStorage(_logger);
            await storage.InitializeAsync(new Dictionary<string, string> { { "path", storageDir } });
            return storage;
        });
        return new Backup(_logger, state, new SizeAnalyzer(), config.Object, null, new RepositoryCoordinator(Path.Combine(_root, "repository.lock")));
    }

    private static List<string> ManifestPaths(string storageDir)
        => [.. Directory.GetFiles(storageDir, "*.manifest.json", SearchOption.AllDirectories)
            .Select(p => ManifestIo.Read(p)!)
            .OrderBy(m => m.CreatedUtc)
            .Last().Files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal)];

    [Fact]
    public async Task Backup_SkipsCloudOnlyFile_ReportsIt_AndKeepsThePreviousVersion()
    {
        var storageDir = Path.Combine(_root, "storage");
        Directory.CreateDirectory(storageDir);
        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_root, "state.json"));
        var local = Write("local.txt", "one");
        var cloud = Write("cloud.docx", "cloud");

        // Never backed up and cloud-only: skipped, not in the snapshot.
        var first = NewBackup(storageDir, state);
        first.FileSelection.ScanInterceptor = f => f.Path == cloud ? f with { Attributes = f.Attributes | (FileAttributes)0x400000 } : f;
        var result = await first.BackupDirectoryAsync(_source);
        result.Skipped.Should().ContainSingle(s => s.Path == cloud && s.Reason.Contains("cloud-only") && !s.CarriedForward);
        ManifestPaths(storageDir).Should().Equal("local.txt");

        // Backed up while local, later dehydrated: the snapshot keeps the old copy.
        var second = NewBackup(storageDir, state);
        await second.BackupDirectoryAsync(_source);
        ManifestPaths(storageDir).Should().Equal("cloud.docx", "local.txt");

        File.WriteAllText(local, "two, changed");
        var third = NewBackup(storageDir, state);
        third.FileSelection.ScanInterceptor = f => f.Path == cloud ? f with { Attributes = f.Attributes | (FileAttributes)0x400000 } : f;
        result = await third.BackupDirectoryAsync(_source);
        result.Skipped.Should().ContainSingle(s => s.Path == cloud && s.CarriedForward);
        ManifestPaths(storageDir).Should().Equal("cloud.docx", "local.txt");

        // Nothing else changed: still reported, and the file is not treated as deleted.
        var fourth = NewBackup(storageDir, state);
        fourth.FileSelection.ScanInterceptor = third.FileSelection.ScanInterceptor;
        result = await fourth.BackupDirectoryAsync(_source);
        result.Skipped.Should().ContainSingle(s => s.Path == cloud && s.CarriedForward);
        state.GetTrackedFiles(_source, "local").Should().Contain(cloud);
    }

    [Fact]
    public async Task Incremental_CloudOnlyChangedFile_KeepsPreviousEntry_AndIsReported()
    {
        var storageDir = Path.Combine(_root, "storage");
        Directory.CreateDirectory(storageDir);
        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_root, "state.json"));
        var local = Write("local.txt", "one");
        var cloud = Write("cloud.docx", "cloud");
        await NewBackup(storageDir, state).BackupDirectoryAsync(_source);

        // Both change; the cloud one is now a placeholder, as reported by the real stat.
        File.WriteAllText(local, "two");
        File.WriteAllText(cloud, "cloud changed");
        var incremental = NewBackup(storageDir, state);
        var service = incremental.FileSelection;
        service.ScanInterceptor = f => f.Path == cloud ? f with { Attributes = f.Attributes | (FileAttributes)0x400000 } : f;
        var before = Directory.GetFiles(storageDir, "*.manifest.json", SearchOption.AllDirectories).Length;
        var result = await incremental.BackupFilesAsync([local, cloud], _source);

        Directory.GetFiles(storageDir, "*.manifest.json", SearchOption.AllDirectories).Length.Should().BeGreaterThan(before);
        ManifestPaths(storageDir).Should().Equal("cloud.docx", "local.txt");
        incremental.TreeScanCount.Should().Be(0, "the incremental path must be the one exercised");
        result.Skipped.Should().ContainSingle(s => s.Path == cloud && s.CarriedForward);
        state.MetadataFor(_source, cloud).Hash.Should().NotBeNullOrEmpty();
    }

    // ---- destination ----

    [Fact]
    public void LocalDestinationInsideWatchedFolder_IsExcludedAutomatically()
    {
        Write("a.txt");
        var destination = Path.Combine(_source, "backups");
        Write("backups/chunks/abc");
        _storage = new() { ["local"] = new StorageConfig { Path = destination, Options = new() { ["path"] = destination } } };
        var service = Service();

        Relative(service.ScanFiles([_source]).Select(f => f.Path)).Should().BeEquivalentTo(["a.txt"]);
        service.ShouldExcludeFile(Path.Combine(destination, "chunks", "abc")).Should().BeTrue();
    }

    [Fact]
    public void DestinationFromEnvironmentVariable_IsExpandedForTheComparison()
    {
        Write("a.txt");
        Write("dest/x");
        Environment.SetEnvironmentVariable("RESTORE_TEST_DEST", Path.Combine(_source, "dest"));
        try
        {
            _storage = new() { ["local"] = new StorageConfig { Path = "%RESTORE_TEST_DEST%" } };
            Relative(Service().ScanFiles([_source]).Select(f => f.Path)).Should().BeEquivalentTo(["a.txt"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RESTORE_TEST_DEST", null);
        }
    }

    [Fact]
    public void AppDataDirectoryInsideWatchedFolder_IsExcludedAutomatically()
    {
        Write("a.txt");
        var previous = AppPaths.Root;
        try
        {
            AppPaths.OverrideRoot(Path.Combine(_source, "ReStoreData"));
            Write("ReStoreData/config.json");
            Relative(Service().ScanFiles([_source]).Select(f => f.Path)).Should().BeEquivalentTo(["a.txt"]);
        }
        finally
        {
            AppPaths.OverrideRoot(previous);
        }
    }

    [Fact]
    public void DestinationContainingTheWatchedFolder_IsNotExcluded()
    {
        Write("a.txt");
        _storage = new() { ["local"] = new StorageConfig { Path = _root } };

        Relative(Service().ScanFiles([_source]).Select(f => f.Path)).Should().BeEquivalentTo(["a.txt"]);
    }

    [Fact]
    public void Validator_WarnsAboutDestinationOverlap_AndUnknownPresets()
    {
        var destination = Path.Combine(_source, "backups");
        Directory.CreateDirectory(destination);
        _storage = new() { ["local"] = new StorageConfig { Path = destination } };
        _presets = ["system", "bogus"];
        _config.SetupGet(c => c.GlobalStorageType).Returns("local");
        _config.SetupGet(c => c.BackupInterval).Returns(TimeSpan.FromHours(1));
        _config.SetupGet(c => c.SizeThresholdMB).Returns(500);
        _config.SetupGet(c => c.BackupType).Returns(BackupType.Incremental);
        _config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false, KeepLastPerDirectory = 5, MaxAgeDays = 30 });
        _config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig());
        _config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig());
        _config.SetupGet(c => c.SystemBackup).Returns(new SystemBackupConfig());

        var result = new ConfigValidator(_logger).ValidateConfiguration(_config.Object);

        result.Warnings.Should().Contain(w => w.Contains("inside the watched folder") && w.Contains("excluded from backups automatically"));
        result.Warnings.Should().Contain(w => w.Contains("Unknown exclude preset 'bogus'"));
        result.Warnings.Should().NotContain(w => w.Contains("'system'"));

        _storage = new() { ["local"] = new StorageConfig { Path = _root } };
        new ConfigValidator(_logger).ValidateConfiguration(_config.Object).Warnings
            .Should().Contain(w => w.Contains("is inside the backup destination"));
    }

    // ---- config ----

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Config_PresetsAndHydrateRoundTrip_AndDefaultsForOldConfigs()
    {
        var manager = new ConfigManager(_logger);
        var load = typeof(ConfigManager).GetMethod("LoadExclusions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        load.Invoke(manager, [Parse("{\"excludedPatterns\":[],\"excludedPaths\":[]}")]);
        manager.ExcludePresets.Should().BeEmpty("an existing config keeps its behaviour");

        load.Invoke(manager, [Parse("{\"excludePresets\":[\"dev\",\"system\"]}")]);
        manager.ExcludePresets.Should().Equal("dev", "system");

        var settings = typeof(ConfigManager).GetMethod("LoadBackupSettings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        settings.Invoke(manager, [Parse("{\"backup\":{\"hydrateCloudFiles\":true}}")]);
        manager.BackupSettings.HydrateCloudFiles.Should().BeTrue();
        settings.Invoke(manager, [Parse("{\"backup\":{\"deepScanIntervalDays\":3}}")]);
        manager.BackupSettings.HydrateCloudFiles.Should().BeFalse();
    }

    [Fact]
    public void SchemaMigration_AddsHydrateCloudFilesDefault()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse("{\"configSchemaVersion\":4,\"backup\":{\"deepScanIntervalDays\":7}}")!.AsObject();

        ConfigSchemaManager.Migrate(root);

        root["backup"]!["hydrateCloudFiles"]!.GetValue<bool>().Should().BeFalse();
    }
}

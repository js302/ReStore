using FluentAssertions;
using Moq;
using System.Security.Cryptography;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Manifests written by an attacker (or corrupted): hostile relative paths, directory entries, absurd sizes, path-like chunk ids and
/// duplicate paths, in plain v2, plain v3 and encrypted v3 snapshots, plus links planted inside the restore target. A restore must
/// refuse or contain every one of them: nothing may be written outside the target, and only clean exception types may come out.
/// </summary>
public class HostileManifestTests : IDisposable
{
    private const string Password = ManifestIo.DefaultPassword;

    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "hostile_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly string _storageDir;
    private readonly string _source;
    private readonly SystemState _state;
    private readonly RepositoryCoordinator _coordinator;
    private string? _manifestPath;
    private bool _encrypted;

    public HostileManifestTests()
    {
        _storageDir = Path.Combine(_root, "storage");
        _source = Path.GetFullPath(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_storageDir);
        Directory.CreateDirectory(_source);
        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "locks", "repository.lock"));
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "locks", "state.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<LocalStorage> OpenStorageAsync()
    {
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = _storageDir });
        return storage;
    }

    private static readonly Dictionary<(int Version, bool Encrypted), string> PristineSnapshots = [];
    private static readonly SemaphoreSlim PristineLock = new(1, 1);

    /// <summary>Backs up a small tree (two files, one empty directory) in the requested format; built once per format and copied afterwards.</summary>
    private async Task CreateSnapshotAsync(int manifestVersion, bool encrypted)
    {
        _encrypted = encrypted;
        string pristine;
        await PristineLock.WaitAsync();
        try
        {
            if (!PristineSnapshots.TryGetValue((manifestVersion, encrypted), out pristine!))
            {
                pristine = Path.Combine(TestEnvironment.RootDirectory, "hostile-pristine-" + manifestVersion + (encrypted ? "-enc" : "") + "-" + Guid.NewGuid().ToString("N"));
                await BuildSnapshotAsync(manifestVersion, encrypted, Path.Combine(pristine, "storage"), Path.Combine(pristine, "src"));
                PristineSnapshots[(manifestVersion, encrypted)] = pristine;
            }
        }
        finally
        {
            PristineLock.Release();
        }

        CopyDirectory(Path.Combine(pristine, "storage"), _storageDir);
        CopyDirectory(Path.Combine(pristine, "src"), _source);
        _manifestPath = Directory.GetFiles(_storageDir, "*.manifest.json", SearchOption.AllDirectories).Single();
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    private async Task BuildSnapshotAsync(int manifestVersion, bool encrypted, string storageDir, string sourceDir)
    {
        Directory.CreateDirectory(storageDir);
        Directory.CreateDirectory(sourceDir);
        var buildLocks = Path.Combine(_root, "build-locks");
        Directory.CreateDirectory(buildLocks);
        var chunk = new ChunkDiffingConfig { ManifestVersion = manifestVersion };
        var config = new Mock<IConfigManager>();
        config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        config.Setup(c => c.GlobalStorageType).Returns("local");
        config.Setup(c => c.SizeThresholdMB).Returns(100);
        config.Setup(c => c.ExcludedPatterns).Returns([]);
        config.Setup(c => c.ExcludedPaths).Returns([]);
        config.Setup(c => c.BackupType).Returns(BackupType.Incremental);
        config.Setup(c => c.WatchDirectories).Returns([]);
        config.Setup(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.ChunkDiffing).Returns(chunk);
        config.Setup(c => c.Encryption).Returns(new EncryptionConfig
        {
            Enabled = encrypted,
            Salt = encrypted ? Convert.ToBase64String(EncryptionService.GenerateSalt()) : null,
            KeyDerivationIterations = 1000
        });
        config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async () =>
        {
            var storage = new LocalStorage(_logger);
            await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = storageDir });
            return storage;
        });

        File.WriteAllText(Path.Combine(sourceDir, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(sourceDir, "sub"));
        File.WriteAllText(Path.Combine(sourceDir, "sub", "b.txt"), "beta");
        Directory.CreateDirectory(Path.Combine(sourceDir, "emptydir"));

        var provider = new Mock<IPurposePasswordProvider>();
        provider.Setup(p => p.GetPasswordAsync()).ReturnsAsync(Password);
        provider.Setup(p => p.GetPasswordAsync(It.IsAny<PasswordPurpose>())).ReturnsAsync(Password);
        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(buildLocks, "state.json"));
        await new Backup(_logger, state, new SizeAnalyzer(), config.Object, encrypted ? provider.Object : null, new RepositoryCoordinator(Path.Combine(buildLocks, "repository.lock")))
            .BackupDirectoryAsync(Path.GetFullPath(sourceDir));
    }

    private void Mutate(Action<SnapshotManifest> change)
    {
        var manifest = ManifestIo.Read(_manifestPath!, Password);
        change(manifest);
        manifest.RootHash = SnapshotManifestHasher.ComputeRootHash(manifest);
        if (_encrypted)
        {
            using var keys = ManifestIo.UnlockNear(_manifestPath!, Password);
            File.WriteAllBytes(_manifestPath!, SnapshotManifestSerializer.Serialize(manifest, keys));
        }
        else
        {
            ManifestIo.Write(_manifestPath!, manifest);
        }
    }

    private Restore NewRestore(LocalStorage storage)
    {
        var provider = new Mock<IPurposePasswordProvider>();
        provider.Setup(p => p.GetPasswordAsync()).ReturnsAsync(Password);
        provider.Setup(p => p.GetPasswordAsync(It.IsAny<PasswordPurpose>())).ReturnsAsync(Password);
        return new Restore(_logger, storage, _encrypted ? provider.Object : null, _state, _coordinator);
    }

    private string Remote => Path.GetRelativePath(_storageDir, _manifestPath!).Replace(Path.DirectorySeparatorChar, '/');

    private string Target => Path.Combine(_root, "target");

    private HashSet<string> Everything() =>
        [.. Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(Path.Combine(_root, "locks"), StringComparison.Ordinal))];

    /// <summary>Restores into <see cref="Target"/> and returns what happened; asserts nothing was created outside it.</summary>
    private async Task<Exception?> RestoreAndCheckContainmentAsync(bool dryRun = false)
    {
        Directory.CreateDirectory(Path.Combine(_root, "target-evil"));
        var before = Everything();
        Exception? error = null;
        using var storage = await OpenStorageAsync();
        try
        {
            await NewRestore(storage)
                .RestoreFromBackupAsync(Remote, Target, new RestoreOptions { ConflictPolicy = RestoreConflictPolicy.Overwrite, DryRun = dryRun });
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var created = Everything().Except(before).ToList();
        var targetPrefix = Target + Path.DirectorySeparatorChar;
        created.Where(path => path != Target && !path.StartsWith(targetPrefix, StringComparison.Ordinal))
            .Should().BeEmpty("a hostile manifest must not create anything outside the restore target");
        return error;
    }

    private static void AssertCleanFailure(Exception? error, string context = "")
    {
        if (error == null)
        {
            return;
        }

        error.Should().BeAssignableTo<Exception>();
        (error is InvalidOperationException or ArgumentException or IOException or FormatException or InvalidDataException or CryptographicException or UnauthorizedAccessException or NotSupportedException)
            .Should().BeTrue($"{context} restore of a hostile manifest must fail with a clean error, not {error.GetType().Name}: {error.Message}");
    }

    public static IEnumerable<object[]> Formats() =>
    [
        [2, false],
        [3, false],
        [3, true]
    ];

    /// <summary>Paths that leave the target on every OS: a restore must refuse them.</summary>
    public static IEnumerable<object[]> EscapingPaths()
    {
        string[] paths = ["/etc/evil.txt", "../evil.txt", "../../evil.txt", "a/../../evil.txt", "sub/../../evil.txt", "../target-evil/evil.txt", "..", "a/b/../../../evil.txt"];
        foreach (var format in Formats())
        {
            foreach (var path in paths)
            {
                yield return [format[0], format[1], path];
            }
        }
    }

    /// <summary>Windows-specific shapes: contained file names on Linux, refused on Windows.</summary>
    public static IEnumerable<object[]> WindowsShapedPaths()
    {
        string[] paths =
        [
            "C:\\evil.txt", "C:evil.txt", "C:/evil.txt", "\\\\srv\\share\\evil.txt", "\\\\?\\C:\\evil.txt", "x.txt:stream", "CON", "nul.txt", "aux/evil.txt",
            "..\\evil.txt", "..\\..\\evil.txt", "..\\target-evil\\evil.txt", "trailing.", "evil.txt "
        ];
        foreach (var format in Formats())
        {
            foreach (var path in paths)
            {
                yield return [format[0], format[1], path];
            }
        }
    }

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task FileEntry_WithEscapingPath_IsRefused(int version, bool encrypted, string path)
    {
        await CreateSnapshotAsync(version, encrypted);
        Mutate(m => m.Files[0].RelativePath = path);

        var error = await RestoreAndCheckContainmentAsync();

        error.Should().BeOfType<InvalidOperationException>();
        File.Exists(Path.Combine(_root, "evil.txt")).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public async Task FileEntry_WithEscapingPath_IsRefusedByTheDryRunToo(int version, bool encrypted, string path)
    {
        await CreateSnapshotAsync(version, encrypted);
        Mutate(m => m.Files[0].RelativePath = path);

        var error = await RestoreAndCheckContainmentAsync(dryRun: true);

        error.Should().BeOfType<InvalidOperationException>();
    }

    [Theory]
    [MemberData(nameof(WindowsShapedPaths))]
    public async Task FileEntry_WithWindowsShapedPath_IsContainedOrRefused(int version, bool encrypted, string path)
    {
        await CreateSnapshotAsync(version, encrypted);
        Mutate(m => m.Files[0].RelativePath = path);

        AssertCleanFailure(await RestoreAndCheckContainmentAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryEntry_WithEscapingPath_IsRefused(bool encrypted)
    {
        await CreateSnapshotAsync(3, encrypted);
        foreach (var path in new[] { "../evil-dir", "/tmp/evil-dir", "a/../../evil-dir", "../target-evil/evil-dir" })
        {
            Mutate(m => m.Directories![0].RelativePath = path);
            var error = await RestoreAndCheckContainmentAsync();
            error.Should().BeOfType<InvalidOperationException>($"directory entry '{path}'");
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task ChunkIds_ThatAreNotChunkIds_AreRefused(int version, bool encrypted)
    {
        await CreateSnapshotAsync(version, encrypted);
        foreach (var id in new[] { "../../../../etc/passwd", "ab/../../x", "..", "", " ", "C:\\x", "a/b", "%2e%2e%2f", new string('a', 5000), "zz", "00" })
        {
            Mutate(m => m.Files[0].Chunks[0].ChunkId = id);
            var error = await RestoreAndCheckContainmentAsync();
            error.Should().NotBeNull($"chunk id '{(id.Length > 40 ? id[..40] + "..." : id)}' cannot be valid");
            AssertCleanFailure(error);
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task AbsurdSizesAndCounts_FailCleanly(int version, bool encrypted)
    {
        await CreateSnapshotAsync(version, encrypted);
        Action<SnapshotManifest>[] mutations =
        [
            m => m.Files[0].SizeBytes = long.MaxValue,
            m => m.Files[0].SizeBytes = -1,
            m => m.Files[0].Chunks[0].PlainSizeBytes = int.MaxValue,
            m => m.Files[0].Chunks[0].PlainSizeBytes = -5,
            m => m.Files[0].Chunks[0].StoredSizeBytes = int.MaxValue,
            m => m.Files[0].Chunks = [],
            m => m.Files[0].Chunks = [.. Enumerable.Repeat(m.Files[0].Chunks[0], 100_000)],
            m => m.KeyDerivationIterations = int.MaxValue,
            m => m.KeyDerivationIterations = -1,
            m => m.Profile.MaxChunkSizeBytes = -1,
            m => m.Files[0].ContentHash = "",
            m => m.Files[0].RelativePath = new string('x', 100_000)
        ];

        for (var i = 0; i < mutations.Length; i++)
        {
            if (i > 0)
            {
                // Mutations are cumulative on the stored file; start each from a fresh snapshot.
                await ResetAsync(version, encrypted);
            }

            Mutate(mutations[i]);
            var error = await RestoreAndCheckContainmentAsync();
            AssertCleanFailure(error, $"mutation #{i}");
        }
    }

    private async Task ResetAsync(int version, bool encrypted)
    {
        Directory.Delete(_storageDir, true);
        Directory.Delete(_source, true);
        await CreateSnapshotAsync(version, encrypted);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task DuplicatePaths_RestoreWithoutEscapingOrCrashing(int version, bool encrypted)
    {
        await CreateSnapshotAsync(version, encrypted);
        Mutate(m =>
        {
            m.Files.Add(m.Files[0]);
            m.Files.Add(new SnapshotFileManifestEntry
            {
                RelativePath = m.Files[0].RelativePath.ToUpperInvariant(),
                Chunks = m.Files[1].Chunks,
                ContentHash = m.Files[1].ContentHash,
                SizeBytes = m.Files[1].SizeBytes
            });
            if (m.Directories != null)
            {
                m.Directories.Add(m.Directories[0]);
            }
        });

        AssertCleanFailure(await RestoreAndCheckContainmentAsync());
    }

    [Theory]
    [InlineData("{\"Version\":2,\"Files\":null}")]
    [InlineData("{\"Version\":2,\"Files\":[null]}")]
    [InlineData("{\"Version\":2,\"Files\":[{\"Chunks\":null}]}")]
    [InlineData("{\"Version\":2,\"Files\":[{\"Chunks\":[null]}]}")]
    [InlineData("{\"Version\":2,\"Files\":[{\"RelativePath\":null}]}")]
    [InlineData("{\"Version\":3,\"Profile\":null}")]
    [InlineData("{\"Version\":3,\"Directories\":[null]}")]
    [InlineData("{\"Version\":3,\"SnapshotId\":null}")]
    public void ManifestWithNullMembers_IsRefusedCleanly_NotWithANullReference(string json)
    {
        var act = () => SnapshotManifestSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

        act.Should().Throw<InvalidOperationException>().WithMessage("*malformed*");
    }

    [Fact]
    public void ManifestWithNegativeOrOverflowingSizes_IsRefusedWhenRead()
    {
        var negative = "{\"Version\":2,\"Files\":[{\"RelativePath\":\"a\",\"SizeBytes\":-1,\"ContentHash\":\"x\",\"Chunks\":[]}]}";
        var overflow = "{\"Version\":2,\"Files\":[{\"RelativePath\":\"a\",\"SizeBytes\":9223372036854775807,\"ContentHash\":\"x\",\"Chunks\":[]},{\"RelativePath\":\"b\",\"SizeBytes\":9223372036854775807,\"ContentHash\":\"x\",\"Chunks\":[]}]}";

        foreach (var json in new[] { negative, overflow })
        {
            var act = () => SnapshotManifestSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));
            act.Should().Throw<InvalidOperationException>();
        }
    }

    // ---- links planted inside the restore target -------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task RestoreDoesNotWriteThroughADirectoryLinkInsideTheTarget(int version, bool encrypted)
    {
        await CreateSnapshotAsync(version, encrypted);
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Target);
        CreateDirectoryLink(Path.Combine(Target, "sub"), outside);

        var error = await RestoreAndCheckContainmentLinksAsync(outside);

        Directory.EnumerateFileSystemEntries(outside).Should().BeEmpty("restore must not follow a link out of the target");
        error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("link");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task RestoreDoesNotCreateEmptyDirectoriesThroughALinkInsideTheTarget(int version, bool encrypted)
    {
        if (version < 3)
        {
            return;
        }

        await CreateSnapshotAsync(version, encrypted);
        Mutate(m => m.Directories![0].RelativePath = "escape/emptydir");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Target);
        CreateDirectoryLink(Path.Combine(Target, "escape"), outside);

        var error = await RestoreAndCheckContainmentLinksAsync(outside);

        Directory.EnumerateFileSystemEntries(outside).Should().BeEmpty();
        error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("link");
    }

    [Fact]
    public async Task RestoreToATargetThatItselfIsALink_StillWorks()
    {
        await CreateSnapshotAsync(3, false);
        var real = Path.Combine(_root, "real-target");
        Directory.CreateDirectory(real);
        CreateDirectoryLink(Target, real);

        using var storage = await OpenStorageAsync();
        await NewRestore(storage).RestoreFromBackupAsync(Remote, Target);

        File.ReadAllText(Path.Combine(real, "a.txt")).Should().Be("alpha");
    }

    [Fact]
    public async Task RestoreReplacesAFileLink_WithoutWritingToItsTarget()
    {
        if (!CanCreateFileLinks())
        {
            return;
        }

        await CreateSnapshotAsync(3, false);
        var victim = Path.Combine(_root, "victim.txt");
        File.WriteAllText(victim, "precious");
        Directory.CreateDirectory(Target);
        File.CreateSymbolicLink(Path.Combine(Target, "a.txt"), victim);

        using var storage = await OpenStorageAsync();
        await NewRestore(storage)
            .RestoreFromBackupAsync(Remote, Target, new RestoreOptions { ConflictPolicy = RestoreConflictPolicy.Overwrite });

        File.ReadAllText(victim).Should().Be("precious", "the file behind a link must not be overwritten");
    }

    private async Task<Exception?> RestoreAndCheckContainmentLinksAsync(string outside)
    {
        using var storage = await OpenStorageAsync();
        try
        {
            await NewRestore(storage)
                .RestoreFromBackupAsync(Remote, Target, new RestoreOptions { ConflictPolicy = RestoreConflictPolicy.Overwrite });
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>A symbolic link on Linux; on Windows a junction (<c>mklink /J</c>), which needs no privilege.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "mklink /J must be able to create the junction");
    }

    /// <summary>Creating a file symbolic link on Windows needs a privilege CI runners usually lack; the Linux run covers the behaviour.</summary>
    private static bool CanCreateFileLinks() => !OperatingSystem.IsWindows();
}

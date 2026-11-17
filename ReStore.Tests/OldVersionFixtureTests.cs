using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// A repository + config.json + state written by a REAL old CLI (v1.6.0 / v1.7.0, produced by
/// Fixtures/generate-old-fixtures.sh), copied to a temp data root. Every test works on its own copy.
/// </summary>
internal sealed class OldFixture : IDisposable
{
    public const string EncryptedPassword = "fixture-pw-Old#1";

    private static readonly Dictionary<string, FileExpectation>[] NoStages = [];

    public sealed record FileExpectation(long Size, string Sha256);

    public string Version { get; }
    public bool Encrypted { get; }
    public string Root { get; }
    public string RepoDir => Path.Combine(Root, "repo");
    public string PristineConfigPath { get; }
    public string? Password => Encrypted ? EncryptedPassword : null;
    public IPasswordProvider? Passwords => Encrypted ? new StaticPasswordProvider(EncryptedPassword) : null;

    /// <summary>The three stages the generator backed up, oldest first (content of the source tree at each snapshot).</summary>
    public IReadOnlyList<Dictionary<string, FileExpectation>> Stages { get; }

    /// <summary>Manifest path (storage-relative, '/') per stage, oldest first.</summary>
    public IReadOnlyList<string> ManifestPaths { get; }

    public string HeadPath { get; }

    public OldFixture(string version, bool encrypted)
    {
        Version = version;
        Encrypted = encrypted;
        Root = Path.Combine(Path.GetTempPath(), "ReStoreOldFixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", version, encrypted ? "encrypted" : "plain");
        Directory.Exists(source).Should().BeTrue($"fixture {version}/{(encrypted ? "encrypted" : "plain")} must be copied to the output directory");
        CopyDirectory(Path.Combine(source, "repo"), RepoDir);
        CopyDirectory(Path.Combine(source, "state"), Path.Combine(Root, "state"));
        PristineConfigPath = Path.Combine(source, "config.json");

        Stages = [.. Enumerable.Range(1, 3).Select(i => JsonSerializer.Deserialize<Dictionary<string, FileExpectation>>(
            File.ReadAllText(Path.Combine(source, "expected", $"stage{i}.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!)];

        var groupDir = Directory.GetDirectories(Path.Combine(RepoDir, "snapshots")).Single();
        var groupName = Path.GetFileName(groupDir);
        ManifestPaths = [.. Directory.GetFiles(groupDir, "snapshot_*.manifest.json")
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).Select(f => $"snapshots/{groupName}/{f}")];
        ManifestPaths.Should().HaveCount(3);
        HeadPath = $"snapshots/{groupName}/HEAD";
    }

    /// <summary>The config.json as the old CLI wrote it, with only the repository placeholder replaced.</summary>
    public JsonObject ReadOldConfig()
    {
        var root = JsonNode.Parse(File.ReadAllText(PristineConfigPath))!.AsObject();
        root["storageSources"]!["local"]!["path"] = RepoDir;
        return root;
    }

    /// <summary>Installs the old config + state under <see cref="AppPaths"/> (the caller has overridden the root to <see cref="Root"/>).</summary>
    public void InstallIntoDataRoot(Action<JsonObject>? edit = null)
    {
        var config = ReadOldConfig();
        edit?.Invoke(config);
        File.WriteAllText(Path.Combine(Root, "config.json"), config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<LocalStorage> OpenStorageAsync()
    {
        var storage = new LocalStorage(new TestLogger());
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = RepoDir });
        return storage;
    }

    /// <summary>Content hash of every file of the repository: proves a command left the storage untouched.</summary>
    public string RepositoryFingerprint()
    {
        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(RepoDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sb.Append(Path.GetRelativePath(RepoDir, file).Replace('\\', '/')).Append('=')
              .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).Append('\n');
        }

        return sb.ToString();
    }

    public static void AssertTreeMatches(string directory, Dictionary<string, FileExpectation> expected)
    {
        var actual = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        actual.Should().Equal(expected.Keys.Order(StringComparer.Ordinal), "the restored tree must contain exactly the snapshot's files");
        foreach (var (rel, info) in expected)
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, rel.Replace('/', Path.DirectorySeparatorChar)));
            bytes.LongLength.Should().Be(info.Size, rel);
            Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(info.Sha256, rel);
        }
    }

    public static void CopyDirectory(string from, string to)
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

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

/// <summary>
/// Compatibility gate for data written by the real v1.6.0 and v1.7.0 CLIs: the current code must still load their
/// config and state, list, browse, restore and verify every old snapshot byte for byte, back up on top of them and
/// never delete anything still referenced. Fixtures are produced by Fixtures/generate-old-fixtures.sh (see Fixtures/README-old-fixtures.md).
/// </summary>
[Collection("GlobalState")]
public class OldVersionFixtureTests : IDisposable
{
    private static readonly string[] ManagedVariables = ["RESTORE_ENCRYPTION_PASSWORD", "RESTORE_ENCRYPTION_PASSWORD_FILE", "RESTORE_NON_INTERACTIVE", "CI"];

    private readonly string _previousRoot = AppPaths.Root;
    private readonly Dictionary<string, string?> _previousVariables = [];
    private readonly List<OldFixture> _fixtures = [];
    private readonly string _work = Path.Combine(Path.GetTempPath(), "ReStoreOldWork_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();

    public OldVersionFixtureTests()
    {
        Directory.CreateDirectory(_work);
        foreach (var name in ManagedVariables)
        {
            _previousVariables[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        AppPaths.OverrideRoot(_previousRoot);
        foreach (var (name, value) in _previousVariables)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        foreach (var fixture in _fixtures)
        {
            fixture.Dispose();
        }

        try { Directory.Delete(_work, true); } catch { }
        GC.SuppressFinalize(this);
    }

    public static IEnumerable<object[]> Fixtures() =>
        [["v160", false], ["v160", true], ["v170", false], ["v170", true]];

    private OldFixture Open(string version, bool encrypted)
    {
        var fixture = new OldFixture(version, encrypted);
        _fixtures.Add(fixture);
        return fixture;
    }

    /// <summary>Points AppPaths at the fixture's data root (config.json + state/) and loads it with the real ConfigManager.</summary>
    private async Task<(ConfigManager Config, SystemState State)> LoadAsync(OldFixture fixture, Action<JsonObject>? editConfig = null)
    {
        AppPaths.OverrideRoot(fixture.Root);
        fixture.InstallIntoDataRoot(editConfig);
        var config = new ConfigManager(_logger);
        await config.LoadAsync();
        var state = new SystemState(_logger);
        state.SetStateFilePath(AppPaths.SystemStateFile);
        await state.LoadStateAsync();
        return (config, state);
    }

    /// <summary>The generator machine\'s watch folders do not exist here; every other validation error would be a real compat problem.</summary>
    private static List<string> OnlyWatchDirectoryErrors(IEnumerable<string> errors) =>
        [.. errors.Where(e => !e.Contains("watch director", StringComparison.OrdinalIgnoreCase))];

    // ---- config ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("v160", 3)]
    [InlineData("v170", 4)]
    public async Task DefaultConfigWrittenByTheOldVersion_LoadsAndMigrates_KeepingEveryOldValue(string version, int oldSchema)
    {
        var fixture = Open(version, false);
        AppPaths.OverrideRoot(fixture.Root);
        var defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", version, "config.default.json");
        var oldText = File.ReadAllText(defaultConfigPath);
        var old = JsonNode.Parse(oldText)!.AsObject();
        old["configSchemaVersion"]!.GetValue<int>().Should().Be(oldSchema, "the fixture really is what that version wrote");
        File.WriteAllText(AppPaths.ConfigFile, oldText);

        var manager = new ConfigManager(_logger);
        await manager.LoadAsync();

        manager.LastMigrationResult.Should().NotBeNull();
        manager.LastMigrationResult!.SourceSchemaVersion.Should().Be(oldSchema);
        manager.LastMigrationResult.TargetSchemaVersion.Should().Be(ConfigSchemaManager.CURRENT_CONFIG_SCHEMA_VERSION);
        // (Not validated here: validating the default config would try to create its %USERPROFILE%\\ReStoreBackups folder.)

        // Values the old version wrote reach the model unchanged.
        manager.GlobalStorageType.Should().Be("local");
        manager.BackupInterval.Should().Be(TimeSpan.FromHours(1));
        manager.SizeThresholdMB.Should().Be(old["sizeThresholdMB"]!.GetValue<long>());
        manager.MaxFileSizeMB.Should().Be(old["maxFileSizeMB"]!.GetValue<int>());
        manager.BackupType.Should().Be(BackupType.ChunkSnapshot);
        manager.WatchDirectories.Should().HaveCount(old["watchDirectories"]!.AsArray().Count);
        manager.ExcludedPatterns.Should().Equal(old["excludedPatterns"]!.AsArray().Select(n => n!.GetValue<string>()));
        manager.ExcludedPaths.Should().Equal(old["excludedPaths"]!.AsArray().Select(n => n!.GetValue<string>()));
        manager.ChunkDiffing.TargetChunkSizeKB.Should().Be(128);
        manager.Encryption.Enabled.Should().BeFalse();

        // Migration only ADDS: every key the old file had is still in the persisted file with the same value.
        var migrated = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        migrated["configSchemaVersion"]!.GetValue<int>().Should().Be(ConfigSchemaManager.CURRENT_CONFIG_SCHEMA_VERSION);
        foreach (var (key, value) in old.Where(kv => kv.Key != "configSchemaVersion"))
        {
            migrated.ContainsKey(key).Should().BeTrue($"'{key}' written by {version} must survive the migration");
            IsSubset(value, migrated[key]).Should().BeTrue($"'{key}' written by {version} must keep its value (new members may be added)");
        }

        manager.LastMigrationResult.BackupPath.Should().NotBeNull();
        File.ReadAllText(manager.LastMigrationResult.BackupPath!).Should().Be(oldText, "the pre-migration copy is byte-identical");
    }

    /// <summary>Every value of <paramref name="old"/> is still in <paramref name="current"/> (objects may have gained members).</summary>
    private static bool IsSubset(JsonNode? old, JsonNode? current)
    {
        if (old is JsonObject oldObject)
        {
            return current is JsonObject currentObject
                && oldObject.All(kv => currentObject.ContainsKey(kv.Key) && IsSubset(kv.Value, currentObject[kv.Key]));
        }

        return JsonNode.DeepEquals(old, current);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task ConfigOfTheRepositoryFixture_LoadsWithItsValues_AndPreservesUnknownKeys(string version, bool encrypted)
    {
        var fixture = Open(version, encrypted);
        var (config, _) = await LoadAsync(fixture, root => root["x-user-added"] = new JsonObject { ["note"] = "keep me" });

        OnlyWatchDirectoryErrors(config.ValidateConfiguration().Errors).Should().BeEmpty();
        config.LastMigrationResult!.SourceSchemaVersion.Should().Be(version == "v160" ? 3 : 4);
        config.StorageSources["local"].Path.Should().Be(fixture.RepoDir);
        config.ChunkDiffing.MinChunkSizeKB.Should().Be(2);
        config.ChunkDiffing.TargetChunkSizeKB.Should().Be(4);
        config.ChunkDiffing.MaxChunkSizeKB.Should().Be(8);
        config.WatchDirectories.Should().ContainSingle();
        config.Encryption.Enabled.Should().Be(encrypted);

        var persisted = JsonNode.Parse(File.ReadAllText(AppPaths.ConfigFile))!.AsObject();
        persisted["x-user-added"]!["note"]!.GetValue<string>().Should().Be("keep me");

        if (encrypted)
        {
            config.Encryption.KeyDerivationIterations.Should().Be(10000);
            var salt = Convert.FromBase64String(config.Encryption.Salt!);
            var service = new EncryptionService(_logger);
            // The verification token the generator computed (python) is accepted by the current code, and a wrong password is not.
            service.VerifyPassword(OldFixture.EncryptedPassword, salt, config.Encryption.VerificationToken!, 10000).Should().BeTrue();
            service.VerifyPassword("not-the-password", salt, config.Encryption.VerificationToken!, 10000).Should().BeFalse();
        }
    }

    // ---- state -----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task OldState_Loads_WithEverySnapshotAndChunkCounts_AndSavesBackLoadable(string version, bool encrypted)
    {
        var fixture = Open(version, encrypted);
        var (_, state) = await LoadAsync(fixture);

        var history = state.BackupHistory.Should().ContainSingle().Subject.Value;
        history.Should().HaveCount(3);
        history.Select(h => h.ManifestPath ?? h.Path).Should().BeEquivalentTo(fixture.ManifestPaths);
        state.ChunkReferenceCounts.Should().NotBeEmpty();
        state.ChunkReferenceCounts.Values.Should().OnlyContain(c => c >= 1);

        // Round trip through the current writer keeps the history readable.
        await state.SaveStateAsync();
        var reloaded = new SystemState(_logger);
        reloaded.SetStateFilePath(AppPaths.SystemStateFile);
        await reloaded.LoadStateAsync();
        reloaded.BackupHistory.Single().Value.Should().HaveCount(3);
        reloaded.ChunkReferenceCounts.Should().BeEquivalentTo(state.ChunkReferenceCounts);
    }

    // ---- browse / restore / verify ----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Discovery_FindsTheOldGroup_AndItsThreeSnapshots(string version, bool encrypted)
    {
        var fixture = Open(version, encrypted);
        using var storage = await fixture.OpenStorageAsync();

        var result = await new RepositoryDiscovery(_logger).DiscoverAsync(storage, fixture.Passwords);

        var group = result.Groups.Should().ContainSingle().Subject;
        group.Snapshots.Select(s => s.ManifestPath).Should().BeEquivalentTo(fixture.ManifestPaths);
        group.Encrypted.Should().Be(encrypted);
        group.OriginalPath.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task EveryOldSnapshot_Browses_Verifies_AndRestoresByteForByte(string version, bool encrypted)
    {
        var fixture = Open(version, encrypted);
        using var storage = await fixture.OpenStorageAsync();

        for (var i = 0; i < 3; i++)
        {
            var manifestPath = fixture.ManifestPaths[i];
            var expected = fixture.Stages[i];

            var tree = await new SnapshotBrowser(_logger, storage, null, fixture.Passwords).LoadTreeAsync(manifestPath);
            tree.ManifestVersion.Should().Be(2, "both old versions wrote v2 manifests");
            tree.Encrypted.Should().Be(encrypted);
            SnapshotTree.EnumerateFilePaths(tree.Root).Order(StringComparer.Ordinal)
                .Should().Equal(expected.Keys.Order(StringComparer.Ordinal));

            var verification = await new SnapshotIntegrityVerifier(_logger, storage, fixture.Passwords).VerifyAsync(manifestPath);
            verification.IsValid.Should().BeTrue(string.Join("; ", verification.Errors));
            verification.ManifestHashValid.Should().BeTrue();
            verification.FileCount.Should().Be(expected.Count);

            var target = Path.Combine(_work, $"restore-{version}-{encrypted}-{i}");
            await new Restore(_logger, storage, fixture.Passwords).RestoreFromBackupAsync(manifestPath, target);
            OldFixture.AssertTreeMatches(target, expected);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task OldHead_PointsAtTheNewestSnapshot_AndRestoresIt(string version, bool encrypted)
    {
        var fixture = Open(version, encrypted);
        using var storage = await fixture.OpenStorageAsync();
        var target = Path.Combine(_work, "head-" + Guid.NewGuid().ToString("N"));

        await new Restore(_logger, storage, fixture.Passwords).RestoreFromBackupAsync(fixture.HeadPath, target);

        OldFixture.AssertTreeMatches(target, fixture.Stages[2]);
        (await new SnapshotIntegrityVerifier(_logger, storage, fixture.Passwords).VerifyAsync(fixture.HeadPath)).IsValid.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Diff_BetweenOldSnapshots_SeesTheRenameDeleteAddAndChange(string version, bool encrypted)
    {
        var fixture = Open(version, encrypted);
        using var storage = await fixture.OpenStorageAsync();

        var diff = await new SnapshotBrowser(_logger, storage, null, fixture.Passwords).DiffAsync(fixture.ManifestPaths[0], fixture.ManifestPaths[2]);

        var a = fixture.Stages[0];
        var b = fixture.Stages[2];
        diff.Added.Select(e => e.RelativePath).Order(StringComparer.Ordinal).Should().Equal(b.Keys.Except(a.Keys).Order(StringComparer.Ordinal));
        diff.Removed.Select(e => e.RelativePath).Order(StringComparer.Ordinal).Should().Equal(a.Keys.Except(b.Keys).Order(StringComparer.Ordinal));
        diff.Changed.Select(e => e.RelativePath).Order(StringComparer.Ordinal)
            .Should().Equal(a.Keys.Intersect(b.Keys).Where(k => a[k].Sha256 != b[k].Sha256).Order(StringComparer.Ordinal));
        diff.Removed.Select(e => e.RelativePath).Should().Contain(["delete-me.txt", "rename-me.txt"]);
        diff.Added.Select(e => e.RelativePath).Should().Contain("renamed.txt");
    }

    // ---- encrypted: wrong password -----------------------------------------------------------------------------

    [Theory]
    [InlineData("v160")]
    [InlineData("v170")]
    public async Task EncryptedOldRepository_RejectsAWrongPassword_AndWritesNoPlaintext(string version)
    {
        var fixture = Open(version, true);
        using var storage = await fixture.OpenStorageAsync();
        var wrong = new StaticPasswordProvider("definitely-the-wrong-password");

        var verification = await TryAsync(() => new SnapshotIntegrityVerifier(_logger, storage, wrong).VerifyAsync(fixture.ManifestPaths[2]));
        (verification == null || !verification.IsValid).Should().BeTrue("verify must not pass with the wrong password");

        var target = Path.Combine(_work, "wrong-" + Guid.NewGuid().ToString("N"));
        var restore = () => new Restore(_logger, storage, wrong).RestoreFromBackupAsync(fixture.ManifestPaths[2], target);
        await restore.Should().ThrowAsync<Exception>();
        if (Directory.Exists(target))
        {
            // Whatever a failed restore leaves behind must not be a correct plaintext file.
            foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(target, file).Replace('\\', '/');
                if (fixture.Stages[2].TryGetValue(rel, out var info) && info.Size > 0)
                {
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))).Should().NotBe(info.Sha256);
                }
            }
        }

        // And the right password still works afterwards.
        var good = Path.Combine(_work, "right-" + Guid.NewGuid().ToString("N"));
        await new Restore(_logger, storage, fixture.Passwords).RestoreFromBackupAsync(fixture.ManifestPaths[2], good);
        OldFixture.AssertTreeMatches(good, fixture.Stages[2]);
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T>> action) where T : class
    {
        try { return await action(); }
        catch (Exception) { return null; }
    }

    // ---- back up on top -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task BackupOnTopOfAnOldRepository_Dedups_AndLeavesTheOldSnapshotsRestorable(string version, bool encrypted)
    {
        var fixture = Open(version, encrypted);
        var (config, state) = await LoadAsync(fixture);
        using var storage = await fixture.OpenStorageAsync();

        // Re-create the newest old tree as a fresh source (a different folder: a new group in the same repository).
        var source = Path.Combine(_work, "src-" + Guid.NewGuid().ToString("N"));
        await new Restore(_logger, storage, fixture.Passwords).RestoreFromBackupAsync(fixture.ManifestPaths[2], source);
        var chunksBefore = CountChunks(fixture.RepoDir);
        var snapshotsBefore = state.BackupHistory.Values.Sum(v => v.Count);

        var backup = new Backup(_logger, state, new SizeAnalyzer(), config, fixture.Passwords, new RepositoryCoordinator(Path.Combine(_work, "a.lock")));
        await backup.BackupDirectoryAsync(source);

        // Same bytes, same repository. v1.7.0 and the current code chunk identically, so every old chunk is reused. v1.6.0 used the
        // older chunker (algorithm 1; the current code recognises such manifests by date), so its multi-chunk file is re-chunked
        // once into new chunks while single-chunk files still dedup and every old chunk stays in place.
        var chunksAfterFirst = CountChunks(fixture.RepoDir);
        if (version == "v170")
        {
            chunksAfterFirst.Should().Be(chunksBefore, "an identical tree must deduplicate against the chunks the old CLI wrote");
        }
        else
        {
            chunksAfterFirst.Should().BeGreaterThanOrEqualTo(chunksBefore);
            chunksAfterFirst.Should().BeLessThan(chunksBefore + 20, "only the one multi-chunk file is re-chunked");
        }

        state.BackupHistory.Values.Sum(v => v.Count).Should().Be(snapshotsBefore + 1);

        // Change one file and add one: only new content adds chunks (and the second run is incremental on the current chunker).
        await File.WriteAllTextAsync(Path.Combine(source, "added-by-current.txt"), string.Concat(Enumerable.Repeat("brand new content ", 500)));
        await File.WriteAllBytesAsync(Path.Combine(source, "empty.txt"), []);
        await backup.BackupDirectoryAsync(source);
        var chunksAfter = CountChunks(fixture.RepoDir);
        chunksAfter.Should().BeGreaterThan(chunksAfterFirst);
        chunksAfter.Should().BeLessThan(chunksAfterFirst + 10);

        // Every old snapshot is still restorable and verifiable; the new ones too.
        var newManifests = state.BackupHistory.Values.SelectMany(v => v).Select(h => h.ManifestPath ?? h.Path).Distinct().ToList();
        newManifests.Should().HaveCount(snapshotsBefore + 2);
        for (var i = 0; i < 3; i++)
        {
            var target = Path.Combine(_work, $"after-{i}-{Guid.NewGuid():N}");
            await new Restore(_logger, storage, fixture.Passwords).RestoreFromBackupAsync(fixture.ManifestPaths[i], target);
            OldFixture.AssertTreeMatches(target, fixture.Stages[i]);
        }

        foreach (var manifest in newManifests)
        {
            var verification = await new SnapshotIntegrityVerifier(_logger, storage, fixture.Passwords).VerifyAsync(manifest);
            verification.IsValid.Should().BeTrue($"{manifest}: {string.Join("; ", verification.Errors)}");
        }

        // Discovery now sees the old group and the new one.
        var discovered = await new RepositoryDiscovery(_logger).DiscoverAsync(storage, fixture.Passwords);
        discovered.Groups.Should().HaveCount(2);
    }

    private static int CountChunks(string repo) =>
        Directory.EnumerateFiles(Path.Combine(repo, "chunks"), "*", SearchOption.AllDirectories).Count();

    // ---- CLI against a copy of the old repository ---------------------------------------------------------------

    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunMainAsync(params string[] args)
    {
        await ConsoleLock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var outWriter = new StringWriter(stdout);
            using var errWriter = new StringWriter(stderr);
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            var exitCode = await Program.Main(args);
            outWriter.Flush();
            errWriter.Flush();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleLock.Release();
        }
    }

    private OldFixture OpenForCli(string version, bool encrypted, Action<JsonObject>? editConfig = null)
    {
        var fixture = Open(version, encrypted);
        AppPaths.OverrideRoot(fixture.Root);
        fixture.InstallIntoDataRoot(editConfig);
        Environment.SetEnvironmentVariable("RESTORE_NON_INTERACTIVE", "1");
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", fixture.Password);
        return fixture;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Cli_SnapshotsLsVerifyRestore_WorkOnTheOldRepository(string version, bool encrypted)
    {
        var fixture = OpenForCli(version, encrypted);

        var snapshots = await RunMainAsync("snapshots", "--json");
        snapshots.ExitCode.Should().Be(0, snapshots.Stderr);
        JsonNode.Parse(snapshots.Stdout)!["ok"]!.GetValue<bool>().Should().BeTrue();
        foreach (var manifest in fixture.ManifestPaths)
        {
            snapshots.Stdout.Should().Contain(manifest.Split('/').Last().Replace(".manifest.json", string.Empty));
        }

        var ls = await RunMainAsync("ls", fixture.ManifestPaths[0], "--json");
        ls.ExitCode.Should().Be(0, ls.Stderr);
        ls.Stdout.Should().Contain("big.bin").And.Contain("docs");

        var verify = await RunMainAsync("verify", fixture.HeadPath, "--json");
        verify.ExitCode.Should().Be(0, verify.Stdout + verify.Stderr);

        for (var i = 0; i < 3; i++)
        {
            var target = Path.Combine(_work, $"cli-restore-{i}-{Guid.NewGuid():N}");
            var dry = await RunMainAsync("restore", fixture.ManifestPaths[i], target, "--dry-run", "--json");
            dry.ExitCode.Should().Be(0, dry.Stderr);
            Directory.Exists(target).Should().BeFalse("--dry-run writes nothing");

            var restore = await RunMainAsync("restore", fixture.ManifestPaths[i], target, "--json");
            restore.ExitCode.Should().Be(0, restore.Stdout + restore.Stderr);
            OldFixture.AssertTreeMatches(target, fixture.Stages[i]);
        }

        var diff = await RunMainAsync("diff", fixture.ManifestPaths[0], fixture.HeadPath, "--json");
        diff.ExitCode.Should().Be(0, diff.Stderr);
        diff.Stdout.Should().Contain("renamed.txt").And.Contain("delete-me.txt");
    }

    [Theory]
    [InlineData("v160")]
    [InlineData("v170")]
    public async Task Cli_VerifyWithAWrongPassword_FailsWithANonZeroExit(string version)
    {
        var fixture = OpenForCli(version, true);
        Environment.SetEnvironmentVariable("RESTORE_ENCRYPTION_PASSWORD", "wrong-password-123");

        var verify = await RunMainAsync("verify", fixture.HeadPath, "--json");

        verify.ExitCode.Should().NotBe(0);
        var target = Path.Combine(_work, "wrongpw-" + Guid.NewGuid().ToString("N"));
        var restore = await RunMainAsync("restore", fixture.HeadPath, target, "--json");
        restore.ExitCode.Should().NotBe(0);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Cli_RetentionDryRun_AndRepairDryRun_DeleteNothing(string version, bool encrypted)
    {
        var fixture = OpenForCli(version, encrypted, root =>
        {
            root["retention"] = new JsonObject { ["enabled"] = true, ["keepLastPerDirectory"] = 1, ["maxAgeDays"] = 0 };
        });
        var before = fixture.RepositoryFingerprint();
        var stateBeforeLoaded = new SystemState(_logger);
        stateBeforeLoaded.SetStateFilePath(AppPaths.SystemStateFile);
        await stateBeforeLoaded.LoadStateAsync();

        var plan = await RunMainAsync("retention", "--dry-run", "--json");
        plan.ExitCode.Should().Be(0, plan.Stdout + plan.Stderr);
        JsonNode.Parse(plan.Stdout)!["ok"]!.GetValue<bool>().Should().BeTrue();

        var repair = await RunMainAsync("repair", "--dry-run", "--json");
        repair.ExitCode.Should().Be(0, repair.Stdout + repair.Stderr);
        JsonNode.Parse(repair.Stdout)!["ok"]!.GetValue<bool>().Should().BeTrue();

        fixture.RepositoryFingerprint().Should().Be(before, "dry runs never change the repository");

        // State-mutating commands load and re-save the state, which migrates old state files to the current layout (per-snapshot
        // chunk ids move to sidecar files), so the bytes may change; what the state RECORDS must not.
        var after = new SystemState(_logger);
        after.SetStateFilePath(AppPaths.SystemStateFile);
        await after.LoadStateAsync();
        after.BackupHistory.Keys.Should().BeEquivalentTo(stateBeforeLoaded.BackupHistory.Keys);
        after.BackupHistory.Values.SelectMany(v => v).Select(h => h.ManifestPath ?? h.Path)
            .Should().BeEquivalentTo(stateBeforeLoaded.BackupHistory.Values.SelectMany(v => v).Select(h => h.ManifestPath ?? h.Path));
        after.ChunkReferenceCounts.Should().BeEquivalentTo(stateBeforeLoaded.ChunkReferenceCounts);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Cli_RealRepair_OnAnOldRepository_DeletesNoReferencedChunk(string version, bool encrypted)
    {
        var fixture = OpenForCli(version, encrypted);
        var before = fixture.RepositoryFingerprint();

        var repair = await RunMainAsync("repair", "--yes", "--json");

        repair.ExitCode.Should().Be(0, repair.Stdout + repair.Stderr);
        // Nothing in an old, healthy repository is unreferenced, so not one stored byte may change
        // (new files such as pending-delete markers or a group index are allowed, removals and rewrites are not).
        var after = fixture.RepositoryFingerprint().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var line in before.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            after.Should().Contain(line, "repair must not delete or rewrite an existing object of an old repository");
        }

        using var storage = await fixture.OpenStorageAsync();
        for (var i = 0; i < 3; i++)
        {
            (await new SnapshotIntegrityVerifier(_logger, storage, fixture.Passwords).VerifyAsync(fixture.ManifestPaths[i])).IsValid.Should().BeTrue();
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Cli_RetentionApply_KeepingOne_RemovesOnlyOlderSnapshots_AndTheNewestStillRestores(string version, bool encrypted)
    {
        var fixture = OpenForCli(version, encrypted, root =>
        {
            root["retention"] = new JsonObject { ["enabled"] = true, ["keepLastPerDirectory"] = 1, ["maxAgeDays"] = 0 };
        });

        var apply = await RunMainAsync("retention", "apply", "--yes", "--json");

        apply.ExitCode.Should().Be(0, apply.Stdout + apply.Stderr);
        File.Exists(Path.Combine(fixture.RepoDir, fixture.ManifestPaths[2].Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue("the newest snapshot is kept");
        File.Exists(Path.Combine(fixture.RepoDir, fixture.ManifestPaths[0].Replace('/', Path.DirectorySeparatorChar))).Should().BeFalse("the oldest snapshot is outside the policy");

        using var storage = await fixture.OpenStorageAsync();
        var verification = await new SnapshotIntegrityVerifier(_logger, storage, fixture.Passwords).VerifyAsync(fixture.ManifestPaths[2]);
        verification.IsValid.Should().BeTrue(string.Join("; ", verification.Errors));
        var target = Path.Combine(_work, "after-retention-" + Guid.NewGuid().ToString("N"));
        await new Restore(_logger, storage, fixture.Passwords).RestoreFromBackupAsync(fixture.ManifestPaths[2], target);
        OldFixture.AssertTreeMatches(target, fixture.Stages[2]);
    }

    // ---- downgrade (best effort) ---------------------------------------------------------------------------------

    /// <summary>
    /// Needs a built old CLI: set RESTORE_COMPAT_OLD_CLI to the path of its ReStore.Core.dll (CI builds v1.7.0 from `git archive`;
    /// see Fixtures/README-old-fixtures.md). Skipped otherwise.
    /// </summary>
    private sealed class OldCliFactAttribute : FactAttribute
    {
        public OldCliFactAttribute()
        {
            var path = Environment.GetEnvironmentVariable("RESTORE_COMPAT_OLD_CLI");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Skip = "Set RESTORE_COMPAT_OLD_CLI to the ReStore.Core.dll of a built v1.7.0 CLI to run the downgrade check.";
            }
        }
    }

    [OldCliFact]
    [Trait("Category", "Compat")]
    public async Task Downgrade_TheV170Cli_StillRestoresAndVerifiesSnapshotsTheCurrentCodeAddedToAnOldRepository()
    {
        var oldCli = Environment.GetEnvironmentVariable("RESTORE_COMPAT_OLD_CLI")!;
        foreach (var encrypted in new[] { false, true })
        {
            var fixture = Open("v170", encrypted);
            var (config, state) = await LoadAsync(fixture);
            using var storage = await fixture.OpenStorageAsync();

            var source = Path.Combine(_work, "dg-src-" + Guid.NewGuid().ToString("N"));
            await new Restore(_logger, storage, fixture.Passwords).RestoreFromBackupAsync(fixture.ManifestPaths[2], source);
            await File.WriteAllTextAsync(Path.Combine(source, "written-by-current.txt"), "current version wrote this\n" + new string('z', 3000));
            await new Backup(_logger, state, new SizeAnalyzer(), config, fixture.Passwords, new RepositoryCoordinator(Path.Combine(_work, "dg.lock")))
                .BackupDirectoryAsync(source);
            var newest = state.GetPreviousBackupPath(source)!;

            // The old CLI gets its own HOME (it has no RESTORE_HOME) with the old config pointing at the repository.
            var home = Path.Combine(_work, "dg-home-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(home, "ReStore"));
            var oldConfig = fixture.ReadOldConfig();
            oldConfig["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = source, ["storageType"] = null }); // the old CLI insists on an existing watch folder
            File.WriteAllText(Path.Combine(home, "ReStore", "config.json"), oldConfig.ToJsonString());

            var verify = await RunOldCliAsync(oldCli, home, fixture.Password, "verify", newest);
            verify.ExitCode.Should().Be(0, verify.Output);
            verify.Output.Should().Contain("verification passed");

            var target = Path.Combine(_work, "dg-restore-" + Guid.NewGuid().ToString("N"));
            var restore = await RunOldCliAsync(oldCli, home, fixture.Password, "restore", newest, target, "--conflict", "overwrite");
            restore.ExitCode.Should().Be(0, restore.Output);
            Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count().Should().Be(Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count());
            File.ReadAllText(Path.Combine(target, "written-by-current.txt")).Should().StartWith("current version wrote this");
            foreach (var (rel, info) in fixture.Stages[2])
            {
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar))))).Should().Be(info.Sha256, rel);
            }

            // The old snapshots written by the old CLI are of course still fine for the old CLI.
            var oldVerify = await RunOldCliAsync(oldCli, home, fixture.Password, "verify", fixture.ManifestPaths[0]);
            oldVerify.ExitCode.Should().Be(0, oldVerify.Output);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunOldCliAsync(string dll, string home, string? password, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["HOME"] = home;
        psi.Environment["USERPROFILE"] = home;
        psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        psi.Environment.Remove("RESTORE_HOME");
        if (password != null)
        {
            psi.Environment["RESTORE_ENCRYPTION_PASSWORD"] = password;
        }

        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        return (process.ExitCode, await output + await error);
    }
}

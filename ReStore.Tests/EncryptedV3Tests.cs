using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Encrypted v3 repositories (P4-3): key file with slots, encrypted manifests, HMAC chunk ids, password change.</summary>
public class EncryptedV3Tests : IDisposable
{
    private const string Password = "hunter2hunter2";
    private const string SecretName = "tax-return-2025-secret.txt";
    private const string SecretFolder = "confidential-folder";

    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "ev3_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly string _storageDir;
    private readonly string _source;
    private readonly SystemState _state;
    private readonly Mock<IConfigManager> _config = new();
    private readonly ChunkDiffingConfig _chunk = new() { ManifestVersion = 3 };
    private readonly EncryptionConfig _encryption;
    private readonly RepositoryCoordinator _coordinator;
    private string _password = Password;

    public EncryptedV3Tests()
    {
        _storageDir = Path.Combine(_root, "storage");
        _source = Path.GetFullPath(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_storageDir);
        Directory.CreateDirectory(Path.Combine(_source, SecretFolder));
        _encryption = new EncryptionConfig
        {
            Enabled = true,
            Salt = Convert.ToBase64String(EncryptionService.GenerateSalt()),
            KeyDerivationIterations = 1000
        };

        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "state.json"));

        _config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        _config.Setup(c => c.GlobalStorageType).Returns("local");
        _config.Setup(c => c.SizeThresholdMB).Returns(100);
        _config.Setup(c => c.Encryption).Returns(_encryption);
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
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<LocalStorage> OpenStorageAsync()
    {
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { { "path", _storageDir } });
        return storage;
    }

    private static IPasswordProvider Provider(string password)
    {
        var mock = new Mock<IPurposePasswordProvider>();
        mock.Setup(p => p.GetPasswordAsync()).ReturnsAsync(password);
        mock.Setup(p => p.GetPasswordAsync(It.IsAny<PasswordPurpose>())).ReturnsAsync(password);
        return mock.Object;
    }

    private Task BackupAsync(string? password = null) =>
        new Backup(_logger, _state, new SizeAnalyzer(), _config.Object, Provider(password ?? _password), _coordinator).BackupDirectoryAsync(_source);

    private static string RemoteOf(string storageRoot, string localPath) =>
        Path.GetRelativePath(storageRoot, localPath).Replace(Path.DirectorySeparatorChar, '/');

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void WriteSample(string suffix = "")
    {
        Write(Path.Combine(SecretFolder, SecretName), "my very private tax data " + suffix + new string('z', 3000));
        Write("notes.txt", "some other notes " + suffix + new string('y', 2000));
    }

    private List<string> ManifestPaths() =>
        [.. Directory.GetFiles(_storageDir, "*.manifest.json", SearchOption.AllDirectories).OrderBy(p => File.GetCreationTimeUtc(p)).ThenBy(p => p)];

    private string LatestManifestPath() => ManifestPaths().OrderBy(p => ManifestIo.Read(p, _password).CreatedUtc).Last();

    private SnapshotManifest LatestManifest() => ManifestIo.Read(LatestManifestPath(), _password);

    private string KeyFilePath => Path.Combine(_storageDir, "keys", "repository.keys.json");

    private async Task RestoreAsync(string remote, string target, string? password = null)
    {
        using var storage = await OpenStorageAsync();
        await new Restore(_logger, storage, Provider(password ?? _password), _state, _coordinator).RestoreFromBackupAsync(remote, target);
    }

    private void AssertRestored(string target)
    {
        File.ReadAllText(Path.Combine(target, SecretFolder, SecretName)).Should().StartWith("my very private tax data");
        File.ReadAllText(Path.Combine(target, "notes.txt")).Should().StartWith("some other notes");
    }

    private async Task<SnapshotVerificationResult> VerifyAsync(string? password = null)
    {
        using var storage = await OpenStorageAsync();
        return await new SnapshotIntegrityVerifier(_logger, storage, Provider(password ?? _password), _state, coordinator: _coordinator)
            .VerifyAsync(RemoteOf(_storageDir, LatestManifestPath()));
    }

    private Dictionary<string, string> StorageFingerprint(bool includeKeys) =>
        Directory.GetFiles(_storageDir, "*", SearchOption.AllDirectories)
            .Where(p => includeKeys || !p.Contains(Path.DirectorySeparatorChar + "keys" + Path.DirectorySeparatorChar))
            .ToDictionary(p => RemoteOf(_storageDir, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    // A GUI provider shows a modal dialog on its first call and caches the answer. That first call must not happen
    // while a repository lease is held, or the dialog would block every other operation.
    private sealed class LeaseProbingProvider(RepositoryCoordinator coordinator, string password) : IPurposePasswordProvider
    {
        private readonly HashSet<PasswordPurpose> _asked = [];

        public List<string> LeasesHeldAtFirstPrompt { get; } = [];

        public Task<string?> GetPasswordAsync() => GetPasswordAsync(PasswordPurpose.Encrypt);

        public async Task<string?> GetPasswordAsync(PasswordPurpose purpose)
        {
            if (_asked.Add(purpose))
            {
                if (coordinator.IsBusy)
                {
                    LeasesHeldAtFirstPrompt.Add("mutation");
                }

                // A held read lease blocks the exclusive delete lease.
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                try
                {
                    await using var delete = await coordinator.AcquireDeleteAsync("probe", cts.Token);
                }
                catch (OperationCanceledException)
                {
                    LeasesHeldAtFirstPrompt.Add("read");
                }
            }

            return password;
        }

        public bool IsPasswordSet() => _asked.Count > 0;

        public void ClearPassword() { }
    }

    [Fact]
    public async Task PasswordPrompt_HappensBeforeAnyLeaseIsHeld_ForBackupRestoreAndVerify()
    {
        WriteSample();
        var backupProvider = new LeaseProbingProvider(_coordinator, _password);
        await new Backup(_logger, _state, new SizeAnalyzer(), _config.Object, backupProvider, _coordinator).BackupDirectoryAsync(_source);
        backupProvider.LeasesHeldAtFirstPrompt.Should().BeEmpty();

        var remote = RemoteOf(_storageDir, LatestManifestPath());
        using var storage = await OpenStorageAsync();

        var restoreProvider = new LeaseProbingProvider(_coordinator, _password);
        await new Restore(_logger, storage, restoreProvider, _state, _coordinator).RestoreFromBackupAsync(remote, Path.Combine(_root, "restored-probe"));
        restoreProvider.LeasesHeldAtFirstPrompt.Should().BeEmpty();

        var verifyProvider = new LeaseProbingProvider(_coordinator, _password);
        (await new SnapshotIntegrityVerifier(_logger, storage, verifyProvider, _state, coordinator: _coordinator).VerifyAsync(remote))
            .IsValid.Should().BeTrue();
        verifyProvider.LeasesHeldAtFirstPrompt.Should().BeEmpty();
    }

    // ---- round trip & format -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EncryptedV3_RoundTrips_UsesKeyedIdsInItsOwnNamespace_AndVerifies()
    {
        WriteSample();
        await BackupAsync();
        Write("notes.txt", "changed notes " + new string('q', 500));
        await BackupAsync();

        File.Exists(KeyFilePath).Should().BeTrue();
        var manifest = LatestManifest();
        manifest.Version.Should().Be(3);
        manifest.EncryptionEnabled.Should().BeTrue();
        manifest.EncryptionSalt.Should().BeNull();
        manifest.ChunkIdScheme.Should().Be("hmac-sha256");
        manifest.ChunkStorageNamespace.Should().StartWith("e3_");
        EncryptedManifestEnvelope.IsEncrypted(File.ReadAllBytes(LatestManifestPath())).Should().BeTrue();

        var secret = manifest.Files.Single(f => f.RelativePath.EndsWith(SecretName));
        var plain = File.ReadAllBytes(Path.Combine(_source, SecretFolder, SecretName));
        secret.ContentHash.Should().HaveLength(64).And.NotBe(Convert.ToHexStringLower(SHA256.HashData(plain)), "ids are keyed, not plain SHA-256");
        secret.Chunks.Should().OnlyContain(c => c.ChunkId.Length == 64 && c.ChunkId == c.ContentHash);
        foreach (var chunk in manifest.Files.SelectMany(f => f.Chunks))
        {
            File.Exists(Path.Combine(_storageDir, SnapshotStoragePaths.GetChunkPath(chunk.ChunkId, manifest.ChunkStorageNamespace).Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue();
        }

        var target = Path.Combine(_root, "restored");
        await RestoreAsync(RemoteOf(_storageDir, LatestManifestPath()), target);
        File.ReadAllText(Path.Combine(target, "notes.txt")).Should().StartWith("changed notes");
        File.ReadAllText(Path.Combine(target, SecretFolder, SecretName)).Should().StartWith("my very private tax data");
        var first = ManifestPaths().OrderBy(p => ManifestIo.Read(p).CreatedUtc).First();
        var firstTarget = Path.Combine(_root, "restored-first");
        await RestoreAsync(RemoteOf(_storageDir, first), firstTarget);
        AssertRestored(firstTarget);

        (await VerifyAsync()).IsValid.Should().BeTrue();

        // HEAD restore works too.
        var headTarget = Path.Combine(_root, "restored-head");
        var head = Directory.GetFiles(_storageDir, "HEAD", SearchOption.AllDirectories).Single();
        await RestoreAsync(RemoteOf(_storageDir, head), headTarget);
        File.ReadAllText(Path.Combine(headTarget, "notes.txt")).Should().StartWith("changed notes");
    }

    [Fact]
    public async Task EncryptedV3_LeaksNoPathNameOrPlainHash_InAnyStoredObject()
    {
        WriteSample();
        await BackupAsync();
        await BackupAsync(); // second run: unchanged, idle path

        var manifest = LatestManifest();
        var plainHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_source, SecretFolder, SecretName))));
        // The group folder key in object names (snapshots/<leaf>_<hash>/) is a known, documented residual: only file paths are hidden.
        string[] forbidden = [SecretName, SecretFolder, "notes.txt", "tax-return", plainHash, _source, "my very private"];
        var forbiddenBytes = forbidden.Select(Encoding.UTF8.GetBytes).ToList();
        var forbiddenUtf16 = forbidden.Select(Encoding.Unicode.GetBytes).ToList();

        var scanned = 0;
        foreach (var file in Directory.GetFiles(_storageDir, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            // Object names may carry the group folder key (snapshots/<name>_<hash>), which is the folder name only.
            foreach (var needle in forbiddenBytes.Concat(forbiddenUtf16))
            {
                IndexOf(bytes, needle).Should().Be(-1, $"'{Encoding.UTF8.GetString(needle)}' must not appear in {Path.GetFileName(file)}");
            }

            scanned++;
        }

        scanned.Should().BeGreaterThan(4);
        // The manifest is not even valid gzip/JSON without the key.
        var manifestBytes = File.ReadAllBytes(LatestManifestPath());
        var act = () => SnapshotManifestSerializer.Deserialize(manifestBytes);
        act.Should().Throw<ManifestKeyRequiredException>();
        manifest.Files.Should().NotBeEmpty();
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    [Fact]
    public async Task GroupJson_OfAnEncryptedV3Group_IsEncryptedWithTheKeyFile_AndReadableWithThePassword()
    {
        WriteSample();
        await BackupAsync();

        var index = Directory.GetFiles(_storageDir, "group.json", SearchOption.AllDirectories).Single();
        var text = File.ReadAllText(index);
        text.Should().NotContain(_source).And.Contain("keyFileId".Replace("keyFileId", "KeyFileId"));
        var keyFileId = RepositoryKeyFiles.Parse(File.ReadAllBytes(KeyFilePath)).KeyFileId;
        text.Should().Contain(keyFileId);

        using var storage = await OpenStorageAsync();
        var found = await new RepositoryDiscovery(_logger).DiscoverAsync(storage, Provider(Password));
        var group = found.Groups.Single();
        group.HasIndex.Should().BeTrue();
        group.OriginalPath.Should().Be(_source);
        group.Encrypted.Should().BeTrue();
        group.Snapshots.Should().ContainSingle().Which.ManifestVersion.Should().Be(3);

        var locked = await new RepositoryDiscovery(_logger).DiscoverAsync(storage, null);
        locked.Groups.Should().BeEmpty("without the password neither the index nor the manifests can be read");
        locked.Warnings.Concat(locked.Groups.SelectMany(g => g.Warnings)).Should().NotBeNull();
    }

    // ---- tamper / key check --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TamperedManifest_BodyOrHeader_IsRejected()
    {
        WriteSample();
        await BackupAsync();
        var path = LatestManifestPath();
        var original = File.ReadAllBytes(path);
        using var keys = ManifestIo.UnlockNear(path);

        // Body byte flipped.
        var body = (byte[])original.Clone();
        body[^5] ^= 0x01;
        var bodyAct = () => SnapshotManifestSerializer.Deserialize(body, keys);
        bodyAct.Should().Throw<InvalidOperationException>().WithMessage("*authentication*");

        // Header snapshot id changed (same length so the framing stays intact): the header is AAD.
        var header = EncryptedManifestEnvelope.ReadHeader(original);
        var tampered = (byte[])original.Clone();
        var text = Encoding.UTF8.GetString(tampered);
        var at = text.IndexOf(header.SnapshotId, StringComparison.Ordinal);
        at.Should().BeGreaterThan(0);
        tampered[at + 10] = (byte)(tampered[at + 10] == (byte)'0' ? '1' : '0');
        var headerAct = () => SnapshotManifestSerializer.Deserialize(tampered, keys);
        headerAct.Should().Throw<InvalidOperationException>();

        // Key check altered: reported as a different repository key before any decryption.
        var wrongCheck = (byte[])original.Clone();
        var checkAt = Encoding.UTF8.GetString(wrongCheck).IndexOf(header.KeyCheck, StringComparison.Ordinal);
        wrongCheck[checkAt] = (byte)(wrongCheck[checkAt] == (byte)'a' ? 'b' : 'a');
        var checkAct = () => SnapshotManifestSerializer.Deserialize(wrongCheck, keys);
        checkAct.Should().Throw<InvalidOperationException>().WithMessage("*different repository key*");

        // Truncated.
        var truncated = original[..(original.Length / 2)];
        var truncAct = () => SnapshotManifestSerializer.Deserialize(truncated, keys);
        truncAct.Should().Throw<Exception>();

        // The untouched file still opens, and a real restore of a tampered object fails.
        SnapshotManifestSerializer.Deserialize(original, keys).Files.Should().NotBeEmpty();
        File.WriteAllBytes(path, body);
        var restore = () => RestoreAsync(RemoteOf(_storageDir, path), Path.Combine(_root, "never"));
        await restore.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task WrongPassword_IsDetectedByTheKeyFile_BeforeAnyChunkIsTouched()
    {
        WriteSample();
        await BackupAsync();
        var remote = RemoteOf(_storageDir, LatestManifestPath());

        // Chunks are gone: only the key check can explain the failure, so it must come first.
        Directory.Delete(Path.Combine(_storageDir, "chunks"), recursive: true);

        var act = () => RestoreAsync(remote, Path.Combine(_root, "wrong"), "not-the-password");
        await act.Should().ThrowAsync<WrongRepositoryPasswordException>();

        using var storage = await OpenStorageAsync();
        var verify = await new SnapshotIntegrityVerifier(_logger, storage, Provider("not-the-password"), _state, coordinator: _coordinator).VerifyAsync(remote);
        verify.Errors.Should().ContainSingle().Which.Should().Contain("Wrong password");
        verify.ChunksDownloaded.Should().Be(0);

        var noPassword = () => RestoreAsync(remote, Path.Combine(_root, "none"), "");
        await noPassword.Should().ThrowAsync<RepositoryPasswordRequiredException>();
    }

    [Fact]
    public async Task ManifestFromAnotherRepository_IsRejectedAsADifferentKey()
    {
        WriteSample();
        await BackupAsync();
        var path = LatestManifestPath();
        var (otherFile, otherKeys) = RepositoryKeyFiles.Create(Password, 1000);
        using var _ = otherKeys;

        var act = () => SnapshotManifestSerializer.Deserialize(File.ReadAllBytes(path), otherKeys);
        act.Should().Throw<InvalidOperationException>().WithMessage("*different repository key*");
        otherFile.KeyFileId.Should().NotBe(RepositoryKeyFiles.Parse(File.ReadAllBytes(KeyFilePath)).KeyFileId);
    }

    [Fact]
    public async Task BackupWithAWrongPassword_NeverReplacesTheExistingKeyFile()
    {
        WriteSample();
        await BackupAsync();
        var keyFileBefore = File.ReadAllBytes(KeyFilePath);
        Write("notes.txt", "more notes " + new string('m', 400));

        var act = () => BackupAsync("not-the-password");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*repository key file*");

        File.ReadAllBytes(KeyFilePath).Should().Equal(keyFileBefore);
        ManifestPaths().Should().ContainSingle();
    }

    // ---- password change -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PasswordChange_ReWrapsTheKey_WithoutTouchingAnyChunkOrManifest()
    {
        WriteSample();
        await BackupAsync();
        var remote = RemoteOf(_storageDir, LatestManifestPath());
        var before = StorageFingerprint(includeKeys: false);
        var namespaceBefore = LatestManifest().ChunkStorageNamespace;

        using (var storage = await OpenStorageAsync())
        {
            (await RepositoryKeyStore.ChangePasswordAsync(storage, Password, "a-brand-new-password", 1000)).Should().BeTrue();
        }

        StorageFingerprint(includeKeys: false).Should().Equal(before, "chunks, manifests, HEAD and group.json are not rewritten");

        var oldPassword = () => RestoreAsync(remote, Path.Combine(_root, "old"), Password);
        await oldPassword.Should().ThrowAsync<WrongRepositoryPasswordException>();

        var target = Path.Combine(_root, "new");
        await RestoreAsync(remote, target, "a-brand-new-password");
        AssertRestored(target);

        // The next backup (with the new password configured) keeps the namespace and uploads only what changed.
        _password = "a-brand-new-password";
        _encryption.VerificationToken = null;
        var chunksBefore = Directory.GetFiles(_storageDir, "*.chunk", SearchOption.AllDirectories).Length;
        Write("notes.txt", "changed after password change " + new string('c', 300));
        await BackupAsync();
        LatestManifest().ChunkStorageNamespace.Should().Be(namespaceBefore);
        Directory.GetFiles(_storageDir, "*.chunk", SearchOption.AllDirectories).Length.Should().Be(chunksBefore + 1);
        (await VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task PasswordChange_WithTheWrongCurrentPassword_Fails_AndLeavesTheKeyFileAlone()
    {
        WriteSample();
        await BackupAsync();
        var before = File.ReadAllBytes(KeyFilePath);

        using var storage = await OpenStorageAsync();
        var act = () => RepositoryKeyStore.ChangePasswordAsync(storage, "not-the-password", "a-brand-new-password", 1000);
        await act.Should().ThrowAsync<WrongRepositoryPasswordException>();
        File.ReadAllBytes(KeyFilePath).Should().Equal(before);
    }

    [Fact]
    public async Task PasswordChange_OnARepositoryWithoutAKeyFile_ReportsFalse()
    {
        using var storage = await OpenStorageAsync();
        (await RepositoryKeyStore.ChangePasswordAsync(storage, Password, "a-brand-new-password", 1000)).Should().BeFalse();
    }

    [Fact]
    public async Task RecoveryKey_UnlocksTheRepository_SurvivesAPasswordChange_AndCanResetThePassword()
    {
        WriteSample();
        await BackupAsync();
        var remote = RemoteOf(_storageDir, LatestManifestPath());

        string recovery;
        using (var storage = await OpenStorageAsync())
        {
            recovery = await RepositoryKeyStore.AddRecoveryKeyAsync(storage, Password);
            recovery.Should().StartWith("RSRK-");
            await RepositoryKeyStore.ChangePasswordAsync(storage, Password, "a-brand-new-password", 1000);
        }

        File.ReadAllText(KeyFilePath).Should().NotContain(recovery);
        var target = Path.Combine(_root, "recovered");
        await RestoreAsync(remote, target, recovery);
        AssertRestored(target);

        // Forgotten password: the recovery key sets a new one (old password slot is replaced).
        using (var storage = await OpenStorageAsync())
        {
            await RepositoryKeyStore.ChangePasswordAsync(storage, recovery, "third-password-here", 1000);
        }

        await RestoreAsync(remote, Path.Combine(_root, "third"), "third-password-here");
        var oldOne = () => RestoreAsync(remote, Path.Combine(_root, "fourth"), "a-brand-new-password");
        await oldOne.Should().ThrowAsync<WrongRepositoryPasswordException>();
    }

    [Fact]
    public void KeyFile_HoldsNoSecret_AndHasTheDocumentedShape()
    {
        var (file, keys) = RepositoryKeyFiles.Create(Password, 1000);
        using var _ = keys;
        var json = Encoding.UTF8.GetString(RepositoryKeyFiles.Serialize(file));
        json.Should().NotContain(Password).And.Contain("slots").And.Contain("wrappedKey").And.Contain("keyCheck").And.Contain("iterations");
        file.Slots.Should().ContainSingle();

        // Slot parameters are authenticated: changing the iteration count breaks the unwrap.
        var tampered = RepositoryKeyFiles.Parse(RepositoryKeyFiles.Serialize(file));
        file.Slots[0].Iterations.Should().Be(RepositoryKeyFiles.MinIterations, "a low requested count is raised to the floor");
        tampered.Slots[0].Iterations = RepositoryKeyFiles.MinIterations + 1;
        var act = () => RepositoryKeyFiles.Unlock(tampered, Password);
        act.Should().Throw<WrongRepositoryPasswordException>();

        // A slot below the floor (hand-edited or foreign file) is refused with a clear error, not tried.
        tampered.Slots[0].Iterations = 1000;
        var low = () => RepositoryKeyFiles.Unlock(tampered, Password);
        low.Should().Throw<InvalidOperationException>().WithMessage("*minimum*");

        // Keys are domain separated.
        keys.ChunkKey.Should().NotEqual(keys.ManifestKey);
        keys.ChunkNamespace.Should().StartWith("e3_").And.HaveLength(27);
        RepositoryKeyFiles.Unlock(file, Password).KeyCheck.Should().Be(keys.KeyCheck);
    }

    [Fact]
    public void HmacChunkIds_AreKeyedAndSameLengthAsSha256()
    {
        var (_, keysA) = RepositoryKeyFiles.Create(Password, 1000);
        var (_, keysB) = RepositoryKeyFiles.Create(Password, 1000);
        using var a = keysA;
        using var b = keysB;
        var data = Encoding.UTF8.GetBytes("guessable content");

        var idA = a.HashScheme.ChunkId(data);
        idA.Should().HaveLength(64).And.NotBe(ChunkHashScheme.Sha256.ChunkId(data));
        idA.Should().NotBe(b.HashScheme.ChunkId(data), "a different repository key gives different ids");
        a.HashScheme.ChunkId(data).Should().Be(idA, "deterministic within one repository, so dedup works");
    }

    // ---- consumers -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SnapshotBrowser_BrowsesVersionsAndDiffs_OfAnEncryptedV3Repository()
    {
        WriteSample("one");
        await BackupAsync();
        var first = RemoteOf(_storageDir, LatestManifestPath());
        Write("notes.txt", "second version of notes " + new string('n', 600));
        await BackupAsync();
        var second = RemoteOf(_storageDir, LatestManifestPath());

        using var storage = await OpenStorageAsync();
        var browser = new SnapshotBrowser(_logger, storage, _coordinator, Provider(Password));
        var tree = await browser.LoadTreeAsync(second);
        tree.ManifestVersion.Should().Be(3);
        tree.Root.Children.Should().NotBeEmpty();

        var history = await browser.GetFileVersionsAsync(_source, "notes.txt");
        history.Versions.Should().HaveCount(2);

        var diff = await browser.DiffAsync(first, second);
        diff.Changed.Should().ContainSingle(e => e.RelativePath == "notes.txt");

        var anonymous = new SnapshotBrowser(_logger, storage, _coordinator);
        var act = () => anonymous.LoadTreeAsync(second);
        await act.Should().ThrowAsync<RepositoryPasswordRequiredException>();
    }

    [Fact]
    public async Task Repair_OfAnEncryptedV3Repository_NeedsThePassword_AndKeepsReferencedChunks()
    {
        WriteSample();
        await BackupAsync();
        var manifest = LatestManifest();
        var referenced = Directory.GetFiles(_storageDir, "*.chunk", SearchOption.AllDirectories).Length;

        var orphan = Path.Combine(_storageDir, "chunks", manifest.ChunkStorageNamespace!, "ab", "abcdef0123.chunk");
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllBytes(orphan, [1, 2, 3]);
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-3));

        using var storage = await OpenStorageAsync();

        // No password: the manifests cannot be decrypted, so the run aborts and deletes nothing.
        var blocked = () => new RepositoryRepair(_logger, _config.Object, _state, _coordinator).RunAsync(storage, "local");
        await blocked.Should().ThrowAsync<RepositoryRepairAbortedException>();
        File.Exists(orphan).Should().BeTrue();

        var wrong = () => new RepositoryRepair(_logger, _config.Object, _state, _coordinator, Provider("not-the-password")).RunAsync(storage, "local");
        await wrong.Should().ThrowAsync<RepositoryRepairAbortedException>();
        File.Exists(orphan).Should().BeTrue();

        var report = await new RepositoryRepair(_logger, _config.Object, _state, _coordinator, Provider(Password)).RunAsync(storage, "local");
        report.ChunksDeleted.Should().Be(1);
        File.Exists(orphan).Should().BeFalse();
        Directory.GetFiles(_storageDir, "*.chunk", SearchOption.AllDirectories).Length.Should().Be(referenced);
        (await VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task MixedV2AndV3_InOneGroup_RestoreAndRepairCorrectly()
    {
        // v2 encrypted snapshot (legacy namespace), then the user opts in to v3.
        _chunk.ManifestVersion = 2;
        WriteSample("legacy");
        await BackupAsync();
        var v2Manifest = LatestManifestPath();
        var v2 = ManifestIo.Read(v2Manifest);
        v2.Version.Should().Be(2);
        v2.ChunkStorageNamespace.Should().StartWith("enc_");
        File.Exists(KeyFilePath).Should().BeFalse("v2 repositories have no key file");

        _chunk.ManifestVersion = 3;
        Write("notes.txt", "now on v3 " + new string('v', 700));
        await BackupAsync();
        var v3 = LatestManifest();
        v3.Version.Should().Be(3);
        v3.ChunkStorageNamespace.Should().StartWith("e3_").And.NotBe(v2.ChunkStorageNamespace);
        v3.ChunkIdScheme.Should().Be("hmac-sha256");
        File.Exists(KeyFilePath).Should().BeTrue();

        // Both restore with the same password.
        var t2 = Path.Combine(_root, "r-v2");
        await RestoreAsync(RemoteOf(_storageDir, v2Manifest), t2);
        File.ReadAllText(Path.Combine(t2, "notes.txt")).Should().StartWith("some other notes legacy");
        var t3 = Path.Combine(_root, "r-v3");
        await RestoreAsync(RemoteOf(_storageDir, LatestManifestPath()), t3);
        File.ReadAllText(Path.Combine(t3, "notes.txt")).Should().StartWith("now on v3");

        // Repair marks both namespaces and removes an orphan in each.
        var orphans = new[] { v2.ChunkStorageNamespace!, v3.ChunkStorageNamespace! }
            .Select(ns => Path.Combine(_storageDir, "chunks", ns, "ab", "abcdef0123.chunk")).ToList();
        foreach (var orphan in orphans)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
            File.WriteAllBytes(orphan, [9]);
            File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-3));
        }

        using var storage = await OpenStorageAsync();
        var report = await new RepositoryRepair(_logger, _config.Object, _state, _coordinator, Provider(Password)).RunAsync(storage, "local");
        report.ChunksDeleted.Should().Be(2);
        orphans.Should().OnlyContain(o => !File.Exists(o));

        await RestoreAsync(RemoteOf(_storageDir, v2Manifest), Path.Combine(_root, "r-v2b"));
        await RestoreAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "r-v3b"));
    }

    [Fact]
    public async Task TouchedButUnchangedFile_IsNotBackedUpAgain_BecauseTheDeepScanUsesTheKeyedHash()
    {
        WriteSample();
        await BackupAsync();
        ManifestPaths().Should().ContainSingle();

        // Timestamp moves, content does not: only a content-hash comparison with the same (keyed) hash can see that.
        var path = Path.Combine(_source, "notes.txt");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
        await BackupAsync();

        ManifestPaths().Should().ContainSingle("the content is unchanged");
    }

    [Fact]
    public async Task UpgradingAnEncryptedV2Group_ByOptInReuploadsIntoTheNewNamespace_AndKeepsOldSnapshotsRestorable()
    {
        _chunk.ManifestVersion = 2;
        WriteSample();
        await BackupAsync();
        var v2Manifest = LatestManifestPath();
        var v2Chunks = Directory.GetFiles(_storageDir, "*.chunk", SearchOption.AllDirectories).Length;

        // Opt in without any change to the files: the idle run notices the format upgrade and writes a v3 snapshot.
        _chunk.ManifestVersion = 3;
        await BackupAsync();

        ManifestPaths().Should().HaveCount(2);
        LatestManifest().ChunkStorageNamespace.Should().StartWith("e3_");
        Directory.GetFiles(_storageDir, "*.chunk", SearchOption.AllDirectories).Length.Should().Be(v2Chunks * 2, "one-time re-upload under keyed ids");
        await RestoreAsync(RemoteOf(_storageDir, v2Manifest), Path.Combine(_root, "old"));
        await RestoreAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "new"));
        AssertRestored(Path.Combine(_root, "new"));
    }

    [Fact]
    public async Task PasswordService_ChangesTheKeyFile_AndMovesTheConfiguredVerificationToken()
    {
        WriteSample();
        await BackupAsync();
        var remote = RemoteOf(_storageDir, LatestManifestPath());
        _config.Setup(c => c.SaveAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        using (var storage = await OpenStorageAsync())
        {
            var tooShort = () => RepositoryPasswordService.ChangePasswordAsync(storage, _config.Object, _logger, Password, "short", false, _state, _coordinator);
            await tooShort.Should().ThrowAsync<ArgumentException>();

            var wrong = () => RepositoryPasswordService.ChangePasswordAsync(storage, _config.Object, _logger, "not-the-password", "a-brand-new-password", false, _state, _coordinator);
            await wrong.Should().ThrowAsync<WrongRepositoryPasswordException>();

            var result = await RepositoryPasswordService.ChangePasswordAsync(storage, _config.Object, _logger, Password, "a-brand-new-password", true, _state, _coordinator);
            result.Changed.Should().BeTrue();
            result.RecoveryKey.Should().StartWith("RSRK-");
        }

        var salt = Convert.FromBase64String(_encryption.Salt!);
        var service = new EncryptionService(_logger);
        service.VerifyPassword("a-brand-new-password", salt, _encryption.VerificationToken!, _encryption.KeyDerivationIterations).Should().BeTrue();
        _config.Verify(c => c.SaveAsync(It.IsAny<string>()), Times.Once);

        await RestoreAsync(remote, Path.Combine(_root, "after"), "a-brand-new-password");
    }

    // ---- review fixes ----------------------------------------------------------------------------------------------------

    private sealed class HookStorage(IStorage inner, Action<string>? afterUpload) : DelegatingStorage(inner)
    {
        public override async Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default)
        {
            await base.UploadAsync(localPath, remotePath, ct);
            afterUpload?.Invoke(remotePath);
        }
    }

    private void UseStorage(Func<IStorage, IStorage> wrap) =>
        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async () => wrap(await OpenStorageAsync()));

    private static string KeyPath(string root, string name = "repository.keys.json") => Path.Combine(root, "keys", name);

    [Fact]
    public async Task ConcurrentKeyFileCreation_OnLocalStorage_HasExactlyOneWinner()
    {
        using var storage = await OpenStorageAsync();
        var (fileA, keysA) = RepositoryKeyFiles.Create(Password, 1000);
        var (fileB, keysB) = RepositoryKeyFiles.Create(Password, 1000);
        using var _a = keysA;
        using var _b = keysB;

        var results = await Task.WhenAll(
            RepositoryKeyStore.CreateIfAbsentAsync(storage, fileA),
            RepositoryKeyStore.CreateIfAbsentAsync(storage, fileB));

        results[0].KeyFileId.Should().Be(results[1].KeyFileId, "both creators see the same stored key file");
        new[] { fileA.KeyFileId, fileB.KeyFileId }.Should().Contain(results[0].KeyFileId);
        RepositoryKeyFiles.Parse(File.ReadAllBytes(KeyFilePath)).KeyFileId.Should().Be(results[0].KeyFileId);
    }

    [Fact]
    public async Task LosingAKeyFileCreationRace_AbortsBeforeAnyChunkIsUploaded_AndTheRetryUsesTheStoredFile()
    {
        WriteSample();
        var (other, otherKeys) = RepositoryKeyFiles.Create(Password, 1000);
        using var _ = otherKeys;
        var fired = false;
        // Provider without an atomic create: another computer overwrites the key file right after our upload.
        UseStorage(inner => new HookStorage(inner, remote =>
        {
            if (!fired && remote == RepositoryKeyFiles.StoragePath)
            {
                fired = true;
                File.WriteAllBytes(KeyFilePath, RepositoryKeyFiles.Serialize(other));
            }
        }));

        var act = () => BackupAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*same time*");
        Directory.Exists(Path.Combine(_storageDir, "chunks")).Should().BeFalse("nothing is uploaded before the key file is settled");
        ManifestPaths().Should().BeEmpty();

        UseStorage(inner => inner);
        await BackupAsync();
        RepositoryKeyFiles.Parse(File.ReadAllBytes(KeyFilePath)).KeyFileId.Should().Be(other.KeyFileId);
        LatestManifest().Keys!.KeyFileId.Should().Be(other.KeyFileId);
        (await VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task PasswordChange_DetectsAConcurrentKeyFileReplacement_ByReadingBack()
    {
        WriteSample();
        await BackupAsync();
        var (other, otherKeys) = RepositoryKeyFiles.Create(Password, 1000);
        using var _ = otherKeys;
        var armed = true;
        using var inner = await OpenStorageAsync();
        var hooked = new HookStorage(inner, remote =>
        {
            if (armed && remote == RepositoryKeyFiles.StoragePath)
            {
                armed = false;
                File.WriteAllBytes(KeyFilePath, RepositoryKeyFiles.Serialize(other));
            }
        });

        var act = () => RepositoryKeyStore.ChangePasswordAsync(hooked, Password, "a-brand-new-password", 1000);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another computer*");
    }

    [Fact]
    public async Task KeyFileMirror_IsWrittenWithThePrimary_AndFollowsPasswordChanges()
    {
        WriteSample();
        await BackupAsync();
        File.ReadAllBytes(KeyPath(_storageDir, "repository.keys.1.json")).Should().Equal(File.ReadAllBytes(KeyFilePath));

        using (var storage = await OpenStorageAsync())
        {
            await RepositoryKeyStore.ChangePasswordAsync(storage, Password, "a-brand-new-password", 1000);
        }

        File.ReadAllBytes(KeyPath(_storageDir, "repository.keys.1.json")).Should().Equal(File.ReadAllBytes(KeyFilePath));
        File.Delete(KeyFilePath);
        var remote = RemoteOf(_storageDir, LatestManifestPath_NoKeys());
        var oldPw = () => RestoreAsync(remote, Path.Combine(_root, "o"), Password);
        await oldPw.Should().ThrowAsync<WrongRepositoryPasswordException>("the mirror carries the new slots only");
        await RestoreAsync(remote, Path.Combine(_root, "n"), "a-brand-new-password");
    }

    private string LatestManifestPath_NoKeys() => ManifestPaths().Last();

    [Fact]
    public async Task MissingOrDamagedPrimaryKeyFile_FallsBackToTheMirror_AndIsRepaired()
    {
        WriteSample();
        await BackupAsync();
        var remote = RemoteOf(_storageDir, LatestManifestPath());
        var original = File.ReadAllBytes(KeyFilePath);

        File.Delete(KeyFilePath);
        await RestoreAsync(remote, Path.Combine(_root, "r1"));
        File.ReadAllBytes(KeyFilePath).Should().Equal(original, "a read repairs the primary");

        File.WriteAllText(KeyFilePath, "{ not json");
        await RestoreAsync(remote, Path.Combine(_root, "r2"));
        File.ReadAllBytes(KeyFilePath).Should().Equal(original);

        // Both copies damaged: a clear error that points at import-key.
        File.WriteAllText(KeyFilePath, "garbage");
        File.WriteAllText(KeyPath(_storageDir, "repository.keys.1.json"), "garbage");
        var act = () => RestoreAsync(remote, Path.Combine(_root, "r3"));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*import-key*");
    }

    [Fact]
    public async Task Repair_RestoresAMissingKeyFileFromTheMirror_ButNotInADryRun()
    {
        WriteSample();
        await BackupAsync();
        var original = File.ReadAllBytes(KeyFilePath);
        File.Delete(KeyFilePath);

        using var storage = await OpenStorageAsync();
        await new RepositoryRepair(_logger, _config.Object, _state, _coordinator, Provider(Password)).RunAsync(storage, "local", new RepairOptions { DryRun = true });
        File.Exists(KeyFilePath).Should().BeFalse();

        await new RepositoryRepair(_logger, _config.Object, _state, _coordinator, Provider(Password)).RunAsync(storage, "local");
        File.ReadAllBytes(KeyFilePath).Should().Equal(original);
    }

    [Fact]
    public async Task ExportedKeyFile_RestoresAStorageWhoseKeyFilesWereLost_AndHoldsNoPlaintextSecret()
    {
        WriteSample();
        await BackupAsync();
        var remote = RemoteOf(_storageDir, LatestManifestPath());
        using var storage = await OpenStorageAsync();
        var exported = await RepositoryKeyStore.ExportAsync(storage);
        Encoding.UTF8.GetString(exported).Should().NotContain(Password);

        File.Delete(KeyFilePath);
        File.Delete(KeyPath(_storageDir, "repository.keys.1.json"));
        var lost = () => RestoreAsync(remote, Path.Combine(_root, "x"));
        await lost.Should().ThrowAsync<InvalidOperationException>();

        await RepositoryKeyStore.ImportAsync(storage, exported);
        await RestoreAsync(remote, Path.Combine(_root, "y"));

        var (foreign, foreignKeys) = RepositoryKeyFiles.Create(Password, 1000);
        using var _ = foreignKeys;
        var replace = () => RepositoryKeyStore.ImportAsync(storage, RepositoryKeyFiles.Serialize(foreign));
        await replace.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different repository key file*");
    }

    [Fact]
    public async Task ManifestSwappedWithinAGroup_OrAcrossGroups_IsRefused()
    {
        WriteSample();
        await BackupAsync();
        Write("notes.txt", "second " + new string('s', 500));
        await BackupAsync();
        var paths = ManifestPaths();
        paths.Should().HaveCount(2);
        var a = paths[0];
        var b = paths[1];
        var bytesA = File.ReadAllBytes(a);
        var bytesB = File.ReadAllBytes(b);

        // A <-> B inside one group: each file now holds the other snapshot.
        File.WriteAllBytes(a, bytesB);
        File.WriteAllBytes(b, bytesA);
        var within = () => RestoreAsync(RemoteOf(_storageDir, a), Path.Combine(_root, "s1"));
        await within.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another location*");
        File.WriteAllBytes(a, bytesA);
        File.WriteAllBytes(b, bytesB);

        // A second group: its manifest placed (under its own file name) in the first group's folder.
        var other = Path.Combine(_root, "src2");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "x.txt"), "group two " + new string('g', 400));
        await new Backup(_logger, _state, new SizeAnalyzer(), _config.Object, Provider(Password), _coordinator).BackupDirectoryAsync(other);
        var foreign = ManifestPaths().Except([a, b]).Single();
        var planted = Path.Combine(Path.GetDirectoryName(a)!, Path.GetFileName(foreign));
        File.Copy(foreign, planted);
        var across = () => RestoreAsync(RemoteOf(_storageDir, planted), Path.Combine(_root, "s2"));
        await across.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another location*");

        // The genuine files still restore.
        await RestoreAsync(RemoteOf(_storageDir, a), Path.Combine(_root, "s3"));
    }

    [Fact]
    public async Task AutoMode_NeverDowngradesAnEncryptedV3Group_EvenWhenThePreviousManifestIsUnreadable()
    {
        WriteSample();
        await BackupAsync();
        var damaged = ManifestPaths().Single();
        File.WriteAllBytes(damaged, File.ReadAllBytes(damaged)[..40]);

        _chunk.ManifestVersion = 0; // auto
        Write("notes.txt", "after damage " + new string('d', 500));
        await BackupAsync();

        var fresh = ManifestPaths().Single(p => p != damaged);
        var manifest = ManifestIo.Read(fresh);
        manifest.Version.Should().Be(3);
        manifest.ChunkIdScheme.Should().Be("hmac-sha256");
        EncryptedManifestEnvelope.IsEncrypted(File.ReadAllBytes(fresh)).Should().BeTrue();
    }

    [Fact]
    public async Task CorruptedChunk_IsCaughtByVerifyAndRestore_OfAnEncryptedV3Snapshot()
    {
        WriteSample();
        await BackupAsync();
        var manifest = LatestManifest();
        var chunk = manifest.Files.SelectMany(f => f.Chunks).First();
        var file = Path.Combine(_storageDir, SnapshotStoragePaths.GetChunkPath(chunk.ChunkId, manifest.ChunkStorageNamespace).Replace('/', Path.DirectorySeparatorChar));
        var bytes = File.ReadAllBytes(file);
        bytes[^3] ^= 0x55;
        File.WriteAllBytes(file, bytes);

        var verify = await VerifyAsync();
        verify.IsValid.Should().BeFalse();
        var act = () => RestoreAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "c"));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Retention_WorksOnEncryptedV3Snapshots_AndKeepsTheRemainingOneRestorable()
    {
        WriteSample("one");
        await BackupAsync();
        var firstManifest = ManifestPaths().Single();
        var firstChunks = ManifestIo.Read(firstManifest).Files.SelectMany(f => f.Chunks).Select(c => c.ChunkId).ToHashSet();

        _config.Setup(c => c.Retention).Returns(new RetentionConfig { Enabled = true, KeepLastPerDirectory = 1, MaxAgeDays = 0 });
        Write("notes.txt", "second version of notes " + new string('r', 800));
        await BackupAsync();

        ManifestPaths().Should().ContainSingle().Which.Should().NotBe(firstManifest);
        var kept = LatestManifest().Files.SelectMany(f => f.Chunks).Select(c => c.ChunkId).ToHashSet();
        foreach (var gone in firstChunks.Except(kept))
        {
            File.Exists(Path.Combine(_storageDir, SnapshotStoragePaths.GetChunkPath(gone, LatestManifest().ChunkStorageNamespace).Replace('/', Path.DirectorySeparatorChar))).Should().BeFalse();
        }

        await RestoreAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "after-retention"));
        (await VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ChunkCodec_IsBoundIntoTheChunkKeyAndAad()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var data = Encoding.UTF8.GetBytes("same plaintext, same id");
        var raw = EncryptionService.EncryptChunkDeterministic(data, key, "abc123", "raw");
        var brotli = EncryptionService.EncryptChunkDeterministic(data, key, "abc123", "brotli");
        raw.Should().NotEqual(brotli);
        EncryptionService.DecryptChunkDeterministic(raw, key, "abc123", "raw").Should().Equal(data);
        var wrongCodec = () => EncryptionService.DecryptChunkDeterministic(raw, key, "abc123", "brotli");
        wrongCodec.Should().Throw<CryptographicException>();
        // The v2 derivation (no codec context) is unchanged.
        var v2 = EncryptionService.EncryptChunkDeterministic(data, key, "abc123");
        v2.Should().NotEqual(raw);
        EncryptionService.DecryptChunkDeterministic(v2, key, "abc123").Should().Equal(data);
    }

    private sealed class SwapKeyFileAfterChunksStorage(IStorage inner, Action swap) : DelegatingStorage(inner)
    {
        private bool _done;

        public override async Task UploadAsync(string localPath, string remotePath, CancellationToken ct = default)
        {
            await base.UploadAsync(localPath, remotePath, ct);
            Maybe(remotePath);
        }

        public override async Task UploadAsync(Stream content, string remotePath, CancellationToken ct = default)
        {
            await base.UploadAsync(content, remotePath, ct);
            Maybe(remotePath);
        }

        private void Maybe(string remotePath)
        {
            if (!_done && remotePath.StartsWith("chunks/", StringComparison.Ordinal))
            {
                _done = true;
                swap();
            }
        }
    }

    [Fact]
    public async Task KeyFileReplacedAfterTheChunksAreUploaded_AbortsTheBackup_WithNothingCommitted()
    {
        WriteSample();
        var (other, otherKeys) = RepositoryKeyFiles.Create(Password, 1000);
        using var _ = otherKeys;
        UseStorage(inner => new SwapKeyFileAfterChunksStorage(inner, () =>
        {
            File.WriteAllBytes(KeyFilePath, RepositoryKeyFiles.Serialize(other));
            File.WriteAllBytes(KeyPath(_storageDir, "repository.keys.1.json"), RepositoryKeyFiles.Serialize(other));
        }));

        var act = () => BackupAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed while this backup was running*");
        ManifestPaths().Should().BeEmpty("nothing is committed");
        Directory.GetFiles(_storageDir, "HEAD", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task IncrementalWatcherSnapshots_WorkOnEncryptedV3_CarryingKeyedIds()
    {
        WriteSample();
        await BackupAsync();
        var first = LatestManifest();
        var notesBefore = first.Files.Single(f => f.RelativePath == "notes.txt");

        var changed = Write(Path.Combine(SecretFolder, SecretName), "my very private tax data edited " + new string('e', 2500));
        await new Backup(_logger, _state, new SizeAnalyzer(), _config.Object, Provider(Password), _coordinator).BackupFilesAsync([changed], _source);

        ManifestPaths().Should().HaveCount(2);
        var second = LatestManifest();
        second.ChunkIdScheme.Should().Be("hmac-sha256");
        second.ChunkStorageNamespace.Should().Be(first.ChunkStorageNamespace);
        var notesAfter = second.Files.Single(f => f.RelativePath == "notes.txt");
        notesAfter.ContentHash.Should().Be(notesBefore.ContentHash);
        notesAfter.Chunks.Select(c => c.ChunkId).Should().Equal(notesBefore.Chunks.Select(c => c.ChunkId), "the carried entry keeps its keyed ids");
        EncryptedManifestEnvelope.IsEncrypted(File.ReadAllBytes(LatestManifestPath())).Should().BeTrue();

        await RestoreAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "inc"));
        File.ReadAllText(Path.Combine(_root, "inc", SecretFolder, SecretName)).Should().Contain("edited");
        (await VerifyAsync()).IsValid.Should().BeTrue();
    }
}

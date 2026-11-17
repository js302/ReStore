using System.Security.Cryptography;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Per-chunk Brotli compression for v3 manifests (P4-2).</summary>
public class ChunkCompressionTests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "cmp_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();
    private readonly string _storageDir;
    private readonly string _source;
    private readonly SystemState _state;
    private readonly Mock<IConfigManager> _config = new();
    private readonly ChunkDiffingConfig _chunk = new() { ManifestVersion = 3 };
    private readonly EncryptionConfig _encryption = new() { Enabled = false };
    private readonly RepositoryCoordinator _coordinator;
    private readonly Mock<IPurposePasswordProvider> _passwords = new();

    public ChunkCompressionTests()
    {
        _storageDir = Path.Combine(_root, "storage");
        _source = Path.GetFullPath(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_storageDir);
        Directory.CreateDirectory(_source);

        _coordinator = new RepositoryCoordinator(Path.Combine(_root, "repository.lock"));
        _state = new SystemState(_logger);
        _state.SetStateFilePath(Path.Combine(_root, "state.json"));

        _passwords.Setup(p => p.GetPasswordAsync()).ReturnsAsync("hunter2hunter2");
        _passwords.Setup(p => p.GetPasswordAsync(It.IsAny<PasswordPurpose>())).ReturnsAsync("hunter2hunter2");

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

    private Task BackupAsync() =>
        new Backup(_logger, _state, new SizeAnalyzer(), _config.Object, _passwords.Object, _coordinator).BackupDirectoryAsync(_source);

    private static string RemoteOf(string storageRoot, string localPath) =>
        Path.GetRelativePath(storageRoot, localPath).Replace(Path.DirectorySeparatorChar, '/');

    private static byte[] Compressible(int length, int seed = 1)
    {
        var bytes = new byte[length];
        var text = System.Text.Encoding.ASCII.GetBytes($"line {seed}: the quick brown fox jumps over the lazy dog\n");
        for (var i = 0; i < length; i++) bytes[i] = text[i % text.Length];
        return bytes;
    }

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new System.Random(seed).NextBytes(bytes);
        return bytes;
    }

    private string Write(string relative, byte[] content)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string LatestManifestPath() =>
        Directory.GetFiles(_storageDir, "*.manifest.json", SearchOption.AllDirectories)
            .OrderBy(p => ManifestIo.Read(p).CreatedUtc).Last();

    private SnapshotManifest LatestManifest() => ManifestIo.Read(LatestManifestPath());

    private string ChunkFile(SnapshotManifest manifest, string chunkId) =>
        Path.Combine(_storageDir, SnapshotStoragePaths.GetChunkPath(chunkId, manifest.ChunkStorageNamespace).Replace('/', Path.DirectorySeparatorChar));

    private async Task RestoreAndCompareAsync(string remote, string target, IPasswordProvider? passwords = null)
    {
        using var storage = await OpenStorageAsync();
        await new Restore(_logger, storage, passwords ?? _passwords.Object, _state, _coordinator).RestoreFromBackupAsync(remote, target);
        foreach (var file in Directory.GetFiles(_source, "*", SearchOption.AllDirectories))
        {
            var restored = Path.Combine(target, Path.GetRelativePath(_source, file));
            File.ReadAllBytes(restored).Should().Equal(File.ReadAllBytes(file), file);
        }
    }

    private async Task<SnapshotVerificationResult> VerifyAsync(IPasswordProvider? passwords = null)
    {
        using var storage = await OpenStorageAsync();
        return await new SnapshotIntegrityVerifier(_logger, storage, passwords ?? _passwords.Object, _state, coordinator: _coordinator)
            .VerifyAsync(RemoteOf(_storageDir, LatestManifestPath()));
    }

    [Fact]
    public async Task CompressibleData_IsStoredSmaller_RecordsBrotli_AndRoundTrips()
    {
        var data = Compressible(600_000);
        Write("log.txt", data);

        await BackupAsync();

        var manifest = LatestManifest();
        manifest.Version.Should().Be(3);
        var chunks = manifest.Files.Single().Chunks;
        chunks.Should().OnlyContain(c => c.Codec == "brotli" && c.StoredSizeBytes < c.PlainSizeBytes / 4);
        foreach (var chunk in chunks)
        {
            new FileInfo(ChunkFile(manifest, chunk.ChunkId)).Length.Should().Be(chunk.StoredSizeBytes);
            // The id is still the SHA-256 of the plaintext.
        }

        await RestoreAndCompareAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "r1"));
        (await VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task IncompressibleData_IsStoredRaw()
    {
        Write("noise.dat", Random(300_000, 7));

        await BackupAsync();

        var manifest = LatestManifest();
        manifest.Files.Single().Chunks.Should().OnlyContain(c => c.Codec == "raw" && c.StoredSizeBytes == c.PlainSizeBytes);
        await RestoreAndCompareAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "r1"));
    }

    [Theory]
    [InlineData("photo.JPG")]
    [InlineData("archive.zip")]
    [InlineData("doc.docx")]
    public async Task KnownCompressedExtension_IsNotCompressed_EvenWhenContentCompresses(string name)
    {
        Write(name, Compressible(200_000));

        await BackupAsync();

        LatestManifest().Files.Single().Chunks.Should().OnlyContain(c => c.Codec == "raw" && c.StoredSizeBytes == c.PlainSizeBytes);
    }

    [Fact]
    public async Task CompressionOff_StoresRaw_AndStillWritesV3WithRawCodec()
    {
        _chunk.Compression = "off";
        Write("log.txt", Compressible(200_000));

        await BackupAsync();

        var manifest = LatestManifest();
        manifest.Version.Should().Be(3);
        manifest.Files.Single().Chunks.Should().OnlyContain(c => c.Codec == "raw" && c.StoredSizeBytes == c.PlainSizeBytes);
    }

    [Fact]
    public async Task ExistingRawChunk_IsReused_AndRecordedRaw_NotReuploadedCompressed()
    {
        // First snapshot with compression off stores the chunks raw; a second with compression on must reuse them.
        _chunk.Compression = "off";
        Write("log.txt", Compressible(200_000));
        await BackupAsync();
        var first = LatestManifest();
        var rawBytes = first.Files.Single().Chunks.ToDictionary(c => c.ChunkId, c => File.ReadAllBytes(ChunkFile(first, c.ChunkId)));

        _chunk.Compression = "auto";
        Write("other.txt", Compressible(200_000)); // same content => same chunk ids
        await BackupAsync();

        var second = LatestManifest();
        second.Files.Single(f => f.RelativePath == "other.txt").Chunks.Should().OnlyContain(c => c.Codec == "raw" && c.StoredSizeBytes == c.PlainSizeBytes);
        foreach (var (id, bytes) in rawBytes)
        {
            File.ReadAllBytes(ChunkFile(second, id)).Should().Equal(bytes, "an existing raw chunk is never replaced by another form");
        }

        await RestoreAndCompareAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "r1"));
        (await VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ExistingCompressedChunk_IsReused_ByRawPayload_WithBrotliRecorded()
    {
        // The same content under a compressed extension is chosen raw, but the stored object is brotli: record that.
        Write("log.txt", Compressible(100_000));
        await BackupAsync();
        var first = LatestManifest();
        var before = first.Files.Single().Chunks.ToDictionary(c => c.ChunkId, c => File.ReadAllBytes(ChunkFile(first, c.ChunkId)));

        File.Delete(Path.Combine(_source, "log.txt"));
        Write("same.zip", Compressible(100_000));
        await BackupAsync();

        var second = LatestManifest();
        second.Files.Single().Chunks.Should().OnlyContain(c => c.Codec == "brotli");
        foreach (var (id, bytes) in before)
        {
            File.ReadAllBytes(ChunkFile(second, id)).Should().Equal(bytes);
        }

        await RestoreAndCompareAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "r1"));
    }

    [Fact]
    public async Task MixedRawAndBrotliChunks_InOneManifest_Restore()
    {
        var mixed = Compressible(150_000).Concat(Random(150_000, 3)).Concat(Compressible(150_000, 9)).ToArray();
        Write("mixed.bin", mixed);
        Write("noise.bin", Random(100_000, 11));
        Write("text.txt", Compressible(120_000, 4));

        await BackupAsync();

        var codecs = LatestManifest().Files.SelectMany(f => f.Chunks).Select(c => c.Codec).Distinct().ToList();
        codecs.Should().BeEquivalentTo(["raw", "brotli"]);
        await RestoreAndCompareAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "r1"));
        (await VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task UnchangedBackup_UploadsNothing_AndKeepsCodecs()
    {
        Write("log.txt", Compressible(300_000));
        Write("noise.bin", Random(100_000, 5));
        await BackupAsync();
        var firstFiles = Directory.GetFiles(_storageDir, "*.chunk", SearchOption.AllDirectories).Length;

        Write("copy.txt", Compressible(300_000));
        await BackupAsync();

        Directory.GetFiles(_storageDir, "*.chunk", SearchOption.AllDirectories).Length.Should().Be(firstFiles);
        var manifest = LatestManifest();
        manifest.Files.Single(f => f.RelativePath == "copy.txt").Chunks.Should().OnlyContain(c => c.Codec == "brotli");
    }

    [Fact]
    public async Task Verify_DetectsTamperedCompressedChunk_AndRestoreFails()
    {
        Write("log.txt", Compressible(200_000));
        await BackupAsync();
        var manifest = LatestManifest();
        var victim = manifest.Files.Single().Chunks[0];
        var file = ChunkFile(manifest, victim.ChunkId);
        var bytes = File.ReadAllBytes(file);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(file, bytes);

        var result = await VerifyAsync();
        result.IsValid.Should().BeFalse();
        result.InvalidChunks.Should().BeGreaterThan(0);

        using var storage = await OpenStorageAsync();
        var act = () => new Restore(_logger, storage, null, _state, _coordinator)
            .RestoreFromBackupAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "r1"));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task EncryptedRepository_CompressesBeforeEncrypting_AndRoundTrips()
    {
        _encryption.Enabled = true;
        _encryption.Salt = Convert.ToBase64String(EncryptionService.GenerateSalt());
        _encryption.KeyDerivationIterations = 1000;
        Write("log.txt", Compressible(300_000));
        Write("noise.bin", Random(100_000, 2));

        await BackupAsync();

        var manifest = LatestManifest();
        var logChunks = manifest.Files.Single(f => f.RelativePath == "log.txt").Chunks;
        logChunks.Should().OnlyContain(c => c.Codec == "brotli" && c.StoredSizeBytes < c.PlainSizeBytes / 4);
        manifest.Files.Single(f => f.RelativePath == "noise.bin").Chunks
            .Should().OnlyContain(c => c.Codec == "raw" && c.StoredSizeBytes == c.PlainSizeBytes + 16);
        foreach (var chunk in manifest.Files.SelectMany(f => f.Chunks))
        {
            new FileInfo(ChunkFile(manifest, chunk.ChunkId)).Length.Should().Be(chunk.StoredSizeBytes);
        }

        await RestoreAndCompareAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "r1"));
        (await VerifyAsync()).IsValid.Should().BeTrue();

        // A wrong password is still reported as such (decrypt runs before decompress).
        var wrong = new Mock<IPurposePasswordProvider>();
        wrong.Setup(p => p.GetPasswordAsync()).ReturnsAsync("not-the-password");
        wrong.Setup(p => p.GetPasswordAsync(It.IsAny<PasswordPurpose>())).ReturnsAsync("not-the-password");
        (await VerifyAsync(wrong.Object)).Errors.Should().ContainSingle().Which.Should().Contain("Wrong password");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingBrotliChunk_WithDifferentSize_IsReusedNotOverwritten(bool encrypted)
    {
        if (encrypted)
        {
            _encryption.Enabled = true;
            _encryption.Salt = Convert.ToBase64String(EncryptionService.GenerateSalt());
            _encryption.KeyDerivationIterations = 1000;
        }

        var data = Compressible(100_000, 21);
        Write("a.txt", data);
        await BackupAsync();
        var first = LatestManifest();
        var chunk = first.Files.Single().Chunks.Single();
        var file = ChunkFile(first, chunk.ChunkId);

        // Replace the object with a brotli form of different size (as another Brotli build could produce).
        var other = new byte[BrotliEncoderMax(data.Length)];
        System.IO.Compression.BrotliEncoder.TryCompress(data, other, out var written, 9, 22).Should().BeTrue();
        var altCompressed = other.AsSpan(0, written).ToArray();
        altCompressed.Length.Should().NotBe((int)new FileInfo(file).Length - (encrypted ? 16 : 0));
        var altStored = encrypted
            ? EncryptionService.EncryptChunkDeterministic(altCompressed, DeriveKey(), chunk.ChunkId, "brotli")
            : altCompressed;
        File.WriteAllBytes(file, altStored);

        Write("b.txt", data);
        await BackupAsync();

        File.ReadAllBytes(file).Should().Equal(altStored, "a decodable object is never overwritten");
        var second = LatestManifest();
        var entry = second.Files.Single(f => f.RelativePath == "b.txt").Chunks.Single();
        entry.Codec.Should().Be("brotli");
        entry.StoredSizeBytes.Should().Be(altStored.Length);
        await RestoreAndCompareAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "r1"));
        (await VerifyAsync()).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task TruncatedExistingChunk_IsOverwritten()
    {
        var data = Compressible(100_000, 22);
        Write("a.txt", data);
        await BackupAsync();
        var first = LatestManifest();
        var chunk = first.Files.Single().Chunks.Single();
        var file = ChunkFile(first, chunk.ChunkId);
        var good = File.ReadAllBytes(file);
        File.WriteAllBytes(file, good[..(good.Length / 2)]);

        Write("b.txt", data);
        await BackupAsync();

        File.ReadAllBytes(file).Should().Equal(good);
        await RestoreAndCompareAsync(RemoteOf(_storageDir, LatestManifestPath()), Path.Combine(_root, "r1"));
        _logger.Messages.Should().Contain(m => m.Contains("replacing it"));
    }

    [Fact]
    public async Task UnreadableAmbiguousExistingChunk_FailsTheBackup_AndIsNotOverwritten()
    {
        var data = Compressible(100_000, 23);
        Write("a.txt", data);
        await BackupAsync();
        var first = LatestManifest();
        var file = ChunkFile(first, first.Files.Single().Chunks.Single().ChunkId);
        var original = File.ReadAllBytes(file);
        var damaged = original[..(original.Length / 2)];
        File.WriteAllBytes(file, damaged);

        _config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).Returns(async () => new FailingDownloadStorage(await OpenStorageAsync()));
        Write("b.txt", data);
        var act = BackupAsync;

        await act.Should().ThrowAsync<IOException>();
        File.ReadAllBytes(file).Should().Equal(damaged);
    }

    private sealed class FailingDownloadStorage(IStorage inner) : DelegatingStorage(inner)
    {
        public override Task DownloadAsync(string remotePath, string localPath, CancellationToken ct = default) =>
            remotePath.Contains("chunks/") ? throw new IOException("network down") : base.DownloadAsync(remotePath, localPath, ct);
    }

    private static int BrotliEncoderMax(int length) => System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(length);

    // Encrypted v3: the chunk key of the repository key file (the v2 password-derived master key is not used for v3 chunks).
    private byte[] DeriveKey() => ManifestIo.UnlockNear(LatestManifestPath()).ChunkKey;

    [Fact]
    public async Task V2Write_NeverCarriesCodecs_AndStoresRaw()
    {
        _chunk.ManifestVersion = 2;
        Write("log.txt", Compressible(200_000));

        await BackupAsync();

        var manifest = LatestManifest();
        manifest.Version.Should().Be(2);
        manifest.Files.Single().Chunks.Should().OnlyContain(c => c.Codec == null && c.StoredSizeBytes == c.PlainSizeBytes);
        File.ReadAllText(LatestManifestPath()).Should().NotContain("Codec");
    }

    [Fact]
    public async Task ForcedV2_AfterCompressedV3Snapshot_FailsClearly_InsteadOfWritingAnUnreadableManifest()
    {
        Write("log.txt", Compressible(200_000));
        await BackupAsync();

        _chunk.ManifestVersion = 2;
        Write("log.txt", Compressible(200_001));
        var act = BackupAsync;

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("compressed");
    }

    [Fact]
    public void ChunkCompression_Decompress_RejectsWrongSizeAndGarbage()
    {
        var plain = Compressible(10_000);
        var compressed = ChunkCompression.TryCompress(plain);
        compressed.Should().NotBeNull();
        ChunkCompression.Decompress(compressed!, plain.Length).Should().Equal(plain);
        var wrongSize = () => ChunkCompression.Decompress(compressed!, plain.Length - 1);
        wrongSize.Should().Throw<InvalidDataException>();
        var garbage = () => ChunkCompression.Decompress(Random(500, 1), 10_000);
        garbage.Should().Throw<InvalidDataException>();
        var huge = () => ChunkCompression.Decompress(compressed!, ChunkCompression.MaxPlainChunkBytes + 1);
        huge.Should().Throw<InvalidDataException>();
    }
}

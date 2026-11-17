using FluentAssertions;
using Octokit;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.github;
using ReStore.Core.src.storage.google;
using ReStore.Core.src.storage.local;
using ReStore.Core.src.storage.sftp;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class StorageContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreStorageContract_" + Guid.NewGuid().ToString("N"));
    private readonly ILogger _logger = new TestLogger();

    public StorageContractTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "storage"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<LocalStorage> CreateLocalAsync()
    {
        var storage = new LocalStorage(_logger);
        await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = Path.Combine(_root, "storage") });
        return storage;
    }

    [Fact]
    public async Task Local_Upload_WritesContentAndLeavesNoTempFiles()
    {
        using var storage = await CreateLocalAsync();
        var source = Path.Combine(_root, "src.bin");
        await File.WriteAllTextAsync(source, "new");

        await storage.UploadAsync(source, "a/b.bin");
        await storage.UploadAsync(source, "a/b.bin"); // overwrite

        File.ReadAllText(Path.Combine(_root, "storage", "a", "b.bin")).Should().Be("new");
        Directory.GetFiles(Path.Combine(_root, "storage", "a")).Should().ContainSingle();
    }

    [Fact]
    public async Task Local_Upload_WhenMoveFails_LeavesNoPartialFile()
    {
        using var storage = await CreateLocalAsync();
        var source = Path.Combine(_root, "src.bin");
        await File.WriteAllTextAsync(source, "data");
        // A directory occupies the final name, so the move cannot succeed.
        var blocker = Path.Combine(_root, "storage", "blocked.bin");
        Directory.CreateDirectory(blocker);

        var act = () => storage.UploadAsync(source, "blocked.bin");

        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.Exists(blocker).Should().BeTrue();
        Directory.GetFileSystemEntries(Path.Combine(_root, "storage")).Should().ContainSingle()
            .Which.Should().Be(blocker);
    }

    [Fact]
    public async Task Local_Download_Missing_ThrowsStorageNotFoundException()
    {
        using var storage = await CreateLocalAsync();
        var target = Path.Combine(_root, "out", "x.bin");

        var act = () => storage.DownloadAsync("nope.bin", target);

        await act.Should().ThrowAsync<StorageNotFoundException>();
        File.Exists(target).Should().BeFalse();
        File.Exists(target + ".part").Should().BeFalse();
    }

    [Fact]
    public async Task Local_Download_ExistingFile_CopiesAndLeavesNoPartFile()
    {
        using var storage = await CreateLocalAsync();
        var source = Path.Combine(_root, "src.bin");
        await File.WriteAllTextAsync(source, "payload");
        await storage.UploadAsync(source, "p.bin");
        var target = Path.Combine(_root, "out", "p.bin");

        await storage.DownloadAsync("p.bin", target);

        File.ReadAllText(target).Should().Be("payload");
        File.Exists(target + ".part").Should().BeFalse();
    }

    [Fact]
    public async Task Local_Delete_Missing_IsNoOp()
    {
        using var storage = await CreateLocalAsync();

        var act = () => storage.DeleteAsync("missing.bin");

        await act.Should().NotThrowAsync();
        (await storage.ExistsAsync("missing.bin")).Should().BeFalse();
    }

    [Fact]
    public void StorageNotFoundException_IsFileNotFoundException()
    {
        new StorageNotFoundException("x").Should().BeAssignableTo<FileNotFoundException>();
    }

    private sealed class FakeStorage(bool failInit) : StorageBase(new TestLogger())
    {
        public bool WasDisposed { get; private set; }

        protected override Task InitializeCoreAsync(Dictionary<string, string> options) =>
            failInit ? throw new InvalidOperationException("init failed") : Task.CompletedTask;
        protected override Task UploadCoreAsync(string localPath, string remotePath, CancellationToken ct) => Task.CompletedTask;
        protected override Task DownloadCoreAsync(string remotePath, string localPath, CancellationToken ct) => Task.CompletedTask;
        protected override Task<bool> ExistsCoreAsync(string remotePath, CancellationToken ct) => Task.FromResult(false);
        protected override Task<long?> GetSizeCoreAsync(string remotePath, CancellationToken ct) => Task.FromResult<long?>(null);
        protected override Task DeleteCoreAsync(string remotePath, CancellationToken ct) => Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task StorageFactory_DisposesProvider_WhenInitializeThrows()
    {
        var fake = new FakeStorage(failInit: true);
        var factory = new StorageFactory(_logger);
        factory.RegisterForTests("fake", _ => fake);

        var act = () => factory.CreateStorageAsync("fake", new StorageConfig());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("init failed");
        fake.WasDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task StorageFactory_DoesNotDispose_WhenInitializeSucceeds()
    {
        var fake = new FakeStorage(failInit: false);
        var factory = new StorageFactory(_logger);
        factory.RegisterForTests("fake", _ => fake);

        var storage = await factory.CreateStorageAsync("fake", new StorageConfig());

        storage.Should().BeSameAs(fake);
        fake.WasDisposed.Should().BeFalse();
    }

    // ---- SFTP host key helper ----

    private const string Fp = "ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og";

    [Theory]
    [InlineData("SHA256:" + Fp)]
    [InlineData(Fp)]
    [InlineData("  sha256:" + Fp + "=  ")]
    public void SftpHostKey_Matches_AcceptsPrefixPaddingAndWhitespace(string configured)
    {
        SftpHostKey.Matches(configured, Fp).Should().BeTrue();
    }

    [Fact]
    public void SftpHostKey_Matches_IsCaseSensitiveAndRejectsDifferentKeys()
    {
        SftpHostKey.Matches(Fp.ToLowerInvariant(), Fp).Should().BeFalse();
        SftpHostKey.Matches("SHA256:" + new string('A', 43), Fp).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SHA256:short")]
    [InlineData("SHA256:!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    public void SftpHostKey_Invalid_IsRejected(string? value)
    {
        SftpHostKey.IsValid(value).Should().BeFalse();
        SftpHostKey.Matches(value, Fp).Should().BeFalse();
    }

    // ---- Drive retry classification ----

    private static Google.GoogleApiException DriveError(System.Net.HttpStatusCode code, string? reason = null)
    {
        var ex = new Google.GoogleApiException("Drive", "msg") { HttpStatusCode = code };
        if (reason != null)
        {
            ex.Error = new Google.Apis.Requests.RequestError
            {
                Code = (int)code,
                Errors = [new Google.Apis.Requests.SingleError { Reason = reason }]
            };
        }
        return ex;
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, null, true)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError, null, true)]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, null, true)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "rateLimitExceeded", true)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "userRateLimitExceeded", true)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "insufficientPermissions", false)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, null, false)]
    [InlineData(System.Net.HttpStatusCode.NotFound, null, false)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, null, false)]
    public void Drive_IsTransient_ClassifiesApiErrors(System.Net.HttpStatusCode code, string? reason, bool expected)
    {
        DriveStorage.IsTransient(DriveError(code, reason)).Should().Be(expected);
    }

    [Fact]
    public void Drive_IsTransient_NeverRetriesUserCancellationButRetriesTimeouts()
    {
        DriveStorage.IsTransient(new OperationCanceledException()).Should().BeFalse();
        DriveStorage.IsTransient(new TaskCanceledException()).Should().BeFalse();
        DriveStorage.IsTransient(new TaskCanceledException("t", new TimeoutException())).Should().BeTrue();
        DriveStorage.IsTransient(new HttpRequestException()).Should().BeTrue();
        DriveStorage.IsTransient(new InvalidOperationException()).Should().BeFalse();
    }

    [Fact]
    public void Drive_IsTransient_ExcludesLocalDiskErrors()
    {
        DriveStorage.IsTransient(new FileNotFoundException()).Should().BeFalse();
        DriveStorage.IsTransient(new DirectoryNotFoundException()).Should().BeFalse();
        DriveStorage.IsTransient(new UnauthorizedAccessException()).Should().BeFalse();
        DriveStorage.IsTransient(new IOException("network reset")).Should().BeTrue();
    }

    private sealed class FakeDownloadProgress(Google.Apis.Download.DownloadStatus status, Exception? ex) : Google.Apis.Download.IDownloadProgress
    {
        public Google.Apis.Download.DownloadStatus Status { get; } = status;
        public long BytesDownloaded => 0;
        public Exception? Exception { get; } = ex;
    }

    private sealed class FakeUploadProgress(Google.Apis.Upload.UploadStatus status, Exception? ex) : Google.Apis.Upload.IUploadProgress
    {
        public Google.Apis.Upload.UploadStatus Status { get; } = status;
        public long BytesSent => 0;
        public Exception? Exception { get; } = ex;
    }

    [Fact]
    public void Drive_ThrowIfDownloadFailed_MapsStatusToException()
    {
        var api = DriveError(System.Net.HttpStatusCode.ServiceUnavailable);
        DriveStorage.ThrowIfDownloadFailed(new FakeDownloadProgress(Google.Apis.Download.DownloadStatus.Completed, null));

        var withEx = () => DriveStorage.ThrowIfDownloadFailed(new FakeDownloadProgress(Google.Apis.Download.DownloadStatus.Failed, api));
        withEx.Should().Throw<Google.GoogleApiException>().Which.Should().BeSameAs(api);
        var noEx = () => DriveStorage.ThrowIfDownloadFailed(new FakeDownloadProgress(Google.Apis.Download.DownloadStatus.Failed, null));
        noEx.Should().Throw<IOException>();
    }

    [Fact]
    public void Drive_ThrowIfUploadFailed_MapsStatusToException()
    {
        var api = DriveError(System.Net.HttpStatusCode.Forbidden, "rateLimitExceeded");
        var ok = new FakeUploadProgress(Google.Apis.Upload.UploadStatus.Completed, null);
        DriveStorage.ThrowIfUploadFailed(ok).Should().BeSameAs(ok);

        var withEx = () => DriveStorage.ThrowIfUploadFailed(new FakeUploadProgress(Google.Apis.Upload.UploadStatus.Failed, api));
        DriveStorage.IsTransient(withEx.Should().Throw<Google.GoogleApiException>().Which).Should().BeTrue();
        var noEx = () => DriveStorage.ThrowIfUploadFailed(new FakeUploadProgress(Google.Apis.Upload.UploadStatus.Failed, null));
        noEx.Should().Throw<IOException>();
    }

    [Fact]
    public void Drive_ComputeRetryDelay_GrowsExponentiallyWithJitterAndCaps()
    {
        DriveStorage.ComputeRetryDelay(1, 0.5).Should().Be(TimeSpan.FromSeconds(1));
        DriveStorage.ComputeRetryDelay(3, 0.5).Should().Be(TimeSpan.FromSeconds(4));
        DriveStorage.ComputeRetryDelay(1, 0).Should().Be(TimeSpan.FromMilliseconds(750));
        DriveStorage.ComputeRetryDelay(1, 1).Should().Be(TimeSpan.FromMilliseconds(1250));
        DriveStorage.ComputeRetryDelay(20, 1).Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(37500));
    }

    // ---- GitHub rate limit classification ----

    private sealed class FakeResponse : IResponse
    {
        public object? Body { get; init; }
        public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
        public ApiInfo ApiInfo { get; } = new(new Dictionary<string, Uri>(), [], [], "", new RateLimit(5000, 0, DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()));
        public System.Net.HttpStatusCode StatusCode { get; init; }
        public string ContentType => "application/json";
    }

    [Fact]
    public void GitHub_GetRateLimitDelay_PrimaryUsesResetWithinBound()
    {
        var ex = new RateLimitExceededException(new FakeResponse { StatusCode = System.Net.HttpStatusCode.Forbidden });

        var delay = GitHubStorage.GetRateLimitDelay(ex);

        delay.Should().NotBeNull();
        delay!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThan(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void GitHub_GetRateLimitDelay_SecondaryHonoursRetryAfterHeader()
    {
        var response = new FakeResponse
        {
            StatusCode = System.Net.HttpStatusCode.Forbidden,
            Headers = new Dictionary<string, string> { ["Retry-After"] = "12" }
        };

        GitHubStorage.GetRateLimitDelay(new SecondaryRateLimitExceededException(response))
            .Should().Be(TimeSpan.FromSeconds(13));
    }

    [Fact]
    public void GitHub_GetRateLimitDelay_TooLongOrUnrelated_ReturnsNull()
    {
        var tooLong = new FakeResponse
        {
            StatusCode = System.Net.HttpStatusCode.Forbidden,
            Headers = new Dictionary<string, string> { ["Retry-After"] = "3600" }
        };

        GitHubStorage.GetRateLimitDelay(new SecondaryRateLimitExceededException(tooLong)).Should().BeNull();
        GitHubStorage.GetRateLimitDelay(new InvalidOperationException()).Should().BeNull();
    }
}

public class StoragePrefixTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("a", "a/")]
    [InlineData("a/b/", "a/b/")]
    [InlineData("a\\b", "a/b/")]
    [InlineData("a//b", "a/b/")]
    public void NormalizePrefix_ProducesTrailingSlashForm(string? input, string expected)
    {
        StorageBase.NormalizePrefix(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("/a")]
    [InlineData("a/../b")]
    [InlineData("..")]
    [InlineData("./a")]
    public void NormalizePrefix_RejectsRootedAndTraversal(string input)
    {
        var act = () => StorageBase.NormalizePrefix(input);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Initialize_RejectsInvalidPrefix()
    {
        var storage = new InMemoryStorage(new TestLogger());

        var act = () => storage.InitializeAsync(new Dictionary<string, string> { ["prefix"] = "../x" });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RemoteProvider_WithLegacyPathOption_LogsOneWarning_AndIgnoresIt()
    {
        StorageBase.ResetLegacyPathWarningsForTests();
        var logger = new TestLogger();
        var store = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
        var first = new InMemoryStorage(logger, store);
        var second = new InMemoryStorage(logger, store);

        await first.InitializeAsync(new Dictionary<string, string> { ["path"] = "/backups" });
        await second.InitializeAsync(new Dictionary<string, string> { ["path"] = "/backups" });
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "x");
            await first.UploadAsync(file, "k");
        }
        finally { File.Delete(file); }

        logger.Messages.Count(m => m.Contains("'path' option is ignored") && m.Contains("prefix")).Should().Be(1);
        store.Keys.Should().ContainSingle().Which.Should().Be("k");
    }

    [Fact]
    public async Task LocalStorage_DoesNotWarnAboutPathOption()
    {
        StorageBase.ResetLegacyPathWarningsForTests();
        var logger = new TestLogger();
        var dir = Path.Combine(Path.GetTempPath(), "ReStorePrefix_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var storage = new LocalStorage(logger);
            await storage.InitializeAsync(new Dictionary<string, string> { ["path"] = dir });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        logger.Messages.Should().NotContain(m => m.Contains("ignored"));
    }

    [Fact]
    public async Task ShareLink_AppliesPrefix()
    {
        var storage = new LinkStorage();
        await storage.InitializeAsync(new Dictionary<string, string> { ["prefix"] = "p" });

        (await storage.GenerateShareLinkAsync("shared/x", TimeSpan.FromHours(1))).Should().Be("link:p/shared/x");
    }

    private sealed class LinkStorage() : InMemoryStorage(new TestLogger())
    {
        protected override Task<string> GenerateShareLinkCoreAsync(string remotePath, TimeSpan expiration) =>
            Task.FromResult("link:" + remotePath);
    }
}

public class StorageListingDecisionTests
{
    [Fact]
    public void GitHub_Classify_DistinguishesEmptyTruncatedAndAvailable()
    {
        GitHubStorage.Classify(null).Should().Be(GitHubStorage.TreeAvailability.Empty);
        GitHubStorage.Classify(new TreeResponse("sha", "url", [], true)).Should().Be(GitHubStorage.TreeAvailability.Truncated);
        GitHubStorage.Classify(new TreeResponse("sha", "url", [], false)).Should().Be(GitHubStorage.TreeAvailability.Available);
    }

    [Theory]
    [InlineData("chunks/ab/x.part-0123456789abcdef0123456789abcdef", true)]
    [InlineData("chunks/ab/x.part", false)]
    [InlineData("chunks/ab/x.part-123", false)]
    [InlineData("chunks/ab/x", false)]
    public void Sftp_OnlyOurOwnPartNamesAreHidden(string path, bool hidden)
    {
        SftpStorage.IsOwnPartFile(path).Should().Be(hidden);
    }
}

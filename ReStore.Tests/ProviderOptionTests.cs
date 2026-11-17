using FluentAssertions;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.aws;
using ReStore.Core.src.storage.azure;
using ReStore.Core.src.storage.backblaze;
using ReStore.Core.src.storage.dropbox;
using ReStore.Core.src.storage.github;
using ReStore.Core.src.storage.google;
using ReStore.Core.src.storage.sftp;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Option validation of the remote providers; every case fails before any network access.</summary>
public class ProviderOptionTests
{
    public static TheoryData<string, string[]> MissingOptionCases => new()
    {
        { "s3", ["accessKeyId", "secretAccessKey", "region", "bucketName"] },
        { "b2", ["keyId", "applicationKey", "serviceUrl", "bucketName"] },
        { "azure", ["connectionString", "containerName"] },
        { "gcp", ["bucketName"] },
        { "sftp", ["host", "username"] },
        { "github", ["token", "owner", "repo"] },
    };

    [Theory]
    [MemberData(nameof(MissingOptionCases))]
    public async Task Initialize_WithNoOptions_NamesEveryMissingOption(string type, string[] expected)
    {
        var act = () => new StorageFactory(new TestLogger()).CreateStorageAsync(type, new StorageConfig { Options = [] });

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        foreach (var key in expected)
        {
            ex.Message.Should().Contain(key);
        }
    }

    [Theory]
    [InlineData("s3", "region")]
    [InlineData("b2", "serviceUrl")]
    [InlineData("azure", "containerName")]
    [InlineData("github", "token")]
    public async Task Initialize_TreatsEmptyValuesAsMissing(string type, string emptyKey)
    {
        var options = new Dictionary<string, string>
        {
            ["accessKeyId"] = "a", ["secretAccessKey"] = "s", ["region"] = "us-east-1", ["bucketName"] = "b",
            ["keyId"] = "k", ["applicationKey"] = "k", ["serviceUrl"] = "http://127.0.0.1:1",
            ["connectionString"] = "UseDevelopmentStorage=true", ["containerName"] = "c",
            ["token"] = "t", ["owner"] = "o", ["repo"] = "r",
        };
        options[emptyKey] = "";

        var act = () => new StorageFactory(new TestLogger()).CreateStorageAsync(type, new StorageConfig { Options = options });

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(emptyKey);
    }

    [Fact]
    public async Task Dropbox_RequiresAnAccessTokenOrACompleteRefreshTokenTriple()
    {
        foreach (var options in new[]
        {
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["refreshToken"] = "r", ["appKey"] = "k" },
            new Dictionary<string, string> { ["accessToken"] = "" },
        })
        {
            var act = () => new DropboxStorage(new TestLogger()).InitializeAsync(options);

            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("accessToken");
        }
    }

    [Fact]
    public async Task Sftp_RequiresPasswordOrPrivateKey_AndReportsItThroughTheInitializeWrapper()
    {
        using var storage = new SftpStorage(new TestLogger());

        var act = () => storage.InitializeAsync(new Dictionary<string, string> { ["host"] = "127.0.0.1", ["username"] = "u" });

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.InnerException.Should().BeOfType<ArgumentException>().Which.Message.Should().Contain("privateKeyPath");
    }

    [Fact]
    public async Task Sftp_RejectsAMalformedHostKeyFingerprint()
    {
        using var storage = new SftpStorage(new TestLogger());

        var act = () => storage.InitializeAsync(new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1", ["username"] = "u", ["password"] = "p", ["hostKeyFingerprint"] = "MD5:aa:bb"
        });

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.InnerException.Should().BeOfType<ArgumentException>().Which.Message.Should().Contain("SHA256");
    }

    [Fact]
    public async Task Sftp_UnreachableHost_FailsWithAnInitializationError()
    {
        using var storage = new SftpStorage(new TestLogger());

        var act = () => storage.InitializeAsync(new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1", ["port"] = "1", ["username"] = "u", ["password"] = "p"
        });

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("SFTP");
    }

    [Fact]
    public async Task Gcp_UnreadableCredentialFile_FailsWithAnInitializationError()
    {
        using var storage = new GcpStorage(new TestLogger());

        var act = () => storage.InitializeAsync(new Dictionary<string, string>
        {
            ["bucketName"] = "b", ["credentialPath"] = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".json")
        });

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("GCP");
    }

    [Fact]
    public void ProvidersThatCanShareLinks_AdvertiseIt()
    {
        var logger = new TestLogger();

        new AzureStorage(logger).SupportsSharing.Should().BeTrue();
        new GcpStorage(logger).SupportsSharing.Should().BeTrue();
        new DropboxStorage(logger).SupportsSharing.Should().BeTrue();
        new SftpStorage(logger).SupportsSharing.Should().BeFalse();
        new GitHubStorage(logger).SupportsSharing.Should().BeFalse();
    }

    [Fact]
    public void Providers_ReportSensibleConcurrencyHints()
    {
        var logger = new TestLogger();

        new S3Storage(logger).MaxConcurrency.Should().Be(8);
        new B2Storage(logger).MaxConcurrency.Should().Be(8);
        new SftpStorage(logger).MaxConcurrency.Should().Be(1);
        new DropboxStorage(logger).MaxConcurrency.Should().Be(4);
    }
}

public class OffsetReadStreamTests
{
    [Fact]
    public void PositionZeroIsTheCallersCurrentPosition()
    {
        using var inner = new MemoryStream("skip|payload"u8.ToArray()) { Position = 5 };
        using var view = new OffsetReadStream(inner);

        view.Length.Should().Be(7);
        view.Position.Should().Be(0);
        var buffer = new byte[16];
        var read = view.Read(buffer, 0, buffer.Length);
        System.Text.Encoding.UTF8.GetString(buffer, 0, read).Should().Be("payload");
        view.Position.Should().Be(7);
    }

    [Fact]
    public void SeekingToTheStartRewindsOnlyToTheCallersPosition_NotTheUnderlyingStreamStart()
    {
        using var inner = new MemoryStream("skip|payload"u8.ToArray()) { Position = 5 };
        using var view = new OffsetReadStream(inner);
        view.Seek(3, SeekOrigin.Begin);

        view.Position = 0;

        inner.Position.Should().Be(5);
        view.Seek(-3, SeekOrigin.End).Should().Be(4);
        view.Seek(1, SeekOrigin.Current).Should().Be(5);
        view.Invoking(v => v.Seek(-1, SeekOrigin.Begin)).Should().Throw<IOException>();
    }

    [Fact]
    public async Task AsyncReads_AndDispose_LeaveTheUnderlyingStreamOpen()
    {
        var inner = new MemoryStream("abcdef"u8.ToArray()) { Position = 2 };
        var view = new OffsetReadStream(inner);
        var buffer = new byte[4];

        (await view.ReadAsync(buffer.AsMemory())).Should().Be(4);
        (await view.ReadAsync(buffer, 0, 4, CancellationToken.None)).Should().Be(0);
        view.Read(buffer.AsSpan()).Should().Be(0);
        view.Dispose();

        inner.CanRead.Should().BeTrue();
        view.CanWrite.Should().BeFalse();
        view.Invoking(v => v.SetLength(1)).Should().Throw<NotSupportedException>();
        view.Invoking(v => v.Write(buffer, 0, 1)).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void RejectsNonSeekableStreams()
    {
        using var forwardOnly = new System.IO.Compression.DeflateStream(new MemoryStream(), System.IO.Compression.CompressionMode.Compress);

        var act = () => new OffsetReadStream(forwardOnly);

        act.Should().Throw<ArgumentException>();
    }
}

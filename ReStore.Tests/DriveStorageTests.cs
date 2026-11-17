using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.google;

namespace ReStore.Tests;

/// <summary>
/// Drives <see cref="DriveStorage"/> through the real Google Drive client against an in-memory fake of the Drive v3 API
/// (list/create/update/get/delete with resumable uploads), injected through the client's HttpClientFactory.
/// </summary>
public sealed class DriveStorageTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "ReStoreDrive_" + Guid.NewGuid().ToString("N"));
    private readonly FakeDriveHandler _fake = new();
    private readonly TestLogger _logger = new();
    private readonly List<IDisposable> _disposables = [];

    public DriveStorageTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_scratch, true); } catch { }
    }

    private async Task<DriveStorage> OpenAsync(string folderName = "ReStoreBackups")
    {
        var service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientFactory = new FakeFactory(_fake),
            ApplicationName = "test",
            // The tests exercise ReStore's own retry loop; the client's built-in one would just slow them down.
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
            GZipEnabled = false
        });
        var storage = new DriveStorage(_logger);
        _disposables.Add(storage);
        await storage.AttachServiceAsync(service, folderName);
        return storage;
    }

    private async Task<string> LocalAsync(string name, byte[] content)
    {
        var path = Path.Combine(_scratch, name);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private static async Task<List<StorageEntry>> ToListAsync(IAsyncEnumerable<StorageEntry> source)
    {
        var list = new List<StorageEntry>();
        await foreach (var e in source) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Attach_CreatesTheBackupFolder_OnlyWhenItDoesNotExist()
    {
        await OpenAsync("MyBackups");
        await OpenAsync("MyBackups");

        _fake.Items.Values.Count(i => i.Name == "MyBackups" && i.IsFolder).Should().Be(1);
    }

    [Fact]
    public async Task Upload_CreatesNestedFolders_ThenRoundTripsAndOverwritesInPlace()
    {
        var storage = await OpenAsync();

        await storage.UploadAsync(await LocalAsync("a", "first"u8.ToArray()), "chunks/aa/file.bin");
        await storage.UploadAsync(await LocalAsync("b", "second!"u8.ToArray()), "chunks/aa/file.bin");
        await storage.DownloadAsync("chunks/aa/file.bin", Path.Combine(_scratch, "out", "down.bin"));

        (await File.ReadAllBytesAsync(Path.Combine(_scratch, "out", "down.bin"))).Should().Equal("second!"u8.ToArray());
        _fake.Items.Values.Count(i => i.Name == "file.bin").Should().Be(1, "the second upload updates the existing file");
        _fake.Items.Values.Where(i => i.IsFolder).Select(i => i.Name).Should().BeEquivalentTo("ReStoreBackups", "chunks", "aa");
    }

    [Fact]
    public async Task Upload_RemovesDuplicateFilesLeftByEarlierRaces()
    {
        var storage = await OpenAsync();
        var root = _fake.Items.Values.Single(i => i.Name == "ReStoreBackups").Id;
        _fake.AddFile(root, "dup.bin", [1]);
        _fake.AddFile(root, "dup.bin", [2]);

        await storage.UploadAsync(await LocalAsync("d", [9, 9, 9]), "dup.bin");

        var remaining = _fake.Items.Values.Where(i => i.Name == "dup.bin").ToList();
        remaining.Should().ContainSingle();
        remaining[0].Content.Should().Equal(9, 9, 9);
    }

    [Fact]
    public async Task Upload_LargerThanOneChunk_ReassemblesCorrectly()
    {
        var storage = await OpenAsync();
        var payload = new byte[256 * 1024 * 3 + 17];
        new Random(5).NextBytes(payload);

        await storage.UploadAsync(await LocalAsync("big", payload), "big.bin");
        await storage.DownloadAsync("big.bin", Path.Combine(_scratch, "big.out"));

        (await File.ReadAllBytesAsync(Path.Combine(_scratch, "big.out"))).Should().Equal(payload);
    }

    [Fact]
    public async Task UploadStream_BuffersViaTheFileOverload_FromTheCurrentPosition()
    {
        var storage = await OpenAsync();
        using var stream = new MemoryStream("skip|payload"u8.ToArray()) { Position = 5 };

        await storage.UploadAsync(stream, "s/stream.bin");
        await storage.DownloadAsync("s/stream.bin", Path.Combine(_scratch, "s.out"));

        (await File.ReadAllBytesAsync(Path.Combine(_scratch, "s.out"))).Should().Equal("payload"u8.ToArray());
    }

    [Fact]
    public async Task Upload_UnexpectedApiError_IsWrapped()
    {
        var storage = await OpenAsync();
        _fake.FailUploads = HttpStatusCode.Forbidden;
        var local = await LocalAsync("x", [1]);

        var act = () => storage.UploadAsync(local, "x.bin");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Google Drive");
        _logger.Messages.Should().Contain(m => m.Contains("Failed to upload"));
    }

    [Fact]
    public async Task Download_Missing_ThrowsStorageNotFound_ForMissingFileAndMissingFolder()
    {
        var storage = await OpenAsync();

        var file = () => storage.DownloadAsync("nope.bin", Path.Combine(_scratch, "n1"));
        var folder = () => storage.DownloadAsync("no/such/dir/nope.bin", Path.Combine(_scratch, "n2"));

        await file.Should().ThrowAsync<StorageNotFoundException>();
        await folder.Should().ThrowAsync<StorageNotFoundException>();
        File.Exists(Path.Combine(_scratch, "n1")).Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAndGetSize_ReportPresence()
    {
        var storage = await OpenAsync();
        await storage.UploadAsync(await LocalAsync("sz", new byte[4321]), "n/sz.bin");

        (await storage.ExistsAsync("n/sz.bin")).Should().BeTrue();
        (await storage.ExistsAsync("n/none.bin")).Should().BeFalse();
        (await storage.ExistsAsync("nofolder/none.bin")).Should().BeFalse();
        (await storage.GetSizeAsync("n/sz.bin")).Should().Be(4321);
        (await storage.GetSizeAsync("n/none.bin")).Should().BeNull();
        (await storage.GetSizeAsync("nofolder/none.bin")).Should().BeNull();
    }

    [Fact]
    public async Task Exists_PropagatesUnexpectedErrors()
    {
        var storage = await OpenAsync();
        _fake.FailLists = HttpStatusCode.Unauthorized;

        var act = () => storage.ExistsAsync("a.bin");

        await act.Should().ThrowAsync<Google.GoogleApiException>();
        _logger.Messages.Should().Contain(m => m.Contains("Error checking if file exists"));
    }

    [Fact]
    public async Task Delete_RemovesTheFile_AndIsANoOpWhenMissing()
    {
        var storage = await OpenAsync();
        await storage.UploadAsync(await LocalAsync("d", [1]), "d/del.bin");

        await storage.DeleteAsync("d/del.bin");
        await storage.DeleteAsync("d/del.bin");
        await storage.DeleteAsync("never/was.bin");

        (await storage.ExistsAsync("d/del.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_FileGoneBetweenLookupAndDelete_IsIgnored()
    {
        var storage = await OpenAsync();
        await storage.UploadAsync(await LocalAsync("d", [1]), "gone.bin");
        _fake.DeleteReturns = HttpStatusCode.NotFound;

        await storage.DeleteAsync("gone.bin");
    }

    [Fact]
    public async Task Delete_OtherFailures_AreWrapped()
    {
        var storage = await OpenAsync();
        await storage.UploadAsync(await LocalAsync("d", [1]), "x.bin");
        _fake.DeleteReturns = HttpStatusCode.Forbidden;

        var act = () => storage.DeleteAsync("x.bin");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task List_WalksSubfoldersAcrossPages_AndFiltersByPrefix()
    {
        var storage = await OpenAsync();
        _fake.PageSize = 2;
        var local = await LocalAsync("l", "12345"u8.ToArray());
        foreach (var k in new[] { "chunks/aa/1", "chunks/aa/2", "chunks/ab/3", "chunks/ab/sub/4", "snapshots/s1.json", "HEAD" })
        {
            await storage.UploadAsync(local, k);
        }

        var all = await ToListAsync(storage.ListAsync(""));
        var chunks = await ToListAsync(storage.ListAsync("chunks/ab/"));
        var partial = await ToListAsync(storage.ListAsync("chunks/a"));
        var none = await ToListAsync(storage.ListAsync("zzz/"));

        all.Select(e => e.Path).Should().BeEquivalentTo("chunks/aa/1", "chunks/aa/2", "chunks/ab/3", "chunks/ab/sub/4", "snapshots/s1.json", "HEAD");
        all.Should().OnlyContain(e => e.Size == 5 && e.LastModifiedUtc != null);
        chunks.Select(e => e.Path).Should().BeEquivalentTo("chunks/ab/3", "chunks/ab/sub/4");
        partial.Should().HaveCount(4);
        none.Should().BeEmpty();
    }

    [Fact]
    public async Task Transient503_IsRetriedByReStore()
    {
        var storage = await OpenAsync();
        await storage.UploadAsync(await LocalAsync("t", [1, 2]), "t.bin");
        _fake.FailListsOnce = HttpStatusCode.ServiceUnavailable;

        (await storage.GetSizeAsync("t.bin")).Should().Be(2);

        _logger.Messages.Should().Contain(m => m.Contains("Retrying in"));
    }

    [Fact]
    public async Task NamesWithQuotes_AreEscapedInQueries()
    {
        var storage = await OpenAsync();
        await storage.UploadAsync(await LocalAsync("q", [7]), "it's/o'k.bin");

        _fake.Queries.Should().Contain(q => q.Contains("name='o\\'k.bin'"));
        (await storage.ExistsAsync("it's/o'k.bin")).Should().BeTrue();
    }

    [Fact]
    public void IsTransient_ClassifiesErrors()
    {
        static Google.GoogleApiException Api(HttpStatusCode code, string? reason = null) =>
            new("drive", "msg")
            {
                HttpStatusCode = code,
                Error = reason == null ? null : new Google.Apis.Requests.RequestError { Errors = [new Google.Apis.Requests.SingleError { Reason = reason }] }
            };

        foreach (var code in new[] { HttpStatusCode.RequestTimeout, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError,
                     HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout })
        {
            DriveStorage.IsTransient(Api(code)).Should().BeTrue(code.ToString());
        }

        DriveStorage.IsTransient(Api(HttpStatusCode.Forbidden, "rateLimitExceeded")).Should().BeTrue();
        DriveStorage.IsTransient(Api(HttpStatusCode.Forbidden, "userRateLimitExceeded")).Should().BeTrue();
        DriveStorage.IsTransient(Api(HttpStatusCode.Forbidden, "insufficientPermissions")).Should().BeFalse();
        DriveStorage.IsTransient(Api(HttpStatusCode.Forbidden)).Should().BeFalse();
        DriveStorage.IsTransient(Api(HttpStatusCode.NotFound)).Should().BeFalse();
        DriveStorage.IsTransient(Api(HttpStatusCode.Unauthorized)).Should().BeFalse();

        DriveStorage.IsTransient(new HttpRequestException()).Should().BeTrue();
        DriveStorage.IsTransient(new TimeoutException()).Should().BeTrue();
        DriveStorage.IsTransient(new IOException()).Should().BeTrue();
        DriveStorage.IsTransient(new TaskCanceledException("t", new TimeoutException())).Should().BeTrue();
        DriveStorage.IsTransient(new OperationCanceledException()).Should().BeFalse();
        DriveStorage.IsTransient(new FileNotFoundException()).Should().BeFalse();
        DriveStorage.IsTransient(new DirectoryNotFoundException()).Should().BeFalse();
        DriveStorage.IsTransient(new UnauthorizedAccessException()).Should().BeFalse();
        DriveStorage.IsTransient(new InvalidOperationException()).Should().BeFalse();
    }

    [Theory]
    [InlineData(1, 0.5, 1000)]
    [InlineData(2, 0.5, 2000)]
    [InlineData(3, 0.0, 3000)]
    [InlineData(3, 1.0, 5000)]
    [InlineData(10, 0.5, 30000)]
    [InlineData(0, 0.5, 1000)]
    public void ComputeRetryDelay_IsExponential_CappedAndJittered(int attempt, double jitter, double expectedMs)
    {
        DriveStorage.ComputeRetryDelay(attempt, jitter).TotalMilliseconds.Should().BeApproximately(expectedMs, 0.001);
    }

    [Fact]
    public void ComputeRetryDelay_ClampsJitterIntoRange()
    {
        DriveStorage.ComputeRetryDelay(1, -5).TotalMilliseconds.Should().BeApproximately(750, 0.001);
        DriveStorage.ComputeRetryDelay(1, 5).TotalMilliseconds.Should().BeApproximately(1250, 0.001);
    }

    [Fact]
    public void ThrowIfUploadFailed_ThrowsOnlyForFailedStatus()
    {
        var failed = new Moq.Mock<Google.Apis.Upload.IUploadProgress>();
        failed.SetupGet(p => p.Status).Returns(Google.Apis.Upload.UploadStatus.Failed);
        failed.SetupGet(p => p.Exception).Returns(new TimeoutException("boom"));
        var failedNoException = new Moq.Mock<Google.Apis.Upload.IUploadProgress>();
        failedNoException.SetupGet(p => p.Status).Returns(Google.Apis.Upload.UploadStatus.Failed);
        var ok = new Moq.Mock<Google.Apis.Upload.IUploadProgress>();
        ok.SetupGet(p => p.Status).Returns(Google.Apis.Upload.UploadStatus.Completed);

        DriveStorage.ThrowIfUploadFailed(ok.Object).Should().BeSameAs(ok.Object);
        new Action(() => DriveStorage.ThrowIfUploadFailed(failed.Object)).Should().Throw<TimeoutException>();
        new Action(() => DriveStorage.ThrowIfUploadFailed(failedNoException.Object)).Should().Throw<IOException>();
    }

    [Fact]
    public void ThrowIfDownloadFailed_ThrowsOnlyWhenNotCompleted()
    {
        var failed = new Moq.Mock<Google.Apis.Download.IDownloadProgress>();
        failed.SetupGet(p => p.Status).Returns(Google.Apis.Download.DownloadStatus.Failed);
        var ok = new Moq.Mock<Google.Apis.Download.IDownloadProgress>();
        ok.SetupGet(p => p.Status).Returns(Google.Apis.Download.DownloadStatus.Completed);

        DriveStorage.ThrowIfDownloadFailed(ok.Object);
        new Action(() => DriveStorage.ThrowIfDownloadFailed(failed.Object)).Should().Throw<IOException>();
    }

    [Fact]
    public async Task Initialize_WithoutClientCredentials_NamesTheMissingOptions()
    {
        using var storage = new DriveStorage(_logger);

        var act = () => storage.InitializeAsync(new Dictionary<string, string>());

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("client_id").And.Contain("client_secret");
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var storage = await OpenAsync();

        storage.Dispose();
        storage.Dispose();
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    internal sealed class DriveItem
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public string? Parent { get; init; }
        public bool IsFolder { get; init; }
        public byte[] Content { get; set; } = [];
        public DateTime Modified { get; set; } = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    }

    /// <summary>In-memory Drive v3 subset: files.list, files.create (metadata and resumable media), files.update, files.get(alt=media), files.delete.</summary>
    internal sealed class FakeDriveHandler : HttpMessageHandler
    {
        private int _nextId = 1;
        private readonly Dictionary<string, (string? FileId, JsonNode? Meta, MemoryStream Data)> _uploads = [];
        public Dictionary<string, DriveItem> Items { get; } = [];
        public List<string> Queries { get; } = [];
        public int PageSize { get; set; } = 1000;
        public HttpStatusCode? FailUploads { get; set; }
        public HttpStatusCode? FailLists { get; set; }
        public HttpStatusCode? FailListsOnce { get; set; }
        public HttpStatusCode DeleteReturns { get; set; } = HttpStatusCode.NoContent;

        public void AddFile(string parent, string name, byte[] content)
        {
            var id = "id" + _nextId++;
            Items[id] = new DriveItem { Id = id, Name = name, Parent = parent, Content = content };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            if (path == "/drive/v3/files" && request.Method == HttpMethod.Get)
            {
                return List(query);
            }

            if (path == "/drive/v3/files" && request.Method == HttpMethod.Post)
            {
                var meta = JsonNode.Parse(body)!;
                var item = Create(meta);
                return Json(HttpStatusCode.OK, Describe(item));
            }

            if (path == "/upload/drive/v3/files" || path.StartsWith("/upload/drive/v3/files/", StringComparison.Ordinal))
            {
                return Upload(request, path, query, body);
            }

            var match = Regex.Match(path, "^/drive/v3/files/([^/]+)$");
            if (match.Success)
            {
                var id = match.Groups[1].Value;
                if (request.Method == HttpMethod.Delete)
                {
                    if (DeleteReturns == HttpStatusCode.NoContent)
                    {
                        Items.Remove(id);
                    }

                    return new HttpResponseMessage(DeleteReturns)
                    {
                        Content = DeleteReturns == HttpStatusCode.NoContent ? null : new StringContent(ErrorJson((int)DeleteReturns), Encoding.UTF8, "application/json")
                    };
                }

                if (request.Method == HttpMethod.Get && Items.TryGetValue(id, out var file))
                {
                    return query["alt"] == "media" ? Media(request, file) : Json(HttpStatusCode.OK, Describe(file));
                }
            }

            return Json(HttpStatusCode.NotFound, ErrorJson(404));
        }

        private HttpResponseMessage List(System.Collections.Specialized.NameValueCollection query)
        {
            var q = query["q"] ?? "";
            Queries.Add(q);
            if (FailListsOnce is { } once)
            {
                FailListsOnce = null;
                return Json(once, ErrorJson((int)once));
            }

            if (FailLists is { } fail)
            {
                return Json(fail, ErrorJson((int)fail));
            }

            var parent = Regex.Match(q, "'([^']+)' in parents").Groups[1].Value;
            var nameMatch = Regex.Match(q, @"name='((?:[^'\\]|\\.)*)'");
            var wantsFolder = q.Contains("mimeType='application/vnd.google-apps.folder'");
            var matching = Items.Values.Where(i => parent.Length == 0 || i.Parent == parent).Where(i => !wantsFolder || i.IsFolder);
            if (nameMatch.Success)
            {
                var name = Regex.Unescape(nameMatch.Groups[1].Value);
                matching = matching.Where(i => i.Name == name);
            }
            else if (parent.Length == 0)
            {
                matching = matching.Where(_ => false);
            }

            var all = matching.OrderByDescending(i => i.Modified).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
            var start = int.TryParse(query["pageToken"], out var offset) ? offset : 0;
            var page = all.Skip(start).Take(PageSize).ToList();
            var next = start + page.Count < all.Count ? $",\"nextPageToken\":\"{start + page.Count}\"" : "";
            return Json(HttpStatusCode.OK, "{\"files\":[" + string.Join(",", page.Select(Describe)) + "]" + next + "}");
        }

        private DriveItem Create(JsonNode meta)
        {
            var id = "id" + _nextId++;
            var item = new DriveItem
            {
                Id = id,
                Name = meta["name"]!.ToString(),
                Parent = meta["parents"]?[0]?.ToString(),
                IsFolder = meta["mimeType"]?.ToString() == "application/vnd.google-apps.folder"
            };
            Items[id] = item;
            return item;
        }

        private HttpResponseMessage Upload(HttpRequestMessage request, string path, System.Collections.Specialized.NameValueCollection query, byte[] body)
        {
            if (FailUploads is { } fail)
            {
                return Json(fail, ErrorJson((int)fail));
            }

            var existingId = path.Length > "/upload/drive/v3/files/".Length ? path["/upload/drive/v3/files/".Length..] : null;
            var uploadId = query["upload_id"];
            if (uploadId == null)
            {
                // Initiation request: metadata only; the response tells the client where to send the media.
                var id = "up" + _nextId++;
                _uploads[id] = (existingId, body.Length == 0 ? null : JsonNode.Parse(body), new MemoryStream());
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri($"https://www.googleapis.com{path}?uploadType=resumable&upload_id={id}");
                return response;
            }

            var (fileId, meta, data) = _uploads[uploadId];
            data.Write(body);
            var range = request.Content!.Headers.ContentRange;
            var total = range?.Length;
            if (total != null && data.Length < total)
            {
                var incomplete = new HttpResponseMessage((HttpStatusCode)308);
                incomplete.Headers.Add("Range", $"bytes=0-{data.Length - 1}");
                return incomplete;
            }

            DriveItem item;
            if (fileId != null)
            {
                item = Items[fileId];
                if (meta?["name"] is { } renamed) item.Name = renamed.ToString();
                item.Modified = item.Modified.AddMinutes(1);
            }
            else
            {
                item = Create(meta!);
            }

            item.Content = data.ToArray();
            _uploads.Remove(uploadId);
            return Json(HttpStatusCode.OK, Describe(item));
        }

        private static HttpResponseMessage Media(HttpRequestMessage request, DriveItem file)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range?.From is not { } from)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Content) };
            }

            var to = Math.Min(range.To ?? file.Content.Length - 1, file.Content.Length - 1);
            var slice = file.Content[(int)from..(int)(to + 1)];
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, file.Content.Length);
            return response;
        }

        private static string Describe(DriveItem i) =>
            "{\"id\":\"" + i.Id + "\",\"name\":" + JsonValue.Create(i.Name)!.ToJsonString() +
            ",\"mimeType\":\"" + (i.IsFolder ? "application/vnd.google-apps.folder" : "application/octet-stream") +
            "\",\"size\":\"" + i.Content.Length + "\",\"modifiedTime\":\"" + i.Modified.ToString("yyyy-MM-ddTHH:mm:ss.000Z") + "\"}";

        private static string ErrorJson(int code) =>
            "{\"error\":{\"code\":" + code + ",\"message\":\"fake error\",\"errors\":[{\"reason\":\"fake\",\"message\":\"fake error\"}]}}";

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

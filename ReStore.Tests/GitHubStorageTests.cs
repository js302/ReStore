using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.github;

namespace ReStore.Tests;

/// <summary>
/// Drives <see cref="GitHubStorage"/> through Octokit against an in-memory fake of the GitHub REST API
/// (repository, git tree and contents endpoints), injected as the HTTP handler.
/// </summary>
public sealed class GitHubStorageTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "ReStoreGitHub_" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHubHandler _fake = new();
    private readonly TestLogger _logger = new();
    private readonly List<IDisposable> _disposables = [];

    public GitHubStorageTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_scratch, true); } catch { }
    }

    private async Task<GitHubStorage> OpenAsync(string prefix = "")
    {
        var storage = new GitHubStorage(_logger, _fake);
        _disposables.Add(storage);
        await storage.InitializeAsync(new Dictionary<string, string>
        {
            ["token"] = "ghp_test", ["owner"] = "octo", ["repo"] = "backup", ["prefix"] = prefix
        });
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
    public async Task Initialize_VerifiesTheRepository_WithTheToken()
    {
        await OpenAsync();

        _fake.Requests.Should().Contain(r => r.Method == "GET" && r.Path == "/repos/octo/backup" && r.Authorization!.Contains("ghp_test"));
        _logger.Messages.Should().Contain(m => m.Contains("octo/backup"));
    }

    [Fact]
    public async Task Initialize_WhenTheRepositoryIsMissing_ThrowsInitializationError()
    {
        _fake.RepositoryExists = false;
        using var storage = new GitHubStorage(_logger, _fake);

        var act = () => storage.InitializeAsync(new Dictionary<string, string> { ["token"] = "t", ["owner"] = "o", ["repo"] = "r" });

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("GitHub");
    }

    [Fact]
    public async Task Upload_CreatesThenUpdates_AndRoundTripsThroughDownload()
    {
        var storage = await OpenAsync("repo");

        await storage.UploadAsync(await LocalAsync("one", "first"u8.ToArray()), "chunks/aa/1");
        await storage.UploadAsync(await LocalAsync("two", "second!"u8.ToArray()), "chunks/aa/1");
        await storage.DownloadAsync("chunks/aa/1", Path.Combine(_scratch, "out", "down"));

        _fake.Files.Keys.Should().Equal("repo/chunks/aa/1");
        (await File.ReadAllBytesAsync(Path.Combine(_scratch, "out", "down"))).Should().Equal("second!"u8.ToArray());
        _fake.Requests.Count(r => r.Method == "PUT").Should().Be(2);
        _fake.Requests.Last(r => r.Method == "PUT").Body!["sha"].Should().NotBeNull("an update must carry the existing blob sha");
    }

    [Fact]
    public async Task UploadStream_BuffersThroughTheFileOverload()
    {
        var storage = await OpenAsync();
        using var stream = new MemoryStream("skip|payload"u8.ToArray()) { Position = 5 };

        await storage.UploadAsync(stream, "s/stream.bin");

        _fake.Files["s/stream.bin"].Should().Equal("payload"u8.ToArray());
    }

    [Fact]
    public async Task Upload_FileOverTheApiLimit_IsRejectedBeforeAnyRequest()
    {
        var storage = await OpenAsync();
        var big = Path.Combine(_scratch, "big.bin");
        await using (var fs = new FileStream(big, FileMode.Create))
        {
            fs.SetLength(100L * 1024 * 1024 + 1);
        }

        var before = _fake.Requests.Count;
        var act = () => storage.UploadAsync(big, "big.bin");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("100MB");
        _fake.Requests.Count.Should().Be(before);
    }

    [Fact]
    public async Task Upload_FailureFromTheApi_IsWrappedWithoutLeakingTheServerMessage()
    {
        var storage = await OpenAsync();
        _fake.FailWrites = true;

        var local = await LocalAsync("x", [1]);
        var act = () => storage.UploadAsync(local, "x");

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("GitHub");
        _logger.Messages.Should().Contain(m => m.Contains("Failed to upload"));
    }

    [Fact]
    public async Task Download_Missing_MapsToStorageNotFound()
    {
        var storage = await OpenAsync();

        var act = () => storage.DownloadAsync("nope", Path.Combine(_scratch, "nope"));

        await act.Should().ThrowAsync<StorageNotFoundException>();
        File.Exists(Path.Combine(_scratch, "nope")).Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAndGetSize_UseTheCachedTree()
    {
        var storage = await OpenAsync();
        _fake.Files["a/b.bin"] = new byte[77];

        (await storage.ExistsAsync("a/b.bin")).Should().BeTrue();
        (await storage.ExistsAsync("a/zzz")).Should().BeFalse();
        (await storage.GetSizeAsync("a/b.bin")).Should().Be(77);
        (await storage.GetSizeAsync("a/zzz")).Should().BeNull();

        _fake.Requests.Count(r => r.Path.Contains("/git/trees/")).Should().Be(1, "the tree is cached between calls");
    }

    [Fact]
    public async Task ExistsAndGetSize_FallBackToTheContentsApi_WhenTheTreeIsTruncated()
    {
        var storage = await OpenAsync();
        _fake.TreeTruncated = true;
        _fake.Files["a/b.bin"] = new byte[9];

        (await storage.ExistsAsync("a/b.bin")).Should().BeTrue();
        (await storage.ExistsAsync("a/none")).Should().BeFalse();
        (await storage.GetSizeAsync("a/b.bin")).Should().Be(9);
        (await storage.GetSizeAsync("a/none")).Should().BeNull();
    }

    [Fact]
    public async Task EmptyRepository_ListsNothing_AndExistsIsFalse()
    {
        var storage = await OpenAsync();
        _fake.RepositoryIsEmpty = true;

        (await ToListAsync(storage.ListAsync(""))).Should().BeEmpty();
        (await storage.ExistsAsync("x")).Should().BeFalse();
    }

    [Fact]
    public async Task List_FiltersByPrefix_AndStripsTheRepositoryPrefix()
    {
        var storage = await OpenAsync("repo");
        foreach (var k in new[] { "chunks/aa/1", "chunks/ab/2", "snapshots/s1.json" })
        {
            _fake.Files["repo/" + k] = new byte[4];
        }

        _fake.Files["other/skip"] = [1];

        var all = await ToListAsync(storage.ListAsync(""));
        var chunks = await ToListAsync(storage.ListAsync("chunks/a"));

        all.Select(e => e.Path).Should().BeEquivalentTo("chunks/aa/1", "chunks/ab/2", "snapshots/s1.json");
        all.Should().OnlyContain(e => e.Size == 4);
        chunks.Should().HaveCount(2);
    }

    [Fact]
    public async Task List_WithATruncatedTree_WalksDirectoriesThroughTheContentsApi()
    {
        var storage = await OpenAsync();
        _fake.TreeTruncated = true;
        foreach (var k in new[] { "chunks/aa/1", "chunks/aa/2", "chunks/ab/3", "HEAD" })
        {
            _fake.Files[k] = new byte[2];
        }

        var all = await ToListAsync(storage.ListAsync(""));
        var some = await ToListAsync(storage.ListAsync("chunks/aa/"));

        all.Select(e => e.Path).Should().BeEquivalentTo("chunks/aa/1", "chunks/aa/2", "chunks/ab/3", "HEAD");
        some.Select(e => e.Path).Should().BeEquivalentTo("chunks/aa/1", "chunks/aa/2");
    }

    [Fact]
    public async Task List_Cancelled_Throws()
    {
        var storage = await OpenAsync();
        _fake.Files["a"] = [1];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => ToListAsync(storage.ListAsync("", cts.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Delete_RemovesTheFile_UpdatesTheCache_AndIgnoresMissing()
    {
        var storage = await OpenAsync();
        _fake.Files["del.bin"] = [1, 2, 3];

        await storage.DeleteAsync("del.bin");
        await storage.DeleteAsync("del.bin");

        _fake.Files.Should().BeEmpty();
        (await storage.ExistsAsync("del.bin")).Should().BeFalse();
        _fake.Requests.Count(r => r.Method == "DELETE").Should().Be(1);
    }

    [Fact]
    public async Task PrimaryRateLimit_IsWaitedOutAndRetried()
    {
        var storage = await OpenAsync();
        _fake.Files["f"] = [1];
        _fake.RateLimitNext = RateLimitKind.Primary;

        var exists = await storage.ExistsAsync("f");

        exists.Should().BeTrue();
        _logger.Messages.Should().Contain(m => m.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SecondaryRateLimit_WithRetryAfter_IsWaitedOutAndRetried()
    {
        var storage = await OpenAsync();
        _fake.Files["f"] = [1];
        _fake.RateLimitNext = RateLimitKind.Secondary;

        (await storage.ExistsAsync("f")).Should().BeTrue();
    }

    [Fact]
    public async Task RateLimit_WithAResetFarInTheFuture_IsNotWaitedFor()
    {
        var storage = await OpenAsync();
        _fake.Files["f"] = [1];
        _fake.RateLimitNext = RateLimitKind.PrimaryFarFuture;

        var act = () => storage.ExistsAsync("f");

        await act.Should().ThrowAsync<Octokit.RateLimitExceededException>();
    }

    [Fact]
    public void Classify_DistinguishesEmptyTruncatedAndAvailable()
    {
        GitHubStorage.Classify(null).Should().Be(GitHubStorage.TreeAvailability.Empty);
        GitHubStorage.Classify(new Octokit.TreeResponse("sha", "url", [], true)).Should().Be(GitHubStorage.TreeAvailability.Truncated);
        GitHubStorage.Classify(new Octokit.TreeResponse("sha", "url", [], false)).Should().Be(GitHubStorage.TreeAvailability.Available);
    }

    [Fact]
    public void GetRateLimitDelay_IgnoresErrorsThatAreNotRateLimits()
    {
        GitHubStorage.GetRateLimitDelay(new InvalidOperationException()).Should().BeNull();
        GitHubStorage.GetRateLimitDelay(new Octokit.NotFoundException("x", HttpStatusCode.NotFound)).Should().BeNull();
    }

    internal enum RateLimitKind { None, Primary, PrimaryFarFuture, Secondary }

    internal sealed record RecordedRequest(string Method, string Path, string? Authorization, JsonNode? Body);

    /// <summary>In-memory model of the GitHub endpoints ReStore uses: repo, recursive tree, contents (get/put/delete).</summary>
    internal sealed class FakeGitHubHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<RecordedRequest> Requests { get; } = [];
        public bool RepositoryExists { get; set; } = true;
        public bool RepositoryIsEmpty { get; set; }
        public bool TreeTruncated { get; set; }
        public bool FailWrites { get; set; }
        public RateLimitKind RateLimitNext { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            JsonNode? body = null;
            if (request.Content != null)
            {
                var text = await request.Content.ReadAsStringAsync(cancellationToken);
                body = text.Length == 0 ? null : JsonNode.Parse(text);
            }

            Requests.Add(new RecordedRequest(request.Method.Method, path, request.Headers.Authorization?.ToString(), body));

            if (RateLimitNext != RateLimitKind.None && path.Contains("/git/trees/") | path.Contains("/contents/"))
            {
                var kind = RateLimitNext;
                RateLimitNext = RateLimitKind.None;
                return RateLimited(kind);
            }

            return Handle(request, path, body);
        }

        private HttpResponseMessage Handle(HttpRequestMessage request, string path, JsonNode? body)
        {
            const string repo = "/repos/octo/backup";
            if (path == repo)
            {
                return RepositoryExists
                    ? Json(200, """{"id":1,"name":"backup","full_name":"octo/backup","default_branch":"main","private":true}""")
                    : Json(404, """{"message":"Not Found"}""");
            }

            if (path == repo + "/git/trees/main")
            {
                if (RepositoryIsEmpty)
                {
                    return Json(409, """{"message":"Git Repository is empty."}""");
                }

                var entries = string.Join(",", Files.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv =>
                    $$"""{"path":"{{kv.Key}}","mode":"100644","type":"blob","sha":"{{ShaOf(kv.Value)}}","size":{{kv.Value.Length}}}"""));
                return Json(200, $$"""{"sha":"treesha","url":"u","tree":[{{entries}}],"truncated":{{(TreeTruncated ? "true" : "false")}}}""");
            }

            if (path.StartsWith(repo + "/contents", StringComparison.Ordinal))
            {
                var file = path.Length > (repo + "/contents/").Length ? path[(repo + "/contents/").Length..] : "";
                return request.Method.Method switch
                {
                    "GET" => GetContents(request, file),
                    "PUT" => Put(file, body!),
                    "DELETE" => Delete(file, body!),
                    _ => Json(405, """{"message":"method"}""")
                };
            }

            return Json(404, """{"message":"Not Found"}""");
        }

        private HttpResponseMessage GetContents(HttpRequestMessage request, string file)
        {
            var wantsRaw = request.Headers.Accept.Any(a => a.MediaType!.Contains("raw", StringComparison.Ordinal));
            if (Files.TryGetValue(file, out var bytes))
            {
                return wantsRaw
                    ? Raw(bytes)
                    : Json(200, Item(file, bytes.Length, ShaOf(bytes), "file"));
            }

            var prefix = file.Length == 0 ? "" : file + "/";
            var below = Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (below.Count == 0)
            {
                return Json(404, """{"message":"Not Found"}""");
            }

            var children = below.Select(k => k[prefix.Length..].Split('/')[0]).Distinct(StringComparer.Ordinal).Select(name =>
                Files.TryGetValue(prefix + name, out var b)
                    ? Item(prefix + name, b.Length, ShaOf(b), "file")
                    : Item(prefix + name, 0, "treesha", "dir"));
            return Json(200, "[" + string.Join(",", children) + "]");
        }

        private static HttpResponseMessage Raw(byte[] bytes)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        private HttpResponseMessage Put(string file, JsonNode body)
        {
            if (FailWrites)
            {
                return Json(422, """{"message":"Validation Failed secret-server-detail"}""");
            }

            var exists = Files.ContainsKey(file);
            var sha = body["sha"]?.ToString();
            if (exists != (sha != null))
            {
                return Json(422, """{"message":"sha mismatch"}""");
            }

            var bytes = Convert.FromBase64String(body["content"]!.ToString());
            Files[file] = bytes;
            return Json(exists ? 200 : 201, "{\"content\":" + Item(file, bytes.Length, ShaOf(bytes), "file") + ",\"commit\":{\"sha\":\"c1\"}}");
        }

        private HttpResponseMessage Delete(string file, JsonNode body)
        {
            if (!Files.TryGetValue(file, out var bytes) || body["sha"]!.ToString() != ShaOf(bytes))
            {
                return Json(404, """{"message":"Not Found"}""");
            }

            Files.Remove(file);
            return Json(200, """{"content":null,"commit":{"sha":"c2"}}""");
        }

        private static string Item(string path, int size, string sha, string type) =>
            $$"""{"type":"{{type}}","name":"{{System.IO.Path.GetFileName(path)}}","path":"{{path}}","sha":"{{sha}}","size":{{size}},"url":"u","html_url":"u","git_url":"u","download_url":null}""";

        private static string ShaOf(byte[] bytes) =>
            Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).ToLowerInvariant();

        private static HttpResponseMessage RateLimited(RateLimitKind kind)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (kind == RateLimitKind.Secondary)
            {
                response.Headers.Add("Retry-After", "0");
                response.Content = new StringContent("""{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""", Encoding.UTF8, "application/json");
                return response;
            }

            var reset = kind == RateLimitKind.PrimaryFarFuture
                ? DateTimeOffset.UtcNow.AddHours(2)
                : DateTimeOffset.UtcNow.AddSeconds(-5);
            response.Headers.Add("X-RateLimit-Limit", "5000");
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
            response.Content = new StringContent("""{"message":"API rate limit exceeded for user."}""", Encoding.UTF8, "application/json");
            return response;
        }

        private static HttpResponseMessage Json(int status, string json) =>
            new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

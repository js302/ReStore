using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.storage;
using ReStore.Core.src.storage.dropbox;

namespace ReStore.Tests;

/// <summary>
/// Drives <see cref="DropboxStorage"/> through the real Dropbox SDK against an in-memory fake of the Dropbox HTTP API
/// (injected through the SDK's HttpClient), so request construction and error mapping are exercised without the network.
/// </summary>
public sealed class DropboxStorageTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "ReStoreDropbox_" + Guid.NewGuid().ToString("N"));
    private readonly FakeDropboxHandler _fake = new();
    private readonly List<IDisposable> _disposables = [];

    public DropboxStorageTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        try { Directory.Delete(_scratch, true); } catch { }
    }

    private async Task<DropboxStorage> OpenAsync(string prefix = "", Dictionary<string, string>? options = null)
    {
        var http = new HttpClient(_fake);
        var storage = new DropboxStorage(new TestLogger(), http);
        _disposables.Add(storage);
        _disposables.Add(http);
        var all = options ?? new Dictionary<string, string> { ["accessToken"] = "token" };
        all["prefix"] = prefix;
        await storage.InitializeAsync(all);
        return storage;
    }

    private async Task<string> LocalAsync(string name, byte[] content)
    {
        var path = Path.Combine(_scratch, name);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    [Fact]
    public async Task Initialize_WithAccessToken_SendsItAsBearer()
    {
        await OpenAsync();

        _fake.Requests.Should().Contain(r => r.Path == "/2/users/get_current_account" && r.Authorization == "Bearer token");
    }

    [Fact]
    public async Task Initialize_WithRefreshToken_ExchangesItForAnAccessToken()
    {
        await OpenAsync(options: new() { ["refreshToken"] = "r", ["appKey"] = "k", ["appSecret"] = "s" });

        _fake.Requests.Select(r => r.Path).Should().Contain("/oauth2/token");
        _fake.Requests.Last().Authorization.Should().Be("Bearer refreshed-access-token");
    }

    [Fact]
    public async Task Initialize_WhenTheAccountCallFails_ThrowsInitializationError()
    {
        _fake.FailAccount = true;
        var http = new HttpClient(_fake);
        using var storage = new DropboxStorage(new TestLogger(), http);

        var act = () => storage.InitializeAsync(new Dictionary<string, string> { ["accessToken"] = "bad" });

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Dropbox");
    }

    [Fact]
    public async Task Upload_SmallFile_UsesOneOverwritingRequest_UnderTheQualifiedPath()
    {
        var storage = await OpenAsync("repo");

        await storage.UploadAsync(await LocalAsync("a.bin", "hello"u8.ToArray()), "chunks/aa/1");

        _fake.Files.Should().ContainKey("/repo/chunks/aa/1");
        _fake.Files["/repo/chunks/aa/1"].Should().Equal("hello"u8.ToArray());
        var upload = _fake.Requests.Single(r => r.Path == "/2/files/upload");
        upload.Arg!["mode"]!.ToString().Should().Contain("overwrite");
    }

    [Fact]
    public async Task Upload_Stream_NormalisesBackslashesAndLeadingSlash()
    {
        var storage = await OpenAsync();
        using var stream = new MemoryStream("data"u8.ToArray());

        await storage.UploadAsync(stream, "a\\b\\c.bin");

        _fake.Files.Keys.Should().Equal("/a/b/c.bin");
    }

    [Fact]
    public async Task Upload_LargePayload_UsesAnUploadSession_AndReassemblesInOrder()
    {
        var storage = await OpenAsync();
        var payload = new byte[8 * 1024 * 1024 * 2 + 123];
        new Random(42).NextBytes(payload);

        await storage.UploadAsync(await LocalAsync("big.bin", payload), "big.bin");

        _fake.Requests.Select(r => r.Path).Should().ContainInOrder(
            "/2/files/upload_session/start", "/2/files/upload_session/append_v2", "/2/files/upload_session/finish");
        _fake.Requests.Should().NotContain(r => r.Path == "/2/files/upload");
        _fake.Files["/big.bin"].Should().Equal(payload);
    }

    [Fact]
    public async Task Upload_PayloadOfExactlyOneChunk_FinishesTheSessionWithAnEmptyTail()
    {
        var storage = await OpenAsync();
        var payload = new byte[8 * 1024 * 1024];
        new Random(7).NextBytes(payload);

        await storage.UploadAsync(await LocalAsync("exact.bin", payload), "exact.bin");

        _fake.Files["/exact.bin"].Should().Equal(payload);
    }

    [Fact]
    public async Task Upload_Cancelled_ThrowsBeforeAnyRequest()
    {
        var storage = await OpenAsync();
        var before = _fake.Requests.Count;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => storage.UploadAsync(new MemoryStream([1]), "x", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _fake.Requests.Count.Should().Be(before);
    }

    [Fact]
    public async Task Download_WritesTheContent_AndMapsNotFound()
    {
        var storage = await OpenAsync();
        _fake.Files["/d/file.bin"] = "downloaded"u8.ToArray();

        await storage.DownloadAsync("d/file.bin", Path.Combine(_scratch, "out", "file.bin"));
        var missing = () => storage.DownloadAsync("d/none.bin", Path.Combine(_scratch, "out", "none.bin"));

        (await File.ReadAllBytesAsync(Path.Combine(_scratch, "out", "file.bin"))).Should().Equal("downloaded"u8.ToArray());
        await missing.Should().ThrowAsync<StorageNotFoundException>();
        File.Exists(Path.Combine(_scratch, "out", "none.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task ExistsAndGetSize_ReportPresence_AndNullForMissing()
    {
        var storage = await OpenAsync();
        _fake.Files["/x/present"] = new byte[321];

        (await storage.ExistsAsync("x/present")).Should().BeTrue();
        (await storage.ExistsAsync("x/absent")).Should().BeFalse();
        (await storage.GetSizeAsync("x/present")).Should().Be(321);
        (await storage.GetSizeAsync("x/absent")).Should().BeNull();
    }

    [Fact]
    public async Task GetSize_OfAFolder_IsNull()
    {
        var storage = await OpenAsync();
        _fake.Files["/folder/inner"] = [1];

        (await storage.GetSizeAsync("folder")).Should().BeNull();
    }

    [Fact]
    public async Task Exists_PropagatesOtherApiErrors()
    {
        var storage = await OpenAsync();
        _fake.MetadataError = "path/malformed_path/";

        var act = () => storage.ExistsAsync("x");

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Delete_RemovesTheFile_AndIgnoresMissing()
    {
        var storage = await OpenAsync();
        _fake.Files["/del"] = [1];

        await storage.DeleteAsync("del");
        await storage.DeleteAsync("del");

        _fake.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task List_ReturnsFilesUnderThePrefix_AcrossPages_StrippingTheRepositoryPrefix()
    {
        var storage = await OpenAsync("repo");
        _fake.PageSize = 2;
        foreach (var k in new[] { "chunks/aa/1", "chunks/aa/2", "chunks/ab/3", "snapshots/s1.json", "HEAD" })
        {
            _fake.Files["/repo/" + k] = new byte[3];
        }

        _fake.Files["/other/skip"] = [1];

        var all = await ToListAsync(storage.ListAsync(""));
        var chunks = await ToListAsync(storage.ListAsync("chunks/a"));

        all.Select(e => e.Path).Should().BeEquivalentTo("chunks/aa/1", "chunks/aa/2", "chunks/ab/3", "snapshots/s1.json", "HEAD");
        all.Should().OnlyContain(e => e.Size == 3 && e.LastModifiedUtc != null);
        chunks.Should().HaveCount(3);
        _fake.Requests.Should().Contain(r => r.Path == "/2/files/list_folder/continue");
    }

    [Fact]
    public async Task List_OfAFolderThatDoesNotExist_IsEmpty()
    {
        var storage = await OpenAsync();

        (await ToListAsync(storage.ListAsync("nowhere/"))).Should().BeEmpty();
    }

    [Fact]
    public async Task ShareLink_CreatesAPublicViewerLink_AndWarnsAboutIgnoredExpiry()
    {
        var logger = new TestLogger();
        var http = new HttpClient(_fake);
        using var storage = new DropboxStorage(logger, http);
        await storage.InitializeAsync(new Dictionary<string, string> { ["accessToken"] = "t" });
        _fake.Files["/share/me"] = [1];

        var url = await storage.GenerateShareLinkAsync("share/me", TimeSpan.FromHours(2));

        url.Should().Be("https://www.dropbox.com/s/fake/share/me?dl=0");
        logger.Messages.Should().Contain(m => m.Contains("expiration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ShareLink_WhenOneAlreadyExists_ReturnsTheExistingLink()
    {
        var storage = await OpenAsync();
        _fake.LinkAlreadyExists = true;
        _fake.Files["/share/me"] = [1];

        var url = await storage.GenerateShareLinkAsync("share/me", TimeSpan.Zero);

        url.Should().Be("https://www.dropbox.com/s/existing/share/me?dl=0");
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var storage = await OpenAsync();

        storage.Dispose();
        storage.Dispose();
    }

    private static async Task<List<StorageEntry>> ToListAsync(IAsyncEnumerable<StorageEntry> source)
    {
        var list = new List<StorageEntry>();
        await foreach (var e in source) list.Add(e);
        return list;
    }

    internal sealed record RecordedRequest(string Path, string? Authorization, JsonNode? Arg);

    /// <summary>Minimal in-memory model of the Dropbox v2 endpoints ReStore uses.</summary>
    internal sealed class FakeDropboxHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, MemoryStream> _sessions = [];
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<RecordedRequest> Requests { get; } = [];
        public bool FailAccount { get; set; }
        public bool LinkAlreadyExists { get; set; }
        public string? MetadataError { get; set; }
        public int PageSize { get; set; } = 1000;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            JsonNode? arg = null;
            if (request.Headers.TryGetValues("Dropbox-API-Arg", out var argHeader))
            {
                arg = JsonNode.Parse(argHeader.First());
            }
            else if (body.Length > 0 && request.Content!.Headers.ContentType?.MediaType is "application/json")
            {
                arg = JsonNode.Parse(body);
            }

            Requests.Add(new RecordedRequest(path, request.Headers.Authorization?.ToString(), arg));
            return Handle(path, arg, body, request);
        }

        private HttpResponseMessage Handle(string path, JsonNode? arg, byte[] body, HttpRequestMessage request)
        {
            switch (path)
            {
                case "/oauth2/token":
                    return Json("""{"access_token":"refreshed-access-token","token_type":"bearer","expires_in":14400}""");
                case "/2/users/get_current_account":
                    return FailAccount ? Error(401, "invalid_access_token/", "{\".tag\":\"invalid_access_token\"}") : Json(Account);
                case "/2/files/upload":
                    Files[arg!["path"]!.ToString()] = body;
                    return Json(FileMeta(arg["path"]!.ToString(), body.Length));
                case "/2/files/upload_session/start":
                    var id = "session-" + _sessions.Count;
                    _sessions[id] = new MemoryStream();
                    _sessions[id].Write(body);
                    return Json($$"""{"session_id":"{{id}}"}""");
                case "/2/files/upload_session/append_v2":
                    var append = _sessions[arg!["cursor"]!["session_id"]!.ToString()];
                    append.Length.Should().Be(arg["cursor"]!["offset"]!.GetValue<long>(), "append offsets must be contiguous");
                    append.Write(body);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") };
                case "/2/files/upload_session/finish":
                    var finish = _sessions[arg!["cursor"]!["session_id"]!.ToString()];
                    finish.Length.Should().Be(arg["cursor"]!["offset"]!.GetValue<long>());
                    finish.Write(body);
                    var target = arg["commit"]!["path"]!.ToString();
                    Files[target] = finish.ToArray();
                    return Json(FileMeta(target, (int)finish.Length));
                case "/2/files/download":
                    var dlPath = arg!["path"]!.ToString();
                    if (!Files.TryGetValue(dlPath, out var content))
                    {
                        return Error(409, "path/not_found/", """{".tag":"path","path":{".tag":"not_found"}}""");
                    }

                    var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
                    ok.Headers.Add("Dropbox-API-Result", FileMeta(dlPath, content.Length));
                    return ok;
                case "/2/files/get_metadata":
                    if (MetadataError != null)
                    {
                        return Error(409, MetadataError, """{".tag":"path","path":{".tag":"malformed_path"}}""");
                    }

                    var mdPath = arg!["path"]!.ToString();
                    if (Files.TryGetValue(mdPath, out var md))
                    {
                        return Json(FileMeta(mdPath, md.Length));
                    }

                    return Files.Keys.Any(k => k.StartsWith(mdPath + "/", StringComparison.Ordinal))
                        ? Json($$"""{".tag":"folder","name":"f","path_lower":"{{mdPath.ToLowerInvariant()}}","path_display":"{{mdPath}}","id":"id:f"}""")
                        : Error(409, "path/not_found/", """{".tag":"path","path":{".tag":"not_found"}}""");
                case "/2/files/list_folder":
                    var folder = arg!["path"]!.ToString();
                    if (folder.Length > 0 && KeysUnder(folder).Count == 0)
                    {
                        return Error(409, "path/not_found/", """{".tag":"path","path":{".tag":"not_found"}}""");
                    }

                    return Page(KeysUnder(folder), 0, folder);
                case "/2/files/list_folder/continue":
                    var cursor = arg!["cursor"]!.ToString().Split('|');
                    return Page(KeysUnder(cursor[0]), int.Parse(cursor[1]), cursor[0]);
                case "/2/files/delete_v2":
                    var delPath = arg!["path"]!.ToString();
                    return Files.Remove(delPath)
                        ? Json($$"""{"metadata":{{FileMeta(delPath, 0)}}}""")
                        : Error(409, "path_lookup/not_found/", """{".tag":"path_lookup","path_lookup":{".tag":"not_found"}}""");
                case "/2/sharing/create_shared_link_with_settings":
                    var shared = arg!["path"]!.ToString();
                    return LinkAlreadyExists
                        ? Error(409, "shared_link_already_exists/", """{".tag":"shared_link_already_exists"}""")
                        : Json(LinkJson("https://www.dropbox.com/s/fake" + shared + "?dl=0", shared));
                case "/2/sharing/list_shared_links":
                    var existing = arg!["path"]!.ToString();
                    return Json("{\"links\":[" + LinkJson("https://www.dropbox.com/s/existing" + existing + "?dl=0", existing) + "],\"has_more\":false}");
                default:
                    return Error(400, "unexpected endpoint " + path, null);
            }
        }

        private static string LinkJson(string url, string path) =>
            "{\".tag\":\"file\",\"url\":\"" + url + "\",\"name\":\"x\",\"path_lower\":\"" + path +
            "\",\"link_permissions\":{\"resolved_visibility\":{\".tag\":\"public\"},\"can_revoke\":true}}";

        private List<string> KeysUnder(string folder) =>
            Files.Keys.Where(k => folder.Length == 0 || k.StartsWith(folder + "/", StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();

        // The cursor carries the folder and the offset so list_folder/continue reproduces the same ordered key set.
        private HttpResponseMessage Page(List<string> keys, int start, string folder)
        {
            var slice = keys.Skip(start).Take(PageSize).ToList();
            var more = start + slice.Count < keys.Count;
            var entries = string.Join(",", slice.Select(k => FileMeta(k, Files[k].Length)));
            return Json($$"""{"entries":[{{entries}}],"cursor":"{{folder}}|{{start + slice.Count}}","has_more":{{(more ? "true" : "false")}}}""");
        }

        private static string FileMeta(string path, int size) =>
            $$"""{".tag":"file","name":"{{System.IO.Path.GetFileName(path)}}","path_lower":"{{path.ToLowerInvariant()}}","path_display":"{{path}}","id":"id:{{Math.Abs(path.GetHashCode())}}","client_modified":"2024-01-02T03:04:05Z","server_modified":"2024-01-02T03:04:05Z","rev":"0123456789abcdef","size":{{size}},"is_downloadable":true,"content_hash":"{{new string('0', 64)}}"}""";

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Error(int status, string summary, string? error) =>
            new((HttpStatusCode)status)
            {
                Content = new StringContent(
                    error == null ? summary : $$"""{"error_summary":"{{summary}}","error":{{error}}}""",
                    Encoding.UTF8, "application/json")
            };

        private const string Account =
            """{"account_id":"dbid:AAH4f99T0taONIb-OurWxbNQ6ywGRopQngc","name":{"given_name":"Test","surname":"User","familiar_name":"Test","display_name":"Test User","abbreviated_name":"TU"},"email":"t@example.invalid","email_verified":true,"disabled":false,"locale":"en","referral_link":"https://db.tt/x","is_paired":false,"account_type":{".tag":"basic"},"root_info":{".tag":"user","root_namespace_id":"1","home_namespace_id":"1"}}""";
    }
}

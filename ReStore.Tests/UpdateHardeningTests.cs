using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using ReStore.Core.src.core;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Update download hardening: size caps enforced while streaming, and a zip-bomb limit on portable extraction.</summary>
public class UpdateHardeningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReStoreUpdHard_" + Guid.NewGuid().ToString("N"));

    public UpdateHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>A readable stream of <paramref name="length"/> filler bytes that records how many were actually read; no Content-Length.</summary>
    private sealed class CountingStream(long length, byte fill = (byte)' ') : Stream
    {
        private long _position;

        public long BytesRead => Interlocked.Read(ref _position);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Fill(buffer, fill, offset, n);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = (int)Math.Min(buffer.Length, length - _position);
            buffer.Span[..n].Fill(fill);
            _position += n;
            return ValueTask.FromResult(n);
        }
    }

    private sealed class UnknownLengthContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context) => stream.CopyToAsync(target);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    // ---- (a) latest.json -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Manifest_WithoutContentLength_IsCappedWhileStreaming()
    {
        var body = new CountingStream(64L * 1024 * 1024);
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(body) }));
        var checker = new UpdateChecker(handler, "1.7.0", AppChannel.Msix, "x64", null, _ => null);

        var result = await checker.CheckAsync(false);

        result.Status.Should().Be(UpdateStatus.Failed);
        result.Error.Should().Contain("too large");
        body.BytesRead.Should().BeLessThan(UpdateChecker.MaxResponseBytes + 1024 * 1024, "the body is abandoned once the cap is exceeded, not buffered whole");
    }

    [Fact]
    public async Task Manifest_WithOversizedContentLength_IsRefused()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CountingStream(5 * 1024 * 1024))
        }));
        var checker = new UpdateChecker(handler, "1.7.0", AppChannel.Msix, "x64", null, _ => null);

        (await checker.CheckAsync(false)).Status.Should().Be(UpdateStatus.Failed);
    }

    [Fact]
    public async Task Manifest_WithinTheCap_StillWorks()
    {
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(FakeHttpHandler.Json(UpdateCheckTests.ManifestJson("1.8.0"))));
        var checker = new UpdateChecker(handler, "1.7.0", AppChannel.Msix, "x64", null, _ => null);

        (await checker.CheckAsync(false)).Status.Should().Be(UpdateStatus.UpdateAvailable);
    }

    // ---- (b) package download --------------------------------------------------------------------------------------

    private UpdateInstaller Installer(HttpContent content) =>
        new(new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })),
            null, AppChannel.Portable, "x64", Path.Combine(_dir, "updates"), Path.Combine(_dir, "install"), _dir,
            new RepositoryCoordinator(Path.Combine(_dir, "repo.lock")), () => "ReStore_abc123");

    private static UpdateAsset Asset(long size) =>
        new("cli-zip", "x64", "a.zip", "https://github.com/js302/ReStore/releases/download/v1.8.0/a.zip", new string('a', 64), size);

    [Fact]
    public async Task Download_ThatExceedsTheListedSize_IsAbortedWhileStreaming()
    {
        var body = new CountingStream(64L * 1024 * 1024);
        var installer = Installer(new UnknownLengthContent(body));
        var target = Path.Combine(_dir, "dl", "a.zip");

        var act = () => installer.DownloadAsync(Asset(1000), target, null, CancellationToken.None);

        (await act.Should().ThrowAsync<UpdateInstallException>()).WithMessage("*larger than the release says*");
        body.BytesRead.Should().BeLessThan(1024 * 1024, "the stream is dropped as soon as the listed size is exceeded");
        File.Exists(target).Should().BeFalse();
        File.Exists(target + ".partial").Should().BeFalse();
    }

    [Fact]
    public async Task Download_WithAnOversizedContentLength_IsRefusedBeforeReading()
    {
        var payload = new byte[5000];
        var installer = Installer(new ByteArrayContent(payload));

        var act = () => installer.DownloadAsync(Asset(1000), Path.Combine(_dir, "dl", "a.zip"), null, CancellationToken.None);

        (await act.Should().ThrowAsync<UpdateInstallException>()).WithMessage("*larger than the release says*");
    }

    [Fact]
    public async Task Download_OfTheListedSize_Succeeds()
    {
        var payload = new byte[5000];
        new Random(1).NextBytes(payload);
        var asset = new UpdateAsset("cli-zip", "x64", "a.zip", "https://github.com/js302/ReStore/releases/download/v1.8.0/a.zip",
            Convert.ToHexStringLower(SHA256.HashData(payload)), payload.Length);

        var path = await Installer(new ByteArrayContent(payload)).DownloadAsync(asset, Path.Combine(_dir, "dl", "a.zip"), null, CancellationToken.None);

        File.ReadAllBytes(path).Should().Equal(payload);
    }

    // ---- (c) zip bombs ---------------------------------------------------------------------------------------------

    private string BuildZip(params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var entryStream = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
            entryStream.Write(content);
        }

        return path;
    }

    [Fact]
    public void ExtractSafely_RefusesAZipThatExpandsFarBeyondItsSize()
    {
        var zip = BuildZip(("bomb.bin", new byte[64 * 1024 * 1024]));
        new FileInfo(zip).Length.Should().BeLessThan(2 * 1024 * 1024);
        var destination = Path.Combine(_dir, "out");

        var act = () => UpdateInstaller.ExtractSafely(zip, destination);

        act.Should().Throw<UpdateInstallException>().WithMessage("*allowed size*");
        (Directory.Exists(destination) && Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Any(f => new FileInfo(f).Length > 0))
            .Should().BeFalse("nothing is written once the declared total is over the cap");
    }

    [Fact]
    public void ExtractSafely_StopsWritingWhenTheRealSizeExceedsTheBudget_EvenIfTheHeaderLies()
    {
        var zip = BuildZip(("big.bin", new byte[4 * 1024 * 1024]));
        var bytes = File.ReadAllBytes(zip);

        // Central directory header (PK\x01\x02): uncompressed size at offset 24. Declare 1 byte.
        var central = bytes.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
        central.Should().BeGreaterThan(0);
        BitConverter.GetBytes(1).CopyTo(bytes, central + 24);
        File.WriteAllBytes(zip, bytes);
        var destination = Path.Combine(_dir, "out-lie");

        try
        {
            UpdateInstaller.ExtractSafely(zip, destination, maxUncompressedBytes: 1024 * 1024);
        }
        catch (UpdateInstallException)
        {
            // Refused, or (when the runtime already honours the declared length) bounded: either way the cap holds.
        }

        Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length).Should().BeLessThanOrEqualTo(1024 * 1024);
    }

    [Fact]
    public void ExtractSafely_RefusesTooManyEntries()
    {
        var zip = BuildZip(Enumerable.Range(0, 11).Select(i => ($"f{i}.txt", new byte[] { 1 })).ToArray());

        var act = () => UpdateInstaller.ExtractSafely(zip, Path.Combine(_dir, "out-many"), maxEntries: 10);

        act.Should().Throw<UpdateInstallException>().WithMessage("*entries*");
    }

    [Fact]
    public void ExtractSafely_AcceptsAnOrdinaryZip()
    {
        var zip = BuildZip(("restore.exe", Encoding.UTF8.GetBytes("MZ" + new string('x', 5000))), ("dir/readme.txt", Encoding.UTF8.GetBytes("hello")));
        var destination = Path.Combine(_dir, "out-ok");

        UpdateInstaller.ExtractSafely(zip, destination);

        File.ReadAllText(Path.Combine(destination, "dir", "readme.txt")).Should().Be("hello");
        File.Exists(Path.Combine(destination, "restore.exe")).Should().BeTrue();
    }

    [Theory]
    [InlineData(100L, 16L << 20)]
    [InlineData(5_000_000L, 50_000_000L)]
    [InlineData(500_000_000L, 1L << 30)]
    public void MaxUncompressedBytes_IsTenTimesTheZipWithinSixteenMegabytesAndOneGigabyte(long zipLength, long expected) =>
        UpdateInstaller.MaxUncompressedBytes(zipLength).Should().Be(expected);
}

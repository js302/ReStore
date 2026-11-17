using System.Diagnostics;
using System.Text;
using FluentAssertions;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;
using Xunit.Abstractions;

namespace ReStore.Tests;

/// <summary>
/// Chunking algorithm 3 (fast chunker, written with v3 manifests). Algorithm 2 stays pinned by
/// <see cref="ChunkingServiceTests"/>; these tests pin algorithm 3 and check it against a naive reference.
/// </summary>
public class ChunkingFastAlgorithmTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ReStoreFastChunk_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static ChunkDiffingConfig Config(int min = 1, int target = 2, int max = 8, int window = 32) => new()
    {
        MinChunkSizeKB = min,
        TargetChunkSizeKB = target,
        MaxChunkSizeKB = max,
        RollingHashWindowSize = window
    };

    private ChunkingService Service(ChunkDiffingConfig config, int algorithm, Func<string, Stream>? open = null) => new(
        _logger, config, new EncryptionService(_logger), false, null, open, algorithmVersion: algorithm);

    private static byte[] Bytes(int length, uint seed)
    {
        var buffer = new byte[length];
        var state = seed;
        for (var index = 0; index < length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            buffer[index] = (byte)(state & 0xFF);
        }

        return buffer;
    }

    private static byte[] Text(int length, uint seed)
    {
        string[] words = ["the", "backup", "chunk", "of", "and", "snapshot", "manifest", "a", "file", "to", "restore", "hash", "data", "is", "in"];
        var state = seed;
        var sb = new StringBuilder();
        while (sb.Length < length)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            sb.Append(words[state % words.Length]).Append(state % 17 == 0 ? ".\n" : " ");
        }

        return Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    private string Write(string name, byte[] data)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private async Task<List<int>> Sizes(ChunkingService service, string path) =>
        (await service.BuildFileManifestEntryAsync(path, _root)).FileEntry.Chunks.Select(c => c.PlainSizeBytes).ToList();

    /// <summary>Independent, slow definition of algorithm 3: hash the last <paramref name="window"/> bytes afresh at every position.</summary>
    private static List<int> NaiveBoundaries(byte[] data, ChunkDiffingConfig config)
    {
        var gear = new ulong[256];
        ulong seed = 0x9E3779B185EBCA87UL;
        for (var i = 0; i < 256; i++)
        {
            seed ^= seed >> 12;
            seed ^= seed << 25;
            seed ^= seed >> 27;
            gear[i] = unchecked(seed * 0x2545F4914F6CDD1DUL);
        }

        int min = config.MinChunkSizeKB * 1024, max = config.MaxChunkSizeKB * 1024, window = config.RollingHashWindowSize;
        var target = (long)config.TargetChunkSizeKB * 1024;
        long down = 1; while (down * 2 <= target) down *= 2;
        var nearest = target - down < down * 2 - target ? down : down * 2;
        var mask = (ulong)nearest - 1;

        var sizes = new List<int>();
        var start = 0;
        while (start < data.Length)
        {
            var end = Math.Min(data.Length, start + max);
            var cut = end - start;
            for (var size = Math.Max(min, 1); size <= end - start; size++)
            {
                ulong h = 0;
                var from = window > 0 ? Math.Max(start, start + size - window) : start;
                unchecked
                {
                    for (var i = from; i < start + size; i++)
                    {
                        h = (h * 257) + gear[data[i]];
                    }
                }

                if ((h & mask) == mask)
                {
                    cut = size;
                    break;
                }
            }

            sizes.Add(cut);
            start += cut;
        }

        return sizes;
    }

    [Theory]
    [InlineData(1, 2, 8, 32)]
    [InlineData(1, 2, 8, 2048)]  // window larger than min: hashing starts at the chunk start
    [InlineData(2, 4, 16, 0)]    // unbounded window
    [InlineData(1, 3, 6, 48)]    // non-power-of-two target
    public async Task Boundaries_MatchNaiveReference(int min, int target, int max, int window)
    {
        var config = Config(min, target, max, window);
        var data = Bytes(160 * 1024, 0xABCDEF01);
        var path = Write("ref.bin", data);

        var sizes = await Sizes(Service(config, 3), path);

        sizes.Should().Equal(NaiveBoundaries(data, config));
        sizes.Sum().Should().Be(data.Length);
        sizes.Take(sizes.Count - 1).Should().OnlyContain(s => s >= min * 1024 && s <= max * 1024);
    }

    [Fact]
    public async Task Boundaries_AreIndependentOfReadSize()
    {
        var data = Bytes(100 * 1024, 7);
        var path = Write("reads.bin", data);
        var config = Config();

        var normal = await Sizes(Service(config, 3), path);
        var dribbled = await Sizes(Service(config, 3, p => new DribbleStream(File.OpenRead(p), 777)), path);

        dribbled.Should().Equal(normal);
    }

    [Fact]
    public async Task Algorithm3_ChunksReassembleToTheFile_AndHaveStableIds()
    {
        var data = Bytes(70 * 1024, 99);
        var path = Write("ids.bin", data);
        var service = Service(Config(), 3);

        var first = await service.BuildFileManifestEntryAsync(path, _root);
        var second = await service.BuildFileManifestEntryAsync(path, _root);

        first.ChunkPayloads.SelectMany(p => p.StoredPayload).ToArray().Should().Equal(data);
        first.FileEntry.Chunks.Select(c => c.ChunkId).Should().Equal(second.FileEntry.Chunks.Select(c => c.ChunkId));
    }

    [Fact]
    public async Task Algorithm3_GoldenBoundaries()
    {
        var path = Write("golden.bin", Bytes(64 * 1024, 0x5EED_1234));

        var result = await Service(Config(1, 3, 8, 32), 3).BuildFileManifestEntryAsync(path, _root);

        result.FileEntry.Chunks.Select(c => c.PlainSizeBytes).Should().Equal(GoldenSizes);
        result.FileEntry.Chunks[0].ChunkId.Should().Be(GoldenFirstChunkId);
    }

    [Fact]
    public async Task Algorithm2_IsUnchangedByTheFastChunker()
    {
        var path = Write("golden2.bin", Bytes(64 * 1024, 0x5EED_1234));

        var sizes = await Sizes(Service(Config(), 2), path);

        sizes.Should().Equal(3084, 3987, 3041, 7147, 6486, 2923, 1563, 1583, 3505, 2363, 3067, 1210, 8192, 2067, 5130, 1082, 1508, 4039, 3559);
    }

    [Fact]
    public async Task Algorithm3_AverageChunkSize_IsComparableToAlgorithm2()
    {
        var config = new ChunkDiffingConfig(); // 32 / 128 / 512 KB, window 64
        foreach (var (name, data) in new[] { ("random", Bytes(24 * 1024 * 1024, 11)), ("text", Text(24 * 1024 * 1024, 13)) })
        {
            var path = Write(name + ".bin", data);
            var a2 = await Sizes(Service(config, 2), path);
            var a3 = await Sizes(Service(config, 3), path);
            output.WriteLine($"{name}: algorithm 2 avg {a2.Average() / 1024:F1} KB over {a2.Count} chunks; algorithm 3 avg {a3.Average() / 1024:F1} KB over {a3.Count} chunks");
            a3.Average().Should().BeInRange(a2.Average() * 0.6, a2.Average() * 1.6);
        }
    }

    /// <summary>Generous floors for shared CI runners; the point is catching an order-of-magnitude regression, not tuning.</summary>
    [PerfFact]
    [Trait("Category", "Perf")]
    public async Task Benchmark_Throughput_BothAlgorithms()
    {
        const int megabytes = 64;
        var path = Write("bench.bin", Bytes(megabytes * 1024 * 1024, 12345));
        var config = new ChunkDiffingConfig();
        var rates = new Dictionary<int, double>();

        foreach (var algorithm in new[] { 2, 3 })
        {
            var service = Service(config, algorithm);
            Task Sink(ChunkBuildPayload _, CancellationToken __) => Task.CompletedTask;
            await service.BuildFileManifestEntryAsync(path, _root, Sink); // warm-up
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            var result = await service.BuildFileManifestEntryAsync(path, _root, Sink);
            sw.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var rate = megabytes / sw.Elapsed.TotalSeconds;
            rates[algorithm] = rate;
            output.WriteLine($"BENCHMARK algorithm {algorithm}: {megabytes} MB in {sw.Elapsed.TotalSeconds:F2} s = {rate:F0} MB/s, allocated {allocated / (1024 * 1024)} MB ({result.FileEntry.Chunks.Count} chunks, includes SHA-256)");

            // Measured serially on a dev box: algorithm 2 ~27 MB/s, algorithm 3 ~230 MB/s, both allocating ~1x the input (chunk copies
            // for the sink). Floors are far below that so shared runners do not flake; allocations at 3x catch a per-pass buffer leak.
            var (floor, allocationFactor) = algorithm == 2 ? (10, 3) : (50, 3);
            rate.Should().BeGreaterThan(floor, $"algorithm {algorithm} chunk+hash throughput floor (MB/s)");
            allocated.Should().BeLessThan(allocationFactor * megabytes * 1024L * 1024, $"algorithm {algorithm} allocations for one {megabytes} MB pass");
        }

        // Relative check, robust to runner speed.
        rates[3].Should().BeGreaterThan(rates[2], "algorithm 3 (the fast chunker) must stay faster than algorithm 2");
    }

    private sealed class DribbleStream(Stream inner, int maxRead) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, maxRead));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await inner.ReadAsync(buffer[..Math.Min(buffer.Length, maxRead)], cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static readonly int[] GoldenSizes = [3529, 8192, 8192, 3832, 2923, 1563, 5088, 2363, 8192, 6344, 6212, 1508, 4039, 3559];
    private const string GoldenFirstChunkId = "3c05b5996e909b95f5e04d158a888d18c661b2c8c4c62c005854f0714696b0cb";
}

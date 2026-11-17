using System.Security.Cryptography;
using System.Text;

namespace ReStore.E2E;

/// <summary>Deterministic test data (fixed seeds, no wall clock) and directory comparison.</summary>
internal static class TestFiles
{
    public static void WriteRandom(string path, int seed, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var random = new Random(seed);
        var buffer = new byte[1 << 20];
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        for (var remaining = length; remaining > 0; remaining -= buffer.Length)
        {
            random.NextBytes(buffer);
            stream.Write(buffer, 0, (int)Math.Min(buffer.Length, remaining));
        }
    }

    public static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    /// <summary>
    /// A small tree with text, binary, an empty file and nested folders. With <paramref name="unicode"/> the names carry
    /// non-ASCII letters, a space and a character outside the BMP.
    /// </summary>
    public static void CreateSampleTree(string root, bool unicode = false)
    {
        var docs = unicode ? "dokumente ünï" : "docs";
        var nested = unicode ? "日本語/глубоко" : "nested/deeper";
        WriteText(Path.Combine(root, unicode ? "café ☕ 😀.txt" : "a.txt"), "hello\nworld\n");
        WriteText(Path.Combine(root, docs, "notes.md"), string.Concat(Enumerable.Repeat("# Notes\nsome text that repeats\n", 200)));
        WriteRandom(Path.Combine(root, docs, "report.docx"), seed: 11, length: 70_000);
        WriteRandom(Path.Combine(root, nested, "data.bin"), seed: 12, length: 3_500_000);
        WriteRandom(Path.Combine(root, nested, "second.bin"), seed: 13, length: 120_001);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "empty.txt"), []);
    }

    /// <summary>Relative path (always '/') to SHA-256 of every file below <paramref name="root"/>.</summary>
    public static SortedDictionary<string, string> HashTree(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(file);
            result[Path.GetRelativePath(root, file).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(stream));
        }

        return result;
    }

    public static void AssertSameTree(string expectedRoot, string actualRoot)
    {
        var expected = HashTree(expectedRoot);
        var actual = HashTree(actualRoot);
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var (path, hash) in expected)
        {
            Assert.True(hash == actual[path], $"'{path}' differs between {expectedRoot} and {actualRoot}");
        }
    }

    public static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }
}

/// <summary>
/// A pool of large incompressible files, generated once per run, used to make a backup last long enough to interrupt it.
/// 32 x 12 MiB: a few seconds of work for the CLI on a normal disk, a few hundred milliseconds to generate.
/// </summary>
internal static class BigData
{
    public const int FileCount = 32;
    private const long FileLength = 12L << 20;

    private static readonly Lazy<string> Pool = new(() =>
    {
        var directory = Path.Combine(CliBinary.RunRoot, "bigpool");
        for (var i = 0; i < FileCount; i++)
        {
            TestFiles.WriteRandom(Path.Combine(directory, $"big-{i:D2}.bin"), seed: 1000 + i, length: FileLength);
        }

        return directory;
    });

    public static string PoolDirectory => Pool.Value;

    /// <summary>Copies the pool into <paramref name="destination"/> (OS copy, fast).</summary>
    public static void CopyInto(string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(PoolDirectory))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }
}

using FluentAssertions;
using ReStore.Core.src.utils;
using System.Security.Cryptography;
using System.Text;

namespace ReStore.Tests;

public class FileHasherTests : IDisposable
{
    private readonly string _testRoot;

    public FileHasherTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreFileHasherTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            try { Directory.Delete(_testRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task ComputeHashAsync_ShouldMatchComputeHash_AndKnownSha256()
    {
        var filePath = Path.Combine(_testRoot, "sample.txt");
        const string content = "ReStore hash test content";
        await File.WriteAllTextAsync(filePath, content);

        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        var syncHash = FileHasher.ComputeHash(filePath);
        var asyncHash = await FileHasher.ComputeHashAsync(filePath);

        syncHash.Should().Be(expectedHash);
        asyncHash.Should().Be(expectedHash);
    }

    [Fact]
    public async Task ComputeDirectoryHashesAsync_ShouldReturnHashes_ForAllFilesIncludingNested()
    {
        var rootFile = Path.Combine(_testRoot, "root.txt");
        var nestedDirectory = Path.Combine(_testRoot, "nested");
        var nestedFile = Path.Combine(nestedDirectory, "nested.txt");
        Directory.CreateDirectory(nestedDirectory);

        await File.WriteAllTextAsync(rootFile, "root-content");
        await File.WriteAllTextAsync(nestedFile, "nested-content");

        var hasher = new FileHasher();

        var hashes = await hasher.ComputeDirectoryHashesAsync(_testRoot, maxConcurrency: 1);

        hashes.Should().HaveCount(2);
        hashes.Should().ContainKey(rootFile);
        hashes.Should().ContainKey(nestedFile);
        hashes[rootFile].Should().Be(FileHasher.ComputeHash(rootFile));
        hashes[nestedFile].Should().Be(FileHasher.ComputeHash(nestedFile));
    }

    [Fact]
    public async Task IsContentDifferentAsync_ShouldReturnTrue_WhenFileLengthsDiffer()
    {
        var fileA = Path.Combine(_testRoot, "a.txt");
        var fileB = Path.Combine(_testRoot, "b.txt");
        await File.WriteAllTextAsync(fileA, "short");
        await File.WriteAllTextAsync(fileB, "this is longer");

        var result = await FileHasher.IsContentDifferentAsync(fileA, fileB);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsContentDifferentAsync_ShouldReturnTrue_WhenFileLengthsMatchButContentDiffers()
    {
        var fileA = Path.Combine(_testRoot, "same-length-a.txt");
        var fileB = Path.Combine(_testRoot, "same-length-b.txt");
        await File.WriteAllTextAsync(fileA, "abc123");
        await File.WriteAllTextAsync(fileB, "xyz123");

        var result = await FileHasher.IsContentDifferentAsync(fileA, fileB);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsContentDifferentAsync_ShouldReturnFalse_WhenFilesMatchExactly()
    {
        var fileA = Path.Combine(_testRoot, "identical-a.txt");
        var fileB = Path.Combine(_testRoot, "identical-b.txt");
        await File.WriteAllTextAsync(fileA, "identical content");
        await File.WriteAllTextAsync(fileB, "identical content");

        var result = await FileHasher.IsContentDifferentAsync(fileA, fileB);

        result.Should().BeFalse();
    }
}
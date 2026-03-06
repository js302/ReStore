using FluentAssertions;
using ReStore.Core.src.core;

namespace ReStore.Tests;

public class DiffManagerTests : IDisposable
{
    private readonly string _testRoot;

    public DiffManagerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ReStoreDiffManagerTests_" + Guid.NewGuid());
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
    public async Task CreateDiffAsync_ShouldReturnEmpty_WhenFilesAreIdentical()
    {
        var originalFile = Path.Combine(_testRoot, "original.bin");
        var newFile = Path.Combine(_testRoot, "new.bin");
        var content = "same content";

        await File.WriteAllTextAsync(originalFile, content);
        await File.WriteAllTextAsync(newFile, content);

        var diff = await DiffManager.CreateDiffAsync(originalFile, newFile);

        diff.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAndApplyDiffAsync_ShouldReconstructNewFile_ForSmallFileChanges()
    {
        var originalFile = Path.Combine(_testRoot, "small-original.txt");
        var newFile = Path.Combine(_testRoot, "small-new.txt");
        var outputFile = Path.Combine(_testRoot, "small-output.txt");

        await File.WriteAllTextAsync(originalFile, "hello world");
        await File.WriteAllTextAsync(newFile, "hello there");

        var diff = await DiffManager.CreateDiffAsync(originalFile, newFile);
        await DiffManager.ApplyDiffAsync(originalFile, diff, outputFile);

        var reconstructed = await File.ReadAllTextAsync(outputFile);
        var expected = await File.ReadAllTextAsync(newFile);

        reconstructed.Should().Be(expected);
    }

    [Fact]
    public async Task CreateAndApplyDiffAsync_ShouldReconstructNewFile_ForChunkBoundaryChanges()
    {
        var originalFile = Path.Combine(_testRoot, "large-original.bin");
        var newFile = Path.Combine(_testRoot, "large-new.bin");
        var outputFile = Path.Combine(_testRoot, "large-output.bin");

        var originalContent = new string('A', 5000) + new string('B', 5000) + new string('C', 5000);
        var modifiedContent = new string('A', 5000) + new string('X', 5000) + new string('C', 5000);

        await File.WriteAllTextAsync(originalFile, originalContent);
        await File.WriteAllTextAsync(newFile, modifiedContent);

        var diff = await DiffManager.CreateDiffAsync(originalFile, newFile);
        await DiffManager.ApplyDiffAsync(originalFile, diff, outputFile);

        var reconstructed = await File.ReadAllTextAsync(outputFile);
        reconstructed.Should().Be(modifiedContent);
    }
}

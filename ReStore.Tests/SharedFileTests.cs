using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public sealed class SharedFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ReStoreSharedFile_" + Guid.NewGuid().ToString("N"));

    public SharedFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task Retry_RepeatsTransientFailures_UntilTheActionSucceeds()
    {
        var calls = 0;
        await SharedFile.RetryAsync(() =>
        {
            if (++calls < 3)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }
        }, attempts: 8, delay: NoDelay);

        calls.Should().Be(3);
    }

    [Fact]
    public async Task Retry_GivesUpAfterTheLastAttempt_WithTheOriginalException()
    {
        var calls = 0;
        var act = () => SharedFile.RetryAsync(() => { calls++; throw new UnauthorizedAccessException("denied"); }, attempts: 4, delay: NoDelay);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        calls.Should().Be(4);
    }

    [Fact]
    public async Task Retry_DoesNotRetryAMissingFile()
    {
        var calls = 0;
        var act = () => SharedFile.RetryAsync(() => { calls++; throw new FileNotFoundException("gone"); }, attempts: 8, delay: NoDelay);

        await act.Should().ThrowAsync<FileNotFoundException>();
        calls.Should().Be(1);
    }

    [Theory]
    [InlineData(typeof(IOException), true)]
    [InlineData(typeof(UnauthorizedAccessException), true)]
    [InlineData(typeof(FileNotFoundException), false)]
    [InlineData(typeof(DirectoryNotFoundException), false)]
    [InlineData(typeof(PathTooLongException), false)]
    [InlineData(typeof(InvalidOperationException), false)]
    public void IsTransient_ClassifiesErrors(Type type, bool expected) =>
        SharedFile.IsTransient((Exception)Activator.CreateInstance(type)!).Should().Be(expected);

    [Fact]
    public async Task ReplaceSucceeds_WhileAReaderStillHasTheFileOpen()
    {
        var live = Path.Combine(_dir, "state.json");
        var temp = Path.Combine(_dir, "state.json.1.tmp");
        await File.WriteAllTextAsync(live, "old");
        await File.WriteAllTextAsync(temp, "new");

        // A reader opened the way SharedFile.ReadAllTextAsync opens files (read/write/delete sharing) must not block the swap:
        // with a plain File.ReadAllText-style handle Windows refuses with "Unable to remove the file to be replaced".
        await using (new FileStream(live, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            await SharedFile.ReplaceAsync(temp, live, live + ".bak");
        }

        (await SharedFile.ReadAllTextAsync(live)).Should().Be("new");
        (await File.ReadAllTextAsync(live + ".bak")).Should().Be("old");
    }

    [Fact]
    public async Task ReadAllText_DoesNotBlockAConcurrentOverwritingMove()
    {
        var live = Path.Combine(_dir, "config.json");
        var temp = Path.Combine(_dir, "config.json.tmp");
        await File.WriteAllTextAsync(live, "{\"v\":1}");
        await File.WriteAllTextAsync(temp, "{\"v\":2}");

        var stream = new FileStream(live, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using (stream)
        {
            await SharedFile.MoveOverwriteAsync(temp, live);
        }

        (await SharedFile.ReadAllTextAsync(live)).Should().Be("{\"v\":2}");
    }
}

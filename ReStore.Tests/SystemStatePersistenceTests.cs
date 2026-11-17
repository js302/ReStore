using FluentAssertions;
using Moq;
using ReStore.Core.src.core;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class SystemStatePersistenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _stateFile;

    public SystemStatePersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ReStoreStatePersist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _stateFile = Path.Combine(_dir, "system_state.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SystemState NewState()
    {
        var state = new SystemState(new Mock<ILogger>().Object)
        {
            LoadRetryBaseDelay = TimeSpan.FromMilliseconds(1)
        };
        state.SetStateFilePath(_stateFile);
        return state;
    }

    private string[] CorruptCopies() => Directory.GetFiles(_dir, "system_state.json.corrupt-*");

    [Fact]
    public async Task Load_CorruptFile_KeepsCorruptCopyAndUsesBak()
    {
        var first = NewState();
        first.LastBackupTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await first.SaveStateAsync();
        first.LastBackupTime = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        await first.SaveStateAsync(); // the first save is now system_state.json.bak
        File.Exists(_stateFile + ".bak").Should().BeTrue();

        await File.WriteAllTextAsync(_stateFile, "{ not json");

        var loaded = NewState();
        await loaded.LoadStateAsync();

        loaded.LastBackupTime.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var corrupt = CorruptCopies();
        corrupt.Should().HaveCount(1);
        File.ReadAllText(corrupt[0]).Should().Be("{ not json");

        // Saving afterwards works and keeps the good .bak slot from being a corrupt file.
        await loaded.SaveStateAsync();
        File.ReadAllText(_stateFile).Should().Contain("LastBackupTime");
    }

    [Fact]
    public async Task Load_CorruptFileAndNoBak_StartsEmptyAndKeepsCorruptCopy()
    {
        await File.WriteAllTextAsync(_stateFile, "garbage");

        var loaded = NewState();
        loaded.LastBackupTime = DateTime.UtcNow;
        await loaded.LoadStateAsync();

        loaded.BackupHistory.Should().BeEmpty();
        loaded.LastBackupTime.Should().Be(DateTime.MinValue);
        CorruptCopies().Should().HaveCount(1);

        await loaded.SaveStateAsync(); // empty start is a legitimate state, saving is allowed
        File.Exists(_stateFile).Should().BeTrue();
    }

    [Fact]
    public async Task Load_CorruptFileAndCorruptBak_StartsEmpty()
    {
        await File.WriteAllTextAsync(_stateFile, "garbage");
        await File.WriteAllTextAsync(_stateFile + ".bak", "also garbage");

        var loaded = NewState();
        await loaded.LoadStateAsync();

        loaded.BackupHistory.Should().BeEmpty();
        CorruptCopies().Should().HaveCount(1);
        File.ReadAllText(_stateFile + ".bak").Should().Be("also garbage");
    }

    [Fact]
    public async Task Load_MissingFile_StartsEmpty()
    {
        var loaded = NewState();
        await loaded.LoadStateAsync();

        loaded.BackupHistory.Should().BeEmpty();
        CorruptCopies().Should().BeEmpty();
    }

    [Fact]
    public async Task Load_LockedFile_ThrowsAndBlocksSaveWithoutTouchingFile()
    {
        const string original = "{ \"LastBackupTime\": \"2026-03-03T00:00:00Z\" }";
        await File.WriteAllTextAsync(_stateFile, original);

        var state = NewState();
        await using (new FileStream(_stateFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var load = () => state.LoadStateAsync();
            await load.Should().ThrowAsync<IOException>();
        }

        var save = () => state.SaveStateAsync();
        await save.Should().ThrowAsync<InvalidOperationException>().WithMessage("*will not be saved*");

        File.ReadAllText(_stateFile).Should().Be(original);
        Directory.GetFiles(_dir).Should().ContainSingle().Which.Should().Be(_stateFile);
    }

    [Fact]
    public async Task Load_AfterFailedLoad_SucceedsOnceFileIsReadableAndUnblocksSave()
    {
        await File.WriteAllTextAsync(_stateFile, "{}");
        var state = NewState();
        await using (new FileStream(_stateFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await ((Func<Task>)(() => state.LoadStateAsync())).Should().ThrowAsync<IOException>();
        }

        await state.LoadStateAsync();
        await state.SaveStateAsync();
    }

    [Fact]
    public async Task Save_ParallelCalls_AlwaysLeaveValidJsonAndNoTempFiles()
    {
        var state = NewState();
        state.LastBackupTime = DateTime.UtcNow;

        var saves = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            state.LastBackupTime = DateTime.UtcNow.AddMinutes(i);
            await state.SaveStateAsync();
        })).ToList();
        await Task.WhenAll(saves);

        var reloaded = NewState();
        await reloaded.LoadStateAsync();
        CorruptCopies().Should().BeEmpty();
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
        reloaded.LastBackupTime.Should().NotBe(DateTime.MinValue);
    }

    [Fact]
    public async Task Save_Failure_PropagatesAndLeavesNoTempFile()
    {
        // A directory squatting on the target path makes both Replace and Move fail.
        Directory.CreateDirectory(_stateFile);
        File.WriteAllText(Path.Combine(_stateFile, "x"), "x");

        var state = NewState();
        var save = () => state.SaveStateAsync();

        await save.Should().ThrowAsync<Exception>();
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Save_ExistingFile_RotatesPreviousVersionToBak()
    {
        var state = NewState();
        state.LastBackupTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await state.SaveStateAsync();
        state.LastBackupTime = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        await state.SaveStateAsync();

        File.ReadAllText(_stateFile + ".bak").Should().Contain("2026-01-01");
        File.ReadAllText(_stateFile).Should().Contain("2026-02-02");
    }
}

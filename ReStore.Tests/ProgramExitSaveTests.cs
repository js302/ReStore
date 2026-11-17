using FluentAssertions;
using ReStore.Core;
using ReStore.Core.src.core;

namespace ReStore.Tests;

/// <summary>The CLI's final state save: read-only commands must not fail or hang because a backup is running.</summary>
public class ProgramExitSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(TestEnvironment.RootDirectory, "exitsave_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();

    public ProgramExitSaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private SystemState NewState()
    {
        var state = new SystemState(_logger);
        state.SetStateFilePath(Path.Combine(_dir, "system_state.json"));
        return state;
    }

    [Fact]
    public async Task ReadOnlyCommand_WhileRepositoryIsBusy_SucceedsWithoutSaving()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_dir, "repository.lock"));
        var state = NewState();
        await using var busy = await coordinator.AcquireMutationAsync("GUI backup");

        var ok = await Program.SaveStateAtExitAsync(state, _logger, mutatedState: false, coordinator);

        ok.Should().BeTrue("a restore must keep exit code 0 when only its telemetry could not be saved");
        File.Exists(Path.Combine(_dir, "system_state.json")).Should().BeFalse();
        _logger.Messages.Should().Contain(m => m.Contains("telemetry was not saved"));
    }

    [Fact]
    public async Task ReadOnlyCommand_WhenRepositoryIsFree_SavesTelemetry()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_dir, "repository.lock"));

        (await Program.SaveStateAtExitAsync(NewState(), _logger, mutatedState: false, coordinator)).Should().BeTrue();

        File.Exists(Path.Combine(_dir, "system_state.json")).Should().BeTrue();
    }

    [Fact]
    public async Task MutatingCommand_FailsWhenTheLeaseCannotBeTaken()
    {
        var coordinator = new RepositoryCoordinator(Path.Combine(_dir, "repository.lock"), TimeSpan.FromMilliseconds(200));
        await using var other = new FileStream(Path.Combine(_dir, "repository.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        (await Program.SaveStateAtExitAsync(NewState(), _logger, mutatedState: true, coordinator)).Should().BeFalse();
    }
}

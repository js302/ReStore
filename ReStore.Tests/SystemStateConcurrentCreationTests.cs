using FluentAssertions;
using ReStore.Core.src.core;

namespace ReStore.Tests;

/// <summary>
/// Found by the real-process E2E suite (ConcurrencyTests.FirstEverStateFile_...): when several ReStore processes start at once on a fresh
/// data folder, one of them creates <c>system_state.json</c> by renaming its temp file. Another process that listed that temp file as a
/// "leftover of an interrupted save" then failed to read it (it had just been renamed) and, after retrying, aborted with
/// "Could not load the system state" (exit 1).
/// </summary>
public class SystemStateConcurrentCreationTests : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "statecreate_" + Guid.NewGuid().ToString("N"));

    public SystemStateConcurrentCreationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>A directory entry that is listed but cannot be opened (the temp file was renamed away a moment ago): a dangling symlink.</summary>
    [NonWindowsFact]
    public async Task ATempFileThatVanishesWhileBeingRead_IsNotALeftover_AndTheLoadDoesNotFail()
    {
        var statePath = Path.Combine(_root, "state", "system_state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.CreateSymbolicLink(statePath + ".0123456789abcdef0123456789abcdef.tmp", Path.Combine(_root, "renamed-away"));

        var state = new SystemState(new TestLogger());
        state.SetStateFilePath(statePath);
        state.LoadRetryBaseDelay = TimeSpan.FromMilliseconds(1);

        var load = () => state.LoadStateAsync();
        await load.Should().NotThrowAsync();
        state.BackupHistory.Should().BeEmpty();
    }
}

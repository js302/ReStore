using FluentAssertions;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class ProtectionControllerTests
{
    private sealed class FakeWorker : IProtectionWorker
    {
        public int Started;
        public int Stopped;
        public Exception? FailOnStart;
        public TaskCompletionSource? StopGate;
        public DateTime? Next { get; set; }

        public Task StartAsync()
        {
            Started++;
            return FailOnStart != null ? Task.FromException(FailOnStart) : Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped++;
            return StopGate?.Task ?? Task.CompletedTask;
        }

        public DateTime? NextRunUtc => Next;

        public bool HeldByWindow { get; set; }

        public bool NextRunHeldByWindow => HeldByWindow;

        public object? Pending;
        public object? Adopted;
        public object? TakePendingChanges() => Pending;
        public void AdoptPendingChanges(object? pending) => Adopted = pending;
    }

    private sealed class Harness
    {
        public readonly List<FakeWorker> Watchers = [];
        public readonly List<FakeWorker> Schedulers = [];
        public bool AutoStart = true;
        public string? ValidationError;
        public Func<FakeWorker>? WatcherFactory;
        public string Fingerprint = "a";
        public readonly ProtectionController Controller;
        public readonly List<ProtectionState> Transitions = [];

        public Harness()
        {
            Controller = new ProtectionController(
                () => { var w = WatcherFactory?.Invoke() ?? new FakeWorker(); Watchers.Add(w); return w; },
                () => { var w = new FakeWorker { Next = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) }; Schedulers.Add(w); return w; },
                new TestLogger(),
                () => AutoStart,
                () => ValidationError,
                () => Fingerprint);
            Controller.StateChanged += (_, _) => Transitions.Add(Controller.State);
        }
    }

    [Fact]
    public async Task StartAsync_StartsBothWorkersOnce_AndIsIdempotent()
    {
        var h = new Harness();

        await h.Controller.StartAsync();
        await h.Controller.StartAsync();

        h.Controller.State.Should().Be(ProtectionState.Running);
        h.Watchers.Should().ContainSingle().Which.Started.Should().Be(1);
        h.Schedulers.Should().ContainSingle().Which.Started.Should().Be(1);
        h.Transitions.Should().Equal(ProtectionState.Running);
        h.Controller.NextRunUtc.Should().Be(h.Schedulers[0].Next);
    }

    [Fact]
    public async Task NextRunHeldByWindow_FollowsTheScheduler_AndIsFalseWhenStopped()
    {
        var h = new Harness();
        h.Controller.NextRunHeldByWindow.Should().BeFalse("no scheduler yet");
        await h.Controller.StartAsync();
        h.Controller.NextRunHeldByWindow.Should().BeFalse();

        h.Schedulers[0].HeldByWindow = true;
        h.Controller.NextRunHeldByWindow.Should().BeTrue("the scheduler's next run waits for the window");

        await h.Controller.StopAsync();
        h.Controller.NextRunHeldByWindow.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_StopsBothWorkers_AndRemembersUserChoice()
    {
        var h = new Harness();
        await h.Controller.StartAsync();

        await h.Controller.StopAsync();

        h.Controller.State.Should().Be(ProtectionState.Stopped);
        h.Controller.StoppedByUser.Should().BeTrue();
        h.Watchers[0].Stopped.Should().Be(1);
        h.Schedulers[0].Stopped.Should().Be(1);
        h.Controller.NextRunUtc.Should().BeNull();
    }

    [Fact]
    public async Task RestartAsync_ReplacesBothWorkers()
    {
        var h = new Harness();
        await h.Controller.StartAsync();

        await h.Controller.RestartAsync();

        h.Controller.State.Should().Be(ProtectionState.Running);
        h.Watchers.Should().HaveCount(2);
        h.Schedulers.Should().HaveCount(2);
        h.Watchers[0].Stopped.Should().Be(1);
        h.Watchers[1].Started.Should().Be(1);
    }

    [Fact]
    public async Task AutoStartAsync_StartsOnlyWhenEnabled()
    {
        var h = new Harness { AutoStart = false };

        await h.Controller.AutoStartAsync();
        h.Controller.State.Should().Be(ProtectionState.Stopped);

        h.AutoStart = true;
        await h.Controller.AutoStartAsync();
        h.Controller.State.Should().Be(ProtectionState.Running);
    }

    [Fact]
    public async Task AutoStartAsync_DoesNotOverrideAUserStop()
    {
        var h = new Harness();
        await h.Controller.StartAsync();
        await h.Controller.StopAsync();

        await h.Controller.AutoStartAsync();

        h.Controller.State.Should().Be(ProtectionState.Stopped);
    }

    [Fact]
    public async Task ApplyConfigChange_RestartsWhenRunning_StartsWhenAutoStartNowApplies_AndRespectsUserStop()
    {
        var h = new Harness { AutoStart = false };

        await h.Controller.ApplyConfigChangeAsync();
        h.Controller.State.Should().Be(ProtectionState.Stopped, "nothing to watch yet");

        h.AutoStart = true;
        await h.Controller.ApplyConfigChangeAsync();
        h.Controller.State.Should().Be(ProtectionState.Running, "the first watch directory was added");

        h.Fingerprint = "changed";
        await h.Controller.ApplyConfigChangeAsync();
        h.Watchers.Should().HaveCount(2, "a running service restarts to pick up the change");

        await h.Controller.StopAsync();
        await h.Controller.ApplyConfigChangeAsync();
        h.Controller.State.Should().Be(ProtectionState.Stopped, "a user stop is not overridden by config saves");
    }

    [Fact]
    public async Task ApplyConfigChange_DoesNotRestart_WhenRelevantConfigIsUnchanged()
    {
        var h = new Harness();
        await h.Controller.StartAsync();

        await h.Controller.ApplyConfigChangeAsync();
        h.Watchers.Should().ContainSingle("an unrelated settings save must not interrupt running backups");

        h.Fingerprint = "b";
        await h.Controller.ApplyConfigChangeAsync();
        h.Watchers.Should().HaveCount(2);

        await h.Controller.ApplyConfigChangeAsync();
        h.Watchers.Should().HaveCount(2, "the new run recorded the new fingerprint");
    }

    [Fact]
    public async Task Restart_CarriesPendingChangesToTheNewWatcher()
    {
        var h = new Harness { WatcherFactory = () => new FakeWorker { Pending = "pending-set" } };
        await h.Controller.StartAsync();

        await h.Controller.RestartAsync();

        h.Watchers[1].Adopted.Should().Be("pending-set");
    }

    [Fact]
    public async Task ShutdownAsync_StopsWithoutRecordingAUserStop()
    {
        var h = new Harness();
        await h.Controller.StartAsync();

        await h.Controller.ShutdownAsync();

        h.Controller.State.Should().Be(ProtectionState.Stopped);
        h.Controller.StoppedByUser.Should().BeFalse();
    }

    [Fact]
    public async Task ShutdownAsync_GivesUpWaitingOnAStuckWorker_AfterTheTimeout()
    {
        var h = new Harness { WatcherFactory = () => new FakeWorker { StopGate = new TaskCompletionSource() } };
        await h.Controller.StartAsync();

        var stop = h.Controller.ShutdownAsync(TimeSpan.FromMilliseconds(100));

        (await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(stop);
        h.Controller.State.Should().Be(ProtectionState.Stopped);
        h.Schedulers[0].Stopped.Should().Be(1);
    }

    [Fact]
    public async Task StartFailure_SetsErrorAndStopsWhatStarted()
    {
        var h = new Harness { WatcherFactory = () => new FakeWorker { FailOnStart = new IOException("boom") } };

        await h.Controller.StartAsync();

        h.Controller.State.Should().Be(ProtectionState.Error);
        h.Controller.LastError.Should().Contain("boom");
        h.Watchers[0].Stopped.Should().Be(1, "a half-started watcher must be released");
        h.Controller.NextRunUtc.Should().BeNull();
    }

    [Fact]
    public async Task ValidationFailure_SetsError_WithoutCreatingWorkers()
    {
        var h = new Harness { ValidationError = "no storage" };

        await h.Controller.StartAsync();

        h.Controller.State.Should().Be(ProtectionState.Error);
        h.Controller.LastError.Should().Contain("no storage");
        h.Watchers.Should().BeEmpty();

        h.ValidationError = null;
        await h.Controller.StartAsync();
        h.Controller.State.Should().Be(ProtectionState.Running);
        h.Controller.LastError.Should().BeNull();
    }

    [Fact]
    public async Task PauseFor_StopsNow_AndResumesAfterTheDuration()
    {
        var h = new Harness();
        await h.Controller.StartAsync();

        await h.Controller.PauseFor(TimeSpan.FromMilliseconds(150));

        h.Controller.State.Should().Be(ProtectionState.Stopped);
        h.Controller.PausedUntilUtc.Should().NotBeNull();

        await WaitUntilAsync(() => h.Controller.State == ProtectionState.Running);
        h.Controller.PausedUntilUtc.Should().BeNull();
        h.Watchers.Should().HaveCount(2);
    }

    [Fact]
    public async Task PauseFor_IsCancelledByAnExplicitStart()
    {
        var h = new Harness();
        await h.Controller.StartAsync();
        // Long enough that it can never expire during the test: only the explicit start can end it.
        await h.Controller.PauseFor(TimeSpan.FromMinutes(10));
        h.Controller.State.Should().Be(ProtectionState.Stopped);

        await h.Controller.StartAsync();

        h.Controller.State.Should().Be(ProtectionState.Running);
        h.Controller.PausedUntilUtc.Should().BeNull("the explicit start cancelled the pause timer");
        h.Watchers.Should().HaveCount(2, "the cancelled pause must not start a third set");
        await h.Controller.StopAsync();
    }

    [Fact]
    public async Task PauseFor_BlocksConfigChangeRestarts()
    {
        var h = new Harness();
        await h.Controller.StartAsync();
        await h.Controller.PauseFor(TimeSpan.FromSeconds(30));

        await h.Controller.ApplyConfigChangeAsync();

        h.Controller.State.Should().Be(ProtectionState.Stopped);
        await h.Controller.StopAsync();
    }

    [Fact]
    public async Task PasswordNeeded_MapsToPausedNeedsPassword_AndBack()
    {
        var h = new Harness();
        h.Controller.MarkNeedsPassword();
        h.Controller.State.Should().Be(ProtectionState.Stopped, "only a running service can be waiting for a password");

        await h.Controller.StartAsync();
        h.Controller.MarkNeedsPassword();
        h.Controller.State.Should().Be(ProtectionState.PausedNeedsPassword);

        h.Controller.MarkPasswordAvailable();
        h.Controller.State.Should().Be(ProtectionState.Running);
    }

    [Fact]
    public async Task StopAsync_FromPausedNeedsPassword_StopsCleanly()
    {
        var h = new Harness();
        await h.Controller.StartAsync();
        h.Controller.MarkNeedsPassword();

        await h.Controller.StopAsync();

        h.Controller.State.Should().Be(ProtectionState.Stopped);
    }

    [Fact]
    public async Task ConcurrentCalls_NeverLeaveMoreThanOneWorkerSet()
    {
        var h = new Harness();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => i % 3 == 0 ? h.Controller.RestartAsync() : h.Controller.StartAsync()));

        h.Controller.State.Should().Be(ProtectionState.Running);
        h.Watchers.Count(w => w.Started > 0 && w.Stopped == 0).Should().Be(1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        condition().Should().BeTrue();
    }
}

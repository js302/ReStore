using FluentAssertions;
using ReStore.Core.src.monitoring;

namespace ReStore.Tests;

public class ProtectionPauseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "restore-pause-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Worker : IProtectionWorker
    {
        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    private ProtectionController NewController(ProtectionPauseStore store) =>
        new(() => new Worker(), () => new Worker(), new TestLogger(), pauseStore: store);

    private ProtectionPauseStore NewStore() => new(() => Path.Combine(_dir, "pause.json"));

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(2), "t", "t");

    [Fact]
    public void Tomorrow_exactly_one_hour_before_8_resumes_that_morning()
    {
        var now = new DateTime(2030, 1, 1, 5, 0, 0, DateTimeKind.Utc); // 07:00 local
        ProtectionPause.UntilTomorrowUtc(now, Zone).Should().Be(new DateTime(2030, 1, 1, 6, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Tomorrow_before_8_in_the_morning_resumes_same_morning()
    {
        var now = new DateTime(2030, 1, 1, 2, 0, 0, DateTimeKind.Utc); // 04:00 local
        ProtectionPause.UntilTomorrowUtc(now, Zone).Should().Be(new DateTime(2030, 1, 1, 6, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Tomorrow_too_close_to_8_rolls_to_next_day()
    {
        var now = new DateTime(2030, 1, 1, 5, 30, 0, DateTimeKind.Utc); // 07:30 local, 30 min to 08:00
        ProtectionPause.UntilTomorrowUtc(now, Zone).Should().Be(new DateTime(2030, 1, 2, 6, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Tomorrow_in_the_afternoon_resumes_next_morning()
    {
        var now = new DateTime(2030, 1, 1, 15, 0, 0, DateTimeKind.Utc); // 17:00 local
        ProtectionPause.UntilTomorrowUtc(now, Zone).Should().Be(new DateTime(2030, 1, 2, 6, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Tomorrow_skips_a_nonexistent_dst_time()
    {
        // Zone where 08:00 does not exist on the transition day: 08:00 -> 09:00 forward.
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2030, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 8, 0, 0), 3, 10),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 8, 0, 0), 11, 10));
        var zone = TimeZoneInfo.CreateCustomTimeZone("dst", TimeSpan.Zero, "dst", "dst", "dst2", [rule]);
        var result = ProtectionPause.UntilTomorrowUtc(new DateTime(2030, 3, 9, 12, 0, 0, DateTimeKind.Utc), zone);
        result.Should().BeAfter(new DateTime(2030, 3, 10, 6, 0, 0, DateTimeKind.Utc)).And.BeBefore(new DateTime(2030, 3, 10, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Store_round_trips_finite_and_indefinite_and_clears()
    {
        var store = NewStore();
        store.Load().Should().BeNull();

        var until = new DateTime(2030, 5, 5, 5, 5, 5, DateTimeKind.Utc);
        store.Save(until);
        store.Load().Should().Be(until);

        store.Save(ProtectionPause.Indefinite);
        ProtectionPause.IsIndefinite(store.Load()!.Value).Should().BeTrue();

        store.Clear();
        store.Load().Should().BeNull();
    }

    [Fact]
    public void Store_ignores_a_corrupt_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "pause.json"), "{not json");
        NewStore().Load().Should().BeNull();
    }

    [Fact]
    public async Task Pause_is_saved_and_restored_by_a_new_controller()
    {
        var store = NewStore();
        await using (var first = NewController(store))
        {
            await first.StartAsync();
            await first.PauseFor(TimeSpan.FromHours(2));
            first.PausedUntilUtc.Should().NotBeNull();
        }

        // Shutdown (disposal) must not forget the pause.
        store.Load().Should().NotBeNull();

        await using var second = NewController(store);
        await second.RestorePauseAsync();
        second.PausedUntilUtc.Should().BeCloseTo(DateTime.UtcNow.AddHours(2), TimeSpan.FromMinutes(1));

        await second.AutoStartAsync();
        second.State.Should().Be(ProtectionState.Stopped, "a restored pause blocks the launch auto-start");
    }

    [Fact]
    public async Task Indefinite_pause_survives_restart_until_resumed()
    {
        var store = NewStore();
        await using (var first = NewController(store))
        {
            await first.PauseUntilAsync(ProtectionPause.Indefinite);
            first.PausedUntilUtc.Should().Be(ProtectionPause.Indefinite);
        }

        await using var second = NewController(store);
        await second.RestorePauseAsync();
        ProtectionPause.IsIndefinite(second.PausedUntilUtc!.Value).Should().BeTrue();

        await second.StartAsync();
        second.State.Should().Be(ProtectionState.Running);
        second.PausedUntilUtc.Should().BeNull();
        store.Load().Should().BeNull("resuming forgets the saved pause");
    }

    [Fact]
    public async Task Expired_saved_pause_is_dropped_and_autostart_proceeds()
    {
        var store = NewStore();
        store.Save(DateTime.UtcNow.AddMinutes(-5));

        await using var controller = NewController(store);
        await controller.RestorePauseAsync();
        controller.PausedUntilUtc.Should().BeNull();
        store.Load().Should().BeNull();

        await controller.AutoStartAsync();
        controller.State.Should().Be(ProtectionState.Running);
    }

    [Fact]
    public async Task Stop_forgets_a_saved_pause()
    {
        var store = NewStore();
        await using var controller = NewController(store);
        await controller.PauseFor(TimeSpan.FromHours(1));
        await controller.StopAsync();
        store.Load().Should().BeNull();
    }

    [Fact]
    public async Task Pausing_while_stopped_still_raises_StateChanged()
    {
        await using var controller = NewController(NewStore());
        var raised = 0;
        controller.StateChanged += (_, _) => raised++;
        await controller.PauseUntilAsync(ProtectionPause.Indefinite);
        raised.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Global_status_describes_an_indefinite_pause()
    {
        var text = GlobalStatus.Build(null, TimeSpan.Zero, ProtectionState.Stopped, ProtectionPause.Indefinite, null, null);
        text.Protection.Should().Be("Protection paused");
        text.NextRun.Should().Be("Paused until resumed");
    }
}

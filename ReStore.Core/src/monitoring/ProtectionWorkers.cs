namespace ReStore.Core.src.monitoring;

/// <summary>Adapts <see cref="FileWatcher"/> to <see cref="IProtectionWorker"/>.</summary>
public sealed class FileWatcherWorker(FileWatcher watcher) : IProtectionWorker
{
    private IReadOnlyDictionary<string, string?>? _pending;

    public Task StartAsync() => watcher.StartAsync();

    // Cancels any watcher-triggered backup in flight and waits for it to unwind before releasing resources.
    public async Task StopAsync()
    {
        try
        {
            await watcher.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _pending = watcher.TakePendingChanges();
            watcher.Dispose();
        }
    }

    public object? TakePendingChanges() => _pending;

    public void AdoptPendingChanges(object? pending)
    {
        if (pending is IReadOnlyDictionary<string, string?> changes)
        {
            watcher.AdoptPendingChanges(changes);
        }
    }
}

/// <summary>Adapts <see cref="BackupScheduler"/> to <see cref="IProtectionWorker"/>.</summary>
public sealed class SchedulerWorker(BackupScheduler scheduler) : IProtectionWorker
{
    public Task StartAsync() => scheduler.StartAsync();

    public DateTime? NextRunUtc => scheduler.NextRunUtc;

    public bool NextRunHeldByWindow => scheduler.NextRunHeldByWindow;

    public async Task StopAsync()
    {
        try
        {
            // Cancels the loop (and a backup in flight) and waits for it to unwind.
            await scheduler.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            scheduler.Dispose();
        }
    }
}

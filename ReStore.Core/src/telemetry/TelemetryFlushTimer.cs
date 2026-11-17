namespace ReStore.Core.src.telemetry;

/// <summary>Uploads queued telemetry periodically (every 30 minutes by default) for long-running processes: the GUI and the service.</summary>
public sealed class TelemetryFlushTimer : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    private readonly Timer _timer;
    private readonly Func<TimeSpan, Task> _flush;
    private readonly TimeSpan _budget;
    private int _running;

    public TelemetryFlushTimer(TimeSpan? interval = null, TimeSpan? budget = null, Func<TimeSpan, Task>? flush = null)
    {
        _budget = budget ?? DefaultBudget;
        _flush = flush ?? (b => Telemetry.FlushAsync(b));
        var period = interval ?? DefaultInterval;
        _timer = new Timer(_ => _ = TickAsync(), null, period, period);
    }

    /// <summary>One flush; skipped when the previous one is still running. Never throws.</summary>
    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            await _flush(_budget).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort.
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}

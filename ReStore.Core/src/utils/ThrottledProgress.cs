namespace ReStore.Core.src.utils;

/// <summary>
/// Rate-limits an <see cref="IProgress{T}"/> sink. Operations report per chunk or per file, far faster than a UI
/// can usefully repaint; this forwards at most one report per <c>minInterval</c> and keeps the newest suppressed
/// one so <see cref="Flush"/> (called from a timer and at completion) can deliver the final state.
/// Thread-safe; the sink is invoked under a lock on the reporting thread (or the caller of <see cref="Flush"/>), so it sees reports in order and must not block.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly Action<T> _sink;
    private readonly long _minIntervalTicks;
    private readonly Func<long> _clock;
    private readonly object _gate = new();

    private long _lastForwarded;
    private bool _hasForwarded;
    private bool _hasPending;
    private T? _pending;

    public ThrottledProgress(Action<T> sink, TimeSpan minInterval, Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _minIntervalTicks = minInterval.Ticks;
        // Stopwatch ticks are converted so the clock and the interval share a unit.
        _clock = clock ?? (() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (TimeSpan.TicksPerSecond / (double)System.Diagnostics.Stopwatch.Frequency)));
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            var now = _clock();
            if (_hasForwarded && now - _lastForwarded < _minIntervalTicks)
            {
                _pending = value;
                _hasPending = true;
                return;
            }

            _lastForwarded = now;
            _hasForwarded = true;
            _hasPending = false;
            _pending = default;

            // Invoked under the lock so concurrent reporters reach the sink in the order they were accepted
            // (the sink must be quick and non-blocking, e.g. Progress<T>.Report which only posts).
            _sink(value);
        }
    }

    /// <summary>Delivers the newest suppressed report, if any.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_hasPending)
            {
                return;
            }

            var value = _pending;
            _pending = default;
            _hasPending = false;
            _lastForwarded = _clock();
            _hasForwarded = true;
            _sink(value!);
        }
    }
}

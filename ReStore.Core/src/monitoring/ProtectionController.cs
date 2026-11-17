using ReStore.Core.src.utils;

namespace ReStore.Core.src.monitoring;

public enum ProtectionState
{
    Stopped,
    Running,

    /// <summary>Running, but an encrypted backup needs a password nobody can type while the app is hidden.</summary>
    PausedNeedsPassword,
    Error
}

/// <summary>One background component (file watcher, scheduler) that the controller starts and stops.</summary>
public interface IProtectionWorker
{
    Task StartAsync();

    /// <summary>Cancels in-flight work and releases the component. Must be safe to call once after a failed start.</summary>
    Task StopAsync();

    /// <summary>Next time this worker expects to do work, when it knows.</summary>
    DateTime? NextRunUtc => null;

    /// <summary>True when <see cref="NextRunUtc"/> is late only because the backup window ("quiet hours") is closed until then.</summary>
    bool NextRunHeldByWindow => false;

    /// <summary>After <see cref="StopAsync"/>: changes seen but not yet backed up, to hand to the next run.</summary>
    object? TakePendingChanges() => null;

    /// <summary>Gives a freshly started worker the changes a previous one left behind.</summary>
    void AdoptPendingChanges(object? pending)
    {
    }
}

/// <summary>
/// Owns the lifetime of the app's background protection (file watcher + scheduler), independent of any
/// page or window. All transitions are serialised, so tray, dashboard, config changes and shutdown can
/// call in from anywhere. Raises <see cref="StateChanged"/> on whatever thread caused the change; UI
/// subscribers must marshal.
/// </summary>
public sealed class ProtectionController : IAsyncDisposable
{
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan MaxPauseSlice = TimeSpan.FromMinutes(5);

    private readonly Func<IProtectionWorker> _createWatcher;
    private readonly Func<IProtectionWorker> _createScheduler;
    private readonly Func<bool> _autoStartEnabled;
    private readonly Func<string?> _validate;
    private readonly Func<string>? _fingerprint;
    private readonly ProtectionPauseStore? _pauseStore;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IProtectionWorker? _watcher;
    private IProtectionWorker? _scheduler;
    private CancellationTokenSource? _pauseCts;
    private ProtectionState _state = ProtectionState.Stopped;
    private bool _disposed;
    private string? _runningFingerprint;
    private object? _carriedPending;

    /// <param name="autoStartEnabled">Whether launch / config changes may start protection (autoStart set and something to watch).</param>
    /// <param name="validate">Returns an error message when the configuration cannot be run, otherwise null.</param>
    /// <param name="fingerprint">Summary of the config a run binds to; <see cref="ApplyConfigChangeAsync"/> restarts only when it changed.</param>
    /// <param name="pauseStore">When set, a pause survives an app restart (see <see cref="RestorePauseAsync"/>).</param>
    public ProtectionController(
        Func<IProtectionWorker> createWatcher,
        Func<IProtectionWorker> createScheduler,
        ILogger logger,
        Func<bool>? autoStartEnabled = null,
        Func<string?>? validate = null,
        Func<string>? fingerprint = null,
        ProtectionPauseStore? pauseStore = null)
    {
        _fingerprint = fingerprint;
        _pauseStore = pauseStore;
        _createWatcher = createWatcher ?? throw new ArgumentNullException(nameof(createWatcher));
        _createScheduler = createScheduler ?? throw new ArgumentNullException(nameof(createScheduler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _autoStartEnabled = autoStartEnabled ?? (() => true);
        _validate = validate ?? (() => null);
    }

    public ProtectionState State => _state;

    public string? LastError { get; private set; }

    /// <summary>True after an explicit <see cref="StopAsync"/>; automatic starts and config changes then leave protection off.</summary>
    public bool StoppedByUser { get; private set; }

    /// <summary>When a pause will resume protection (<see cref="ProtectionPause.Indefinite"/> for "until resumed"), or null.</summary>
    public DateTime? PausedUntilUtc { get; private set; }

    public DateTime? NextRunUtc => _state is ProtectionState.Stopped or ProtectionState.Error ? null : _scheduler?.NextRunUtc;

    /// <summary>True when <see cref="NextRunUtc"/> is late only because the backup window ("quiet hours") is closed until then.</summary>
    public bool NextRunHeldByWindow => NextRunUtc != null && _scheduler?.NextRunHeldByWindow == true;

    public event EventHandler? StateChanged;

    /// <summary>User-initiated start (also clears a user stop and any pause).</summary>
    public async Task StartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelPause();
            StoppedByUser = false;
            await StartCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>User-initiated stop. Cancels in-flight work; <paramref name="timeout"/> bounds how long to wait for it.</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelPause();
            StoppedByUser = true;
            await StopCoreAsync(timeout ?? DefaultStopTimeout).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stop for app exit or sign-out: like <see cref="StopAsync"/> but not remembered as a user choice.</summary>
    public async Task ShutdownAsync(TimeSpan? timeout = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelPause(forget: false);
            await StopCoreAsync(timeout ?? DefaultStopTimeout).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops and starts again, so new watch directories/intervals/storage take effect.</summary>
    public async Task RestartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelPause();
            StoppedByUser = false;
            await StopCoreAsync(DefaultStopTimeout).ConfigureAwait(false);
            await StartCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Start at launch when configured to.</summary>
    public async Task AutoStartAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state != ProtectionState.Stopped || StoppedByUser || PausedUntilUtc != null || !_autoStartEnabled())
            {
                return;
            }

            await StartCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Call after a configuration save that touches watch directories, intervals, storage or encryption.
    /// Restarts if protection is active, starts it if auto-start now applies (e.g. first directory just
    /// added), and leaves it alone if the user stopped it or paused it.
    /// </summary>
    public async Task ApplyConfigChangeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (StoppedByUser || PausedUntilUtc != null)
            {
                return;
            }

            if (_watcher != null)
            {
                if (_fingerprint != null && _runningFingerprint == SafeFingerprint())
                {
                    return;
                }

                await StopCoreAsync(DefaultStopTimeout).ConfigureAwait(false);
                await StartCoreAsync().ConfigureAwait(false);
            }
            else if (_autoStartEnabled())
            {
                await StartCoreAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops protection now and resumes it after <paramref name="duration"/>.</summary>
    public Task PauseFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return PauseUntilAsync(DateTime.UtcNow + duration);
    }

    /// <summary>Stops protection now and resumes it at <paramref name="untilUtc"/>; <see cref="ProtectionPause.Indefinite"/> waits for <see cref="StartAsync"/>.</summary>
    public async Task PauseUntilAsync(DateTime untilUtc)
    {
        var indefinite = ProtectionPause.IsIndefinite(untilUtc);
        if (!indefinite && untilUtc <= DateTime.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(untilUtc));
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CancelPause();
            await StopCoreAsync(DefaultStopTimeout).ConfigureAwait(false);
            BeginPause(indefinite ? ProtectionPause.Indefinite : untilUtc);
            _logger.Log($"Protection paused {ProtectionPause.Describe(PausedUntilUtc!.Value)}.", LogLevel.Info);
            NotifyChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// At launch: re-enters a pause saved by a previous run (so a restart does not silently end it), or
    /// forgets one that has expired. Call before <see cref="AutoStartAsync"/>.
    /// </summary>
    public async Task RestorePauseAsync()
    {
        if (_pauseStore == null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_state != ProtectionState.Stopped || PausedUntilUtc != null || StoppedByUser || _pauseStore.Load() is not { } until)
            {
                return;
            }

            if (!ProtectionPause.IsIndefinite(until) && until <= DateTime.UtcNow)
            {
                _pauseStore.Clear();
                return;
            }

            BeginPause(until);
            _logger.Log($"Protection still paused {ProtectionPause.Describe(until)} (restored).", LogLevel.Info);
            NotifyChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Brings the controller in line with the stored pause (<c>state/protection_pause.json</c>), which another process may have
    /// written: a pause that appeared stops protection and starts the pause, a pause that was removed or changed by
    /// <c>restore resume</c> / <c>restore pause</c> ends or replaces the current one (a resume starts protection unless the user
    /// stopped it). Cheap and idempotent; the app calls it on a timer.
    /// </summary>
    public async Task SyncPauseFromStoreAsync()
    {
        if (_pauseStore == null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            // Read under the gate: this controller's own pause and resume write the file inside it.
            var stored = _pauseStore.LoadActive(DateTime.UtcNow);
            if (stored is not { } until)
            {
                if (PausedUntilUtc != null)
                {
                    CancelPause(forget: false);
                    _logger.Log("Protection pause was ended by another process.", LogLevel.Info);
                    if (!StoppedByUser)
                    {
                        await StartCoreAsync().ConfigureAwait(false);
                    }
                }

                return;
            }

            if (PausedUntilUtc is { } current && Math.Abs((current - until).TotalSeconds) < 1)
            {
                return;
            }

            CancelPause(forget: false);
            await StopCoreAsync(DefaultStopTimeout).ConfigureAwait(false);
            BeginPause(until);
            _logger.Log($"Protection paused {ProtectionPause.Describe(until)} (set by another process).", LogLevel.Info);
            NotifyChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds the gate and has already cancelled any previous pause.
    private void BeginPause(DateTime untilUtc)
    {
        PausedUntilUtc = untilUtc;
        try
        {
            _pauseStore?.Save(untilUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Log($"Could not remember the pause across restarts: {ex.Message}", LogLevel.Warning);
        }

        if (!ProtectionPause.IsIndefinite(untilUtc))
        {
            var cts = new CancellationTokenSource();
            _pauseCts = cts;
            _ = ResumeAfterAsync(untilUtc, cts);
        }
    }

    /// <summary>A backup needed the encryption password and none was available in the background.</summary>
    public void MarkNeedsPassword()
    {
        if (_state == ProtectionState.Running)
        {
            SetState(ProtectionState.PausedNeedsPassword);
        }
    }

    /// <summary>The password became available again.</summary>
    public void MarkPasswordAvailable()
    {
        if (_state == ProtectionState.PausedNeedsPassword)
        {
            SetState(ProtectionState.Running);
        }
    }

    private async Task ResumeAfterAsync(DateTime untilUtc, CancellationTokenSource cts)
    {
        try
        {
            // Task.Delay caps at ~49 days and a long wait should survive a clock change, so wait in slices.
            while (true)
            {
                var remaining = untilUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(remaining < MaxPauseSlice ? remaining : MaxPauseSlice, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A start/stop/restart in the meantime replaced or cancelled this pause.
            if (!ReferenceEquals(_pauseCts, cts) || cts.IsCancellationRequested || _disposed)
            {
                return;
            }

            _pauseCts = null;
            PausedUntilUtc = null;
            cts.Dispose();
            ClearStoredPause();
            await StartCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="forget">False at shutdown: the saved pause must outlive the process.</param>
    private void CancelPause(bool forget = true)
    {
        if (_pauseCts != null)
        {
            _pauseCts.Cancel();
            _pauseCts.Dispose();
            _pauseCts = null;
        }

        var wasPaused = PausedUntilUtc != null;
        PausedUntilUtc = null;
        if (forget)
        {
            ClearStoredPause();
        }

        if (wasPaused)
        {
            NotifyChanged();
        }
    }

    private void ClearStoredPause()
    {
        try
        {
            _pauseStore?.Clear();
        }
        catch (Exception ex)
        {
            _logger.Log($"Could not clear the saved pause: {ex.Message}", LogLevel.Warning);
        }
    }

    private async Task StartCoreAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_watcher != null)
        {
            return;
        }

        var problem = SafeValidate();
        if (problem != null)
        {
            Fail($"Cannot start protection: {problem}");
            return;
        }

        IProtectionWorker? watcher = null;
        IProtectionWorker? scheduler = null;
        try
        {
            watcher = _createWatcher();
            await watcher.StartAsync().ConfigureAwait(false);

            scheduler = _createScheduler();
            await scheduler.StartAsync().ConfigureAwait(false);

            if (_carriedPending != null)
            {
                watcher.AdoptPendingChanges(_carriedPending);
                _carriedPending = null;
            }

            _runningFingerprint = SafeFingerprint();

            _watcher = watcher;
            _scheduler = scheduler;
            LastError = null;
            _logger.Log("Protection started.", LogLevel.Info);
            SetState(ProtectionState.Running);
        }
        catch (Exception ex)
        {
            await SafeStopAsync(scheduler, DefaultStopTimeout).ConfigureAwait(false);
            await SafeStopAsync(watcher, DefaultStopTimeout).ConfigureAwait(false);
            _watcher = null;
            _scheduler = null;
            Fail($"Failed to start protection: {ex.Message}");
        }
    }

    private async Task StopCoreAsync(TimeSpan timeout)
    {
        var watcher = _watcher;
        var scheduler = _scheduler;
        _watcher = null;
        _scheduler = null;

        if (watcher == null && scheduler == null)
        {
            if (_state != ProtectionState.Stopped)
            {
                SetState(ProtectionState.Stopped);
            }

            return;
        }

        // Scheduler first: it may be mid-backup, and both honour cancellation on stop.
        await Task.WhenAll(SafeStopAsync(scheduler, timeout), SafeStopAsync(watcher, timeout)).ConfigureAwait(false);

        // Changes the old watcher had not backed up yet go to the next one rather than being dropped.
        try
        {
            _carriedPending = watcher?.TakePendingChanges() ?? _carriedPending;
        }
        catch (Exception ex)
        {
            _logger.Log($"Could not carry over pending changes: {ex.Message}", LogLevel.Warning);
        }

        LastError = null;
        _logger.Log("Protection stopped.", LogLevel.Info);
        SetState(ProtectionState.Stopped);
    }

    private async Task SafeStopAsync(IProtectionWorker? worker, TimeSpan timeout)
    {
        if (worker == null)
        {
            return;
        }

        try
        {
            var stop = worker.StopAsync();
            var finished = await Task.WhenAny(stop, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished != stop)
            {
                _logger.Log($"Protection component did not stop within {timeout}; continuing shutdown.", LogLevel.Warning);
                // Observe a late fault so it is not reported as unobserved.
                _ = stop.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return;
            }

            await stop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Log($"Error stopping protection component: {ex.Message}", LogLevel.Warning);
        }
    }

    private string? SafeFingerprint()
    {
        try
        {
            return _fingerprint?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Log($"Could not fingerprint configuration: {ex.Message}", LogLevel.Warning);
            return null;
        }
    }

    private string? SafeValidate()
    {
        try
        {
            return _validate();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void Fail(string message)
    {
        LastError = message;
        _logger.Log(message, LogLevel.Error);
        SetState(ProtectionState.Error);
    }

    private void SetState(ProtectionState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        NotifyChanged();
    }

    /// <summary>Also raised when only the pause changed (state stays Stopped).</summary>
    private void NotifyChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A faulty subscriber must not break protection's own transitions.
            _logger.Log($"Protection state subscriber failed: {ex.Message}", LogLevel.Warning);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            CancelPause(forget: false);
            await StopCoreAsync(DefaultStopTimeout).ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}

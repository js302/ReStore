using ReStore.Core.src.utils;

namespace ReStore.Core.src.core;

/// <summary>Thrown when a repository operation cannot start because another one holds the repository.</summary>
public sealed class RepositoryBusyException(string message) : IOException(message)
{
}

public enum RepositoryLeaseKind
{
    /// <summary>Exclusive among mutations (backup, system backup, label/edit, delete): one at a time per process and, via a lock file, per machine.</summary>
    Mutation,

    /// <summary>Shared with other readers (restore, verify, preview); excludes <see cref="Delete"/>.</summary>
    Read,

    /// <summary>Exclusive against readers: removes chunks or snapshots from storage (retention, delete, repair).</summary>
    Delete
}

/// <summary>A held repository lease. Dispose to release; disposing twice is harmless.</summary>
public sealed class RepositoryLease : IAsyncDisposable
{
    private Func<ValueTask>? _release;

    internal RepositoryLease(RepositoryLeaseKind kind, string reason, Func<ValueTask> release)
    {
        Kind = kind;
        Reason = reason;
        _release = release;
    }

    public RepositoryLeaseKind Kind { get; }
    public string Reason { get; }

    public ValueTask DisposeAsync()
    {
        var release = Interlocked.Exchange(ref _release, null);
        return release?.Invoke() ?? ValueTask.CompletedTask;
    }
}

/// <summary>
/// Serialises everything that can change what a repository holds, so that for example retention can
/// not delete a chunk an in-flight backup has decided to reuse, and the CLI and GUI cannot overwrite
/// each other's <c>system_state.json</c>.
/// <list type="bullet">
/// <item><b>Mutation</b> lease: one at a time per process, plus a cross-process lock file
/// (<c>repository.lock</c> in <see cref="AppPaths.StateDirectory"/>, <see cref="FileShare.None"/>; the OS
/// releases it if the process dies). Once the file lock is held the optional <see cref="SystemState"/> is
/// reloaded if another process changed it.</item>
/// <item><b>Read</b> / <b>Delete</b> leases: an async reader/writer lock within the process, mirrored across
/// processes by a second file (<c>repository.read.lock</c>): readers (restore, verify, preview) open it shared
/// (<see cref="FileAccess.Read"/> + <see cref="FileShare.Read"/>), the delete lease (retention, delete, repair)
/// opens it exclusive (<see cref="FileShare.None"/>). Readers never touch the mutation lock, so they do not
/// wait behind a running backup.</item>
/// </list>
/// Cross-process protections: mutations are exclusive machine-wide, and chunk deletion is exclusive against
/// restores/verifies in any ReStore process. In-process only: the FIFO fairness of the gate and
/// <see cref="IsBusy"/>/<see cref="BusyChanged"/>.
/// Leases are NOT re-entrant. A path that already holds a lease must pass it down (see
/// <c>RetentionManager.ApplyGroupAsync(string, RepositoryLease)</c>) rather than acquire again, or it
/// deadlocks. Lock order is mutation then delete; never take mutation while holding a read lease (a
/// backup waiting to delete would wait for that reader forever).
/// </summary>
public sealed class RepositoryCoordinator
{
    /// <summary>Default wait for the cross-process lock for interactive (blocking) acquisitions.</summary>
    public static readonly TimeSpan DefaultCrossProcessTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>The process-wide coordinator used by production entry points.</summary>
    public static RepositoryCoordinator Instance { get; } = new();

    private readonly string? _lockFilePathOverride;
    private long _crossProcessTimeoutTicks;
    private volatile Action<string>? _waitNotifier;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly AsyncReadWriteGate _readWrite = new();
    private readonly Lock _busyLock = new();
    private string? _currentReason;

    /// <param name="lockFilePath">Lock file location; null uses <c>AppPaths.StateDirectory/repository.lock</c>
    /// resolved at acquisition time. Tests pass their own so instances do not contend.</param>
    /// <param name="crossProcessTimeout">How long a blocking mutation waits for another process; null for the default.</param>
    public RepositoryCoordinator(string? lockFilePath = null, TimeSpan? crossProcessTimeout = null)
    {
        _lockFilePathOverride = lockFilePath;
        _crossProcessTimeoutTicks = (crossProcessTimeout ?? DefaultCrossProcessTimeout).Ticks;
    }

    /// <summary>How long blocking acquisitions wait for another process (see <see cref="ConfigureWait"/>).</summary>
    public TimeSpan CrossProcessTimeout => TimeSpan.FromTicks(Interlocked.Read(ref _crossProcessTimeoutTicks));

    /// <summary>
    /// Changes how long blocking acquisitions wait for another ReStore process (the CLI's <c>--wait</c>; zero fails at once)
    /// and sets an optional callback that receives one line, once per acquisition, when it starts waiting. Safe to call
    /// while operations run: it affects acquisitions that start afterwards.
    /// </summary>
    public void ConfigureWait(TimeSpan crossProcessTimeout, Action<string>? onWaiting = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(crossProcessTimeout.Ticks);
        Interlocked.Exchange(ref _crossProcessTimeoutTicks, crossProcessTimeout.Ticks);
        _waitNotifier = onWaiting;
    }

    /// <summary>True while a mutation lease is held in this process.</summary>
    public bool IsBusy => CurrentReason != null;

    /// <summary>Description of the operation holding the mutation lease, or null.</summary>
    public string? CurrentReason
    {
        get
        {
            lock (_busyLock)
            {
                return _currentReason;
            }
        }
    }

    /// <summary>Raised (on an arbitrary thread, outside internal locks) when <see cref="IsBusy"/> changes.</summary>
    public event EventHandler? BusyChanged;

    /// <summary>
    /// Waits for the mutation lease (and the cross-process lock, up to the configured timeout), then
    /// reloads <paramref name="state"/> if another process changed it.
    /// </summary>
    /// <exception cref="RepositoryBusyException">Another ReStore process holds the lock for longer than the timeout.</exception>
    public async Task<RepositoryLease> AcquireMutationAsync(
        string reason,
        CancellationToken cancellationToken = default,
        SystemState? state = null)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await CompleteMutationAsync(reason, CrossProcessTimeout, state, cancellationToken, announce: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Fast-fail variant for the watcher and scheduler: returns null (rather than waiting) if the mutation
    /// lease or the cross-process lock is not available within <paramref name="timeout"/>.
    /// </summary>
    public async Task<RepositoryLease?> TryAcquireMutationAsync(
        TimeSpan timeout,
        string reason,
        CancellationToken cancellationToken = default,
        SystemState? state = null)
    {
        if (!await _mutationGate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            // Best effort: a caller that can simply skip the work does not announce that it is waiting.
            return await CompleteMutationAsync(reason, timeout, state, cancellationToken, announce: false).ConfigureAwait(false);
        }
        catch (RepositoryBusyException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a small state edit under the mutation lease: takes the lease (reloading <paramref name="state"/>
    /// if another process changed it), calls <paramref name="mutate"/>, and saves if it returns true.
    /// Used for label/edit style changes that have no storage side effects. Returns what <paramref name="mutate"/> returned.
    /// </summary>
    public async Task<bool> MutateStateAsync(
        SystemState state,
        string reason,
        Func<bool> mutate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(mutate);

        var lease = await AcquireMutationAsync(reason, cancellationToken, state).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        if (!mutate())
        {
            return false;
        }

        await state.SaveStateAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Shared lease for restore, verify and preview. Waits only while a delete lease is held or queued.</summary>
    public async Task<RepositoryLease> AcquireReadAsync(string reason, CancellationToken cancellationToken = default)
    {
        await _readWrite.AcquireAsync(writer: false, cancellationToken).ConfigureAwait(false);
        var fileLock = await AcquireGateFileLockAsync(
            writer: false, "A delete or retention is running in another ReStore process. Wait for it to finish and try again.", cancellationToken).ConfigureAwait(false);
        return new RepositoryLease(RepositoryLeaseKind.Read, reason, () =>
        {
            fileLock.Dispose();
            _readWrite.Release(writer: false);
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// Exclusive lease for operations that delete chunks or snapshots; waits for running readers. A backup
    /// takes this after its mutation lease, for its retention phase.
    /// </summary>
    public async Task<RepositoryLease> AcquireDeleteAsync(string reason, CancellationToken cancellationToken = default)
    {
        await _readWrite.AcquireAsync(writer: true, cancellationToken).ConfigureAwait(false);
        var fileLock = await AcquireGateFileLockAsync(
            writer: true, "A restore or verify is running in another ReStore process. Wait for it to finish and try again.", cancellationToken).ConfigureAwait(false);
        return new RepositoryLease(RepositoryLeaseKind.Delete, reason, () =>
        {
            fileLock.Dispose();
            _readWrite.Release(writer: true);
            return ValueTask.CompletedTask;
        });
    }

    // Called with the in-process gate held; releases it again if the cross-process lock cannot be taken.
    private async Task<FileStream> AcquireGateFileLockAsync(bool writer, string busyMessage, CancellationToken cancellationToken)
    {
        try
        {
            return await AcquireFileLockAsync(
                ReadLockPath, FileAccess.Read, writer ? FileShare.None : FileShare.Read, CrossProcessTimeout, busyMessage, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _readWrite.Release(writer);
            throw;
        }
    }

    private string LockPath => _lockFilePathOverride ?? Path.Combine(AppPaths.StateDirectory, "repository.lock");

    private string ReadLockPath => _lockFilePathOverride != null
        ? _lockFilePathOverride + ".read"
        : Path.Combine(AppPaths.StateDirectory, "repository.read.lock");

    // Called with _mutationGate held; releases it again on any failure.
    private async Task<RepositoryLease> CompleteMutationAsync(
        string reason,
        TimeSpan lockTimeout,
        SystemState? state,
        CancellationToken cancellationToken,
        bool announce)
    {
        FileStream? acquired = null;
        try
        {
            acquired = await AcquireFileLockAsync(
                LockPath,
                FileAccess.ReadWrite,
                FileShare.None,
                lockTimeout,
                "Another ReStore process (CLI or GUI) is running an operation on this repository. Wait for it to finish and try again.",
                cancellationToken,
                announce).ConfigureAwait(false);

            if (state != null)
            {
                await state.ReloadIfChangedAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            acquired?.Dispose();
            _mutationGate.Release();
            throw;
        }

        var lockStream = acquired;

        SetReason(reason);
        return new RepositoryLease(RepositoryLeaseKind.Mutation, reason, () =>
        {
            // The gate must be released whatever a BusyChanged subscriber does.
            try
            {
                lockStream.Dispose();
            }
            finally
            {
                try
                {
                    SetReason(null);
                }
                finally
                {
                    _mutationGate.Release();
                }
            }

            return ValueTask.CompletedTask;
        });
    }

    private async Task<FileStream> AcquireFileLockAsync(
        string path, FileAccess access, FileShare share, TimeSpan timeout, string busyMessage, CancellationToken cancellationToken, bool announce = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(25);
        var announced = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, access, share, 1, FileOptions.None);
            }
            catch (IOException)
            {
                // Held by another process (sharing violation / flock conflict).
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new RepositoryBusyException(busyMessage);
            }

            if (announce && !announced)
            {
                announced = true;
                AnnounceWaiting(busyMessage);
            }

            var wait = delay < remaining ? delay : remaining;
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private void AnnounceWaiting(string busyMessage)
    {
        if (_waitNotifier is not { } notify)
        {
            return;
        }

        var holder = CurrentReason;
        try
        {
            notify(holder == null ? busyMessage : $"{busyMessage} (in this process: {holder})");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A notification failure must not turn into a lock failure.
        }
    }

    private void SetReason(string? reason)
    {
        bool changed;
        lock (_busyLock)
        {
            changed = (_currentReason == null) != (reason == null);
            _currentReason = reason;
        }

        if (!changed || BusyChanged is not { } handlers)
        {
            return;
        }

        // One failing subscriber (typically UI code) must neither stop the others nor break the caller.
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"RepositoryCoordinator.BusyChanged subscriber failed: {ex}");
            }
        }
    }

    /// <summary>Async reader/writer gate with FIFO fairness (a queued writer blocks later readers).</summary>
    private sealed class AsyncReadWriteGate
    {
        private readonly object _sync = new();
        private readonly LinkedList<Waiter> _queue = new();
        private int _readers;
        private bool _writerActive;

        private sealed class Waiter(bool writer)
        {
            public bool Writer { get; } = writer;
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task AcquireAsync(bool writer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LinkedListNode<Waiter> node;
            lock (_sync)
            {
                if (_queue.Count == 0 && CanGrant(writer))
                {
                    Grant(writer);
                    return Task.CompletedTask;
                }

                node = _queue.AddLast(new Waiter(writer));
            }

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() =>
                {
                    lock (_sync)
                    {
                        // Already granted if it is no longer queued.
                        if (node.List == null)
                        {
                            return;
                        }

                        _queue.Remove(node);
                        node.Value.Completion.TrySetCanceled(cancellationToken);
                        Pump();
                    }
                });
                node.Value.Completion.Task.ContinueWith(
                    _ => registration.Dispose(), TaskScheduler.Default);
            }

            return node.Value.Completion.Task;
        }

        public void Release(bool writer)
        {
            lock (_sync)
            {
                if (writer)
                {
                    _writerActive = false;
                }
                else
                {
                    _readers--;
                }

                Pump();
            }
        }

        private bool CanGrant(bool writer) => writer ? !_writerActive && _readers == 0 : !_writerActive;

        private void Grant(bool writer)
        {
            if (writer)
            {
                _writerActive = true;
            }
            else
            {
                _readers++;
            }
        }

        // Caller holds _sync.
        private void Pump()
        {
            while (_queue.First is { } first && CanGrant(first.Value.Writer))
            {
                _queue.RemoveFirst();
                Grant(first.Value.Writer);
                first.Value.Completion.TrySetResult();
            }
        }
    }
}

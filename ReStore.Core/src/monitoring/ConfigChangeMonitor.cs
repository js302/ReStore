using ReStore.Core.src.utils;

namespace ReStore.Core.src.monitoring;

/// <summary>
/// Raises <see cref="Changed"/> (debounced) when config.json is touched. It only says "look again": the owner decides with
/// <see cref="ConfigManager.HasChangedOnDiskAsync"/> whether the content really differs from what it loaded or wrote itself, so the
/// process's own saves (and editors that touch a file without changing it) cause no reload.
/// </summary>
public sealed class ConfigChangeMonitor : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(800);

    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _timer;
    private readonly TimeSpan _debounce;
    private readonly ILogger? _logger;
    private bool _disposed;

    public ConfigChangeMonitor(string configPath, TimeSpan? debounce = null, ILogger? logger = null)
    {
        _debounce = debounce ?? DefaultDebounce;
        _logger = logger;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            var full = Path.GetFullPath(configPath);
            var directory = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(full))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            _watcher.Changed += (_, _) => Poke();
            _watcher.Created += (_, _) => Poke();
            _watcher.Renamed += (_, _) => Poke();
            _watcher.Deleted += (_, _) => Poke();
            _watcher.Error += (_, e) =>
            {
                _logger?.Log($"Watching config.json failed: {e.GetException().Message}", LogLevel.Debug);
                Poke();
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Without a watcher the owner still checks on its own triggers (window activation, a timer).
            _logger?.Log($"Could not watch config.json for outside changes: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>Raised on a thread-pool thread, once per burst of file events (or <see cref="Poke"/> calls).</summary>
    public event Action? Changed;

    /// <summary>Asks for a check after the debounce time: used by the file events and for "the window was activated".</summary>
    public void Poke()
    {
        if (!_disposed)
        {
            try
            {
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Disposed while an event was in flight.
            }
        }
    }

    /// <summary>Completes at the next <see cref="Changed"/>.</summary>
    public async Task WaitForChangeAsync(CancellationToken cancellationToken)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler() => signal.TrySetResult();
        Changed += Handler;
        try
        {
            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Changed -= Handler;
        }
    }

    private void Fire()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.Log($"A config change handler failed: {ex.Message}", LogLevel.Warning);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _timer.Dispose();
    }
}

/// <summary>What the app does when config.json changed outside it.</summary>
public enum ExternalConfigAction
{
    /// <summary>Nothing changed on disk, or nothing to decide.</summary>
    None,

    /// <summary>Settings has no unsaved edits: take the new file silently.</summary>
    ReloadSilently,

    /// <summary>Settings has unsaved edits: ask (banner with Reload / Keep my edits) and never replace them unasked.</summary>
    AskUser
}

public static class ExternalConfigChangePolicy
{
    public const string BannerText = "config.json was changed outside ReStore (e.g. by the CLI)";

    public static ExternalConfigAction Decide(bool changedOnDisk, bool settingsHaveUnsavedEdits) =>
        !changedOnDisk ? ExternalConfigAction.None
        : settingsHaveUnsavedEdits ? ExternalConfigAction.AskUser
        : ExternalConfigAction.ReloadSilently;
}

public enum ConfigReloadOutcome
{
    /// <summary>The file is what this process already has.</summary>
    Unchanged,

    /// <summary>Reloaded; nothing the running protection binds to changed (no restart needed).</summary>
    Reloaded,

    /// <summary>Reloaded; the protection fingerprint (folders, schedules, storage, encryption...) changed, so workers should restart.</summary>
    ReloadedRestartNeeded,

    /// <summary>The file on disk is not valid (mid-edit, broken): the running configuration was kept.</summary>
    Invalid
}

public sealed record ConfigReloadResult(ConfigReloadOutcome Outcome, string? Error = null);

public static class ConfigReloader
{
    /// <summary>
    /// Reloads <paramref name="manager"/> when config.json changed on disk and the new file loads and validates; an unusable file
    /// leaves the running configuration alone. Says whether the protection fingerprint changed, so the caller restarts only then.
    /// </summary>
    public static async Task<ConfigReloadResult> ReloadIfChangedAsync(IConfigManager config)
    {
        if (config is not ConfigManager manager || !await manager.HasChangedOnDiskAsync().ConfigureAwait(false))
        {
            return new ConfigReloadResult(ConfigReloadOutcome.Unchanged);
        }

        var before = ProtectionFingerprint.Compute(manager);

        // A throwaway manager first: a half-written or invalid file must not replace the working configuration.
        try
        {
            var silent = new ConfigPortability.SilentLogger();
            var probe = new ConfigManager(silent, manager.GetConfigFilePath());
            await probe.LoadAsync().ConfigureAwait(false);
            var validation = new ConfigValidator(silent).ValidateConfiguration(probe, validateWatchDirectories: false);
            if (validation.Errors.Count > 0)
            {
                return new ConfigReloadResult(ConfigReloadOutcome.Invalid, validation.Errors[0]);
            }

            if (!probe.StorageSources.ContainsKey(probe.GlobalStorageType))
            {
                return new ConfigReloadResult(ConfigReloadOutcome.Invalid, $"globalStorageType '{probe.GlobalStorageType}' is not a key of storageSources.");
            }

            await manager.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new ConfigReloadResult(ConfigReloadOutcome.Invalid, ex.InnerException?.Message ?? ex.Message);
        }

        return new ConfigReloadResult(before == ProtectionFingerprint.Compute(manager) ? ConfigReloadOutcome.Reloaded : ConfigReloadOutcome.ReloadedRestartNeeded);
    }
}

/// <summary>
/// The app's reaction to config.json being edited outside it (the CLI, the service, a text editor): reload silently while Settings
/// has no unsaved edits, otherwise raise <see cref="PendingChanged"/> so the UI can offer "Reload / Keep my edits". After a reload it
/// applies the change to protection, which restarts only when its fingerprint changed. UI-free so it can be tested.
/// </summary>
public sealed class ConfigExternalChangeCoordinator(
    ConfigManager manager,
    Func<bool> settingsHaveUnsavedEdits,
    Func<Task> applyProtectionChange,
    ILogger logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _keptHash;

    /// <summary>True while an external change waits for the user's decision.</summary>
    public bool Pending { get; private set; }

    /// <summary>Raised when <see cref="Pending"/> changes (true: show the banner, false: hide it).</summary>
    public event Action<bool>? PendingChanged;

    /// <summary>Raised after the configuration was reloaded from disk, so pages can refresh what they show.</summary>
    public event Action? Reloaded;

    /// <summary>Looks at config.json (call on a file event, on window activation, or both); does nothing when it is what we have.</summary>
    public async Task CheckAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var disk = await manager.ReadDiskSignatureAsync().ConfigureAwait(false);
            var changed = disk != null && !disk.SameContent(manager.LoadedSignature) && disk.Hash != _keptHash;
            switch (ExternalConfigChangePolicy.Decide(changed, settingsHaveUnsavedEdits()))
            {
                case ExternalConfigAction.ReloadSilently:
                    await ReloadLockedAsync().ConfigureAwait(false);
                    break;
                case ExternalConfigAction.AskUser:
                    SetPending(true);
                    break;
                default:
                    if (!changed)
                    {
                        SetPending(false);
                    }

                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The "Reload" button: take the file from disk, dropping what this app holds for it.</summary>
    public async Task ReloadNowAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _keptHash = null;
            await ReloadLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The "Keep my edits" button: stop asking about this version of the file; saving later merges the outside changes in.</summary>
    public async Task KeepMyEditsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _keptHash = (await manager.ReadDiskSignatureAsync().ConfigureAwait(false))?.Hash;
            SetPending(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReloadLockedAsync()
    {
        var result = await ConfigReloader.ReloadIfChangedAsync(manager).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case ConfigReloadOutcome.Invalid:
                logger.Log($"config.json was changed outside ReStore but cannot be used yet ({result.Error}); keeping the current settings.", LogLevel.Warning);
                break;
            case ConfigReloadOutcome.Unchanged:
                SetPending(false);
                break;
            default:
                logger.Log("config.json was changed outside ReStore (for example by the CLI); the new settings were loaded.", LogLevel.Info);
                SetPending(false);
                try
                {
                    await applyProtectionChange().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.Log($"Applying the reloaded configuration to protection failed: {ex.Message}", LogLevel.Warning);
                }

                Reloaded?.Invoke();
                break;
        }
    }

    private void SetPending(bool pending)
    {
        if (Pending == pending)
        {
            return;
        }

        Pending = pending;
        PendingChanged?.Invoke(pending);
    }
}

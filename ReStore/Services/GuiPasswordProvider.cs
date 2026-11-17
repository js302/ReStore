using ReStore.Core.src.utils;
using System.Windows;

namespace ReStore.Services;

/// <summary>
/// Thin WPF adapter over <see cref="VerifiedPasswordProvider"/>: all verification, caching and migration
/// decisions live in Core; this class only shows the dialogs on the UI thread.
/// </summary>
public class GuiPasswordProvider(bool promptUser = true) : IPurposePasswordProvider, ILogger
{
    private static readonly TimeSpan TrayNoticeInterval = TimeSpan.FromMinutes(10);

    private readonly bool _promptUser = promptUser;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private VerifiedPasswordProvider? _inner;
    private string? _pendingSeed;
    private DateTime _lastTrayNoticeUtc = DateTime.MinValue;

    /// <summary>Raised when a background run needed the encryption password and could not ask for it.</summary>
    public event Action? BackgroundUnlockNeeded;

    /// <summary>Raised whenever an encryption password was successfully obtained.</summary>
    public event Action? Unlocked;

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        System.Diagnostics.Trace.WriteLine($"[{level}] {message}");
    }

    /// <summary>Seeds the encryption-mode cache with a password that was just set up (wizard/Settings).</summary>
    public void SetPassword(string? password)
    {
        if (_inner != null)
        {
            _inner.SetPassword(password);
            if (!string.IsNullOrEmpty(password))
            {
                Unlocked?.Invoke();
            }
        }
        else
        {
            _pendingSeed = password;
        }
    }

    public Task<string?> GetPasswordAsync() => GetPasswordAsync(PasswordPurpose.Encrypt);

    public async Task<string?> GetPasswordAsync(PasswordPurpose purpose)
    {
        var inner = await GetInnerAsync();

        if (purpose == PasswordPurpose.Encrypt && !inner.IsPasswordSet() && NoWindowVisible())
        {
            // Background run with the app hidden in the tray: an invisible modal would just hang the backup.
            BackgroundUnlockNeeded?.Invoke();
            ShowTrayUnlockNotice();
            return null;
        }

        var password = await inner.GetPasswordAsync(purpose);
        if (purpose == PasswordPurpose.Encrypt && password != null)
        {
            Unlocked?.Invoke();
        }

        return password;
    }

    public bool IsPasswordSet() => _inner?.IsPasswordSet() ?? !string.IsNullOrEmpty(_pendingSeed);

    public void ClearPassword()
    {
        _inner?.ClearPassword();
    }

    private async Task<VerifiedPasswordProvider> GetInnerAsync()
    {
        if (_inner != null)
        {
            return _inner;
        }

        await _initGate.WaitAsync();
        try
        {
            if (_inner == null)
            {
                var config = await AppServices.GetConfigManagerAsync();
                var inner = new VerifiedPasswordProvider(config, this, PromptOnUiThreadAsync, ShowNotice);
                if (!string.IsNullOrEmpty(_pendingSeed))
                {
                    inner.SetPassword(_pendingSeed);
                    _pendingSeed = null;
                }

                _inner = inner;
            }

            return _inner;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task<string?> PromptOnUiThreadAsync(PasswordPromptRequest request)
    {
        if (!_promptUser)
        {
            return null;
        }

        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Views.Windows.PasswordPromptWindow(
                request.Purpose == PasswordPurpose.Encrypt,
                request.IsConfirmation);
            return dialog.ShowDialog() == true ? dialog.Password : null;
        });
    }

    private static void ShowNotice(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
            MessageBox.Show(message, "Password", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    private static bool NoWindowVisible()
    {
        return Application.Current.Dispatcher.Invoke(() => Application.Current.MainWindow is not { IsVisible: true });
    }

    private void ShowTrayUnlockNotice()
    {
        var now = DateTime.UtcNow;
        if (now - _lastTrayNoticeUtc < TrayNoticeInterval)
        {
            return;
        }

        _lastTrayNoticeUtc = now;
        // Through the notification service so it also works without a tray icon, honours Focus Assist
        // and opens Settings when clicked.
        if (App.Protection is { } protection)
        {
            protection.NotifyUnlockNeeded();
        }
    }
}

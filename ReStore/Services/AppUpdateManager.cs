using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ReStore.Core.src.core;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Services
{
    /// <summary>
    /// GUI side of the update feature (see docs/updates.md): checks about a minute after startup and then hourly (the
    /// service throttles automatic checks to once a day), exposes the pending update to the Dashboard / Settings / About pages,
    /// raises one toast per version and runs "Update now" for MSIX installs. UI-thread object.
    /// </summary>
    public sealed class AppUpdateManager
    {
        private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan TickInterval = TimeSpan.FromHours(1);

        private readonly UpdateService _service = new();
        private DispatcherTimer? _timer;
        private string? _dismissedVersion;
        private bool _checking;

        public static AppUpdateManager Instance { get; } = new();

        /// <summary>The pending update or the install state changed; handlers run on the UI thread.</summary>
        public event Action? Changed;

        public bool IsInstalling { get; private set; }

        /// <summary>Download progress text while <see cref="IsInstalling"/>.</summary>
        public string? InstallStatus { get; private set; }

        /// <summary>The newer, non-skipped version found by the last check, or null.</summary>
        public UpdateCheckResult? Pending => _service.GetPendingUpdate();

        /// <summary>The pending update unless the user closed the banner for it ("remind me later" lasts until the app restarts).</summary>
        public UpdateCheckResult? PendingForBanner => Pending is { } pending && !string.Equals(pending.Latest, _dismissedVersion, StringComparison.Ordinal) ? pending : null;

        public bool IsMsix => AppChannel.Current == AppChannel.Msix;

        public bool CanInstall => IsMsix && Pending is { Asset: not null };

        public DateTime? LastCheckUtc => _service.Cache.Load().LastCheckUtc;

        /// <summary>One line for the Settings and About pages.</summary>
        public string DescribeStatus()
        {
            if (IsInstalling)
            {
                return InstallStatus ?? "Installing the update...";
            }

            var cached = _service.GetCachedResult();
            var checkedText = LastCheckUtc is { } at ? $" Last checked {DisplayTime.Format(at)}." : string.Empty;
            return cached switch
            {
                { Status: UpdateStatus.UpdateAvailable } when Pending == null => $"Version {cached.Latest} is available (skipped).{checkedText}",
                { Status: UpdateStatus.UpdateAvailable } => $"Version {cached.Latest} is available.{checkedText}",
                { Status: UpdateStatus.UpToDate } => $"You have the latest version.{checkedText}",
                { Status: UpdateStatus.Failed } => $"The last check failed: {cached.Error}.{checkedText}",
                _ => AppChannel.Current == AppChannel.Dev ? "Built from source: update by pulling and rebuilding." : "Not checked yet."
            };
        }

        /// <summary>Starts the background checks. Call once, on the UI thread, after the main window exists.</summary>
        public void Start()
        {
            if (_timer != null)
            {
                return;
            }

            _timer = new DispatcherTimer { Interval = FirstCheckDelay };
            _timer.Tick += async (_, _) =>
            {
                _timer.Interval = TickInterval;
                await CheckAsync(manual: false);
            };
            _timer.Start();
        }

        /// <summary>
        /// Checks now (<paramref name="manual"/>: always, and tells the caller about failures through the returned result) or as the
        /// background check (only when allowed and due). Never throws.
        /// </summary>
        public async Task<UpdateCheckResult?> CheckAsync(bool manual)
        {
            if (_checking || IsInstalling)
            {
                return null;
            }

            _checking = true;
            try
            {
                var config = await AppServices.GetConfigManagerAsync();
                var result = manual
                    ? await _service.CheckNowAsync(config.Updates)
                    : await _service.CheckAutomaticallyAsync(config.Updates);
                if (result != null && result.UpdateAvailable)
                {
                    _dismissedVersion = null;
                    ToastOncePerVersion();
                }

                Changed?.Invoke();
                return result;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Log($"Update check failed: {ex.Message}", LogLevel.Debug);
                return null;
            }
            finally
            {
                _checking = false;
            }
        }

        private void ToastOncePerVersion()
        {
            if (Pending is not { Latest: { } latest } pending
                || string.Equals(_service.Cache.Load().LastNotifiedVersion, latest, StringComparison.Ordinal))
            {
                return;
            }

            var notice = new UpdateNotice(latest, UpdateChecker.CurrentVersion, pending.Critical, pending.ReleaseNotesUrl);
            _service.MarkNotified(notice);
            App.Protection?.NotifyUpdateAvailable(latest, pending.Critical);
        }

        /// <summary>The banner's close button: hide it for this version until the app restarts.</summary>
        public void Dismiss()
        {
            _dismissedVersion = Pending?.Latest;
            Changed?.Invoke();
        }

        public void Skip()
        {
            if (Pending is { Latest: { } latest })
            {
                _service.Skip(latest);
                Changed?.Invoke();
            }
        }

        public void OpenReleaseNotes()
        {
            var url = Pending?.ReleaseNotesUrl;
            if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
        }

        /// <summary>Confirm, stop protection, download + verify + hand over to the installer (which relaunches the app), then exit.</summary>
        public async Task UpdateNowAsync()
        {
            if (IsInstalling || !CanInstall || Pending is not { Latest: { } latest } result)
            {
                return;
            }

            var answer = MessageBox.Show(
                $"Install ReStore {latest} now?\n\nReStore stops background protection, downloads and verifies the update, then closes and starts again automatically. "
                + "A backup that is running is cancelled.",
                "Update ReStore", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            var protection = App.Protection;
            var wasActive = protection?.IsActive == true;
            IsInstalling = true;
            InstallStatus = "Stopping protection...";
            Changed?.Invoke();
            try
            {
                if (protection != null)
                {
                    await protection.StopAsync();
                }

                InstallStatus = "Downloading the update...";
                Changed?.Invoke();
                var progress = new Progress<DownloadProgress>(p =>
                {
                    InstallStatus = p.Total > 0 ? $"Downloading the update... {p.Received * 100 / p.Total}%" : "Downloading the update...";
                    Changed?.Invoke();
                });
                var outcome = await new UpdateInstaller().InstallAsync(result, new InstallOptions(Relaunch: true, RepositoryWait: TimeSpan.FromSeconds(10)), progress);
                InstallStatus = outcome.Message;
                Changed?.Invoke();
                if (outcome.ProcessShouldExit)
                {
                    Application.Current.Shutdown();
                    return;
                }
            }
            catch (Exception ex) when (ex is UpdateInstallException or RepositoryBusyException)
            {
                MessageBox.Show(ex.Message, "ReStore update", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ErrorReporter.Report("Installing the update", ex);
            }

            IsInstalling = false;
            InstallStatus = null;
            Changed?.Invoke();
            if (wasActive && protection != null)
            {
                try
                {
                    await protection.ResumeAsync();
                }
                catch (Exception ex)
                {
                    ErrorReporter.Report("Restarting protection", ex);
                }
            }
        }
    }
}

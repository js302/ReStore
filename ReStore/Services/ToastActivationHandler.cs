using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Services
{
    /// <summary>Runs what a toast click or button asked for, inside the running instance (UI thread).</summary>
    internal static class ToastActivationHandler
    {
        public static void Handle(ToastActivation activation)
        {
            // Only toasts shown by this process carry the token. Anything else (a web page, a toast from before a
            // restart) just brings the window forward: no backup, no password prompt.
            if (!ToastActivationArgs.IsAuthorized(activation, ToastActivationArgs.CurrentToken))
            {
                AppServices.Logger.Log($"Ignored toast action '{activation.Action}' without a valid token.", LogLevel.Debug);
                AppNavigator.Open(activation.Target);
                return;
            }

            switch (activation.Action)
            {
                case ToastAction.RetryNow:
                case ToastAction.BackUpNow:
                    AppNavigator.Open(NotificationTarget.Dashboard);
                    if (App.Protection is { } protection)
                    {
                        _ = ErrorReporter.GuardAsync("Backing up now", () => protection.BackupNowAsync());
                    }
                    break;

                case ToastAction.OpenLog:
                    OpenLog();
                    break;

                case ToastAction.Unlock:
                    AppNavigator.Open(NotificationTarget.Settings);
                    if (App.GlobalPasswordProvider is { } passwords)
                    {
                        // After the window is up, so the password dialog is allowed to show; success resumes protection.
                        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
                            await ErrorReporter.GuardAsync("Unlocking ReStore", () => passwords.GetPasswordAsync(PasswordPurpose.Encrypt)));
                    }
                    break;

                case ToastAction.ViewReport:
                    AppNavigator.Open(NotificationTarget.Activity);
                    break;

                default:
                    AppNavigator.Open(activation.Target);
                    break;
            }
        }

        private static void OpenLog()
        {
            try
            {
                if (File.Exists(AppPaths.LogFile))
                {
                    Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
                    return;
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Log($"Could not open the log file: {ex.Message}", LogLevel.Warning);
            }

            AppNavigator.Open(NotificationTarget.Activity);
        }
    }
}

using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ReStore.Core.src.telemetry;
using ReStore.Views.Windows;

namespace ReStore.Services
{
    /// <summary>
    /// Asks the user, without blocking, whether the pending anonymous crash report may be sent (telemetry.crashReports = ask).
    /// Shown right after a crash the app survived and on the next start. One prompt at a time. Never throws.
    /// </summary>
    public static class CrashPromptService
    {
        private static int _open;

        /// <summary>Shows the prompt later on the UI thread when a report is pending and the user has not decided yet.</summary>
        public static void ShowSoon()
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                dispatcher?.BeginInvoke(DispatcherPriority.ApplicationIdle, (Action)ShowIfNeeded);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Crash prompt failed: {ex}");
            }
        }

        private static void ShowIfNeeded()
        {
            try
            {
                var client = Telemetry.Client;
                if (!CrashReportPrompt.ShouldAsk(client) || Interlocked.CompareExchange(ref _open, 1, 0) != 0)
                {
                    return;
                }

                var window = new CrashReportPromptWindow(client.CrashStore.Count(), choice => ChooseAsync(client, choice));
                window.Closed += (_, _) => Interlocked.Exchange(ref _open, 0);
                window.Show();
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _open, 0);
                Trace.WriteLine($"Crash prompt failed: {ex}");
            }
        }

        private static async Task ChooseAsync(TelemetryClient client, CrashPromptChoice choice)
        {
            try
            {
                var result = await CrashReportPrompt.ApplyAsync(client, choice, SaveModeAsync);
                if (choice == CrashPromptChoice.View && result.Text != null)
                {
                    new CrashReportViewWindow(result.Text).Show();
                }
                else if (result.Queued > 0)
                {
                    // Send now if a collection server exists; otherwise the report waits in the queue.
                    _ = Task.Run(() => client.FlushAsync(TimeSpan.FromSeconds(5)));
                }
            }
            catch (Exception ex)
            {
                ErrorReporter.Report("Handling the crash report choice", ex, showDialog: false);
            }
        }

        /// <summary>Persists <c>telemetry.crashReports</c> in config.json.</summary>
        public static async Task SaveModeAsync(string mode)
        {
            var config = await AppServices.GetConfigManagerAsync();
            config.Telemetry.CrashReports = mode;
            await config.SaveAsync();
            Telemetry.Client.InvalidateConfig();
        }
    }
}

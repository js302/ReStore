using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using ReStore.Core.src.utils;
using ReStore.Views.Windows;

namespace ReStore.Services
{
    /// <summary>
    /// Central place for GUI error handling: always logs, writes a crash file for unhandled errors,
    /// and shows at most one friendly (de-duplicated, rate-limited) dialog with a "Copy details" button.
    /// Never throws.
    /// </summary>
    public static class ErrorReporter
    {
        private static readonly ErrorDialogGate Gate = new(TimeSpan.FromSeconds(60), 3);
        private static int _dialogOpen;

        /// <summary>Handles an exception caught in a UI handler: log + friendly dialog.</summary>
        public static void Report(string context, Exception exception, bool showDialog = true)
            => Handle(context, exception, writeCrashFile: false, showDialog);

        /// <summary>
        /// Runs a UI action from an async-void handler: failures are logged and shown as one friendly dialog
        /// instead of escaping to the global handler. <paramref name="context"/> is a gerund phrase ("Saving settings").
        /// </summary>
        public static async Task GuardAsync(string context, Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                // User-initiated cancellation is not an error.
            }
            catch (Exception ex)
            {
                Report(context, ex);
            }
        }

        /// <summary>Handles an unhandled exception from a global hook: log + crash file + friendly dialog.</summary>
        public static void ReportUnhandled(string source, Exception? exception, bool showDialog = true)
            => Handle(source, exception, writeCrashFile: true, showDialog);

        private static void Handle(string context, Exception? exception, bool writeCrashFile, bool showDialog)
        {
            try
            {
                var now = DateTime.UtcNow;
                var details = CrashReportFormatter.Format(context, exception, now, GetVersion());

                try
                {
                    AppServices.Logger.Log($"{context}: {exception}", LogLevel.Error);
                }
                catch
                {
                    Trace.WriteLine($"{context}: {exception}");
                }

                if (writeCrashFile)
                {
                    WriteCrashFile(details, now);

                    // Anonymous: only the exception's type name (System.*/ReStore.*) is ever recorded.
                    ReStore.Core.src.telemetry.Telemetry.TrackUnhandled(exception);

                    // Opt-in crash report (docs/telemetry.md): scrubbed, kept locally until the user agrees unless "always".
                    if (ReStore.Core.src.telemetry.Telemetry.RecordCrash(exception).Disposition == ReStore.Core.src.telemetry.CrashDisposition.Pending)
                    {
                        CrashPromptService.ShowSoon();
                    }
                }

                if (!showDialog || !Gate.ShouldShow(CrashReportFormatter.DedupeKey(exception), now))
                {
                    return;
                }

                ShowDialog(CrashReportFormatter.FriendlyMessage(context, exception), details);
            }
            catch (Exception inner)
            {
                Trace.WriteLine($"Error reporting failed: {inner}");
            }
        }

        private static void WriteCrashFile(string details, DateTime now)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                File.WriteAllText(Path.Combine(AppPaths.LogsDirectory, CrashReportFormatter.CrashFileName(now)), details);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Could not write crash file: {ex}");
            }
        }

        private static void ShowDialog(string message, string details)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _dialogOpen, 1, 0) != 0)
            {
                return; // one error dialog at a time
            }

            void Show()
            {
                try
                {
                    ModernDialogService.ShowError(message, "ReStore Error", details);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"Error dialog failed: {ex}");
                }
                finally
                {
                    Interlocked.Exchange(ref _dialogOpen, 0);
                }
            }

            // BeginInvoke: never block the thread that raised the error (may be a non-UI thread or inside a handler).
            dispatcher.BeginInvoke((Action)Show);
        }

        private static string? GetVersion()
            => Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
    }
}

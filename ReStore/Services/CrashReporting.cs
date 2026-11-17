using System.Text;
using ReStore.Core.src.utils;

namespace ReStore.Services
{
    /// <summary>
    /// WPF-free helpers for the GUI's error reporting (linked into the test project):
    /// crash-report text, crash file names and the gate that keeps error dialogs from flooding the user.
    /// </summary>
    public static class CrashReportFormatter
    {
        /// <summary>Full diagnostic text: header, then the exception chain with stack traces.</summary>
        public static string Format(string source, Exception? exception, DateTime utcNow, string? appVersion = null, string? context = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ReStore error report");
            sb.AppendLine($"Time (UTC): {utcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine($"Source: {source}");
            if (!string.IsNullOrWhiteSpace(context))
            {
                sb.AppendLine($"Action: {context}");
            }
            if (!string.IsNullOrWhiteSpace(appVersion))
            {
                sb.AppendLine($"Version: {appVersion}");
            }
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine();
            sb.AppendLine(exception?.ToString() ?? "Unknown error (no exception object)");
            // Exception text can carry connection strings, tokens or URLs with credentials; the file may be shared in a bug report.
            return DiagnosticsReport.RedactText(sb.ToString());
        }

        /// <summary>File name for a crash dump; sortable and filesystem-safe.</summary>
        public static string CrashFileName(DateTime utcNow) => $"crash-{utcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.txt";

        /// <summary>One-line, stack-free description suitable for a dialog body.</summary>
        public static string FriendlyMessage(string? context, Exception? exception)
        {
            var what = string.IsNullOrWhiteSpace(context) ? "An unexpected error occurred." : $"{context} failed.";
            var reason = exception is null ? null : FirstLine(exception.GetBaseException().Message);
            return string.IsNullOrWhiteSpace(reason)
                ? $"{what}\n\nUse \"Copy details\" to copy a technical report."
                : $"{what}\n\n{reason}\n\nUse \"Copy details\" to copy a technical report.";
        }

        /// <summary>Key identifying "the same error" for de-duplication (type + first message line).</summary>
        public static string DedupeKey(Exception? exception)
            => exception is null ? "unknown" : $"{exception.GetType().FullName}|{FirstLine(exception.Message)}";

        private static string FirstLine(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var idx = text.IndexOfAny(['\r', '\n']);
            var line = idx < 0 ? text : text[..idx];
            return line.Length > 300 ? line[..300] + "..." : line;
        }
    }

    /// <summary>
    /// Decides whether an error dialog may be shown: the same error is suppressed for a window,
    /// and the total number of dialogs per window is capped. Thread-safe.
    /// </summary>
    public sealed class ErrorDialogGate
    {
        private readonly TimeSpan _window;
        private readonly int _maxPerWindow;
        private readonly Dictionary<string, DateTime> _lastShown = new();
        private readonly Queue<DateTime> _recent = new();
        private readonly object _lock = new();

        public ErrorDialogGate(TimeSpan window, int maxPerWindow)
        {
            _window = window;
            _maxPerWindow = maxPerWindow;
        }

        public bool ShouldShow(string key, DateTime now)
        {
            lock (_lock)
            {
                while (_recent.Count > 0 && now - _recent.Peek() >= _window)
                {
                    _recent.Dequeue();
                }

                if (_lastShown.TryGetValue(key, out var last) && now - last < _window)
                {
                    return false;
                }

                if (_recent.Count >= _maxPerWindow)
                {
                    return false;
                }

                _lastShown[key] = now;
                _recent.Enqueue(now);

                if (_lastShown.Count > 64)
                {
                    foreach (var stale in _lastShown.Where(kv => now - kv.Value >= _window).Select(kv => kv.Key).ToList())
                    {
                        _lastShown.Remove(stale);
                    }
                }

                return true;
            }
        }
    }

    /// <summary>Counts consecutive failures so a repeating job (e.g. a UI timer) can give up.</summary>
    public sealed class FailureStreak
    {
        private readonly int _limit;
        private int _count;

        public FailureStreak(int limit) => _limit = limit;

        public void RecordSuccess() => _count = 0;

        /// <summary>Records a failure; returns true when the limit has been reached.</summary>
        public bool RecordFailure() => ++_count >= _limit;
    }
}

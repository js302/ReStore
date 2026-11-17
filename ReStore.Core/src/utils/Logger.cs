using System.Text;

namespace ReStore.Core.src.utils;

public interface ILogger
{
    void Log(string message, LogLevel level = LogLevel.Info);
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// File + console logger. Lines below <see cref="MinimumLevel"/> are dropped. Lines are buffered in memory under one lock and
/// written in batches: immediately for warnings and errors, otherwise by a short timer, <see cref="Flush"/>,
/// <see cref="Dispose"/> and process exit. Every batch opens the file fresh in append mode (shared read/write/delete), writes
/// once and closes, so several processes (GUI and CLI) can share restore.log without overwriting each other, and rotation is
/// decided from the real file length at flush time. A failed flush keeps the lines (bounded) and retries on the next one.
/// </summary>
public class Logger : ILogger, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private const int MAX_BUFFERED_LINES = 10_000;

    private string _logFilePath;
    private string _logDir;
    private readonly Lock _lockObject = new();
    private const long MAX_LOG_SIZE_BYTES = 10 * 1024 * 1024; // 10 MB
    private const int MAX_BACKUP_COUNT = 5;

    private readonly List<string> _buffer = [];
    private Timer? _flushTimer;
    private bool _disposed;
    private volatile LogLevel _minimumLevel = LogLevel.Info;

    public Logger()
    {
        _logDir = AppPaths.LogsDirectory;
        try 
        {
            Directory.CreateDirectory(_logDir);
            _logFilePath = Path.Combine(_logDir, "restore.log");
        }
        catch
        {
            // Fallback to temp if we can't create the directory
            _logDir = Path.GetTempPath();
            _logFilePath = Path.Combine(_logDir, "restore.log");
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>
    /// When true (the CLI's <c>--quiet</c>), the console only gets error lines, written to stderr; the log file is
    /// unaffected. Otherwise warnings and errors go to stderr and info and debug lines to stdout. Process-wide, so every logger instance in the CLI honours it.
    /// </summary>
    public static bool QuietConsole { get; set; }

    /// <summary>
    /// Optional process-wide observer of error lines (redacted text, no timestamp). The CLI uses it to put the first
    /// error into its JSON error document; it is not invoked for lines dropped by <see cref="MinimumLevel"/>.
    /// </summary>
    public static Action<string>? ErrorObserver { get; set; }

    /// <summary>Lines logged below this level are dropped (file and console). Default <see cref="LogLevel.Info"/>.</summary>
    public LogLevel MinimumLevel
    {
        get => _minimumLevel;
        set => _minimumLevel = value;
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        // Messages can quote exception text with connection strings, tokens or URL credentials; keep them out of the log file.
        var redacted = DiagnosticsReport.RedactText(message);
        var logMessage = $"[{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}] [{level}] {redacted}";

        if (level >= LogLevel.Error)
        {
            try
            {
                ErrorObserver?.Invoke(redacted);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An observer must never break logging.
            }
        }

        lock (_lockObject)
        {
            // Warnings and errors go to stderr so stdout stays free for results; info and debug go to stdout.
            if (QuietConsole ? level >= LogLevel.Error : level >= LogLevel.Warning)
            {
                Console.Error.WriteLine(logMessage);
            }
            else if (!QuietConsole)
            {
                Console.WriteLine(logMessage);
            }

            if (_disposed)
            {
                return;
            }

            _buffer.Add(logMessage);
            _flushTimer ??= new Timer(_ => Flush(), null, FlushInterval, FlushInterval);

            if (level >= LogLevel.Warning)
            {
                FlushCore();
            }
        }
    }

    /// <summary>Writes any buffered lines to the file.</summary>
    public void Flush()
    {
        lock (_lockObject)
        {
            FlushCore();
        }
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        lock (_lockObject)
        {
            if (_disposed)
            {
                return;
            }

            FlushCore();
            _disposed = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
            _buffer.Clear();
        }

        GC.SuppressFinalize(this);
    }

    private void OnProcessExit(object? sender, EventArgs e) => Flush();

    // Caller holds the lock.
    private void FlushCore()
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        try
        {
            RotateLogIfNeeded();

            var bytes = Encoding.UTF8.GetBytes(string.Concat(_buffer.Select(line => line + Environment.NewLine)));

            // Several processes (GUI, CLI, service) append to the same file. "Read the length, write at that offset" is not
            // atomic on any OS (two writers can read the same length and overwrite each other's lines), so serialise the
            // append with a named mutex for this log file. Readers (editors, tail) are never blocked by it.
            using (var appendLock = LogAppendLock.Acquire(_logFilePath))
            using (var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                RandomAccess.Write(stream.SafeFileHandle, bytes, RandomAccess.GetLength(stream.SafeFileHandle));
            }

            _buffer.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Console output already happened; keep the lines for the next flush, bounded.
            if (_buffer.Count > MAX_BUFFERED_LINES)
            {
                _buffer.RemoveRange(0, _buffer.Count - MAX_BUFFERED_LINES);
            }
        }
    }

    /// <summary>
    /// A cross-process lock for appending to one log file (named after a hash of its full path). Waits briefly; if the lock
    /// cannot be had (or named mutexes are unavailable) the append goes ahead unlocked rather than losing the lines.
    /// </summary>
    private sealed class LogAppendLock : IDisposable
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
        private readonly Mutex? _mutex;
        private readonly bool _held;

        private LogAppendLock(Mutex? mutex, bool held)
        {
            _mutex = mutex;
            _held = held;
        }

        public static LogAppendLock Acquire(string logFilePath)
        {
            Mutex? mutex = null;
            try
            {
                var key = Path.GetFullPath(logFilePath);
                if (OperatingSystem.IsWindows())
                {
                    key = key.ToUpperInvariant();
                }

                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0, 16);
                mutex = new Mutex(false, "ReStoreLog_" + hash);
                bool held;
                try
                {
                    held = mutex.WaitOne(Wait);
                }
                catch (AbandonedMutexException)
                {
                    held = true; // the previous owner died mid-append; we own it now
                }

                return new LogAppendLock(mutex, held);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException or ArgumentException)
            {
                mutex?.Dispose();
                return new LogAppendLock(null, false);
            }
        }

        public void Dispose()
        {
            if (_held)
            {
                _mutex?.ReleaseMutex();
            }

            _mutex?.Dispose();
        }
    }

    private void RotateLogIfNeeded()
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;

            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Length < MAX_LOG_SIZE_BYTES) return;

            var oldestBackup = Path.Combine(_logDir, $"restore.log.{MAX_BACKUP_COUNT}");
            if (File.Exists(oldestBackup))
            {
                File.Delete(oldestBackup);
            }

            // Rotate existing backups: .4 -> .5, .3 -> .4, etc.
            for (int i = MAX_BACKUP_COUNT - 1; i >= 1; i--)
            {
                var currentBackup = Path.Combine(_logDir, $"restore.log.{i}");
                var nextBackup = Path.Combine(_logDir, $"restore.log.{i + 1}");
                if (File.Exists(currentBackup))
                {
                    File.Move(currentBackup, nextBackup, overwrite: true);
                }
            }

            var firstBackup = Path.Combine(_logDir, "restore.log.1");
            File.Move(_logFilePath, firstBackup, overwrite: true);
        }
        catch
        {
            // Log rotation failed, but we should still try to write to the log
        }
    }
}

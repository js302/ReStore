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

public class Logger : ILogger
{
    private readonly string _logFilePath;
    private readonly string _logDir;
    private readonly Lock _lockObject = new();
    private const long MAX_LOG_SIZE_BYTES = 10 * 1024 * 1024; // 10 MB
    private const int MAX_BACKUP_COUNT = 5;

    public Logger()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _logDir = Path.Combine(userProfile, "ReStore", "logs");
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
    }

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var logMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

        lock (_lockObject)
        {
            Console.WriteLine(logMessage);
            
            try
            {
                RotateLogIfNeeded();
                File.AppendAllText(_logFilePath, logMessage + Environment.NewLine);
            }
            catch (IOException)
            {
                // If we can't write to the log file, at least we've output to console
                // This prevents the entire application from failing due to logging issues
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above - continue even if log file access is denied
            }
        }
    }

    private void RotateLogIfNeeded()
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;

            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Length < MAX_LOG_SIZE_BYTES) return;

            // Delete oldest backup if we're at max count
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

            // Move current log to .1
            var firstBackup = Path.Combine(_logDir, "restore.log.1");
            File.Move(_logFilePath, firstBackup, overwrite: true);
        }
        catch
        {
            // Log rotation failed, but we should still try to write to the log
        }
    }
}

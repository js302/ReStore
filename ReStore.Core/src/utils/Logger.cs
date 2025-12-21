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
    private readonly Lock _lockObject = new();

    public Logger()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var logDir = Path.Combine(userProfile, "ReStore", "logs");
        try 
        {
            Directory.CreateDirectory(logDir);
            _logFilePath = Path.Combine(logDir, "restore.log");
        }
        catch
        {
            // Fallback to temp if we can't create the directory
            _logFilePath = Path.Combine(Path.GetTempPath(), "restore.log");
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
}

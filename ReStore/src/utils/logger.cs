namespace ReStore.Utils;

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
    private const string LOG_FILE = "restore.log";
    private readonly Lock _lockObject = new();

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var logMessage = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";

        lock (_lockObject)
        {
            Console.WriteLine(logMessage);
            File.AppendAllText(LOG_FILE, logMessage + Environment.NewLine);
        }
    }
}

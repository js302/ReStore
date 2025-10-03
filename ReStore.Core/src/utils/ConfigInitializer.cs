using System.Reflection;

namespace ReStore.Core.src.utils;

public static class ConfigInitializer
{
    private static readonly string USER_CONFIG_DIR = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "ReStore");
    
    private static readonly string USER_STATE_DIR = Path.Combine(USER_CONFIG_DIR, "state");
    private static readonly string USER_CONFIG_PATH = Path.Combine(USER_CONFIG_DIR, "config.json");
    private static readonly string USER_EXAMPLE_CONFIG_PATH = Path.Combine(USER_CONFIG_DIR, "config.example.json");

    public static void EnsureConfigurationSetup(ILogger? logger = null)
    {
        try
        {
            Directory.CreateDirectory(USER_CONFIG_DIR);
            Directory.CreateDirectory(USER_STATE_DIR);
            logger?.Log($"Ensured ReStore directories exist at: {USER_CONFIG_DIR}", LogLevel.Debug);

            var exampleExists = File.Exists(USER_EXAMPLE_CONFIG_PATH);
            var configExists = File.Exists(USER_CONFIG_PATH);

            if (!exampleExists)
            {
                var appExamplePath = GetApplicationExampleConfigPath();
                if (appExamplePath != null && File.Exists(appExamplePath))
                {
                    File.Copy(appExamplePath, USER_EXAMPLE_CONFIG_PATH, overwrite: false);
                    logger?.Log($"Copied example configuration to: {USER_EXAMPLE_CONFIG_PATH}", LogLevel.Info);
                }
            }

            if (!configExists && !exampleExists)
            {
                logger?.Log($"Configuration directory created at: {USER_CONFIG_DIR}", LogLevel.Info);
                logger?.Log("Example configuration will be created on first run.", LogLevel.Info);
            }
            else if (!configExists && exampleExists)
            {
                logger?.Log($"Configuration directory: {USER_CONFIG_DIR}", LogLevel.Info);
                logger?.Log($"Example configuration found at: {USER_EXAMPLE_CONFIG_PATH}", LogLevel.Info);
                logger?.Log("Please rename config.example.json to config.json and configure your settings.", LogLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            logger?.Log($"Error during configuration setup: {ex.Message}", LogLevel.Error);
        }
    }

    private static string? GetApplicationExampleConfigPath()
    {
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
        
        if (string.IsNullOrEmpty(assemblyDirectory))
        {
            return null;
        }

        var inAppConfig = Path.Combine(assemblyDirectory, "config", "config.example.json");
        if (File.Exists(inAppConfig))
        {
            return inAppConfig;
        }

        var currentDir = new DirectoryInfo(assemblyDirectory);
        DirectoryInfo? projectRoot = null;

        while (currentDir?.Parent != null)
        {
            if ((currentDir.Name == "net9.0" || currentDir.Name == "net9.0-windows")
                && currentDir.Parent?.Name == "Debug" && currentDir.Parent.Parent?.Name == "bin")
            {
                projectRoot = currentDir.Parent.Parent.Parent;
                break;
            }
            else if ((currentDir.Name == "net9.0" || currentDir.Name == "net9.0-windows")
                && currentDir.Parent?.Name == "Release" && currentDir.Parent.Parent?.Name == "bin")
            {
                projectRoot = currentDir.Parent.Parent.Parent;
                break;
            }
            else if (currentDir.GetFiles("*.csproj").Length > 0)
            {
                projectRoot = currentDir;
                break;
            }
            
            currentDir = currentDir.Parent;
        }

        if (projectRoot != null && projectRoot.Exists)
        {
            var probe = projectRoot;
            for (int i = 0; i < 3 && probe != null; i++)
            {
                var candidate = Path.Combine(probe.FullName, "config", "config.example.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                probe = probe.Parent;
            }
        }

        return null;
    }

    public static string GetUserConfigDirectory() => USER_CONFIG_DIR;
    public static string GetUserStateDirectory() => USER_STATE_DIR;
    public static string GetUserConfigPath() => USER_CONFIG_PATH;
    public static string GetUserExampleConfigPath() => USER_EXAMPLE_CONFIG_PATH;
}

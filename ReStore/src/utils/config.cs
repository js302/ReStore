using System.Text.Json;
using ReStore.Storage;

namespace ReStore.Utils;

public interface IConfigManager
{
    List<string> WatchDirectories { get; }
    TimeSpan BackupInterval { get; }
    long SizeThresholdMB { get; }
    Dictionary<string, StorageConfig> StorageSources { get; }
    Task LoadAsync();
    Task<IStorage> CreateStorageAsync(string storageType);
}

public class StorageConfig
{
    public string Path { get; set; } = string.Empty;
    public Dictionary<string, string> Options { get; set; } = [];
}

public class ConfigManager(ILogger logger) : IConfigManager
{
    private const string CONFIG_PATH = "config/default_config.json";
    private JsonDocument? _config;

    public List<string> WatchDirectories { get; private set; } = [];
    public TimeSpan BackupInterval { get; private set; } = TimeSpan.FromHours(1);
    public long SizeThresholdMB { get; private set; } = 500;
    public Dictionary<string, StorageConfig> StorageSources { get; private set; } = [];

    private readonly StorageFactory _storageFactory = new(logger);

    public async Task<IStorage> CreateStorageAsync(string storageType)
    {
        if (_config is null)
        {
            throw new InvalidOperationException("Configuration not loaded");
        }

        if (!StorageSources.TryGetValue(storageType, out var config))
        {
            throw new ArgumentException($"Storage type '{storageType}' not found in configuration");
        }

        return await _storageFactory.CreateStorageAsync(storageType, config);
    }

    public async Task LoadAsync()
    {
        var jsonString = await File.ReadAllTextAsync(CONFIG_PATH);
        _config = JsonDocument.Parse(jsonString);
        var root = _config.RootElement;

        try
        {
            WatchDirectories = root.GetProperty("watchDirectories")
                .EnumerateArray()
                .Select(x => Environment.ExpandEnvironmentVariables(x.GetString() ?? string.Empty))
                .ToList();

            BackupInterval = TimeSpan.Parse(root.GetProperty("backupInterval").GetString() ?? "01:00:00");
            SizeThresholdMB = root.GetProperty("sizeThresholdMB").GetInt64();

            var storageSources = root.GetProperty("storageSources");

            StorageSources = JsonSerializer.Deserialize<Dictionary<string, StorageConfig>>(
                storageSources,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new JsonException("Failed to deserialize storage sources");

            foreach (var source in StorageSources.Values)
            {
                source.Options = source.Options.ToDictionary(x => x.Key, x => Environment.ExpandEnvironmentVariables(x.Value));
            }

            Console.WriteLine("Configuration loaded successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to load configuration", ex);
        }
    }
}

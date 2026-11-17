using System.Text.Json;
using System.Text.Json.Serialization;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.updates;

/// <summary><c>state/update-check.json</c>: the last check, the version the user skipped and when we last told them about one.</summary>
public sealed class UpdateCache(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();

    public string Path => path ?? System.IO.Path.Combine(AppPaths.StateDirectory, "update-check.json");

    /// <summary>The cached data; an absent, unreadable or corrupt file is an empty cache.</summary>
    public UpdateCacheData Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return new UpdateCacheData();
                }

                return JsonSerializer.Deserialize<UpdateCacheData>(File.ReadAllText(Path), Options) ?? new UpdateCacheData();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new UpdateCacheData();
            }
        }
    }

    /// <summary>Best effort: a cache that cannot be written only means the next check is not throttled.</summary>
    public void Save(UpdateCacheData data)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                ReStore.Core.src.utils.SharedFile.WriteAllTextAtomic(Path, JsonSerializer.Serialize(data, Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignored on purpose.
            }
        }
    }

    /// <summary>Loads, applies <paramref name="change"/> and saves.</summary>
    public UpdateCacheData Update(Action<UpdateCacheData> change)
    {
        lock (_gate)
        {
            var data = Load();
            change(data);
            Save(data);
            return data;
        }
    }
}

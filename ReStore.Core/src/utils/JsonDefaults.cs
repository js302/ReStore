using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReStore.Core.src.utils;

/// <summary>
/// Shared JSON options for system-backup export/import files (environment variables, programs,
/// Windows settings manifest). Writes camelCase with enums as strings; reads case-insensitively
/// (so PascalCase and camelCase both work) and accepts enums as either names or numbers.
/// </summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions SystemBackup { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: true));
        return options;
    }
}

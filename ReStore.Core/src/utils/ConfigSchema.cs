using System.Text.Json.Nodes;

namespace ReStore.Core.src.utils;

/// <summary>Where a dotted config key lands in <c>config.schema.json</c>.</summary>
/// <param name="Known">False when the schema lists the parent's keys and this one is not among them (typo protection).</param>
/// <param name="Node">The schema node of the key; null when it is not <paramref name="Known"/>.</param>
/// <param name="Types">The JSON types the schema allows for the key (empty when it does not say).</param>
/// <param name="Canonical">The segments with the schema's own spelling where a key matched case-insensitively.</param>
public sealed record ConfigKeyInfo(bool Known, JsonObject? Node, IReadOnlyList<string> Types, IReadOnlyList<string> Canonical)
{
    public bool AllowsString => Types.Contains("string");
}

/// <summary>
/// The hand-maintained JSON Schema of config.json (embedded resource <c>config.schema.json</c>) and the default config
/// (<c>config.example.json</c>), both embedded so the single-file CLI has them without files next to the executable.
/// </summary>
public static class ConfigSchema
{
    private static readonly Lazy<string> SchemaText = new(() => ReadResource("config.schema.json"));
    private static readonly Lazy<string> ExampleText = new(() => ReadResource("config.example.json"));

    /// <summary>The schema document as written in the resource.</summary>
    public static string Text => SchemaText.Value;

    /// <summary>A fresh parsed copy of the schema.</summary>
    public static JsonObject LoadSchema() => (JsonObject)JsonNode.Parse(SchemaText.Value)!;

    /// <summary>A fresh parsed copy of the default config.json.</summary>
    public static JsonObject LoadExample() => (JsonObject)JsonNode.Parse(ExampleText.Value)!;

    /// <summary>
    /// Follows <paramref name="segments"/> (object keys, or indexes into arrays) through the schema. A key is unknown only when
    /// the schema lists the parent's properties, does not list this one and the parent does not allow additional properties.
    /// </summary>
    public static ConfigKeyInfo Resolve(IReadOnlyList<string> segments) => Resolve(LoadSchema(), segments);

    internal static ConfigKeyInfo Resolve(JsonObject schema, IReadOnlyList<string> segments)
    {
        var node = schema;
        var canonical = new List<string>();
        foreach (var segment in segments)
        {
            var next = Step(node, segment, out var spelling);
            if (next == null)
            {
                return new ConfigKeyInfo(false, null, [], segments);
            }

            canonical.Add(spelling);
            node = next;
        }

        return new ConfigKeyInfo(true, node, TypesOf(node), canonical);
    }

    private static JsonObject? Step(JsonObject node, string segment, out string spelling)
    {
        spelling = segment;
        if (node["properties"] is JsonObject properties)
        {
            foreach (var property in properties)
            {
                if (string.Equals(property.Key, segment, StringComparison.OrdinalIgnoreCase))
                {
                    spelling = property.Key;
                    return property.Value as JsonObject;
                }
            }
        }

        if (node["items"] is JsonObject items && int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return items;
        }

        return node["additionalProperties"] switch
        {
            JsonObject additional => additional,
            JsonValue value when value.TryGetValue<bool>(out var allowed) && allowed => [],
            _ => null
        };
    }

    private static string[] TypesOf(JsonObject node) => node["type"] switch
    {
        JsonValue single when single.TryGetValue<string>(out var type) => [type],
        JsonArray many => [.. many.Select(t => t?.GetValue<string>()).OfType<string>()],
        _ => []
    };

    private static string ReadResource(string name)
    {
        using var stream = typeof(ConfigSchema).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

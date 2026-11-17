using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReStore.Core.src.utils;

/// <summary>
/// Checks a config.json document against <c>config.schema.json</c>: type, enum, minimum/maximum, minLength, pattern, required,
/// array items and nested properties. Used before a CLI edit is written, so a value the loader would silently reset to its
/// default (a wrong type, an unknown enum member, an out-of-range number) is refused instead of reported as success.
/// Keys the schema does not list are not errors here (unknown keys are preserved by design).
/// </summary>
public static class ConfigSchemaValidator
{
    public static List<string> Validate(JsonObject document) => Validate(ConfigSchema.LoadSchema(), document);

    internal static List<string> Validate(JsonObject schema, JsonObject document)
    {
        var errors = new List<string>();

        // Documents built in code hold CLR-typed values; a text round trip gives every node the same (parsed) representation.
        Check(schema, JsonNode.Parse(document.ToJsonString()), string.Empty, errors);
        return errors;
    }

    private static void Check(JsonObject schema, JsonNode? value, string path, List<string> errors)
    {
        var label = path.Length == 0 ? "config.json" : path;
        var types = TypesOf(schema);
        if (types.Count > 0 && !types.Any(t => MatchesType(t, value)))
        {
            errors.Add($"{label}: expected {string.Join(" or ", types)} but found {Describe(value)} (schema rule 'type').");
            return;
        }

        if (value == null)
        {
            return;
        }

        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => EnumMatches(a, value)))
        {
            errors.Add($"{label}: {value.ToJsonString()} is not one of {string.Join(", ", allowed.Select(a => a?.ToJsonString()))} (schema rule 'enum').");
            return;
        }

        if (value is JsonValue scalar)
        {
            CheckScalar(schema, scalar, label, errors);
        }
        else if (value is JsonObject obj)
        {
            CheckObject(schema, obj, path, errors);
        }
        else if (value is JsonArray array && schema["items"] is JsonObject items)
        {
            for (var i = 0; i < array.Count; i++)
            {
                Check(items, array[i], $"{path}[{i}]", errors);
            }
        }
    }

    private static void CheckScalar(JsonObject schema, JsonValue value, string label, List<string> errors)
    {
        if (value.TryGetValue<string>(out var text))
        {
            if (schema["minLength"] is JsonValue minLength && minLength.TryGetValue<int>(out var min) && text.Trim().Length < min)
            {
                errors.Add($"{label}: must not be empty (schema rule 'minLength' {min}).");
            }

            if (schema["pattern"] is JsonValue pattern && pattern.TryGetValue<string>(out var regex) && !Regex.IsMatch(text, regex, RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                errors.Add($"{label}: '{text}' does not match the pattern {regex} (schema rule 'pattern').");
            }

            return;
        }

        if (!TryNumber(value, out var number))
        {
            return;
        }

        if (schema["minimum"] is JsonValue minimum && minimum.TryGetValue<double>(out var low) && number < low)
        {
            errors.Add($"{label}: {value.ToJsonString()} is below the minimum {low.ToString(CultureInfo.InvariantCulture)} (schema rule 'minimum').");
        }

        if (schema["maximum"] is JsonValue maximum && maximum.TryGetValue<double>(out var high) && number > high)
        {
            errors.Add($"{label}: {value.ToJsonString()} is above the maximum {high.ToString(CultureInfo.InvariantCulture)} (schema rule 'maximum').");
        }
    }

    private static void CheckObject(JsonObject schema, JsonObject obj, string path, List<string> errors)
    {
        var prefix = path.Length == 0 ? string.Empty : path + ".";
        if (schema["required"] is JsonArray required)
        {
            foreach (var name in required.Select(r => r?.GetValue<string>()).OfType<string>())
            {
                if (!obj.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add($"{(path.Length == 0 ? "config.json" : path)}: missing required key '{name}' (schema rule 'required').");
                }
            }
        }

        var properties = schema["properties"] as JsonObject;
        var additional = schema["additionalProperties"] as JsonObject;
        foreach (var member in obj)
        {
            JsonObject? child = null;
            if (properties != null)
            {
                child = properties.FirstOrDefault(p => string.Equals(p.Key, member.Key, StringComparison.OrdinalIgnoreCase)).Value as JsonObject;
            }

            child ??= additional;
            if (child != null)
            {
                Check(child, member.Value, prefix + member.Key, errors);
            }
        }
    }

    private static List<string> TypesOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue single when single.TryGetValue<string>(out var type) => [type],
        JsonArray many => [.. many.Select(t => t?.GetValue<string>()).OfType<string>()],
        _ => []
    };

    private static bool MatchesType(string type, JsonNode? value) => type switch
    {
        "null" => value == null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue s && s.TryGetValue<string>(out _),
        "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
        // The config model stores these as int, and the loader resets anything that does not fit.
        "integer" => value is JsonValue i && TryNumber(i, out var n) && n == Math.Floor(n) && n >= int.MinValue && n <= int.MaxValue,
        "number" => value is JsonValue d && TryNumber(d, out _),
        _ => true
    };

    private static bool TryNumber(JsonValue value, out double number)
    {
        number = 0;
        if (value.TryGetValue<string>(out _) || value.TryGetValue<bool>(out _))
        {
            return false;
        }

        return value.TryGetValue<double>(out number);
    }

    private static bool EnumMatches(JsonNode? allowed, JsonNode value)
    {
        if (allowed is JsonValue a && value is JsonValue v)
        {
            if (a.TryGetValue<string>(out var allowedText))
            {
                return v.TryGetValue<string>(out var text) && string.Equals(text, allowedText, StringComparison.OrdinalIgnoreCase);
            }

            return TryNumber(a, out var x) && TryNumber(v, out var y) && x == y;
        }

        return false;
    }

    private static string Describe(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "an object",
        JsonArray => "a list",
        JsonValue v when v.TryGetValue<string>(out var s) => $"the text \"{(s.Length > 40 ? s[..40] + "..." : s)}\"",
        _ => value.ToJsonString()
    };
}

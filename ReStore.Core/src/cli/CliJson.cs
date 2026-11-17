using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ReStore.Core.src.cli;

/// <summary>Builds the single JSON document a <c>--json</c> command writes: every document starts with <c>"ok"</c>.</summary>
internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // CLI documents go to a terminal or a pipe, never into HTML: keep ' < > & readable instead of \u0027 and friends.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>One-line JSON for text output (<c>Set key = value</c>), with the same relaxed escaping.</summary>
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Same as <see cref="Options"/> but property names are camelCased (for documents built from PascalCase records).</summary>
    public static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Serialises <paramref name="document"/> and puts <c>ok</c> first (unless it already has one). A failure document also
    /// gets <c>exitCode</c> and <c>error</c> when they are missing.
    /// </summary>
    public static string Build(object document, int exitCode, CliError? error = null, JsonSerializerOptions? options = null)
    {
        options ??= Options;
        return BuildNode(document, exitCode, error, options).ToJsonString(options);
    }

    /// <summary>Same as <see cref="Build"/> but returns the document as a node so the caller can still amend it.</summary>
    public static JsonObject BuildNode(object document, int exitCode, CliError? error, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(document, options))?.AsObject() ?? [];
        var ok = exitCode == 0;
        if (!node.ContainsKey("ok"))
        {
            node.Insert(0, "ok", ok);
        }

        if (!ok)
        {
            if (!node.ContainsKey("exitCode"))
            {
                node.Insert(Math.Min(1, node.Count), "exitCode", exitCode);
            }

            // A handler may bring its own error object; a plain string/null "error" field never replaces the envelope.
            if (error != null && node["error"] is not JsonObject)
            {
                node["error"] = ErrorNode(error);
            }
        }

        return node;
    }

    /// <summary>
    /// A command that succeeded but could not persist its state is a failure for the caller: flips <c>ok</c>, sets
    /// <c>exitCode</c> and <c>error</c> (the error envelope wins over a field of the same name) and adds <c>stateSaved: false</c>.
    /// </summary>
    public static void MarkStateNotSaved(JsonObject node, int exitCode, CliError error)
    {
        node["ok"] = false;
        node["exitCode"] = exitCode;
        node["error"] = ErrorNode(error);
        node["stateSaved"] = false;
    }

    private static JsonObject ErrorNode(CliError error)
    {
        var errorNode = new JsonObject { ["code"] = error.Code, ["message"] = error.Message, ["hint"] = error.Hint };
        if (error.Details is { Count: > 0 })
        {
            errorNode["details"] = new JsonArray([.. error.Details.Select(d => (JsonNode?)JsonValue.Create(d))]);
        }

        return errorNode;
    }
}

using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>The hand-maintained config.schema.json must describe every key the example config and ConfigManager.Save write.</summary>
public class ConfigSchemaTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ReStoreSchemaTests_" + Guid.NewGuid().ToString("N"));

    public ConfigSchemaTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>The schema node for <paramref name="child"/> below <paramref name="schema"/>: a listed property, else the additionalProperties schema, else the array items.</summary>
    private static JsonObject? SchemaFor(JsonObject schema, string child) =>
        schema["properties"]?[child] as JsonObject
        ?? schema["additionalProperties"] as JsonObject;

    private static void Walk(JsonNode? node, JsonObject schema, string path, List<string> problems)
    {
        // The elements of a plain list (strings) are described by the list itself.
        if (!path.EndsWith("[]", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(schema["description"]?.GetValue<string>()))
        {
            problems.Add($"{path}: no description");
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (var member in obj)
                {
                    if (SchemaFor(schema, member.Key) is { } childSchema)
                    {
                        Walk(member.Value, childSchema, $"{path}.{member.Key}", problems);
                    }
                    else
                    {
                        problems.Add($"{path}.{member.Key}: not in the schema");
                    }
                }

                break;
            case JsonArray array:
                if (array.Count > 0)
                {
                    if (schema["items"] is JsonObject items)
                    {
                        foreach (var item in array)
                        {
                            Walk(item, items, $"{path}[]", problems);
                        }
                    }
                    else
                    {
                        problems.Add($"{path}: the schema has no items for this list");
                    }
                }

                break;
        }
    }

    [Fact]
    public void Schema_ParsesAsDraft202012_AndEveryNodeHasADescription()
    {
        var schema = ConfigSchema.LoadSchema();

        schema["$schema"]!.GetValue<string>().Should().Be("https://json-schema.org/draft/2020-12/schema");
        schema["type"]!.GetValue<string>().Should().Be("object");

        var problems = new List<string>();
        void Describe(JsonObject node, string path)
        {
            if (!path.EndsWith("[]", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(node["description"]?.GetValue<string>()))
            {
                problems.Add($"{path}: no description");
            }

            foreach (var member in (node["properties"] as JsonObject) ?? [])
            {
                Describe((JsonObject)member.Value!, $"{path}.{member.Key}");
            }

            if (node["items"] is JsonObject items)
            {
                Describe(items, $"{path}[]");
            }

            if (node["additionalProperties"] is JsonObject additional)
            {
                Describe(additional, $"{path}.*");
            }
        }

        Describe(schema, "$");
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Schema_DescribesEveryKeyOfTheExampleConfig()
    {
        var example = JsonNode.Parse(File.ReadAllText(FindExample()))!;
        var problems = new List<string>();

        Walk(example, ConfigSchema.LoadSchema(), "$", problems);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void EmbeddedExample_IsTheShippedExampleConfig()
    {
        JsonNode.DeepEquals(ConfigSchema.LoadExample(), JsonNode.Parse(File.ReadAllText(FindExample()))).Should().BeTrue();
    }

    [Fact]
    public async Task Schema_DescribesEveryKeyConfigManagerSaves()
    {
        // Load the example, save it again (as the app does after a change) and walk what Save wrote, including the optional
        // members the example leaves out (includeDevTools, devToolsStorageType, ...).
        var path = Path.Combine(_directory, "config.json");
        File.Copy(FindExample(), path);
        var manager = new ConfigManager(new TestLogger(), path);
        await manager.LoadAsync();
        manager.StorageSources["s3"].Options["prefix"] = "p";
        await manager.SaveAsync();

        var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var problems = new List<string>();
        Walk(saved, ConfigSchema.LoadSchema(), "$", problems);

        problems.Should().BeEmpty();
        saved["systemBackup"]!.AsObject().ContainsKey("devToolsStorageType").Should().BeTrue("the walk covers the keys Save adds");
    }

    [Fact]
    public void Resolve_FindsKnownKeys_CaseInsensitively_AndRejectsTypos()
    {
        var known = ConfigSchema.Resolve(["RETENTION", "keepdaily"]);
        known.Known.Should().BeTrue();
        known.Canonical.Should().Equal("retention", "keepDaily");
        known.Types.Should().Equal("integer");

        ConfigSchema.Resolve(["retention", "keepDayly"]).Known.Should().BeFalse();
        ConfigSchema.Resolve(["nonsense"]).Known.Should().BeFalse();
        ConfigSchema.Resolve(["watchDirectories", "0", "path"]).Known.Should().BeTrue();
        ConfigSchema.Resolve(["watchDirectories", "0", "extra"]).Known.Should().BeTrue("watch entries may carry unknown members");
        ConfigSchema.Resolve(["storageSources", "myown", "options", "anything"]).Known.Should().BeTrue("custom sources are free-form");
        ConfigSchema.Resolve(["storageSources", "s3", "options", "bucketName"]).AllowsString.Should().BeTrue();
        ConfigSchema.Resolve(["storageSources", "s3", "options", "bucketNme"]).Known.Should().BeFalse();
        ConfigSchema.Resolve(["retention", "keepDaily", "deeper"]).Known.Should().BeFalse();
    }

    [Theory]
    [InlineData("watchDirectories", "0", "schedule")]
    [InlineData("systemBackup", "schedule")]
    [InlineData("backupWindow", "enabled")]
    [InlineData("backupWindow", "start")]
    [InlineData("backupWindow", "end")]
    [InlineData("backupWindow", "days")]
    public void Schema_DescribesTheScheduleAndWindowKeys(params string[] key)
    {
        var info = ConfigSchema.Resolve(key);

        info.Known.Should().BeTrue();
        info.Node!["description"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    private static string FindExample()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "ReStore.Core", "config", "config.example.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("ReStore.Core/config/config.example.json was not found above the test output.");
    }
}

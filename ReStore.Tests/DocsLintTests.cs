using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using ReStore.Core.src.cli;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Keeps the copy-paste JSON in docs/storage/providers/*.md honest: every storage source shown in a
/// <c>json</c> block must use option names the provider actually reads, contain every required option and
/// pass the provider validator in <see cref="ConfigValidator"/>.
/// </summary>
public class DocsLintTests
{
    /// <summary>Options each provider reads (see the provider's InitializeCoreAsync); <c>prefix</c> is accepted by all.</summary>
    private static readonly Dictionary<string, string[]> KnownOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["local"] = [],
        ["gdrive"] = ["client_id", "client_secret", "token_folder", "backup_folder_name"],
        ["s3"] = ["accessKeyId", "secretAccessKey", "region", "bucketName", "serviceUrl"],
        ["github"] = ["token", "owner", "repo"],
        ["azure"] = ["connectionString", "containerName"],
        ["gcp"] = ["bucketName", "credentialPath"],
        ["dropbox"] = ["accessToken", "refreshToken", "appKey", "appSecret"],
        ["sftp"] = ["host", "port", "username", "password", "privateKeyPath", "passphrase", "hostKeyFingerprint"],
        ["b2"] = ["keyId", "applicationKey", "serviceUrl", "bucketName"],
    };

    /// <summary>Options the provider throws without, in addition to what <see cref="ConfigValidator"/> checks.</summary>
    private static readonly Dictionary<string, string[]> RequiredOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["b2"] = ["serviceUrl"],
    };

    private static readonly Regex JsonBlock = new(@"```json\s*\r?\n(.*?)\r?\n```", RegexOptions.Singleline);

    [Fact]
    public void ProviderDocs_JsonExamples_UseValidProviderOptions()
    {
        var docsDir = FindProviderDocsDirectory();
        var problems = new List<string>();
        var sourcesChecked = 0;

        foreach (var file in Directory.GetFiles(docsDir, "*.md"))
        {
            foreach (System.Text.RegularExpressions.Match match in JsonBlock.Matches(File.ReadAllText(file)))
            {
                using var doc = JsonDocument.Parse(match.Groups[1].Value, new JsonDocumentOptions { AllowTrailingCommas = true });
                if (!doc.RootElement.TryGetProperty("storageSources", out var sources) || sources.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var source in sources.EnumerateObject())
                {
                    sourcesChecked++;
                    problems.AddRange(Lint(source.Name, source.Value).Select(p => $"{Path.GetFileName(file)}: {p}"));
                }
            }
        }

        sourcesChecked.Should().BeGreaterThanOrEqualTo(9, "every provider page documents at least one source");
        problems.Should().BeEmpty();
    }

    [Fact]
    public void Lint_FlagsTheOldBackblazeNamesAndARemotePath()
    {
        using var doc = JsonDocument.Parse("""
            { "path": "backups", "options": { "accessKeyId": "a", "secretAccessKey": "b", "serviceUrl": "https://x", "bucketName": "c" } }
            """);

        var problems = Lint("b2", doc.RootElement);

        problems.Should().Contain(p => p.Contains("'accessKeyId'") && p.Contains("unknown"));
        problems.Should().Contain(p => p.Contains("keyId") && p.Contains("missing"));
        problems.Should().Contain(p => p.Contains("'path'"));
    }

    private static List<string> Lint(string sourceName, JsonElement source)
    {
        var problems = new List<string>();
        var label = $"source '{sourceName}'";

        if (!KnownOptions.TryGetValue(sourceName, out var known))
        {
            problems.Add($"{label}: unknown storage type");
            return problems;
        }

        var isLocal = sourceName.Equals("local", StringComparison.OrdinalIgnoreCase);
        var path = source.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        if (isLocal && string.IsNullOrWhiteSpace(path))
        {
            problems.Add($"{label}: Local storage needs a 'path'");
        }
        else if (!isLocal && path != null)
        {
            problems.Add($"{label}: remote providers ignore the top-level 'path'; use options.prefix instead");
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source.TryGetProperty("options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var option in optionsElement.EnumerateObject())
            {
                options[option.Name] = option.Value.ValueKind == JsonValueKind.String ? option.Value.GetString() ?? "" : option.Value.ToString();
            }
        }
        else
        {
            problems.Add($"{label}: no 'options' object");
        }

        foreach (var key in options.Keys.Where(k => k != "prefix" && !known.Contains(k, StringComparer.Ordinal)))
        {
            problems.Add($"{label}: unknown option '{key}' (provider reads: {string.Join(", ", known)})");
        }

        foreach (var key in RequiredOptions.GetValueOrDefault(sourceName) ?? [])
        {
            if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"{label}: required option '{key}' is missing");
            }
        }

        if (isLocal)
        {
            return problems; // The Local validator creates the directory on disk; nothing else to check.
        }

        // Placeholders such as "your_key" make the validator report "not configured"; swap in real-looking values.
        var concrete = options.ToDictionary(o => o.Key, o => string.IsNullOrWhiteSpace(o.Value) || o.Value.Contains("your_", StringComparison.OrdinalIgnoreCase) ? "value" : o.Value);
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.StorageSources).Returns(new Dictionary<string, StorageConfig>
        {
            [sourceName] = new StorageConfig { Path = "", Options = concrete }
        });

        var result = new ConfigValidator(new TestLogger()).ValidateStorageOnly(config.Object, sourceName);
        problems.AddRange(result.Errors.Select(e => $"{label}: validator error: {e}"));
        problems.AddRange(result.Warnings
            .Where(w => w.Contains("not configured") || w.Contains("requires") || w.Contains("Requires"))
            .Select(w => $"{label}: validator warning: {w}"));
        return problems;
    }

    /// <summary>
    /// docs/agents.md tells agents to preview with <c>--dry-run</c> / <c>--plan</c>: every command it shows with one of them must
    /// really accept it (the options <c>commands --json</c> lists), so the guide never recommends a preview that exits 2.
    /// </summary>
    [Fact]
    public void AgentGuide_EveryPreviewFlagItShowsIsAcceptedByThatCommand()
    {
        var guide = Path.Combine(FindRepositoryRoot(), "docs", "agents.md");
        var command = new Regex(@"restore(?:\.exe)? ((?:[a-z][a-z-]*)(?: [a-z][a-z-]*)?)(?<rest>[^`\r\n]*)", RegexOptions.Compiled);
        var problems = new List<string>();
        var checkedCount = 0;

        foreach (var line in File.ReadAllLines(guide))
        {
            foreach (System.Text.RegularExpressions.Match match in command.Matches(line))
            {
                var rest = match.Groups["rest"].Value;
                var flags = new[] { "--dry-run", "--plan" }.Where(f => rest.Contains(f, StringComparison.Ordinal) || match.Groups[1].Value.Contains(f, StringComparison.Ordinal)).ToList();
                if (flags.Count == 0)
                {
                    continue;
                }

                var words = match.Groups[1].Value.Split(' ');
                var spec = (words.Length == 2 ? CommandRegistry.Find(string.Join(' ', words)) : null) ?? CommandRegistry.Find(words[0]);
                if (spec == null)
                {
                    problems.Add($"'{match.Value.Trim()}': no such command");
                    continue;
                }

                foreach (var flag in flags)
                {
                    checkedCount++;
                    if (!spec.Options.Any(o => o.Name == flag))
                    {
                        problems.Add($"'{spec.Name}' does not accept {flag} (line: {line.Trim()})");
                    }
                }
            }
        }

        checkedCount.Should().BeGreaterThan(4, "the guide shows several previews; the check must actually see them");
        problems.Should().BeEmpty();
    }

    [Fact]
    public void AgentGuide_DoesNotPromiseAPreviewForCommandsWithout()
    {
        var text = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "agents.md"));

        text.Should().NotContain("`config import` act at once too. For those, preview first");
        foreach (var name in new[] { "system-undo", "config import", "schedule remove" })
        {
            var spec = CommandRegistry.Find(name)!;
            spec.Options.Select(o => o.Name).Should().NotContain(["--dry-run", "--plan"], $"{name} has no preview, so the guide must not say it does");
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "agents.md")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("docs/agents.md was not found above " + AppContext.BaseDirectory);
    }

    private static string FindProviderDocsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "storage", "providers");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("docs/storage/providers was not found above " + AppContext.BaseDirectory);
    }
}

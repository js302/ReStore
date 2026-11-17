using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReStore.Core.src.utils;

public enum WingetInstallOutcome
{
    Installed,
    AlreadyInstalled,
    RebootRequired,
    NotFound,
    Failed
}

public sealed record WingetInstallStatus(WingetInstallOutcome Outcome, string Description)
{
    public bool IsSuccess => Outcome is WingetInstallOutcome.Installed or WingetInstallOutcome.AlreadyInstalled or WingetInstallOutcome.RebootRequired;
}

public sealed record WingetExportEntry(string Id, string Source);

/// <summary>Pure helpers for winget: id validation, argument building, exit-code mapping and output parsing.</summary>
public static partial class WingetHelper
{
    // winget exit codes (HRESULT-style); see winget's returnCodes documentation.
    private const int PackageAlreadyInstalled = unchecked((int)0x8A150061);
    private const int RebootRequiredToFinish = unchecked((int)0x8A150109);
    private const int NoPackagesFound = unchecked((int)0x8A150014);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9.\-_+]*\z")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdPattern().IsMatch(id);

    /// <summary>Arguments for an exact-id, non-interactive install. The id must already be validated.</summary>
    public static IReadOnlyList<string> BuildInstallArguments(string id)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"'{id}' is not a valid winget package id.", nameof(id));
        }

        return
        [
            "install", "--exact", "--id", id,
            "--silent", "--disable-interactivity",
            "--accept-package-agreements", "--accept-source-agreements"
        ];
    }

    public static WingetInstallStatus DescribeInstallExitCode(int exitCode)
    {
        return exitCode switch
        {
            0 => new(WingetInstallOutcome.Installed, "installed"),
            PackageAlreadyInstalled => new(WingetInstallOutcome.AlreadyInstalled, "already installed"),
            RebootRequiredToFinish => new(WingetInstallOutcome.RebootRequired, "installed, restart required to finish"),
            NoPackagesFound => new(WingetInstallOutcome.NotFound, "package not found in the winget sources"),
            _ => new(WingetInstallOutcome.Failed, $"winget exit code {exitCode} (0x{exitCode:X8})")
        };
    }

    /// <summary>
    /// Parses the JSON written by <c>winget export -o file</c>: <c>Sources[].Packages[].PackageIdentifier</c> with the
    /// source name from <c>SourceDetails.Name</c>. Property names are matched case-insensitively; invalid ids are dropped.
    /// </summary>
    public static List<WingetExportEntry> ParseExportJson(string json)
    {
        var entries = new List<WingetExportEntry>();
        using var document = JsonDocument.Parse(json);
        if (!TryGet(document.RootElement, "Sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (var source in sources.EnumerateArray())
        {
            var sourceName = "";
            if (TryGet(source, "SourceDetails", out var details) && TryGet(details, "Name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String)
            {
                sourceName = nameElement.GetString() ?? "";
            }

            if (!TryGet(source, "Packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var package in packages.EnumerateArray())
            {
                if (TryGet(package, "PackageIdentifier", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && idElement.GetString() is { } id
                    && IsValidId(id))
                {
                    entries.Add(new WingetExportEntry(id, sourceName));
                }
            }
        }

        return entries;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>Parses the fixed-width table printed by <c>winget list</c>.</summary>
    public static List<InstalledProgram> ParseListOutput(string output)
    {
        var programs = new List<InstalledProgram>();

        // Progress spinners are separated by '\r' on the same line as the table header; keep only what follows.
        var lines = output.Split('\n')
            .Select(l => { var t = l.TrimEnd('\r'); var i = t.LastIndexOf('\r'); return i >= 0 ? t[(i + 1)..] : t; })
            .Where(l => l.Length > 0)
            .ToList();

        var headerLine = lines.FirstOrDefault(l => l.StartsWith("Name") && l.Contains("Id") && l.Contains("Version"));
        if (headerLine == null)
        {
            return programs;
        }

        var idIndex = headerLine.IndexOf("Id", StringComparison.Ordinal);
        var versionIndex = headerLine.IndexOf("Version", StringComparison.Ordinal);
        var availableIndex = headerLine.IndexOf("Available", StringComparison.Ordinal);
        var sourceIndex = headerLine.IndexOf("Source", StringComparison.Ordinal);
        if (idIndex < 0 || versionIndex <= idIndex)
        {
            return programs;
        }

        // The Version column ends where the next column starts. winget omits "Available" when nothing has an update,
        // in which case Source directly follows Version.
        var versionEnd = new[] { availableIndex, sourceIndex }.Where(i => i > versionIndex).DefaultIfEmpty(-1).Min();

        var dataLines = lines.SkipWhile(l => !l.StartsWith("---")).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l));
        foreach (var line in dataLines)
        {
            if (line.Length < versionIndex)
            {
                continue;
            }

            var name = line[..idIndex].Trim();
            var id = line[idIndex..versionIndex].Trim();

            var end = versionEnd > 0 ? Math.Min(versionEnd, line.Length) : line.Length;
            var version = line[versionIndex..end].Trim().TrimStart('<', '>', ' ');

            var source = sourceIndex > 0 && line.Length > sourceIndex ? line[sourceIndex..].Trim() : "";

            if (string.IsNullOrEmpty(name) || name.StartsWith('-'))
            {
                continue;
            }

            var validId = IsValidId(id);
            programs.Add(new InstalledProgram
            {
                Name = name,
                Version = version,
                // ARP\... style ids are not installable winget ids.
                WingetId = validId ? id : "",
                Source = string.IsNullOrEmpty(source) ? "registry" : source,
                IsWingetAvailable = validId && source.Equals("winget", StringComparison.OrdinalIgnoreCase)
            });
        }

        return programs;
    }
}

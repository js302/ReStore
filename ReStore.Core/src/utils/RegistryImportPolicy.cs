namespace ReStore.Core.src.utils;

/// <summary>Result of validating the text of a <c>.reg</c> file against the import allow-list.</summary>
public sealed class RegFileValidation
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = [];
    public List<string> Keys { get; } = [];
}

/// <summary>
/// Allow-list for restoring <c>.reg</c> files. Only the keys Windows settings backup exports may be
/// written back; anything else (including key deletions) is rejected before <c>reg import</c> runs.
/// </summary>
public static class RegistryImportPolicy
{
    private const string MachineHivePrefix = "HKEY_LOCAL_MACHINE";

    /// <summary>Allowed key prefixes, derived from the export definitions.</summary>
    public static IReadOnlyList<string> AllowedKeyPrefixes { get; } =
        [.. WindowsSettingsDefinitions.CreateCategories().Values
            .SelectMany(keys => keys)
            .Select(k => k.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    public static bool IsMachineKey(string registryPath)
        => registryPath.StartsWith(MachineHivePrefix + "\\", StringComparison.OrdinalIgnoreCase)
           || registryPath.Equals(MachineHivePrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsKeyAllowed(string registryPath)
        => IsKeyUnder(registryPath, AllowedKeyPrefixes);

    /// <summary>
    /// Validates the text of a <c>.reg</c> file: it must carry a registry-editor header, have at least one key,
    /// contain no key deletions, and every key must be under an allowed prefix (and, when
    /// <paramref name="expectedKey"/> is given, under that exported key).
    /// </summary>
    public static RegFileValidation Validate(string regText, string? expectedKey = null)
    {
        var result = new RegFileValidation();
        var sawHeader = false;

        // reg.exe may treat a lone CR (or NUL) as a line break the validator would not see, so such files are refused.
        if (regText.Contains('\0'))
        {
            result.Errors.Add("The file contains NUL characters.");
            return result;
        }

        var normalized = regText.Replace("\r\n", "\n");
        if (normalized.Contains('\r'))
        {
            result.Errors.Add("The file contains a bare carriage return.");
            return result;
        }

        var continued = false;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (continued)
            {
                // Continuation of a value ending in '\' (hex data). It must not look like a key line.
                if (line.StartsWith('['))
                {
                    result.Errors.Add($"Key line inside a value continuation: {Truncate(line)}");
                    return result;
                }

                continued = line.EndsWith('\\');
                continue;
            }

            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (!sawHeader)
            {
                if (line.Equals("Windows Registry Editor Version 5.00", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("REGEDIT4", StringComparison.OrdinalIgnoreCase))
                {
                    sawHeader = true;
                    continue;
                }

                result.Errors.Add("Missing registry file header.");
                return result;
            }

            if (line[0] != '[')
            {
                continued = line.EndsWith('\\');
                continue;
            }

            if (line[^1] != ']')
            {
                result.Errors.Add($"Malformed key line: {Truncate(line)}");
                continue;
            }

            var key = line[1..^1].Trim();
            if (key.StartsWith('-'))
            {
                result.Errors.Add($"Key deletion is not allowed: {Truncate(key)}");
                continue;
            }

            if (!IsKeyAllowed(key))
            {
                result.Errors.Add($"Key is not on the allow-list: {Truncate(key)}");
                continue;
            }

            if (expectedKey != null && !IsKeyUnder(key, [expectedKey]))
            {
                result.Errors.Add($"Key is outside the exported key '{expectedKey}': {Truncate(key)}");
                continue;
            }

            result.Keys.Add(key);
        }

        if (!sawHeader)
        {
            result.Errors.Add("Missing registry file header.");
        }
        else if (result.Keys.Count == 0 && result.Errors.Count == 0)
        {
            result.Errors.Add("The file contains no registry keys.");
        }

        return result;
    }

    private static bool IsKeyUnder(string key, IEnumerable<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string value) => value.Length <= 120 ? value : value[..120] + "...";
}

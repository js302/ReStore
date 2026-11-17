using System.Text;
using System.Text.RegularExpressions;

namespace ReStore.Core.src.utils;

/// <summary>
/// One exclusion pattern. Two flavours:
/// <list type="bullet">
/// <item><b>Legacy</b> (<c>excludedPatterns</c>, <see cref="ParseLegacy"/>): <c>*</c>/<c>?</c> wildcards compared with the file
/// NAME only, files only, exactly as before directory pruning existed. A directory is never matched or pruned by one, and a
/// pattern containing a slash never matches. Existing configs are therefore unaffected.</item>
/// <item><b>Glob</b> (<c>excludeGlobs</c> and the built-in presets, <see cref="Parse"/>): the syntax below, which can prune
/// directories.</item>
/// </list>
/// Glob syntax (matching is case-insensitive):
/// <list type="bullet">
/// <item><c>*</c> any run of characters within one path segment, <c>?</c> one character, <c>**</c> any number of segments
/// (<c>**/</c> may match nothing).</item>
/// <item>A trailing <c>/</c> (or <c>\</c>) makes the pattern match directories only; a directory match excludes the whole subtree.</item>
/// <item>A pattern without a slash is matched against the name of every file and directory at any depth
/// (<c>node_modules/</c>, <c>*.tmp</c>).</item>
/// <item>A pattern with a slash is matched against the end of the full path (<c>**/bin/</c>, <c>*/cache/*</c>,
/// <c>Service Worker/CacheStorage/</c>); one that starts with <c>/</c>, a drive letter or <c>%VAR%</c> must match the whole path.</item>
/// </list>
/// </summary>
public sealed class ExcludePattern
{
    private readonly Regex _regex;
    private readonly bool _matchesFullPath;
    private readonly bool _legacy;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Text, bool Legacy), ExcludePattern?> Cache = new();

    public string Source { get; }
    public bool DirectoryOnly { get; }

    private ExcludePattern(string source, bool directoryOnly, bool matchesFullPath, Regex regex, bool legacy = false)
    {
        _legacy = legacy;
        Source = source;
        DirectoryOnly = directoryOnly;
        _matchesFullPath = matchesFullPath;
        _regex = regex;
    }

    /// <summary>Parsed once per distinct text; the watcher and incremental paths ask for the same patterns on every event.</summary>
    public static ExcludePattern? GetCached(string? text, bool legacy)
        => text == null ? null : Cache.GetOrAdd((text, legacy), key => key.Legacy ? ParseLegacy(key.Text) : Parse(key.Text));

    /// <summary>Legacy <c>excludedPatterns</c> entry: file-name wildcards only (see the class summary). Null for a blank pattern.</summary>
    public static ExcludePattern? ParseLegacy(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var regex = new Regex(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return new ExcludePattern(pattern, false, false, regex, legacy: true);
    }

    /// <summary>Null for a blank pattern.</summary>
    public static ExcludePattern? Parse(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var text = pattern.Trim();
        if (text.Contains('%'))
        {
            text = ConfigManager.ExpandConfiguredPath(text);
        }

        text = text.Replace('\\', '/');
        var directoryOnly = text.EndsWith('/');
        text = text.TrimEnd('/');
        if (text.Length == 0)
        {
            return null;
        }

        var hasSlash = text.Contains('/');
        var absolute = text.StartsWith('/') || (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] == '/');
        var regex = BuildRegex(text, hasSlash && !absolute);
        return new ExcludePattern(pattern, directoryOnly, hasSlash, regex);
    }

    /// <param name="forwardSlashPath">Full path with <c>/</c> separators.</param>
    /// <param name="name">Last segment of the path.</param>
    /// <param name="isDirectory">Null when unknown (a deleted path): directory-only patterns then match too.</param>
    public bool IsMatch(string forwardSlashPath, string name, bool? isDirectory)
    {
        if (DirectoryOnly && isDirectory == false)
        {
            return false;
        }

        if (_legacy && isDirectory == true)
        {
            return false;
        }

        return _regex.IsMatch(_matchesFullPath ? forwardSlashPath : name);
    }

    private static Regex BuildRegex(string pattern, bool matchEnd)
    {
        var builder = new StringBuilder(matchEnd ? "(?:^|/)" : "^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                {
                    i++;
                    builder.Append("(?:.*/)?");
                }
                else
                {
                    builder.Append(".*");
                }
            }
            else if (c == '*')
            {
                builder.Append("[^/]*");
            }
            else if (c == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
}

/// <summary>Built-in exclusion presets selectable with <c>excludePresets</c> in config.json.</summary>
public static class ExcludePresetCatalog
{
    public const string Dev = "dev";
    public const string Caches = "caches";
    public const string OfficeTemp = "office-temp";
    public const string System = "system";

    private static readonly Dictionary<string, string[]> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        [Dev] = ["node_modules/", ".git/", "bin/", "obj/", ".vs/", "target/", "__pycache__/", ".venv/"],
        [Caches] = ["Cache/", "Cache2/", "Code Cache/", "GPUCache/", "ShaderCache/", "Service Worker/CacheStorage/", "Temp/", "*.tmp"],
        [OfficeTemp] = ["~$*", "*.~lock*"],
        [System] = ["Thumbs.db", "desktop.ini", "$RECYCLE.BIN/", "System Volume Information/"]
    };

    public static IReadOnlyList<string> Names { get; } = [Dev, Caches, OfficeTemp, System];

    public static bool IsKnown(string? name) => name != null && Presets.ContainsKey(name.Trim());

    /// <summary>Patterns of the named preset; empty for an unknown name.</summary>
    public static IReadOnlyList<string> PatternsFor(string? name)
        => name != null && Presets.TryGetValue(name.Trim(), out var patterns) ? patterns : [];
}

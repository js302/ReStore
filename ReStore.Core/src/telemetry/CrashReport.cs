using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.telemetry;

/// <summary>The values of <c>telemetry.crashReports</c>: whether a scrubbed crash report may be sent along with telemetry.</summary>
public static class CrashReportMode
{
    /// <summary>Keep the report locally and ask the user (the default).</summary>
    public const string Ask = "ask";

    public const string Always = "always";
    public const string Never = "never";

    public static readonly string[] All = [Ask, Always, Never];

    /// <summary>The canonical (lower-case) value, or null when <paramref name="value"/> is not one of <see cref="All"/>.</summary>
    public static string? Normalize(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        return text != null && Array.IndexOf(All, text) >= 0 ? text : null;
    }
}

/// <summary>What <see cref="TelemetryClient.RecordCrash"/> did with a crash.</summary>
public enum CrashDisposition
{
    /// <summary>Nothing was stored: telemetry is off, crash reports are set to <c>never</c>, or the crash was a repeat.</summary>
    None,

    /// <summary>Stored locally as a pending report; nothing is sent until the user agrees.</summary>
    Pending,

    /// <summary>Moved to the upload queue (<c>always</c>).</summary>
    Queued
}

public sealed record CrashOutcome(string? Id, string Mode, CrashDisposition Disposition)
{
    public static CrashOutcome None { get; } = new(null, CrashReportMode.Ask, CrashDisposition.None);
}

/// <summary>The facts a crash report carries besides the exception (all of them closed or random values).</summary>
public sealed class CrashReportContext
{
    public string AppVersion { get; set; } = "";
    public string Channel { get; set; } = AppChannel.Current;
    public string Arch { get; set; } = AppChannel.Architecture;
    public string OsVersion { get; set; } = Environment.OSVersion.Version.ToString(3);
    public string Surface { get; set; } = "cli";
    public string? InstallId { get; set; }
    public string SessionId { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Extra strings (bucket names, group names, ...) that must never survive in a message.</summary>
    public IReadOnlyCollection<string> SensitiveTerms { get; set; } = [];
}

/// <summary>One scrubbed exception of a report.</summary>
public sealed record CrashExceptionInfo(int Depth, string Type, int HResult, string Message, IReadOnlyList<string> Frames, int FramesOmitted);

/// <summary>
/// A scrubbed crash report: exception types, scrubbed messages and <c>Namespace.Type.Method</c> frames only (see
/// <see cref="CrashScrubber"/>). <see cref="ToJson"/> is the exact single-line event that is sent (event name <c>crash.report</c>, the
/// same common properties as every other telemetry event, the details under <c>props</c>). See docs/telemetry.md.
/// </summary>
public sealed class CrashReport
{
    public const string EventName = "crash.report";

    public required string Id { get; init; }
    public required CrashReportContext Context { get; init; }
    public required IReadOnlyList<CrashExceptionInfo> Exceptions { get; init; }

    public string ToJson()
    {
        var context = Context;
        var document = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = EventName,
            ["ts"] = context.CreatedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["installId"] = context.InstallId,
            ["sessionId"] = context.SessionId,
            ["appVersion"] = context.AppVersion,
            ["channel"] = context.Channel,
            ["arch"] = context.Arch,
            ["osVersion"] = context.OsVersion,
            ["surface"] = context.Surface,
            ["props"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Id,
                ["exceptions"] = Exceptions.Select(e => new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["depth"] = e.Depth,
                    ["type"] = e.Type,
                    ["hResult"] = e.HResult,
                    ["message"] = e.Message,
                    ["frames"] = e.Frames,
                    ["framesOmitted"] = e.FramesOmitted
                }).ToList()
            }
        };
        return JsonSerializer.Serialize(document);
    }
}

/// <summary>Builds a <see cref="CrashReport"/> from an exception: bounded depth, frame counts and text, everything scrubbed.</summary>
public static class CrashReportBuilder
{
    public const int MaxInnerDepth = 5;
    public const int MaxExceptions = 10;
    public const int MaxFramesPerException = 50;

    public static CrashReport Build(Exception exception, CrashReportContext context, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(context);

        var list = new List<CrashExceptionInfo>();
        Walk(exception, 0, list, context.SensitiveTerms);
        return new CrashReport { Id = id ?? NewId(), Context = context, Exceptions = list };
    }

    /// <summary>12 random lower-case hex characters.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static bool IsValidId(string? id) => id is { Length: 12 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static void Walk(Exception exception, int depth, List<CrashExceptionInfo> list, IReadOnlyCollection<string> terms)
    {
        if (list.Count >= MaxExceptions)
        {
            return;
        }

        var frames = new List<string>();
        var omitted = 0;
        try
        {
            foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
            {
                var method = frame.GetMethod();
                var name = CrashScrubber.ScrubFrame(method?.DeclaringType?.FullName, method?.Name);
                if (name == CrashScrubber.External && frames.Count > 0 && frames[^1] == CrashScrubber.External)
                {
                    continue;
                }

                if (frames.Count >= MaxFramesPerException)
                {
                    omitted++;
                }
                else
                {
                    frames.Add(name);
                }
            }
        }
        catch (Exception)
        {
            // No usable stack: the report simply has no frames.
        }

        list.Add(new CrashExceptionInfo(
            depth,
            CrashScrubber.ScrubTypeName(exception.GetType().FullName),
            exception.HResult,
            CrashScrubber.ScrubMessage(exception.Message, terms),
            frames,
            omitted));

        if (depth >= MaxInnerDepth)
        {
            return;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.Take(MaxExceptions))
            {
                Walk(inner, depth + 1, list, terms);
            }
        }
        else if (exception.InnerException is { } single)
        {
            Walk(single, depth + 1, list, terms);
        }
    }
}

/// <summary>
/// Removes everything personal from the text of a crash report. Messages go through <see cref="DiagnosticsReport.RedactText"/>, then
/// URLs, e-mail addresses, quoted values, paths of every kind (drive, UNC, %VAR%, /home/..., relative ones with backslashes),
/// file names with a well-known extension, GUIDs, IP addresses and long base64/hex tokens are replaced by placeholders, as are the
/// user, machine and configured resource names; the result is one line of at most <see cref="MaxMessageLength"/> characters.
/// Paths are removed up to the end of their line, because a path may contain spaces and its tail would otherwise leak.
/// </summary>
public static class CrashScrubber
{
    public const int MaxMessageLength = 500;
    public const string External = "<external>";

    /// <summary>Namespaces whose type names and frames are kept verbatim: the platform, ReStore and the SDKs it uses.</summary>
    public static readonly string[] KnownNamespaces =
    [
        "System.", "Microsoft.", "Windows.", "WinRT.", "MS.", "ReStore.", "Amazon.", "AWSSDK.", "Azure.", "Google.", "Dropbox.", "Octokit.", "Renci."
    ];

    private static readonly Regex Url = new(@"\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s'""<>]+", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Quoted = new(@"(?<!\w)(?:'[^'\r\n]*'|""[^""\r\n]*""|\u2018[^\u2019\r\n]*\u2019|\u201C[^\u201D\r\n]*\u201D)(?!\w)", RegexOptions.Compiled);
    private static readonly Regex PathToEndOfLine = new(
        @"(?<![\w])(?:[A-Za-z]:[\\/]|\\\\|%[A-Za-z_][\w()]*%|~[\\/]|\$HOME\b|\$env:)[^\r\n]*"
        + @"|(?<![\w:/.\\)\]])/(?:home|Users|root|mnt|var|tmp|etc|opt|usr|srv|media|Volumes|private|run|proc|dev|bin|lib|snap|Library|Applications|data)\b[^\r\n]*"
        + @"|(?<![\w:/.\\)\]])/[\w.\-]+/[^\r\n]*",
        RegexOptions.Compiled);
    private static readonly Regex BackslashToken = new(@"[^\s]*\\[^\s]*", RegexOptions.Compiled);
    private static readonly Regex FileName = new(
        @"[\w\-.()\[\]]+\.(?:txt|log|md|docx?|xlsx?|pptx?|pdf|csv|json|xml|ya?ml|ini|cfg|conf|config|jpe?g|png|gif|bmp|heic|svg|mp[34]|mov|avi|mkv|wav|zip|7z|rar|tar|gz|bak|tmp|db|sqlite|sql|key|pem|pfx|cer|kdbx|ps1|bat|cmd|sh|lnk|iso|vhdx?|pst|eml)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GuidPattern = new(@"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
    private static readonly Regex IpV4 = new(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.Compiled);
    private static readonly Regex IpV6 = new(@"(?<![\w:])(?:[0-9a-fA-F]{1,4}:){2,7}[0-9a-fA-F]{0,4}(?![\w:])", RegexOptions.Compiled);
    private static readonly Regex Token = new(@"[A-Za-z0-9+/_\-]{20,}={0,2}", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex FrameChars = new(@"^[A-Za-z0-9_.+`<>$|,]+$", RegexOptions.Compiled);

    private static readonly string[] ConfigTermKeys =
        ["bucket", "container", "repo", "owner", "account", "host", "user", "name", "group", "folder", "endpoint", "project", "site", "email", "tenant", "label"];

    private static readonly HashSet<string> IgnoredTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null", "none", "local", "default", "backup", "restore", "restores", "backups", "system", "the", "and", "ask", "always", "never"
    };

    /// <summary>Scrubs a free-text message; <paramref name="sensitiveTerms"/> (plus the user and machine name) never survive.</summary>
    public static string ScrubMessage(string? message, IEnumerable<string>? sensitiveTerms = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "";
        }

        var text = DiagnosticsReport.RedactText(message);
        text = Url.Replace(text, "<url>");
        text = Email.Replace(text, "<email>");
        var unquoted = text;
        text = Quoted.Replace(unquoted, m => KeepParameterName(unquoted, m) ? m.Value : "<value>");
        text = PathToEndOfLine.Replace(text, "<path>");
        text = BackslashToken.Replace(text, "<path>");
        text = FileName.Replace(text, "<file>");
        text = GuidPattern.Replace(text, "<guid>");
        text = IpV4.Replace(text, "<ip>");
        text = IpV6.Replace(text, "<ip>");
        text = Token.Replace(text, m => m.Value.Any(char.IsAsciiDigit) || m.Value.Length >= 40 ? "<token>" : m.Value);
        // Configured names last: replacing one inside an address, URL or file name earlier would leave the rest of it unrecognisable to the patterns above.
        text = ReplaceTerms(text, EnvironmentTerms().Concat(sensitiveTerms ?? []));
        text = Whitespace.Replace(text, " ").Trim();
        return text.Length > MaxMessageLength ? text[..(MaxMessageLength - 1)] + "\u2026" : text;
    }

    /// <summary>A type name as is when it belongs to <see cref="KnownNamespaces"/> and looks like a type name, else <see cref="External"/>.</summary>
    public static string ScrubTypeName(string? fullName)
    {
        var name = StripGenericArguments(fullName);
        return IsKnown(name) && name.Length <= 200 && FrameChars.IsMatch(name) ? name : External;
    }

    /// <summary>One stack frame as <c>Namespace.Type.Method</c>; <see cref="External"/> unless the type is from <see cref="KnownNamespaces"/>.</summary>
    public static string ScrubFrame(string? declaringTypeFullName, string? methodName)
    {
        var type = StripGenericArguments(declaringTypeFullName);
        if (!IsKnown(type) || string.IsNullOrEmpty(methodName))
        {
            return External;
        }

        var name = type + "." + methodName;
        return name.Length <= 200 && FrameChars.IsMatch(name) ? name : External;
    }

    /// <summary>The user, machine and profile folder names of this process.</summary>
    public static IEnumerable<string> EnvironmentTerms()
    {
        var terms = new List<string>();
        TryAdd(terms, () => Environment.UserName);
        TryAdd(terms, () => Environment.MachineName);
        TryAdd(terms, () => Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/')));
        return terms;
    }

    /// <summary>
    /// Names the user chose in config.json (storage names, bucket/container/repository/owner/account/host/user/group names, ...),
    /// so a message that mentions one of them is scrubbed. Best effort: unreadable configs yield none.
    /// </summary>
    public static List<string> CollectConfigTerms(string configPath, int max = 200)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(configPath))
            {
                return [];
            }

            using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            Collect(document.RootElement, null, terms, max);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Not readable: no extra terms.
        }

        return [.. terms];
    }

    private static void Collect(JsonElement element, string? key, HashSet<string> terms, int max)
    {
        if (terms.Count >= max)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // The names of storage sources and watch groups are user-chosen too.
                    if (property.Value.ValueKind == JsonValueKind.Object && key is "storageSources" or "watchGroups" or "groups")
                    {
                        AddTerm(terms, property.Name);
                    }

                    Collect(property.Value, property.Name, terms, max);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, key, terms, max);
                }

                break;
            case JsonValueKind.String when key != null && ConfigTermKeys.Any(k => key.Contains(k, StringComparison.OrdinalIgnoreCase)):
                AddTerm(terms, element.GetString());
                break;
        }
    }

    private static void AddTerm(HashSet<string> terms, string? value)
    {
        var term = value?.Trim();
        if (term is { Length: >= 3 and <= 100 } && !IgnoredTerms.Contains(term) && !term.StartsWith("env:", StringComparison.Ordinal))
        {
            terms.Add(term);
        }
    }

    private static string ReplaceTerms(string text, IEnumerable<string> terms)
    {
        foreach (var term in terms.Where(t => t.Length >= 3 && !IgnoredTerms.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length))
        {
            text = text.Replace(term, "<redacted>", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private static bool KeepParameterName(string text, Match match)
    {
        var inner = match.Value.Length >= 2 ? match.Value[1..^1] : "";
        if (inner.Length == 0 || inner.Length > 64 || !(char.IsAsciiLetter(inner[0]) || inner[0] == '_') || !inner.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            return false;
        }

        var before = text[..match.Index];
        return before.EndsWith("Parameter ", StringComparison.OrdinalIgnoreCase) || before.EndsWith("(Parameter ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnown(string name) => KnownNamespaces.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static string StripGenericArguments(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName))
        {
            return "";
        }

        var bracket = fullName.IndexOf('[', StringComparison.Ordinal);
        return bracket >= 0 ? fullName[..bracket] : fullName;
    }

    private static void TryAdd(List<string> terms, Func<string?> get)
    {
        try
        {
            if (get() is { Length: >= 3 } value)
            {
                terms.Add(value);
            }
        }
        catch (Exception)
        {
            // Not available on this platform.
        }
    }
}

/// <summary>Short summary of a stored crash report, for lists.</summary>
public sealed record CrashReportSummary(string Id, DateTime CreatedUtc, string? ExceptionType, int ExceptionCount, long Bytes);

/// <summary>
/// The pending crash reports (<c>state/crash-reports/&lt;id&gt;.json</c>, one scrubbed single-line event each): reports waiting for
/// the user's decision. Bounded: only the newest <see cref="DefaultMax"/> are kept. Never throws.
/// </summary>
public sealed class CrashReportStore(string directory, int max = CrashReportStore.DefaultMax)
{
    public const int DefaultMax = 20;

    private static readonly UTF8Encoding Utf8 = new(false);

    public string Directory { get; } = directory;

    public bool Save(string id, string json)
    {
        if (!CrashReportBuilder.IsValidId(id) || json.Contains('\n') || json.Contains('\r'))
        {
            return false;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = Path.Combine(Directory, id + "." + System.Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, json, Utf8);
                File.Move(temp, FileFor(id), overwrite: true);
            }
            catch
            {
                ReStore.Core.src.utils.SharedFile.TryDelete(temp);
                throw;
            }
            Prune();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The report's JSON line, or null when there is no such report.</summary>
    public string? Read(string id)
    {
        if (!CrashReportBuilder.IsValidId(id))
        {
            return null;
        }

        try
        {
            var path = FileFor(id);
            return File.Exists(path) ? File.ReadAllText(path, Utf8).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Delete(string id)
    {
        if (!CrashReportBuilder.IsValidId(id))
        {
            return false;
        }

        try
        {
            var path = FileFor(id);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public int DeleteAll() => List().Count(summary => Delete(summary.Id));

    public int Count() => Files().Count();

    /// <summary>Newest first.</summary>
    public List<CrashReportSummary> List()
    {
        var result = new List<CrashReportSummary>();
        foreach (var file in Files())
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            var created = file.LastWriteTimeUtc;
            string? type = null;
            var count = 0;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file.FullName, Utf8));
                if (document.RootElement.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(ts.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    created = parsed;
                }

                if (document.RootElement.TryGetProperty("props", out var props) && props.TryGetProperty("exceptions", out var exceptions) && exceptions.ValueKind == JsonValueKind.Array)
                {
                    count = exceptions.GetArrayLength();
                    if (count > 0 && exceptions[0].TryGetProperty("type", out var t))
                    {
                        type = t.GetString();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Damaged file: still listed so it can be deleted.
            }

            result.Add(new CrashReportSummary(id, created, type, count, file.Length));
        }

        return [.. result.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id, StringComparer.Ordinal)];
    }

    private string FileFor(string id) => Path.Combine(Directory, id + ".json");

    private IEnumerable<FileInfo> Files()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return [];
            }

            return new DirectoryInfo(Directory).EnumerateFiles("*.json")
                .Where(f => CrashReportBuilder.IsValidId(Path.GetFileNameWithoutExtension(f.Name)))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Prune()
    {
        var excess = Files().OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(max).ToList();
        foreach (var file in excess)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retried on the next save.
            }
        }
    }
}

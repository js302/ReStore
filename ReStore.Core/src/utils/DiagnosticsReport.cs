using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ReStore.Core.src.utils;

/// <summary>Builds the text users paste into a bug report: versions, OS, redacted config and the end of the log.</summary>
public static partial class DiagnosticsReport
{
    public const int DefaultLogLines = 200;

    /// <summary>Builds the report from the files on disk; never throws for a missing or unreadable file.</summary>
    public static async Task<string> BuildAsync(IConfigManager configManager, string? appVersion = null, int logLines = DefaultLogLines)
    {
        string? configJson = null;
        try
        {
            configJson = await File.ReadAllTextAsync(configManager.GetConfigFilePath()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported as "unreadable" below.
        }

        return Build(appVersion, configJson, ReadLogTail(AppPaths.LogFile, logLines));
    }

    public static string Build(string? appVersion, string? configJson, IEnumerable<string> logLines)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ReStore diagnostics");
        builder.AppendLine($"Generated: {DateTime.UtcNow:O}");
        builder.AppendLine($"ReStore: {appVersion ?? typeof(DiagnosticsReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown"}");
        builder.AppendLine($"Core: {typeof(DiagnosticsReport).Assembly.GetName().Version}");
        builder.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        builder.AppendLine();
        builder.AppendLine("--- config.json (secrets redacted) ---");
        if (configJson == null)
        {
            builder.AppendLine("(config.json could not be read)");
        }
        else
        {
            try
            {
                builder.AppendLine(RedactText(ConfigSecretsCommands.RedactConfigJson(configJson)));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                builder.AppendLine("(config.json is not valid JSON; not included)");
            }
        }

        builder.AppendLine();
        builder.AppendLine("--- log (last lines, redacted) ---");
        foreach (var line in logLines)
        {
            builder.AppendLine(RedactText(line));
        }

        return builder.ToString();
    }

    /// <summary>Masks secrets that may appear in free text: secret option assignments, dpapi blobs, bearer tokens, account keys and URL credentials.</summary>
    public static string RedactText(string text)
    {
        var redacted = DpapiBlobPattern().Replace(text, SecretReferences.DpapiPrefix + "…");
        redacted = SecretAssignmentPattern().Replace(redacted, m => m.Groups[1].Value + SecretReferences.RedactedValue);
        redacted = BearerPattern().Replace(redacted, "Bearer " + SecretReferences.RedactedValue);
        redacted = UrlCredentialPattern().Replace(redacted, m => m.Groups[1].Value + SecretReferences.RedactedValue + "@");
        redacted = QueryStringPattern().Replace(redacted, m => RedactQuery(m.Value));
        return SignatureParameterPattern().Replace(redacted, m => m.Groups[1].Value + SecretReferences.RedactedValue);
    }

    /// <summary>Masks signature-style query parameters; when a SAS signature (<c>sig</c>) is present its companion parameters (se, sp, sv, ...) are masked too.</summary>
    private static string RedactQuery(string query)
    {
        var result = SignatureParameterPattern().Replace(query, m => m.Groups[1].Value + SecretReferences.RedactedValue);
        return SasSignaturePattern().IsMatch(query)
            ? SasCompanionPattern().Replace(result, m => m.Groups[1].Value + SecretReferences.RedactedValue)
            : result;
    }

    /// <summary>The last <paramref name="count"/> lines of <paramref name="path"/>, read with shared access (the logger keeps writing).</summary>
    public static List<string> ReadLogTail(string path, int count)
    {
        var tail = new Queue<string>(count + 1);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > count)
                {
                    tail.Dequeue();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No log yet, or it is locked: the report simply has no log section content.
        }

        return [.. tail];
    }

    [GeneratedRegex(@"dpapi:[A-Za-z0-9+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex DpapiBlobPattern();

    [GeneratedRegex(
        @"((?:secretAccessKey|sessionToken|applicationKey|connectionString|accessToken|refreshToken|appKey|appSecret|password|passphrase|client_secret|AccountKey|SharedAccessSignature|token)""?\s*[:=]\s*""?)(?!\*\*\*)[^\s"",;&]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"\?[^\s""'#]+")]
    private static partial Regex QueryStringPattern();

    [GeneratedRegex(@"([?&;]?\b(?:sig|signature|X-Amz-Signature|X-Amz-Credential|X-Amz-Security-Token)=)[^&\s""';]+", RegexOptions.IgnoreCase)]
    private static partial Regex SignatureParameterPattern();

    [GeneratedRegex(@"(?:^|[?&])sig=", RegexOptions.IgnoreCase)]
    private static partial Regex SasSignaturePattern();

    [GeneratedRegex(@"((?:^|[?&])(?:se|sp|sv|sr|st|spr|sip|skoid|sktid)=)[^&\s""';]+", RegexOptions.IgnoreCase)]
    private static partial Regex SasCompanionPattern();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"([a-z][a-z0-9+.\-]*://[^/\s:@]+:)[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentialPattern();
}

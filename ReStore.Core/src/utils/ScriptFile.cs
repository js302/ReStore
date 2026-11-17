using System.Text;

namespace ReStore.Core.src.utils;

/// <summary>Helpers for the human-runnable PowerShell scripts that are archived next to the JSON data.</summary>
public static class ScriptFile
{
    /// <summary>UTF-8 with BOM so Windows PowerShell 5.1 reads non-ASCII names correctly.</summary>
    public static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static Task WriteAllTextAsync(string path, string content)
        => File.WriteAllTextAsync(path, content, Utf8WithBom);

    /// <summary>
    /// Escapes text for use inside a single-quoted PowerShell string. PowerShell also treats the typographic
    /// single quotes as quote characters, so those are doubled as well.
    /// </summary>
    public static string EscapeSingleQuoted(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        foreach (var ch in value)
        {
            sb.Append(ch);
            if (ch is '\'' or '‘' or '’' or '‚' or '‛')
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>Like <see cref="EscapeSingleQuoted"/> but also flattens line breaks (for names and labels).</summary>
    public static string EscapeSingleQuotedLabel(string value)
        => EscapeSingleQuoted(value).ReplaceLineEndings(" ");

    /// <summary>Rewrites an existing script as UTF-8 with BOM when it has none and is valid UTF-8.</summary>
    public static void EnsureBom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return;
        }

        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return; // UTF-16 with BOM already.
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return; // Not UTF-8 (legacy ANSI); leave untouched rather than corrupt it.
        }

        File.WriteAllText(path, text, Utf8WithBom);
    }
}

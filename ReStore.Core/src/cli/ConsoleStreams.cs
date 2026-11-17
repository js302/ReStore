using System.Text;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary>
/// Makes redirected standard streams UTF-8 on Windows. When stdout/stderr/stdin are pipes or files (agents, scripts, Task Scheduler),
/// .NET uses the console's legacy code page (e.g. 437/850), so non-ASCII file names in <c>--json</c> output come out garbled and a
/// non-ASCII password piped to <c>--password-stdin</c> is misread. Interactive consoles are left alone (they already write Unicode),
/// and the console code page itself is never changed, so the user's terminal is unaffected after the command exits.
/// </summary>
internal static class ConsoleStreams
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>True for encodings that can represent every character (UTF-8/16/32).</summary>
    internal static bool IsUnicode(Encoding encoding) => encoding.CodePage is 65001 or 1200 or 1201 or 12000 or 12001;

    public static void UseUtf8ForRedirectedStreams()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // .NET on Linux/macOS already uses UTF-8 for the standard streams.
        }

        try
        {
            if (Console.IsOutputRedirected && !IsUnicode(Console.Out.Encoding))
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = true });
            }

            if (Console.IsErrorRedirected && !IsUnicode(Console.Error.Encoding))
            {
                Console.SetError(new StreamWriter(Console.OpenStandardError(), Utf8NoBom) { AutoFlush = true });
            }

            // In-process tests (which always override the data root) supply their own stdin reader; never replace it.
            if (Console.IsInputRedirected && !IsUnicode(Console.InputEncoding) && !AppPaths.IsRootOverridden)
            {
                Console.SetIn(new StreamReader(Console.OpenStandardInput(), Utf8NoBom, detectEncodingFromByteOrderMarks: true));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Keep the default streams rather than fail the command.
        }
    }
}

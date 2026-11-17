using System.Runtime.InteropServices;
using System.Text;

namespace ReStore.Core.src.utils;

/// <summary>
/// How this copy of ReStore was installed: <see cref="Msix"/> (running with package identity), <see cref="Portable"/>
/// (a <c>ReStore.channel</c> marker file containing <c>portable</c> next to the executable) or <see cref="Dev"/>
/// (anything else: a build output folder, tests). Used by the CLI <c>version</c> command and by later update and
/// diagnostics code, which must behave differently per channel.
/// </summary>
public static class AppChannel
{
    public const string Msix = "msix";
    public const string Portable = "portable";
    public const string Dev = "dev";

    /// <summary>Name of the marker file the portable build ships next to the executable.</summary>
    public const string MarkerFileName = "ReStore.channel";

    private static readonly Lazy<string> Detected = new(() => Determine(HasPackageIdentity(), ReadMarker(AppContext.BaseDirectory)));

    /// <summary>The channel of the running process (detected once).</summary>
    public static string Current => Detected.Value;

    /// <summary>The decision logic: package identity wins, then the portable marker, otherwise dev.</summary>
    public static string Determine(bool hasPackageIdentity, string? markerContents)
    {
        if (hasPackageIdentity)
        {
            return Msix;
        }

        return string.Equals(markerContents?.Trim(), Portable, StringComparison.OrdinalIgnoreCase) ? Portable : Dev;
    }

    /// <summary>The text of the marker file in <paramref name="directory"/>, or null when it is absent or unreadable.</summary>
    public static string? ReadMarker(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(directory, MarkerFileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Process architecture as a short name: <c>x64</c>, <c>arm64</c>, <c>x86</c> or <c>arm</c>.</summary>
    public static string Architecture => ArchitectureName(RuntimeInformation.ProcessArchitecture);

    public static string ArchitectureName(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        System.Runtime.InteropServices.Architecture.Arm => "arm",
        _ => architecture.ToString().ToLowerInvariant()
    };

    /// <summary>True when the process runs with MSIX package identity. Always false off Windows.</summary>
    public static bool HasPackageIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, null);
            // 15700 (APPMODEL_ERROR_NO_PACKAGE) means unpackaged; success or ERROR_INSUFFICIENT_BUFFER (122) means a package name exists.
            return result is 0 or 122;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows without the packaging API (older than 8): certainly not packaged.
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
}

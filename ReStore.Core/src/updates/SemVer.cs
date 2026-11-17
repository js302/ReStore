using System.Globalization;

namespace ReStore.Core.src.updates;

/// <summary>
/// A semantic version: MAJOR.MINOR.PATCH with an optional <c>-prerelease</c> part. Build metadata (<c>+...</c>) is ignored.
/// A release is newer than any of its pre-releases; pre-release identifiers compare per the SemVer 2.0 rules.
/// </summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    private readonly string[] _prerelease;

    private SemVer(int major, int minor, int patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public bool IsPrerelease => _prerelease.Length > 0;

    /// <summary>Normalised text without build metadata, for example <c>1.8.0-beta.1</c>.</summary>
    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}{(IsPrerelease ? "-" + string.Join('.', _prerelease) : string.Empty)}";

    /// <summary>Parses <c>1.2.3</c>, <c>v1.2.3</c>, <c>1.2.3-rc.1</c> and <c>1.2.3+sha</c>; a missing minor or patch is not accepted.</summary>
    public static bool TryParse(string? text, out SemVer version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.Length > 1 && (value[0] == 'v' || value[0] == 'V'))
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }

        string[] prerelease = [];
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            var pre = value[(dash + 1)..];
            value = value[..dash];
            prerelease = pre.Split('.');
            if (prerelease.Any(id => id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                return false;
            }
        }

        var parts = value.Split('.');
        if (parts.Length != 3
            || !TryNumber(parts[0], out var major) || !TryNumber(parts[1], out var minor) || !TryNumber(parts[2], out var patch))
        {
            return false;
        }

        version = new SemVer(major, minor, patch, prerelease);
        return true;
    }

    public static SemVer? ParseOrNull(string? text) => TryParse(text, out var version) ? version : null;

    private static bool TryNumber(string text, out int number)
    {
        number = 0;
        return text.Length > 0 && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major != other.Major ? Major.CompareTo(other.Major)
            : Minor != other.Minor ? Minor.CompareTo(other.Minor)
            : Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        if (IsPrerelease != other.IsPrerelease)
        {
            return IsPrerelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var result = ComparePart(_prerelease[i], other._prerelease[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static int ComparePart(string a, string b)
    {
        var aNumeric = a.All(char.IsAsciiDigit);
        var bNumeric = b.All(char.IsAsciiDigit);
        if (aNumeric && bNumeric)
        {
            // Compare by length first so very long numbers do not overflow.
            var a0 = a.TrimStart('0');
            var b0 = b.TrimStart('0');
            return a0.Length != b0.Length ? a0.Length.CompareTo(b0.Length) : string.CompareOrdinal(a0, b0);
        }

        if (aNumeric != bNumeric)
        {
            return aNumeric ? -1 : 1;
        }

        return string.CompareOrdinal(a, b);
    }

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemVer other && Equals(other);
    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    public static bool operator >(SemVer? a, SemVer? b) => a is not null && a.CompareTo(b) > 0;
    public static bool operator <(SemVer? a, SemVer? b) => b is not null && (a is null || a.CompareTo(b) < 0);
    public static bool operator >=(SemVer? a, SemVer? b) => !(a < b);
    public static bool operator <=(SemVer? a, SemVer? b) => !(a > b);
}

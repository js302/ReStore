using System.Globalization;

namespace ReStore.Tests;

/// <summary>
/// Switches the current thread's culture (and the default for threads started meanwhile) and restores it on dispose.
/// Tests that use it must join the "GlobalState" collection because the default culture is process-wide.
/// </summary>
internal sealed class CultureScope : IDisposable
{
    /// <summary>tr-TR (dotless i), de-DE (comma decimals), ar-SA (Hijri calendar, other minus sign), ja-JP, th-TH (Buddhist calendar).</summary>
    public static readonly string[] Names = ["tr-TR", "de-DE", "ar-SA", "ja-JP", "th-TH"];

    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public CultureScope(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static IEnumerable<object[]> All() => Names.Select(n => new object[] { n });

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }
}

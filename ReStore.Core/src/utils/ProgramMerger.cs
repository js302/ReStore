using System.Text;

namespace ReStore.Core.src.utils;

/// <summary>Combines programs found by winget and the registry into one list without duplicates.</summary>
public static class ProgramMerger
{
    /// <summary>Lower-cased letters/digits only, words separated by single spaces; trademark symbols and punctuation drop out.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
            }
            else if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                // Trademark/registered symbols are dropped without splitting a word; other separators split.
                if (ch is '™' or '®' or '©')
                {
                    continue;
                }

                pendingSpace = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>Merge key: normalised name and publisher joined by '|' (which normalisation never produces).</summary>
    public static string CreateKey(string? name, string? publisher)
        => Normalize(name) + "|" + Normalize(publisher);

    /// <summary>
    /// Merges <paramref name="additional"/> into <paramref name="primary"/> (primary wins, gaps are filled).
    /// Entries match on the normalised name+publisher key; a missing publisher (winget list has none) matches on name alone.
    /// </summary>
    public static List<InstalledProgram> Merge(IEnumerable<InstalledProgram> primary, IEnumerable<InstalledProgram> additional)
    {
        var result = new List<InstalledProgram>();
        var byKey = new Dictionary<string, InstalledProgram>();
        var byName = new Dictionary<string, List<InstalledProgram>>();

        foreach (var program in primary.Concat(additional))
        {
            var nameKey = Normalize(program.Name);
            if (nameKey.Length == 0)
            {
                continue;
            }

            var key = CreateKey(program.Name, program.Publisher);
            if (byKey.TryGetValue(key, out var existing))
            {
                Enrich(existing, program);
                continue;
            }

            var hasPublisher = Normalize(program.Publisher).Length > 0;
            byName.TryGetValue(nameKey, out var sameName);
            existing = sameName?.FirstOrDefault(c => !hasPublisher || Normalize(c.Publisher).Length == 0);
            if (existing != null)
            {
                Enrich(existing, program);
                byKey[CreateKey(existing.Name, existing.Publisher)] = existing;
                continue;
            }

            result.Add(program);
            byKey[key] = program;
            if (sameName == null)
            {
                byName[nameKey] = sameName = [];
            }

            sameName.Add(program);
        }

        return result;
    }

    private static void Enrich(InstalledProgram target, InstalledProgram source)
    {
        if (string.IsNullOrEmpty(target.Publisher)) target.Publisher = source.Publisher;
        if (string.IsNullOrEmpty(target.Version)) target.Version = source.Version;
        if (string.IsNullOrEmpty(target.InstallLocation)) target.InstallLocation = source.InstallLocation;
        if (string.IsNullOrEmpty(target.UninstallString)) target.UninstallString = source.UninstallString;
        if (string.IsNullOrEmpty(target.InstallDate)) target.InstallDate = source.InstallDate;
        if (string.IsNullOrEmpty(target.WingetId) && !string.IsNullOrEmpty(source.WingetId))
        {
            target.WingetId = source.WingetId;
            target.IsWingetAvailable = source.IsWingetAvailable;
        }
    }
}

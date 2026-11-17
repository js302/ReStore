using System.Globalization;

namespace ReStore.Services
{
    /// <summary>
    /// Parses numbers typed into Settings boxes. A value is first read in the user's own culture (so "1,5" works
    /// on a German PC) and then in the invariant culture (so "1.5" copied from documentation also works).
    /// Grouping separators are not accepted, which keeps the two readings unambiguous.
    /// </summary>
    public static class NumberInput
    {
        public static bool TryParseDouble(string? text, out double value, CultureInfo? culture = null)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (var provider in Providers(culture))
            {
                if (double.TryParse(text, NumberStyles.Float, provider, out var parsed) && double.IsFinite(parsed))
                {
                    value = parsed;
                    return true;
                }
            }

            return false;
        }

        public static bool TryParseInt(string? text, out int value, CultureInfo? culture = null)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (var provider in Providers(culture))
            {
                if (int.TryParse(text, NumberStyles.Integer, provider, out value))
                {
                    return true;
                }
            }

            value = 0;
            return false;
        }

        public static bool TryParseLong(string? text, out long value, CultureInfo? culture = null)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (var provider in Providers(culture))
            {
                if (long.TryParse(text, NumberStyles.Integer, provider, out value))
                {
                    return true;
                }
            }

            value = 0;
            return false;
        }

        /// <summary>One decimal place in the user's culture; <see cref="TryParseDouble"/> reads it back.</summary>
        public static string FormatHours(double hours, CultureInfo? culture = null) =>
            hours.ToString("F1", culture ?? CultureInfo.CurrentCulture);

        private static IEnumerable<CultureInfo> Providers(CultureInfo? culture)
        {
            var primary = culture ?? CultureInfo.CurrentCulture;
            yield return primary;
            if (!ReferenceEquals(primary, CultureInfo.InvariantCulture) && primary.Name.Length != 0)
            {
                yield return CultureInfo.InvariantCulture;
            }
        }
    }
}

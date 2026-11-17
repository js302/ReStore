namespace ReStore.Core.src.utils
{
    /// <summary>
    /// The one place user-facing timestamps are formatted: stored times are UTC, shown in the user's
    /// local time zone with a fixed format, with the exact UTC instant available as a tooltip.
    /// </summary>
    public static class DisplayTime
    {
        /// <summary>Local time as <c>yyyy-MM-dd HH:mm</c>. An unspecified-kind value is taken to be UTC.</summary>
        public static string Format(DateTime utc)
            => ToUtc(utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The same instant as ISO-8601 UTC (<c>yyyy-MM-ddTHH:mm:ssZ</c>), for tooltips.</summary>
        public static string Tooltip(DateTime utc)
            => ToUtc(utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}

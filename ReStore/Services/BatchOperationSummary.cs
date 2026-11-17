using System.IO;
using System.Text;

namespace ReStore.Services
{
    /// <summary>
    /// Collects the per-item results of a batch (delete, restore) so failures can be listed to the user
    /// instead of being reduced to a count. WPF-free so the wording and truncation can be unit-tested.
    /// </summary>
    public sealed class BatchOperationSummary
    {
        private const int MaxListedFailures = 10;

        private readonly List<(string Item, string Reason)> _failures = [];

        public int Succeeded { get; private set; }

        public IReadOnlyList<(string Item, string Reason)> Failures => _failures;

        public bool HasFailures => _failures.Count > 0;

        public void AddSuccess() => Succeeded++;

        public void AddFailure(string item, string reason) => _failures.Add((item, reason));

        /// <summary>"Deleted 3 backup(s)." or, with failures, the count plus up to ten "item: reason" lines.</summary>
        public string Format(string pastTense)
        {
            var text = new StringBuilder();
            text.Append($"{pastTense} {Succeeded} backup(s).");

            if (_failures.Count == 0)
            {
                return text.ToString();
            }

            text.Append($" {_failures.Count} failed:");
            foreach (var (item, reason) in _failures.Take(MaxListedFailures))
            {
                text.Append($"\n\n• {item}\n  {reason}");
            }

            if (_failures.Count > MaxListedFailures)
            {
                text.Append($"\n\n...and {_failures.Count - MaxListedFailures} more. See the log for details.");
            }

            return text.ToString();
        }

        /// <summary>
        /// A folder name unique within a batch restore: <c>&lt;directory&gt;\&lt;yyyyMMdd-HHmmss&gt;</c>, with
        /// characters Windows forbids in a name replaced and a numeric suffix added on a collision.
        /// </summary>
        public static string RestoreFolderName(string directory, DateTime timestampUtc, ISet<string> alreadyUsed)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safeDirectory = new string((directory ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (safeDirectory.Length == 0)
            {
                safeDirectory = "backup";
            }

            var utc = timestampUtc.Kind == DateTimeKind.Local ? timestampUtc.ToUniversalTime() : timestampUtc;
            var baseName = Path.Combine(safeDirectory, utc.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));

            var candidate = baseName;
            for (var suffix = 2; !alreadyUsed.Add(candidate); suffix++)
            {
                candidate = $"{baseName}-{suffix}";
            }

            return candidate;
        }
    }
}

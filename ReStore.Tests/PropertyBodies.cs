using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReStore.Core.src.backup;
using ReStore.Core.src.cli;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.telemetry;
using ReStore.Core.src.updates;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>The bodies of the property tests; each takes the seed FsCheck generated and derives its structured inputs from it.</summary>
internal static class PropertyBodies
{
    private sealed class PropertyFailure(string message) : Exception(message);

    /// <summary>Hands out at most <c>maxRead</c> bytes per read, so the chunker sees awkward fragmentation.</summary>
    private sealed class DribbleStream(Stream inner, int maxRead) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, maxRead));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            await inner.ReadAsync(buffer[..Math.Min(buffer.Length, maxRead)], cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void Require(bool condition, Func<string> message)
    {
        if (!condition)
        {
            throw new PropertyFailure(message());
        }
    }

    private static string Show(string? text) => text == null ? "<null>" : "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static T Pick<T>(this Random random, IReadOnlyList<T> items) => items[random.Next(items.Count)];

    private const string Letters = "abcdefghijklmnopqrstuvwxyz";
    private const string AlphaNumeric = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static string Word(Random random, int min, int max, string alphabet = AlphaNumeric)
    {
        var chars = new char[random.Next(min, max + 1)];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[random.Next(alphabet.Length)];
        }

        return new string(chars);
    }

    // ---- schedules -------------------------------------------------------------------------------------------------------

    private static readonly string[] ScheduleTokens =
    [
        "off", "every", "daily", "weekdays", "weekends", "weekly", "monthly", "cron", "last", "mon", "tue", "wed", "thu", "fri", "sat", "sun", "mon-fri", "sat-sun",
        "mon,wed,fri", "sun,sun", "00:00", "02:00", "23:59", "24:00", "7:5", "12:30,18:45", "09:15 ,10:00", "4h", "90m", "5m", "4m", "2d", "0h", "365d", "366d", "1", "15", "28", "29",
        "*", "*/5", "*/0", "1-5", "5-1", "0-59/15", "1,2,3", "*/15", "0", "12", "jan", "dec", "mon-sun", "31", "-1", "1e3", "٣", "ı", "İ"
    ];

    private static readonly string[] ScheduleSeparators = [" ", " ", " ", "  ", "\t", ",", " , "];

    private static string ScheduleText(Random random)
    {
        var tokenCount = random.Next(0, 8);
        var builder = new StringBuilder();
        for (var i = 0; i < tokenCount; i++)
        {
            builder.Append(random.Pick(ScheduleTokens)).Append(random.Pick(ScheduleSeparators));
        }

        var text = builder.ToString();
        // Character-level noise on top of the token soup.
        for (var i = random.Next(0, 3); i > 0 && text.Length > 0; i--)
        {
            var at = random.Next(text.Length);
            text = random.Next(3) switch
            {
                0 => text.Remove(at, 1),
                1 => text.Insert(at, ((char)random.Next(32, 0x250)).ToString()),
                _ => text[..at]
            };
        }

        return random.Next(8) == 0 ? ValidSchedule(random) : text;
    }

    private static readonly string[] ValidSchedules =
    [
        "daily 02:00", "daily 02:00,14:30", "weekdays 18:30", "weekends 10:00", "mon,wed,fri 07:00", "mon-fri 18:30", "weekly sun 03:00", "monthly 15 04:00",
        "monthly last 04:00", "every 4h", "every 30m", "every 2d", "off", "cron 0 3 * * *", "cron */15 * * * *", "cron 0 0 1 1 *", "cron 30 2 * * mon-fri", "cron 0 12 31 * *", "cron 59 23 29 2 *"
    ];

    private static string ValidSchedule(Random random) => random.Pick(ValidSchedules);

    private static TimeZoneInfo DstZone(Random random) => random.Next(4) switch
    {
        0 => TimeZoneInfo.Utc,
        1 => NewYork(),
        2 => Midnight(),
        _ => HalfHour()
    };

    /// <summary>US-style rules built by hand (02:00 gap on the second Sunday of March, repeat on the first Sunday of November).</summary>
    internal static TimeZoneInfo NewYork() => Zone("Test/Eastern", TimeSpan.FromHours(-5), TimeSpan.FromHours(1), 2, 3, 2, 11, 1);

    /// <summary>A zone whose clocks jump at midnight (00:00 to 01:00) like some real zones do.</summary>
    internal static TimeZoneInfo Midnight() => Zone("Test/Midnight", TimeSpan.FromHours(-3), TimeSpan.FromHours(1), 0, 10, 3, 2, 3);

    /// <summary>A 30-minute daylight-saving shift (Lord Howe style).</summary>
    internal static TimeZoneInfo HalfHour() => Zone("Test/HalfHour", TimeSpan.FromHours(10.5), TimeSpan.FromMinutes(30), 2, 10, 1, 4, 1);

    private static TimeZoneInfo Zone(string id, TimeSpan standard, TimeSpan shift, int hour, int startMonth, int startWeek, int endMonth, int endWeek)
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, hour, 0, 0), startMonth, startWeek, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, hour + (shift == TimeSpan.FromHours(1) ? 1 : 0), 0, 0), endMonth, endWeek, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, shift, start, end);
        return TimeZoneInfo.CreateCustomTimeZone(id, standard, id, "S", "D", [rule]);
    }

    private static DateTime RandomUtc(Random random) =>
        new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(random.NextInt64(0, 11L * 365 * 24 * 60));

    public static bool ScheduleParseAndOccurrences(int seed)
    {
        var random = new Random(seed);
        var text = ScheduleText(random);
        BackupSchedule? schedule;
        string? error;
        try
        {
            if (!BackupSchedule.TryParse(text, out schedule, out error))
            {
                Require(!string.IsNullOrWhiteSpace(error), () => $"TryParse({Show(text)}) failed without an error message");
                Require(schedule == null, () => $"TryParse({Show(text)}) failed but returned a schedule");
                return true;
            }
        }
        catch (Exception ex)
        {
            throw new PropertyFailure($"TryParse({Show(text)}) threw {ex.GetType().Name}: {ex.Message}");
        }

        Require(schedule != null && !string.IsNullOrWhiteSpace(schedule.Expression) && !string.IsNullOrWhiteSpace(schedule.Description), () => $"TryParse({Show(text)}) gave an empty expression/description");
        var zone = DstZone(random);
        var after = RandomUtc(random);
        try
        {
            var first = schedule!.NextOccurrence(after, zone);
            if (!schedule.IsClock)
            {
                Require(first == null, () => $"{Show(text)} is not a clock schedule but has a next occurrence");
                return true;
            }

            Require(first is { } f && f > after, () => $"{Show(text)} in {zone.Id}: next after {after:O} is {first:O}, not later");
            var cursor = after;
            foreach (var next in schedule.NextOccurrences(after, 12, zone))
            {
                Require(next > cursor, () => $"{Show(text)} in {zone.Id}: occurrence {next:O} is not after {cursor:O}");
                cursor = next;
            }

            var latest = schedule.LatestOccurrenceAtOrBefore(after, zone);
            Require(latest == null || latest <= after, () => $"{Show(text)}: latest occurrence {latest:O} is after {after:O}");
        }
        catch (PropertyFailure)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PropertyFailure($"{Show(text)} in {zone.Id} after {after:O} threw {ex.GetType().Name}: {ex.Message}");
        }

        return true;
    }

    public static bool ScheduleCanonicalIsStable(int seed)
    {
        var random = new Random(seed);
        var text = random.Next(2) == 0 ? ValidSchedule(random) : ScheduleText(random);
        if (!BackupSchedule.TryParse(text, out var schedule, out _))
        {
            return true;
        }

        Require(BackupSchedule.TryParse(schedule!.Expression, out var again, out var error), () => $"{Show(text)} parsed to {Show(schedule.Expression)}, which no longer parses: {error}");
        Require(again!.Expression == schedule.Expression, () => $"{Show(text)} -> {Show(schedule.Expression)} -> {Show(again.Expression)}");
        Require(again.Kind == schedule.Kind, () => $"{Show(text)}: kind changed on re-parse");
        return true;
    }

    public static bool ScheduleAroundDst(int seed)
    {
        var random = new Random(seed);
        var zone = DstZone(random);
        // Pick a day close to a transition of the zone, and a schedule time close to its gap or repeat.
        var year = random.Next(2021, 2030);
        var anchor = new DateTime(year, random.Pick(new[] { 3, 4, 10, 11 }), random.Next(1, 29), 0, 0, 0, DateTimeKind.Utc).AddHours(random.Next(-30, 30));
        var hour = random.Pick(new[] { 0, 1, 2, 3, 23 });
        var minute = random.Pick(new[] { 0, 15, 30, 45 });
        var text = random.Next(3) switch
        {
            0 => $"daily {hour:D2}:{minute:D2}",
            1 => $"cron {minute} {hour} * * *",
            _ => $"daily {hour:D2}:{minute:D2},{(hour + 1) % 24:D2}:{minute:D2}"
        };
        var schedule = BackupSchedule.Parse(text);

        var runs = schedule.NextOccurrences(anchor, 6, zone);
        Require(runs.Count == 6, () => $"{text} in {zone.Id}: only {runs.Count} runs after {anchor:O}");
        for (var i = 0; i < runs.Count; i++)
        {
            Require(i == 0 || runs[i] > runs[i - 1], () => $"{text} in {zone.Id}: runs are not strictly increasing: {string.Join(", ", runs.Select(r => r.ToString("O", CultureInfo.InvariantCulture)))}");
            var local = TimeZoneInfo.ConvertTimeFromUtc(runs[i], zone);
            Require(!zone.IsInvalidTime(local), () => $"{text} in {zone.Id}: run {runs[i]:O} falls in a nonexistent local time");
        }

        // A time that happens twice runs once: no two runs may share the same local date and minute.
        var keys = runs.Select(r => TimeZoneInfo.ConvertTimeFromUtc(r, zone)).Select(l => l.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).ToList();
        Require(keys.Count == keys.Distinct().Count(), () => $"{text} in {zone.Id}: a local time ran twice: {string.Join(", ", keys)}");
        return true;
    }

    // ---- path mapping ----------------------------------------------------------------------------------------------------

    public static bool PathMapping(int seed)
    {
        var random = new Random(seed);
        // Old prefixes use the letters a-m and new ones n-z, so applying the mapping twice cannot match its own output.
        string Segments(string alphabet, int min, int max)
        {
            var separator = random.Next(2) == 0 ? "/" : "\\";
            var parts = Enumerable.Range(0, random.Next(min, max + 1)).Select(_ => Word(random, 1, 5, alphabet));
            var prefix = random.Next(3) == 0 ? "C:" + separator : random.Next(2) == 0 ? separator : "";
            return prefix + string.Join(separator, parts);
        }

        var old = Segments("abcdefghijklm", 1, 4);
        var replacement = Segments("nopqrstuvwxyz", 1, 3);
        PathMapping mapping;
        try
        {
            mapping = ReStore.Core.src.core.PathMapping.Parse([old + "=" + replacement]);
        }
        catch (ArgumentException)
        {
            return true;
        }

        var normalizedOld = old.Replace('\\', '/').TrimEnd('/');
        var rest = Segments("abcdefghijklm", 1, 3).Replace('\\', '/').TrimStart('/');
        string[] inputs =
        [
            old, old + "/" + rest, old.ToUpperInvariant() + "\\" + rest.Replace('/', '\\'),
            old + Word(random, 1, 3, "abcdefghijklm"), // same prefix text but not a whole segment
            Segments("abcdefghijklm", 1, 4), Segments("nopqrstuvwxyz", 1, 4), "", "/", "\\"
        ];
        foreach (var input in inputs)
        {
            string output;
            try
            {
                output = mapping.Apply(input);
            }
            catch (Exception ex)
            {
                throw new PropertyFailure($"Apply({Show(input)}) with {Show(old)}={Show(replacement)} threw {ex.GetType().Name}: {ex.Message}");
            }

            var normalizedInput = input.Replace('\\', '/').TrimEnd('/');
            var matches = normalizedInput.Equals(normalizedOld, StringComparison.OrdinalIgnoreCase)
                || normalizedInput.StartsWith(normalizedOld + "/", StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                Require(output == input, () => $"Apply({Show(input)}) with {Show(old)}={Show(replacement)} changed a path that does not match whole segments: {Show(output)}");
            }
            else
            {
                Require(output.StartsWith(replacement.TrimEnd('\\', '/'), StringComparison.Ordinal), () => $"Apply({Show(input)}) with {Show(old)}={Show(replacement)} did not start with the replacement: {Show(output)}");
                Require(mapping.Apply(output) == output, () => $"Apply is not idempotent: {Show(input)} -> {Show(output)} -> {Show(mapping.Apply(output))}");
            }
        }

        foreach (var garbage in new[] { "", "=", "a=", "=b", "no equals sign", old, old + "=", "=" + replacement, "a=b=c", "\0=\0" })
        {
            try
            {
                ReStore.Core.src.core.PathMapping.Parse([garbage]);
            }
            catch (ArgumentException)
            {
            }
            catch (Exception ex)
            {
                throw new PropertyFailure($"Parse({Show(garbage)}) threw {ex.GetType().Name}, not ArgumentException");
            }
        }

        return true;
    }

    // ---- config key paths ------------------------------------------------------------------------------------------------

    private static readonly string[] KeySegments =
    [
        "retention", "keepDaily", "storageSources", "s3", "options", "bucketName", "watchDirectories", "0", "1", "2", "99", "path", "RETENTION", "Retention", "encryption",
        "enabled", "telemetry", "", " ", "a b", "__proto__", "constructor", "-1", "1e2", "٣", "ı", "*", "[0]", "..", "\u0000", "very.long"
    ];

    public static bool ConfigKeyPaths(int seed)
    {
        var random = new Random(seed);
        var key = random.Next(6) == 0
            ? Word(random, 0, 12, AlphaNumeric + ".. _-/\\")
            : string.Join(random.Next(10) == 0 ? ".." : ".", Enumerable.Range(0, random.Next(0, 6)).Select(_ => random.Pick(KeySegments)));

        string[] segments;
        try
        {
            segments = ConfigEditor.ParseKey(key);
        }
        catch (ConfigEditException)
        {
            return true;
        }
        catch (Exception ex)
        {
            throw new PropertyFailure($"ParseKey({Show(key)}) threw {ex.GetType().Name}, not ConfigEditException");
        }

        Require(segments.Length > 0 && segments.All(s => !string.IsNullOrWhiteSpace(s)) && string.Join('.', segments) == key, () => $"ParseKey({Show(key)}) lost information: {string.Join("|", segments)}");

        var root = ConfigSchema.LoadExample();
        var before = root.ToJsonString();
        var beforeTop = root.ToDictionary(p => p.Key, p => p.Value?.ToJsonString());
        try
        {
            ConfigEditor.TryGet(root, segments, out _);
            ConfigSchema.Resolve(segments);
            ConfigEditor.Set(root, segments, JsonValue.Create("zqMarker"));
        }
        catch (ConfigEditException)
        {
            Require(root.ToJsonString() == before, () => $"Set({Show(key)}) failed but still changed the document");
            return true;
        }
        catch (Exception ex)
        {
            throw new PropertyFailure($"Set({Show(key)}) threw {ex.GetType().Name}: {ex.Message}");
        }

        Require(ConfigEditor.TryGet(root, segments, out var value) && value?.GetValue<string>() == "zqMarker", () => $"Set({Show(key)}) did not store the value at that key");
        // Nothing outside the targeted top-level key changed.
        var targeted = root.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, segments[0], StringComparison.OrdinalIgnoreCase)) ?? segments[0];
        foreach (var (name, json) in beforeTop.Where(p => !string.Equals(p.Key, targeted, StringComparison.OrdinalIgnoreCase)))
        {
            Require(root[name]?.ToJsonString() == json, () => $"Set({Show(key)}) changed unrelated top-level key '{name}'");
        }

        Require(root.Count == beforeTop.Count + (beforeTop.Keys.Any(k => string.Equals(k, segments[0], StringComparison.OrdinalIgnoreCase)) ? 0 : 1), () => $"Set({Show(key)}) added or removed top-level keys");

        var copy = (JsonObject)JsonNode.Parse(root.ToJsonString())!;
        try
        {
            ConfigEditor.Remove(copy, segments);
        }
        catch (Exception ex)
        {
            throw new PropertyFailure($"Remove({Show(key)}) threw {ex.GetType().Name}: {ex.Message}");
        }

        return true;
    }

    // ---- manifests -------------------------------------------------------------------------------------------------------

    private static readonly Lazy<(byte[] V2, byte[] V3, string Json)> ManifestSamples = new(() =>
    {
        SnapshotManifest Build(int version) => new()
        {
            Version = version,
            SnapshotId = "snapshot_20260101000000_abc",
            Group = "docs_0123456789abcdef",
            CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Profile = new ChunkingProfile { AlgorithmVersion = version == 3 ? 3 : 2 },
            Directories = version == 3 ? [new SnapshotDirectoryManifestEntry { RelativePath = "empty", Attributes = 16, LastWriteUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) }] : null,
            Files =
            [
                new SnapshotFileManifestEntry
                {
                    RelativePath = "a/b.txt", SizeBytes = 11, ContentHash = new string('a', 64), LastModifiedUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                    Attributes = version == 3 ? 32 : null,
                    Chunks = [new SnapshotChunkManifestEntry { ChunkId = new string('b', 64), ContentHash = new string('b', 64), PlainSizeBytes = 11, StoredSizeBytes = 11, Codec = version == 3 ? "raw" : null }]
                },
                new SnapshotFileManifestEntry { RelativePath = "empty.txt", ContentHash = new string('c', 64), Chunks = [] }
            ]
        };

        var v2 = Build(2);
        v2.RootHash = SnapshotManifestHasher.ComputeRootHash(v2);
        var v3 = Build(3);
        v3.RootHash = SnapshotManifestHasher.ComputeRootHash(v3);
        return (SnapshotManifestSerializer.Serialize(v2), SnapshotManifestSerializer.Serialize(v3), Encoding.UTF8.GetString(SnapshotManifestSerializer.Serialize(v2)));
    });

    private static bool IsCleanManifestError(Exception ex) =>
        ex is JsonException or InvalidOperationException or InvalidDataException or NotSupportedException or ArgumentException or FormatException
            or EndOfStreamException or System.Security.Cryptography.CryptographicException or IOException or OverflowException { Source: null };

    private static void ReadManifestAndUseIt(byte[] bytes, string description)
    {
        SnapshotManifest manifest;
        try
        {
            manifest = SnapshotManifestSerializer.Deserialize(bytes);
        }
        catch (Exception ex) when (IsCleanManifestError(ex) || ex is ManifestKeyRequiredException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new PropertyFailure($"Deserialize({description}) threw {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            SnapshotManifestHasher.IsValid(manifest);
            manifest.GetChunkingAlgorithmVersion();
            manifest.GetHashScheme();
            _ = manifest.Files.Sum(f => f.Chunks.Count);
        }
        catch (Exception ex) when (IsCleanManifestError(ex))
        {
        }
        catch (Exception ex)
        {
            throw new PropertyFailure($"Using a manifest read from {description} threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static bool ManifestByteMutations(int seed)
    {
        var random = new Random(seed);
        var (v2, v3, _) = ManifestSamples.Value;
        var bytes = (byte[])(random.Next(2) == 0 ? v2 : v3).Clone();
        var steps = random.Next(1, 6);
        var log = new StringBuilder();
        for (var i = 0; i < steps && bytes.Length > 0; i++)
        {
            var at = random.Next(bytes.Length);
            switch (random.Next(5))
            {
                case 0:
                    bytes[at] ^= (byte)(1 << random.Next(8));
                    log.Append($"flip@{at} ");
                    break;
                case 1:
                    bytes[at] = (byte)random.Next(256);
                    log.Append($"set@{at} ");
                    break;
                case 2:
                    bytes = bytes[..at];
                    log.Append($"truncate@{at} ");
                    break;
                case 3:
                    var insert = new byte[random.Next(1, 8)];
                    random.NextBytes(insert);
                    bytes = [.. bytes[..at], .. insert, .. bytes[at..]];
                    log.Append($"insert@{at} ");
                    break;
                default:
                    bytes = [.. bytes[..at], .. bytes[Math.Min(bytes.Length, at + random.Next(1, 6))..]];
                    log.Append($"delete@{at} ");
                    break;
            }
        }

        ReadManifestAndUseIt(bytes, $"v{(bytes.Length > 1 && bytes[0] == 0x1f ? 3 : 2)} bytes mutated by {log}");
        return true;
    }

    private static JsonNode? RandomJsonValue(Random random) => random.Next(14) switch
    {
        0 => null,
        1 => JsonValue.Create(0),
        2 => JsonValue.Create(-1),
        3 => JsonValue.Create(long.MaxValue),
        4 => JsonValue.Create(1e300),
        5 => JsonValue.Create(""),
        6 => JsonValue.Create("../../x"),
        7 => JsonValue.Create(new string('z', 10_000)),
        8 => new JsonArray(),
        9 => new JsonArray(1, "a", null),
        10 => new JsonObject(),
        11 => new JsonObject { ["x"] = new JsonArray() },
        12 => JsonValue.Create(true),
        _ => JsonValue.Create("not a number")
    };

    private static void Collect(JsonNode node, List<(JsonNode Parent, string? Name, int Index)> slots)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    slots.Add((obj, name, -1));
                    if (child != null)
                    {
                        Collect(child, slots);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    slots.Add((array, null, i));
                    if (array[i] != null)
                    {
                        Collect(array[i]!, slots);
                    }
                }

                break;
        }
    }

    public static bool ManifestJsonMutations(int seed)
    {
        var random = new Random(seed);
        var root = JsonNode.Parse(ManifestSamples.Value.Json)!;
        var slots = new List<(JsonNode Parent, string? Name, int Index)>();
        Collect(root, slots);
        var log = new StringBuilder();
        for (var i = random.Next(1, 4); i > 0; i--)
        {
            var (parent, name, index) = slots[random.Next(slots.Count)];
            var replacement = RandomJsonValue(random);
            log.Append($"{(name ?? index.ToString(CultureInfo.InvariantCulture))}={(replacement?.ToJsonString() is { Length: > 40 } text ? text[..40] + "..." : replacement?.ToJsonString() ?? "null")}; ");
            if (parent is JsonObject obj && name != null)
            {
                if (random.Next(5) == 0)
                {
                    obj.Remove(name);
                }
                else
                {
                    obj[name] = replacement;
                }
            }
            else if (parent is JsonArray array && index >= 0 && index < array.Count)
            {
                array[index] = replacement;
            }
        }

        ReadManifestAndUseIt(Encoding.UTF8.GetBytes(root.ToJsonString()), "v2 JSON with " + log);
        return true;
    }

    // ---- chunker ---------------------------------------------------------------------------------------------------------

    public static bool Chunker(int seed)
    {
        var random = new Random(seed);
        var directory = Path.Combine(Path.GetTempPath(), "ReStoreChunkProp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var algorithm = random.Next(2) == 0 ? 3 : 2;
            var config = new ChunkDiffingConfig { MinChunkSizeKB = 1, TargetChunkSizeKB = 2, MaxChunkSizeKB = 8, RollingHashWindowSize = random.Pick(new[] { 16, 32, 64 }) };
            var data = RandomContent(random, random.Next(0, 90_000));
            var tail = RandomContent(random, random.Next(1, 40_000));
            var fragment = random.Next(1, 5000);

            var service = new ChunkingService(new TestLogger(), config, new EncryptionService(new TestLogger()), false, null,
                path => new DribbleStream(File.OpenRead(path), fragment), algorithmVersion: algorithm);

            var first = Build(service, directory, "a.bin", data);
            Require(first.ChunkPayloads.SelectMany(p => p.StoredPayload).SequenceEqual(data), () => $"seed {seed}: concatenated chunks differ from the input (alg {algorithm}, {data.Length} bytes, read size {fragment})");
            var sizes = first.FileEntry.Chunks.Select(c => c.PlainSizeBytes).ToList();
            Require(sizes.Sum() == data.Length, () => $"seed {seed}: chunk sizes sum to {sizes.Sum()}, input is {data.Length}");
            for (var i = 0; i < sizes.Count; i++)
            {
                Require(sizes[i] <= config.MaxChunkSizeKB * 1024 && sizes[i] > 0, () => $"seed {seed}: chunk {i} has size {sizes[i]}");
                Require(i == sizes.Count - 1 || sizes[i] >= config.MinChunkSizeKB * 1024, () => $"seed {seed}: non-final chunk {i} is smaller than the minimum ({sizes[i]})");
            }

            // Same bytes, different read fragmentation: identical boundaries.
            var other = Build(new ChunkingService(new TestLogger(), config, new EncryptionService(new TestLogger()), false, null, null, algorithmVersion: algorithm), directory, "a2.bin", data);
            Require(other.FileEntry.Chunks.Select(c => c.ChunkId).SequenceEqual(first.FileEntry.Chunks.Select(c => c.ChunkId)), () => $"seed {seed}: chunk boundaries depend on the read size");

            // Appending data keeps every earlier chunk except possibly the last one.
            var appended = Build(service, directory, "b.bin", [.. data, .. tail]);
            var firstIds = first.FileEntry.Chunks.Select(c => c.ChunkId).ToList();
            var appendedIds = appended.FileEntry.Chunks.Select(c => c.ChunkId).ToList();
            var stable = Math.Max(0, firstIds.Count - 1);
            Require(appendedIds.Take(stable).SequenceEqual(firstIds.Take(stable)), () => $"seed {seed}: appending {tail.Length} bytes changed earlier chunks (alg {algorithm}, {data.Length} bytes)");
            return true;
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static ChunkedFileBuildResult Build(ChunkingService service, string directory, string name, byte[] content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        return service.BuildFileManifestEntryAsync(path, directory).GetAwaiter().GetResult();
    }

    private static byte[] RandomContent(Random random, int length)
    {
        var data = new byte[length];
        switch (random.Next(4))
        {
            case 0:
                random.NextBytes(data);
                break;
            case 1:
                // Text-like and repetitive: long runs and repeated blocks.
                var block = new byte[random.Next(1, 600)];
                random.NextBytes(block);
                for (var i = 0; i < length; i++)
                {
                    data[i] = block[i % block.Length];
                }

                break;
            case 2:
                break; // zeros
            default:
                random.NextBytes(data);
                for (var i = 0; i < length; i += random.Next(500, 4000))
                {
                    Array.Clear(data, i, Math.Min(length - i, random.Next(0, 3000)));
                }

                break;
        }

        return data;
    }

    // ---- scrubber and telemetry ------------------------------------------------------------------------------------------

    private static string Marker(Random random) => "zq" + Word(random, 7, 11, "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789");

    private static string Base64Blob(Random random, int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var blob = new StringBuilder("zqA7");
        while (blob.Length < Math.Max(length, 8))
        {
            blob.Append(alphabet[random.Next(alphabet.Length)]);
        }

        blob[2] = char.ToUpperInvariant(blob[2]);
        blob[3] = (char)('2' + random.Next(8));
        return blob.ToString();
    }

    /// <summary>A sensitive token shape the scrubber promises to remove, and the part of it that must never survive.</summary>
    private static (string Text, string[] Forbidden) SensitiveToken(Random random)
    {
        var m1 = Marker(random);
        var m2 = Marker(random);
        var m3 = Marker(random);
        switch (random.Next(16))
        {
            case 0: return ($@"C:\Users\{m1}\Documents\{m2} {m3}\file.txt", [m1, m2, m3]);
            case 1: return ($@"\\{m1}\{m2}\{m3}.xlsx", [m1, m2, m3]);
            case 2: return ($"/home/{m1}/{m2}/{m3}.dat", [m1, m2, m3]);
            case 3: return ($"/mnt/c/Users/{m1}/Tax {m2}/return.pdf", [m1, m2]);
            case 4: return ($"https://{m1}.example.org/{m2}/x?token={m3}&sig={m3}", [m1, m2, m3]);
            case 5: return ($"{m1}.{m2}+tag@{m3}.example.com", [m1, m2, m3]);
            case 6:
                // A JWT uses the URL-safe base64 alphabet; its header is not secret, the payload and signature are.
                string UrlSafe(int length) => Base64Blob(random, length).Replace('+', '-').Replace('/', '_');
                var payload = UrlSafe(random.Next(24, 60));
                var signature = UrlSafe(random.Next(24, 60));
                return ($"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.{payload}.{signature}", [payload[..14], signature[..14]]);
            case 7:
                var blob = Base64Blob(random, random.Next(40, 90));
                return (blob, [blob[..24]]);
            case 8:
                var hex = new string(Enumerable.Range(0, random.Next(40, 64)).Select(_ => "0123456789abcdef"[random.Next(16)]).ToArray());
                return (hex, [hex[..20]]);
            case 9:
                var accountKey = Base64Blob(random, 44);
                return ($"AccountKey={accountKey};EndpointSuffix=core.windows.net", [accountKey[..20]]);
            case 10: return ($"secretAccessKey={m1}", [m1]);
            case 11: return ($"password: {m1}", [m1]);
            case 12: return ($"the bucket '{m1}' was not found", [m1]);
            case 13: return ($"Group \"{m1} {m2}\" failed", [m1, m2]);
            case 14: return ($"{m1}-{m2}.docx", [m1, m2]);
            default: return ($"10.{random.Next(256)}.{random.Next(256)}.{random.Next(1, 255)}:{random.Next(1, 65535)} and {Guid.NewGuid()}", ["10."]);
        }
    }

    private static readonly string[] Filler =
    [
        "failed to open", "could not read", "The operation was canceled.", "Access is denied", "Unexpected character", "while uploading chunk", "at snapshot", "retrying",
        "HTTP 403", "(Parameter 'value')", "an app and a channel", "Hannah met Ann by the apple stand", "timeout", "and then"
    ];

    private static IEnumerable<string> CollectMessages(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key.Equals("message", StringComparison.OrdinalIgnoreCase) && value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text))
                    {
                        yield return text;
                    }
                    else
                    {
                        foreach (var inner in CollectMessages(value))
                        {
                            yield return inner;
                        }
                    }
                }

                break;
            case System.Text.Json.Nodes.JsonArray array:
                foreach (var item in array)
                {
                    foreach (var inner in CollectMessages(item))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    public static bool Scrubber(int seed)
    {
        var random = new Random(seed);
        var parts = new List<string>();
        var forbidden = new List<string>();
        var terms = new List<string>();
        for (var i = random.Next(1, 5); i > 0; i--)
        {
            parts.Add(random.Pick(Filler));
            var (text, secrets) = SensitiveToken(random);
            parts.Add(text);
            forbidden.AddRange(secrets.Where(s => s.Length > 0));
        }

        if (random.Next(3) == 0)
        {
            // Configured names (bucket, owner, host...) appearing unquoted anywhere in the message, including inside ordinary words.
            var term = random.Pick(new[] { "ann", "app", "acme-prod", "payroll", "Tuna", Marker(random) });
            terms.Add(term);
            parts.Insert(random.Next(parts.Count + 1), "the " + term + " archive and " + term + "s");
            forbidden.Add(term);
        }

        var message = string.Join(random.Next(3) == 0 ? "\n" : " ", parts);
        var scrubbed = CrashScrubber.ScrubMessage(message, terms);
        foreach (var secret in forbidden.Where(s => s.Length > 0))
        {
            Require(scrubbed.IndexOf(secret, StringComparison.OrdinalIgnoreCase) < 0, () => $"'{secret}' survived scrubbing.\n  input:   {Show(message)}\n  output:  {Show(scrubbed)}");
        }

        Require(scrubbed.Length <= CrashScrubber.MaxMessageLength && !scrubbed.Contains('\n', StringComparison.Ordinal), () => $"scrubbed message is not one short line: {Show(scrubbed)}");

        // The same text inside a built crash report (message of an exception and of its inner exception).
        var exception = new InvalidOperationException(message, new IOException(message));
        var json = CrashReportBuilder.Build(exception, new CrashReportContext { SensitiveTerms = terms }).ToJson();
        // Only the scrubbed free text is checked: the report's own fields (osVersion "10.0.26100", appVersion, ids) may legitimately
        // contain a generated fragment such as "10.", and configured terms are only scrubbed from messages (framework type names in
        // stack frames may legitimately contain "ann", e.g. Channels).
        var messages = string.Join("\n", CollectMessages(System.Text.Json.Nodes.JsonNode.Parse(json)));
        Require(messages.Length > 0, () => "the crash report JSON has no exception message");
        foreach (var secret in forbidden.Where(s => s.Length > 0 && !terms.Contains(s)))
        {
            Require(messages.IndexOf(secret, StringComparison.OrdinalIgnoreCase) < 0, () => $"'{secret}' survived into a crash report message for {Show(message)}");
        }

        return true;
    }

    public static bool TelemetryAllowList(int seed)
    {
        var random = new Random(seed);
        var hostile = new List<string>();
        string HostileValue()
        {
            var (text, secrets) = SensitiveToken(random);
            hostile.AddRange(secrets);
            return text;
        }

        var events = TelemetrySchema.EventNames.Concat(["", "unknown.event", Word(random, 1, 10)]).ToList();
        var eventName = random.Pick(events);
        var properties = new Dictionary<string, object?>();
        var known = TelemetrySchema.PropertyNames(eventName).ToList();
        for (var i = random.Next(0, 8); i > 0; i--)
        {
            var name = known.Count > 0 && random.Next(3) > 0 ? random.Pick(known) : random.Pick(new[] { "path", "user", "bucket", "message", "exception", Word(random, 1, 8) });
            object? value = random.Next(6) switch
            {
                0 => random.Next(-5, 400),
                1 => random.Next(2) == 0,
                2 => null,
                3 => new string('x', random.Next(100, 400)),
                _ => HostileValue()
            };
            if (name == "exceptionType" && value is string text && (text.StartsWith("System.", StringComparison.Ordinal) || text.StartsWith("ReStore.", StringComparison.Ordinal)))
            {
                value = "other";
            }

            properties[name] = value;
        }

        var allowed = TelemetrySchema.TryBuild(eventName, properties, out var clean);
        Require(allowed == TelemetrySchema.EventNames.Contains(eventName), () => $"event '{eventName}' allowed={allowed}");
        Require(clean.Keys.All(known.Contains), () => $"unknown property names survived for '{eventName}': {string.Join(",", clean.Keys.Except(known))}");
        var json = JsonSerializer.Serialize(clean);
        foreach (var secret in hostile.Where(s => s.Length > 0))
        {
            Require(!json.Contains(secret, StringComparison.OrdinalIgnoreCase), () => $"'{secret}' survived the telemetry allow-list for {eventName}: {json}");
        }

        foreach (var value in clean.Values.OfType<string>())
        {
            Require(value.Length <= 120 && value.IndexOfAny(['\\', '/', ':', '\r', '\n', '"']) < 0, () => $"unsafe text value survived: {Show(value)}");
        }

        return true;
    }

    // ---- durations, versions, globs --------------------------------------------------------------------------------------

    public static bool Durations(int seed)
    {
        var random = new Random(seed);
        var seconds = random.Next(5) switch
        {
            0 => random.Next(1, 60),
            1 => random.Next(60, 3600),
            2 => random.Next(3600, 86_400),
            3 => random.Next(86_400, 30 * 86_400),
            _ => random.Next(1, 3650 * 86_400)
        };
        var duration = TimeSpan.FromSeconds(seconds);
        // Format separates its parts with spaces ("1d 6h") for messages; the parser takes them joined ("1d6h").
        var formatted = CliDurations.Format(duration).Replace(" ", "", StringComparison.Ordinal);
        Require(CliDurations.TryParse(formatted, out var parsed), () => $"Format({duration}) = {Show(formatted)} does not parse");
        // Format drops seconds once a duration reaches one minute, so compare at the resolution it prints.
        var expected = duration.TotalSeconds < 60 ? duration : TimeSpan.FromMinutes(Math.Floor(duration.TotalMinutes));
        Require(parsed == expected, () => $"parse(format({duration})) = {parsed}, expected {expected} via {Show(formatted)}");

        var text = ScheduleText(random);
        try
        {
            CliDurations.TryParse(text, out _);
            CliDurations.TryParse(random.Next(2) == 0 ? "9999999999999999999d" : new string('9', random.Next(1, 40)) + "s", out var huge);
            Require(huge == TimeSpan.Zero || huge <= TimeSpan.FromDays(3650), () => "an oversized duration was accepted");
        }
        catch (Exception ex)
        {
            throw new PropertyFailure($"TryParse({Show(text)}) threw {ex.GetType().Name}");
        }

        return true;
    }

    private static string RandomVersion(Random random)
    {
        var text = $"{random.Next(0, 4)}.{random.Next(0, 12)}.{random.Next(0, 5)}";
        if (random.Next(3) == 0)
        {
            text += "-" + random.Pick(new[] { "rc.1", "rc.2", "beta", "alpha.10", "alpha.9", "0", "x.y" });
        }

        if (random.Next(5) == 0)
        {
            text += "+build." + random.Next(100);
        }

        return random.Next(8) == 0 ? "v" + text : text;
    }

    public static bool SemVerOrder(int seed)
    {
        var random = new Random(seed);
        var texts = Enumerable.Range(0, 3).Select(_ => RandomVersion(random)).ToList();
        var versions = texts.Select(t => SemVer.ParseOrNull(t)).ToList();
        Require(versions.All(v => v != null), () => $"valid version text did not parse: {string.Join(", ", texts)}");
        foreach (var version in versions)
        {
            Require(SemVer.ParseOrNull(version!.ToString()) is { } again && again.CompareTo(version) == 0, () => $"parse(format({version})) differs");
        }

        var (a, b, c) = (versions[0]!, versions[1]!, versions[2]!);
        Require(Math.Sign(a.CompareTo(b)) == -Math.Sign(b.CompareTo(a)), () => $"CompareTo is not antisymmetric for {a} and {b}");
        Require(!(a.CompareTo(b) <= 0 && b.CompareTo(c) <= 0) || a.CompareTo(c) <= 0, () => $"CompareTo is not transitive for {a}, {b}, {c}");
        Require(a.CompareTo(a) == 0 && a.Equals(SemVer.ParseOrNull(a.ToString())), () => $"{a} is not equal to itself");
        var junk = ScheduleText(random);
        try
        {
            SemVer.ParseOrNull(junk);
        }
        catch (Exception ex)
        {
            throw new PropertyFailure($"ParseOrNull({Show(junk)}) threw {ex.GetType().Name}");
        }

        return true;
    }

    public static bool ExcludeGlobs(int seed)
    {
        var random = new Random(seed);
        var pattern = random.Next(4) == 0
            ? Word(random, 0, 14, AlphaNumeric + "*?/\\.[]{}()^$|+!%~ ")
            : random.Pick(new[] { "*.tmp", "node_modules/", "**/cache/**", "*.LOG", "build", "a?c.txt", "docs/*.md", "/abs/path", "C:\\Temp\\*", "**", "*", "?" });
        var path = "C:/work/" + string.Join('/', Enumerable.Range(0, random.Next(1, 5)).Select(_ => Word(random, 1, 6, "abcdeCDE.tmplogMD"))) + random.Pick(new[] { "", ".tmp", ".LOG", ".md" });
        var name = path[(path.LastIndexOf('/') + 1)..];
        try
        {
            var parsed = ExcludePattern.Parse(pattern);
            var legacy = ExcludePattern.ParseLegacy(pattern);
            foreach (var isDirectory in new bool?[] { true, false, null })
            {
                parsed?.IsMatch(path, name, isDirectory);
                legacy?.IsMatch(path, name, isDirectory);
            }

            // A plain "*.ext" pattern against a file name agrees with a naive suffix check.
            if (random.Next(2) == 0)
            {
                var extension = random.Pick(new[] { ".tmp", ".log", ".md" });
                var simple = ExcludePattern.Parse("*" + extension)!;
                var expected = name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
                Require(simple.IsMatch(path, name, false) == expected, () => $"'*{extension}' vs '{name}': got {!expected}, expected {expected}");
            }
        }
        catch (PropertyFailure)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PropertyFailure($"Parse/IsMatch({Show(pattern)}, {Show(path)}) threw {ex.GetType().Name}: {ex.Message}");
        }

        return true;
    }
}

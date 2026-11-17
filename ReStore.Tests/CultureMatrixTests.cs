using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Machine formats must not depend on the user's culture (tr-TR dotless i, de-DE decimal comma, ar-SA Hijri calendar and minus sign,
/// ja-JP, th-TH Buddhist calendar). Every test runs once per culture and compares with the invariant-culture result.
/// </summary>
[Collection("GlobalState")]
public class CultureMatrixTests : CliConfigTestBase
{
    public static IEnumerable<object[]> Cultures() => CultureScope.All();

    private async Task<ConfigManager> LoadAsync(JsonObject root)
    {
        File.WriteAllText(AppPaths.ConfigFile, root.ToJsonString());
        var manager = new ConfigManager(new TestLogger());
        await manager.LoadAsync();
        return manager;
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ConfigLoad_TimeSpans_AreCultureIndependent(string culture)
    {
        WriteConfig(root =>
        {
            root["backupInterval"] = "01:30:00";
            root["systemBackup"] = new JsonObject { ["backupInterval"] = "2.00:00:00" };
            root["verification"] = new JsonObject { ["enabled"] = true, ["verifyInterval"] = "7.00:00:00" };
            root["watcher"] = new JsonObject { ["minSnapshotInterval"] = "00:00:01.5", ["maxDebounceWait"] = "00:10:00.25" };
        });
        var expected = await LoadAsync(ReadConfig());

        using var _ = new CultureScope(culture);
        var actual = await LoadAsync(ReadConfig());

        actual.BackupInterval.Should().Be(TimeSpan.FromMinutes(90)).And.Be(expected.BackupInterval);
        actual.SystemBackup.BackupInterval.Should().Be(TimeSpan.FromDays(2));
        actual.Verification.VerifyInterval.Should().Be(TimeSpan.FromDays(7));
        actual.Watcher.MinSnapshotInterval.Should().Be(TimeSpan.FromSeconds(1.5));
        actual.Watcher.MaxDebounceWait.Should().Be(TimeSpan.FromSeconds(600.25));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task ConfigRoundTrip_IsByteIdenticalAcrossCultures(string culture)
    {
        WriteConfig(root =>
        {
            root["backupInterval"] = "01:30:00";
            root["watcher"] = new JsonObject { ["minSnapshotInterval"] = "00:00:01.5" };
        });
        var invariant = await SaveAsync();

        using var _ = new CultureScope(culture);
        var localised = await SaveAsync();

        localised.Should().Be(invariant);
    }

    private async Task<string> SaveAsync()
    {
        var manager = new ConfigManager(new TestLogger());
        await manager.LoadAsync();
        var target = Path.Combine(Root, "saved-" + Guid.NewGuid().ToString("N") + ".json");
        await manager.SaveAsync(target);
        return File.ReadAllText(target);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task SchedulePreview_AndConfigGet_AreCultureIndependent(string culture)
    {
        WriteConfig();
        string[][] commands =
        [
            ["schedule", "preview", "daily 02:00,14:30", "--count", "3", "--json"],
            ["schedule", "preview", "cron */15 1-3 * * mon-fri", "--count", "4", "--json"],
            ["schedule", "preview", "monthly last 04:00", "--count", "2", "--json"],
            ["config", "get", "retention.keepDaily", "--json"],
            ["config", "get", "backupInterval", "--json"]
        ];
        var expected = new List<(int, string)>();
        foreach (var command in commands)
        {
            var (code, stdout, _) = await RunAsync(command);
            expected.Add((code, stdout));
        }

        using var _ = new CultureScope(culture);
        for (var i = 0; i < commands.Length; i++)
        {
            var (code, stdout, _) = await RunAsync(commands[i]);
            (code, stdout).Should().Be(expected[i], "command {0} under {1}", string.Join(' ', commands[i]), culture);
        }
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void ScheduleParser_AndNextOccurrence_AreCultureIndependent(string culture)
    {
        string[] expressions = ["daily 02:00", "weekdays 18:30", "mon,wed 07:00", "weekly sun 03:00", "monthly last 04:00", "every 4h", "cron 5 4 * * 1", "DAILY 02:00", "Weekdays 09:15"];
        var after = new DateTime(2026, 3, 7, 12, 0, 0, DateTimeKind.Utc);
        var expected = expressions.Select(e => BackupSchedule.Parse(e)).Select(s => (s.Expression, s.NextOccurrence(after, TimeZoneInfo.Utc))).ToList();

        using var _ = new CultureScope(culture);
        var actual = expressions.Select(e => BackupSchedule.Parse(e)).Select(s => (s.Expression, s.NextOccurrence(after, TimeZoneInfo.Utc))).ToList();

        actual.Should().Equal(expected);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void ExcludePatterns_MatchWithDottedAndDotlessI(string culture)
    {
        using var _ = new CultureScope(culture);

        // Under tr-TR "I".ToLower() is a dotless i; matching must still treat I and i as the same letter.
        ExcludePattern.Parse("info.txt")!.IsMatch("C:/data/INFO.TXT", "INFO.TXT", false).Should().BeTrue();
        ExcludePattern.Parse("*.TXT")!.IsMatch("C:/data/info.txt", "info.txt", false).Should().BeTrue();
        ExcludePattern.Parse("ilk.md")!.IsMatch("C:/data/Ilk.md", "Ilk.md", false).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void SnapshotIds_UseTheGregorianCalendar(string culture)
    {
        using var _ = new CultureScope(culture);

        var id = SnapshotStoragePaths.BuildSnapshotId();

        id.Should().MatchRegex($@"^snapshot_{DateTime.UtcNow.Year}\d{{10}}_[0-9a-f]{{32}}$");
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Retention_BucketsAreGregorian_InEveryCulture(string culture)
    {
        // Monthly/yearly buckets must follow Gregorian months and years: with a Hijri calendar the month boundaries drift by days.
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var snaps = Enumerable.Range(0, 120)
            .Select(i => new BackupInfo { Path = $"s{i:D3}", Timestamp = now.AddDays(-i * 3), StorageType = "local" }).ToList();
        var policy = new RetentionConfig { Enabled = true, KeepLastPerDirectory = 1, MaxAgeDays = 0, KeepDaily = 2, KeepMonthly = 6, KeepYearly = 1 };
        var expected = RetentionPlanner.Plan(snaps, policy, now, "local", TimeZoneInfo.Utc).ToDelete.Select(b => b.Path).Order().ToList();

        using var _ = new CultureScope(culture);
        var actual = RetentionPlanner.Plan(snaps, policy, now, "local", TimeZoneInfo.Utc).ToDelete.Select(b => b.Path).Order().ToList();

        actual.Should().Equal(expected);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void LogLines_UseTheGregorianCalendar(string culture)
    {
        using var _ = new CultureScope(culture);
        var originalOut = Console.Out;
        var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            using var logger = new Logger();
            logger.Log("culture probe", LogLevel.Info);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        captured.ToString().Should().StartWith($"[{DateTime.UtcNow.Year}-");
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void SftpPort_IsParsedWithTheInvariantCulture(string culture)
    {
        using var _ = new CultureScope(culture);

        ReStore.Core.src.storage.sftp.SftpStorage.ParsePort(new Dictionary<string, string> { ["port"] = "2222" }).Should().Be(2222);
        ReStore.Core.src.storage.sftp.SftpStorage.ParsePort(new Dictionary<string, string> { ["port"] = "+2200" }).Should().Be(2200);
        ReStore.Core.src.storage.sftp.SftpStorage.ParsePort([]).Should().Be(22);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void StateTimestampInBackupFileName_IsReadAsGregorian(string culture)
    {
        using var _ = new CultureScope(culture);
        var state = new SystemState(new TestLogger());
        var parse = typeof(SystemState).GetMethod("GetTimestampFromPath", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var timestamp = (DateTime)parse.Invoke(state, ["backups/backup_docs_0123456789abcdef0123456789abcdef_20260115123045.zip"])!;

        timestamp.Should().Be(new DateTime(2026, 1, 15, 12, 30, 45));
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void SchemaMigration_ReadsNumbersStoredAsText_WithAnyMinusSign(string culture)
    {
        using var _ = new CultureScope(culture);
        var tryGetInt = typeof(ConfigSchemaManager).GetMethod("TryGetInt", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        object?[] args = [new JsonObject { ["value"] = "-5" }, "value", 0];
        var found = (bool)tryGetInt.Invoke(null, args)!;

        found.Should().BeTrue();
        args[2].Should().Be(-5);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void ByteFormatter_UsesADecimalPoint_InEveryCulture(string culture)
    {
        using var _ = new CultureScope(culture);

        ByteFormatter.Format(1536).Should().Be("1.5 KB");
        ByteFormatter.Format(-1536).Should().Be("-1.5 KB");
        ByteFormatter.Format(1_234_567_890).Should().Be("1.15 GB");
    }

    /// <summary>A Hijri (14xx) or Buddhist (25xx) year in a date shown to the user or written to a log is a culture leak.</summary>
    private static readonly System.Text.RegularExpressions.Regex ForeignYear = new(@"(?<!\d)(1[34]\d\d|25\d\d)[-/]\d\d[-/]\d\d|(?<!\d)(1[34]\d\d|25\d\d)(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])(?!\d)", System.Text.RegularExpressions.RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(Cultures))]
    public async Task HumanOutput_AndFiles_NeverShowForeignCalendarYears(string culture)
    {
        WriteConfig(root => root["logging"] = new JsonObject { ["level"] = "Debug" });
        await File.WriteAllTextAsync(Path.Combine(WatchedDirectory, "a.txt"), "alpha");

        using var _ = new CultureScope(culture);
        var transcript = new System.Text.StringBuilder();
        async Task<string> RunLoggedAsync(params string[] args)
        {
            var (exit, stdout, stderr) = await RunAsync(args);
            transcript.AppendLine($"$ {string.Join(' ', args)} (exit {exit})").AppendLine(stdout).AppendLine(stderr);
            return stdout;
        }

        await RunLoggedAsync("backup", WatchedDirectory);
        var list = await RunLoggedAsync("snapshots", "--json");
        var manifest = JsonNode.Parse(list)!["groups"]!.AsArray().SelectMany(g => g!["snapshots"]!.AsArray()).Select(x => x!["manifestPath"]!.GetValue<string>()).First();
        await RunLoggedAsync("snapshots");
        await RunLoggedAsync("ls", manifest);
        await RunLoggedAsync("versions", manifest, "a.txt");
        await RunLoggedAsync("restore", manifest, Path.Combine(Root, "restored"), "--dry-run");
        await RunLoggedAsync("retention", "--dry-run");
        await RunLoggedAsync("doctor");
        await RunLoggedAsync("status");
        await RunLoggedAsync("activity");
        await RunLoggedAsync("usage");
        await RunLoggedAsync("logs");
        await RunLoggedAsync("diagnostics");
        await RunLoggedAsync("telemetry", "crash-reports");

        ForeignYear.Matches(transcript.ToString()).Select(m => m.Value).Should().BeEmpty("dates are always shown in the Gregorian calendar");
        foreach (var file in Directory.EnumerateFiles(AppPaths.Root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("config.json", StringComparison.Ordinal)))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var text = await new StreamReader(stream).ReadToEndAsync();
            ForeignYear.Matches(text).Select(m => m.Value).Should().BeEmpty($"{Path.GetRelativePath(AppPaths.Root, file)} must use Gregorian dates");
        }
    }
}

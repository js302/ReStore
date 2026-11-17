using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.telemetry;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>
/// Property and fuzz tests of the parsers and the scrubber. FsCheck supplies a seed per case (pinned with <c>Replay</c>, so runs are
/// reproducible; a failure prints the FsCheck seed plus the offending input); structured inputs are derived from that seed with
/// <see cref="Random"/>. The short run here (200 cases per property) is part of the normal test run; the long runs live in
/// <see cref="FuzzPropertyTests"/> under <c>Category=Fuzz</c>.
/// </summary>
public class PropertyTests
{
    public const int Cases = 200;

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,1")]
    public bool Schedule_Parse_NeverThrows_AndOccurrencesAreMonotonic(int seed) => PropertyBodies.ScheduleParseAndOccurrences(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,3")]
    public bool Schedule_ParsedExpression_IsAFixedPoint(int seed) => PropertyBodies.ScheduleCanonicalIsStable(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,5")]
    public bool PathMapping_IsWholeSegmentOnly_AndRejectsGarbageCleanly(int seed) => PropertyBodies.PathMapping(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,7")]
    public bool ConfigKeyPaths_NeverEscapeTheTargetedKey(int seed) => PropertyBodies.ConfigKeyPaths(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,9")]
    public bool ManifestReader_OnMutatedBytes_OnlyThrowsCleanErrors(int seed) => PropertyBodies.ManifestByteMutations(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,11")]
    public bool ManifestReader_OnTypeMutatedJson_OnlyThrowsCleanErrors(int seed) => PropertyBodies.ManifestJsonMutations(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = 60, Replay = "20261117,13")]
    public bool Chunker_ReassemblesTheInput_RespectsBounds_AndKeepsEarlierChunksStable(int seed) => PropertyBodies.Chunker(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,15")]
    public bool CrashScrubber_NeverLetsGeneratedSensitiveTokensSurvive(int seed) => PropertyBodies.Scrubber(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,17")]
    public bool Telemetry_AllowList_DropsEveryHostileValue(int seed) => PropertyBodies.TelemetryAllowList(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,19")]
    public bool CliDurations_RoundTrip(int seed) => PropertyBodies.Durations(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,21")]
    public bool SemVer_ParseFormat_IsStable_AndOrderIsTotal(int seed) => PropertyBodies.SemVerOrder(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,23")]
    public bool ExcludePatterns_NeverThrow_AndAgreeWithAReferenceForSimpleGlobs(int seed) => PropertyBodies.ExcludeGlobs(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261117,25")]
    public bool ScheduleOccurrences_AroundDstTransitions_AreMonotonicAndUnique(int seed) => PropertyBodies.ScheduleAroundDst(seed);
}

/// <summary>FsCheck sizes plain ints by test number (-100..100), which would replay only ~200 distinct seeds; this draws from the whole range.</summary>
public static class WideSeeds
{
    public static Arbitrary<int> Seed() => Arb.From(Gen.Choose(0, int.MaxValue));
}

/// <summary>The long runs: thousands of cases per property. <c>dotnet test --filter Category=Fuzz</c>; excluded from the default run.</summary>
[Trait("Category", "Fuzz")]
public class FuzzPropertyTests
{
    public const int Cases = 20_000;

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,1")]
    public bool Schedule_Parse_NeverThrows_AndOccurrencesAreMonotonic(int seed) => PropertyBodies.ScheduleParseAndOccurrences(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,3")]
    public bool Schedule_ParsedExpression_IsAFixedPoint(int seed) => PropertyBodies.ScheduleCanonicalIsStable(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,5")]
    public bool PathMapping_IsWholeSegmentOnly_AndRejectsGarbageCleanly(int seed) => PropertyBodies.PathMapping(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,7")]
    public bool ConfigKeyPaths_NeverEscapeTheTargetedKey(int seed) => PropertyBodies.ConfigKeyPaths(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,9")]
    public bool ManifestReader_OnMutatedBytes_OnlyThrowsCleanErrors(int seed) => PropertyBodies.ManifestByteMutations(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,11")]
    public bool ManifestReader_OnTypeMutatedJson_OnlyThrowsCleanErrors(int seed) => PropertyBodies.ManifestJsonMutations(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = 1500, Replay = "20261118,13")]
    public bool Chunker_ReassemblesTheInput_RespectsBounds_AndKeepsEarlierChunksStable(int seed) => PropertyBodies.Chunker(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,15")]
    public bool CrashScrubber_NeverLetsGeneratedSensitiveTokensSurvive(int seed) => PropertyBodies.Scrubber(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,17")]
    public bool Telemetry_AllowList_DropsEveryHostileValue(int seed) => PropertyBodies.TelemetryAllowList(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,19")]
    public bool CliDurations_RoundTrip(int seed) => PropertyBodies.Durations(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,21")]
    public bool SemVer_ParseFormat_IsStable_AndOrderIsTotal(int seed) => PropertyBodies.SemVerOrder(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,23")]
    public bool ExcludePatterns_NeverThrow_AndAgreeWithAReferenceForSimpleGlobs(int seed) => PropertyBodies.ExcludeGlobs(seed);

    [Property(Arbitrary = new[] { typeof(WideSeeds) }, MaxTest = Cases, Replay = "20261118,25")]
    public bool ScheduleOccurrences_AroundDstTransitions_AreMonotonicAndUnique(int seed) => PropertyBodies.ScheduleAroundDst(seed);
}

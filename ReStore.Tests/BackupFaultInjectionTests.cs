using System.Security.Cryptography;
using FluentAssertions;
using Moq;
using ReStore.Core.src.backup;
using ReStore.Core.src.core;
using ReStore.Core.src.monitoring;
using ReStore.Core.src.storage;
using ReStore.Core.src.utils;
using Xunit.Abstractions;

namespace ReStore.Tests;

/// <summary>
/// Crash-consistency of backups under storage faults: seeded probabilistic faults (transient/permanent errors, timeouts, truncated
/// uploads, list omissions, latency) and scripted "the Nth call fails" faults, with the repository invariants checked after every
/// run: HEAD always names a complete, verifiable snapshot; every chunk any committed manifest references exists and is intact; a failed
/// backup never damages the previous snapshot; a later fault-free backup plus <c>repair</c> leaves a clean repository.
/// A failing seed is part of the test name and reproduces exactly.
/// </summary>
public class BackupFaultInjectionTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(TestEnvironment.RootDirectory, "faults_" + Guid.NewGuid().ToString("N"));
    private readonly TestLogger _logger = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    /// <summary>The seeds of the property run (about 25, as the bounded default; fixed so every run is the same run).</summary>
    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 25).Select(seed => new object[] { seed });

    private sealed class Repo
    {
        public required InMemoryStorage Inner { get; init; }
        public required FaultyStorage Faulty { get; init; }
        public required string Source { get; init; }
        public required SystemState State { get; set; }
        public required string StatePath { get; init; }
        public required RepositoryCoordinator Coordinator { get; init; }
        public required int ManifestVersion { get; init; }
    }

    private Repo NewRepo(string name, long seed, double rate, int manifestVersion = 2, IReadOnlyList<FaultKind>? kinds = null)
    {
        var dir = Path.Combine(_root, name);
        var source = Path.Combine(dir, "src");
        Directory.CreateDirectory(source);
        var inner = new InMemoryStorage(_logger);
        var statePath = Path.Combine(dir, "state", "system_state.json");
        var state = new SystemState(_logger);
        state.SetStateFilePath(statePath);
        return new Repo
        {
            Inner = inner,
            Faulty = new FaultyStorage(inner, seed, rate, kinds) { Enabled = false },
            Source = source,
            State = state,
            StatePath = statePath,
            Coordinator = new RepositoryCoordinator(Path.Combine(dir, "repository.lock")),
            ManifestVersion = manifestVersion
        };
    }

    private Mock<IConfigManager> Config(IStorage storage, int manifestVersion)
    {
        var config = new Mock<IConfigManager>();
        config.SetupGet(c => c.Retention).Returns(new RetentionConfig { Enabled = false });
        config.SetupGet(c => c.GlobalStorageType).Returns("local");
        config.SetupGet(c => c.SizeThresholdMB).Returns(500);
        config.SetupGet(c => c.ExcludedPatterns).Returns([]);
        config.SetupGet(c => c.ExcludedPaths).Returns([]);
        config.SetupGet(c => c.BackupType).Returns(BackupType.ChunkSnapshot);
        config.SetupGet(c => c.WatchDirectories).Returns([]);
        config.SetupGet(c => c.MaxFileSizeMB).Returns(100);
        config.SetupGet(c => c.ChunkDiffing).Returns(new ChunkDiffingConfig { ManifestVersion = manifestVersion, MinChunkSizeKB = 2, TargetChunkSizeKB = 4, MaxChunkSizeKB = 8 });
        config.SetupGet(c => c.Encryption).Returns(new EncryptionConfig { Enabled = false });
        config.Setup(c => c.CreateStorageAsync(It.IsAny<string>())).ReturnsAsync(storage);
        return config;
    }

    private Task BackupAsync(Repo repo, IStorage storage) =>
        new Backup(_logger, repo.State, new SizeAnalyzer(), Config(storage, repo.ManifestVersion).Object, null, repo.Coordinator).BackupDirectoryAsync(repo.Source);

    // ---- source tree, deterministic from the seed ------------------------------------------------------------------

    private static void SeedTree(string source, Random rng)
    {
        for (var i = 0; i < 10; i++)
        {
            var dir = i % 3 == 0 ? source : Path.Combine(source, "d" + (i % 3), i % 2 == 0 ? "deep" : "");
            Directory.CreateDirectory(dir);
            var size = i == 4 ? 0 : i % 4 == 0 ? rng.Next(20_000, 60_000) : rng.Next(100, 6_000);
            var bytes = new byte[size];
            rng.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(dir, $"f{i}.bin"), bytes);
        }
    }

    private static void MutateTree(string source, Random rng)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        foreach (var file in files.Where(_ => rng.Next(3) == 0).Take(3))
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length > 10)
            {
                rng.NextBytes(bytes.AsSpan(rng.Next(bytes.Length / 2), Math.Min(64, bytes.Length / 2)));
                File.WriteAllBytes(file, bytes);
            }
        }

        var added = new byte[rng.Next(500, 12_000)];
        rng.NextBytes(added);
        File.WriteAllBytes(Path.Combine(source, $"added-{rng.Next(1_000_000)}.bin"), added);
        if (files.Count > 6)
        {
            File.Delete(files[rng.Next(files.Count)]);
        }
    }

    private static Dictionary<string, string> Hashes(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(directory, file).Replace('\\', '/'),
            file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));

    // ---- invariants ------------------------------------------------------------------------------------------------

    private static List<string> ManifestKeys(InMemoryStorage store) =>
        [.. store.Store.Keys.Where(k => k.StartsWith("snapshots/", StringComparison.Ordinal) && k.EndsWith(".manifest.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private static List<string> HeadKeys(InMemoryStorage store) =>
        [.. store.Store.Keys.Where(k => k.StartsWith("snapshots/", StringComparison.Ordinal) && k.EndsWith("/HEAD", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    /// <summary>Every committed manifest is complete and verifiable (all its chunks exist, intact); every HEAD resolves to such a manifest.</summary>
    private async Task AssertRepositoryConsistentAsync(InMemoryStorage store, string context)
    {
        var verifier = new SnapshotIntegrityVerifier(_logger, store, null);
        foreach (var manifest in ManifestKeys(store))
        {
            var result = await verifier.VerifyAsync(manifest);
            result.IsValid.Should().BeTrue($"{context}: manifest {manifest} must be complete: {string.Join("; ", result.Errors)}");
        }

        foreach (var head in HeadKeys(store))
        {
            var result = await verifier.VerifyAsync(head);
            result.IsValid.Should().BeTrue($"{context}: HEAD {head} must name a complete snapshot: {string.Join("; ", result.Errors)}");
        }
    }

    private async Task AssertRestoresAsync(InMemoryStorage store, string manifestPathOrHead, Dictionary<string, string> expected, string context)
    {
        var target = Path.Combine(_root, "restore_" + Guid.NewGuid().ToString("N"));
        await new Restore(_logger, store, null).RestoreFromBackupAsync(manifestPathOrHead, target);
        Hashes(target).Should().BeEquivalentTo(expected, $"{context}: {manifestPathOrHead} must restore exactly what was backed up");
    }

    // ---- the property test -------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task BackupSequencesUnderSeededFaults_KeepTheRepositoryConsistent_AndRecoverCleanly(int seed)
    {
        var rng = new Random(seed);
        var repo = NewRepo("seed" + seed, seed, rate: 0.03 + (seed % 5) * 0.02, manifestVersion: seed % 2 == 0 ? 2 : 3);
        SeedTree(repo.Source, rng);

        // Baseline snapshot, fault free.
        await BackupAsync(repo, repo.Faulty);
        var baselineManifest = ManifestKeys(repo.Inner).Should().ContainSingle().Subject;
        var baselineBytes = repo.Inner.Store[baselineManifest].ToArray();
        var baselineTree = Hashes(repo.Source);
        var committed = new List<(string Manifest, Dictionary<string, string> Tree)> { (baselineManifest, baselineTree) };
        var failures = 0;

        for (var round = 1; round <= 4; round++)
        {
            MutateTree(repo.Source, rng);
            var tree = Hashes(repo.Source);
            var manifestsBefore = ManifestKeys(repo.Inner).ToHashSet();

            repo.Faulty.Enabled = true;
            Exception? failure = null;
            try
            {
                await BackupAsync(repo, repo.Faulty);
            }
            catch (Exception ex)
            {
                failure = ex;
                failures++;
            }

            repo.Faulty.Enabled = false;
            var context = $"seed {seed} round {round} ({(failure == null ? "backup succeeded" : "backup failed: " + failure.GetType().Name)}; injected {repo.Faulty.Injected.Count})";

            await AssertRepositoryConsistentAsync(repo.Inner, context);
            repo.Inner.Store[baselineManifest].Should().Equal(baselineBytes, $"{context}: an earlier snapshot is never modified");

            var newManifests = ManifestKeys(repo.Inner).Except(manifestsBefore).ToList();
            if (failure == null)
            {
                newManifests.Should().ContainSingle($"{context}: a successful backup commits exactly one manifest");
                var head = HeadKeys(repo.Inner).Should().ContainSingle().Subject;
                await AssertRestoresAsync(repo.Inner, head, tree, context);
                committed.Add((newManifests[0], tree));
            }
            else
            {
                // A failed run may have written its manifest (a fault after it, e.g. on HEAD) but never an incomplete one, and it
                // never moves HEAD to a snapshot it did not complete: HEAD still restores a snapshot we know.
                var headNow = HeadKeys(repo.Inner).Should().ContainSingle().Subject;
                var resolved = await RepositoryDiscovery.ResolveManifestPathAsync(repo.Inner, headNow);
                (committed.Select(c => c.Manifest).Contains(resolved) || newManifests.Contains(resolved)).Should().BeTrue($"{context}: HEAD names a known snapshot");
                if (committed.Select(c => c.Manifest).Contains(resolved))
                {
                    await AssertRestoresAsync(repo.Inner, resolved, committed.First(c => c.Manifest == resolved).Tree, context);
                }
            }

            // A restarted process reads the state back from disk.
            if (round % 2 == 0)
            {
                repo.State = new SystemState(_logger);
                repo.State.SetStateFilePath(repo.StatePath);
                await repo.State.LoadStateAsync();
            }
        }

        // Recovery: one fault-free backup of the current tree, then repair (no grace period) removes leftovers of failed runs.
        await BackupAsync(repo, repo.Faulty);
        var finalTree = Hashes(repo.Source);
        await AssertRepositoryConsistentAsync(repo.Inner, $"seed {seed} after recovery backup");
        await AssertRestoresAsync(repo.Inner, HeadKeys(repo.Inner).Single(), finalTree, $"seed {seed} after recovery backup");

        var config = Config(repo.Faulty, repo.ManifestVersion);
        var repair = new RepositoryRepair(_logger, config.Object, repo.State, repo.Coordinator);
        await repair.RunAsync(repo.Faulty, "local", new RepairOptions { GracePeriod = TimeSpan.Zero });
        var dry = await repair.RunAsync(repo.Faulty, "local", new RepairOptions { DryRun = true, GracePeriod = TimeSpan.Zero });
        dry.OrphanChunks.Should().Be(0, $"seed {seed}: after repair nothing unreferenced is left");
        await AssertRepositoryConsistentAsync(repo.Inner, $"seed {seed} after repair");
        foreach (var (manifest, tree) in committed.Take(1).Concat(committed.TakeLast(1)))
        {
            // Snapshots committed before repair still restore afterwards (repair never removes a referenced chunk).
            await AssertRestoresAsync(repo.Inner, manifest, tree, $"seed {seed} after repair");
        }

        output.WriteLine($"seed {seed}: {failures} failed run(s), {repo.Faulty.Injected.Count} injected fault(s): "
            + string.Join(", ", repo.Faulty.Injected.GroupBy(i => i.Split(':')[0] + ":" + i.Split(':')[1]).Select(g => $"{g.Key} x{g.Count()}")));
    }

    // ---- scripted: the Nth call of each operation fails ------------------------------------------------------------------

    public static IEnumerable<object[]> ScriptedFaults()
    {
        foreach (var op in new[] { StorageOp.Upload, StorageOp.Exists, StorageOp.List, StorageOp.Download })
        {
            foreach (var nth in new[] { 1, 3, 8, 21 })
            {
                foreach (var kind in new[] { FaultKind.Transient, FaultKind.Timeout })
                {
                    yield return new object[] { op, nth, kind };
                }
            }
        }

        foreach (var nth in new[] { 1, 9 })
        {
            yield return new object[] { StorageOp.Upload, nth, FaultKind.PartialWriteThenThrow };
        }
    }

    [Theory]
    [MemberData(nameof(ScriptedFaults))]
    public async Task ScriptedFaultOnTheNthCall_NeverCorruptsThePreviousSnapshot_AndTheNextBackupConverges(StorageOp op, int nth, FaultKind kind)
    {
        var repo = NewRepo($"script_{op}_{nth}_{kind}", 0, 0);
        var rng = new Random(77);
        SeedTree(repo.Source, rng);
        await BackupAsync(repo, repo.Faulty);
        var firstManifest = ManifestKeys(repo.Inner).Single();
        var firstBytes = repo.Inner.Store[firstManifest].ToArray();
        var firstTree = Hashes(repo.Source);

        MutateTree(repo.Source, rng);
        var secondTree = Hashes(repo.Source);
        repo.Faulty.Script.Add(new ScriptedFault(op, nth, kind));
        repo.Faulty.Enabled = true;
        var failed = false;
        try { await BackupAsync(repo, repo.Faulty); }
        catch (Exception) { failed = true; }
        repo.Faulty.Enabled = false;

        var context = $"{op} #{nth} {kind} ({(failed ? "backup failed" : "backup survived")}; injected {repo.Faulty.Injected.Count})";
        await AssertRepositoryConsistentAsync(repo.Inner, context);
        repo.Inner.Store[firstManifest].Should().Equal(firstBytes, context);
        if (failed)
        {
            // HEAD still names the first snapshot or the (complete) second one.
            var resolved = await RepositoryDiscovery.ResolveManifestPathAsync(repo.Inner, HeadKeys(repo.Inner).Single());
            if (resolved == firstManifest)
            {
                await AssertRestoresAsync(repo.Inner, firstManifest, firstTree, context);
            }
        }
        else
        {
            await AssertRestoresAsync(repo.Inner, HeadKeys(repo.Inner).Single(), secondTree, context);
        }

        // The next, fault-free backup converges: HEAD restores the current tree and no new fault was injected.
        await BackupAsync(repo, repo.Faulty);
        await AssertRepositoryConsistentAsync(repo.Inner, context + " / recovery");
        await AssertRestoresAsync(repo.Inner, HeadKeys(repo.Inner).Single(), secondTree, context + " / recovery");
    }

    // ---- other operations under faults --------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RepairUnderDeleteAndListFaults_NeverRemovesAReferencedChunk(int seed)
    {
        var repo = NewRepo("repairfaults" + seed, seed, 0.15, kinds: [FaultKind.Transient, FaultKind.Timeout, FaultKind.ListOmission]);
        var rng = new Random(seed);
        SeedTree(repo.Source, rng);
        await BackupAsync(repo, repo.Faulty);
        MutateTree(repo.Source, rng);
        await BackupAsync(repo, repo.Faulty);
        var tree = Hashes(repo.Source);
        // Orphans of an abandoned run.
        foreach (var i in Enumerable.Range(0, 20))
        {
            repo.Inner.Store[$"chunks/ab/{i:x2}{new string('0', 62)}.chunk"] = [1, 2, 3];
        }

        repo.Faulty.Enabled = true;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                await new RepositoryRepair(_logger, Config(repo.Faulty, 2).Object, repo.State, repo.Coordinator)
                    .RunAsync(repo.Faulty, "local", new RepairOptions { GracePeriod = TimeSpan.Zero });
            }
            catch (Exception)
            {
                // Aborting is allowed; deleting a referenced chunk is not.
            }

            repo.Faulty.Enabled = false;
            await AssertRepositoryConsistentAsync(repo.Inner, $"seed {seed} repair attempt {attempt}");
            repo.Faulty.Enabled = true;
        }

        repo.Faulty.Enabled = false;
        await AssertRestoresAsync(repo.Inner, HeadKeys(repo.Inner).Single(), tree, $"seed {seed}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RestoreUnderFaults_EitherFailsOrProducesExactlyTheSnapshot(int seed)
    {
        var repo = NewRepo("restorefaults" + seed, seed, 0.12, kinds: [FaultKind.Transient, FaultKind.Timeout, FaultKind.Latency]);
        SeedTree(repo.Source, new Random(seed));
        await BackupAsync(repo, repo.Faulty);
        var tree = Hashes(repo.Source);
        var head = HeadKeys(repo.Inner).Single();

        repo.Faulty.Enabled = true;
        var failedRuns = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var target = Path.Combine(_root, $"restore-faulty-{seed}-{attempt}");
            try
            {
                await new Restore(_logger, repo.Faulty, null).RestoreFromBackupAsync(head, target);
                Hashes(target).Should().BeEquivalentTo(tree, "a restore that reports success restored exactly the snapshot");
            }
            catch (Exception)
            {
                failedRuns++;
                // Whatever a failed restore leaves behind is a subset of correct files, never wrong content.
                if (Directory.Exists(target))
                {
                    foreach (var (rel, hash) in Hashes(target).Where(kv => !kv.Key.EndsWith(".part", StringComparison.Ordinal)))
                    {
                        tree.Should().ContainKey(rel);
                        hash.Should().Be(tree[rel], $"{rel} left by a failed restore must be complete");
                    }
                }
            }
        }

        output.WriteLine($"seed {seed}: {failedRuns}/5 restores failed under faults");
    }
}

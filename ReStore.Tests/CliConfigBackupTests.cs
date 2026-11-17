using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>I5: backups/config.pre-*.json follow the live file's secrets and are pruned.</summary>
public class CliConfigBackupTests : CliConfigTestBase
{
    private const string Secret = "plain-secret-value-12345";

    private static string[] AllBackups() =>
        Directory.Exists(Path.Combine(AppPaths.Root, "backups")) ? Directory.GetFiles(Path.Combine(AppPaths.Root, "backups"), "config.pre-*.json") : [];

    private void WriteConfigWithPlainSecret() => WriteConfig(root =>
    {
        var sources = root["storageSources"]!.AsObject();
        sources["s3"] = new JsonObject { ["options"] = new JsonObject { ["bucketName"] = "b", ["accessKeyId"] = "AKIA", ["secretAccessKey"] = Secret, ["region"] = "eu-west-1" } };
    });

    [Fact]
    public async Task ConfigProtect_ProtectsTheSecretsInExistingBackupsToo()
    {
        WriteConfigWithPlainSecret();
        (await RunAsync("config", "set", "retention.keepDaily", "3")).ExitCode.Should().Be(0);
        (await RunAsync("config", "set", "retention.keepDaily", "4")).ExitCode.Should().Be(0);
        AllBackups().Should().NotBeEmpty();
        AllBackups().Select(File.ReadAllText).Should().OnlyContain(t => t.Contains(Secret), "precondition: the backups hold the plain secret");

        var manager = new ConfigManager(new TestLogger());
        await manager.LoadAsync();
        var changed = await ConfigSecretsCommands.ProtectAsync(manager, new FakeSecretProtector());

        changed.Should().Contain("s3.secretAccessKey");
        File.ReadAllText(AppPaths.ConfigFile).Should().NotContain(Secret);
        foreach (var backup in AllBackups())
        {
            var text = File.ReadAllText(backup);
            text.Should().NotContain(Secret, backup);
            text.Should().Contain("dpapi:", "the backup stays restorable with the protected value");
        }
    }

    [Fact]
    public async Task ScrubExisting_ProtectsOrphanedSecrets_AndDeletesWhatCannotBeProtected()
    {
        WriteConfigWithPlainSecret();
        (await RunAsync("config", "set", "retention.keepDaily", "3")).ExitCode.Should().Be(0);
        var live = ReadConfig();
        live["storageSources"]!.AsObject().Remove("s3");

        ConfigBackups.ScrubExisting(AppPaths.ConfigFile, live, new FakeSecretProtector(), protectRemaining: true).Should().BeGreaterThan(0);
        AllBackups().Select(File.ReadAllText).Should().OnlyContain(t => !t.Contains(Secret) && t.Contains("dpapi:"));

        // A backup that still holds a literal and cannot be protected is removed.
        File.WriteAllText(Path.Combine(AppPaths.Root, "backups", "config.pre-change.20000101000000_x.json"), ReadConfigText());
        ConfigBackups.ScrubExisting(AppPaths.ConfigFile, live, new FakeSecretProtector(available: false), protectRemaining: true);
        AllBackups().Select(File.ReadAllText).Should().OnlyContain(t => !t.Contains(Secret));
        AllBackups().Should().NotContain(f => f.EndsWith("_x.json"));
    }

    private static string ReadConfigText() => File.ReadAllText(AppPaths.ConfigFile);

    [Fact]
    public async Task ConfigSetWithProtect_WritesTheBackupWithoutThePlainSecret_AndScrubsOlderOnes()
    {
        WriteConfigWithPlainSecret();
        (await RunAsync("config", "set", "retention.keepDaily", "3")).ExitCode.Should().Be(0);
        AllBackups().Select(File.ReadAllText).Should().OnlyContain(t => t.Contains(Secret));

        var (exit, _, err) = await RunWithStdinAsync(Secret + "\n", "config", "set", "storageSources.s3.options.secretAccessKey", "--value-stdin", "--protect");

        exit.Should().Be(0, err);
        AllBackups().Should().NotBeEmpty();
        AllBackups().Select(File.ReadAllText).Should().OnlyContain(t => !t.Contains(Secret), "neither the new nor the older backups keep the literal");
    }

    [Fact]
    public async Task ConfigBackups_AreCappedAtTheNewestTwenty()
    {
        WriteConfig();
        for (var i = 1; i <= ConfigBackups.KeepCount + 5; i++)
        {
            (await RunAsync("config", "set", "retention.keepDaily", i.ToString())).ExitCode.Should().Be(0);
        }

        AllBackups().Length.Should().Be(ConfigBackups.KeepCount);
        // The newest backup holds the previous value, so it is among those kept.
        AllBackups().Select(File.ReadAllText).Should().Contain(t => t.Contains($"\"keepDaily\":{ConfigBackups.KeepCount + 4}") || t.Contains($"\"keepDaily\": {ConfigBackups.KeepCount + 4}"));
    }
}

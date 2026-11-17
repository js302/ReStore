using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.cli;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary>Review fixes that touch the setup/configuration commands: excluded watch folders, S3-compatible endpoints.</summary>
public class CliFixConfigTests : CliConfigTestBase
{
    // ---- B3: watch directories inside an exclusion --------------------------------------------------------------------

    [Fact]
    public async Task AddWatch_InsideAnExcludedPath_WarnsInTheDocumentAndOnStderr()
    {
        var excludedParent = Path.Combine(Root, "scratch");
        var folder = Path.Combine(excludedParent, "project");
        Directory.CreateDirectory(folder);
        WriteConfig(root => root["excludedPaths"] = new JsonArray(excludedParent));

        var (exitCode, stdout, stderr) = await RunAsync("config", "add-watch", folder, "--json");

        exitCode.Should().Be(0, stdout + stderr);
        var warnings = Json(stdout)["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Where(w => w.Contains("is excluded by")).ToList();
        warnings.Should().ContainSingle().Which.Should().Contain("excludedPaths entry").And.Contain(excludedParent);
        stderr.Should().Contain("excludedPaths entry");
        ReadConfig()["watchDirectories"]!.AsArray().Should().HaveCount(2, "it is still added: the warning is advice, not a refusal");
    }

    [Fact]
    public async Task AddWatch_MatchedByAnExcludeGlob_Warns()
    {
        var folder = Path.Combine(Root, "node_modules");
        Directory.CreateDirectory(folder);
        WriteConfig(root => root["excludeGlobs"] = new JsonArray("**/node_modules"));

        var (_, stdout, stderr) = await RunAsync("config", "add-watch", folder, "--json");

        Json(stdout)["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Where(w => w.Contains("is excluded by")).Should()
            .ContainSingle().Which.Should().Contain("excludeGlobs pattern");
        stderr.Should().Contain("excludeGlobs");
    }

    [Fact]
    public async Task AddWatch_OfAnOrdinaryFolder_HasNoWarning()
    {
        var folder = Path.Combine(Root, "ordinary");
        Directory.CreateDirectory(folder);
        WriteConfig();

        var (exitCode, stdout, stderr) = await RunAsync("config", "add-watch", folder, "--json");

        exitCode.Should().Be(0);
        Json(stdout)["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().NotContain(w => w.Contains("is excluded by"));
        stderr.Should().NotContain("is excluded by");
    }

    [Fact]
    public async Task Init_WithAWatchFolderInsideTheDefaultExcludedTempFolder_WarnsAndExplainsTheFix()
    {
        WritePristineConfig();
        var tempRule = ReadConfig()["excludedPaths"]!.AsArray().Select(p => p!.GetValue<string>())
            .FirstOrDefault(p => p.Contains("Temp", StringComparison.OrdinalIgnoreCase) || p.Contains("tmp", StringComparison.OrdinalIgnoreCase));
        // The test root is under the temp folder, which the shipped config excludes (%TEMP%); skip when this machine's rules differ.
        if (tempRule == null)
        {
            return;
        }

        var (exitCode, stdout, stderr) = await RunAsync(
            "init", "--storage", "local", "--path", Path.Combine(Root, "init-store"), "--watch", WatchedDirectory, "--json");

        exitCode.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().Contain(w => w.Contains("excludedPaths entry") && w.Contains(WatchedDirectory));
        doc["nextSteps"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Contain(s => s.Contains("config set excludedPaths"));
        stderr.Should().Contain("excludedPaths entry");

        var (_, humanOut, _) = await RunAsync("init", "--storage", "local", "--path", Path.Combine(Root, "init-store"), "--watch", WatchedDirectory, "--force");
        humanOut.Should().Contain("Warning:").And.Contain("excludedPaths entry");
    }

    [Fact]
    public async Task Init_WithAnOrdinaryWatchFolder_HasNoExclusionWarning()
    {
        WritePristineConfig();
        // Lift the temp-folder default so the test root (under the temp folder) is an ordinary folder.
        (await RunAsync("config", "set", "excludedPaths", "[]")).ExitCode.Should().Be(0);

        var (exitCode, stdout, stderr) = await RunAsync(
            "init", "--storage", "local", "--path", Path.Combine(Root, "init-store"), "--watch", WatchedDirectory, "--force", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        Json(stdout)["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).Should().NotContain(w => w.Contains("is excluded by"));
    }

    // ---- B7: S3 serviceUrl --------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetStorage_S3_AcceptsServiceUrl()
    {
        WriteConfig();

        var (exitCode, stdout, stderr) = await RunAsync(
            "config", "set-storage", "s3",
            "--option", "region=us-east-1", "--option", "bucketName=my-backups", "--option", "accessKeyId=minio",
            "--option", "secretAccessKey=env:MINIO_SECRET", "--option", "serviceUrl=http://localhost:9000", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        ReadConfig()["storageSources"]!["s3"]!["options"]!["serviceUrl"]!.GetValue<string>().Should().Be("http://localhost:9000");
        Json(stdout)["options"]!["serviceUrl"]!.GetValue<string>().Should().Be("http://localhost:9000");
    }

    [Fact]
    public void StorageCatalog_S3_KnowsServiceUrl_ButOtherTypesDoNot()
    {
        StorageCatalog.CanonicalOption(StorageCatalog.Find("s3")!, "serviceurl").Should().Be("serviceUrl");
        StorageCatalog.CanonicalOption(StorageCatalog.Find("azure")!, "serviceUrl").Should().BeNull();
    }

    [Fact]
    public void Validator_S3WithServiceUrl_AcceptsAnyRegion_AndRejectsABadUrl()
    {
        StorageConfig S3(string region, string? url)
        {
            var options = new Dictionary<string, string>
            {
                ["accessKeyId"] = "k", ["secretAccessKey"] = "s", ["region"] = region, ["bucketName"] = "backups"
            };
            if (url != null)
            {
                options["serviceUrl"] = url;
            }

            return new StorageConfig { Options = options };
        }

        ConfigValidationResult Validate(StorageConfig config)
        {
            var manager = new Moq.Mock<IConfigManager>();
            manager.SetupGet(m => m.StorageSources).Returns(new Dictionary<string, StorageConfig> { ["s3"] = config });
            return new ConfigValidator(new TestLogger()).ValidateStorageOnly(manager.Object, "s3");
        }

        var plain = Validate(S3("auto", null));
        plain.Warnings.Should().Contain(w => w.Contains("invalid AWS region"), "without serviceUrl the region is checked as before");

        var compatible = Validate(S3("auto", "https://acct.r2.cloudflarestorage.com"));
        compatible.Errors.Should().BeEmpty();
        compatible.Warnings.Should().NotContain(w => w.Contains("region"));

        var bad = Validate(S3("us-east-1", "minio:9000"));
        bad.Errors.Should().ContainSingle().Which.Should().Contain("serviceUrl");
    }

    [Fact]
    public void ConfigSchema_DescribesServiceUrlForS3()
    {
        var schema = JsonNode.Parse(ConfigSchema.Text)!;
        var options = schema["properties"]!["storageSources"]!["properties"]!["s3"]!["properties"]!["options"]!["properties"]!.AsObject();

        options.ContainsKey("serviceUrl").Should().BeTrue();
        options["serviceUrl"]!["description"]!.GetValue<string>().Should().Contain("S3-compatible");
    }
}

using System.Text.Json.Nodes;
using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

/// <summary><c>config path|get|set|unset|add-watch|remove-watch|list-watch|set-storage|schema</c>, run in-process against a temp data root.</summary>
public class CliConfigCommandsTests : CliConfigTestBase
{
    // ---- path / schema ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConfigPath_PrintsTheFile_AndJsonHasTheDataRoot_WithoutCreatingAnything()
    {
        var (exitCode, stdout, _) = await RunAsync("config", "path");
        exitCode.Should().Be(0);
        stdout.Trim().Should().Be(AppPaths.ConfigFile);

        var (jsonExit, jsonOut, _) = await RunAsync("config", "path", "--json");
        jsonExit.Should().Be(0);
        var doc = Json(jsonOut);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["configFile"]!.GetValue<string>().Should().Be(AppPaths.ConfigFile);
        doc["dataRoot"]!.GetValue<string>().Should().Be(AppPaths.Root);
        doc["exists"]!.GetValue<bool>().Should().BeFalse();
        File.Exists(AppPaths.ConfigFile).Should().BeFalse();
    }

    [Fact]
    public async Task ConfigSchema_PrintsTheSchema_AsTextAndInsideTheJsonDocument()
    {
        var (exitCode, stdout, _) = await RunAsync("config", "schema");
        exitCode.Should().Be(0);
        Json(stdout)["$schema"]!.GetValue<string>().Should().Be("https://json-schema.org/draft/2020-12/schema");

        var (jsonExit, jsonOut, _) = await RunAsync("config", "schema", "--json");
        jsonExit.Should().Be(0);
        var doc = Json(jsonOut);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["schema"]!["properties"]!["retention"]!["properties"]!["keepDaily"]!["description"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        File.Exists(AppPaths.ConfigFile).Should().BeFalse("the schema needs no configuration");
    }

    // ---- get -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConfigGet_ReadsScalarsListsAndObjects_AndTheWholeFile()
    {
        WriteConfig(root => root["retention"] = new JsonObject { ["enabled"] = true, ["keepDaily"] = 7, ["mode"] = "any" });

        var (_, plain, _) = await RunAsync("config", "get", "retention.keepDaily");
        plain.Trim().Should().Be("7");

        var (_, text, _) = await RunAsync("config", "get", "globalStorageType");
        text.Trim().Should().Be("local", "strings print without quotes");

        var (_, list, _) = await RunAsync("config", "get", "watchDirectories");
        Json("{\"x\":" + list + "}")["x"]!.AsArray().Should().HaveCount(1);

        var (_, item, _) = await RunAsync("config", "get", "watchDirectories.0.path");
        item.Trim().Should().Be(WatchedDirectory);

        var (exitCode, json, _) = await RunAsync("config", "get", "retention", "--json");
        exitCode.Should().Be(0);
        var doc = Json(json);
        doc["key"]!.GetValue<string>().Should().Be("retention");
        doc["value"]!["keepDaily"]!.GetValue<int>().Should().Be(7);
        doc["secretsRevealed"]!.GetValue<bool>().Should().BeFalse();

        var (_, whole, _) = await RunAsync("config", "get", "--json");
        Json(whole)["value"]!["globalStorageType"]!.GetValue<string>().Should().Be("local");
    }

    [Fact]
    public async Task ConfigGet_MasksSecretsLikeConfigShow_UnlessRevealed()
    {
        WriteConfig(root => root["storageSources"] = new JsonObject
        {
            ["local"] = new JsonObject { ["path"] = StorageDirectory },
            ["s3"] = new JsonObject
            {
                ["options"] = new JsonObject
                {
                    ["accessKeyId"] = "AKIATEST", ["secretAccessKey"] = "very-secret", ["region"] = "eu-west-1", ["bucketName"] = "b",
                    ["token"] = "env:S3_TOKEN"
                }
            }
        });

        var (_, masked, _) = await RunAsync("config", "get", "storageSources.s3.options", "--json");
        var options = Json(masked)["value"]!;
        options["secretAccessKey"]!.GetValue<string>().Should().Be("***");
        options["accessKeyId"]!.GetValue<string>().Should().Be("AKIATEST");
        options["token"]!.GetValue<string>().Should().Be("env:S3_TOKEN", "references stay visible");
        masked.Should().NotContain("very-secret");

        var (_, single, _) = await RunAsync("config", "get", "storageSources.s3.options.secretAccessKey");
        single.Trim().Should().Be("***");

        var (_, revealed, _) = await RunAsync("config", "get", "storageSources.s3.options.secretAccessKey", "--reveal");
        revealed.Trim().Should().Be("very-secret");
    }

    [Fact]
    public async Task ConfigGet_UnknownOrUnsetKeys_AreUsageErrors()
    {
        WriteConfig();

        var (missing, _, missingErr) = await RunAsync("config", "get", "nope.nothing");
        missing.Should().Be(2);
        missingErr.Should().Contain("not a known key");

        var (unset, json, _) = await RunAsync("config", "get", "storageSources.s3.options.region", "--json");
        unset.Should().Be(2, "s3 is not configured in this file");
        Json(json)["error"]!["code"]!.GetValue<string>().Should().Be("usage");

        var (empty, _, _) = await RunAsync("config", "get", "a..b");
        empty.Should().Be(2);
    }

    // ---- set -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConfigSet_ParsesJsonValues_AndFallsBackToStrings()
    {
        WriteConfig();

        (await RunAsync("config", "set", "retention.keepDaily", "7")).ExitCode.Should().Be(0);
        (await RunAsync("config", "set", "telemetry.enabled", "false")).ExitCode.Should().Be(0);
        (await RunAsync("config", "set", "backupInterval", "02:00:00")).ExitCode.Should().Be(0);
        (await RunAsync("config", "set", "excludedPatterns", "[\"*.bak\",\"*.old\"]")).ExitCode.Should().Be(0);
        (await RunAsync("config", "set", "logging.level", "\"Debug\"")).ExitCode.Should().Be(0);

        var config = ReadConfig();
        config["retention"]!["keepDaily"]!.GetValue<int>().Should().Be(7);
        config["telemetry"]!["enabled"]!.GetValue<bool>().Should().BeFalse();
        config["backupInterval"]!.GetValue<string>().Should().Be("02:00:00");
        config["excludedPatterns"]!.AsArray().Select(p => p!.GetValue<string>()).Should().Equal("*.bak", "*.old");
        config["logging"]!["level"]!.GetValue<string>().Should().Be("Debug");
    }

    [Fact]
    public async Task ConfigSet_Json_ReportsTheChange_AndBacksUpThePreviousFile()
    {
        WriteConfig();
        var before = File.ReadAllText(AppPaths.ConfigFile);

        var (exitCode, stdout, _) = await RunAsync("config", "set", "retention.keepDaily", "3", "--json");

        exitCode.Should().Be(0);
        var doc = Json(stdout);
        doc["ok"]!.GetValue<bool>().Should().BeTrue();
        doc["key"]!.GetValue<string>().Should().Be("retention.keepDaily");
        doc["value"]!.GetValue<int>().Should().Be(3);
        doc["changed"]!.GetValue<bool>().Should().BeTrue();
        var backup = doc["backup"]!.GetValue<string>();
        File.ReadAllText(backup).Should().Be(before);
        BackupFiles().Should().Contain(backup);

        var (_, again, _) = await RunAsync("config", "set", "retention.keepDaily", "3", "--json");
        Json(again)["changed"]!.GetValue<bool>().Should().BeFalse();
        Json(again)["backup"].Should().BeNull("nothing was rewritten");
    }

    [Fact]
    public async Task ConfigSet_PreservesUnknownKeys_EnvReferencesAndPercentValues()
    {
        WriteConfig(root =>
        {
            root["futureFeature"] = new JsonObject { ["flag"] = true, ["list"] = new JsonArray(1, 2) };
            root["storageSources"] = new JsonObject
            {
                ["local"] = new JsonObject { ["path"] = StorageDirectory, ["options"] = new JsonObject(), ["note"] = "kept" },
                ["s3"] = new JsonObject
                {
                    ["options"] = new JsonObject
                    {
                        ["accessKeyId"] = "%AWS_KEY%", ["secretAccessKey"] = "env:AWS_SECRET", ["region"] = "eu-west-1", ["bucketName"] = "bucket-1"
                    }
                }
            };
        });

        (await RunAsync("config", "set", "retention.keepDaily", "5")).ExitCode.Should().Be(0);

        var config = ReadConfig();
        config["futureFeature"]!["flag"]!.GetValue<bool>().Should().BeTrue();
        config["futureFeature"]!["list"]!.AsArray().Should().HaveCount(2);
        config["storageSources"]!["local"]!["note"]!.GetValue<string>().Should().Be("kept");
        var options = config["storageSources"]!["s3"]!["options"]!;
        options["accessKeyId"]!.GetValue<string>().Should().Be("%AWS_KEY%");
        options["secretAccessKey"]!.GetValue<string>().Should().Be("env:AWS_SECRET");
    }

    [Fact]
    public async Task ConfigSet_InvalidValues_ExitThree_AndLeaveTheFileUntouched()
    {
        WriteConfig();
        var before = File.ReadAllBytes(AppPaths.ConfigFile);

        var (negative, negativeOut, _) = await RunAsync("config", "set", "retention.keepDaily", "-5", "--json");
        negative.Should().Be(3);
        var doc = Json(negativeOut);
        doc["ok"]!.GetValue<bool>().Should().BeFalse();
        doc["applied"]!.GetValue<bool>().Should().BeFalse();
        doc["error"]!["code"]!.GetValue<string>().Should().Be("config");
        doc["errors"]!.AsArray().Select(e => e!.GetValue<string>()).Should().Contain(e => e.Contains("keepDaily"));

        (await RunAsync("config", "set", "backupType", "Bogus")).ExitCode.Should().Be(3);
        (await RunAsync("config", "set", "sizeThresholdMB", "\"abc\"")).ExitCode.Should().Be(3);
        (await RunAsync("config", "set", "globalStorageType", "s3")).ExitCode.Should().Be(3, "s3 is not configured");
        (await RunAsync("config", "set", "logging.level", "Loud")).ExitCode.Should().Be(3);

        File.ReadAllBytes(AppPaths.ConfigFile).Should().Equal(before);
        BackupFiles().Should().BeEmpty("a refused change writes no backup");
    }

    [Fact]
    public async Task ConfigSet_ExistingProblemsDoNotBlockAnUnrelatedChange()
    {
        // The only watched folder is gone: the config is already invalid, but changing the retention is still fine.
        WriteConfig(root => root["watchDirectories"] = new JsonArray(new JsonObject { ["path"] = Path.Combine(Root, "gone"), ["storageType"] = null }));

        (await RunAsync("config", "set", "retention.keepDaily", "4")).ExitCode.Should().Be(0);
        ReadConfig()["retention"]!["keepDaily"]!.GetValue<int>().Should().Be(4);
    }

    [Fact]
    public async Task ConfigSet_UnknownKeys_AreRefusedUnlessForced()
    {
        WriteConfig();

        var (typo, _, err) = await RunAsync("config", "set", "retention.keepDayly", "5");
        typo.Should().Be(2);
        err.Should().Contain("not a known setting");
        (await RunAsync("config", "set", "newTopLevel", "1")).ExitCode.Should().Be(2);
        (await RunAsync("config", "set", "storageSources.s3.options.bucketnme", "x")).ExitCode.Should().Be(2);
        ReadConfig().ContainsKey("newTopLevel").Should().BeFalse();

        (await RunAsync("config", "set", "newTopLevel", "1", "--force")).ExitCode.Should().Be(0);
        ReadConfig()["newTopLevel"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ConfigSet_UsesTheSchemasSpellingAndKeepsTextSettingsAsText()
    {
        WriteConfig(root => root["storageSources"] = new JsonObject
        {
            ["local"] = new JsonObject { ["path"] = StorageDirectory },
            ["sftp"] = new JsonObject { ["options"] = new JsonObject { ["host"] = "h", ["username"] = "u", ["password"] = "env:P" } }
        });

        (await RunAsync("config", "set", "storageSources.sftp.options.PORT", "2222")).ExitCode.Should().Be(0);
        (await RunAsync("config", "set", "RETENTION.keepdaily", "2")).ExitCode.Should().Be(0);

        var config = ReadConfig();
        config["storageSources"]!["sftp"]!["options"]!["port"]!.GetValue<string>().Should().Be("2222", "provider options are strings");
        config["retention"]!["keepDaily"]!.GetValue<int>().Should().Be(2);
        config["storageSources"]!["sftp"]!["options"]!.AsObject().ContainsKey("PORT").Should().BeFalse();
    }

    [Fact]
    public async Task ConfigSet_ValueStdin_KeepsTheSecretOffTheCommandLine()
    {
        WriteConfig(root => root["storageSources"] = new JsonObject
        {
            ["local"] = new JsonObject { ["path"] = StorageDirectory },
            ["s3"] = new JsonObject
            {
                ["options"] = new JsonObject { ["accessKeyId"] = "a", ["secretAccessKey"] = "old", ["region"] = "eu-west-1", ["bucketName"] = "bucket-1" }
            }
        });

        var (exitCode, stdout, stderr) = await RunWithStdinAsync("brand-new-secret\n", "config", "set", "storageSources.s3.options.secretAccessKey", "--value-stdin", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        ReadConfig()["storageSources"]!["s3"]!["options"]!["secretAccessKey"]!.GetValue<string>().Should().Be("brand-new-secret");
        var doc = Json(stdout);
        doc["value"]!.GetValue<string>().Should().Be("***");
        doc["warnings"]!.AsArray().Should().NotBeEmpty("a literal secret in the file deserves a hint");
        (stdout + stderr).Should().NotContain("brand-new-secret");

        var (both, _, _) = await RunWithStdinAsync("x", "config", "set", "retention.keepDaily", "5", "--value-stdin");
        both.Should().Be(2);
        var (neither, _, _) = await RunAsync("config", "set", "retention.keepDaily");
        neither.Should().Be(2);
        (await RunWithStdinAsync(string.Empty, "config", "set", "retention.keepDaily", "--value-stdin")).ExitCode.Should().Be(2);
    }

    [Fact]
    public async Task ConfigSet_Protect_StoresADpapiReference_OnlyForSecretOptions()
    {
        WriteConfig(root => root["storageSources"] = new JsonObject
        {
            ["local"] = new JsonObject { ["path"] = StorageDirectory },
            ["s3"] = new JsonObject
            {
                ["options"] = new JsonObject { ["accessKeyId"] = "a", ["secretAccessKey"] = "old", ["region"] = "eu-west-1", ["bucketName"] = "bucket-1" }
            }
        });

        var (exitCode, stdout, _) = await RunWithStdinAsync("top-secret", "config", "set", "storageSources.s3.options.secretAccessKey", "--value-stdin", "--protect", "--json");

        exitCode.Should().Be(0);
        var stored = ReadConfig()["storageSources"]!["s3"]!["options"]!["secretAccessKey"]!.GetValue<string>();
        stored.Should().StartWith("dpapi:");
        SecretReferences.Resolve("secretAccessKey", stored, CliServicesProtector()).Should().Be("top-secret");
        stdout.Should().NotContain("top-secret");
        Json(stdout)["warnings"]!.AsArray().Should().BeEmpty();

        (await RunAsync("config", "set", "retention.keepDaily", "3", "--protect")).ExitCode.Should().Be(2, "--protect is for secret options");

        ReStore.Core.src.cli.CliServices.SecretProtector = new FakeSecretProtector(available: false);
        var (unavailable, _, err) = await RunWithStdinAsync("x", "config", "set", "storageSources.s3.options.secretAccessKey", "--value-stdin", "--protect");
        unavailable.Should().Be(2);
        err.Should().Contain("DPAPI");
    }

    private static ISecretProtector CliServicesProtector() => ReStore.Core.src.cli.CliServices.SecretProtector;

    // ---- unset -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConfigUnset_RemovesAKey_AndIsHarmlessWhenItIsNotThere()
    {
        WriteConfig(root => root["excludeGlobs"] = new JsonArray("**/node_modules/**"));

        var (exitCode, stdout, _) = await RunAsync("config", "unset", "excludeGlobs", "--json");
        exitCode.Should().Be(0);
        Json(stdout)["existed"]!.GetValue<bool>().Should().BeTrue();
        ReadConfig().ContainsKey("excludeGlobs").Should().BeFalse();

        var (again, againOut, _) = await RunAsync("config", "unset", "excludeGlobs", "--json");
        again.Should().Be(0);
        Json(againOut)["existed"]!.GetValue<bool>().Should().BeFalse();
        Json(againOut)["changed"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task ConfigUnset_OfARequiredSection_IsRefused_AndTheFileIsUntouched()
    {
        WriteConfig();
        var before = File.ReadAllBytes(AppPaths.ConfigFile);

        (await RunAsync("config", "unset", "storageSources")).ExitCode.Should().Be(3);
        (await RunAsync("config", "unset", "watchDirectories")).ExitCode.Should().Be(3);

        File.ReadAllBytes(AppPaths.ConfigFile).Should().Equal(before);
    }

    // ---- watch directories -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Watch_AddListRemove_RoundTrip()
    {
        WriteConfig();
        var photos = Directory.CreateDirectory(Path.Combine(Root, "photos")).FullName;

        var (addExit, addOut, _) = await RunAsync("config", "add-watch", photos + Path.DirectorySeparatorChar, "--json");
        addExit.Should().Be(0);
        Json(addOut)["path"]!.GetValue<string>().Should().Be(photos, "the trailing separator is trimmed");

        var (listExit, listOut, _) = await RunAsync("config", "list-watch", "--json");
        listExit.Should().Be(0);
        var items = Json(listOut)["watchDirectories"]!.AsArray();
        items.Select(i => i!["path"]!.GetValue<string>()).Should().Equal(WatchedDirectory, photos);
        items.Should().OnlyContain(i => i!["exists"]!.GetValue<bool>());

        var (plain, plainOut, _) = await RunAsync("config", "list-watch");
        plain.Should().Be(0);
        plainOut.Should().Contain(photos);

        var (removeExit, removeOut, _) = await RunAsync("config", "remove-watch", photos, "--json");
        removeExit.Should().Be(0);
        Json(removeOut)["removed"]!.GetValue<bool>().Should().BeTrue();
        ReadConfig()["watchDirectories"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Watch_Add_RejectsDuplicatesMissingFoldersAndUnknownStorage()
    {
        WriteConfig();

        var (duplicate, _, dupErr) = await RunAsync("config", "add-watch", WatchedDirectory);
        duplicate.Should().Be(2);
        dupErr.Should().Contain("already watched");

        var gone = Path.Combine(Root, "gone");
        var (missing, _, _) = await RunAsync("config", "add-watch", gone);
        missing.Should().Be(2);
        (await RunAsync("config", "add-watch", gone, "--force")).ExitCode.Should().Be(0);
        ReadConfig()["watchDirectories"]!.AsArray().Select(w => w!["path"]!.GetValue<string>()).Should().Contain(gone);

        var other = Directory.CreateDirectory(Path.Combine(Root, "other")).FullName;
        var (badStorage, _, _) = await RunAsync("config", "add-watch", other, "--storage", "s3");
        badStorage.Should().Be(2);
    }

    [Fact]
    public async Task Watch_Add_KeepsEnvironmentStylePaths_AndStorageType()
    {
        WriteConfig(root => root["storageSources"] = new JsonObject
        {
            ["local"] = new JsonObject { ["path"] = StorageDirectory },
            ["backup2"] = new JsonObject { ["path"] = Path.Combine(Root, "second") }
        });
        Environment.SetEnvironmentVariable("CFGTEST_VALUE", Directory.CreateDirectory(Path.Combine(Root, "viaenv")).FullName);

        var (exitCode, stdout, stderr) = await RunAsync("config", "add-watch", "%CFGTEST_VALUE%", "--storage", "backup2", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        var entry = ReadConfig()["watchDirectories"]!.AsArray().Last()!;
        entry["path"]!.GetValue<string>().Should().Be("%CFGTEST_VALUE%");
        entry["storageType"]!.GetValue<string>().Should().Be("backup2");
        Json(stdout)["expandedPath"]!.GetValue<string>().Should().Be(Path.Combine(Root, "viaenv"));
    }

    [Fact]
    public async Task Watch_Remove_UnknownFolder_IsAUsageError_AndTheLastFolderCannotBeRemoved()
    {
        WriteConfig();

        (await RunAsync("config", "remove-watch", Path.Combine(Root, "nowhere"))).ExitCode.Should().Be(2);
        var (last, _, _) = await RunAsync("config", "remove-watch", WatchedDirectory);
        last.Should().Be(3, "a config needs at least one folder");
        ReadConfig()["watchDirectories"]!.AsArray().Should().HaveCount(1);
    }

    // ---- set-storage -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SetStorage_Local_SetsThePath_AndCanBecomeTheDefault()
    {
        WriteConfig();
        var target = Path.Combine(Root, "second-store");

        var (exitCode, stdout, stderr) = await RunAsync("config", "set-storage", "local", "--path", target, "--default", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        var doc = Json(stdout);
        doc["type"]!.GetValue<string>().Should().Be("local");
        doc["path"]!.GetValue<string>().Should().Be(target);
        doc["default"]!.GetValue<bool>().Should().BeTrue();
        doc["nextSteps"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Contain("restore.exe probe --storage local");
        ReadConfig()["storageSources"]!["local"]!["path"]!.GetValue<string>().Should().Be(target);
        Directory.Exists(target).Should().BeTrue("validation creates the local folder, as every command does");
    }

    [Fact]
    public async Task SetStorage_S3_TakesOptions_AndSecretsFromStdin()
    {
        WriteConfig();

        var (exitCode, stdout, stderr) = await RunWithStdinAsync(
            "s3-secret-value\n",
            "config", "set-storage", "s3",
            "--option", "region=eu-west-1", "--option", "bucketName=my-backups", "--option", "accessKeyId=AKIATEST",
            "--option-stdin", "secretAccessKey", "--protect", "--default", "--json");

        exitCode.Should().Be(0, stdout + stderr);
        var s3 = ReadConfig()["storageSources"]!["s3"]!;
        s3["options"]!["bucketName"]!.GetValue<string>().Should().Be("my-backups");
        s3["options"]!["secretAccessKey"]!.GetValue<string>().Should().StartWith("dpapi:");
        s3.AsObject().ContainsKey("path").Should().BeFalse("remote providers have no path");
        ReadConfig()["globalStorageType"]!.GetValue<string>().Should().Be("s3");
        var doc = Json(stdout);
        doc["options"]!["secretAccessKey"]!.GetValue<string>().Should().Be("dpapi:…");
        (stdout + stderr).Should().NotContain("s3-secret-value");
        doc["nextSteps"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Contain("restore.exe probe --storage s3");
    }

    [Fact]
    public async Task SetStorage_MergesIntoAnExistingEntry_KeepingTheOtherOptions()
    {
        WriteConfig(root => root["storageSources"] = new JsonObject
        {
            ["local"] = new JsonObject { ["path"] = StorageDirectory },
            ["s3"] = new JsonObject
            {
                ["options"] = new JsonObject { ["accessKeyId"] = "a", ["secretAccessKey"] = "env:S3_SECRET", ["region"] = "eu-west-1", ["bucketName"] = "old-bucket", ["prefix"] = "laptop" }
            }
        });

        (await RunAsync("config", "set-storage", "s3", "--option", "bucketName=new-bucket")).ExitCode.Should().Be(0);

        var options = ReadConfig()["storageSources"]!["s3"]!["options"]!;
        options["bucketName"]!.GetValue<string>().Should().Be("new-bucket");
        options["secretAccessKey"]!.GetValue<string>().Should().Be("env:S3_SECRET");
        options["prefix"]!.GetValue<string>().Should().Be("laptop");
    }

    [Fact]
    public async Task SetStorage_RejectsUnknownTypesOptionsAndMissingRequirements_WithoutChangingTheFile()
    {
        WriteConfig();
        var before = File.ReadAllBytes(AppPaths.ConfigFile);

        var (unknownType, _, typeErr) = await RunAsync("config", "set-storage", "floppy");
        unknownType.Should().Be(2);
        typeErr.Should().Contain("Known types");

        var (unknownOption, _, optionErr) = await RunAsync("config", "set-storage", "s3", "--option", "bucketname2=x");
        unknownOption.Should().Be(2);
        optionErr.Should().Contain("not an option of the s3 storage");

        var (incomplete, json, _) = await RunAsync("config", "set-storage", "s3", "--option", "region=eu-west-1", "--json");
        incomplete.Should().Be(2);
        Json(json)["error"]!["message"]!.GetValue<string>().Should().Contain("accessKeyId");

        (await RunAsync("config", "set-storage", "s3", "--path", "/x", "--option", "accessKeyId=a", "--option", "secretAccessKey=b", "--option", "region=r", "--option", "bucketName=bucket-1")).ExitCode
            .Should().Be(2, "remote storages have no path");
        (await RunAsync("config", "set-storage", "s3", "--option", "noequals")).ExitCode.Should().Be(2);
        (await RunAsync("config", "set-storage", "local", "--path", Path.Combine(Root, "x"), "--option", "bucketName=x")).ExitCode.Should().Be(2);

        File.ReadAllBytes(AppPaths.ConfigFile).Should().Equal(before);
    }

    [Fact]
    public async Task SetStorage_Dropbox_AcceptsEitherAnAccessTokenOrTheAppCredentials()
    {
        WriteConfig();

        (await RunAsync("config", "set-storage", "dropbox", "--option", "appKey=key-1")).ExitCode.Should().Be(2);
        var (exitCode, stdout, _) = await RunAsync("config", "set-storage", "dropbox", "--option", "appKey=key-1", "--option", "appSecret=secret-1", "--json");

        exitCode.Should().Be(0);
        Json(stdout)["nextSteps"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Contain(s => s.Contains("auth dropbox"));
        var options = ReadConfig()["storageSources"]!["dropbox"]!["options"]!.AsObject();
        options.Select(o => o.Key).Should().BeEquivalentTo(["appKey", "appSecret"], "the example's placeholder values are not carried over");
    }

    [Fact]
    public async Task SetStorage_StartsFromACleanEntry_WhenTheExistingOneIsStillTheShippedExample()
    {
        WritePristineConfig();
        var target = Path.Combine(Root, "store");

        var (exitCode, _, stderr) = await RunAsync("config", "set-storage", "local", "--path", target, "--default");

        exitCode.Should().Be(0, stderr);
        var entry = ReadConfig()["storageSources"]!["local"]!;
        entry["path"]!.GetValue<string>().Should().Be(target);
    }

    // ---- registry --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NewCommands_AreListedInTheCommandReference()
    {
        var (_, stdout, _) = await RunAsync("commands", "--json");
        var names = Json(stdout)["commands"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();

        names.Should().Contain(
        [
            "config path", "config get", "config set", "config unset", "config add-watch", "config remove-watch", "config list-watch",
            "config set-storage", "config schema", "init", "encryption status", "encryption enable", "encryption disable", "unlock", "lock",
            "auth status", "auth gdrive", "auth dropbox"
        ]);
    }
}

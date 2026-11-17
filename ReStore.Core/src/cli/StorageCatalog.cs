using System.Text.Json.Nodes;
using ReStore.Core.src.utils;

namespace ReStore.Core.src.cli;

/// <summary>One storage type the CLI can configure: what it needs and which options it understands.</summary>
/// <param name="RequiredAnyOf">Alternatives of option names that must all be set; one alternative is enough.</param>
/// <param name="Options">Every option the provider reads (besides the universal <c>prefix</c>, which is listed too).</param>
/// <param name="NextStep">What a person or agent must do after the options are set (an OAuth sign-in), if anything.</param>
internal sealed record StorageTypeInfo(string Type, bool Remote, string[][] RequiredAnyOf, string[] Options, string? NextStep = null)
{
    /// <summary>The smallest set of required options still missing (blank or placeholder values count as missing); empty when satisfied.</summary>
    public IReadOnlyList<string> Missing(JsonObject? options)
    {
        if (RequiredAnyOf.Length == 0)
        {
            return [];
        }

        var best = RequiredAnyOf
            .Select(alternative => alternative.Where(name => !IsSet(options, name)).ToList())
            .OrderBy(missing => missing.Count)
            .First();
        return best;
    }

    /// <summary>The requirement for messages: <c>accessToken</c>, or <c>appKey and appSecret</c>.</summary>
    public string RequirementText => string.Join(" or ", RequiredAnyOf.Select(alternative => string.Join(" and ", alternative)));

    private static bool IsSet(JsonObject? options, string name) =>
        options != null
        && options.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value is JsonValue value
        && value.TryGetValue<string>(out var text)
        && !CliSupport.IsPlaceholder(text);
}

/// <summary>The storage types of <c>config set-storage</c> and <c>init</c>, from the provider code and docs/storage/providers.</summary>
internal static class StorageCatalog
{
    private const string Prefix = "prefix";

    public static IReadOnlyList<StorageTypeInfo> All { get; } =
    [
        new("local", false, [], ["path", Prefix]),
        new("gdrive", true, [["client_id", "client_secret"]], ["client_id", "client_secret", "token_folder", "backup_folder_name", Prefix],
            "sign in once with: restore.exe auth gdrive"),
        new("s3", true, [["accessKeyId", "secretAccessKey", "region", "bucketName"]], ["accessKeyId", "secretAccessKey", "region", "bucketName", "serviceUrl", Prefix]),
        new("github", true, [["token", "repo", "owner"]], ["token", "repo", "owner", Prefix]),
        new("azure", true, [["connectionString", "containerName"]], ["connectionString", "containerName", Prefix]),
        new("gcp", true, [["bucketName"]], ["bucketName", "credentialPath", Prefix]),
        new("dropbox", true, [["accessToken"], ["appKey", "appSecret"]], ["accessToken", "refreshToken", "appKey", "appSecret", Prefix],
            "authorise once with: restore.exe auth dropbox (then restore.exe auth dropbox --code <code>)"),
        new("sftp", true, [["host", "username", "password"], ["host", "username", "privateKeyPath"]],
            ["host", "port", "username", "password", "privateKeyPath", "passphrase", "hostKeyFingerprint", Prefix]),
        new("b2", true, [["keyId", "applicationKey", "serviceUrl", "bucketName"]], ["keyId", "applicationKey", "serviceUrl", "bucketName", Prefix])
    ];

    public static string KnownTypes => string.Join(", ", All.Select(t => t.Type));

    public static StorageTypeInfo? Find(string type) =>
        All.FirstOrDefault(t => string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>The canonical spelling of <paramref name="option"/> for <paramref name="info"/>, or null when the provider does not read it.</summary>
    public static string? CanonicalOption(StorageTypeInfo info, string option) =>
        info.Options.FirstOrDefault(o => string.Equals(o, option, StringComparison.OrdinalIgnoreCase));
}

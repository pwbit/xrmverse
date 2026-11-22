using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xrmd.Cli.Config;

/// <summary>The values of <c>auth login --mode</c>.</summary>
public static class AuthModes
{
    public const string Dev = "dev";
    public const string DeviceCode = "device-code";
    public const string Browser = "browser";
    public const string ClientSecret = "client-secret";

    public static readonly string[] All = [Dev, DeviceCode, Browser, ClientSecret];
}

/// <summary>The values of <c>auth login --idp</c> and <see cref="Profile.Idp"/>.</summary>
public static class IdentityProviders
{
    public const string Entra = "entra";
    public const string Local = "local";

    public static readonly string[] All = [Entra, Local];

    /// <summary>The client id of the built-in public client of the local identity provider.</summary>
    public const string LocalCliClientId = "xrmd-cli";
}

/// <summary>
/// One connection profile. Holds only non-secret values: the dev signing key and the client secret are read
/// from environment variables at use time, MSAL tokens live in <c>msal-cache.bin</c>.
/// </summary>
public sealed record Profile
{
    /// <summary>Server base URL, absolute, with a trailing slash.</summary>
    public required string Url { get; init; }

    /// <summary>One of <see cref="AuthModes"/>.</summary>
    public required string Mode { get; init; }

    /// <summary>
    /// The identity provider of the MSAL modes: <c>entra</c> (absent in the file) or <c>local</c>, the server's own
    /// (authority <c>&lt;url&gt;adfs/</c>, client id default <c>xrmd-cli</c>).
    /// </summary>
    public string? Idp { get; init; }

    /// <summary>True for <c>Idp = local</c>.</summary>
    [JsonIgnore]
    public bool IsLocalIdp => Idp == IdentityProviders.Local;

    /// <summary>Entra application (client) id (local: the IdP client, default <c>xrmd-cli</c>); device-code, browser and client-secret.</summary>
    public string? ClientId { get; init; }

    /// <summary>Entra tenant id; required for client-secret, <c>organizations</c> when absent otherwise.</summary>
    public string? TenantId { get; init; }

    /// <summary>dev: the <c>oid</c> claim.</summary>
    public string? ObjectId { get; init; }

    /// <summary>dev: name of the environment variable holding the HS256 signing key.</summary>
    public string? SigningKeyEnv { get; init; }

    /// <summary>device-code/browser: MSAL home account id of the signed-in user (an identifier, not a credential).</summary>
    public string? HomeAccountId { get; init; }

    /// <summary>
    /// PEM root certificate trusted for this profile's host only (H7): the root of Caddy's internal CA, exported by
    /// <c>server up</c> to <c>&lt;dir&gt;/certs/caddy-root.crt</c>. Absolute path; null = the machine's trust store only.
    /// </summary>
    public string? CaFile { get; init; }
}

/// <summary>The content of <c>config.json</c>.</summary>
public sealed class CliConfig
{
    public string? CurrentProfile { get; set; }
    public SortedDictionary<string, Profile> Profiles { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Reads and writes <c>{ConfigDir}/config.json</c>.</summary>
public sealed class ProfileStore(string configDir)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath { get; } = Path.Combine(configDir, "config.json");

    public CliConfig Load()
    {
        if (!File.Exists(FilePath)) return new CliConfig();
        try
        {
            var config = JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(FilePath), Json) ?? new CliConfig();
            config.Profiles = new SortedDictionary<string, Profile>(config.Profiles ?? new(), StringComparer.Ordinal);
            return config;
        }
        catch (JsonException ex)
        {
            throw new CliException(ExitCodes.Unexpected, $"Cannot read {FilePath}: {ex.Message}", ex);
        }
    }

    public void Save(CliConfig config) =>
        ConfigFiles.WriteAllBytes(FilePath, JsonSerializer.SerializeToUtf8Bytes(config, Json));
}

/// <summary>Writes files of the config dir: the dir is 0700 and files 0600 on Unix, replaced atomically.</summary>
public static class ConfigFiles
{
    private const UnixFileMode PrivateDirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureDirectory(string dir)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, PrivateDirMode);
    }

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;
        try
        {
            using (var stream = new FileStream(temp, options)) stream.Write(bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

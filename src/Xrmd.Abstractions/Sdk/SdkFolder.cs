using System.Text.Json;

namespace Xrmd.Abstractions.Sdk;

/// <summary>
/// Where the Microsoft SDK folder is, and its <c>sdk.json</c> record. Names no SDK type, so the host and the CLI
/// can use it before the SDK is loaded.
/// </summary>
public static class SdkFolder
{
    public const string DirectoryVariable = "XRMD_SDK_DIR";
    public const string RecordFileName = "sdk.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// The default SDK folder of the host and the CLI alike: <c>~/.xrmd/sdk</c> (<c>%USERPROFILE%\.xrmd\sdk</c>), independent
    /// of <c>XRMD_CONFIG_DIR</c>.
    /// </summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xrmd", "sdk");

    /// <summary>The explicit path, else <c>XRMD_SDK_DIR</c>, else <c>~/.xrmd/sdk</c>.</summary>
    public static string Resolve(string? explicitPath) =>
        Resolve(explicitPath, Environment.GetEnvironmentVariable, DefaultPath);

    /// <summary>As <see cref="Resolve(string?)"/> with the variable lookup and the default folder given (tests, a custom config dir).</summary>
    public static string Resolve(string? explicitPath, Func<string, string?> getVariable, string defaultFolder)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
        var fromVariable = getVariable(DirectoryVariable);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(fromVariable) ? defaultFolder : fromVariable);
    }

    /// <summary>The record of the SDK folder at <paramref name="path"/>; null when it has none or it is unreadable.</summary>
    public static SdkRecord? TryRead(string path)
    {
        try
        {
            var file = Path.Combine(path, RecordFileName);
            if (!File.Exists(file)) return null;
            var record = JsonSerializer.Deserialize<SdkRecord>(File.ReadAllText(file), Json);
            return record is { Package: not null, Version: not null, Sha512: not null, LicenseUrl: not null, AcceptedBy: not null } ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Write(string path, SdkRecord record) =>
        File.WriteAllText(Path.Combine(path, RecordFileName), JsonSerializer.Serialize(record, Json) + "\n");
}

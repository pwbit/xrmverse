namespace Xrmd.Abstractions.Sdk;

/// <summary>Whether the server loads the Microsoft SDK (<see cref="Standard"/>) or runs without any SDK assembly (<see cref="None"/>).</summary>
public enum SdkMode
{
    /// <summary>The default: the SDK folder is resolved and the SDK-bound features are available.</summary>
    Standard,

    /// <summary>SDK-free: no SDK assembly is loaded; the features that need it are refused.</summary>
    None,
}

/// <summary>The <c>Sdk:Mode</c> setting: its keys, parsing and the names of the assemblies an SDK-free server must never load.</summary>
public static class SdkModes
{
    /// <summary>The configuration key of the setting.</summary>
    public const string ConfigKey = "Sdk:Mode";

    /// <summary>The environment variable form of the setting.</summary>
    public const string EnvironmentVariable = "XRMD_SDK_MODE";

    /// <summary>The names of the SDK assemblies, in the order the host looks for them.</summary>
    public static readonly IReadOnlyList<string> AssemblyNames =
    [
        "Microsoft.Xrm.Sdk",
        "Microsoft.Crm.Sdk.Proxy",
        "Microsoft.PowerPlatform.Dataverse.Client",
        "Microsoft.Xrm.Sdk.Workflow",
    ];

    /// <summary>Parses the setting (case-insensitive); null, empty and whitespace mean <see cref="SdkMode.Standard"/>.</summary>
    public static bool TryParse(string? value, out SdkMode mode)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("standard", StringComparison.OrdinalIgnoreCase))
        {
            mode = SdkMode.Standard;
            return true;
        }

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            mode = SdkMode.None;
            return true;
        }

        mode = SdkMode.Standard;
        return false;
    }

    /// <summary>The setting value of <paramref name="mode"/>: <c>standard</c> or <c>none</c>.</summary>
    public static string ToSetting(SdkMode mode) => mode == SdkMode.None ? "none" : "standard";

    /// <summary>The message for a setting value that <see cref="TryParse"/> rejects.</summary>
    public static string InvalidMessage(string value) => $"{ConfigKey} '{value}' is not valid: use 'standard' or 'none'.";
}

/// <summary>The mode the host runs in (a DI singleton).</summary>
public sealed record SdkModeInfo(SdkMode Mode)
{
    /// <summary>True when no SDK assembly is loaded.</summary>
    public bool IsSdkFree => Mode == SdkMode.None;
}

/// <summary>The refusal texts of an SDK-free server; each is one sentence plus the docs link.</summary>
public static class SdkFreeMessages
{
    /// <summary>The documentation page of SDK-free servers.</summary>
    public const string DocsLink = "https://xrmverse.com/docs/microsoft-sdk#sdk-free-servers";

    /// <summary>The SOAP organization service is refused.</summary>
    public const string Soap =
        "The SOAP organization service is not available on an SDK-free server; use the Web API instead. See " + DocsLink;

    /// <summary>Registering a plug-in assembly is refused.</summary>
    public const string PluginAssembly =
        "Plug-in assemblies built on the Microsoft SDK cannot be registered on an SDK-free server; use webhooks or service endpoints instead. See " + DocsLink;

    /// <summary>Migration from Dataverse is refused.</summary>
    public const string Migration =
        "Migration from Dataverse is not available on an SDK-free server. See " + DocsLink;

    /// <summary>The text of a setting or status that does not apply to an SDK-free server.</summary>
    public const string NotUsed = "Not used (SDK-free server).";
}

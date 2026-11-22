namespace Xrmd.Abstractions.Sdk;

/// <summary>
/// The pinned Microsoft SDK package that customers install with <c>xrmd sdk install</c> (Plan 12).
/// Constants only: this type names no SDK type, so it is safe to use before the SDK is loaded.
/// </summary>
public static class SdkPackage
{
    public const string Id = "Microsoft.PowerPlatform.Dataverse.Client";

    public const string Version = "1.2.27";

    /// <summary>SHA-512 of the .nupkg, base64, as nuget.org publishes it.</summary>
    public const string Sha512Base64 = "20F8F6UyuLk+awBMheqKkcDFfoZmT0tBijhJHm39zW2lq2LM9PKs7/xk/SxKB0BjZi96p+y1jD4aGWqlAcJ/XA==";

    public const string LicenseUrl = "https://go.microsoft.com/fwlink/?linkid=2108407";

    public const string DownloadUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.powerplatform.dataverse.client/1.2.27/microsoft.powerplatform.dataverse.client.1.2.27.nupkg";

    /// <summary>
    /// The file version of the package's <c>Microsoft.Xrm.Sdk.dll</c> (its assembly version is 9.0.0.0 in every 9.x
    /// release, so the file version is what tells this package apart). The host refuses an SDK folder with another one.
    /// </summary>
    public const string XrmSdkFileVersion = "9.2.49.17970";

    /// <summary>The assemblies taken from <c>lib/net10.0/</c> of the package.</summary>
    public static IReadOnlyList<string> Assemblies { get; } = new[]
    {
        "Microsoft.Xrm.Sdk.dll",
        "Microsoft.Crm.Sdk.Proxy.dll",
        "Microsoft.PowerPlatform.Dataverse.Client.dll",
    };

    /// <summary>
    /// The pinned Microsoft workflow SDK (plan 2.14, ruling W18, issue #45): <c>Microsoft.Xrm.Sdk.Workflow.dll</c>, which custom
    /// workflow activities are built against. Optional: <c>xrmd sdk install</c> installs it next to the Dataverse SDK after the
    /// same acceptance, and the host runs without it (custom activity steps are then unsupported steps). Constants only, as
    /// <see cref="SdkPackage"/>: no type of it is named anywhere under <c>src/</c>.
    /// </summary>
    public static class Workflow
    {
        public const string Id = "Microsoft.CrmSdk.Workflow";

        public const string Version = "9.0.2.60";

        /// <summary>SHA-512 of the .nupkg, base64, as nuget.org publishes it (checked on the downloaded bytes, 2026-11-12).</summary>
        public const string Sha512Base64 = "RHB5/GGSncAjfjIkrw7krJtPdILjH5NejRzN3/5DFVVH/PpnfxWvevb+J5+PEbAZaspKUZs3YoIQTt58XQxchQ==";

        /// <summary>The same license terms as the Dataverse SDK package: one acceptance covers both.</summary>
        public const string LicenseUrl = SdkPackage.LicenseUrl;

        public const string DownloadUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.crmsdk.workflow/9.0.2.60/microsoft.crmsdk.workflow.9.0.2.60.nupkg";

        /// <summary>The only entry taken from the package.</summary>
        public const string Entry = "lib/net462/Microsoft.Xrm.Sdk.Workflow.dll";

        /// <summary>The file name of the assembly in the SDK folder.</summary>
        public const string AssemblyFileName = "Microsoft.Xrm.Sdk.Workflow.dll";

        /// <summary>The simple name of the assembly.</summary>
        public const string AssemblyName = "Microsoft.Xrm.Sdk.Workflow";

        /// <summary>
        /// The file version of the package's <c>Microsoft.Xrm.Sdk.Workflow.dll</c> (its assembly version is 9.0.0.0). The host
        /// loads another one all the same, and the doctor warns (<c>sdk-workflow</c>).
        /// </summary>
        public const string FileVersion = "9.2.49.18751";
    }

    public static string MissingMessage(string lookedIn) =>
        $"The Microsoft SDK is not installed (looked in {lookedIn}). Run 'xrmd sdk install' to download it from nuget.org after accepting Microsoft's license terms.";
}

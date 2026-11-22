namespace Xrmd.Abstractions.Sdk;

/// <summary>
/// The contents of <c>sdk.json</c>: which package was installed and who accepted Microsoft's license terms. The three workflow
/// fields name the workflow SDK package (<see cref="SdkPackage.Workflow"/>) when it was installed with it (plan 2.14, W18);
/// they are optional, so a record written before stays valid.
/// </summary>
public sealed record SdkRecord(
    string Package,
    string Version,
    string Sha512,
    string LicenseUrl,
    DateTimeOffset AcceptedAt,
    string AcceptedBy,
    string? WorkflowPackage = null,
    string? WorkflowVersion = null,
    string? WorkflowSha512 = null);

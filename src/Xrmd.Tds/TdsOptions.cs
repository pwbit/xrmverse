namespace Xrmd.Tds;

/// <summary>
/// The SQL (TDS) endpoint of the organization (plan 3.3), section <c>Tds</c>. Off while <see cref="Port"/> is not set.
/// </summary>
public sealed class TdsOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "Tds";

    /// <summary>The TCP port of the endpoint (ruling T2); 0 picks a free port (tests). Not set: no endpoint.</summary>
    public int? Port { get; set; }

    /// <summary>The PEM certificate presented to clients (ruling T3). Required with <see cref="PrivateKeyPath"/>, except in Development.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>The PEM private key of <see cref="CertificatePath"/>.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>The most rows one query may return (ruling T8).</summary>
    public int MaxRows { get; set; } = 50000;

    /// <summary>
    /// The rows read at once from the query engine when a query is paged (no <c>TOP</c>, or one above 5000): at most 5000, the
    /// engine's limit. Lower values only change how many pages a query takes (tests use them to span pages with few rows).
    /// </summary>
    public int PageSize { get; set; } = 5000;

    /// <summary>The time limit of one batch (ruling T8).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}

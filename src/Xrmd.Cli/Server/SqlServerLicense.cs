namespace Xrmd.Cli.Server;

/// <summary>
/// Issue #16, option 1: the bundled SQL Server runs only after the user has accepted its Microsoft license terms. xrmd
/// shows where the terms of the chosen edition are and never accepts them for the user.
/// </summary>
public static class SqlServerLicense
{
    public const string FlagName = "--accept-sql-server-eula";

    /// <summary>Microsoft Learn: what <c>ACCEPT_EULA=Y</c> and an edition in <c>MSSQL_PID</c> express for a container.</summary>
    public const string ContainerTermsUrl = "https://learn.microsoft.com/sql/linux/containers/deploy#run-production-container-images";

    /// <summary>The free Express production license, as linked by Microsoft Learn.</summary>
    public const string ExpressTermsUrl = "https://go.microsoft.com/fwlink/?linkid=857693";

    /// <summary>Microsoft's SQL Server downloads page, which links the license terms of each free edition (Developer).</summary>
    public const string DownloadsUrl = "https://www.microsoft.com/sql-server/sql-server-downloads";

    /// <summary>Microsoft Volume Licensing: the terms of a paid edition (Standard, Enterprise, EnterpriseCore, a product key).</summary>
    public const string VolumeLicensingUrl = "https://www.microsoft.com/licensing";

    /// <summary>Where the license terms of <paramref name="edition"/> (an <c>MSSQL_PID</c> value) are.</summary>
    public static string TermsUrl(string edition) => edition.ToLowerInvariant() switch
    {
        "express" => ExpressTermsUrl,
        "developer" => DownloadsUrl,
        _ => VolumeLicensingUrl,
    };

    /// <summary>The edition as shown to the user: a product key is never printed.</summary>
    public static string Describe(string edition) =>
        Installation.SqlEditions.Contains(edition, StringComparer.OrdinalIgnoreCase) ? $"the {edition} edition" : "the edition of your product key";

    /// <summary>The text shown before the question, and in every refusal.</summary>
    public static string Notice(string edition) =>
        $"The bundled SQL Server ({Describe(edition)}, MSSQL_PID) is Microsoft software under Microsoft's license terms: {TermsUrl(edition)}. " +
        $"Accepting them writes ACCEPT_EULA=Y to .env, which states that you accept them and hold a valid license for this edition ({ContainerTermsUrl}).";
}

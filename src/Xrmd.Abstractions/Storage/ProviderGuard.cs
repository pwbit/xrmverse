using Xrmd.Core.Errors;

namespace Xrmd.Abstractions.Storage;

/// <summary>
/// The explicit refusals of what a PostgreSQL organization does not do as a SQL Server one (plan 2.7a, ruling P10): HTTP 501 /
/// <see cref="ErrorCodes.Unexpected"/> with <c>'&lt;feature&gt;' is not available on PostgreSQL organizations yet.</c> instead of
/// diverging silently. Since plan 2.7b every feature of P10 runs on PostgreSQL; what remains is the refusal of <c>like</c>
/// patterns with character ranges (ruling P15, raised by the PostgreSQL dialect) and the text of
/// <c>xrmd org restore --direct</c> without <c>pg_restore</c> (ruling Q7). Nothing changes on SQL Server.
/// </summary>
public static class ProviderGuard
{
    /// <summary>
    /// Plan 2.7b, ruling Q7: <c>xrmd org restore --direct</c> runs <c>pg_restore</c> on the user's machine for a PostgreSQL server,
    /// and stops with this text when no <c>pg_restore</c> of a supported major is on its <c>PATH</c>, or when the one there cannot
    /// read the backup because <c>pg_dump</c> of a newer major made it (Task 9 fix round 1: the server image's <c>pg_dump</c> 18
    /// writes archive format 1.16, which <c>pg_restore</c> 16 and 17 refuse).
    /// </summary>
    public const string DirectRestoreNeedsPgRestore =
        "'xrmd org restore --direct' on PostgreSQL needs pg_restore on PATH, of the PostgreSQL version that made the backup or later: " +
        "18 or later for the backups of the xrmd server image (at least 16).";

    /// <summary>The HTTP status of the refusal: 501 Not Implemented.</summary>
    public const int HttpStatus = 501;

    /// <summary>The text of the refusal of <paramref name="feature"/>.</summary>
    public static string Message(string feature) => $"'{feature}' is not available on PostgreSQL organizations yet.";

    /// <summary>The refusal of <paramref name="feature"/> (501, <see cref="ErrorCodes.Unexpected"/>, <see cref="Message"/>).</summary>
    public static XrmdFaultException Unavailable(string feature) => new(ErrorCodes.Unexpected, Message(feature), HttpStatus);
}

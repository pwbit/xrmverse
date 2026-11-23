using System.Text.RegularExpressions;
using Xrmd.Abstractions;

namespace Xrmd.Metadata;

/// <summary>
/// Logical names become SQL identifiers (<c>data.[name]</c>, column names): only <c>^[a-z][a-z0-9_]{0,63}$</c> is allowed.
/// </summary>
public static partial class LogicalNameValidator
{
    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);

    /// <exception cref="XrmdValidationException">The name is not a valid logical name.</exception>
    public static void EnsureValid(string? name, string kind)
    {
        if (!IsValid(name))
            throw new XrmdValidationException(
                $"Invalid {kind} logical name '{name}': it must match ^[a-z][a-z0-9_]{{0,63}}$ " +
                "(lowercase letter first, then lowercase letters, digits or '_', at most 64 characters).");
    }

    /// <summary>
    /// Entity set names the Web API's fixed routes would shadow (routing ignores case): <c>EntityDefinitions</c>,
    /// <c>GlobalOptionSetDefinitions</c>, <c>RelationshipDefinitions</c>, <c>WhoAmI</c>, <c>RetrieveCurrentOrganization</c>, and
    /// the solution actions and function (<c>ImportSolution</c>, <c>ImportSolutionAsync</c>, <c>ExportSolution</c>,
    /// <c>AddSolutionComponent</c>, <c>RemoveSolutionComponent</c>, <c>PublishXml</c>, <c>PublishAllXml</c>,
    /// <c>RetrieveFormattedImportJobResults</c>, the refused patch actions <c>CloneAsPatch</c> and <c>CloneAsSolution</c>,
    /// <c>UninstallSolutionAsync</c>, <c>StageAndUpgrade</c>, <c>StageAndUpgradeAsync</c>, <c>DeleteAndPromote</c>,
    /// <c>DeleteAndPromoteAsync</c>), the file messages and the unbound audit operations
    /// (<c>RetrieveRecordChangeHistory</c>, <c>RetrieveAttributeChangeHistory</c>, <c>DeleteRecordChangeHistory</c>,
    /// <c>DeleteAuditData</c>), the currency function <c>RetrieveExchangeRate</c> (plan 2.10), the elastic function
    /// <c>ExecuteCosmosSqlQuery</c> (plan #78), the rollup function <c>CalculateRollupField</c> (plan 2.12b), the option value
    /// actions <c>InsertOptionValue</c> and <c>UpdateOptionValue</c> and the refused activity and note
    /// messages (plan 2.11, <see cref="ActivitySchema.RefusedMessages"/>) and the queue messages (plan 4.1a, <see cref="QueueSchema.Messages"/>).
    /// </summary>
    public static IReadOnlySet<string> ReservedEntitySetNames { get; } = new HashSet<string>(
    [
        "EntityDefinitions", "GlobalOptionSetDefinitions", "RelationshipDefinitions", "WhoAmI", "RetrieveCurrentOrganization",
        "ImportSolution", "ImportSolutionAsync", "ExportSolution", "AddSolutionComponent", "RemoveSolutionComponent",
        "PublishXml", "PublishAllXml", "RetrieveFormattedImportJobResults",
        "InitializeFileBlocksUpload", "UploadBlock", "CommitFileBlocksUpload", "InitializeFileBlocksDownload", "DownloadBlock", "DeleteFile",
        "RetrieveRecordChangeHistory", "RetrieveAttributeChangeHistory", "DeleteRecordChangeHistory", "DeleteAuditData",
        "SetAutoNumberSeed", "GetAutoNumberSeed", "GetNextAutoNumberValue", "CloneAsPatch", "CloneAsSolution",
        "UninstallSolutionAsync", "StageAndUpgrade", "StageAndUpgradeAsync", "DeleteAndPromote", "DeleteAndPromoteAsync",
        "UnpublishDuplicateRule", "RetrieveDuplicates", "BulkDetectDuplicates",
        "RetrieveSharedPrincipalsAndAccess", "GrantAccess", "ModifyAccess", "RevokeAccess",
        "RetrieveExchangeRate", "ExecuteCosmosSqlQuery",
        // Plan 2.12b (R10): the rollup function.
        "CalculateRollupField",
        // Choice colours: the option value actions.
        "InsertOptionValue", "UpdateOptionValue",
        // Plan 2.11 (A18): the refused activity and note messages, which the Web API answers as unbound operations.
        .. ActivitySchema.RefusedMessages,
        // Plan 4.1b, task 7: the tracking token action, no longer refused.
        ActivitySchema.GetTrackingTokenEmailMessage,
        // Plan 4.1a: the queue actions (RouteTo is unbound).
        .. QueueSchema.Messages,
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>True for a valid logical name that is not one of <see cref="ReservedEntitySetNames"/> (ignoring case).</summary>
    public static bool IsValidEntitySetName(string? name) => IsValid(name) && !ReservedEntitySetNames.Contains(name!);

    /// <summary>The message of an invalid EntitySetName (see <see cref="IsValidEntitySetName"/>).</summary>
    public static string InvalidEntitySetNameMessage(string? name) =>
        $"Invalid EntitySetName '{name}': it must match ^[a-z][a-z0-9_]{{0,63}}$ and not be a reserved name.";

    // \z instead of $ so that a trailing newline is not accepted.
    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

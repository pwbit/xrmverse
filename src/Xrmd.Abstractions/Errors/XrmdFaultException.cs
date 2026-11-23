using System.Security.Cryptography;
using System.Text;
using Microsoft.Crm.Sdk.Messages;

namespace Xrmd.Core.Errors;

/// <summary>Dataverse error codes (OrganizationServiceFault.ErrorCode) used by xrmd.</summary>
public static class ErrorCodes
{
    public const int ObjectDoesNotExist = unchecked((int)0x80040217);
    public const int InvalidArgument = unchecked((int)0x80040203);
    public const int AttributeNotFound = unchecked((int)0x80040203);
    public const int DuplicateRecord = unchecked((int)0x80040237);

    /// <summary>A link entity on a query of (or to) an elastic table (400).</summary>
    public const int ElasticLinkEntityNotSupported = unchecked((int)0x80048d0b);

    /// <summary>The URL names no known resource (e.g. an unknown entity set): "Resource not found for the segment".</summary>
    public const int ResourceNotFound = unchecked((int)0x80060888);

    /// <summary>
    /// A Web API call of a custom API function that declares no response property (Learn: such a function is left out of
    /// <c>$metadata</c> and answers 404 with this code; plan 15, C8).
    /// </summary>
    public const int FunctionWithoutResponseNotFound = unchecked((int)0x8006088a);

    /// <summary>Unexpected server error (the Web API answers 500 without details).</summary>
    public const int Unexpected = unchecked((int)0x80040216);

    /// <summary>A SQL error such as a deadlock victim (<c>SqlException.Number</c> 1205); Learn: "Generic SQL error", retryable.</summary>
    public const int SqlError = unchecked((int)0x80044150);

    /// <summary>The database did not answer within the command timeout (<c>SqlException.Number</c> -2); Learn: "Sql timeout".</summary>
    public const int SqlTimeout = unchecked((int)0x80044151);

    /// <summary>An aggregate query would read more than 50,000 records.</summary>
    public const int AggregateQueryRecordLimitExceeded = unchecked((int)0x8004E023);

    /// <summary>The caller is authenticated but has no <c>systemuser</c> row in the organization.</summary>
    public const int NotMemberOfOrganization = unchecked((int)0x80072560);

    /// <summary>A plug-in aborted the operation with an <c>InvalidPluginExecutionException</c> that carries no error code (ISV aborted).</summary>
    public const int ISVAborted = unchecked((int)0x80040265);

    /// <summary>A plug-in failed with an unexpected exception, or timed out in the sandbox.</summary>
    public const int UnexpectedPluginException = unchecked((int)0x80040224);

    /// <summary>A plug-in call chain went deeper than the maximum depth (8): "...included an infinite loop".</summary>
    public const int InfiniteLoopDetected = unchecked((int)0x80044182);

    /// <summary>The principal lacks a privilege (403).</summary>
    public const int PrivilegeDenied = unchecked((int)0x80040220);

    /// <summary>The principal has no access right on a record (Learn: <c>unManagedIdsAccessDenied</c>; 403).</summary>
    public const int AccessDenied = unchecked((int)0x80048306);

    /// <summary>The target user or team does not hold the required privileges (403).</summary>
    public const int PrincipalPrivilegeDenied = unchecked((int)0x80040231);

    /// <summary>The user has no role, neither direct nor from a team (403).</summary>
    public const int UserNotAssignedRoles = unchecked((int)0x80042F09);

    /// <summary>The caller lacks <c>prvActOnBehalfOfAnotherUser</c> to impersonate (403).</summary>
    public const int CannotActOnBehalfOfAnotherUser = unchecked((int)0x8004A110);

    /// <summary>The caller lacks, at the required depth, a privilege of the role it grants (403).</summary>
    public const int RoleUpdateValidationFailure = unchecked((int)0x80048D3C);

    /// <summary>A privilege id that is not in the catalog (400).</summary>
    public const int InvalidPrivilege = unchecked((int)0x80090903);

    /// <summary>The user is disabled (403; value not verified on Learn).</summary>
    public const int UserDisabled = unchecked((int)0x80040225);

    /// <summary>
    /// ImportFileTooLargeToUpload, "The import file is too large to upload." (Learn, Web service error codes): a solution
    /// file over the import size limit.
    /// </summary>
    public const int ImportFileTooLargeToUpload = unchecked((int)0x80040375);

    /// <summary>
    /// <c>unManagedidsattachmentinvalidfilesize</c>, "Attachment file size is too big." (Learn, file column data): a file over the
    /// column's <c>MaxSizeInKB</c>.
    /// </summary>
    public const int AttachmentInvalidFileSize = unchecked((int)0x80044a02);

    /// <summary>
    /// <c>ProcessImageFailure</c> (Learn, image column data): an image value that cannot be processed (too big for the column's
    /// <c>MaxSizeInKB</c>, not an image, or of a type an image column does not store).
    /// </summary>
    public const int ProcessImageFailure = unchecked((int)0x80072553);

    /// <summary>
    /// <c>CannotUploadNonPrimaryImageAttributeOnCreate</c> (Learn, image column data): a Create that sets an image column other than
    /// the table's primary image.
    /// </summary>
    public const int CannotUploadNonPrimaryImageAttributeOnCreate = unchecked((int)0x80090487);

    // ---- field security (Plan 14; values from Learn, Web service error codes) -------------------------------------------

    /// <summary>The caller may not set a secured column on create (403).</summary>
    public const int FieldCreateDenied = unchecked((int)0x8004F502);

    /// <summary>The caller may not update a secured column (403).</summary>
    public const int FieldUpdateDenied = unchecked((int)0x8004F507);

    /// <summary>The caller may not read a secured column, also used in a filter, order, aggregate or link (403).</summary>
    public const int FieldReadDenied = unchecked((int)0x8004F504);

    /// <summary>The column cannot be secured (400).</summary>
    public const int FieldNotSecurable = unchecked((int)0x8004F501);

    /// <summary>The System Administrator field security profile cannot be modified (403).</summary>
    public const int AdminProfileReadOnly = unchecked((int)0x8004F50C);

    /// <summary>A field permission or field share names a column that is not secured (400).</summary>
    public const int PermissionOnUnsecuredColumn = unchecked((int)0x8004F50E);

    /// <summary>A field share grants neither read nor update (400).</summary>
    public const int ShareWithoutAccess = unchecked((int)0x8004F509);

    /// <summary>An update of a field share sets both read and update to false (400).</summary>
    public const int ShareUpdateWithoutAccess = unchecked((int)0x8004F50A);

    /// <summary>The column of the record is already shared with the principal (400).</summary>
    public const int ShareDuplicate = unchecked((int)0x8004F50B);

    /// <summary>The caller shares a column it cannot update on the record (403).</summary>
    public const int ShareWithoutUpdate = unchecked((int)0x8004F503);

    /// <summary>
    /// The caller turns field security off on a column without read, create and update on it (403). The value is from
    /// memory, not verified against Dataverse (field security checklist).
    /// </summary>
    public const int UnsecureWithoutPermissions = unchecked((int)0x8004F508);

    /// <summary>
    /// A Create or Update that asked for duplicate detection (<c>SuppressDuplicateDetection = false</c>) found a duplicate under a
    /// published rule (Learn, plan 2.15 ruling D12): <c>-2147220685</c>.
    /// </summary>
    public const int DuplicateRecordsFound = unchecked((int)0x80040333);
    // Currencies (plan 2.10; the codes are Learn's "Web service error codes", the texts our own).

    /// <summary>The record's currency does not exist or is inactive (400).</summary>
    public const int InvalidCurrency = unchecked((int)0x80048cfc);

    /// <summary>The currency of a record cannot be cleared while one of its money columns holds a value (400).</summary>
    public const int CurrencyCannotBeNullDueToNonNullMoneyFields = unchecked((int)0x80048cfb);

    /// <summary>The exchange rate is missing or outside the accepted range (400).</summary>
    public const int InvalidExchangeRate = unchecked((int)0x80048cfd);

    /// <summary>The exchange rate of the base currency is always 1 (400).</summary>
    public const int ExchangeRateOfBaseCurrencyNotUpdatable = unchecked((int)0x80048cf5);

    /// <summary>The base currency cannot be deactivated (400).</summary>
    public const int BaseCurrencyCannotBeDeactivated = unchecked((int)0x80048cf4);

    /// <summary>The base currency cannot be deleted (400).</summary>
    public const int BaseCurrencyNotDeletable = unchecked((int)0x80048cff);

    /// <summary>A value converted to the base currency is above the money range (400).</summary>
    public const int BaseCurrencyOverflow = unchecked((int)0x80048cec);

    /// <summary>A value converted to the base currency is below the money range (400).</summary>
    public const int BaseCurrencyUnderflow = unchecked((int)0x80048ceb);

    /// <summary>Another currency already has the ISO currency code (400).</summary>
    public const int DuplicateIsoCurrencyCode = unchecked((int)0x80048cf3);

    /// <summary>The ISO currency code is not a valid code (400).</summary>
    public const int InvalidIsoCurrencyCode = unchecked((int)0x80048cf2);

    /// <summary>The <c>_base</c> column of a money column is computed by the server and cannot be deleted on its own (400).</summary>
    public const int CannotDeleteBaseMoneyCalculationAttribute = unchecked((int)0x80048cfe);

    // ---- rollup columns (plan 2.12b; codes and names from Learn, Web service error codes; texts our own) -------------------

    /// <summary>A rollup definition has neither a related table nor the table's hierarchy (400).</summary>
    public const int RollupFieldsTargetRelationshipNull = unchecked((int)0x80060533);

    /// <summary>The related relationship of a rollup is not a one-to-many relationship from the rollup's table with the named lookup (400).</summary>
    public const int RollupFieldsTargetRelationshipNotPartOfOneToNRelationship = unchecked((int)0x80060534);

    /// <summary>A hierarchy rollup names a relationship that is not the table's hierarchical relationship (400).</summary>
    public const int RollupFieldsSourceEntityNotHierarchical = unchecked((int)0x80060535);

    /// <summary>A condition of a rollup's filter on its own rows is not valid (400).</summary>
    public const int RollupFieldsSourceFilterConditionInvalid = unchecked((int)0x80060538);

    /// <summary>A condition of a rollup's filter on the related rows is not valid (400).</summary>
    public const int RollupFieldsTargetFilterConditionInvalid = unchecked((int)0x80060539);

    /// <summary>The aggregated column's type does not suit the aggregate function (400).</summary>
    public const int RollupFieldsAggregateFunctionTypeMismatch = unchecked((int)0x8006053a);

    /// <summary>The aggregated column is a rollup or a calculated column that is not basic (400).</summary>
    public const int RollupFieldsAggregateOnRollupFieldOrComplexCalcFieldNotAllowed = unchecked((int)0x8006053c);

    /// <summary>The aggregated column's type does not suit the rollup column's type (400).</summary>
    public const int RollupFieldsAggregateFieldDataTypeNotAllowedSimilarRollupFieldDataType = unchecked((int)0x8006053d);

    /// <summary>The type of a rollup column is not one a rollup can have (400).</summary>
    public const int RollupFieldsDataTypeNotValid = unchecked((int)0x8006053e);

    /// <summary>A column, lookup or relationship a rollup column reads cannot be deleted (400).</summary>
    public const int RollupFieldDependentFieldCannotDeleted = unchecked((int)0x80060541);

    /// <summary>The organization has the most rollup columns it can have (400).</summary>
    public const int ExceededRollupFieldsPerOrgQuota = unchecked((int)0x80060542);

    /// <summary>The table has the most rollup columns it can have (400).</summary>
    public const int ExceededRollupFieldsPerEntityQuota = unchecked((int)0x80060543);

    /// <summary>A date rollup and the date it aggregates differ in behavior or format (400).</summary>
    public const int RollupFieldAndAggregateFieldDataTypeFormatMismatch = unchecked((int)0x80060544);

    /// <summary>The aggregate function does not suit the rollup column's type (400).</summary>
    public const int RollupFieldAggregateFunctionNotAllowedForRollupFieldDataType = unchecked((int)0x80060545);

    /// <summary>An online rollup calculation goes deeper in the hierarchy than allowed (400).</summary>
    public const int HierarchyCalculateLimitReached = unchecked((int)0x80060547);

    /// <summary>A rollup's filter on its own rows uses a rollup or a calculated column that is not basic (400).</summary>
    public const int RollupFieldSourceFilterFieldNotAllowed = unchecked((int)0x80060548);

    /// <summary>A rollup's filter on the related rows uses a rollup or a calculated column that is not basic (400).</summary>
    public const int RollupFieldTargetFilterFieldNotAllowed = unchecked((int)0x80060549);

    /// <summary>A rollup's related relationship is self-referential (400).</summary>
    public const int RollupFieldsTargetSameAsSourceEntity = unchecked((int)0x80060551);

    /// <summary>A rollup's related table is one rollups cannot aggregate (activitypointer, activityparty; 400).</summary>
    public const int RollupFieldsTargetEntityNotValid = unchecked((int)0x80060552);

    /// <summary>CalculateRollupField names a column that is not a rollup (400).</summary>
    public const int RecalculateNotSupportedOnNonRollupField = unchecked((int)0x80060554);

    /// <summary>A companion of a rollup column (<c>_date</c>, <c>_state</c>) cannot be updated or deleted on its own (400).</summary>
    public const int CannotModifyRollupDependentField = unchecked((int)0x80060555);

    /// <summary>A companion of a new rollup column would take the name of an existing column (400).</summary>
    public const int RollupDependentFieldNameAlreadyExists = unchecked((int)0x80060556);

    /// <summary>A workflow wait condition names a rollup, a rollup companion or a calculated column (400).</summary>
    public const int RollupOrCalcNotAllowedInWorkflowWaitCondition = unchecked((int)0x80060557);

    /// <summary>An online rollup calculation would read more related rows than allowed (400).</summary>
    public const int RollupAggregateQueryRecordLimitExceeded = unchecked((int)0x8004E025);

    /// <summary>The caller of CalculateRollupField has no write access on the row (403).</summary>
    public const int RollupFieldNoWriteAccess = unchecked((int)0x8004E027);

    /// <summary>The recurrence of a rollup calculation job is shorter than an hour (400).</summary>
    public const int InvalidRecurrenceIntervalForRollupJobs = unchecked((int)0x8004D2A2);

    /// <summary>The behavior of a date column a rollup reads cannot change (400).</summary>
    public const int AttributeBehaviorCanNotBeUpdatedForRollupField = unchecked((int)0x80097214);
}

/// <summary>
/// A Dataverse-style fault: <see cref="ErrorCode"/> is the SDK error code, <see cref="HttpStatus"/> the status the
/// Web API answers with.
/// </summary>
/// <remarks>
/// Messages policy (legal guardrails §2, issue #10): error codes and statuses always match Dataverse; message text is our
/// own wording, except where client code is known to match on Dataverse's text. Those few messages keep the exact text and
/// say why in an "Exact Dataverse text" comment. Any new verbatim message needs the same comment.
/// </remarks>
public sealed class XrmdFaultException(int errorCode, string message, int httpStatus) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
    public int HttpStatus { get; } = httpStatus;

    /// <summary>
    /// Extra details of the fault, by name (the <c>ErrorDetails</c> of an <c>OrganizationServiceFault</c>); empty for most faults.
    /// </summary>
    public IDictionary<string, object?> ErrorDetails { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public static XrmdFaultException InvalidArgument(string message) => new(ErrorCodes.InvalidArgument, message, 400);

    /// <summary>
    /// 400 <see cref="ErrorCodes.InvalidArgument"/>: the request of custom API <paramref name="api"/> does not match its
    /// declared parameters (plan 15, C6): <paramref name="problem"/> says which and how.
    /// </summary>
    public static XrmdFaultException CustomApiInvalidArgument(string api, string problem) =>
        InvalidArgument($"Request to custom API '{api}' is invalid: {problem}");

    /// <summary>
    /// 500 <see cref="ErrorCodes.UnexpectedPluginException"/>: a plug-in of a call to custom API <paramref name="api"/> (its main
    /// operation or a registered step) left the response property <paramref name="property"/> with a value of another type than
    /// the one it declares (plan 15, C7).
    /// </summary>
    public static XrmdFaultException CustomApiResponseTypeMismatch(string api, string property, string expectedType, string actualType) =>
        new(ErrorCodes.UnexpectedPluginException,
            $"Custom API '{api}' declares the response property '{property}' as {expectedType}, but a plug-in of the call set it to {actualType}.",
            500);

    /// <summary>
    /// 404 <see cref="ErrorCodes.FunctionWithoutResponseNotFound"/>: the Web API cannot call the custom API function
    /// <paramref name="api"/>, because it declares no response property (plan 15, C8).
    /// </summary>
    public static XrmdFaultException FunctionWithoutResponseNotFound(string api) =>
        new(ErrorCodes.FunctionWithoutResponseNotFound,
            $"The custom API function '{api}' declares no response property, so the Web API does not expose it: call it through the organization service.",
            404);

    /// <summary>400 <see cref="ErrorCodes.AttachmentInvalidFileSize"/>: the file is over the column's maximum size.</summary>
    public static XrmdFaultException AttachmentFileSizeTooBig() =>
        new(ErrorCodes.AttachmentInvalidFileSize, "The file is larger than the maximum size of the column.", 400);

    /// <summary>
    /// 400 <see cref="ErrorCodes.AttachmentInvalidFileSize"/>: a note body (<c>annotation.documentbody</c>) over the organization's
    /// <c>maxuploadfilesize</c> (plan 2.11, ruling A16). Our own text: Dataverse's for this case is not published.
    /// </summary>
    public static XrmdFaultException NoteBodyTooBig(long size, long maxBytes) =>
        new(ErrorCodes.AttachmentInvalidFileSize,
            $"The note attachment is {size} bytes long; the organization's maximum upload file size (maxuploadfilesize) is {maxBytes} bytes.", 400);

    /// <summary>
    /// 400 <see cref="ErrorCodes.AttachmentInvalidFileSize"/>: an email attachment's body (<c>activitymimeattachment.body</c>) over the
    /// organization's <c>maxuploadfilesize</c> (plan 4.1b). Our own text.
    /// </summary>
    public static XrmdFaultException AttachmentBodyTooBig(long size, long maxBytes) =>
        new(ErrorCodes.AttachmentInvalidFileSize,
            $"The email attachment is {size} bytes long; the organization's maximum upload file size (maxuploadfilesize) is {maxBytes} bytes.", 400);

    /// <summary>
    /// 400 <see cref="ErrorCodes.ProcessImageFailure"/> for an image over <c>MaxSizeInKB</c>. The sizes are whole MB (truncated).
    /// </summary>
    public static XrmdFaultException ImageTooBig(long maxBytes, long uploadedBytes) =>
        new(ErrorCodes.ProcessImageFailure,
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"The image is too large for the column: {uploadedBytes / 1048576} MB uploaded, the maximum is {maxBytes / 1048576} MB."),
            400);

    /// <summary>
    /// 400 <see cref="ErrorCodes.ProcessImageFailure"/> for an image value that cannot be made a thumbnail, with the reason
    /// appended.
    /// </summary>
    public static XrmdFaultException ImageNotProcessed(Guid recordId, string entity, string attribute, string reason) =>
        new(ErrorCodes.ProcessImageFailure,
            $"The image of column {attribute} of {entity} record {recordId} could not be processed. {reason}",
            400);

    /// <summary>400 <see cref="ErrorCodes.CannotUploadNonPrimaryImageAttributeOnCreate"/>: only the primary image goes in a create.</summary>
    public static XrmdFaultException NonPrimaryImageOnCreate(string attribute, string entity) =>
        new(ErrorCodes.CannotUploadNonPrimaryImageAttributeOnCreate,
            $"Only the primary image column can be set on create; set {attribute} of {entity} with an update.", 400);

    public static XrmdFaultException ObjectDoesNotExist(string entityLogicalName, Guid id) =>
        // Exact Dataverse text: client code commonly detects a missing record by matching "Does Not Exist" in the message.
        new(ErrorCodes.ObjectDoesNotExist, $"{entityLogicalName} With Id = {id} Does Not Exist", 404);

    /// <summary>403 <see cref="ErrorCodes.PrivilegeDenied"/>: the user misses <paramref name="privilegeName"/> on <paramref name="entity"/>.</summary>
    public static XrmdFaultException PrivilegeDenied(Guid userId, string privilegeName, string entity, int objectTypeCode)
    {
        ArgumentNullException.ThrowIfNull(privilegeName);
        // The privilege id as the catalog derives it (Xrmd.Security.PrivilegeNames.IdOf).
        var privilegeId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes("xrmd:privilege:" + privilegeName.ToLowerInvariant())));
        // Exact Dataverse text: client code and support tooling parse the missing privilege name ("is missing prv… privilege")
        // out of this message.
        return new(ErrorCodes.PrivilegeDenied,
            $"Principal user (Id={userId}, type=8) is missing {privilegeName} privilege (Id={privilegeId}) on OTC={objectTypeCode} for entity '{entity}'. "
            + "Consider adding missing privilege to one of the principal (user/team) roles for the request to succeed.", 403);
    }

    /// <summary>403 <see cref="ErrorCodes.AccessDenied"/>: the user has no <paramref name="right"/> on the record.</summary>
    public static XrmdFaultException AccessDenied(Guid userId, AccessRights right, string entity, Guid recordId) =>
        // Exact Dataverse text: client code parses the missing access right and the record id out of this message.
        new(ErrorCodes.AccessDenied,
            $"Principal user with id {userId} does not have {right} right(s) for record with id {recordId} of entity {entity}.", 403);

    /// <summary>403 <see cref="ErrorCodes.PrincipalPrivilegeDenied"/>: the target user or team (e.g. a new owner) lacks a privilege.</summary>
    public static XrmdFaultException PrincipalPrivilegeDenied() =>
        new(ErrorCodes.PrincipalPrivilegeDenied, "The target user or team lacks a privilege the operation needs.", 403);

    /// <summary>403 <see cref="ErrorCodes.UserNotAssignedRoles"/>: the user has no role, neither direct nor from a team.</summary>
    public static XrmdFaultException UserNotAssignedRoles(Guid userId) =>
        new(ErrorCodes.UserNotAssignedRoles, $"User {userId} has no security role, neither directly nor through a team.", 403);

    /// <summary>
    /// 403 <see cref="ErrorCodes.RoleUpdateValidationFailure"/>: the calling user lacks <paramref name="privilegeName"/> at
    /// <paramref name="depth"/> (Basic, Local, Deep or Global), which it would grant.
    /// </summary>
    public static XrmdFaultException RoleUpdateValidationFailure(Guid userId, string privilegeName, string depth) =>
        new(ErrorCodes.RoleUpdateValidationFailure,
            $"User {userId} cannot grant {privilegeName} at depth {depth} without holding it at that depth.", 403);

    /// <summary>400 <see cref="ErrorCodes.InvalidPrivilege"/>: no privilege of the catalog has that id.</summary>
    public static XrmdFaultException InvalidPrivilege(Guid privilegeId) =>
        new(ErrorCodes.InvalidPrivilege, $"No privilege with id {privilegeId} exists.", 400);

    /// <summary>
    /// 412 <see cref="ErrorCodes.DuplicateRecordsFound"/>: the record a Create or Update writes duplicates an existing one under a
    /// published duplicate detection rule (plan 2.15, ruling D12; clients switch on the code, the text is our own).
    /// </summary>
    public static XrmdFaultException DuplicateRecordsFound() =>
        new(ErrorCodes.DuplicateRecordsFound,
            "A duplicate record was found: a published duplicate detection rule matches an existing record. Change the values or suppress duplicate detection.",
            412);

    /// <summary>412 <see cref="ErrorCodes.DuplicateRecord"/>: the association already exists.</summary>
    public static XrmdFaultException DuplicateAssociation() =>
        // Exact Dataverse text: client code treats an existing association as success by matching "Cannot insert duplicate key".
        new(ErrorCodes.DuplicateRecord, "Cannot insert duplicate key.", 412);

    /// <summary>
    /// 403 <see cref="ErrorCodes.PrivilegeDenied"/>: <paramref name="message"/> (e.g. <c>Create</c>) is refused to every caller
    /// on <paramref name="entity"/>, a table the system maintains.
    /// </summary>
    public static XrmdFaultException SystemMaintainedTable(string message, string entity) =>
        new(ErrorCodes.PrivilegeDenied, $"{message} of {entity} is not allowed: the table is maintained by the system.", 403);

    /// <summary>403 <see cref="ErrorCodes.PrivilegeDenied"/>: the SYSTEM user's row is never written by a request.</summary>
    public static XrmdFaultException SystemUserCannotBeModified() =>
        new(ErrorCodes.PrivilegeDenied, "The SYSTEM user cannot be modified.", 403);

    /// <summary>403 <see cref="ErrorCodes.CannotActOnBehalfOfAnotherUser"/>: the user lacks <c>prvActOnBehalfOfAnotherUser</c>.</summary>
    public static XrmdFaultException CannotActOnBehalfOfAnotherUser() =>
        new(ErrorCodes.CannotActOnBehalfOfAnotherUser, "The caller lacks prvActOnBehalfOfAnotherUser, which impersonating another user requires.", 403);

    /// <summary>403 <see cref="ErrorCodes.FieldCreateDenied"/>: the user may not set the secured column on create.</summary>
    public static XrmdFaultException FieldCreateDenied(string entity, string attribute, Guid userId) =>
        new(ErrorCodes.FieldCreateDenied,
            $"User {userId} has no field security permission to set the secured column {attribute} of {entity} on create.", 403);

    /// <summary>403 <see cref="ErrorCodes.FieldUpdateDenied"/>: the user may not update the secured column.</summary>
    public static XrmdFaultException FieldUpdateDenied(string entity, string attribute, Guid userId) =>
        new(ErrorCodes.FieldUpdateDenied,
            $"User {userId} has no field security permission to update the secured column {attribute} of {entity}.", 403);

    /// <summary>403 <see cref="ErrorCodes.FieldReadDenied"/>: the user may not read the secured column.</summary>
    public static XrmdFaultException FieldReadDenied(string entity, string attribute, Guid userId) =>
        new(ErrorCodes.FieldReadDenied,
            $"User {userId} has no field security permission to read the secured column {attribute} of {entity}.", 403);

    /// <summary>400 <see cref="ErrorCodes.FieldNotSecurable"/>: the column cannot be secured.</summary>
    public static XrmdFaultException NotSecurable(string attribute) =>
        new(ErrorCodes.FieldNotSecurable, $"The column {attribute} cannot be secured with field security.", 400);

    /// <summary>403 <see cref="ErrorCodes.AdminProfileReadOnly"/>: the System Administrator profile and its permissions are fixed.</summary>
    public static XrmdFaultException AdminProfileReadOnly() =>
        new(ErrorCodes.AdminProfileReadOnly,
            "The System Administrator field security profile is read-only: it cannot be changed or deleted, and no permission can be added to it.", 403);

    /// <summary>400 <see cref="ErrorCodes.PermissionOnUnsecuredColumn"/>: the column of the permission or share is not secured.</summary>
    public static XrmdFaultException PermissionOnUnsecuredColumn(string entity, string attribute) =>
        new(ErrorCodes.PermissionOnUnsecuredColumn,
            $"The column {attribute} of {entity} is not secured, so it takes no field permission or field share.", 400);

    /// <summary>
    /// 403 <see cref="ErrorCodes.UnsecureWithoutPermissions"/>: only a caller holding read, create and update on a secured
    /// column (or bypassing field security) may turn its <c>IsSecured</c> off.
    /// </summary>
    public static XrmdFaultException UnsecureWithoutPermissions(string entity, string attribute, Guid userId) =>
        new(ErrorCodes.UnsecureWithoutPermissions,
            $"User {userId} cannot turn field security off on the column {attribute} of {entity}: that needs read, create and update permission on the column.",
            403);

    /// <summary>400 <see cref="ErrorCodes.ShareWithoutAccess"/>: a new field share must grant read or update.</summary>
    public static XrmdFaultException ShareWithoutAccess() =>
        new(ErrorCodes.ShareWithoutAccess, "A field share must grant read access, update access or both.", 400);

    /// <summary>400 <see cref="ErrorCodes.ShareUpdateWithoutAccess"/>: an update may not leave a field share with no access.</summary>
    public static XrmdFaultException ShareUpdateWithoutAccess() =>
        new(ErrorCodes.ShareUpdateWithoutAccess,
            "A field share cannot be updated to grant neither read nor update access; delete it instead.", 400);

    /// <summary>400 <see cref="ErrorCodes.ShareDuplicate"/>: the column of the record is already shared with the principal.</summary>
    public static XrmdFaultException ShareDuplicate(string attribute, Guid recordId, Guid principalId) =>
        new(ErrorCodes.ShareDuplicate,
            $"The column {attribute} of record {recordId} is already shared with principal {principalId}; update that share instead.", 400);

    /// <summary>403 <see cref="ErrorCodes.ShareWithoutUpdate"/>: only a user who can update the column on the record may share it.</summary>
    public static XrmdFaultException ShareWithoutUpdate(string attribute, Guid recordId, Guid userId) =>
        new(ErrorCodes.ShareWithoutUpdate,
            $"User {userId} cannot share the column {attribute} of record {recordId} without being able to update it.", 403);

    /// <summary>403 <see cref="ErrorCodes.ShareWithoutUpdate"/>: a field share granting read needs the sharer to read the column on the record.</summary>
    public static XrmdFaultException ShareWithoutRead(string attribute, Guid recordId, Guid userId) =>
        new(ErrorCodes.ShareWithoutUpdate,
            $"User {userId} cannot give read access to the column {attribute} of record {recordId} without being able to read it.", 403);

    /// <summary>403 <see cref="ErrorCodes.UserDisabled"/>: the user is disabled (<c>isdisabled = 1</c>).</summary>
    public static XrmdFaultException UserDisabled(Guid userId) =>
        new(ErrorCodes.UserDisabled, $"User {userId} is disabled.", 403);
}

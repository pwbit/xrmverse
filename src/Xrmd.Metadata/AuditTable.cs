using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The seeded <c>audit</c> table (<c>fixtures/seed/audit.json</c>): its name, object type code and column names. The table is
/// read-only for every request; the audit writer and the audit messages are its only users.
/// </summary>
public static class AuditTable
{
    /// <summary>The logical name of the table.</summary>
    public const string Name = "audit";

    /// <summary>
    /// The object type code, from memory (not on Learn): the checklist verifies it on a real environment and records the
    /// observed value when it differs.
    /// </summary>
    public const int ObjectTypeCode = 4567;

    /// <summary>The primary id column.</summary>
    public const string AuditId = "auditid";

    public const string Action = "action";
    public const string Operation = "operation";
    public const string ObjectId = "objectid";
    public const string ObjectTypeCodeColumn = "objecttypecode";
    public const string UserId = "userid";
    public const string CallingUserId = "callinguserid";
    public const string CreatedOn = "createdon";

    /// <summary>The change data of the row (our own versioned JSON); never part of the Web API entity type.</summary>
    public const string ChangeData = "changedata";

    public const string AttributeMask = "attributemask";
    public const string TransactionId = "transactionid";
    public const string TimeToLiveInSeconds = "timetoliveinseconds";
    public const string UserAdditionalInfo = "useradditionalinfo";
    public const string RegardingObjectId = "regardingobjectid";
    public const string AdditionalInfo = "additionalinfo";

    /// <summary>The global choices of <see cref="Action"/> and <see cref="Operation"/>.</summary>
    public const string ActionChoice = "audit_action", OperationChoice = "audit_operation";

    /// <summary>The relationships from <c>systemuser</c>.</summary>
    public const string UserRelationship = "lk_audit_userid", CallingUserRelationship = "lk_audit_callinguserid";

    /// <summary>
    /// True for the two polymorphic lookups of the audit table, <c>objectid</c> and <c>regardingobjectid</c>: they name the
    /// audited record, whichever table it is in, so they have no fixed targets and keep a type column
    /// (<c>&lt;name&gt;type</c>, the object type code of the record's table) as <c>fileattachment.objectid</c> does.
    /// </summary>
    public static bool IsTargetlessLookup(ColumnDefinition a) =>
        a is LookupColumn && a.EntityLogicalName == Name && a.LogicalName is ObjectId or RegardingObjectId;
}

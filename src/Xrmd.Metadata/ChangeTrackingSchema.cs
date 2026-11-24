using Xrmd.Model.Metadata;
using Xrmd.Abstractions.Storage;

namespace Xrmd.Metadata;

/// <summary>
/// Change tracking (plan 2.13): which tables can be tracked (ruling C2), the retention limits of the organization column
/// <c>expirechangetrackingindays</c> (ruling C1) and the index a tracked table gets on <c>versionnumber</c> (ruling C11).
/// </summary>
/// <remarks>
/// The tracking state lives in <c>meta</c> (migration 0007): <c>meta.change_tracking_table</c> (one row per tracked table, with
/// the version at which tracking started), <c>meta.deleted_record</c> (the deletion log) and <c>meta.change_tracking_state</c>
/// (one row: the version up to which the log was purged, and the time of the last restore from a backup).
/// </remarks>
public static partial class ChangeTrackingSchema
{
    /// <summary>The default of <c>organization.expirechangetrackingindays</c> (Learn: 7 days).</summary>
    public const int DefaultExpireDays = 7;

    /// <summary>The largest value of <c>organization.expirechangetrackingindays</c> (Learn: 0 to 365).</summary>
    public const int MaxExpireDays = 365;

    /// <summary>The organization column that holds how many days the deletion log is kept.</summary>
    public const string ExpireDaysColumn = "expirechangetrackingindays";

    /// <summary>
    /// The system tables xrmverse deletes rows from outside <c>CoreOperations.DeleteAsync</c> (so those deletions would never
    /// reach the deletion log): they cannot be tracked. Beyond the seven of ruling C2, the views and forms of a deleted table
    /// (<c>savedquery</c>, <c>systemform</c>), its privileges (<c>privilege</c>) and its message filters and steps
    /// (<c>sdkmessagefilter</c>, <c>sdkmessageprocessingstep</c>), deleted with the table (controller ruling, fix round 1), and the
    /// activity pointer and party rows of a deleted custom activity (plan 2.11 ruling A13), and the others commented below. A guard
    /// test (<c>ChangeTrackingSchemaTests</c>) checks every raw delete against this list.
    /// </summary>
    public static IReadOnlySet<string> IneligibleTables { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AuditTable.Name, "principalobjectaccess", FieldSecurityTables.Share, FileColumns.AttachmentEntity, "solutioncomponent",
        FieldSecurityTables.Permission, "sdkmessageprocessingstepimage",
        "savedquery", "systemform", "privilege", "sdkmessagefilter", "sdkmessageprocessingstep",
        ActivitySchema.Pointer, ActivitySchema.Party,
        // The duplicate rules of a deleted table, their conditions and the duplicate records of its rows (plan 2.15, ruling D6).
        DuplicateDetectionSchema.Rule, DuplicateDetectionSchema.Condition, DuplicateDetectionSchema.Record,
        // A succeeded job of a step with asyncautodelete is deleted by the async worker (plan 3.2).
        "asyncoperation",
        // The components of an app module are replaced by the solution import (plan 3.5 UI-1a).
        AppModuleSchema.Component,
        // The system charts of a deleted table go with it, as its views and forms (plan 3.5 UI-1f).
        ChartSchema.Table,
        // The postregarding rows of a deleted table go with it; the server maintains the table (plan 3.5 UI-1g).
        PostSchema.Regarding,
    };

    /// <summary>
    /// True when <paramref name="entity"/> can have <c>ChangeTrackingEnabled</c>: neither an intersect table nor one of
    /// <see cref="IneligibleTables"/>. The source of <c>CanChangeTrackingBeEnabled</c>, which is computed, never stored.
    /// </summary>
    public static bool CanBeEnabled(TableDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.IsIntersect != true && entity.LogicalName is { } name && !IneligibleTables.Contains(name);
    }

    /// <summary>The name of the index of a tracked table on <c>versionnumber</c>: <c>IX_{table}_versionnumber</c>.</summary>
    public static string IndexName(string entityLogicalName) => $"IX_{entityLogicalName}_{TypeMapping.VersionNumber}";

    /// <summary>The DDL that creates <see cref="IndexName"/> on the table of <paramref name="entityLogicalName"/> when it is missing.</summary>
    /// <remarks>In the SQL of <paramref name="d"/> (plan 2.7a, Task 4: <see cref="ISqlDialect.CreateIndexIfMissing"/>).</remarks>
    public static string EnsureIndexSql(string entityLogicalName, ISqlDialect d)
    {
        ArgumentNullException.ThrowIfNull(d);
        LogicalNameValidator.EnsureValid(entityLogicalName, "entity");
        return d.CreateIndexIfMissing(DdlGenerator.DataSchema, entityLogicalName, IndexName(entityLogicalName), d.Quote(TypeMapping.VersionNumber));
    }
}

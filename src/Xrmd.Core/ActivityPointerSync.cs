using System.Data;
using System.Data.Common;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Abstractions;
using Xrmd.Metadata;

namespace Xrmd.Core;

/// <summary>
/// The <c>activitypointer</c> row of an activity (plan 2.11, ruling A1, decision D1): one row per activity with the activity's
/// <c>activityid</c>, holding the common columns of the stored activity row and <c>activitytypecode</c>. The Core writes it in the
/// session of every Create, Update (SetState and Assign included) and Delete of an activity, after the activity row and before
/// the post-operation stage, so that a rollback undoes both. It is not a message: no plug-in step and no audit row (A21).
/// </summary>
public sealed class ActivityPointerSync(IMetadataStore store)
{
    private const string IdParameter = "@_id";

    /// <summary>
    /// Copies the common columns of the stored row of <paramref name="activity"/> <paramref name="id"/> into
    /// <c>data.activitypointer</c>: every SQL column of the pointer that the activity table also has, but <c>versionnumber</c>;
    /// <c>activitytypecode</c> is the activity's object type code. On Create (<paramref name="isCreate"/>) the row is inserted
    /// with a plain <c>INSERT</c>: each activity table has its own primary key, so the pointer's key is the only place where two
    /// activities with one id meet, and a second activity with an id already taken is refused with Dataverse's duplicate-record
    /// fault (412) instead of taking over the first one's row (final review C1). On Update it is updated with a <c>MERGE</c>
    /// (inserted when missing). A text column longer on the activity than on the pointer is cut to the pointer's length (ruling Y1:
    /// <c>email.subject</c> 800 into <c>activitypointer.subject</c> 400; final review M8: <c>description</c>, a Memo of 2000 on the
    /// pointer; the activity row keeps the full value).
    /// </summary>
    /// <remarks>Ruling Y1, from memory: checklist (what Dataverse's pointer holds for a subject longer than its own length).</remarks>
    /// <exception cref="XrmdFaultException">DuplicateRecord (412) on Create when a pointer row with <paramref name="id"/> exists.</exception>
    public async Task WriteAsync(DataSession s, EntityMetadata activity, Guid id, bool isCreate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(activity);
        var pointer = store.GetEntity(ActivitySchema.Pointer)
                      ?? throw new InvalidOperationException($"The {ActivitySchema.Pointer} table has not been seeded.");
        var activityColumns = ColumnsOf(activity);
        var columns = new List<(string Name, string Source)>();
        foreach (var (column, attribute) in ColumnsOf(pointer))
        {
            if (column is ActivitySchema.ActivityId or ActivitySchema.ActivityTypeCode or TypeMapping.VersionNumber) continue;
            if (!activityColumns.TryGetValue(column, out var source)) continue;
            columns.Add((column, Truncated(s.Dialect, column, attribute, source)));
        }

        var all = columns.Select(c => c.Name).Prepend(ActivitySchema.ActivityTypeCode).ToList();
        var select = $"""
            SELECT a.{s.Quote(ActivitySchema.ActivityId)}, @_otc AS {s.Quote(ActivitySchema.ActivityTypeCode)}{string.Concat(columns.Select(c => $", {c.Source} AS {s.Quote(c.Name)}"))}
                   FROM {s.DataTable(activity.LogicalName)} a WHERE a.{s.Quote(ActivitySchema.ActivityId)} = {IdParameter}
            """;
        var sql = isCreate
            ? $"""
            INSERT INTO {s.DataTable(ActivitySchema.Pointer)} ({s.Quote(ActivitySchema.ActivityId)}, {string.Join(", ", all.Select(c => s.Quote(c)))})
            {select}
            """
            : s.Dialect.Upsert($"{s.Dialect.Quote(DdlGenerator.DataSchema)}.{s.Dialect.Quote(ActivitySchema.Pointer)}",
                [ActivitySchema.ActivityId], all, select);
        await using var cmd = s.Command(sql);
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        cmd.AddParameter("@_otc", DbType.Int32, activity.ObjectTypeCode ?? 0);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (DbException ex) when (isCreate && s.Dialect.IsPrimaryKeyViolation(ex))
        {
            // The same fault as the Create of a row whose id its own table already has (CoreOperations.CreateAsync).
            throw new XrmdFaultException(ErrorCodes.DuplicateRecord,
                $"A record that has the attribute values {ActivitySchema.ActivityId} = {id} already exists on entity '{activity.LogicalName}'.", 412);
        }
    }

    /// <summary>Deletes the pointer row of the activity <paramref name="id"/> (nothing to delete is not an error).</summary>
    public static async Task DeleteAsync(DataSession s, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command(
            $"DELETE FROM {s.DataTable(ActivitySchema.Pointer)} WHERE {s.Quote(ActivitySchema.ActivityId)} = {IdParameter}");
        cmd.AddParameter(IdParameter, DbType.Guid, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The SQL columns of a table, each with the attribute that owns it.</summary>
    private static Dictionary<string, AttributeMetadata> ColumnsOf(EntityMetadata e)
    {
        var result = new Dictionary<string, AttributeMetadata>(StringComparer.Ordinal);
        foreach (var a in e.Attributes ?? [])
            foreach (var column in TypeMapping.ToColumns(a))
                result.TryAdd(column.Name, a);
        return result;
    }

    /// <summary>
    /// The activity's column, cut to the pointer's text length when the activity's own is longer: a String column (ruling Y1,
    /// from memory: checklist) and a Memo column such as <c>description</c> (final review M8: the pointer declares 2000, an
    /// email's description is longer; from memory: checklist 1b).
    /// </summary>
    private static string Truncated(ISqlDialect d, string column, AttributeMetadata pointer, AttributeMetadata source)
    {
        int? max = pointer switch
        {
            StringAttributeMetadata { MaxLength: { } length } when length <= TypeMapping.MaxNvarcharLength => length,
            MemoAttributeMetadata { MaxLength: { } length } => length,
            _ => null,
        };
        if (max is not { } limit) return $"a.{d.Quote(column)}";
        var longer = source switch
        {
            StringAttributeMetadata s => s.MaxLength is not { } length || length > limit,
            MemoAttributeMetadata m => m.MaxLength is not { } length || length > limit,
            _ => false,
        };
        return longer ? $"LEFT(a.{d.Quote(column)}, {limit})" : $"a.{d.Quote(column)}";
    }
}

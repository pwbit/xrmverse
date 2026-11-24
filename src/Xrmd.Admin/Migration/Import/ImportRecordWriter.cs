using System.Data;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Creates the records of one sub-chunk through the core, in the sub-chunk's transaction (plan M4a): a record that already exists
/// is skipped (a resume), lookups are written, deferred or nulled as <see cref="ImportRecordBuilder"/> says, and the deferred and
/// nulled ones go to the ledger with the record. A column the target lacks is reported once per table and column.
/// </summary>
public sealed class ImportRecordWriter(CoreOperations core, Caller caller, MigrationLedger ledger, ImportRecordBuilder builder, int phase,
    IMetadataStore metadata)
{
    /// <summary>
    /// Tables whose rows the organization makes itself for each new row of another table (a user's settings): an exported row that
    /// already exists updates it, rather than being skipped.
    /// </summary>
    public static readonly IReadOnlySet<string> UpdatedWhenPresent = new HashSet<string>(StringComparer.Ordinal) { "usersettings" };

    private readonly HashSet<(string Table, string Column)> _missingReported = [];

    public ImportRecordBuilder Builder => builder;

    /// <summary>
    /// Writes <paramref name="rows"/> of <paramref name="table"/> in <paramref name="s"/>; <paramref name="adjust"/> changes the entity
    /// of a record before it is created (null: create nothing for that record).
    /// </summary>
    public async Task WriteAsync(DataSession s, string table, IReadOnlyList<MigrationRecord> rows,
        Func<DataSession, MigrationRecord, Entity, Task<Entity?>>? adjust, CancellationToken ct)
    {
        builder.Reset();
        await builder.LoadAsync(s, table, rows, ct);
        foreach (var record in rows)
        {
            ct.ThrowIfCancellationRequested();
            var id = builder.Map(record.Id);
            var present = builder.Exists(table, id);
            if (present && !UpdatedWhenPresent.Contains(table)) continue;
            var built = builder.Build(table, record);
            foreach (var column in built.Missing)
                if (_missingReported.Add((table, column)))
                    await ledger.IssueAsync(new MigrationIssue(phase, MigrationIssue.Warning, "column-missing", table, null, column,
                        $"The column '{column}' of '{table}' does not exist in the organization: its values are not imported."), ct);
            var entity = adjust is null ? built.Entity : await adjust(s, record, built.Entity);
            if (entity is null) continue;
            if (present) await core.UpdateAsync(s, caller, entity, ct);
            else await core.CreateAsync(s, caller, entity, ct);
            await KeepStateAsync(s, table, record, id, ct);
            builder.Written(table, id);
            foreach (var d in built.Deferred)
                await ledger.DeferAsync(s, table, id, d.Column, d.RefTable, d.RefId, ModifiedOn(record), ct);
            foreach (var n in built.Nulled)
                await ledger.IssueAsync(entity.GetAttributeValue<object>(n.Column) is EntityCollection
                    ? new MigrationIssue(phase, MigrationIssue.Warning, "party-dropped", table, id, n.Column,
                        $"A party of '{n.Column}' is {n.RefTable} {n.RefId}, which is not in the organization: the party is left out.")
                    : new MigrationIssue(phase, MigrationIssue.Warning, "lookup-nulled", table, id, n.Column,
                        $"The column '{n.Column}' referenced {n.RefTable} {n.RefId}, which is not in the package: it is left empty."), s, ct);
        }
    }

    /// <summary>
    /// The exported <c>statecode</c> and <c>statuscode</c>, written as they are (spec §7.5): the create applies the defaults of a new
    /// row (an activity starts open), so the row gets them afterwards, in the same transaction.
    /// </summary>
    private async Task KeepStateAsync(DataSession s, string table, MigrationRecord record, Guid id, CancellationToken ct)
    {
        if (metadata.GetEntity(table) is not { } m) return;
        var columns = new[] { "statecode", "statuscode" }
            .Where(c => record.Values.GetValueOrDefault(c) is OptionValue && m.Attributes?.Any(a => a.LogicalName == c) == true).ToList();
        if (columns.Count == 0) return;
        await using var cmd = s.Command($"UPDATE {s.DataTable(m.LogicalName)} SET " +
            string.Join(", ", columns.Select(c => $"{s.Quote(c)} = @{c}")) + $" WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = @id");
        foreach (var c in columns) cmd.AddParameter("@" + c, DbType.Int32, ((OptionValue)record.Values[c]).Value);
        cmd.AddParameter("@id", DbType.Guid, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Sets on <paramref name="entity"/> the <c>modifiedon</c> and <c>modifiedby</c> its row has now (the exported ones, written by the
    /// records phase), unless it already carries them: an update of a later phase keeps them, since the migration scope keeps what
    /// the entity carries and otherwise stamps the importing user and the current time.
    /// </summary>
    public static async Task KeepModifiedAsync(DataSession s, IMetadataStore metadata, Entity entity, CancellationToken ct)
    {
        if (metadata.GetEntity(entity.LogicalName) is not { } m) return;
        var columns = new[] { "modifiedon", "modifiedby" }
            .Where(c => !entity.Contains(c) && m.Attributes?.Any(a => a.LogicalName == c) == true).ToList();
        if (columns.Count == 0) return;
        await using var cmd = s.Command($"SELECT {string.Join(", ", columns.Select(s.Quote))} FROM {s.DataTable(m.LogicalName)} WHERE {s.Quote(SystemAttributes.PrimaryIdName(m))} = @id");
        cmd.AddParameter("@id", DbType.Guid, entity.Id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return;
        for (var i = 0; i < columns.Count; i++)
        {
            if (rd.IsDBNull(i)) continue;
            entity[columns[i]] = columns[i] == "modifiedon" ? DateTime.SpecifyKind(rd.GetDateTime(i), DateTimeKind.Utc) : new EntityReference("systemuser", rd.GetGuid(i));
        }
    }

    /// <summary>The record's exported <c>modifiedon</c>, which the deferred update keeps.</summary>
    public static DateTime? ModifiedOn(MigrationRecord record) =>
        record.Values.GetValueOrDefault("modifiedon") is DateTimeValue d ? d.Utc : null;
}

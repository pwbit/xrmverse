using System.Data;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Migration.Records;

namespace Xrmd.Admin.Migration.Import;

/// <summary>A lookup of a record that is not written with it: deferred to the deferred-lookups phase, or nulled.</summary>
public sealed record HeldLookup(string Column, string RefTable, Guid RefId);

/// <summary>A package record turned into the entity to create, with its deferred and nulled lookups and the columns the target lacks.</summary>
public sealed record BuiltRecord(Entity Entity, IReadOnlyList<HeldLookup> Deferred, IReadOnlyList<HeldLookup> Nulled, IReadOnlyList<string> Missing);

/// <summary>
/// The records of a sub-chunk as the target sees them (plan M4a): which referenced rows exist, and the entity of each record.
/// A lookup is written when its row exists (in the target, or written earlier in the transaction); it is deferred when its table
/// is in the package (the row may come later); otherwise it is nulled, or the row fails with strict lookups.
/// </summary>
public sealed class ImportRecordBuilder(IMetadataStore metadata, IReadOnlyDictionary<Guid, Guid> idMap, IReadOnlySet<string> packageTables,
    bool strictLookups)
{
    private static readonly HashSet<string> KeptSystemColumns = new(StringComparer.Ordinal)
        { "createdon", "createdby", "modifiedon", "modifiedby", "owningbusinessunit", "overriddencreatedon" };

    private readonly HashSet<(string Table, Guid Id)> _known = [];

    public IReadOnlyDictionary<Guid, Guid> IdMap => idMap;

    /// <summary>The target's id of a source id.</summary>
    public Guid Map(Guid id) => ImportValueMapper.Map(idMap, id);

    /// <summary>Marks a row as existing in the target (written in this transaction).</summary>
    public void Written(string table, Guid id) => _known.Add((table, id));

    public bool Exists(string table, Guid id) => _known.Contains((table, id));

    /// <summary>Forgets what <see cref="LoadAsync"/> and <see cref="Written"/> learned (a new transaction).</summary>
    public void Reset() => _known.Clear();

    /// <summary>Finds which of the rows the records reference, and which of the records themselves, exist in the target.</summary>
    public async Task LoadAsync(DataSession s, string table, IEnumerable<MigrationRecord> records, CancellationToken ct)
    {
        var wanted = new List<(string Table, Guid Id)>();
        foreach (var r in records)
        {
            wanted.Add((table, Map(r.Id)));
            foreach (var value in r.Values.Values)
                switch (value)
                {
                    case LookupValue l: wanted.Add((l.Table, Map(l.Id))); break;
                    case PartyListValue p:
                        wanted.AddRange(p.Parties.Where(x => x.Table is not null && x.Id is not null).Select(x => (x.Table!, Map(x.Id!.Value))));
                        break;
                }
        }
        foreach (var (t, id) in await ExistingAsync(s, metadata, wanted, ct)) _known.Add((t, id));
    }

    /// <summary>The (table, id) pairs of <paramref name="wanted"/> whose row exists, read with one query per table.</summary>
    public static async Task<List<(string Table, Guid Id)>> ExistingAsync(DataSession s, IMetadataStore metadata,
        IEnumerable<(string Table, Guid Id)> wanted, CancellationToken ct)
    {
        var found = new List<(string, Guid)>();
        foreach (var group in wanted.Distinct().GroupBy(w => w.Table, StringComparer.Ordinal))
        {
            if (metadata.GetEntity(group.Key) is not { } m) continue;
            var pk = SystemAttributes.PrimaryIdName(m);
            foreach (var batch in group.Select(g => g.Id).Chunk(5000))
            {
                await using var cmd = s.Command($"""
                    SELECT t.{s.Quote(pk)} FROM {s.DataTable(m.LogicalName)} t
                    JOIN {s.Dialect.JsonRecords("@ids", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$")])} j ON j.id = t.{s.Quote(pk)}
                    """);
                cmd.AddParameter("@ids", DbType.String, JsonSerializer.Serialize(batch), -1);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) found.Add((group.Key, rd.GetGuid(0)));
            }
        }
        return found;
    }

    /// <summary>
    /// The entity to create for <paramref name="record"/> of <paramref name="table"/>: columns the target lacks are left out (and
    /// listed), columns the server sets are left out except the system columns a migration keeps, file and image columns are left
    /// to the files phase, party lists keep their parties that exist.
    /// </summary>
    /// <exception cref="ImportValueException">A value that does not fit its column; a missing lookup with strict lookups.</exception>
    public BuiltRecord Build(string table, MigrationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var m = metadata.GetEntity(table) ?? throw new ImportValueException($"The table '{table}' does not exist in the organization.");
        var pk = SystemAttributes.PrimaryIdName(m);
        var attrs = (m.Attributes ?? []).ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        var entity = new Entity(table, Map(record.Id));
        List<HeldLookup> deferred = [], nulled = [];
        List<string> missing = [];
        foreach (var (column, raw) in record.Values)
        {
            if (column == pk) continue;
            if (!attrs.TryGetValue(column, out var a))
            {
                missing.Add(column);
                continue;
            }
            if (a is EntityNameAttributeMetadata || a.AttributeOf is not null) continue;
            if (!KeptSystemColumns.Contains(column) && !Xrmd.Core.Validation.EntityValidator.IsWritable(a, isCreate: true)) continue;
            var value = Retyped(a, raw);
            if (value is PartyListValue parties)
            {
                entity[column] = Parties(column, parties, nulled);
                continue;
            }
            if (value is LookupValue l)
            {
                var id = Map(l.Id);
                if (Exists(l.Table, id)) entity[column] = new EntityReference(l.Table, id);
                else if (packageTables.Contains(l.Table)) deferred.Add(new HeldLookup(column, l.Table, id));
                else if (strictLookups)
                    throw new ImportValueException($"The column '{column}' references {l.Table} {l.Id}, which is not in the package.", "0x80040217");
                else nulled.Add(new HeldLookup(column, l.Table, id));
                continue;
            }
            var mapped = ImportValueMapper.Map(a, value, idMap);
            if (!ReferenceEquals(mapped, ImportValueMapper.Skip)) entity[column] = mapped;
        }
        return new BuiltRecord(entity, deferred, nulled, missing);
    }

    /// <summary>
    /// A value Dataverse types otherwise than xrmverse (M6 run on the OP365 trial): a GUID in a lookup with one target
    /// (<c>owningbusinessunit</c> of organization-owned tables), an object type code as a number in a text column or as a table
    /// name in a whole number column (<c>objecttypecode</c>). An object type code that names no table is an error of the row.
    /// </summary>
    private MigrationValue Retyped(AttributeMetadata column, MigrationValue value)
    {
        switch (value)
        {
            case GuidValue g when column is LookupAttributeMetadata { Targets: [var target] }:
                return new LookupValue(target, g.Value);
            case IntValue code when IsObjectTypeCode(column) && column.AttributeType is AttributeTypeCode.String or AttributeTypeCode.Memo:
                return metadata.GetEntityByObjectTypeCode(checked((int)code.Value)) is { LogicalName: { } logicalName } ? new StringValue(logicalName)
                    : throw new ImportValueException($"The object type code {code.Value} of the column '{column.LogicalName}' names no table of the organization.");
            case StringValue table when IsObjectTypeCode(column) && column.AttributeType is AttributeTypeCode.Integer:
                return metadata.GetEntity(table.Value)?.ObjectTypeCode is { } otc ? new IntValue(otc)
                    : throw new ImportValueException($"The table '{table.Value}' of the column '{column.LogicalName}' is not in the organization.");
            default:
                return value;
        }
    }

    private static bool IsObjectTypeCode(AttributeMetadata column) => column.LogicalName.EndsWith("objecttypecode", StringComparison.Ordinal);

    /// <summary>The parties whose record exists (or that carry only an address); the others are listed in <paramref name="nulled"/>.</summary>
    private EntityCollection Parties(string column, PartyListValue value, List<HeldLookup> nulled)
    {
        var parties = new EntityCollection { EntityName = "activityparty" };
        foreach (var p in value.Parties)
        {
            var party = new Entity("activityparty") { ["participationtypemask"] = new OptionSetValue(p.ParticipationMask) };
            if (p.Table is not null && p.Id is { } id)
            {
                var target = Map(id);
                if (!Exists(p.Table, target))
                {
                    nulled.Add(new HeldLookup(column, p.Table, target));
                    continue;
                }
                party["partyid"] = new EntityReference(p.Table, target);
            }
            if (p.Address is not null) party["addressused"] = p.Address;
            if (!party.Contains("partyid") && !party.Contains("addressused")) continue;
            parties.Entities.Add(party);
        }
        return parties;
    }
}

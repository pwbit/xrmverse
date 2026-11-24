using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata;

/// <summary>
/// The <c>sdkmessage</c> / <c>sdkmessagefilter</c> catalog: one message row per entity message, file message, access team
/// message, change tracking message, currency message, rollup message, SendEmail and the queue messages, and one filter row per (entity message, entity), with stable ids so that every restart and every instance derives the same rows. Idempotent. A deleted
/// entity takes its filters with it, and the steps (with their images) registered on them.
/// </summary>
public sealed partial class SdkMessageCatalog(IDbConnectionFactory factory, IMetadataStore store, ILogger<SdkMessageCatalog>? logger = null) : IEntityCreatedHandler, IEntityDeletedHandler
{
    /// <summary>Messages that get a filter for every entity.</summary>
    public static readonly IReadOnlyList<string> EntityMessages =
    [
        "Create", "Update", "Delete", "Retrieve", "RetrieveMultiple", "Assign", "SetState", "SetStateDynamicEntity",
        "Associate", "Disassociate", "GrantAccess", "ModifyAccess", "RevokeAccess",
        // Plan 3.4, ruling E6; #76, ruling B9: a filter (an event) on elastic tables only.
        "Upsert",
    ];

    /// <summary>
    /// The file and image messages: message rows without filters (a step registered on one fires for every table, as a step
    /// without <c>sdkmessagefilterid</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> FileMessages =
    [
        "InitializeFileBlocksUpload", "UploadBlock", "CommitFileBlocksUpload", "InitializeFileBlocksDownload", "DownloadBlock", "DeleteFile",
    ];

    /// <summary>
    /// The access team messages (plan 2.16, ruling A9): message rows without filters, as their record is a parameter of any
    /// table (a step registered on one fires for every table).
    /// </summary>
    public static readonly IReadOnlyList<string> RecordTeamMessages = ["AddUserToRecordTeam", "RemoveUserFromRecordTeam"];

    /// <summary>
    /// The change tracking message (plan 2.13, ruling C10): a message row without filters, as only tracked tables answer it (a
    /// step registered on it fires for every table; the plug-in sees the table as <c>PrimaryEntityName</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> ChangeTrackingMessages = ["RetrieveEntityChanges"];

    /// <summary>
    /// The Cosmos SQL query message (plan #78, ruling J9): a message row without filters, as only elastic tables answer it (a
    /// step registered on it fires for every table; the plug-in sees the queried table as <c>PrimaryEntityName</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> CosmosQueryMessages = ["ExecuteCosmosSqlQuery"];

    /// <summary>
    /// The currency message (plan 2.10, ruling X12): a message row without filters (a step registered on it fires for any
    /// currency; the plug-in sees <c>transactioncurrency</c> as <c>PrimaryEntityName</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> CurrencyMessages = ["RetrieveExchangeRate"];

    /// <summary>
    /// The rollup message (plan 2.12b, ruling R10): <c>CalculateRollupField</c>, a message row without filters, as any table with a
    /// rollup column answers it (a step registered on it fires for every table; the plug-in sees the row's table as
    /// <c>PrimaryEntityName</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> RollupMessages = ["CalculateRollupField"];

    /// <summary>
    /// The email messages (plan 2.11, ruling A17): SendEmail, a message row with one filter, on <c>email</c>, so its steps register
    /// on that table.
    /// </summary>
    public static readonly IReadOnlyList<string> EmailMessages = [ActivitySchema.SendEmailMessage];

    /// <summary>
    /// The queue messages (plan 4.1a): message rows without filters, as AddToQueue takes a record of any queue-enabled table (a step
    /// registered on one fires for every table; the plug-in sees the record's table, <c>queueitem</c> or <c>queue</c> as
    /// <c>PrimaryEntityName</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> QueueMessages = QueueSchema.Messages;

    /// <summary>
    /// The entity messages of a table (plan 3.4, ruling E6; #76, ruling B9). The two activity tables are not written through
    /// requests (plan 2.11): Learn lists Retrieve, RetrieveMultiple, Associate and Disassociate for <c>activitypointer</c> and
    /// RetrieveMultiple, Associate and Disassociate for <c>activityparty</c>, so no write filter and no step. An elastic table
    /// answers every entity message including <c>Upsert</c> and all four bulk messages. Any other table answers the entity
    /// messages without <c>Upsert</c> (not an event on a standard table), plus <c>CreateMultiple</c> and <c>UpdateMultiple</c>.
    /// </summary>
    public static IReadOnlyList<string> EntityMessagesOf(TableDefinition entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var name = entity.LogicalName;
        if (name == ActivitySchema.Pointer) return ["Retrieve", "RetrieveMultiple", "Associate", "Disassociate"];
        if (name == ActivitySchema.Party) return ["RetrieveMultiple", "Associate", "Disassociate"];
        if (ElasticTables.IsElastic(entity)) return [.. EntityMessages, .. BulkMessages];
        var standard = EntityMessages.Where(m => m != "Upsert").Concat(StandardBulkMessages);
        return name == ActivitySchema.Email ? [.. standard, .. EmailMessages] : [.. standard];
    }

    /// <summary>The bulk messages that are events on a standard table (#76, ruling B9).</summary>
    private static IEnumerable<string> StandardBulkMessages => MergePairs.Values;

    /// <summary>
    /// The single-record message each mergeable bulk message runs per target (#76, ruling B4/B5), keyed by the single-record
    /// message, case-insensitive: <c>Create</c> with <c>CreateMultiple</c>, <c>Update</c> with <c>UpdateMultiple</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> MergePairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Create"] = "CreateMultiple",
        ["Update"] = "UpdateMultiple",
    };

    /// <summary>True when <paramref name="message"/> is an event on <paramref name="entity"/>: a filter row exists for it.</summary>
    public static bool IsEventOn(string message, TableDefinition entity) =>
        EntityMessagesOf(entity).Contains(message ?? "", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The bulk messages (plan 3.4, ruling E5; #76): message rows. Filters exist for <c>CreateMultiple</c> and
    /// <c>UpdateMultiple</c> on standard tables and for all four on elastic tables (<see cref="EntityMessagesOf"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> BulkMessages = ["CreateMultiple", "UpdateMultiple", "UpsertMultiple", "DeleteMultiple"];

    /// <summary>Every message row the catalog writes (<see cref="EntityMessages"/>, <see cref="FileMessages"/>,
    /// <see cref="RecordTeamMessages"/>, <see cref="ChangeTrackingMessages"/>, <see cref="CosmosQueryMessages"/>, <see cref="CurrencyMessages"/>,
    /// <see cref="RollupMessages"/>, <see cref="EmailMessages"/>, <see cref="QueueMessages"/> and <see cref="BulkMessages"/>).</summary>
    public static readonly IReadOnlyList<string> Messages =
        [.. EntityMessages, .. FileMessages, .. RecordTeamMessages, .. ChangeTrackingMessages, .. CosmosQueryMessages, .. CurrencyMessages, .. RollupMessages, .. EmailMessages, .. QueueMessages, .. BulkMessages];

    /// <summary>True for a message the catalog writes (case-insensitive): not a custom action.</summary>
    public static bool IsCatalogMessage(string? name) => Messages.Contains(name ?? "", StringComparer.OrdinalIgnoreCase);

    /// <summary>Stable id of a message: MD5 of <c>xrmd:sdkmessage:{name}</c>.</summary>
    public static Guid MessageId(string name) => Derive("xrmd:sdkmessage:" + name);

    /// <summary>Stable id of a filter: MD5 of <c>xrmd:sdkmessagefilter:{message}:{entity}</c>.</summary>
    public static Guid FilterId(string message, string entity) => Derive($"xrmd:sdkmessagefilter:{message}:{entity}");

    private static Guid Derive(string text) => new(MD5.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Creates the missing message rows and the missing filters of every known entity.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var session = new DataSession(conn, tx);
        var entities = store.GetAllTables().ToList();
        await UpsertAsync(session, entities, ct);
        await DeleteStaleUpsertFiltersAsync(session, entities, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Creates, in the creating session, the filters of the new entity (and the message rows if still missing).</summary>
    private Task OnTableCreatedAsync(TableDefinition entity, DataSession session, CancellationToken ct) =>
        UpsertAsync(session, [entity], ct);

    /// <summary>
    /// Deletes, in the deleting session, the entity's filters and the steps (with their images) registered on them. First,
    /// before any catalog row is deleted, a table a custom API is bound to cannot be deleted (plan 15, C5): 400 naming the API.
    /// This check runs inside the deleting transaction, after the store has already deleted the table's <c>meta</c> rows (the
    /// store calls this handler then); the refusal rolls the transaction back, those rows with it, and the store publishes
    /// nothing. It is not, at the SQL level, a check before any deletion.
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 when a custom API is bound to the table.</exception>
    private async Task OnTableDeletingAsync(TableDefinition entity, DataSession session, CancellationToken ct)
    {
        if (store.GetTable(CustomApiTables.Api) is not null)
        {
            await using var check = session.Command(
                session.Dialect.Limit($"SELECT uniquename FROM data.{CustomApiTables.Api} WHERE {CustomApiTables.BoundEntityLogicalName} = @entity ORDER BY uniquename", "1"));
            check.AddParameter("@entity", DbType.String, entity.LogicalName);
            if (await check.ExecuteScalarAsync(ct) is string api)
                throw Xrmd.Core.Errors.XrmdFaultException.InvalidArgument(
                    $"Table '{entity.LogicalName}' cannot be deleted: custom API '{api}' is bound to it.");
        }
        if (store.GetTable("sdkmessagefilter") is null) return;

        // Plan 2.7a, Task 5 fix round 1: subqueries instead of T-SQL's DELETE-alias-FROM-JOIN, so both engines run the statements.
        await using var cmd = session.Command("""
            DELETE FROM data.sdkmessageprocessingstepimage
            WHERE sdkmessageprocessingstepid IN (
                SELECT s.sdkmessageprocessingstepid FROM data.sdkmessageprocessingstep AS s
                JOIN data.sdkmessagefilter AS f ON f.sdkmessagefilterid = s.sdkmessagefilterid
                WHERE f.primaryobjecttypecode = @entity);

            DELETE FROM data.sdkmessageprocessingstep
            WHERE sdkmessagefilterid IN (SELECT f.sdkmessagefilterid FROM data.sdkmessagefilter AS f WHERE f.primaryobjecttypecode = @entity);

            DELETE FROM data.sdkmessagefilter WHERE primaryobjecttypecode = @entity;
            """);
        cmd.AddParameter("@entity", DbType.String, entity.LogicalName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// #76, ruling B9: <c>Upsert</c> is not an event on a standard table, so the <c>Upsert</c> filters that earlier versions wrote
    /// for standard tables are deleted when no step uses them; one a step uses stays (the step never fires, as before) and is logged.
    /// </summary>
    private async Task DeleteStaleUpsertFiltersAsync(DataSession session, IEnumerable<TableDefinition> entities, CancellationToken ct)
    {
        if (store.GetTable("sdkmessagefilter") is null || store.GetTable("sdkmessageprocessingstep") is null) return;
        var standard = JsonSerializer.Serialize(entities.Where(e => !ElasticTables.IsElastic(e)).Select(e => e.LogicalName));
        var upsert = MessageId("Upsert");

        await using (var used = session.Command($"""
            SELECT s.name, f.primaryobjecttypecode
            FROM data.sdkmessageprocessingstep AS s
            JOIN data.sdkmessagefilter AS f ON f.sdkmessagefilterid = s.sdkmessagefilterid
            WHERE f.sdkmessageid = @upsert AND f.primaryobjecttypecode IN (SELECT value FROM {session.Dialect.JsonArray("@standard")})
            """))
        {
            used.AddParameter("@upsert", DbType.Guid, upsert);
            used.AddParameter("@standard", DbType.String, standard);
            await using var rd = await used.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                logger?.LogWarning("Step '{Step}' is registered on Upsert of the standard table '{Table}': Upsert is not an event there, so the step never fires.",
                    rd.IsDBNull(0) ? "" : rd.GetString(0), rd.GetString(1));
        }

        await using var cmd = session.Command($"""
            DELETE FROM data.sdkmessagefilter
            WHERE sdkmessageid = @upsert AND primaryobjecttypecode IN (SELECT value FROM {session.Dialect.JsonArray("@standard")})
              AND NOT EXISTS (SELECT 1 FROM data.sdkmessageprocessingstep AS s WHERE s.sdkmessagefilterid = data.sdkmessagefilter.sdkmessagefilterid)
            """);
        cmd.AddParameter("@upsert", DbType.Guid, upsert);
        cmd.AddParameter("@standard", DbType.String, standard);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpsertAsync(DataSession session, IEnumerable<TableDefinition> entities, CancellationToken ct)
    {
        // During the first seed the catalog tables may not exist yet: EnsureAsync runs after the seed.
        if (store.GetTable("sdkmessage") is null || store.GetTable("sdkmessagefilter") is null) return;

        var messages = JsonSerializer.Serialize(Messages.Select(m => new { id = MessageId(m), name = m }));
        var filters = JsonSerializer.Serialize(entities.SelectMany(e => EntityMessagesOf(e).Select(m => new
        {
            id = FilterId(m, e.LogicalName!),
            name = $"{m}:{e.LogicalName}",
            message = MessageId(m),
            entity = e.LogicalName,
        })));

        // Plan 2.7a, Task 5 fix round 1: the T-SQL batch (DECLARE, IF … RETURN) became commands of their own in one session.
        var d = session.Dialect;
        // Mid-seed (no root business unit yet): the post-seed EnsureAsync inserts everything with the root business unit.
        if (await session.RootBusinessUnitAsync(ct) is not { } root) return;
        var (bu, now) = root;

        // A check then insert of many rows: the key lock serializes the seeds where the hint does not lock the missing rows.
        await session.KeyLockAsync("sdkmessage", "seed", ct);
        await using (var cmd = session.Command($"""
            INSERT INTO data.sdkmessage (sdkmessageid, name, categoryname, isprivate, isvalidforexecuteasync, availability, template,
                owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            SELECT j.id, j.name, N'', {d.BooleanLiteral(false)}, {d.BooleanLiteral(true)}, 1, {d.BooleanLiteral(false)}, @bu, @now, @now, 0, 1
            FROM {d.JsonRecords("@messages", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$.id"), new JsonColumn("name", SqlColumnKind.String, 256, "$.name")])} AS j
            WHERE NOT EXISTS (SELECT 1 FROM data.sdkmessage{d.LockHint(RowLock.UpdateRange)} WHERE sdkmessageid = j.id);
            """))
        {
            cmd.AddParameter("@messages", DbType.String, messages);
            cmd.AddParameter("@bu", DbType.Guid, bu);
            cmd.AddParameter("@now", DbType.DateTime2, now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await session.KeyLockAsync("sdkmessagefilter", "seed", ct);
        await using (var cmd = session.Command($"""
            INSERT INTO data.sdkmessagefilter (sdkmessagefilterid, name, sdkmessageid, primaryobjecttypecode, secondaryobjecttypecode,
                iscustomprocessingstepallowed, isvisible, availability, owningbusinessunit, createdon, modifiedon, statecode, statuscode)
            SELECT j.id, j.name, j.message, j.entity, N'none', {d.BooleanLiteral(true)}, {d.BooleanLiteral(true)}, 1, @bu, @now, @now, 0, 1
            FROM {d.JsonRecords("@filters", [new JsonColumn("id", SqlColumnKind.Guid, Path: "$.id"), new JsonColumn("name", SqlColumnKind.String, 256, "$.name"),
                new JsonColumn("message", SqlColumnKind.Guid, Path: "$.message"), new JsonColumn("entity", SqlColumnKind.String, 64, "$.entity")])} AS j
            WHERE NOT EXISTS (SELECT 1 FROM data.sdkmessagefilter{d.LockHint(RowLock.UpdateRange)} WHERE sdkmessagefilterid = j.id);
            """))
        {
            cmd.AddParameter("@filters", DbType.String, filters);
            cmd.AddParameter("@bu", DbType.Guid, bu);
            cmd.AddParameter("@now", DbType.DateTime2, now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}

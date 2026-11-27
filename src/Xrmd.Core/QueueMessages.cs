using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Metadata;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Pipeline;
using Xrmd.Query;

namespace Xrmd.Core;

/// <summary>
/// Stage 30 of the queue messages (plan 4.1a, tasks 5 and 6; spec §6): <c>AddToQueue</c>, <c>RemoveFromQueue</c>, <c>RouteTo</c>
/// and <c>AddPrincipalToQueue</c> place items; <c>PickFromQueue</c> and <c>ReleaseToQueue</c> set and clear the item's worker;
/// <c>RetrieveUserQueues</c> reads a user's queues. The core also calls here for the item's life cycle: the deactivation of a
/// record's items when it leaves the active state, and the delete of a queue. A record has at most one active queue item, except an email with status Received,
/// which may be in several queues; the rule is kept by an update lock on the record's row, held until commit, which every message
/// that places or moves an item takes first (the pattern of <c>EmailMessages.LockAsync</c>), not by an index, because the exception
/// depends on the record's status.
/// </summary>
/// <remarks>
/// Run by the pipeline, a message writes its items and memberships through the <see cref="NestedRequest"/> as the Create, Update and
/// Delete of <c>queueitem</c> and the Associate of <c>queuemembership_association</c>: their steps fire one level deeper, with the
/// queue message as their parent context, and the security guard checks each (decision 4). Without one (the server's own work, such as
/// the deactivation of a record's items) the same writes go straight to the core operations: no step fires and no privilege is checked.
/// An incoming email reaches its queue through AddToQueue run by the pipeline (plan 4.1c, <c>IncomingEmailImporter</c>).
/// <para>
/// S.2e3c1 (Task 5, Decisions 4 and 5): the messages run on the model request and read and write model records. The nested writes are
/// model requests; the core's entry adapter pairs each with today's SDK request class (<c>CoreResponses.TypedRequest</c>: a
/// <c>CreateRequest</c>, <c>UpdateRequest</c>, <c>DeleteRequest</c> or <c>AssociateRequest</c>), so the pipeline, the security guard and
/// the plug-ins see what they saw before.
/// </para>
/// </remarks>
public sealed class QueueMessages
{
    private const string Item = QueueSchema.QueueItem;
    private const string QueueTable = QueueSchema.Queue;
    private const string QueueId = QueueSchema.QueueIdColumn;
    private const string StateCode = "statecode", StatusCode = "statuscode", Title = "title", EnteredOn = "enteredon";
    private const string SystemUser = "systemuser", QueueViewType = "queueviewtype";

    /// <summary>The <c>statuscode</c> of an active item (<c>statecode</c> 0).</summary>
    private const int ActiveItemStatus = 1;

    /// <summary>The columns of <c>QueueItemProperties</c> the message sets itself, so the given ones are ignored.</summary>
    private static readonly HashSet<string> ServerColumns = new(StringComparer.Ordinal)
    {
        QueueId, QueueSchema.ObjectId, QueueSchema.ObjectIdTypeCode, "queueitemid",
    };

    private readonly CoreOperations _core;
    private readonly IMetadataStore _store;

    internal QueueMessages(CoreOperations core, IMetadataStore store)
    {
        _core = core;
        _store = store;
    }

    /// <inheritdoc cref="ExecuteAsync(Request, Caller, DataSession, NestedRequest?, CancellationToken)"/>
    public Task<Response> ExecuteAsync(Request request, Caller caller, DataSession s, CancellationToken ct) =>
        ExecuteAsync(request, caller, s, null, ct);

    /// <summary>
    /// The message <c>request.Name</c> (case-insensitive) with its final parameters: AddToQueue (<c>Target</c>,
    /// <c>DestinationQueueId</c>, optional <c>SourceQueueId</c> and <c>QueueItemProperties</c>; response <c>QueueItemId</c>),
    /// RemoveFromQueue (<c>QueueItemId</c>), RouteTo (<c>Target</c>: a queue, user or team; <c>QueueItemId</c>) and
    /// AddPrincipalToQueue (<c>Principal</c>: a user or team; <c>QueueId</c>), with empty responses for the last three;
    /// PickFromQueue (<c>QueueItemId</c>, <c>WorkerId</c>, <c>RemoveQueueItem</c>) and ReleaseToQueue (<c>QueueItemId</c>), with empty
    /// responses; RetrieveUserQueues (<c>UserId</c>, <c>IncludePublic</c>; response <c>EntityCollection</c> of <c>queue</c>).
    /// </summary>
    /// <param name="nested">Runs the item and membership writes through the pipeline; null writes them directly.</param>
    /// <exception cref="XrmdFaultException">400 for a missing or wrong parameter, a table that is not queue-enabled, an inactive record,
    /// queue or item, a source queue the record is not in, or a principal of another table; 404 for a missing record, queue, item,
    /// user or team; the faults of the writes.</exception>
    /// <remarks>The answer is a model <see cref="Response"/> named as the message; the core's entry adapter pairs it with today's
    /// typed SDK response (<c>AddToQueueResponse</c>, …, <c>RetrieveUserQueuesResponse</c>). The parameters are read in the order of
    /// the SDK handler this replaces, with its texts (the types named as the SDK ones a caller passes).</remarks>
    public async Task<Response> ExecuteAsync(Request request, Caller caller, DataSession s, NestedRequest? nested, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(s);
        var writer = new Writer(_core, s, caller, nested);
        switch (QueueSchema.Messages.FirstOrDefault(m => string.Equals(m, request.Name, StringComparison.OrdinalIgnoreCase)))
        {
            case QueueSchema.AddToQueue:
                var properties = request.Parameters.TryGetValue("QueueItemProperties", out var p) ? p as Record : null;
                var source = request.Parameters.TryGetValue("SourceQueueId", out var q) && q is Guid sourceId && sourceId != Guid.Empty ? sourceId : (Guid?)null;
                var id = await PlaceAsync(s, writer, CoreOperations.Parameter<RecordReference>(request, "Target", "EntityReference"),
                    CoreOperations.GuidParameter(request, "DestinationQueueId"), source, properties, ct);
                return new Response { Name = QueueSchema.AddToQueue, Results = { ["QueueItemId"] = id } };
            case QueueSchema.RemoveFromQueue:
                await writer.DeleteAsync(CoreOperations.GuidParameter(request, "QueueItemId"), ct);
                return new Response { Name = QueueSchema.RemoveFromQueue };
            case QueueSchema.RouteTo:
                await RouteAsync(s, writer, CoreOperations.Parameter<RecordReference>(request, "Target", "EntityReference"),
                    CoreOperations.GuidParameter(request, "QueueItemId"), ct);
                return new Response { Name = QueueSchema.RouteTo };
            case QueueSchema.AddPrincipalToQueue:
                await AddPrincipalAsync(s, writer, CoreOperations.Parameter<Record>(request, "Principal", "Entity"),
                    CoreOperations.GuidParameter(request, "QueueId"), ct);
                return new Response { Name = QueueSchema.AddPrincipalToQueue };
            case QueueSchema.PickFromQueue:
                await PickAsync(s, writer, CoreOperations.GuidParameter(request, "QueueItemId"), CoreOperations.GuidParameter(request, "WorkerId"),
                    request.Parameters.TryGetValue("RemoveQueueItem", out var r) && r is true, ct);
                return new Response { Name = QueueSchema.PickFromQueue };
            case QueueSchema.ReleaseToQueue:
                await ReleaseAsync(s, writer, CoreOperations.GuidParameter(request, "QueueItemId"), ct);
                return new Response { Name = QueueSchema.ReleaseToQueue };
            case QueueSchema.RetrieveUserQueues:
                var queues = await UserQueuesAsync(s, caller, CoreOperations.GuidParameter(request, "UserId"),
                    request.Parameters.TryGetValue("IncludePublic", out var pub) && pub is true, ct);
                return new Response { Name = QueueSchema.RetrieveUserQueues, Results = { ["EntityCollection"] = queues } };
            default:
                throw new NotSupportedException($"Request '{request.Name}' is not a queue message.");
        }
    }

    /// <summary>
    /// AddToQueue: with the record locked, the item already in the destination is kept (its properties applied); otherwise the
    /// record's item (the one in <paramref name="source"/> when given) moves there, with <c>enteredon</c> now; otherwise, and always
    /// for a received email without a source, a new active item is created there, titled with the record's primary name.
    /// </summary>
    private async Task<Guid> PlaceAsync(DataSession s, Writer writer, RecordReference target, Guid destination, Guid? source,
        Record? properties, CancellationToken ct)
    {
        var table = _store.GetTable(target.LogicalName ?? "")
                    ?? throw XrmdFaultException.InvalidArgument($"The entity '{target.LogicalName}' does not exist.");
        if (table.IsValidForQueue?.Value != true)
            throw XrmdFaultException.InvalidArgument(
                $"Records of '{table.LogicalName}' cannot be added to a queue: the table is not enabled for queues (IsValidForQueue).");
        var record = await LockRecordAsync(s, table, target.Id, ct);
        // A received email may be in several queues (Learn, queue tables). Any other inactive record is refused (from memory, not in the plan checklist yet).
        var received = IsReceivedEmail(table, record.Status);
        if (record.State is { } state && state != 0 && !received)
            throw XrmdFaultException.InvalidArgument(
                $"The {table.LogicalName} {target.Id} cannot be added to a queue: it is inactive (statecode {state}).");
        await DemandActiveQueueAsync(s, destination, ct);

        var items = await ActiveItemsAsync(s, target.Id, ct);
        Guid? fromSource = null;
        if (source is { } from)
            fromSource = items.Where(i => i.Queue == from).Select(i => (Guid?)i.Id).FirstOrDefault()
                         ?? throw XrmdFaultException.InvalidArgument(
                             $"The {table.LogicalName} {target.Id} has no active item in the source queue {from}.");
        var extra = Properties(properties);

        if (items.Where(i => i.Queue == destination).Select(i => (Guid?)i.Id).FirstOrDefault() is { } there)
        {
            if (extra.Attributes.Count > 0) await writer.UpdateAsync(With(new Record(Item, there), extra), ct);
            return there;
        }
        var moving = fromSource ?? (received || items.Count == 0 ? null : items[0].Id);
        if (moving is { } move)
        {
            var update = With(new Record(Item, move), extra);
            update[QueueId] = Reference(QueueTable, destination);
            update[EnteredOn] = DateTime.UtcNow;
            await writer.UpdateAsync(update, ct);
            return move;
        }

        var item = With(new Record(Item), extra);
        item[QueueId] = Reference(QueueTable, destination);
        item[QueueSchema.ObjectId] = Reference(table.LogicalName!, target.Id);
        if (!item.Contains(Title)) item[Title] = record.Name;
        if (!item.Contains(EnteredOn)) item[EnteredOn] = DateTime.UtcNow;
        return await writer.CreateAsync(item, ct);
    }

    /// <summary>
    /// Ruling R15: the Create of an active <c>queueitem</c> (direct, or the one AddToQueue issues) keeps the single-active-item rule.
    /// It takes the lock on the record's row that AddToQueue takes, then refuses the item when the record already has an active one,
    /// unless the record is a received email. Nothing is moved. AddToQueue's own Create passes: it creates only when the record has
    /// no active item, or is a received email. An inactive item, or one without a record of a known table, is left to the Create.
    /// </summary>
    /// <param name="row">The values about to be inserted.</param>
    /// <exception cref="XrmdFaultException">400 when the record already has an active item; 404 for a missing record.</exception>
    internal static async Task DemandNewItemAllowedAsync(DataSession s, IMetadataStore store, Record row, CancellationToken ct)
    {
        if (row.GetValue<ChoiceValue>(StateCode)?.Value is { } state && state != 0) return;
        if (row.GetValue<RecordReference>(QueueSchema.ObjectId) is not { } record
            || store.GetTable(record.LogicalName ?? "") is not { } table) return;
        var locked = await LockRecordAsync(s, table, record.Id, ct);
        if (IsReceivedEmail(table, locked.Status)) return;
        if ((await ActiveItemsAsync(s, record.Id, ct)).Count > 0)
            throw XrmdFaultException.InvalidArgument(
                $"The {table.LogicalName} {record.Id} already has an active queue item: a record is in one queue at a time. " +
                "Use AddToQueue or RouteTo to move it.");
    }

    /// <summary>A received email may be in several queues at once (Learn, queue tables).</summary>
    private static bool IsReceivedEmail(TableDefinition table, int? status) =>
        table.LogicalName == ActivitySchema.Email && status == QueueSchema.ReceivedEmailStatus;

    /// <summary>
    /// RouteTo: the item moves to the target queue, or to the default queue (<c>queueid</c>) of the target user or team, with
    /// <c>enteredon</c> now; an item already there is left as it is.
    /// </summary>
    private async Task RouteAsync(DataSession s, Writer writer, RecordReference target, Guid itemId, CancellationToken ct)
    {
        var destination = target.LogicalName switch
        {
            QueueTable => target.Id,
            "systemuser" or "team" => await DefaultQueueAsync(s, target, ct),
            _ => throw XrmdFaultException.InvalidArgument(
                $"A queue item is routed to a queue, a systemuser or a team, not to '{target.LogicalName}'."),
        };
        // The lock every placement takes, so a concurrent AddToQueue of the record waits for this move; the item is read under it.
        var columns = Columns(QueueId, QueueSchema.ObjectId, StateCode);
        var item = await _core.RetrieveRecordAsync(s, Caller.Internal, Item, itemId, columns, ct);
        if (item.GetValue<RecordReference>(QueueSchema.ObjectId) is { } record && _store.GetTable(record.LogicalName) is { } table)
        {
            await LockRecordAsync(s, table, record.Id, ct);
            item = await _core.RetrieveRecordAsync(s, Caller.Internal, Item, itemId, columns, ct);
        }
        if (item.GetValue<ChoiceValue>(StateCode)?.Value is { } state && state != 0)
            throw XrmdFaultException.InvalidArgument($"The queue item {itemId} cannot be routed: it is inactive.");
        await DemandActiveQueueAsync(s, destination, ct);
        if (item.GetValue<RecordReference>(QueueId)?.Id == destination) return;
        await writer.UpdateAsync(new Record(Item, itemId)
        {
            [QueueId] = Reference(QueueTable, destination), [EnteredOn] = DateTime.UtcNow,
        }, ct);
    }

    /// <summary>
    /// AddPrincipalToQueue: the user, or each member of the team, becomes a member of the queue (<c>queuemembership</c>); those who
    /// are members already are left out, and nothing is written when no one is left.
    /// </summary>
    private async Task AddPrincipalAsync(DataSession s, Writer writer, Record principal, Guid queue, CancellationToken ct)
    {
        await _core.RetrieveRecordAsync(s, Caller.Internal, QueueTable, queue, Columns(), ct); // 404 for a missing queue
        List<Guid> users;
        switch (principal.LogicalName)
        {
            case "systemuser":
                await _core.RetrieveRecordAsync(s, Caller.Internal, "systemuser", principal.Id, Columns(), ct);
                users = [principal.Id];
                break;
            case "team":
                await _core.RetrieveRecordAsync(s, Caller.Internal, "team", principal.Id, Columns(), ct);
                users = await GuidsAsync(s, $"SELECT systemuserid FROM {s.DataTable("teammembership")} WHERE teamid = @id", principal.Id, ct);
                break;
            default:
                throw XrmdFaultException.InvalidArgument(
                    $"The principal added to a queue is a systemuser or a team, not '{principal.LogicalName}'.");
        }
        var members = (await GuidsAsync(s,
            $"SELECT systemuserid FROM {s.DataTable(QueueSchema.QueueMembership)} WHERE {s.Quote(QueueId)} = @id", queue, ct)).ToHashSet();
        var added = users.Where(u => !members.Contains(u)).Distinct().ToList();
        if (added.Count == 0) return;
        await writer.AssociateAsync(Reference(QueueTable, queue), [.. added.Select(u => Reference(SystemUser, u))], ct);
    }

    /// <summary>
    /// PickFromQueue: the active item gets <paramref name="worker"/> as its <c>workerid</c>, with <c>workeridmodifiedon</c> now, whoever
    /// worked on it before (from memory, checklist 5); with <paramref name="remove"/> the item is deleted instead (the record leaves
    /// the queue). The worker is any user: the routing service of decision 4 picks on behalf of agents.
    /// </summary>
    /// <exception cref="XrmdFaultException">404 for a missing item or user; 400 for an inactive item (from memory).</exception>
    private async Task PickAsync(DataSession s, Writer writer, Guid itemId, Guid worker, bool remove, CancellationToken ct)
    {
        var item = await _core.RetrieveRecordAsync(s, Caller.Internal, Item, itemId, Columns(StateCode), ct);
        await _core.RetrieveRecordAsync(s, Caller.Internal, SystemUser, worker, Columns(), ct);
        if (item.GetValue<ChoiceValue>(StateCode)?.Value is { } state && state != 0)
            throw XrmdFaultException.InvalidArgument($"The queue item {itemId} cannot be picked: it is inactive.");
        if (remove)
        {
            await writer.DeleteAsync(itemId, ct);
            return;
        }
        await writer.UpdateAsync(new Record(Item, itemId)
        {
            [QueueSchema.WorkerId] = Reference(SystemUser, worker), [QueueSchema.WorkerIdModifiedOn] = DateTime.UtcNow,
        }, ct);
    }

    /// <summary>ReleaseToQueue: the item's <c>workerid</c> is cleared, so that anyone may pick it; it stays in its queue.</summary>
    /// <exception cref="XrmdFaultException">404 for a missing item.</exception>
    private async Task ReleaseAsync(DataSession s, Writer writer, Guid itemId, CancellationToken ct)
    {
        await _core.RetrieveRecordAsync(s, Caller.Internal, Item, itemId, Columns(), ct);
        await writer.UpdateAsync(new Record(Item, itemId) { [QueueSchema.WorkerId] = null }, ct);
    }

    /// <summary>
    /// RetrieveUserQueues: the private queues of <paramref name="user"/> (those it is a member of, its default queue among them, and
    /// the default queues of the teams it is in, whose members are resolved here: from memory), and with
    /// <paramref name="includePublic"/> every public queue; all columns, by name. The query runs as <paramref name="caller"/>, so only
    /// the queues the caller can read come back (the queue privacy of the read security, ruling R13).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 for a missing user; the faults of the query (403 without Read Queue).</exception>
    private async Task<RecordCollection> UserQueuesAsync(DataSession s, Caller caller, Guid user, bool includePublic, CancellationToken ct)
    {
        await _core.RetrieveRecordAsync(s, Caller.Internal, SystemUser, user, Columns(), ct);
        var mine = await GuidsAsync(s,
            $"SELECT m.{s.Quote(QueueId)} FROM {s.DataTable(QueueSchema.QueueMembership)} m WHERE m.systemuserid = @id " +
            $"UNION SELECT t.{s.Quote(QueueId)} FROM {s.DataTable("team")} t JOIN {s.DataTable("teammembership")} tm ON tm.teamid = t.teamid " +
            $"WHERE tm.systemuserid = @id AND t.{s.Quote(QueueId)} IS NOT NULL", user, ct);
        // The query the SDK body built as a QueryExpression (all columns, by name; private member queues or public ones).
        var query = new RecordQuery
        {
            EntityName = QueueTable, Columns = new ColumnSelection { AllColumns = true },
            Orders = { new QueryOrder { AttributeName = "name", Direction = SortDirection.Ascending } },
            Criteria = new QueryFilter { Logic = FilterLogic.Or },
        };
        if (mine.Count > 0)
            query.Criteria.Filters.Add(new QueryFilter
            {
                Logic = FilterLogic.And,
                Conditions =
                {
                    Condition(QueueViewType, FilterOperator.Equal, QueueSchema.PrivateViewType),
                    Condition(QueueId, FilterOperator.In, [.. mine.Distinct().Cast<object?>()]),
                },
            });
        if (includePublic) query.Criteria.Conditions.Add(Condition(QueueViewType, FilterOperator.Equal, QueueSchema.PublicViewType));
        if (mine.Count == 0 && !includePublic) return new RecordCollection { EntityName = QueueTable };
        try
        {
            return await _core.RetrieveRecordsAsync(s, caller, QueryExpressionReader.Read(query), ct);
        }
        catch (XrmdValidationException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
    }

    private static QueryCondition Condition(string column, FilterOperator op, params object?[] values) =>
        new() { AttributeName = column, Operator = op, Values = [.. values] };

    /// <summary>
    /// Ruling R15 on the Update of a <c>queueitem</c> (direct, or nested in a message): an item that becomes active again, or that an
    /// active item now points to another record, takes the record's lock and is refused while the record has another active item,
    /// unless the record is a received email. An Update that keeps the item's state and record (a move between queues, a worker)
    /// checks nothing.
    /// </summary>
    /// <param name="row">The values about to be written, with <c>statecode</c> completed from <c>statuscode</c>.</param>
    /// <exception cref="XrmdFaultException">400 when the record already has another active item; 404 for a missing item or record.</exception>
    internal async Task DemandItemUpdateAllowedAsync(DataSession s, Guid itemId, Record row, CancellationToken ct)
    {
        if (!row.Contains(StateCode) && !row.Contains(QueueSchema.ObjectId)) return;
        if (row.Contains(StateCode) && row.GetValue<ChoiceValue>(StateCode)?.Value is { } written && written != 0) return;
        var stored = await _core.RetrieveRecordAsync(s, Caller.Internal, Item, itemId, Columns(StateCode, QueueSchema.ObjectId), ct);
        var storedState = stored.GetValue<ChoiceValue>(StateCode)?.Value ?? 0;
        var storedRecord = stored.GetValue<RecordReference>(QueueSchema.ObjectId);
        var state = row.Contains(StateCode) ? row.GetValue<ChoiceValue>(StateCode)?.Value ?? 0 : storedState;
        var record = row.Contains(QueueSchema.ObjectId) ? row.GetValue<RecordReference>(QueueSchema.ObjectId) : storedRecord;
        if (state != 0 || record is null || (storedState == 0 && storedRecord?.Id == record.Id)) return;
        if (_store.GetTable(record.LogicalName ?? "") is not { } table) return;
        var locked = await LockRecordAsync(s, table, record.Id, ct);
        if (IsReceivedEmail(table, locked.Status)) return;
        if ((await ActiveItemsAsync(s, record.Id, ct)).Any(i => i.Id != itemId))
            throw XrmdFaultException.InvalidArgument(
                $"The {table.LogicalName} {record.Id} already has an active queue item: the queue item {itemId} cannot be active too. " +
                "A record is in one queue at a time.");
    }

    /// <summary>
    /// A queue-enabled record that leaves the active state (completed, cancelled; any <c>statecode</c> but 0) deactivates its active
    /// items, in the transaction of its Update (the core calls this next to the activity rows). A received email keeps them: it may
    /// stay in its queues. Reactivating the record later leaves the items inactive (from memory, checklist 6). The items are written
    /// by the core Update as <paramref name="caller"/>: no queue item step fires and no privilege on the item is checked.
    /// </summary>
    internal async Task DeactivateItemsAsync(DataSession s, Caller caller, TableDefinition table, Guid record, int? status, CancellationToken ct)
    {
        if (IsReceivedEmail(table, status)) return;
        foreach (var (item, _) in await ActiveItemsAsync(s, record, ct))
            await _core.UpdateRecordAsync(s, caller, new Record(Item, item)
            {
                [StateCode] = new ChoiceValue(1), [StatusCode] = new ChoiceValue(QueueSchema.InactiveItemStatus),
            }, ct);
    }

    /// <summary>The ids of the active items of <paramref name="record"/>, oldest first: those its deactivation would write.</summary>
    internal static async Task<IReadOnlyList<Guid>> ActiveItemIdsAsync(DataSession s, Guid record, CancellationToken ct) =>
        [.. (await ActiveItemsAsync(s, record, ct)).Select(i => i.Id)];

    /// <summary>
    /// The items an email's SendEmail deactivated, made active again when its send failed (#333; that Dataverse puts them back is
    /// from memory, plan 4.1c checklist 14). Each item is written by the core Update as <paramref name="caller"/> (the sender, as the
    /// deactivation): no queue item step fires and no privilege is checked. Its queue and worker are kept. An item is left as it is
    /// when it no longer exists, is already active, its queue is inactive, or its record already has another active item (ruling
    /// R15, checked per item under the record's lock; a received email excepted).
    /// </summary>
    /// <returns>The number of items made active.</returns>
    internal async Task<int> ReactivateItemsAsync(DataSession s, Caller caller, IReadOnlyList<Guid> items, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(items);
        var reactivated = 0;
        foreach (var item in items.Distinct())
        {
            Guid? queue = null;
            await using (var cmd = s.Command(
                             $"SELECT {StateCode}, {s.Quote(QueueId)} FROM {s.DataTable(Item)} WHERE queueitemid = @id"))
            {
                cmd.AddParameter("@id", DbType.Guid, item);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct) || rd.IsDBNull(0)
                    || Convert.ToInt32(rd.GetValue(0), System.Globalization.CultureInfo.InvariantCulture) == 0) continue;
                if (!rd.IsDBNull(1)) queue = rd.GetGuid(1);
            }
            if (queue is not { } queueId || !await IsActiveQueueAsync(s, queueId, ct)) continue;
            var stored = await _core.RetrieveRecordAsync(s, Caller.Internal, Item, item, Columns(QueueSchema.ObjectId), ct);
            if (stored.GetValue<RecordReference>(QueueSchema.ObjectId) is not { } record
                || _store.GetTable(record.LogicalName ?? "") is not { } table) continue;
            var locked = await LockRecordAsync(s, table, record.Id, ct);
            if (!IsReceivedEmail(table, locked.Status) && (await ActiveItemsAsync(s, record.Id, ct)).Any(i => i.Id != item)) continue;
            await _core.UpdateRecordAsync(s, caller, new Record(Item, item)
            {
                [StateCode] = new ChoiceValue(0), [StatusCode] = new ChoiceValue(ActiveItemStatus),
            }, ct);
            reactivated++;
        }
        return reactivated;
    }

    /// <summary>The Delete of a queue (plan 4.1a): refused while the queue holds active items, which must be moved or deactivated first.</summary>
    /// <exception cref="XrmdFaultException">400 when an active item is in the queue.</exception>
    internal static async Task DemandNoActiveItemsAsync(DataSession s, Guid queue, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT COUNT(*) FROM {s.DataTable(Item)} WHERE {s.Quote(QueueId)} = @id AND {StateCode} = 0");
        cmd.AddParameter("@id", DbType.Guid, queue);
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) > 0)
            throw XrmdFaultException.InvalidArgument(
                $"The queue {queue} cannot be deleted: it still holds active queue items. Move, remove or deactivate them first.");
    }

    /// <summary>The default queue of a user or team.</summary>
    /// <exception cref="XrmdFaultException">404 for a missing principal; 400 when it has no default queue.</exception>
    private async Task<Guid> DefaultQueueAsync(DataSession s, RecordReference principal, CancellationToken ct)
    {
        var row = await _core.RetrieveRecordAsync(s, Caller.Internal, principal.LogicalName, principal.Id, Columns(QueueId), ct);
        return row.GetValue<RecordReference>(QueueId)?.Id
               ?? throw XrmdFaultException.InvalidArgument($"The {principal.LogicalName} {principal.Id} has no default queue to route to.");
    }

    /// <summary>True when the queue exists and is active.</summary>
    private static async Task<bool> IsActiveQueueAsync(DataSession s, Guid queue, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT {StateCode} FROM {s.DataTable(QueueTable)} WHERE {s.Quote(QueueId)} = @id");
        cmd.AddParameter("@id", DbType.Guid, queue);
        var state = await cmd.ExecuteScalarAsync(ct);
        return state is not null && (state is DBNull || Convert.ToInt32(state, System.Globalization.CultureInfo.InvariantCulture) == 0);
    }

    /// <summary>The queue must exist (404) and be active (400).</summary>
    private static async Task DemandActiveQueueAsync(DataSession s, Guid queue, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT {StateCode} FROM {s.DataTable(QueueTable)} WHERE {s.Quote(QueueId)} = @id");
        cmd.AddParameter("@id", DbType.Guid, queue);
        var state = await cmd.ExecuteScalarAsync(ct);
        if (state is null) throw XrmdFaultException.ObjectDoesNotExist(QueueTable, queue);
        if (state is not DBNull && Convert.ToInt32(state, System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw XrmdFaultException.InvalidArgument($"The queue {queue} is inactive: items cannot be placed in it.");
    }

    /// <summary>The record's state, status and primary name, read under an update lock held until commit.</summary>
    /// <exception cref="XrmdFaultException">404 when the record does not exist.</exception>
    private static async Task<(int? State, int? Status, string? Name)> LockRecordAsync(DataSession s, TableDefinition table,
        Guid id, CancellationToken ct)
    {
        var known = (table.Attributes ?? []).Select(a => a.LogicalName).ToHashSet(StringComparer.Ordinal);
        string Column(string? name) => name is not null && known.Contains(name) ? s.Quote(name) : "NULL";
        await using var cmd = s.Command(
            $"SELECT {Column(StateCode)}, {Column(StatusCode)}, {Column(table.PrimaryNameAttribute)} " +
            $"FROM {s.DataTable(table.LogicalName!)}{s.Dialect.LockHint(RowLock.UpdateRow)} " +
            $"WHERE {s.Quote(SystemAttributes.PrimaryIdName(table))} = @id{s.Dialect.LockClause(RowLock.UpdateRow)}");
        cmd.AddParameter("@id", DbType.Guid, id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw XrmdFaultException.ObjectDoesNotExist(table.LogicalName!, id);
        return (rd.IsDBNull(0) ? null : Convert.ToInt32(rd.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
            rd.IsDBNull(1) ? null : Convert.ToInt32(rd.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
            rd.IsDBNull(2) ? null : Convert.ToString(rd.GetValue(2), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The active items of the record, as (item, queue), oldest first.</summary>
    private static async Task<List<(Guid Id, Guid Queue)>> ActiveItemsAsync(DataSession s, Guid record, CancellationToken ct)
    {
        await using var cmd = s.Command(
            $"SELECT queueitemid, {s.Quote(QueueId)} FROM {s.DataTable(Item)} " +
            $"WHERE {s.Quote(QueueSchema.ObjectId)} = @id AND {StateCode} = 0 ORDER BY createdon, queueitemid");
        cmd.AddParameter("@id", DbType.Guid, record);
        var items = new List<(Guid, Guid)>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) items.Add((rd.GetGuid(0), rd.IsDBNull(1) ? Guid.Empty : rd.GetGuid(1)));
        return items;
    }

    private static async Task<List<Guid>> GuidsAsync(DataSession s, string sql, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command(sql);
        cmd.AddParameter("@id", DbType.Guid, id);
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) ids.Add(rd.GetGuid(0));
        return ids;
    }

    /// <summary>The columns of <c>QueueItemProperties</c> without those the message sets itself.</summary>
    private static Record Properties(Record? properties)
    {
        var extra = new Record(Item);
        foreach (var (name, value) in properties?.Attributes ?? [])
            if (!ServerColumns.Contains(name)) extra[name] = value;
        return extra;
    }

    private static Record With(Record record, Record extra)
    {
        foreach (var (name, value) in extra.Attributes) record[name] = value;
        return record;
    }

    private static RecordReference Reference(string table, Guid id) => new() { LogicalName = table, Id = id };

    /// <summary>The columns of a read (none: the primary key only, as the SDK <c>ColumnSet(false)</c>).</summary>
    private static ColumnSelection Columns(params string[] columns) => new() { Columns = [.. columns] };

    /// <summary>
    /// The writes of a message: through the pipeline when it runs one (steps fire, the guard checks), else directly. A nested write is a
    /// model request with the parameters of today's SDK request, in its order; the core's entry adapter pairs it with that request class.
    /// </summary>
    private sealed class Writer(CoreOperations core, DataSession s, Caller caller, NestedRequest? nested)
    {
        public async Task<Guid> CreateAsync(Record record, CancellationToken ct)
        {
            if (nested is null) return await core.CreateRecordAsync(s, caller, record, ct);
            var request = new Request { Name = "Create", Parameters = { ["Target"] = record } };
            return (Guid)CoreSdkBridges.NestedResponse(request, await nested(request, ct)).Results["id"]!;
        }

        public Task UpdateAsync(Record record, CancellationToken ct) => nested is null
            ? core.UpdateRecordAsync(s, caller, record, ct)
            : nested(new Request { Name = "Update", Parameters = { ["Target"] = record } }, ct);

        public Task DeleteAsync(Guid id, CancellationToken ct) => nested is null
            ? core.DeleteRecordAsync(s, caller, Item, id, null, ct)
            : nested(new Request { Name = "Delete", Parameters = { ["Target"] = Reference(Item, id) } }, ct);

        public Task AssociateAsync(RecordReference queue, RecordReferenceCollection users, CancellationToken ct)
        {
            var relationship = new RelationshipName(QueueSchema.MembershipRelationshipName, null);
            return nested is null
                ? core.AssociateRecordsAsync(s, caller, queue, relationship, users, associate: true, ct)
                : nested(new Request
                {
                    Name = "Associate",
                    Parameters = { ["Target"] = queue, ["Relationship"] = relationship, ["RelatedEntities"] = users },
                }, ct);
        }
    }
}

using System.Data.Common;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline.Async;
using Xrmd.Query.Model;
using static Xrmd.Metadata.DuplicateDetectionSchema;

namespace Xrmd.Core.Duplicates;

/// <summary>
/// <c>BulkDetectDuplicates</c> (plan 2.15, ruling D15): <see cref="EnqueueAsync"/> checks the request and queues a system job
/// (<c>asyncoperation.operationtype</c> 8, <see cref="AsyncOperationQueue.BulkDuplicateDetection"/>) whose <c>data</c> is the
/// query (<see cref="AsyncContextSerializer.SerializeQuery"/>); <see cref="RunAsync"/> runs it for the
/// <see cref="AsyncOperationWorker"/>. The job reads the query's rows as its owner through the query path, page by page (record
/// and field security apply: a row the owner cannot read is not examined), and for each row and each Published rule of its
/// table with at least one match (any row of the matching table counts, D13) writes one <c>duplicaterecord</c>
/// (<c>baserecordid</c> = the row, <c>duplicateruleid</c>, <c>asyncoperationid</c> = the job, owned by the job's owner,
/// <c>duplicaterecordid</c> empty as in Learn's sample), at most <see cref="RecordLimit"/> of them (reaching it is said in the job's <c>message</c>). Each page's records are
/// written in a transaction of their own.
/// </summary>
public sealed class BulkDetectDuplicatesJob(IDbConnectionFactory factory, IMetadataStore store, CoreOperations core, DuplicateRuleCache cache)
    : IAsyncOperationHandler
{
    /// <summary>The message name.</summary>
    public const string Message = "BulkDetectDuplicates";

    /// <summary>The rows the job reads per page: the largest page of the query path, lowered by tests only.</summary>
    internal static int PageSize { get; set; } = MaxPageSize;

    /// <summary>
    /// The most <c>duplicaterecord</c> rows a job writes: <see cref="MaxBulkRecords"/>, lowered by tests only. It counts rows
    /// written, not base records: a record matched under two rules counts twice. A job that reaches it ends 3/30 with
    /// <see cref="LimitReachedMessage"/> as its <c>message</c>.
    /// </summary>
    internal static int RecordLimit { get; set; } = MaxBulkRecords;

    /// <summary>The <c>message</c> of a job that stopped at <paramref name="limit"/> records.</summary>
    internal static string LimitReachedMessage(int limit) =>
        $"Stopped after {limit} duplicate records, the most one job writes: rows after that point were not examined. Narrow the query to see the rest.";

    /// <inheritdoc />
    public int OperationType => AsyncOperationQueue.BulkDuplicateDetection;

    /// <summary>
    /// Checks <paramref name="request"/> and queues its job in <paramref name="s"/>, owned by <paramref name="caller"/> (the job
    /// runs as its owner), named <c>JobName</c>, with <c>primaryentitytype</c> = the query's table; the worker is woken after the
    /// commit. Refused (400): a missing <c>Query</c>; an aggregate query; <c>SendEmailNotification</c> true (xrmverse sends no
    /// email); a non-empty <c>RecurrencePattern</c> (no recurring jobs); a table without <c>IsDuplicateDetectionEnabled</c> or
    /// without a Published rule as base. <c>TemplateId</c>, <c>ToRecipients</c>, <c>CCRecipients</c> and
    /// <c>RecurrenceStartTime</c> are accepted and ignored.
    /// </summary>
    /// <returns>The <c>asyncoperationid</c> of the job.</returns>
    /// <exception cref="XrmdFaultException">400 InvalidArgument for each refusal.</exception>
    public static async Task<Guid> EnqueueAsync(DataSession s, Caller caller, OrganizationRequest request, IMetadataStore store,
        DuplicateRuleCache cache, AsyncOperationQueue queue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(queue);
        var parameters = request.Parameters;
        if (!parameters.TryGetValue("Query", out var given) || given is not QueryBase query)
            throw XrmdFaultException.InvalidArgument($"Required parameter 'Query' of request '{Message}' is missing.");
        if (parameters.TryGetValue("SendEmailNotification", out var email) && email is true)
            throw XrmdFaultException.InvalidArgument(
                $"{Message} with SendEmailNotification is not supported: xrmverse sends no email. Set it to false.");
        if (parameters.TryGetValue("RecurrencePattern", out var recurrence) && recurrence is string { Length: > 0 })
            throw XrmdFaultException.InvalidArgument(
                $"{Message} with a RecurrencePattern is not supported: xrmverse runs no recurring system job. Leave it empty.");

        var model = CoreOperations.ReadQuery(query);
        if (model.Aggregate)
            throw XrmdFaultException.InvalidArgument($"The query of {Message} cannot be an aggregate query: the job needs the rows.");
        var entity = model.Root.EntityName;
        var m = store.GetEntity(entity)
                ?? throw XrmdFaultException.InvalidArgument($"The entity with a name = '{entity}' was not found in the MetadataCache.");
        if (m.IsDuplicateDetectionEnabled?.Value != true)
            throw XrmdFaultException.InvalidArgument(
                $"The table '{entity}' does not have IsDuplicateDetectionEnabled: turn duplicate detection on for the table before running {Message} on it.");
        await cache.EnsureLoadedAsync(s, ct);
        if (!cache.ForBase(entity).Any(r => r.StatusCode == RuleStatus.Published))
            throw XrmdFaultException.InvalidArgument(
                $"The table '{entity}' has no published duplicate detection rule: publish one before running {Message} on it.");

        var name = parameters.TryGetValue("JobName", out var jobName) && jobName is string { Length: > 0 } text ? text : Message;
        var job = await queue.EnqueueSystemJobAsync(s, caller, AsyncOperationQueue.BulkDuplicateDetection, name, Message, entity, null,
            AsyncContextSerializer.SerializeQuery(query), ct);
        s.OnCommitted(queue.Signal);
        return job;
    }

    /// <inheritdoc />
    /// <exception cref="XrmdFaultException">400 when the job has no query; the faults of the query.</exception>
    public async Task RunAsync(AsyncOperationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrEmpty(run.Data)) throw XrmdFaultException.InvalidArgument($"The system job of {Message} has no query (data).");
        var model = CoreOperations.ReadQuery(AsyncContextSerializer.DeserializeQuery(run.Data));
        var m = store.GetEntity(model.Root.EntityName)
                ?? throw XrmdFaultException.InvalidArgument($"The entity with a name = '{model.Root.EntityName}' was not found in the MetadataCache.");
        var pk = SystemAttributes.PrimaryIdName(m);

        IReadOnlyList<PublishedRule> rules;
        await using (var conn = await factory.OpenAsync(ct))
        {
            await cache.EnsureLoadedAsync(new DataSession(conn, null), ct);
            rules = cache.ForBase(m.LogicalName).Where(r => r.StatusCode == RuleStatus.Published).ToList();
        }
        if (rules.Count == 0) return;

        // Only the key and the columns the rules match on are read, from the first page whatever the client asked.
        model.Root.AllColumns = false;
        model.Root.Columns.Clear();
        model.Root.Columns.Add(new ColumnNode(pk));
        foreach (var attribute in rules.SelectMany(r => r.Conditions).Select(c => c.BaseAttribute).Distinct(StringComparer.Ordinal))
            if (attribute != pk) model.Root.Columns.Add(new ColumnNode(attribute));
        // Ordered by the key when the query has no order, so the pages follow each other on the paging cookie (keyset).
        if (model.Root.Orders.Count == 0 && model.Root.Links.All(l => l.Orders.Count == 0)) model.Root.Orders.Add(new OrderNode(pk, Descending: false));
        model.PageSize = PageSize;
        model.PageNumber = 1;
        model.PagingCookie = null;
        model.ReturnTotalRecordCount = false;

        var written = 0;
        var examined = new HashSet<Guid>(); // a one-to-many link repeats a row: it is examined once
        while (written < RecordLimit)
        {
            EntityCollection page;
            var found = new List<(Guid Row, Guid Rule)>();
            await using (var conn = await factory.OpenAsync(ct))
            {
                var session = new DataSession(conn, null);
                page = await core.RetrieveMultipleAsync(session, run.Owner, model, ct);
                foreach (var row in page.Entities)
                {
                    if (!examined.Add(row.Id)) continue;
                    Func<string, object?> values = name => row.Attributes.TryGetValue(name, out var v) ? v : null;
                    foreach (var rule in rules)
                    {
                        if (written + found.Count >= RecordLimit) break;
                        if (await core.DuplicateFinder.HasDuplicateAsync(session, rule, values, row.Id, ct)) found.Add((row.Id, rule.Id));
                    }
                    if (written + found.Count >= RecordLimit) break;
                }
            }
            if (found.Count > 0) await WriteAsync(run, m.LogicalName, found, ct);
            written += found.Count;
            if (written >= RecordLimit)
            {
                run.CompletionMessage = LimitReachedMessage(RecordLimit);
                break;
            }
            if (!page.MoreRecords || model.Top is not null) break;
            model.PageNumber++;
            model.PagingCookie = page.PagingCookie;
        }
    }

    /// <summary>Writes the <c>duplicaterecord</c> rows of one page in a transaction of their own, as the job's owner.</summary>
    private async Task WriteAsync(AsyncOperationRun run, string entity, IReadOnlyList<(Guid Row, Guid Rule)> found, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        var session = new DataSession(conn, await conn.BeginTransactionAsync(ct));
        try
        {
            foreach (var (row, rule) in found)
                await core.CreateAsync(session, run.Owner, new Entity(Record)
                {
                    [BaseRecordId] = new EntityReference(entity, row),
                    [DuplicateRuleId] = new EntityReference(DuplicateDetectionSchema.Rule, rule),
                    [AsyncOperationId] = new EntityReference("asyncoperation", run.AsyncOperationId),
                    ["ownerid"] = new EntityReference("systemuser", run.Owner.UserId),
                }, ct);
            await session.Transaction!.CommitAsync(ct);
            session.Committed();
        }
        catch
        {
            try
            {
                await session.Transaction!.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                // The connection is broken: nothing was committed.
            }
            finally
            {
                session.RolledBack();
            }
            throw;
        }
    }
}

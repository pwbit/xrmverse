using System.Data;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Query;
using Xrmd.Query.Model;
using Xrmd.Solution;

namespace Xrmd.Core;

/// <summary>
/// S.2e3c1 (Task 6, Decisions 1 and 5): the messages answered outside the pipeline whose bodies the core owns (WhoAmI,
/// RetrieveAppComponents, RetrieveUnpublished, InitializeFrom, RetrieveFormattedImportJobResults), the refusals of the caller-only and
/// metadata-write messages from a plug-in, and the lookups the pipeline asks the core for (<see cref="LockExistsAsync"/>, the image read
/// of <c>RetrieveImageAsync</c>), on the model request and <see cref="Record"/>s. Each read runs in the caller's session, so a plug-in's
/// call sees its uncommitted rows. The members other modules still call with SDK requests keep SDK twins of the same names
/// (<c>Sdk/CoreOperations.Records.Sdk.cs</c>).
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>
    /// True for a message of <see cref="ExecuteNonPipelineReadAsync"/>: one of the reads above, or a message refused from a plug-in (a
    /// caller-only solution message, a metadata write, ImportSolution). Canonical case.
    /// </summary>
    private static bool IsNonPipelineRead(string canonicalName) =>
        canonicalName is "WhoAmI" or RetrieveAppComponents or RetrieveUnpublished or InitializeFrom.MessageName
            or "RetrieveFormattedImportJobResults" or "ImportSolution"
        || MetadataOperations.IsWriteMessage(canonicalName) || CallerOnlyMessages.Contains(canonicalName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A message of <see cref="IsNonPipelineRead"/> on the model request, named <paramref name="canonicalName"/>, in the caller's
    /// <paramref name="session"/>. The refusals keep today's texts and come in today's order (a metadata write or ImportSolution, then a
    /// caller-only message).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a refused message, a missing parameter; the faults of the reads.</exception>
    private async Task<Response> ExecuteNonPipelineReadAsync(Request request, string canonicalName, Caller caller, DataSession session,
        CancellationToken ct)
    {
        switch (canonicalName)
        {
            case "WhoAmI":
                return WhoAmIOf(caller, (await OrganizationAsync(session, ct)).Id);
            case RetrieveAppComponents:
                return await RetrieveAppComponentsAsync(request, caller, session, ct);
            case RetrieveUnpublished:
                return await RetrieveUnpublishedAsync(request, caller, session, ct);
            case InitializeFrom.MessageName:
                return await InitializeFromAsync(request, caller, session, ct);
            case "RetrieveFormattedImportJobResults":
                return await RetrieveFormattedImportJobResultsAsync(request, caller, session, ct);
            case var name when MetadataOperations.IsWriteMessage(name) || name == "ImportSolution":
                // A plug-in's transaction has already written data.* rows, and metadata is always written before data in a transaction
                // (the order that keeps the metadata lock and the SQL locks from deadlocking).
                throw XrmdFaultException.InvalidArgument($"The metadata message '{request.Name}' cannot be executed from a plug-in.");
            default:
                // Not replicated: xrmverse queues solution import jobs and exports solutions only for the organization service's callers.
                throw XrmdFaultException.InvalidArgument($"The message '{request.Name}' cannot be executed from a plug-in in xrmverse.");
        }
    }

    /// <summary>The model response of WhoAmI for <paramref name="caller"/> in organization <paramref name="organizationId"/>.</summary>
    internal static Response WhoAmIOf(Caller caller, Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return new Response
        {
            Name = "WhoAmI",
            Results =
            {
                ["UserId"] = caller.UserId,
                ["BusinessUnitId"] = caller.BusinessUnitId,
                ["OrganizationId"] = organizationId,
            },
        };
    }

    /// <summary>
    /// RetrieveFormattedImportJobResults in <paramref name="session"/>: the <c>data</c> of the <c>importjob</c> (the import log)
    /// read as a Retrieve of <paramref name="caller"/> (read privilege and access on <c>importjob</c>; 404 when it does not
    /// exist), as an Excel workbook (<see cref="ImportJobLog.ToSpreadsheet"/>).
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>ImportJobId</c>; 404 for an unknown import job; the Retrieve's faults.</exception>
    public async Task<Response> RetrieveFormattedImportJobResultsAsync(Request request, Caller caller, DataSession session, CancellationToken ct)
    {
        var job = await RetrieveRecordAsync(session, caller, "importjob", ImportJobIdParameter(request), Columns("data"), ct);
        return FormattedImportJobResults(job);
    }

    /// <summary>The <c>ImportJobId</c> of a RetrieveFormattedImportJobResults request.</summary>
    /// <exception cref="XrmdFaultException">400 when it is missing or empty.</exception>
    public static Guid ImportJobIdParameter(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Parameters.TryGetValue("ImportJobId", out var value) && value is Guid id && id != Guid.Empty
            ? id
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'ImportJobId' of request '{request.Name}' is missing.");
    }

    /// <summary>The response of RetrieveFormattedImportJobResults for the retrieved <c>importjob</c> <paramref name="job"/>.</summary>
    public static Response FormattedImportJobResults(Record job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new Response
        {
            Name = "RetrieveFormattedImportJobResults",
            Results = { ["FormattedResults"] = ImportJobLog.ToSpreadsheet(job.GetValue<string>("data")) },
        };
    }

    /// <summary>
    /// InitializeFrom (#143): the values a new record of <c>TargetEntityName</c> starts with, taken from the record
    /// <c>EntityMoniker</c> through the maps from its table to the target table: the lookup of each one-to-many relationship between
    /// them set to the source record (unless <c>SkipParentalRelationshipMapping</c>; derived, see
    /// <see cref="InitializeFrom.ParentalRelationships"/>), and the imported <c>attributemap</c> rows, each copying a value that is not
    /// null. Only the target columns that <c>TargetFieldType</c> keeps are set; a map whose column no longer exists is skipped. The
    /// source is read as a Retrieve of <paramref name="caller"/> (403 without read access, 404 when it does not exist, field security
    /// applies); the maps are read as <see cref="Caller.Internal"/>. Runs outside the pipeline.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for a missing parameter, an unknown table or a pair of tables without a map; the
    /// Retrieve's faults.</exception>
    public async Task<Response> InitializeFromAsync(Request request, Caller caller, DataSession session, CancellationToken ct)
    {
        var parameters = InitializeFrom.Parameters(request);
        var source = TableOf(parameters.Source.LogicalName);
        var target = TableOf(parameters.Target);
        var relationships = InitializeFrom.ParentalRelationships(store, source.LogicalName!, target.LogicalName!);

        var mapQuery = new RecordQuery { EntityName = AttributeMapSchema.EntityMap, Columns = Columns(AttributeMapSchema.SourceEntity) };
        mapQuery.Criteria!.Conditions.Add(Condition(AttributeMapSchema.SourceEntity, FilterOperator.Equal, source.LogicalName));
        mapQuery.Criteria.Conditions.Add(Condition(AttributeMapSchema.TargetEntity, FilterOperator.Equal, target.LogicalName));
        var mapIds = (await RetrieveRecordsAsync(session, Caller.Internal, ReadQuery(mapQuery), ct)).Records.Select(e => (object?)e.Id).ToArray();
        if (relationships.Count == 0 && mapIds.Length == 0)
            throw XrmdFaultException.InvalidArgument($"No map exists from table '{source.LogicalName}' to table '{target.LogicalName}'.");
        var maps = new List<(Model.Metadata.ColumnDefinition From, Model.Metadata.ColumnDefinition To)>();
        if (mapIds.Length > 0)
        {
            var attributeQuery = new RecordQuery
            {
                EntityName = AttributeMapSchema.AttributeMap,
                Columns = Columns(AttributeMapSchema.SourceAttribute, AttributeMapSchema.TargetAttribute),
            };
            attributeQuery.Criteria!.Conditions.Add(Condition(AttributeMapSchema.EntityMapLookup, FilterOperator.In, mapIds));
            foreach (var row in (await RetrieveRecordsAsync(session, Caller.Internal, ReadQuery(attributeQuery), ct)).Records)
            {
                var from = source.Attributes?.FirstOrDefault(a => a.LogicalName == row.GetValue<string>(AttributeMapSchema.SourceAttribute));
                var to = target.Attributes?.FirstOrDefault(a => a.LogicalName == row.GetValue<string>(AttributeMapSchema.TargetAttribute));
                if (from is not null && to is not null) maps.Add((from, to));
            }
        }

        var columns = maps.Select(m => m.From.LogicalName!).ToList();
        if (source.PrimaryNameAttribute is { } primaryName) columns.Add(primaryName);
        var record = await RetrieveRecordAsync(session, caller, source.LogicalName!, parameters.Source.Id, Columns([.. columns.Distinct()]), ct);

        var result = new Record(target.LogicalName!);
        if (!parameters.SkipParentalRelationshipMapping)
            foreach (var relationship in relationships)
            {
                var lookup = target.Attributes?.FirstOrDefault(a => a.LogicalName == relationship.ReferencingAttribute);
                if (lookup is null || !InitializeFrom.Keeps(lookup, parameters.FieldType)) continue;
                result[lookup.LogicalName!] = new RecordReference
                {
                    LogicalName = source.LogicalName!, Id = parameters.Source.Id,
                    Name = source.PrimaryNameAttribute is { } name ? record.GetValue<string>(name) : null,
                };
            }
        foreach (var (from, to) in maps)
        {
            if (result.Contains(to.LogicalName!) || !InitializeFrom.Keeps(to, parameters.FieldType)) continue;
            if (record.Attributes.TryGetValue(from.LogicalName!, out var value) && value is not null) result[to.LogicalName!] = value;
        }
        return new Response { Name = InitializeFrom.MessageName, Results = { ["Entity"] = result } };
    }

    /// <summary>The tables RetrieveUnpublished supports (Learn's bound-function list, narrowed to the tables xrmverse has).</summary>
    private static readonly string[] UnpublishedTables = ["systemform", "savedquery", "sitemap", "appmodule", "webresource"];

    /// <summary>
    /// RetrieveAppComponents: the <c>appmodulecomponent</c> rows of the app <c>AppModuleId</c> (<c>AppComponents</c>), once
    /// <paramref name="caller"/> has read the <c>appmodule</c> row as a Retrieve would (403 without read access, 404 for an unknown
    /// app). The caller needs no privilege on <c>appmodulecomponent</c>: the rows are read as <see cref="Caller.Internal"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>AppModuleId</c>; the Retrieve's and the query's faults.</exception>
    public async Task<Response> RetrieveAppComponentsAsync(Request request, Caller caller, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var appId = request.Parameters.TryGetValue("AppModuleId", out var value) && value is Guid id && id != Guid.Empty
            ? id
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'AppModuleId' of request '{request.Name}' is missing.");
        await RetrieveRecordAsync(session, caller, "appmodule", appId, Columns("appmoduleid"), ct);
        var query = new RecordQuery { EntityName = AppModuleSchema.Component, Columns = new ColumnSelection { AllColumns = true } };
        query.Criteria!.Conditions.Add(Condition("appmoduleidunique", FilterOperator.Equal, appId));
        var components = await RetrieveRecordsAsync(session, Caller.Internal, ReadQuery(query), ct);
        return new Response { Name = RetrieveAppComponents, Results = { ["AppComponents"] = components } };
    }

    /// <summary>
    /// The row and columns of a RetrieveUnpublished request (<c>Target</c>, <c>ColumnSet</c>): xrmverse has no unpublished layer,
    /// so the message is a Retrieve of the published row.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 without <c>Target</c> or <c>ColumnSet</c>, or for a table other than systemform,
    /// savedquery, sitemap, appmodule and webresource.</exception>
    internal static (RecordReference Target, ColumnSelection Columns) UnpublishedTarget(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = request.Parameters.TryGetValue("Target", out var t) && t is RecordReference reference
            ? reference
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'Target' of request '{request.Name}' is missing.");
        var columns = request.Parameters.TryGetValue("ColumnSet", out var c) && c is ColumnSelection selection
            ? selection
            : throw XrmdFaultException.InvalidArgument($"Required parameter 'ColumnSet' of request '{request.Name}' is missing.");
        RequireUnpublishedTable(target.LogicalName);
        return (target, columns);
    }

    /// <summary>The tables RetrieveUnpublished supports, for the Web API metadata.</summary>
    public static IReadOnlyList<string> UnpublishedSupportedTables => UnpublishedTables;

    /// <exception cref="XrmdFaultException">400 for a table RetrieveUnpublished does not support.</exception>
    public static void RequireUnpublishedTable(string logicalName)
    {
        if (!UnpublishedTables.Contains(logicalName, StringComparer.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument(
                $"RetrieveUnpublished is not supported for the table '{logicalName}': it supports {string.Join(", ", UnpublishedTables)}.");
    }

    /// <summary>RetrieveUnpublished in <paramref name="session"/>: the Retrieve of the published row (see <see cref="UnpublishedTarget"/>).</summary>
    public async Task<Response> RetrieveUnpublishedAsync(Request request, Caller caller, DataSession session, CancellationToken ct)
    {
        var (target, columns) = UnpublishedTarget(request);
        var row = await RetrieveRecordAsync(session, caller, target.LogicalName, target.Id, columns, ct);
        return new Response { Name = RetrieveUnpublished, Results = { ["Entity"] = row } };
    }

    /// <inheritdoc />
    public async Task<bool> LockExistsAsync(RecordReference target, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(session);
        if (session.Transaction is null) throw new InvalidOperationException("LockExistsAsync must run inside a transaction.");
        var m = TableOf(target.LogicalName);
        await session.KeyLockAsync(m.LogicalName!, target.Id, ct);
        // Plan 2.7a, Task 6: the key read with the lock clause, so that an existing row is locked on both engines.
        var key = session.Quote(SystemAttributes.PrimaryIdName(m));
        await using var cmd = session.Command(
            $"SELECT {key} FROM {session.DataTable(m.LogicalName!)}{session.Dialect.LockHint(RowLock.UpdateRange)} " +
            $"WHERE {key} = {IdParameter}{session.Dialect.LockClause(RowLock.UpdateRange)}");
        cmd.AddParameter(IdParameter, DbType.Guid, target.Id);
        return await cmd.ExecuteScalarAsync(ct) is not (null or DBNull);
    }

    /// <summary>
    /// The image of <paramref name="target"/> with <paramref name="attributes"/> (null: all; names the table does not have are ignored),
    /// read as <see cref="Caller.Internal"/>; null when the record does not exist. The party lists of an activity have no column: they are
    /// read from <c>activityparty</c> and added to the image (plan 2.11).
    /// </summary>
    private async Task<Record?> ReadImageAsync(RecordReference target, string[]? attributes, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var m = TableOf(target.LogicalName);
        var partyLists = ActivitySchema.PartyLists(m).Select(p => p.Attribute).ToHashSet(StringComparer.Ordinal);
        ColumnSelection columns;
        string[] parties;
        if (attributes is null)
        {
            columns = new ColumnSelection { AllColumns = true };
            parties = [.. partyLists];
        }
        else
        {
            var known = ColumnsOf(m);
            columns = Columns([.. attributes.Where(a => known.ContainsKey(a) && !partyLists.Contains(a))]);
            parties = attributes.Where(partyLists.Contains).ToArray();
        }

        Record image;
        try
        {
            image = await RetrieveRecordAsync(session, Caller.Internal, m.LogicalName!, target.Id, columns, ct);
        }
        catch (XrmdFaultException ex) when (ex.ErrorCode == ErrorCodes.ObjectDoesNotExist)
        {
            return null;
        }
        if (attributes is null)
        {
            // All columns: the read above already filled the party lists that have rows (ActivityRead) and left out the empty
            // ones; they are not read a second time (final review M3), the empty ones are added as empty collections.
            foreach (var name in parties)
                if (!image.Contains(name)) image[name] = new RecordCollection { EntityName = ActivitySchema.Party };
        }
        else if (parties.Length > 0)
            foreach (var (name, list) in (await _parties.ReadAsync(session, m, [target.Id], parties, ct))[target.Id])
                image[name] = list;
        return image;
    }

    /// <summary>A model column selection of <paramref name="columns"/> (none: the key only, as an SDK <c>ColumnSet(false)</c>).</summary>
    private static ColumnSelection Columns(params string[] columns) => new() { Columns = [.. columns] };

    /// <summary>A model condition on <paramref name="column"/>.</summary>
    private static QueryCondition Condition(string column, FilterOperator op, params object?[] values) =>
        new() { AttributeName = column, Operator = op, Values = [.. values] };

    /// <summary>Reads a query the core built into the query model.</summary>
    /// <exception cref="XrmdFaultException">InvalidArgument (400) for an invalid or unsupported query.</exception>
    private static QueryModel ReadQuery(RecordQuery query)
    {
        try
        {
            return QueryExpressionReader.Read(query);
        }
        catch (XrmdValidationException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
    }
}

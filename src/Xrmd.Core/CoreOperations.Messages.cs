using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Core.Duplicates;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Xrmd.Pipeline;
using Xrmd.Query;
using Xrmd.Query.FetchXml;
using Xrmd.Query.Model;
using Xrmd.Solution;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b (Task 8): the eight CRUD messages (Create, Retrieve, Update, Delete, RetrieveMultiple, Assign, Associate, Disassociate)
/// dispatched on the model request: the parameters are read as model values and the model bodies run, with no conversion of the
/// request. The answer is a model <see cref="Response"/> with today's results; the entry adapter (<c>Sdk/CoreOperations.Sdk.cs</c>)
/// pairs it with today's typed SDK response. A helper of another step that is still on the SDK is reached through
/// <see cref="CoreSdkBridges"/>, which converts only when the helper applies.
/// </summary>
public sealed partial class CoreOperations
{
    /// <summary>
    /// Test seam: called with each model request <see cref="ExecuteCrudAsync"/> dispatches (the CRUD messages no longer reach the
    /// adapter's <c>CoreHandOff.HandlerRequest</c> seam, which sees the SDK request of a handler of <c>CoreOperations.cs</c>).
    /// </summary>
    internal static readonly AsyncLocal<Action<Request>?> CrudRequest = new();

    /// <summary>
    /// One of the eight CRUD messages, named <paramref name="canonicalName"/> (the canonical case of <see cref="CoreMessages"/>), on the
    /// model request. The order of the reads and checks is the one of the SDK handlers it replaces, so the same fault comes first.
    /// </summary>
    /// <exception cref="InvalidOperationException">Server values on a message other than Create.</exception>
    /// <exception cref="XrmdFaultException">400 for a missing parameter or one of another type (named with today's SDK type); the
    /// faults of the model bodies.</exception>
    private async Task<Response> ExecuteCrudAsync(Request request, string canonicalName, CoreCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(call);
        if (call.ServerSetValues is not null && canonicalName != "Create")
            throw new InvalidOperationException($"Server values are written on a Create only, not on '{request.Name}'.");
        CrudRequest.Value?.Invoke(request);
        var (caller, session) = (call.Caller, call.Session);
        switch (canonicalName)
        {
            case "Create":
                var created = Parameter<Record>(request, "Target", "Entity");
                var detectOnCreate = DetectDuplicates(request);
                var id = await CreateRecordAsync(session, caller, created, CoreSdkBridges.ClaimRequestMailSecrets(created, request), ct,
                    detectOnCreate, call.ServerSetValues);
                if (ComponentTypeOf(created) is { } componentType)
                    await tracker.TrackAsync(session, componentType, id, SolutionUniqueName(request), ct, markActive: caller != Caller.Internal);
                await MarkParentChangedAsync(session, caller, created.LogicalName, id, null, ct);
                return await WithSessionTokenAsync(session, TableOf(created.LogicalName), id,
                    new Response { Name = "Create", Results = { ["id"] = id } }, ct);
            case "Retrieve":
                UnmaskedData.Refuse(request);
                var target = Parameter<RecordReference>(request, "Target", "EntityReference");
                var columns = request.Parameters.TryGetValue("ColumnSet", out var c) ? c as ColumnSelection : null;
                var record = await RetrieveRecordAsync(session, caller, target.LogicalName, target.Id, columns, ct,
                    CoreSdkBridges.ElasticKeyOf(TableOf(target.LogicalName), target, request));
                return new Response { Name = "Retrieve", Results = { ["Entity"] = record } };
            case "Update":
                var updated = Parameter<Record>(request, "Target", "Entity");
                var updatedTable = TableOf(updated.LogicalName);
                // Plan 3.4: an elastic row is found in its partition first (404), under an update lock.
                await CoreSdkBridges.BeforeElasticUpdateAsync(session, updatedTable, updated, request, ct);
                // SetState runs as an Update the pipeline marks with its own audit action (ruling A6).
                var detectOnUpdate = DetectDuplicates(request);
                await UpdateRecordAsync(session, caller, updated, AuditActionHint.Of(request) ?? AuditActions.Update, null,
                    CoreSdkBridges.ClaimRequestMailSecrets(updated, request), null, ct, detectOnUpdate);
                return await WithSessionTokenAsync(session, updatedTable, UpdateId(updated, updatedTable, SystemAttributes.PrimaryIdName(updatedTable)),
                    new Response { Name = "Update" }, ct);
            case "Delete":
                var deleted = Parameter<RecordReference>(request, "Target", "EntityReference");
                var deletedId = await CoreSdkBridges.BeforeElasticDeleteAsync(session, TableOf(deleted.LogicalName), deleted, request, ct);
                await DeleteRecordAsync(session, caller, deleted.LogicalName, deletedId, call.Nested, ct);
                return new Response { Name = "Delete" };
            case "RetrieveMultiple":
                UnmaskedData.Refuse(request);
                // A front end that built the query model itself passes it along (QueryModelHint); a plug-in that replaced Query wins.
                var model = QueryModelHint.Of(request) ?? ReadQuery(request);
                model = CoreSdkBridges.ApplyElasticRequest(model, request, store);
                var collection = await RetrieveRecordsAsync(session, caller, model, ct);
                return new Response { Name = "RetrieveMultiple", Results = { ["EntityCollection"] = collection } };
            case "Assign":
                var assigned = Parameter<RecordReference>(request, "Target", "EntityReference");
                await AssignRecordAsync(session, caller, assigned, Parameter<RecordReference>(request, "Assignee", "EntityReference"), ct);
                return new Response { Name = "Assign" };
            case "Associate":
            case "Disassociate":
                var associate = canonicalName == "Associate";
                await AssociateRecordsAsync(session, caller, Parameter<RecordReference>(request, "Target", "EntityReference"),
                    Parameter<RelationshipName>(request, "Relationship", "Relationship"),
                    Parameter<RecordReferenceCollection>(request, "RelatedEntities", "EntityReferenceCollection"), associate, ct);
                return new Response { Name = canonicalName };
            default:
                throw new ArgumentOutOfRangeException(nameof(canonicalName), canonicalName, "Not a CRUD message.");
        }
    }

    /// <summary>
    /// A required parameter of the model <paramref name="request"/>; InvalidArgument (400) when missing or of another type, the type
    /// named as the SDK one a caller passes (<paramref name="sdkTypeName"/>), as the SDK handlers said it.
    /// </summary>
    private static T Parameter<T>(Request request, string name, string sdkTypeName) where T : class =>
        request.Parameters.TryGetValue(name, out var value) && value is T typed
            ? typed
            : throw XrmdFaultException.InvalidArgument($"Required parameter '{name}' of request '{request.Name}' is missing or not a {sdkTypeName}.");

    /// <summary>The model <c>DetectDuplicates</c>: true when <c>SuppressDuplicateDetection</c> is false (plan 2.15, ruling D1).</summary>
    /// <exception cref="XrmdFaultException">400 for a value that is not a Boolean.</exception>
    internal static bool DetectDuplicates(Request request)
    {
        if (!request.Parameters.TryGetValue(DuplicateFinder.SuppressParameter, out var value)) return false;
        return value is bool suppress
            ? !suppress
            : throw XrmdFaultException.InvalidArgument(
                $"The parameter '{DuplicateFinder.SuppressParameter}' of request '{request.Name}' must be a Boolean, got '{value ?? "null"}'.");
    }

    /// <summary>The optional <c>SolutionUniqueName</c> parameter (a Create in a solution); null when absent or empty.</summary>
    private static string? SolutionUniqueName(Request request) =>
        request.Parameters.TryGetValue("SolutionUniqueName", out var value) && value is string { Length: > 0 } name ? name : null;

    /// <summary>
    /// The component type of a created record (the model counterpart of the removed SDK <c>ComponentType.OfRow</c>): a <c>workflow</c> row is a component only as a
    /// definition.
    /// </summary>
    private static int? ComponentTypeOf(Record record) =>
        ComponentType.OfTable(record.LogicalName) is { } type
        && (type != ComponentType.Workflow || ComponentType.IsWorkflowDefinition(
            record.Attributes.TryGetValue(WorkflowSchema.TypeColumn, out var value) ? value is ChoiceValue choice ? choice.Value : value : null))
            ? type
            : null;

    /// <summary>
    /// The query of a RetrieveMultiple request, read from its <c>Query</c> parameter: a model query definition, or (through
    /// <see cref="CoreSdkBridges.ReadQuery"/>) any other value, which the SDK reader answers or refuses as today.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 for an invalid or unsupported query, or a missing one.</exception>
    private static QueryModel ReadQuery(Request request)
    {
        var query = request.Parameters.TryGetValue("Query", out var value) ? value : null;
        try
        {
            return query switch
            {
                RecordQuery expression => QueryExpressionReader.Read(expression),
                AttributeQuery byAttribute => QueryExpressionReader.Read(byAttribute),
                FetchQuery fetch => FetchXmlParser.Parse(fetch.Text!),
                _ => CoreSdkBridges.ReadQuery(query, request.Name),
            };
        }
        catch (XrmdValidationException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
    }

    /// <summary>
    /// <paramref name="response"/> with the session token of the write of <paramref name="id"/> on an elastic table
    /// (<see cref="CoreSdkBridges.ElasticSessionTokenAsync"/>); unchanged for a standard table.
    /// </summary>
    private static async Task<Response> WithSessionTokenAsync(DataSession s, Model.Metadata.TableDefinition table,
        Guid id, Response response, CancellationToken ct)
    {
        if (await CoreSdkBridges.ElasticSessionTokenAsync(s, table, id, ct) is { } token) response.Results[Elastic.ElasticRecords.SessionToken] = token;
        return response;
    }
}

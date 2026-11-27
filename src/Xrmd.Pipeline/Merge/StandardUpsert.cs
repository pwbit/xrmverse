using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Pipeline.Merge;

/// <summary>
/// Stage 30 of <c>Upsert</c> on a standard table (#76, ruling B10). Upsert is not an event there: no step runs on the Upsert
/// itself. The record's id is locked in the Upsert's transaction (<see cref="IPipelineCore.LockExistsAsync"/>), then an inner
/// <c>Update</c> runs when the row exists, else an inner <c>Create</c>, each through the whole pipeline (its steps fire and the
/// security guard checks it). An elastic table keeps its own Upsert (plan 3.4, ruling E6), which this class never handles.
/// </summary>
public static class StandardUpsert
{
    private static readonly ConditionalWeakTable<OrganizationRequest, object> CreateOnly = new();

    /// <summary>The create-only marks of the model requests the pipeline runs (S.2e3c2b, Decision 7).</summary>
    private static readonly ConditionalWeakTable<Xrmd.Model.Messages.Request, object> ModelCreateOnly = new();

    /// <summary>The message name.</summary>
    public const string MessageName = "Upsert";

    /// <summary>True when <paramref name="message"/> is <c>Upsert</c> on the standard table <paramref name="table"/>.</summary>
    internal static bool Applies(string? message, EntityMetadata? table) =>
        string.Equals(message, MessageName, StringComparison.OrdinalIgnoreCase) && table is not null && !ElasticTables.IsElastic(table);

    /// <summary>
    /// Creates or updates the record of <paramref name="target"/>, named by <see cref="Entity.Id"/> or its primary key
    /// attribute. The Update target is a copy of <paramref name="target"/> without the primary key attribute; the Create target
    /// keeps the given id (a target without one is created with a new id). Key attributes are left on the inner target, whose
    /// core operation refuses them on a standard table.
    /// </summary>
    /// <param name="createOnly">True when the request was marked by <see cref="MarkCreateOnly(OrganizationRequest)"/>: an existing row is then a 412
    /// DuplicateRecord, raised inside the Upsert's transaction after the lock (no race with a concurrent create).</param>
    /// <param name="runInner">Runs the inner <c>Create</c> or <c>Update</c> through the pipeline as an inner execution.</param>
    /// <returns><c>RecordCreated</c> and <c>Target</c> (a reference to the record).</returns>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">The faults of the inner Create or Update (its plug-ins, its
    /// security check, its core operation).</exception>
    internal static async Task<UpsertResponse> ExecuteAsync(Entity target, EntityMetadata table, IPipelineCore core, DataSession session,
        Func<OrganizationRequest, Task<OrganizationResponse>> runInner, bool createOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(runInner);
        var pk = SystemAttributes.PrimaryIdName(table);
        var id = target.Id != Guid.Empty ? target.Id
            : target.Attributes.TryGetValue(pk, out var given) && given is Guid g ? g
            : Guid.Empty;

        bool created;
        if (id != Guid.Empty && await core.LockExistsAsync(new Xrmd.Model.Records.RecordReference { LogicalName = table.LogicalName, Id = id }, session, ct))
        {
            if (createOnly)
                throw new XrmdFaultException(ErrorCodes.DuplicateRecord,
                    $"A record of '{table.LogicalName}' with the id {id} already exists.", 412);
            await runInner(new UpdateRequest { Target = Copy(target, table, id, without: pk) });
            created = false;
        }
        else
        {
            var response = await runInner(new CreateRequest { Target = Copy(target, table, id, without: null) });
            id = (Guid)response.Results["id"];
            created = true;
        }

        return new UpsertResponse
        {
            ResponseName = MessageName,
            Results = { ["RecordCreated"] = created, ["Target"] = new EntityReference(table.LogicalName, id) },
        };
    }

    /// <summary>
    /// Marks <paramref name="request"/> (an <c>Upsert</c>) as create-only (Web API <c>If-None-Match: *</c>, ruling B11). The mark
    /// lives beside the request, never in its parameters, so a client cannot send it.
    /// </summary>
    public static void MarkCreateOnly(OrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CreateOnly.AddOrUpdate(request, new object());
    }

    /// <summary>True when <paramref name="request"/> was marked by <see cref="MarkCreateOnly(OrganizationRequest)"/>.</summary>
    public static bool IsCreateOnly(OrganizationRequest request) => CreateOnly.TryGetValue(request, out _);

    /// <summary>
    /// Marks the model request <paramref name="request"/> (an <c>Upsert</c>) as create-only (S.2e3c2b, Decision 7): the mark the
    /// pipeline reads at stage 30, beside the request as the SDK one.
    /// </summary>
    public static void MarkCreateOnly(Xrmd.Model.Messages.Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModelCreateOnly.AddOrUpdate(request, new object());
    }

    /// <summary>True when the model request <paramref name="request"/> was marked by <see cref="MarkCreateOnly(Xrmd.Model.Messages.Request)"/>.</summary>
    public static bool IsCreateOnly(Xrmd.Model.Messages.Request request) => ModelCreateOnly.TryGetValue(request, out _);

    /// <summary>
    /// At the crossing of <paramref name="sdk"/> into its model twin <paramref name="model"/> (S.2e3c2b, Decision 7): the create-only
    /// mark of the SDK request, set by its caller (<see cref="MarkCreateOnly(OrganizationRequest)"/>), follows onto the model request.
    /// </summary>
    internal static void CopyCreateOnly(OrganizationRequest sdk, Xrmd.Model.Messages.Request model)
    {
        ArgumentNullException.ThrowIfNull(sdk);
        ArgumentNullException.ThrowIfNull(model);
        if (IsCreateOnly(sdk)) MarkCreateOnly(model);
    }

    /// <summary>
    /// A copy of <paramref name="source"/> with the id <paramref name="id"/>, without the attribute <paramref name="without"/> and
    /// without the values of computed columns: a calculated column (plan 2.12a, ruling K5), a rollup column, its <c>_date</c> and
    /// <c>_state</c> companions and the <c>_base</c> of a Currency rollup (plan 2.12b, ruling R11). They are ignored as a direct
    /// Create or Update ignores them, so they never reach the inner message's plug-ins (its stage 20 runs before the core drops them).
    /// </summary>
    private static Entity Copy(Entity source, EntityMetadata table, Guid id, string? without)
    {
        var attrs = (table.Attributes ?? []).Where(a => a.LogicalName is not null)
            .ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        var copy = new Entity(source.LogicalName, id) { RowVersion = source.RowVersion };
        foreach (var (name, value) in source.Attributes)
            if (!string.Equals(name, without, StringComparison.Ordinal) && !IsComputedValue(attrs.GetValueOrDefault(name), attrs))
                copy[name] = value;
        if (source.KeyAttributes is { Count: > 0 } keys)
            foreach (var (name, value) in keys) copy.KeyAttributes[name] = value;
        // Plan 4.1b: the mail secrets the outer Upsert's stage 20 took out of its Target belong to the inner Create or Update; S.2e3c2b
        // (Decision 6): they are keyed on the model Target, so the pipeline hands them over when the inner request crosses in.
        return copy;
    }

    private static bool IsComputedValue(AttributeMetadata? a, Dictionary<string, AttributeMetadata> attrs) =>
        a is not null && (ComputedColumnRules.IsComputed(a) || RollupColumns.IsRollupValue(a, attrs.GetValueOrDefault));

    /// <summary>
    /// Gives <paramref name="inner"/> the parameters of <paramref name="outer"/> it does not have (e.g.
    /// <c>SuppressDuplicateDetection</c>), never <c>Target</c> or <c>Targets</c> (a bulk message's, #76 Task 3); returns
    /// <paramref name="inner"/>.
    /// </summary>
    internal static OrganizationRequest CarryParameters(OrganizationRequest outer, OrganizationRequest inner)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(inner);
        foreach (var (name, value) in outer.Parameters)
            if (name is not ("Target" or "Targets") && !inner.Parameters.ContainsKey(name)) inner.Parameters[name] = value;
        return inner;
    }
}

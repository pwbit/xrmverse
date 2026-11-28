using System.Runtime.CompilerServices;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Model.Messages;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;

namespace Xrmd.Pipeline.Merge;

/// <summary>
/// Stage 30 of <c>Upsert</c> on a standard table (#76, ruling B10). Upsert is not an event there: no step runs on the Upsert
/// itself. The record's id is locked in the Upsert's transaction (<see cref="IPipelineCore.LockExistsAsync"/>), then an inner
/// <c>Update</c> runs when the row exists, else an inner <c>Create</c>, each through the whole pipeline (its steps fire and the
/// security guard checks it). An elastic table keeps its own Upsert (plan 3.4, ruling E6), which this class never handles.
/// </summary>
/// <remarks>
/// S.2e3c3a (Decisions 3 and 4): the stage runs on the pipeline's model request (<see cref="Record"/>, <see cref="TableDefinition"/>).
/// The SDK create-only mark that callers outside the pipeline still set is in <c>Sdk/MergeStages.Sdk.cs</c>. <see cref="CarryParameters"/>
/// is the one rule for the parameters of an inner request, shared by the Upsert and the bulk stage (<see cref="BulkStage"/>).
/// </remarks>
public static partial class StandardUpsert
{
    /// <summary>The create-only marks of the model requests the pipeline runs (S.2e3c2b, Decision 7).</summary>
    private static readonly ConditionalWeakTable<Request, object> ModelCreateOnly = new();

    /// <summary>The message name.</summary>
    public const string MessageName = "Upsert";

    /// <summary>True when <paramref name="message"/> is <c>Upsert</c> on the standard table <paramref name="table"/>.</summary>
    internal static bool Applies(string? message, TableDefinition? table) =>
        string.Equals(message, MessageName, StringComparison.OrdinalIgnoreCase) && table is not null && !ElasticTables.IsElastic(table);

    /// <summary>
    /// Creates or updates the record of <paramref name="target"/>, named by <see cref="Record.Id"/> or its primary key
    /// attribute. The Update target is a copy of <paramref name="target"/> without the primary key attribute; the Create target
    /// keeps the given id (a target without one is created with a new id). Key attributes are left on the inner target, whose
    /// core operation refuses them on a standard table.
    /// </summary>
    /// <param name="createOnly">True when the request was marked by <see cref="MarkCreateOnly(Request)"/>: an existing row is then a 412
    /// DuplicateRecord, raised inside the Upsert's transaction after the lock (no race with a concurrent create).</param>
    /// <param name="runInner">Runs the inner <c>Create</c> or <c>Update</c> through the pipeline as an inner execution; answers its
    /// model response, read in (the <c>id</c> of a Create is read from it).</param>
    /// <returns>Whether the record was created, and its id.</returns>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">The faults of the inner Create or Update (its plug-ins, its
    /// security check, its core operation).</exception>
    internal static async Task<(bool Created, Guid Id)> ExecuteAsync(Record target, TableDefinition table, IPipelineCore core,
        DataSession session, Func<Request, Task<Response>> runInner, bool createOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(runInner);
        var pk = SystemAttributes.PrimaryIdName(table);
        var id = target.Id != Guid.Empty ? target.Id
            : target.Attributes.TryGetValue(pk, out var given) && given is Guid g ? g
            : Guid.Empty;

        if (id != Guid.Empty && await core.LockExistsAsync(new RecordReference { LogicalName = table.LogicalName!, Id = id }, session, ct))
        {
            if (createOnly)
                throw new XrmdFaultException(ErrorCodes.DuplicateRecord,
                    $"A record of '{table.LogicalName}' with the id {id} already exists.", 412);
            await runInner(new Request { Name = "Update", Parameters = { ["Target"] = Copy(target, table, id, without: pk) } });
            return (false, id);
        }

        var response = await runInner(new Request { Name = "Create", Parameters = { ["Target"] = Copy(target, table, id, without: null) } });
        return (true, (Guid)response.Results["id"]!);
    }

    /// <summary>
    /// Marks the model request <paramref name="request"/> (an <c>Upsert</c>) as create-only (S.2e3c2b, Decision 7): the mark the
    /// pipeline reads at stage 30, beside the request as the SDK one.
    /// </summary>
    public static void MarkCreateOnly(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModelCreateOnly.AddOrUpdate(request, new object());
    }

    /// <summary>True when the model request <paramref name="request"/> was marked by <see cref="MarkCreateOnly(Request)"/>.</summary>
    public static bool IsCreateOnly(Request request) => ModelCreateOnly.TryGetValue(request, out _);

    /// <summary>
    /// A copy of <paramref name="source"/> with the id <paramref name="id"/>, without the attribute <paramref name="without"/> and
    /// without the values of computed columns: a calculated column (plan 2.12a, ruling K5), a rollup column, its <c>_date</c> and
    /// <c>_state</c> companions and the <c>_base</c> of a Currency rollup (plan 2.12b, ruling R11). They are ignored as a direct
    /// Create or Update ignores them, so they never reach the inner message's plug-ins (its stage 20 runs before the core drops them).
    /// Only what the SDK <c>Copy</c> copied: the logical name, the row version, the attributes in source order and the key attributes;
    /// never the state, the formatted values or the related records.
    /// </summary>
    private static Record Copy(Record source, TableDefinition table, Guid id, string? without)
    {
        var columns = (table.Attributes ?? []).Where(a => a.LogicalName is not null)
            .ToDictionary(a => a.LogicalName!, StringComparer.Ordinal);
        var copy = new Record { LogicalName = source.LogicalName, Id = id, RowVersion = source.RowVersion };
        foreach (var (name, value) in source.Attributes)
            if (!string.Equals(name, without, StringComparison.Ordinal) && !IsComputedValue(columns.GetValueOrDefault(name), columns))
                copy.Attributes[name] = value;
        foreach (var (name, value) in source.KeyAttributes) copy.KeyAttributes[name] = value;
        // Plan 4.1b: the mail secrets the outer Upsert's stage 20 took out of its Target belong to the inner Create or Update; S.2e3c2b
        // (Decision 6): they are keyed on the model Target, so the pipeline hands them over when the inner request crosses in.
        return copy;
    }

    private static bool IsComputedValue(ColumnDefinition? a, Dictionary<string, ColumnDefinition> columns) =>
        a is not null && (ComputedColumnRules.IsComputed(a) || RollupColumns.IsRollupValue(a, columns.GetValueOrDefault));

    /// <summary>
    /// Gives <paramref name="inner"/> the parameters of <paramref name="outer"/> it does not have (e.g.
    /// <c>SuppressDuplicateDetection</c>), never <c>Target</c> or <c>Targets</c> (a bulk message's, #76 Task 3); returns
    /// <paramref name="inner"/>.
    /// </summary>
    internal static Request CarryParameters(Request outer, Request inner)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(inner);
        foreach (var (name, value) in outer.Parameters)
            if (name is not ("Target" or "Targets") && !inner.Parameters.ContainsKey(name)) inner.Parameters[name] = value;
        return inner;
    }
}

using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Messages;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;

namespace Xrmd.Pipeline.Bulk;

/// <summary>
/// The targets of the bulk messages (<see cref="SdkMessageCatalog.BulkMessages"/>), shared by the elastic path (plan 3.4, ruling
/// E5: <c>ElasticBulkMessages</c>) and the standard-table pipeline (#76, rulings B1–B4 and B14): reading <c>Targets</c>, the one
/// table they belong to, the record id each names and the repeated-id rules (the response built from the single-record responses
/// is in the SDK half).
/// It lives in the pipeline project so the pipeline's bulk stage and the organization service can both use it.
/// </summary>
/// <remarks>
/// This file is the model half (S.2e3c3a Decision 3) and names no SDK type; the SDK half, for the callers still on the SDK, is in
/// <c>Sdk/MergeStages.Sdk.cs</c> (Decision 4). Both halves raise their faults through the private text helpers below, so a fault
/// has the same code, status and text whichever half raises it.
/// </remarks>
public static partial class BulkTargets
{
    /// <summary>The bulk message names.</summary>
    public const string CreateMultiple = "CreateMultiple";

    /// <inheritdoc cref="CreateMultiple"/>
    public const string UpdateMultiple = "UpdateMultiple";

    /// <inheritdoc cref="CreateMultiple"/>
    public const string UpsertMultiple = "UpsertMultiple";

    /// <inheritdoc cref="CreateMultiple"/>
    public const string DeleteMultiple = "DeleteMultiple";

    /// <summary>The parameter that holds the targets.</summary>
    public const string TargetsParameter = "Targets";

    /// <summary>The canonical name of the bulk message <paramref name="name"/> (ignoring case); null for any other message.</summary>
    public static string? MessageOf(string? name) =>
        SdkMessageCatalog.BulkMessages.FirstOrDefault(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The targets of a model bulk request: each target (<see cref="Record"/>, or <see cref="RecordReference"/> for DeleteMultiple)
    /// with its table name (null for a null target or a target without a name), the collection's <c>EntityName</c>, and the model
    /// single-record request of a target.
    /// </summary>
    public sealed record RecordTargetList(string Message, string? CollectionName, string?[] Names, object?[] Items, Func<int, Request> Single)
    {
        /// <summary>The number of targets.</summary>
        public int Count => Names.Length;
    }

    /// <summary>
    /// Reads the targets of <paramref name="request"/>, a model bulk request: a <see cref="RecordCollection"/>, or a
    /// <see cref="RecordReferenceCollection"/> for DeleteMultiple. A null target and a target without a name are kept as they are.
    /// </summary>
    /// <exception cref="XrmdFaultException">400 when <c>Targets</c> is missing, of the wrong type or empty.</exception>
    public static RecordTargetList Read(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var message = MessageOf(request.Name) ?? throw new ArgumentException(NotBulk(request.Name), nameof(request));
        request.Parameters.TryGetValue(TargetsParameter, out var value);
        if (message == DeleteMultiple)
        {
            var references = value as RecordReferenceCollection ?? throw MissingTargets(message);
            DemandAny(message, references.Count);
            var list = references.ToList();
            return new RecordTargetList(message, null, [.. list.Select(r => (string?)r?.LogicalName)], [.. list],
                i => SingleRequest("Delete", list[i]));
        }
        var records = value as RecordCollection ?? throw MissingTargets(message);
        DemandAny(message, records.Records.Count);
        var rows = records.Records.ToList();
        return new RecordTargetList(message, records.EntityName, [.. rows.Select(r => (string?)r?.LogicalName)], [.. rows],
            i => message switch
            {
                CreateMultiple => SingleRequest("Create", rows[i]),
                UpdateMultiple => SingleRequest("Update", rows[i]),
                _ => SingleRequest("Upsert", rows[i]),
            });
    }

    /// <summary>Every target must be a record of <paramref name="table"/>.</summary>
    /// <exception cref="XrmdFaultException">400 naming the first target of another table.</exception>
    public static void DemandOneTable(RecordTargetList targets, string? table)
    {
        ArgumentNullException.ThrowIfNull(targets);
        DemandOneTable(targets.Message, targets.Names, table);
    }

    /// <summary>
    /// The id a target names: <see cref="Record.Id"/> (<see cref="RecordReference.Id"/>), else the primary id in its key
    /// attributes, else (a record) its primary id attribute; empty when none.
    /// </summary>
    public static Guid IdOf(TableDefinition table, object? target)
    {
        ArgumentNullException.ThrowIfNull(table);
        var pk = SystemAttributes.PrimaryIdName(table);
        var (id, keys, attributes) = target switch
        {
            Record r => (r.Id, r.KeyAttributes, (IDictionary<string, object?>?)r.Attributes),
            RecordReference r => (r.Id, r.KeyAttributes, null),
            _ => (Guid.Empty, null, null),
        };
        if (id != Guid.Empty) return id;
        if (keys is not null && keys.TryGetValue(pk, out var key)) return KeyId(key);
        return attributes is not null && attributes.TryGetValue(pk, out var value) && value is Guid attribute ? attribute : Guid.Empty;
    }

    /// <summary>
    /// The record id of each target of a model bulk execution, in target order, for its image collections (ruling B6):
    /// <paramref name="created"/> when given (the <c>Ids</c> of a CreateMultiple after stage 30), else from <c>Targets</c>
    /// (<see cref="IdOf(TableDefinition, object?)"/>; empty for a target that names none).
    /// </summary>
    public static Guid[] TargetIds(Request bulk, TableDefinition table, Guid[]? created)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        ArgumentNullException.ThrowIfNull(table);
        if (created is not null) return created;
        return bulk.Parameters.TryGetValue(TargetsParameter, out var t) && t is RecordCollection targets
            ? [.. targets.Records.Select(r => IdOf(table, r))]
            : [];
    }

    /// <summary>
    /// UpdateMultiple: true when <paramref name="id"/> was already named by an earlier target (added to <paramref name="seen"/>
    /// otherwise): the first one wins, the later one is ignored (ruling B3 and plan 3.4).
    /// </summary>
    public static bool IsRepeated(Guid id, ISet<Guid> seen) => id != Guid.Empty && !seen.Add(id);

    private static Request SingleRequest(string name, object? target) => new() { Name = name, Parameters = { ["Target"] = target } };

    // The texts both halves share (Decision 3).

    private static string NotBulk(string? name) => $"'{name}' is not a bulk message.";

    private static XrmdFaultException MissingTargets(string message) =>
        XrmdFaultException.InvalidArgument($"Required parameter 'Targets' of request '{message}' is missing.");

    private static void DemandAny(string message, int count)
    {
        if (count == 0) throw XrmdFaultException.InvalidArgument($"Request '{message}' needs at least one target.");
    }

    private static void DemandOneTable(string message, string?[] names, string? table)
    {
        for (var i = 0; i < names.Length; i++)
            if (!string.Equals(names[i], table, StringComparison.Ordinal))
                throw XrmdFaultException.InvalidArgument(
                    $"Target {i} of request '{message}' is a '{names[i] ?? "(none)"}' record: every target must be a record of '{table}'.");
    }

    private static Guid KeyId(object? key) =>
        key switch { Guid g => g, string text when Guid.TryParse(text, out var parsed) => parsed, _ => Guid.Empty };
}

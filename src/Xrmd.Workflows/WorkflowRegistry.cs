using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Workflows.Xaml;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Workflows;

/// <summary>
/// The activated processes (plan 2.14), process-wide: for every Activated definition, its activation row and parsed step tree.
/// Read from the database on first use and again after <see cref="Invalidate"/>, which the pipeline calls after the commit of a
/// transaction that wrote <c>workflow</c> or <c>processtrigger</c>. An activation whose XAML no longer parses is left out and logged.
/// </summary>
public sealed class WorkflowRegistry(IServiceScopeFactory scopes, IDbConnectionFactory factory, ILogger<WorkflowRegistry>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly object _gate = new();
    private Task<Snapshot>? _loading;

    /// <summary>
    /// The message of an action definition (category 3, plan 2.14, W14): its name, the definition and its activation when it is
    /// Activated.
    /// </summary>
    /// <param name="Message">The <c>sdkmessage</c> name.</param>
    /// <param name="DefinitionId">The definition (type 1).</param>
    /// <param name="Name">The process's name.</param>
    /// <param name="ActivationId">Its activation; null while the definition is Draft.</param>
    public sealed record ActionMessage(string Message, Guid DefinitionId, string? Name, Guid? ActivationId);

    /// <summary>The activated processes, indexed by primary entity and by activation id, and the messages of the actions.</summary>
    private sealed class Snapshot(IReadOnlyList<(WorkflowRow Row, WorkflowDefinition Definition)> all, IReadOnlyList<ActionMessage> actions)
    {
        public IReadOnlyList<(WorkflowRow Row, WorkflowDefinition Definition)> All { get; } = all;

        public IReadOnlyDictionary<string, ActionMessage> Actions { get; } = actions
            .GroupBy(a => a.Message, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, IReadOnlyList<(WorkflowRow Row, WorkflowDefinition Definition)>> ByEntity { get; } = all
            .Where(a => a.Row.PrimaryEntity is not null)
            .GroupBy(a => a.Row.PrimaryEntity!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<(WorkflowRow, WorkflowDefinition)>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<Guid, (WorkflowRow Row, WorkflowDefinition Definition)> ById { get; } = all.ToDictionary(a => a.Row.Id);
    }

    /// <summary>The activated processes (their activation rows), loaded on first use; blocks while they load.</summary>
    public IReadOnlyList<(WorkflowRow Row, WorkflowDefinition Definition)> Activated => GetActivatedAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>The activated processes (their activation rows), loaded on first use.</summary>
    public async Task<IReadOnlyList<(WorkflowRow Row, WorkflowDefinition Definition)>> GetActivatedAsync(CancellationToken ct) =>
        (await SnapshotAsync(ct)).All;

    /// <summary>
    /// The activated processes whose primary entity is <paramref name="entity"/> (ignoring case), loaded on first use; afterwards a
    /// dictionary lookup (plan 2.14, Task 5: a table without processes costs nothing in the pipeline).
    /// </summary>
    public async Task<IReadOnlyList<(WorkflowRow Row, WorkflowDefinition Definition)>> GetActivatedForEntityAsync(string entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return (await SnapshotAsync(ct)).ByEntity.TryGetValue(entity, out var activated) ? activated : [];
    }

    /// <summary>The activated process whose activation row is <paramref name="activationId"/>; null when it is not activated (any more).</summary>
    public async Task<(WorkflowRow Row, WorkflowDefinition Definition)?> FindAsync(Guid activationId, CancellationToken ct) =>
        (await SnapshotAsync(ct)).ById.TryGetValue(activationId, out var activated) ? activated : null;

    /// <summary>
    /// The action definition (Draft or Activated) whose message is <paramref name="message"/> (ignoring case); null when no action has
    /// that message.
    /// </summary>
    public async Task<ActionMessage?> FindActionMessageAsync(string message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        return (await SnapshotAsync(ct)).Actions.TryGetValue(message, out var action) ? action : null;
    }

    private async Task<Snapshot> SnapshotAsync(CancellationToken ct)
    {
        Task<Snapshot> loading;
        lock (_gate) loading = _loading ??= LoadSnapshotAsync();
        try
        {
            return await loading.WaitAsync(ct);
        }
        catch when (loading.IsFaulted)
        {
            // A failed load is not kept: the next call tries again.
            lock (_gate)
                if (ReferenceEquals(_loading, loading)) _loading = null;
            throw;
        }
    }

    /// <summary>The next use reads the activated processes again.</summary>
    public void Invalidate()
    {
        lock (_gate) _loading = null;
    }

    /// <summary>The definition of an activated process whose XAML does not parse: one <see cref="UnsupportedStep"/> naming the reason.</summary>
    internal static WorkflowDefinition Unparsable(WorkflowRow row, string reason) =>
        new(row.Category, row.PrimaryEntity, [], new SequenceStep("0", null, [new UnsupportedStep("0/0", "XAML", "Activity", $"its XAML does not parse ({reason})")]));

    private async Task<Snapshot> LoadSnapshotAsync() => new(await LoadAsync(), await LoadActionsAsync());

    /// <summary>The messages of the action definitions (W14): a definition of category 3 with its <c>sdkmessageid</c> set.</summary>
    private async Task<IReadOnlyList<ActionMessage>> LoadActionsAsync()
    {
        await using var conn = await factory.OpenAsync(CancellationToken.None);
        var d = factory.Dialect;
        await using var cmd = new DataSession(conn, null).Command(
            $"SELECT m.name, d.{d.Quote(S.WorkflowId)}, d.{d.Quote(S.Name)}, CASE WHEN d.statecode = {S.ActivatedState} THEN d.{d.Quote(S.ActiveWorkflowId)} END " +
            $"FROM {d.DataTable(S.Table)} d JOIN {d.DataTable("sdkmessage")} m ON m.sdkmessageid = d.{d.Quote(S.SdkMessageId)} " +
            $"WHERE d.{d.Quote(S.TypeColumn)} = {(int)S.Type.Definition} AND d.{d.Quote(S.CategoryColumn)} = {(int)S.Category.Action} AND m.name IS NOT NULL " +
            $"ORDER BY d.{d.Quote(S.WorkflowId)}");
        await using var rd = await cmd.ExecuteReaderAsync(CancellationToken.None);
        var actions = new List<ActionMessage>();
        while (await rd.ReadAsync(CancellationToken.None))
            actions.Add(new(rd.GetString(0), rd.GetGuid(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetGuid(3)));
        return actions;
    }

    private async Task<IReadOnlyList<(WorkflowRow, WorkflowDefinition)>> LoadAsync()
    {
        await Task.Yield(); // outside the caller's lock
        await using var conn = await factory.OpenAsync(CancellationToken.None);
        var session = new DataSession(conn, null);
        var d = factory.Dialect;
        var ids = new List<Guid>();
        await using (var cmd = session.Command(
                         $"SELECT a.{d.Quote(S.WorkflowId)} FROM {d.DataTable(S.Table)} d " +
                         $"JOIN {d.DataTable(S.Table)} a ON a.{d.Quote(S.WorkflowId)} = d.{d.Quote(S.ActiveWorkflowId)} " +
                         $"WHERE d.{d.Quote(S.TypeColumn)} = {(int)S.Type.Definition} AND d.statecode = {S.ActivatedState} " +
                         $"AND a.statecode = {S.ActivatedState} ORDER BY a.{d.Quote(S.WorkflowId)}"))
        {
            await using var rd = await cmd.ExecuteReaderAsync(CancellationToken.None);
            while (await rd.ReadAsync(CancellationToken.None)) ids.Add(rd.GetGuid(0));
        }

        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<WorkflowStore>();
        var activated = new List<(WorkflowRow, WorkflowDefinition)>();
        foreach (var id in ids)
        {
            if (await store.GetAsync(session, id, CancellationToken.None) is not { } row) continue;
            try
            {
                activated.Add((row, XamlWorkflowParser.Parse(row.Xaml ?? throw new XamlWorkflowException("the activation has no XAML"), row.Category)));
            }
            catch (XamlWorkflowException ex)
            {
                // Never left out silently (W20): kept as a definition whose only step cannot run, so a trigger of it fails explicitly.
                _logger.LogError("The activated process {Name} ({Id}) cannot run: its XAML does not parse ({Reason}).", row.Name, row.Id, ex.Message);
                activated.Add((row, Unparsable(row, ex.Message)));
            }
        }
        return activated;
    }
}

using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Workflows;
using Xrmd.Workflows;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Leaves off the business logic an imported solution brings (migration spec §7.9, plan M4b task 1): every plug-in step and webhook
/// step (<c>sdkmessageprocessingstep</c>, components of type 92) that is enabled gets <c>statecode</c> 1 / <c>statuscode</c> 2, and
/// every process definition (<c>workflow</c> type 1, components of type 29) that is activated goes back to Draft. Each one is recorded
/// with its state as imported, for <c>xrmd migrate activate</c>. The items are found by the solution's components, not by comparing
/// the organization before and after the import, so that a solution imported again after a crash is switched off again.
/// </summary>
public static class BusinessLogicSwitch
{
    public const string StepTable = "sdkmessageprocessingstep", WorkflowTable = "workflow";

    /// <summary>
    /// Switches off, in <paramref name="s"/>'s transaction, the active steps and processes of the solution
    /// <paramref name="solutionId"/> and records them in <paramref name="ledger"/>; the step and process caches are read again after
    /// the commit. Returns how many were switched off.
    /// </summary>
    public static async Task<int> SwitchOffAsync(DataSession s, IServiceProvider services, Caller caller, Guid solutionId, MigrationLedger ledger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ledger);
        var items = new List<DeactivatedItem>();
        await using (var cmd = s.Command($"""
            SELECT {s.Dialect.TextLiteral(StepTable)}, st.sdkmessageprocessingstepid, st.name, st.statecode, st.statuscode
            FROM data.solutioncomponent sc JOIN data.sdkmessageprocessingstep st ON st.sdkmessageprocessingstepid = sc.objectid
            WHERE sc.solutionid = @solution AND sc.componenttype = 92 AND st.statecode = 0
            UNION ALL
            SELECT {s.Dialect.TextLiteral(WorkflowTable)}, w.workflowid, w.name, w.statecode, w.statuscode
            FROM data.solutioncomponent sc JOIN data.workflow w ON w.workflowid = sc.objectid
            WHERE sc.solutionid = @solution AND sc.componenttype = 29 AND w.type = 1 AND w.statecode = 1
            """))
        {
            cmd.AddParameter("@solution", DbType.Guid, solutionId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                items.Add(new DeactivatedItem(rd.GetString(0), rd.GetGuid(1), rd.IsDBNull(2) ? "" : rd.GetString(2), rd.GetInt32(3), rd.GetInt32(4)));
        }
        if (items.Count == 0) return 0;

        var core = services.GetRequiredService<CoreOperations>();
        var activation = services.GetRequiredService<WorkflowActivation>();
        foreach (var item in items)
        {
            await ledger.RecordDeactivatedAsync(s, item, ct);
            if (item.Table == StepTable)
                await core.UpdateAsync(s, caller, new Entity(StepTable, item.Id) { ["statecode"] = new OptionSetValue(1), ["statuscode"] = new OptionSetValue(2) }, ct);
            else
                await activation.DeactivateAsync(s, Caller.Internal, item.Id, ct);
        }
        var steps = services.GetRequiredService<StepRegistry>();
        var workflows = services.GetService<IWorkflowPipelineHook>();
        s.OnCommitted(() =>
        {
            steps.Invalidate();
            workflows?.Invalidate();
        });
        return items.Count;
    }

    /// <summary>The ledger key written before a solution's import, so that one imported but not yet switched off can be found.</summary>
    public static string PendingKey(string uniqueName) => $"schema/pending/{uniqueName}";

    /// <summary>
    /// Switches off the logic of every solution of run <paramref name="runId"/> whose import started (<see cref="PendingKey"/>) but was
    /// never marked done: the run was cancelled or stopped between the import's commit and the switch-off. Called when the run is
    /// finalized, before the lock lifts. Each such solution is then marked done; returns how many items were switched off.
    /// </summary>
    public static async Task<int> SwitchOffPendingAsync(IServiceProvider services, Guid runId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var factory = provider.GetRequiredService<IDbConnectionFactory>();
        var store = provider.GetRequiredService<MigrationRunStore>();
        var ledger = new MigrationLedger(factory, runId);
        var pending = new List<(string Name, Guid Solution)>();
        await using (var conn = await factory.OpenAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            // Plan 2.7b, Task 10: the solution is matched by its unique name's text key (#299), as everywhere else.
            var dialect = factory.Dialect;
            cmd.CommandText = $"""
                SELECT SUBSTRING(p.entry, 16, 400), so.solutionid
                FROM meta.migration_chunk p JOIN data.solution so ON {dialect.TextKey("so.uniquename")} = {dialect.TextKey("SUBSTRING(p.entry, 16, 400)")}
                WHERE p.runid = @run AND p.entry LIKE 'schema/pending/%'
                  AND NOT EXISTS (SELECT 1 FROM meta.migration_chunk d WHERE d.runid = @run AND d.entry = {dialect.Concat(dialect.TextLiteral("schema/solution/"), "SUBSTRING(p.entry, 16, 400)")})
                """;
            cmd.AddParameter("@run", DbType.Guid, runId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) pending.Add((rd.GetString(0), rd.GetGuid(1)));
        }
        var total = 0;
        foreach (var (name, solution) in pending)
        {
            await using var conn = await factory.OpenAsync(ct);
            await using var tx = conn.BeginTransaction();
            var s = new DataSession(conn, tx);
            var off = await SwitchOffAsync(s, provider, Caller.Internal, solution, ledger, ct);
            if (off > 0)
                await ledger.IssueAsync(new MigrationIssue(SchemaPhase.PhaseOrder, MigrationIssue.Info, "logic-deactivated", "solution", solution, null,
                    $"{name}: {off} plug-in step(s), webhook step(s) or process(es) left off until 'xrmd migrate activate' (switched off at finalize)."), s, ct);
            await store.MarkChunkAsync(runId, SchemaPhase.PhaseOrder, $"schema/solution/{name}", 1, 0, s, ct);
            await tx.CommitAsync(ct);
            s.Committed();
            total += off;
        }
        return total;
    }
}

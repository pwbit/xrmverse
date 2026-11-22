using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Workflows;
using Xrmd.Workflows;

namespace Xrmd.Admin.Migration.Import;

/// <summary>What <c>xrmd migrate activate</c> did with one item: <c>activated</c>, <c>would-activate</c>, <c>skipped</c> or <c>failed</c>.</summary>
public sealed record ActivationResult(string Table, Guid Id, string Name, string Action, string? Message);

/// <summary>
/// Switches back on what an import left off (migration spec §7.9, plan M5 task 2), and only that: the items of
/// <c>meta.migration_deactivated</c> whose source state was on. A plug-in or webhook step gets <c>statecode</c> 0 / <c>statuscode</c> 1;
/// a process is activated as an administrator would. An item that no longer exists or is already on is skipped; each item has its
/// own transaction, so one failure does not stop the others.
/// </summary>
public static class BusinessLogicActivation
{
    public const string Activated = "activated", WouldActivate = "would-activate", Skipped = "skipped", Failed = "failed";

    /// <param name="ids">The items to switch on; null for all of the run's.</param>
    /// <exception cref="MigrationRequestException">An id the run did not switch off.</exception>
    public static async Task<List<ActivationResult>> ActivateAsync(IServiceProvider services, Caller caller, Guid runId, IReadOnlyCollection<Guid>? ids,
        bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var factory = provider.GetRequiredService<IDbConnectionFactory>();
        var ledger = new MigrationLedger(factory, runId);
        var items = await ledger.DeactivatedAsync(ct);
        if (ids is not null)
        {
            var unknown = ids.Where(id => items.All(i => i.Id != id)).ToList();
            if (unknown.Count > 0)
                throw new MigrationRequestException($"The run {runId} did not switch off {string.Join(", ", unknown)}: activate only switches on what the import left off.");
            items = [.. items.Where(i => ids.Contains(i.Id))];
        }

        var core = provider.GetRequiredService<CoreOperations>();
        var activation = provider.GetRequiredService<WorkflowActivation>();
        var results = new List<ActivationResult>();
        var changed = false;
        try
        {
            foreach (var item in items)
            {
                var wasOn = item.Table == BusinessLogicSwitch.StepTable ? item.SourceState == 0 : item.SourceState == 1;
                await using var conn = await factory.OpenAsync(ct);
                var current = await StateAsync(new DataSession(conn, null), item, ct);
                if (current is null)
                {
                    results.Add(new(item.Table, item.Id, item.Name, Skipped, "It no longer exists."));
                    continue;
                }
                if (!wasOn)
                {
                    results.Add(new(item.Table, item.Id, item.Name, Skipped, "It was off in the source."));
                    continue;
                }
                if (IsOn(item.Table, current.Value))
                {
                    results.Add(new(item.Table, item.Id, item.Name, Skipped, "It is already on."));
                    continue;
                }
                if (dryRun)
                {
                    results.Add(new(item.Table, item.Id, item.Name, WouldActivate, null));
                    continue;
                }
                await using var tx = conn.BeginTransaction();
                var s = new DataSession(conn, tx);
                try
                {
                    if (item.Table == BusinessLogicSwitch.StepTable)
                        await core.UpdateAsync(s, caller, new Entity(item.Table, item.Id) { ["statecode"] = new OptionSetValue(0), ["statuscode"] = new OptionSetValue(1) }, ct);
                    else
                        await activation.ActivateAsync(s, caller, item.Id, ct);
                    await ledger.MarkActivatedAsync(s, item.Table, item.Id, ct);
                    await tx.CommitAsync(ct);
                    s.Committed();
                    changed = true;
                    results.Add(new(item.Table, item.Id, item.Name, Activated, null));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Any failure of this item (a fault, a database error, a validation) is its own: the others still go on.
                    try { await tx.RollbackAsync(CancellationToken.None); } catch (Exception) { /* the transaction is already gone */ }
                    results.Add(new(item.Table, item.Id, item.Name, Failed, ex.Message));
                }
            }
        }
        finally
        {
            // Also when a cancel stops the loop: the items committed so far must run from now on.
            if (changed)
            {
                provider.GetRequiredService<StepRegistry>().Invalidate();
                provider.GetService<IWorkflowPipelineHook>()?.Invalidate();
            }
        }
        return results;
    }

    private static bool IsOn(string table, int state) => table == BusinessLogicSwitch.StepTable ? state == 0 : state == 1;

    /// <summary>The item's <c>statecode</c> now, or null when it no longer exists.</summary>
    private static async Task<int?> StateAsync(DataSession s, DeactivatedItem item, CancellationToken ct)
    {
        var (table, key) = item.Table == BusinessLogicSwitch.StepTable
            ? ("sdkmessageprocessingstep", "sdkmessageprocessingstepid") : ("workflow", "workflowid");
        await using var cmd = s.Command($"SELECT statecode FROM data.{table} WHERE {key} = @id");
        cmd.AddParameter("@id", DbType.Guid, item.Id);
        return await cmd.ExecuteScalarAsync(ct) is int state ? state : null;
    }
}

using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Connector;
using S = Xrmd.Metadata.FlowSubscriptionSchema;

namespace Xrmd.WebApi.Connector;

/// <summary>
/// X.12b: queues, in the write's transaction, one flow notification (<see cref="AsyncOperationQueue.FlowNotification"/>) per
/// subscription a row change matches: its mask has the change and, for an update, its filtering columns (none: all) meet the
/// sent columns. The job is owned by the subscriber and carries the row as the subscriber reads it now (a create or an update),
/// so a rolled-back write queues nothing and each change keeps its own snapshot. A subscriber that is disabled or cannot read the row
/// at the change (for a delete, the row before the core delete: the executor tells a delete after pre-operation) hears nothing. Depends on the <see cref="FlowSubscriptionCache"/> only, never on the
/// <see cref="Xrmd.Pipeline.PipelineExecutor"/> (which depends on this hook); the core operations are resolved per call.
/// </summary>
public sealed class FlowSubscriptionHook(IServiceProvider services, FlowSubscriptionCache cache, AsyncOperationQueue queue)
    : IRowChangeHook
{
    /// <summary>
    /// False for the subscriptions' own table (it cannot be subscribed, and its writes must not load the cache on another connection
    /// while their transaction holds the row), else <see cref="FlowSubscriptionCache.MayHave"/>.
    /// </summary>
    public bool MayHave(string table) =>
        !string.Equals(table, S.Table, StringComparison.OrdinalIgnoreCase) && cache.MayHave(table);

    public async Task<bool> OnChangedAsync(string message, string table, Guid id, IReadOnlyCollection<string> sentColumns, DataSession s,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(sentColumns);
        ArgumentNullException.ThrowIfNull(s);
        var bit = message switch
        {
            RowChanges.Create => S.Create,
            RowChanges.Update => S.Update,
            RowChanges.Delete => S.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(message), message, "The change is not one of create, update or delete."),
        };
        var subscriptions = await cache.ForTableAsync(table, ct);
        var queued = false;
        foreach (var subscription in subscriptions)
        {
            if ((subscription.Changes & bit) == 0 || subscription.OwnerId == Guid.Empty) continue;
            if (bit == S.Update && subscription.FilteringColumns is { } filtering
                && !sentColumns.Any(c => filtering.Contains(c.ToLowerInvariant()))) continue;
            if (await OwnerAsync(subscription.OwnerId, s, ct) is not { } owner) continue;

            // The owner must read the row at the change: a delete is told before the core delete (the row is still there), its
            // snapshot discarded. SnapshotAsync refuses in C# (403/404), never by a failed statement: the transaction goes on.
            if (await EntitySetController.SnapshotAsync(services, owner, table, id, s, ct) is not { } row) continue;
            var body = new JsonObject { ["change"] = message, ["table"] = table, ["id"] = id.ToString() };
            if (bit != S.Delete) body["row"] = row;
            var data = new JsonObject { ["subscriptionId"] = subscription.Id.ToString(), ["attempt"] = 1, ["body"] = body };
            await queue.EnqueueSystemJobAsync(s, owner, AsyncOperationQueue.FlowNotification, $"Flow notification: {table} {message}",
                AsyncOperationQueue.FlowNotificationMessage, table, null, data.ToJsonString(), ct);
            queued = true;
        }
        return queued;
    }

    /// <summary>The subscriber as a caller; null when it no longer exists or is disabled (both raised in C#, no failed statement).</summary>
    private async Task<Caller?> OwnerAsync(Guid userId, DataSession s, CancellationToken ct)
    {
        try
        {
            return await services.GetRequiredService<CoreOperations>().ResolveUserAsync(userId, s, ct);
        }
        catch (XrmdFaultException fault) when (fault.HttpStatus is 403 or 404)
        {
            return null;
        }
    }
}

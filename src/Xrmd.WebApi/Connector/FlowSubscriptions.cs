using System.Data;
using Microsoft.Extensions.Options;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Model.Security;
using Xrmd.Pipeline;
using Xrmd.Security;
using S = Xrmd.Metadata.FlowSubscriptionSchema;

namespace Xrmd.WebApi.Connector;

/// <summary>One webhook subscription of the flow trigger: who owns it, the table it watches, the mask of changes and the columns.</summary>
/// <param name="Changes">A mask of <see cref="FlowSubscriptionSchema.Create"/>, <see cref="FlowSubscriptionSchema.Update"/> and
/// <see cref="FlowSubscriptionSchema.Delete"/>.</param>
/// <param name="FilteringColumns">The columns whose change notifies an Update (lower-case); null means all.</param>
public sealed record FlowSubscription(Guid Id, Guid OwnerId, string Table, int Changes, IReadOnlySet<string>? FilteringColumns);

/// <summary>
/// The body of <c>POST /api/connector/v1/subscriptions</c>, as the flow trigger sends it. <paramref name="Changes"/> is a
/// comma-separated subset of <c>create,update,delete</c>; <paramref name="FilteringColumns"/> is comma-separated.
/// </summary>
public sealed record SubscribeBody(string? Table, string? Changes, string? FilteringColumns, string? CallbackUrl);

/// <summary>
/// X.12b: the flow subscriptions of all users, kept in memory (process-wide). Loaded once, as internal work, at the first
/// <see cref="ForTableAsync"/>, and served from memory until <see cref="Invalidate"/>, which every subscribe and unsubscribe
/// calls after its commit. Holds no callback URL.
/// </summary>
public sealed class FlowSubscriptionCache(IDbConnectionFactory factory)
{
    private static readonly IReadOnlyList<FlowSubscription> None = [];
    private readonly SemaphoreSlim _load = new(1, 1);
    private State _state = new(0, null);

    /// <summary>
    /// The list (null while not loaded) and the generation it belongs to, swapped as one object: an <see cref="Invalidate"/>
    /// installs the next generation, and a load installs its list only over the state it started from.
    /// </summary>
    private sealed record State(long Generation, Dictionary<string, IReadOnlyList<FlowSubscription>>? ByTable);

    /// <summary>The subscriptions watching <paramref name="table"/> (by logical name, ignoring case).</summary>
    public async Task<IReadOnlyList<FlowSubscription>> ForTableAsync(string table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        var byTable = Volatile.Read(ref _state).ByTable ?? await LoadAsync(ct);
        return byTable.TryGetValue(table, out var list) ? list : None;
    }

    /// <summary>
    /// Answers from memory whether <paramref name="table"/> may have subscriptions: true while the list is not loaded (so the first
    /// write loads it through <see cref="ForTableAsync"/>), else whether one watches it.
    /// </summary>
    public bool MayHave(string table) => Volatile.Read(ref _state).ByTable is not { } byTable || byTable.ContainsKey(table);

    /// <summary>Drops the list; the next <see cref="ForTableAsync"/> reads it again.</summary>
    public void Invalidate()
    {
        // Never waits for a running load: that load's list belongs to the old generation and is not installed.
        State current, next;
        do
        {
            current = Volatile.Read(ref _state);
            next = new State(current.Generation + 1, null);
        }
        while (Interlocked.CompareExchange(ref _state, next, current) != current);
    }

    private async Task<Dictionary<string, IReadOnlyList<FlowSubscription>>> LoadAsync(CancellationToken ct)
    {
        await _load.WaitAsync(ct);
        try
        {
            var start = Volatile.Read(ref _state);
            if (start.ByTable is { } loaded) return loaded;
            var rows = new List<FlowSubscription>();
            await using (var conn = await factory.OpenAsync(ct))
            {
                var s = new DataSession(conn, null);
                await using var cmd = s.Command(
                    $"SELECT {S.Id}, ownerid, {S.TableColumn}, {S.Changes}, {S.FilteringColumns} FROM {s.DataTable(S.Table)}");
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (reader.IsDBNull(2) || reader.IsDBNull(3)) continue;
                    var filtering = reader.IsDBNull(4) ? null : FlowSubscriptions.SplitColumns(reader.GetString(4));
                    rows.Add(new FlowSubscription(reader.GetGuid(0), reader.IsDBNull(1) ? Guid.Empty : reader.GetGuid(1), reader.GetString(2),
                        Convert.ToInt32(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture),
                        filtering is { Count: > 0 } ? filtering : null));
                }
            }
            var byTable = rows.GroupBy(r => r.Table, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<FlowSubscription>)g.ToList(), StringComparer.OrdinalIgnoreCase);
            // An Invalidate during the read leaves the list unloaded: the read may have missed the write that invalidated it.
            Interlocked.CompareExchange(ref _state, start with { ByTable = byTable }, start);
            return byTable;
        }
        finally
        {
            _load.Release();
        }
    }
}

/// <summary>
/// X.12b: subscribes and unsubscribes the flow trigger's webhooks (<c>/api/connector/v1/subscriptions</c>). The row of
/// <see cref="FlowSubscriptionSchema.Table"/> is written as internal work (the public API refuses the table), owned by the subscriber;
/// the callback URL goes to <see cref="FlowSubscriptionSchema.CallbackTable"/> in the same transaction. The callback URL is a secret:
/// it is never logged, quoted in a fault or returned.
/// </summary>
public sealed class FlowSubscriptions(IServiceProvider services, IDbConnectionFactory factory, PipelineExecutor pipeline,
    ConnectorCatalog catalog, SecurityGuard guard, IMetadataStore store, IOptions<ConnectorOptions> options, FlowSubscriptionCache cache)
{
    /// <summary>The error code of the 429 of a subscribe over <see cref="ConnectorOptions.MaxSubscriptionsPerUser"/>.</summary>
    public const int SubscriptionLimitReached = unchecked((int)0x80090910);

    private const int MaxFilteringLength = 4000;

    /// <summary>The scope of the per-user key lock of a subscribe (the limit's count).</summary>
    private const string OwnerLockScope = "xrmverse_flowsubscription.owner";

    private static readonly (string Token, int Bit)[] ChangeTokens =
        [("create", S.Create), ("update", S.Update), ("delete", S.Delete)];

    /// <summary>Creates the subscription of <paramref name="caller"/> described by <paramref name="body"/>.</summary>
    /// <returns>The id of the subscription.</returns>
    /// <exception cref="XrmdFaultException">403 without <c>prvCreatexrmverse_FlowSubscription</c> or read on the table; 404 for
    /// an unknown table; 400 for a bad body; 429 <see cref="SubscriptionLimitReached"/> over the per-user limit.</exception>
    public async Task<Guid> SubscribeAsync(Caller caller, SubscribeBody body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(body);
        var settings = options.Value;
        var own = store.GetTable(S.Table) ?? throw new InvalidOperationException($"The table '{S.Table}' is missing.");
        await guard.DemandPrivilegeAsync(caller, PrivilegeNames.For(AccessMask.CreateAccess, own), S.Table, ct);
        if (string.IsNullOrWhiteSpace(body.Table)) throw XrmdFaultException.InvalidArgument("The table is required.");

        var table = await catalog.GetReadableTableAsync(caller, body.Table.Trim(), ct);
        var logicalName = table.LogicalName!;
        if (string.Equals(logicalName, S.Table, StringComparison.OrdinalIgnoreCase))
            throw XrmdFaultException.InvalidArgument("A flow cannot be triggered by the changes of flow subscriptions.");
        if (ElasticTables.IsElastic(table))
            throw XrmdFaultException.InvalidArgument($"The table '{logicalName}' is an elastic table, which cannot trigger a flow.");

        var (changes, changesText) = ParseChanges(body.Changes);
        var filtering = ParseFiltering(body.FilteringColumns, table);
        if (filtering is not null && (changes & S.Update) == 0)
            throw XrmdFaultException.InvalidArgument("Filtering columns apply to updates only: the changes must include 'update'.");

        if (CallbackUrlPolicy.Problem(body.CallbackUrl, settings) is { } problem) throw XrmdFaultException.InvalidArgument(problem);
        var callback = body.CallbackUrl!;
        var host = new Uri(callback).Host;

        var values = new Dictionary<string, object?>
        {
            [S.Name] = $"{logicalName}: {changesText}",
            [S.TableColumn] = logicalName,
            [S.Changes] = changes,
            [S.FilteringColumns] = filtering,
            [S.CallbackHost] = host,
        };
        Guid id;
        await using (var conn = await factory.OpenAsync(ct))
        {
            var s = new DataSession(conn, null);
            await pipeline.BeginTransactionAsync(s, ct);
            try
            {
                var max = settings.MaxSubscriptionsPerUser;
                // Serialized per user: the count and the insert of two subscribes of one user do not interleave.
                await s.KeyLockAsync(OwnerLockScope, caller.UserId, ct);
                if (await CountOwnedAsync(s, caller.UserId, ct) >= max)
                    throw new XrmdFaultException(SubscriptionLimitReached, $"You have reached the limit of {max} flow subscriptions.", 429);
                id = await EntitySetController.CreateInternalRowAsync(services, S.Table, values, caller.UserId, s, ct);
                await SetCreatorAsync(s, id, caller.UserId, ct);
                await using (var insert = s.Command($"INSERT INTO {S.CallbackTable} (subscriptionid, callbackurl) VALUES (@id, @url)"))
                {
                    insert.AddParameter("@id", DbType.Guid, id);
                    insert.AddParameter("@url", DbType.String, callback, 2048);
                    await insert.ExecuteNonQueryAsync(ct);
                }
                await pipeline.CommitTransactionAsync(s);
            }
            catch
            {
                await pipeline.RollbackTransactionAsync(s);
                throw;
            }
        }
        cache.Invalidate();
        return id;
    }

    /// <summary>Ends the subscription <paramref name="id"/> for its owner or a System Administrator.</summary>
    /// <exception cref="XrmdFaultException">404 for an unknown subscription; 403 for another user.</exception>
    public async Task UnsubscribeAsync(Caller caller, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        Guid? owner;
        await using (var conn = await factory.OpenAsync(ct))
            owner = await OwnerAsync(new DataSession(conn, null), id, ct);
        if (owner is null)
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"The flow subscription '{id}' does not exist.", 404);
        if (owner != caller.UserId && !await guard.IsAdministratorAsync(caller, ct))
            throw new XrmdFaultException(ErrorCodes.PrivilegeDenied, "Only the owner of a flow subscription or a System Administrator can end it.", 403);
        await EndAsync(id, caller.UserId, ct);
    }

    /// <summary>The callback URL of the subscription <paramref name="id"/>, or null when it has none. Never log it.</summary>
    public async Task<string?> CallbackAsync(DataSession s, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var cmd = s.Command($"SELECT callbackurl FROM {S.CallbackTable} WHERE subscriptionid = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>True when the subscription row <paramref name="id"/> exists (whether or not it has a callback).</summary>
    public async Task<bool> ExistsAsync(DataSession s, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        return await OwnerAsync(s, id, ct) is not null;
    }

    /// <summary>
    /// Deletes the subscription <paramref name="id"/> and its callback as internal work (an unknown id deletes nothing), in one
    /// transaction, then <see cref="FlowSubscriptionCache.Invalidate"/>.
    /// </summary>
    public Task EndAsync(Guid id, CancellationToken ct) => EndAsync(id, null, ct);

    /// <summary><see cref="EndAsync(Guid, CancellationToken)"/> on behalf of <paramref name="actingUserId"/> (null: the row's owner).</summary>
    private async Task EndAsync(Guid id, Guid? actingUserId, CancellationToken ct)
    {
        await using (var conn = await factory.OpenAsync(ct))
        {
            var s = new DataSession(conn, null);
            await pipeline.BeginTransactionAsync(s, ct);
            try
            {
                if (await OwnerAsync(s, id, ct) is { } owner)
                    await EntitySetController.DeleteInternalRowAsync(services, S.Table, id, actingUserId ?? owner, s, ct);
                await using (var delete = s.Command($"DELETE FROM {S.CallbackTable} WHERE subscriptionid = @id"))
                {
                    delete.AddParameter("@id", DbType.Guid, id);
                    await delete.ExecuteNonQueryAsync(ct);
                }
                await pipeline.CommitTransactionAsync(s);
            }
            catch
            {
                await pipeline.RollbackTransactionAsync(s);
                throw;
            }
        }
        cache.Invalidate();
    }

    /// <summary>The trimmed, lower-cased, distinct and sorted names of a comma-separated column list.</summary>
    internal static SortedSet<string> SplitColumns(string text) =>
        new(text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(c => c.ToLowerInvariant()),
            StringComparer.Ordinal);

    private static (int Mask, string Text) ParseChanges(string? text)
    {
        var mask = 0;
        foreach (var token in (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var bit = ChangeTokens.FirstOrDefault(c => string.Equals(c.Token, token, StringComparison.OrdinalIgnoreCase)).Bit;
            if (bit == 0) throw XrmdFaultException.InvalidArgument($"The change '{token}' is not one of create, update or delete.");
            mask |= bit;
        }
        if (mask == 0) throw XrmdFaultException.InvalidArgument("The changes must name at least one of create, update or delete.");
        return (mask, string.Join(',', ChangeTokens.Where(c => (mask & c.Bit) != 0).Select(c => c.Token)));
    }

    private static string? ParseFiltering(string? text, Model.Metadata.TableDefinition table)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var columns = SplitColumns(text);
        var known = new HashSet<string>((table.Attributes ?? []).Select(a => a.LogicalName).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
            if (!known.Contains(column))
                throw XrmdFaultException.InvalidArgument($"The filtering column '{column}' is not a column of the table '{table.LogicalName}'.");
        var joined = string.Join(',', columns);
        return joined.Length <= MaxFilteringLength ? joined
            : throw XrmdFaultException.InvalidArgument($"The filtering columns must not be longer than {MaxFilteringLength} characters.");
    }

    /// <summary>
    /// The internal Create fills <c>createdby</c> and <c>modifiedby</c> with the internal caller (no user): the subscriber is the
    /// creator. The core sets no <c>…onbehalfby</c> column on a Create.
    /// </summary>
    private static async Task SetCreatorAsync(DataSession s, Guid id, Guid userId, CancellationToken ct)
    {
        await using var cmd = s.Command($"UPDATE {s.DataTable(S.Table)} SET createdby = @user, modifiedby = @user WHERE {S.Id} = @id");
        cmd.AddParameter("@user", DbType.Guid, userId);
        cmd.AddParameter("@id", DbType.Guid, id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountOwnedAsync(DataSession s, Guid owner, CancellationToken ct)
    {
        // The key lock (PostgreSQL) or this hint (SQL Server: UPDLOCK, HOLDLOCK on the range read) serializes the count.
        await using var cmd = s.Command(
            $"SELECT COUNT(*) FROM {s.DataTable(S.Table)}{s.Dialect.LockHint(RowLock.UpdateRange)} WHERE ownerid = @owner");
        cmd.AddParameter("@owner", DbType.Guid, owner);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<Guid?> OwnerAsync(DataSession s, Guid id, CancellationToken ct)
    {
        await using var cmd = s.Command($"SELECT ownerid FROM {s.DataTable(S.Table)} WHERE {S.Id} = @id");
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(ct) switch
        {
            Guid g => g,
            null or DBNull => null,
            var other => Guid.Parse(other.ToString()!),
        };
    }
}

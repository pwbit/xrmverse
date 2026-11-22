using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Pipeline.Registration;

namespace Xrmd.Pipeline.CustomApis;

/// <summary>
/// In-memory registry of the custom APIs (plan 15): every <c>customapi</c> row with its parameters, properties and main
/// operation, loaded once per generation, as <see cref="StepRegistry"/> loads the steps (the same
/// <see cref="GenerationalSnapshot{T}"/>). A commit that wrote a registration table (<see cref="StepRegistry.RegistrationEntities"/>)
/// invalidates it, as it invalidates the step registry: the pipeline, the solution import and the cascade delete do so.
/// The snapshot shows committed rows only: an API written in a transaction still open is not in it.
/// </summary>
public sealed class CustomApiRegistry
{
    private sealed record Snapshot(IReadOnlyList<CustomApiDefinition> All, IReadOnlyDictionary<string, CustomApiDefinition> ByName);

    private readonly GenerationalSnapshot<Snapshot> _snapshot = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<CustomApiDefinition>>> _source;

    /// <summary>A registry that reads the API tables through <paramref name="factory"/>.</summary>
    public CustomApiRegistry(IDbConnectionFactory factory) : this(ct => LoadFromDatabaseAsync(factory, ct)) { }

    /// <summary>A registry that takes its definitions from <paramref name="source"/> (the database path above, or a test's).</summary>
    internal CustomApiRegistry(Func<CancellationToken, Task<IReadOnlyList<CustomApiDefinition>>> source) => _source = source;

    private static Snapshot Index(IReadOnlyList<CustomApiDefinition> all)
    {
        var byName = new Dictionary<string, CustomApiDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var api in all) byName.TryAdd(api.UniqueName, api);
        return new Snapshot(all, byName);
    }

    /// <summary>The current generation: it changes at every <see cref="Invalidate"/> (part of the <c>$metadata</c> cache key).</summary>
    public int Generation => _snapshot.Generation;

    /// <summary>Drops the cached definitions: the next call reloads them from the database.</summary>
    public void Invalidate() => _snapshot.Invalidate();

    /// <summary>The API named <paramref name="name"/> (its unique name, ignoring case); null when there is none.</summary>
    public async Task<CustomApiDefinition?> FindAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        return (await _snapshot.GetAsync(LoadAsync, ct)).ByName.GetValueOrDefault(name);
    }

    /// <summary>The generation <see cref="Current"/> reports before the first load: no loaded snapshot has it, so a model cached for it is rebuilt after the load.</summary>
    public const int NotLoaded = -1;

    /// <summary>
    /// The definitions of the last snapshot loaded, with the generation it was loaded for (empty before the first load). It never
    /// loads and never waits, so <c>ODataEdmModelBuilder.Build</c>, which is synchronous, can read it: it may be one invalidation
    /// behind until the next <see cref="AllAsync"/> or <see cref="FindAsync"/>.
    /// </summary>
    public (int Generation, IReadOnlyList<CustomApiDefinition> Apis) Current =>
        _snapshot.Loaded is { } loaded ? (loaded.Generation, loaded.Value.All) : (NotLoaded, []);

    /// <summary>Every API, by unique name.</summary>
    public async Task<IReadOnlyList<CustomApiDefinition>> AllAsync(CancellationToken ct) => (await _snapshot.GetAsync(LoadAsync, ct)).All;

    private async Task<Snapshot> LoadAsync(CancellationToken ct) => Index(await _source(ct));

    private static async Task<IReadOnlyList<CustomApiDefinition>> LoadFromDatabaseAsync(IDbConnectionFactory factory, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.Command($"""
            SELECT c.customapiid, c.uniquename, c.bindingtype, c.boundentitylogicalname, c.isfunction, c.isprivate,
                   c.allowedcustomprocessingsteptype, c.executeprivilegename, t.plugintypeid, t.typename, a.pluginassemblyid,
                   CAST(a.versionnumber AS bigint)
            FROM data.{CustomApiTables.Api} AS c
            LEFT JOIN data.plugintype AS t ON t.plugintypeid = c.plugintypeid
            LEFT JOIN data.pluginassembly AS a ON a.pluginassemblyid = t.pluginassemblyid
            WHERE c.uniquename IS NOT NULL;
            SELECT customapiid, uniquename, type, logicalentityname, isoptional FROM data.{CustomApiTables.RequestParameter}
                WHERE customapiid IS NOT NULL AND uniquename IS NOT NULL;
            SELECT customapiid, uniquename, type, logicalentityname FROM data.{CustomApiTables.ResponseProperty}
                WHERE customapiid IS NOT NULL AND uniquename IS NOT NULL;
            """);
        await using var rd = await cmd.ExecuteReaderAsync(ct);

        var apis = new List<(Guid Id, Func<IReadOnlyList<CustomApiField>, IReadOnlyList<CustomApiField>, CustomApiDefinition> Make)>();
        while (await rd.ReadAsync(ct))
        {
            var id = rd.GetGuid(0);
            var uniqueName = rd.GetString(1);
            var binding = rd.IsDBNull(2) ? 0 : rd.GetInt32(2);
            var bound = rd.IsDBNull(3) ? null : rd.GetString(3);
            var isFunction = !rd.IsDBNull(4) && rd.GetBoolean(4);
            var isPrivate = !rd.IsDBNull(5) && rd.GetBoolean(5);
            var allowed = rd.IsDBNull(6) ? 0 : rd.GetInt32(6);
            var privilege = rd.IsDBNull(7) || rd.GetString(7).Length == 0 ? null : rd.GetString(7);
            var main = rd.IsDBNull(8) || rd.IsDBNull(10)
                ? null
                : new CustomApiMainOperation(rd.GetGuid(8), rd.IsDBNull(9) ? "" : rd.GetString(9), rd.GetGuid(10), rd.IsDBNull(11) ? 0 : rd.GetInt64(11));
            apis.Add((id, (requests, responses) =>
                new CustomApiDefinition(id, uniqueName, binding, bound, isFunction, isPrivate, allowed, privilege, main, requests, responses)));
        }

        var requests = new Dictionary<Guid, List<CustomApiField>>();
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct))
            Add(requests, rd.GetGuid(0), new CustomApiField(rd.GetString(1), (CustomApiType)(rd.IsDBNull(2) ? 0 : rd.GetInt32(2)),
                rd.IsDBNull(3) ? null : rd.GetString(3), !rd.IsDBNull(4) && rd.GetBoolean(4)));
        var responses = new Dictionary<Guid, List<CustomApiField>>();
        await rd.NextResultAsync(ct);
        while (await rd.ReadAsync(ct))
            Add(responses, rd.GetGuid(0), new CustomApiField(rd.GetString(1), (CustomApiType)(rd.IsDBNull(2) ? 0 : rd.GetInt32(2)),
                rd.IsDBNull(3) ? null : rd.GetString(3), false));

        IReadOnlyList<CustomApiField> Of(Dictionary<Guid, List<CustomApiField>> fields, Guid id) =>
            fields.TryGetValue(id, out var list) ? list.OrderBy(f => f.UniqueName, StringComparer.Ordinal).ToList() : [];
        var all = apis.Select(a => a.Make(Of(requests, a.Id), Of(responses, a.Id)))
            .OrderBy(a => a.UniqueName, StringComparer.Ordinal).ToList();
        return all;
    }

    private static void Add(Dictionary<Guid, List<CustomApiField>> fields, Guid apiId, CustomApiField field)
    {
        if (!fields.TryGetValue(apiId, out var list)) fields[apiId] = list = [];
        list.Add(field);
    }
}

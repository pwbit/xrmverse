using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Pipeline.Webhooks;

namespace Xrmd.Pipeline.Registration;

/// <summary>
/// In-memory registry of the plug-in steps read from the standard registration tables. Everything (steps, types, images,
/// assembly versions, custom message names, the webhook endpoints of the steps on a <c>serviceendpoint</c> with their auth value
/// read through <see cref="WebhookSecrets.ReadAsync"/>; not the assembly content) is loaded once per invalidation. The deletion of an
/// entity (whose steps go with it) invalidates it once committed.
/// </summary>
public sealed class StepRegistry(IDbConnectionFactory factory, WebhookSecrets secrets, ILogger<StepRegistry>? logger = null)
    : IEntityDeletedHandler
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>The logical names of the registration tables; changes to these invalidate the registry.</summary>
    public static readonly IReadOnlySet<string> RegistrationEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pluginassembly", "plugintype", "sdkmessage", "sdkmessagefilter", "sdkmessageprocessingstep", "sdkmessageprocessingstepimage",
        CustomApiTables.Api, CustomApiTables.RequestParameter, CustomApiTables.ResponseProperty, ServiceEndpointSchema.Table,
    };

    private sealed record Snapshot(IReadOnlyList<PluginStep> Enabled, IReadOnlyDictionary<Guid, PluginStep> ById,
        IReadOnlySet<string> CustomMessages);

    private readonly GenerationalSnapshot<Snapshot> _snapshot = new();

    /// <summary>Drops the cached snapshot: the next call reloads from the database.</summary>
    public void Invalidate() => _snapshot.Invalidate();

    /// <summary>Invalidates the registry after the commit of the session deleting <paramref name="entity"/>.</summary>
    public Task OnEntityDeletingAsync(Microsoft.Xrm.Sdk.Metadata.EntityMetadata entity, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.OnCommitted(Invalidate);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Enabled steps (statecode 0) of a message (case-insensitive), entity (a filter with that primary object type code, or
    /// no filter), stage and mode, ordered by rank then id. None on <c>serviceendpoint</c> (plan 3.2): its messages are not
    /// events in Dataverse, and a step without a table filter must never see its <c>authvalue</c>.
    /// </summary>
    public async Task<IReadOnlyList<PluginStep>> GetStepsAsync(string message, string? entity, int stage, int mode, CancellationToken ct)
    {
        if (string.Equals(entity, ServiceEndpointSchema.Table, StringComparison.OrdinalIgnoreCase)) return [];
        var snap = await GetSnapshotAsync(ct);
        return snap.Enabled
            .Where(s => s.Stage == stage && s.Mode == mode
                && string.Equals(s.Message, message, StringComparison.OrdinalIgnoreCase)
                && (s.PrimaryEntity is null || string.Equals(s.PrimaryEntity, entity, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.Rank).ThenBy(s => s.Id)
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="message"/> (case-insensitive) is a <c>sdkmessage</c> row that is not one of the
    /// predefined messages of the <see cref="SdkMessageCatalog"/>: a custom action (or custom API).
    /// </summary>
    public async Task<bool> IsCustomMessageAsync(string message, CancellationToken ct) =>
        (await GetSnapshotAsync(ct)).CustomMessages.Contains(message);

    /// <summary>A step by id, also if disabled; null only if it does not exist.</summary>
    public async Task<PluginStep?> GetStepAsync(Guid stepId, CancellationToken ct) =>
        (await GetSnapshotAsync(ct)).ById.GetValueOrDefault(stepId);

    /// <summary>True when the step exists and is enabled (statecode 0).</summary>
    public async Task<bool> IsEnabledAsync(Guid stepId, CancellationToken ct) =>
        (await GetSnapshotAsync(ct)).Enabled.Any(s => s.Id == stepId);

    /// <summary>The assembly bytes (the base64 <c>content</c> column), read on demand and not cached.</summary>
    public async Task<byte[]> GetAssemblyContentAsync(Guid assemblyId, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT content FROM data.pluginassembly WHERE pluginassemblyid = @id");
        cmd.AddParameter("@id", DbType.Guid, assemblyId);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is not string b64 || b64.Length == 0)
            throw new InvalidOperationException($"Plug-in assembly {assemblyId} not found or has no content.");
        return Convert.FromBase64String(b64);
    }

    private Task<Snapshot> GetSnapshotAsync(CancellationToken ct) => _snapshot.GetAsync(LoadAsync, ct);

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);

        var images = new Dictionary<Guid, List<PluginStepImage>>();
        await using (var cmd = conn.Command(
            "SELECT sdkmessageprocessingstepimageid, sdkmessageprocessingstepid, imagetype, entityalias, attributes, messagepropertyname " +
            "FROM data.sdkmessageprocessingstepimage WHERE sdkmessageprocessingstepid IS NOT NULL ORDER BY sdkmessageprocessingstepimageid"))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var stepId = rd.GetGuid(1);
                if (!images.TryGetValue(stepId, out var list)) images[stepId] = list = [];
                list.Add(new PluginStepImage(rd.GetGuid(0), rd.IsDBNull(2) ? 0 : rd.GetInt32(2),
                    rd.IsDBNull(3) ? "" : rd.GetString(3), ParseCsv(rd.IsDBNull(4) ? null : rd.GetString(4)),
                    rd.IsDBNull(5) ? "" : rd.GetString(5)));
            }
        }

        var sql = $"""
            SELECT s.sdkmessageprocessingstepid, s.name, m.name, f.primaryobjecttypecode, s.stage, s.mode, s.rank,
                   s.filteringattributes, s.configuration, s.impersonatinguserid, t.plugintypeid, t.typename,
                   a.pluginassemblyid, CAST(a.versionnumber AS bigint), s.statecode, s.eventhandlertype, s.asyncautodelete,
                   e.serviceendpointid, e.name, e.url, e.authtype, s.eventhandler
            FROM data.sdkmessageprocessingstep s
            JOIN data.sdkmessage m ON m.sdkmessageid = s.sdkmessageid
            LEFT JOIN data.sdkmessagefilter f ON f.sdkmessagefilterid = s.sdkmessagefilterid
            LEFT JOIN data.plugintype t ON t.plugintypeid = COALESCE(CASE WHEN s.eventhandlertype IS NULL OR s.eventhandlertype = 4602
                THEN s.eventhandler END, s.plugintypeid)
            LEFT JOIN data.pluginassembly a ON a.pluginassemblyid = t.pluginassemblyid
            LEFT JOIN data.serviceendpoint e ON s.eventhandlertype = {ServiceEndpointSchema.ObjectTypeCode} AND e.serviceendpointid = s.eventhandler
            """;
        var all = new List<PluginStep>();
        var enabled = new List<PluginStep>();
        await using (var cmd = conn.Command(sql))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                // A step on a service endpoint (eventhandlertype 4618) posts to its webhook (plan 3.2); its secret is read below.
                // A step whose plug-in type, assembly or endpoint is gone runs nothing (as the inner joins this replaced).
                var onEndpoint = !rd.IsDBNull(15) && rd.GetInt32(15) == ServiceEndpointSchema.ObjectTypeCode;
                WebhookEndpoint? endpoint = null;
                if (onEndpoint)
                {
                    if (rd.IsDBNull(17) || rd.IsDBNull(19) || !Uri.TryCreate(rd.GetString(19), UriKind.Absolute, out var url))
                    {
                        // Never the url (its query may hold a secret): the step and the endpoint it names.
                        _logger.LogWarning("Step {StepId} is not run: its service endpoint {EndpointId} does not exist or has no valid url.",
                            rd.GetGuid(0), rd.IsDBNull(21) ? null : rd.GetGuid(21));
                        continue;
                    }
                    endpoint = new WebhookEndpoint(rd.GetGuid(17), rd.IsDBNull(18) ? "" : rd.GetString(18), url,
                        rd.IsDBNull(20) ? 0 : rd.GetInt32(20), null);
                }
                else if (rd.IsDBNull(10) || rd.IsDBNull(12)) continue;
                var id = rd.GetGuid(0);
                var entity = rd.IsDBNull(3) ? null : rd.GetString(3);
                if (string.Equals(entity, "none", StringComparison.OrdinalIgnoreCase) || entity?.Length == 0) entity = null;
                var step = new PluginStep(
                    id, rd.IsDBNull(1) ? "" : rd.GetString(1), rd.GetString(2), entity,
                    rd.IsDBNull(4) ? 0 : rd.GetInt32(4), rd.IsDBNull(5) ? 0 : rd.GetInt32(5), rd.IsDBNull(6) ? 0 : rd.GetInt32(6),
                    ParseCsv(rd.IsDBNull(7) ? null : rd.GetString(7)),
                    rd.IsDBNull(8) ? null : rd.GetString(8), null,
                    rd.IsDBNull(9) ? null : rd.GetGuid(9),
                    onEndpoint ? Guid.Empty : rd.GetGuid(10), onEndpoint || rd.IsDBNull(11) ? "" : rd.GetString(11),
                    onEndpoint ? Guid.Empty : rd.GetGuid(12), onEndpoint || rd.IsDBNull(13) ? 0 : rd.GetInt64(13),
                    images.TryGetValue(id, out var imgs) ? imgs : [], !rd.IsDBNull(16) && rd.GetBoolean(16), endpoint);
                all.Add(step);
                if (rd.IsDBNull(14) || rd.GetInt32(14) == 0) enabled.Add(step);
            }
        }
        // The write-only authvalue of each endpoint, read once per endpoint after the reader is closed.
        var session = new DataSession(conn, null);
        var authValues = new Dictionary<Guid, string?>();
        foreach (var endpointId in all.Where(s => s.Endpoint is not null).Select(s => s.Endpoint!.Id).Distinct())
            authValues[endpointId] = await secrets.ReadAsync(endpointId, session, ct);
        if (authValues.Count > 0)
        {
            PluginStep WithSecret(PluginStep s) =>
                s.Endpoint is { } e ? s with { Endpoint = e with { AuthValue = authValues[e.Id] } } : s;
            all = all.Select(WithSecret).ToList();
            enabled = enabled.Select(WithSecret).ToList();
        }

        var custom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.Command("SELECT name FROM data.sdkmessage WHERE name IS NOT NULL"))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
            {
                var name = rd.GetString(0);
                if (!SdkMessageCatalog.IsCatalogMessage(name)) custom.Add(name);
            }
        }

        return new Snapshot(enabled, all.ToDictionary(s => s.Id), custom);
    }

    /// <summary>CSV → lowercase trimmed names; null/blank → null.</summary>
    private static string[]? ParseCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant()).ToArray();
        return parts.Length == 0 ? null : parts;
    }
}

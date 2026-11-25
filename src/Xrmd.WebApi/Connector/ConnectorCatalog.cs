using System.Text.Json.Nodes;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Model.Security;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Security;

namespace Xrmd.WebApi.Connector;

/// <summary>One table of the connector's table picker.</summary>
public sealed record ConnectorTable(string LogicalName, string EntitySetName, string DisplayName);

/// <summary>One custom API of the connector's action picker.</summary>
public sealed record ConnectorCustomApi(string UniqueName, string DisplayName);

/// <summary>
/// X.12a: the tables a caller may read through the custom connector (the designer's table list). Only tables with an entity
/// set, no intersect tables, and those whose read privilege every principal of the call holds.
/// </summary>
public sealed class ConnectorCatalog(IMetadataStore store, SecurityGuard guard, FieldSecurity fields, CustomApiRegistry apis,
    IDbConnectionFactory factory)
{
    // Used by the actions of the next tasks (secured columns, custom APIs).
    internal FieldSecurity Fields { get; } = fields;
    internal CustomApiRegistry Apis { get; } = apis;

    /// <summary>The readable tables, ordered by display name (in the caller's language), then logical name.</summary>
    public async Task<IReadOnlyList<ConnectorTable>> ListTablesAsync(Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        int language;
        await using (var conn = await factory.OpenAsync(ct))
            language = await UserLanguage.ForAsync(caller, new DataSession(conn, null), ct, store);

        var result = new List<ConnectorTable>();
        foreach (var table in store.GetAllTables())
        {
            if (table.LogicalName is not { } name || table.EntitySetName is not { } set || table.IsIntersect == true) continue;
            if (!await guard.HoldsPrivilegeAsync(caller, PrivilegeNames.For(AccessMask.ReadAccess, table), ct)) continue;
            result.Add(new ConnectorTable(name, set, FormattedValues.TableName(name, table, language)));
        }
        return result
            .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.LogicalName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The table named <paramref name="logicalName"/> when the caller may read it.</summary>
    /// <exception cref="XrmdFaultException">404 ResourceNotFound for an unknown name (an entity set name is unknown too);
    /// 403 PrivilegeDenied without read.</exception>
    public async Task<TableDefinition> GetReadableTableAsync(Caller caller, string logicalName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var table = store.GetTable(logicalName);
        if (table is null || table.EntitySetName is null || table.IsIntersect == true)
            throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{logicalName}'.", 404);
        await guard.DemandPrivilegeAsync(caller, PrivilegeNames.For(AccessMask.ReadAccess, table), table.LogicalName!, ct);
        return table;
    }

    /// <summary>
    /// The JSON Schema of a table for <paramref name="use"/>, in the caller's language and under their field security.
    /// <paramref name="name"/> is a logical name, else an entity set name (the connector's actions carry the entity set name).
    /// </summary>
    /// <exception cref="XrmdFaultException">404 for an unknown table; 403 without read on it.</exception>
    public async Task<JsonObject> GetTableSchemaAsync(Caller caller, string name, SchemaUse use, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var logical = store.GetTable(name)?.LogicalName ?? store.GetTableBySetName(name)?.LogicalName ?? name;
        var table = await GetReadableTableAsync(caller, logical, ct);
        int language;
        await using (var conn = await factory.OpenAsync(ct))
            language = await UserLanguage.ForAsync(caller, new DataSession(conn, null), ct, store);
        var permissions = await Fields.ForAsync(caller, table.LogicalName!, ct);
        return TableSchemaBuilder.Build(table, use, store.GetOneToManyDefinitions(), store, permissions, language);
    }

    /// <summary>The unbound, non-function, non-private custom APIs whose execute privilege the caller holds, by unique name.</summary>
    public async Task<IReadOnlyList<ConnectorCustomApi>> ListCustomApisAsync(Caller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var result = new List<ConnectorCustomApi>();
        foreach (var api in await Apis.AllAsync(ct))
        {
            if (api.BindingType != 0 || api.IsFunction || api.IsPrivate) continue;
            if (api.ExecutePrivilegeName is { } privilege && !await guard.HoldsPrivilegeAsync(caller, privilege, ct)) continue;
            result.Add(new ConnectorCustomApi(api.UniqueName, api.UniqueName));
        }
        return result.OrderBy(a => a.UniqueName, StringComparer.Ordinal).ToList();
    }

    /// <summary>The custom API <paramref name="uniqueName"/> when the connector can run it for the caller.</summary>
    /// <exception cref="XrmdFaultException">404 ResourceNotFound for an unknown name; 400 InvalidArgument for a bound, function
    /// or private API; 403 PrivilegeDenied without the execute privilege.</exception>
    public async Task<CustomApiDefinition> GetRunnableCustomApiAsync(Caller caller, string uniqueName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var api = await Apis.FindAsync(uniqueName, ct)
            ?? throw new XrmdFaultException(ErrorCodes.ResourceNotFound, $"Resource not found for the segment '{uniqueName}'.", 404);
        var why = api.BindingType != 0 ? "bound" : api.IsFunction ? "a function" : api.IsPrivate ? "private" : null;
        if (why is not null)
            throw new XrmdFaultException(ErrorCodes.InvalidArgument,
                $"The custom API '{api.UniqueName}' cannot be run by the connector: it is {why}.", 400);
        if (api.ExecutePrivilegeName is { } privilege)
            await guard.DemandPrivilegeAsync(caller, privilege, api.UniqueName, ct);
        return api;
    }
}

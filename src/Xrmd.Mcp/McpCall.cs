using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using ModelContextProtocol;
using Xrmd.Abstractions;
using Xrmd.Admin;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.WebApi;

namespace Xrmd.Mcp;

/// <summary>
/// Runs a tool or resource body as the token's user: the services are the HTTP request's (<see cref="HttpContext.RequestServices"/>),
/// the caller is resolved first by the request's <see cref="CallerResolver"/> (403 for a user who is not a member, disabled
/// or without role), and every write or read goes through <see cref="OrganizationService"/> or the metadata store after
/// that check: there is no privileged path. A <see cref="XrmdFaultException"/> or <see cref="XrmdValidationException"/>
/// becomes an <see cref="McpException"/> whose message is <c>0x80040220: …</c> (the Web API error code and message), which
/// the SDK answers as a tool result with <c>isError</c> true (a JSON-RPC error for a resource), never a transport error.
/// </summary>
internal static class McpCall
{
    /// <summary>Tool output: compact JSON, non-ASCII and markup characters kept as they are (the text is read, not embedded).</summary>
    internal static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Runs <paramref name="body"/> as the caller; with <paramref name="adminOnly"/>, only for a System Administrator.</summary>
    /// <exception cref="McpException">The Core's fault, as <c>{code}: {message}</c>.</exception>
    public static async Task<T> RunAsync<T>(IServiceProvider services, Func<IServiceProvider, Caller, Task<T>> body, CancellationToken ct,
        bool adminOnly = false)
    {
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext
                   ?? throw new McpException("The xrmverse MCP server runs over HTTP only.");
        var request = http.RequestServices;
        try
        {
            var caller = adminOnly ? await AdministratorAsync(request, ct) : await request.GetRequiredService<CallerResolver>().ResolveAsync(ct);
            return await body(request, caller);
        }
        catch (XrmdFaultException fault)
        {
            throw new McpException($"{ODataErrorMiddleware.FormatCode(fault.ErrorCode)}: {fault.Message}", fault);
        }
        catch (XrmdValidationException invalid)
        {
            throw new McpException($"{ODataErrorMiddleware.FormatCode(ErrorCodes.InvalidArgument)}: {invalid.Message}", invalid);
        }
    }

    /// <summary>A tool: <see cref="RunAsync{T}"/>, answering the JSON as text.</summary>
    public static async Task<string> ToolAsync(IServiceProvider services, Func<IServiceProvider, Caller, Task<JsonNode>> body,
        CancellationToken ct, bool adminOnly = false) =>
        (await RunAsync(services, body, ct, adminOnly)).ToJsonString(Json);

    /// <summary>A value as JSON with the Web defaults (camelCase).</summary>
    public static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, WebJson)!;

    /// <summary>
    /// The caller, when the user (and, impersonating, the impersonator) is a System Administrator, as the Admin API's
    /// <see cref="AdminOnlyFilter"/> decides: checked before anything reads the tool's input.
    /// </summary>
    private static async Task<Caller> AdministratorAsync(IServiceProvider request, CancellationToken ct)
    {
        Caller caller;
        try
        {
            caller = await request.GetRequiredService<CallerResolver>().ResolveAsync(ct);
        }
        catch (XrmdFaultException ex) when (ex.HttpStatus == StatusCodes.Status403Forbidden)
        {
            throw AdminRequired();
        }
        if (!await request.GetRequiredService<SecurityGuard>().IsAdministratorAsync(caller, ct)) throw AdminRequired();
        return caller;
    }

    private static XrmdFaultException AdminRequired() =>
        new(ErrorCodes.PrivilegeDenied, AdminOnlyFilter.Message, StatusCodes.Status403Forbidden);

    /// <summary>The table with that logical name; 400 as the Core's own lookup when unknown.</summary>
    public static EntityMetadata Table(IMetadataStore store, string? logicalName) =>
        (string.IsNullOrWhiteSpace(logicalName) ? null : store.GetEntity(logicalName))
        // Exact Dataverse text: client code matches on "was not found in the MetadataCache" to detect an unknown table.
        ?? throw XrmdFaultException.InvalidArgument(
            $"The entity with a name = '{logicalName}' with namemapping = 'Logical' was not found in the MetadataCache.");

    /// <summary>A record id: a GUID, else 400 naming the value.</summary>
    public static Guid Id(string? text) =>
        Guid.TryParse(text, out var id) ? id : throw XrmdFaultException.InvalidArgument($"'{text}' is not a valid record id (a GUID).");

    /// <summary>The <c>data</c> argument of a write: a JSON object in Web API form.</summary>
    public static JsonObject Data(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(data.GetRawText())!.AsObject()
            : throw XrmdFaultException.InvalidArgument("'data' must be a JSON object of column values, in Web API form.");

    /// <summary>A <c>take</c> argument between 1 and <see cref="McpLimits.MaxPageSize"/>.</summary>
    public static int Take(int take) =>
        take is >= 1 and <= McpLimits.MaxPageSize
            ? take
            : throw XrmdFaultException.InvalidArgument($"'take' must be between 1 and {McpLimits.MaxPageSize}, got {take}.");

    /// <summary>
    /// The records as the Web API writes them (without formatted values) in a JSON array: a secured column the caller cannot
    /// read is left out, as the Web API leaves it out (ruling F3), rather than written as null. <paramref name="tables"/> are the
    /// tables of the query (the root and its links); the unreadable columns are looked up only when a value is null.
    /// </summary>
    public static async Task<JsonArray> RecordsAsync(OrganizationService service, EntityCollection collection, EntityMetadata m,
        IMetadataStore store, IEnumerable<string> tables, CancellationToken ct, IReadOnlyDictionary<string, ODataExpand>? expands = null)
    {
        var unreadable = collection.Entities.Any(e => e.Attributes.Values.Any(v => v is null or AliasedValue { Value: null }))
            ? await service.UnreadableColumnsAsync(tables.Prepend(m.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct)
            : null;
        var relationships = store.GetOneToManyRelationships();
        var records = new JsonArray();
        foreach (var e in collection.Entities) records.Add(ODataJson.ToRecordJson(e, m, false, expands, relationships, unreadable));
        return records;
    }
}

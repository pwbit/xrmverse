using System.CommandLine;
using System.Text.Json;
using Xrmd.Cli.Http;
using Xrmd.Cli.Output;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd customapi list|show|create|delete</c>: the custom API definitions of the organization over the Web API as the
/// signed-in user (plan 15; create and delete are plan 3.1). The server expands only single-valued navigation properties, so three queries are sent: the APIs with
/// their plug-in type (<c>$expand=plugintypeid</c>, the navigation property the server names after the lookup column), then the
/// parameters and the properties, which are joined to their API by <c>_customapiid_value</c>. <c>create</c> posts the API and then its
/// fields, one request each (the server has no deep insert), and deletes the API again when a field is refused; <c>delete</c> removes an
/// API by unique name.
/// </summary>
public static class CustomApiCommands
{
    private const string ApiQuery =
        "customapis?$select=customapiid,uniquename,name,displayname,description,bindingtype,boundentitylogicalname,isfunction,isprivate" +
        "&$expand=plugintypeid($select=typename)&$orderby=uniquename";
    private const string ParameterQuery =
        "customapirequestparameters?$select=uniquename,name,type,isoptional,logicalentityname,_customapiid_value&$orderby=uniquename";
    private const string PropertyQuery =
        "customapiresponseproperties?$select=uniquename,name,type,logicalentityname,_customapiid_value&$orderby=uniquename";

    /// <summary>The labels of the global choice <c>customapifieldtype</c>, by value.</summary>
    private static readonly string[] FieldTypes =
    [
        "Boolean", "DateTime", "Decimal", "Entity", "EntityCollection", "EntityReference", "Float", "Integer", "Money", "Picklist",
        "String", "StringArray", "Guid",
    ];

    public static Command Build(CliContext ctx) => new("customapi", "List, show, create and delete the custom APIs of the organization.")
    {
        List(ctx),
        Show(ctx),
        Create(ctx),
        Delete(ctx),
    };

    private const string SolutionHeader = "MSCRM.SolutionUniqueName";
    private static readonly string[] FieldArrays = ["requestparameters", "responseproperties"];

    private static async Task<JsonElement?> FindApiAsync(XrmdClient client, string uniqueName, CancellationToken ct) =>
        (await client.GetAllPagesAsync("customapis?$select=customapiid,uniquename&$filter=uniquename eq " + UserRoleLookup.Literal(uniqueName), ct))
            .Select(r => (JsonElement?)r).FirstOrDefault();

    private static Command Create(CliContext ctx)
    {
        var file = new Option<FileInfo>("--file") { Description = "JSON file: the customapis Web API body plus the arrays requestparameters and responseproperties (bodies without customapiid); an optional \"plugintype\" names the plug-in type.", Required = true };
        var solution = new Option<string?>("--solution") { Description = "Unique name of the solution that receives the API and its fields (MSCRM.SolutionUniqueName header)." };
        var command = new Command("create", "Create a custom API with its request parameters and response properties from a JSON file; deletes the API again if a field is refused.") { file, solution };
        command.SetAction(async (parse, ct) =>
        {
            var info = parse.GetValue(file)!;
            if (!info.Exists) throw new CliException(ExitCodes.Usage, $"File '{info.FullName}' not found.");
            Dictionary<string, JsonElement> document;
            try
            {
                document = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(info.FullName, ct))
                           ?? throw new JsonException("The file is empty.");
            }
            catch (JsonException ex)
            {
                throw new CliException(ExitCodes.Usage, $"'{info.Name}' is not a JSON object: {ex.Message}");
            }
            if (document.TryGetValue("plugintype", out var pluginType) && pluginType.ValueKind != JsonValueKind.String)
                throw new CliException(ExitCodes.Usage, $"'{info.Name}': \"plugintype\" must be the type name as a string.");
            if (!document.TryGetValue("uniquename", out var unique) || unique.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(unique.GetString()))
                throw new CliException(ExitCodes.Usage, $"'{info.Name}' has no \"uniquename\".");
            var fields = FieldArrays.ToDictionary(a => a, a => document.TryGetValue(a, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().ToList() : []);
            if (fields.Values.SelectMany(f => f).Any(f => f.ValueKind != JsonValueKind.Object))
                throw new CliException(ExitCodes.Usage, $"'{info.Name}': requestparameters and responseproperties hold JSON objects.");

            var client = ctx.CreateClient();
            var body = document.Where(p => p.Key is not ("requestparameters" or "responseproperties" or "plugintype"))
                .ToDictionary(p => p.Key, p => (object?)p.Value);
            if (document.TryGetValue("plugintype", out var plugin))
            {
                var typeName = plugin.GetString()!;
                var types = await client.GetAllPagesAsync($"plugintypes?$select=plugintypeid&$filter=typename eq {UserRoleLookup.Literal(typeName)}", ct);
                if (types.Count == 0) throw new CliException(ExitCodes.NotFound, $"Plug-in type '{typeName}' not found: register the assembly first (xrmd plugin).");
                body["plugintypeid@odata.bind"] = $"/plugintypes({Str(types[0], "plugintypeid")})";
            }

            var headers = parse.GetValue(solution) is { } name ? new Dictionary<string, string> { [SolutionHeader] = name } : null;
            var created = await client.SendJsonAsync(HttpMethod.Post, "customapis", body, headers, ct);
            var apiId = created.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server did not return the id of the new custom API.");
            try
            {
                foreach (var (array, set) in new[] { ("requestparameters", "customapirequestparameters"), ("responseproperties", "customapiresponseproperties") })
                    foreach (var field in fields[array])
                    {
                        var fieldBody = field.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
                        fieldBody["CustomAPIId@odata.bind"] = $"/customapis({apiId})";
                        await client.SendJsonAsync(HttpMethod.Post, set, fieldBody, headers, ct);
                    }
            }
            catch (Exception ex) when (ex is CliException or OperationCanceledException)
            {
                // A cancelled Create is compensated too (the undo ignores the cancelled token), then the original error continues.
                try { await client.SendJsonAsync(HttpMethod.Delete, $"customapis({apiId})", cancellationToken: CancellationToken.None); }
                catch (CliException undo)
                {
                    ctx.Env.Err.WriteLine($"Could not delete the partly created custom API {apiId}: {undo.Message} Delete it with `xrmd customapi delete {unique.GetString()}`.");
                }
                throw;
            }

            ctx.WriteResult(new { customapiid = apiId, uniqueName = unique.GetString(), requestparameters = fields["requestparameters"].Count, responseproperties = fields["responseproperties"].Count },
                $"Created custom API '{unique.GetString()}' ({apiId}) with {fields["requestparameters"].Count} request parameter(s) and {fields["responseproperties"].Count} response property(ies).");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Delete(CliContext ctx)
    {
        var name = new Argument<string>("uniquename") { Description = "Unique name of the custom API, e.g. new_Ping." };
        var command = new Command("delete", "Delete a custom API with its request parameters and response properties (refused while a step is registered on it).") { name, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var uniqueName = parse.GetValue(name)!;
            var client = ctx.CreateClient();
            var api = await FindApiAsync(client, uniqueName, ct) ?? throw new CliException(ExitCodes.NotFound, $"Custom API '{uniqueName}' not found.");
            var id = Str(api, "customapiid");
            await ctx.ConfirmDestructiveAsync($"This deletes custom API '{uniqueName}' with its request parameters and response properties. Continue?");
            await client.SendJsonAsync(HttpMethod.Delete, $"customapis({id})", cancellationToken: ct);
            ctx.WriteResult(new { customapiid = id, uniqueName, deleted = true }, $"Deleted custom API '{uniqueName}' ({id}).");
            return ExitCodes.Ok;
        });
        return command;
    }

    private sealed record Field(string UniqueName, string Type, bool? Optional, string? Table);

    private sealed record Api(string UniqueName, string? Name, string? DisplayName, string? Description, string Binding, string Kind,
        bool Private, string? PluginType, List<Field> Parameters, List<Field> Properties);

    private static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Binding(JsonElement row)
    {
        var table = Str(row, "boundentitylogicalname");
        return row.TryGetProperty("bindingtype", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() switch { 1 => "entity:" + table, 2 => "collection:" + table, _ => "global" }
            : "global";
    }

    private static Dictionary<string, List<Field>> FieldsByApi(IEnumerable<JsonElement> rows, bool withOptional) =>
        rows.GroupBy(r => Str(r, "_customapiid_value") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g
                .Select(f => new Field(Str(f, "uniquename") ?? "", TypeName(f), withOptional ? Flag(f, "isoptional") : null, Str(f, "logicalentityname")))
                .OrderBy(f => f.UniqueName, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);

    private static string TypeName(JsonElement field) =>
        field.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() is >= 0 and var i && i < FieldTypes.Length ? FieldTypes[i] : t.GetInt32().ToString()
            : "";

    /// <summary>
    /// The APIs of <paramref name="rows"/> with their parameters and properties; <paramref name="fieldFilter"/> (a <c>$filter</c>) narrows the
    /// two field queries, none reads them all.
    /// </summary>
    private static async Task<List<Api>> JoinAsync(XrmdClient client, IReadOnlyList<JsonElement> rows, string? fieldFilter, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        string Narrowed(string query) => fieldFilter is null ? query : query + "&$filter=" + fieldFilter;
        var parameters = FieldsByApi(await client.GetAllPagesAsync(Narrowed(ParameterQuery), ct), withOptional: true);
        var properties = FieldsByApi(await client.GetAllPagesAsync(Narrowed(PropertyQuery), ct), withOptional: false);
        return rows.Select(row =>
        {
            var id = Str(row, "customapiid") ?? "";
            return new Api(Str(row, "uniquename") ?? "", Str(row, "name"), Str(row, "displayname"), Str(row, "description"), Binding(row),
                Flag(row, "isfunction") ? "function" : "action", Flag(row, "isprivate"),
                row.TryGetProperty("plugintypeid", out var type) && type.ValueKind == JsonValueKind.Object ? Str(type, "typename") : null,
                parameters.GetValueOrDefault(id) ?? [], properties.GetValueOrDefault(id) ?? []);
        }).ToList();
    }

    private static Command List(CliContext ctx)
    {
        var command = new Command("list", "List the custom APIs: unique name, binding, kind, private, plug-in type, number of parameters and properties.");
        command.SetAction(async (_, ct) =>
        {
            var client = ctx.CreateClient();
            // The two field queries read every row: the list shows counts, and a count needs all of them (the client follows every page).
            var apis = await JoinAsync(client, await client.GetAllPagesAsync(ApiQuery, ct), null, ct);
            ctx.Write(apis.Select(a => new
            {
                uniqueName = a.UniqueName, binding = a.Binding, kind = a.Kind, @private = a.Private, pluginType = a.PluginType,
                parameters = a.Parameters.Count, properties = a.Properties.Count,
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Show(CliContext ctx)
    {
        var name = new Argument<string>("uniquename") { Description = "Unique name of the custom API, e.g. xt_Echo." };
        var command = new Command("show", "Show a custom API with its request parameters and response properties.") { name };
        command.SetAction(async (parse, ct) =>
        {
            var uniqueName = parse.GetValue(name)!;
            var client = ctx.CreateClient();
            var rows = await client.GetAllPagesAsync(ApiQuery + "&$filter=uniquename eq " + UserRoleLookup.Literal(uniqueName), ct);
            var api = (await JoinAsync(client, rows, rows.Count == 0 ? null : $"_customapiid_value eq {Str(rows[0], "customapiid")}", ct)).FirstOrDefault()
                      ?? throw new CliException(ExitCodes.NotFound, $"Custom API '{uniqueName}' not found.");
            if (ctx.Format == OutputFormat.Json)
            {
                ctx.Write(new
                {
                    uniqueName = api.UniqueName, name = api.Name, displayName = api.DisplayName, description = api.Description,
                    binding = api.Binding, kind = api.Kind, @private = api.Private, pluginType = api.PluginType,
                    parameters = api.Parameters.Select(p => new { uniqueName = p.UniqueName, type = p.Type, optional = p.Optional, table = p.Table }).ToList(),
                    properties = api.Properties.Select(p => new { uniqueName = p.UniqueName, type = p.Type, table = p.Table }).ToList(),
                });
                return ExitCodes.Ok;
            }

            ctx.Write(new
            {
                uniqueName = api.UniqueName, name = api.Name, displayName = api.DisplayName, description = api.Description,
                binding = api.Binding, kind = api.Kind, @private = api.Private, pluginType = api.PluginType,
            });
            ctx.Env.Out.WriteLine();
            ctx.Env.Out.WriteLine("Parameters");
            ctx.Write(api.Parameters.Select(p => new { uniqueName = p.UniqueName, type = p.Type, optional = p.Optional, table = p.Table }).ToList());
            ctx.Env.Out.WriteLine();
            ctx.Env.Out.WriteLine("Properties");
            ctx.Write(api.Properties.Select(p => new { uniqueName = p.UniqueName, type = p.Type, table = p.Table }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }
}

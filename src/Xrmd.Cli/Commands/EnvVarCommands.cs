using System.CommandLine;
using System.Text.Json;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd envvar list|get|set|unset</c> over the Web API as the signed-in user (#167, ruling E6): the
/// <c>environmentvariabledefinitions</c> and <c>environmentvariablevalues</c> entity sets. <c>set</c> creates the definition's value
/// row or updates it; <c>unset</c> deletes it, so the default value applies again. An unknown schema name is exit 4, a refusal
/// of the server exit 5; a value for a Secret definition is refused here with exit 2, because such values are kept in Azure Key
/// Vault, which xrmverse does not support.
/// </summary>
public static class EnvVarCommands
{
    private const string DefinitionSelect = "environmentvariabledefinitionid,schemaname,displayname,type,defaultvalue,isrequired";
    private const string ValueSelect = "environmentvariablevalueid,value,_environmentvariabledefinitionid_value";
    private const int SecretType = 100000005;

    public static Command Build(CliContext ctx) => new("envvar", "List environment variables and set or remove their values (Web API).")
    {
        List(ctx), Get(ctx), Set(ctx), Unset(ctx),
    };

    private static string Literal(string value) => UserRoleLookup.Literal(value);

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool Bool(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string TypeName(int? type) => type switch
    {
        100000000 or null => "String",
        100000001 => "Number",
        100000002 => "Boolean",
        100000003 => "JSON",
        100000004 => "Data source",
        SecretType => "Secret",
        _ => type.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static Argument<string> SchemaArgument() => new("schemaname") { Description = "Schema name of the environment variable, e.g. new_ServiceUrl." };

    private static async Task<JsonElement> FindDefinitionAsync(XrmdClient client, string schemaName, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync(
            $"environmentvariabledefinitions?$select={DefinitionSelect}&$filter=schemaname eq {Literal(schemaName.Trim())}", ct);
        return rows.Count == 0 ? throw new CliException(ExitCodes.NotFound, $"Environment variable '{schemaName}' not found.") : rows[0];
    }

    private static async Task<JsonElement?> FindValueAsync(XrmdClient client, string definitionId, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync(
            $"environmentvariablevalues?$select={ValueSelect}&$filter=_environmentvariabledefinitionid_value eq {definitionId}", ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private static object Shape(JsonElement definition, JsonElement? value)
    {
        var current = value is { } v ? Str(v, "value") : null;
        var fallback = Str(definition, "defaultvalue");
        return new
        {
            schemaname = Str(definition, "schemaname"),
            displayname = Str(definition, "displayname"),
            type = TypeName(Int(definition, "type")),
            isrequired = Bool(definition, "isrequired"),
            @default = fallback,
            value = current,
            effective = current ?? fallback,
            environmentvariabledefinitionid = Str(definition, "environmentvariabledefinitionid"),
            environmentvariablevalueid = value is { } row ? Str(row, "environmentvariablevalueid") : null,
        };
    }

    private static Command List(CliContext ctx)
    {
        var list = new Command("list", "List the environment variables: schema name, type, default value and current value.");
        list.SetAction(async (_, ct) =>
        {
            var client = ctx.CreateClient();
            var definitions = await client.GetAllPagesAsync($"environmentvariabledefinitions?$select={DefinitionSelect}&$orderby=schemaname", ct);
            var values = (await client.GetAllPagesAsync($"environmentvariablevalues?$select={ValueSelect}", ct))
                .Where(v => Str(v, "_environmentvariabledefinitionid_value") is not null)
                .GroupBy(v => Str(v, "_environmentvariabledefinitionid_value")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            JsonElement? ValueOf(JsonElement d) =>
                Str(d, "environmentvariabledefinitionid") is { } id && values.TryGetValue(id, out var v) ? v : null;
            if (ctx.Format == Output.OutputFormat.Json)
                ctx.Write(definitions.Select(d => Shape(d, ValueOf(d))).ToList());
            else
                ctx.Write(definitions.Select(d =>
                {
                    var value = ValueOf(d) is { } row ? Str(row, "value") : null;
                    return new
                    {
                        schemaname = Str(d, "schemaname"),
                        type = TypeName(Int(d, "type")),
                        @default = Str(d, "defaultvalue") ?? "(none)",
                        value = value ?? "(none)",
                    };
                }).ToList());
            return ExitCodes.Ok;
        });
        return list;
    }

    private static Command Get(CliContext ctx)
    {
        var schema = SchemaArgument();
        var get = new Command("get", "Show one environment variable: its definition, default value, current value and the value in effect.") { schema };
        get.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var definition = await FindDefinitionAsync(client, parse.GetValue(schema)!, ct);
            var value = await FindValueAsync(client, Str(definition, "environmentvariabledefinitionid")!, ct);
            ctx.Write(Shape(definition, value));
            return ExitCodes.Ok;
        });
        return get;
    }

    private static Command Set(CliContext ctx)
    {
        var schema = SchemaArgument();
        var text = new Argument<string>("value") { Description = "The value for this environment (not checked against the variable's type)." };
        var set = new Command("set", "Set the value of an environment variable in this organization (creates or updates its value row).") { schema, text };
        set.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var definition = await FindDefinitionAsync(client, parse.GetValue(schema)!, ct);
            var name = Str(definition, "schemaname");
            if (Int(definition, "type") == SecretType)
                throw new CliException(ExitCodes.Usage,
                    $"Environment variable '{name}' is a Secret: secret values live in Azure Key Vault, which xrmverse does not support.");
            var definitionId = Str(definition, "environmentvariabledefinitionid")!;
            var newValue = parse.GetValue(text)!;
            var existing = await FindValueAsync(client, definitionId, ct);
            if (existing is { } row)
                await client.SendJsonAsync(HttpMethod.Patch, $"environmentvariablevalues({Str(row, "environmentvariablevalueid")})",
                    new Dictionary<string, object?> { ["value"] = newValue }, cancellationToken: ct);
            else
                await client.SendJsonAsync(HttpMethod.Post, "environmentvariablevalues", new Dictionary<string, object?>
                {
                    ["value"] = newValue,
                    ["EnvironmentVariableDefinitionId@odata.bind"] = $"/environmentvariabledefinitions({definitionId})",
                }, cancellationToken: ct);
            ctx.WriteResult(new { schemaname = name, value = newValue, created = existing is null },
                $"Environment variable {name} is now '{newValue}'.");
            return ExitCodes.Ok;
        });
        return set;
    }

    private static Command Unset(CliContext ctx)
    {
        var schema = SchemaArgument();
        var unset = new Command("unset", "Remove the value of an environment variable, so that its default value applies again.") { schema };
        unset.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var definition = await FindDefinitionAsync(client, parse.GetValue(schema)!, ct);
            var name = Str(definition, "schemaname");
            var existing = await FindValueAsync(client, Str(definition, "environmentvariabledefinitionid")!, ct);
            if (existing is { } row)
                await client.SendJsonAsync(HttpMethod.Delete, $"environmentvariablevalues({Str(row, "environmentvariablevalueid")})", cancellationToken: ct);
            ctx.WriteResult(new { schemaname = name, removed = existing is not null },
                existing is null ? $"Environment variable {name} has no value; nothing to remove." : $"Removed the value of environment variable {name}.");
            return ExitCodes.Ok;
        });
        return unset;
    }
}

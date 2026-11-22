using System.CommandLine;
using System.Text.Json;
using System.Xml.Linq;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd webhook create|list|show|update|delete</c>: the webhooks of the organization, that is the <c>serviceendpoint</c> rows
/// of contract 8, over the Web API as the signed-in user. The server validates the registration (url, port, auth); the CLI builds the
/// <c>authvalue</c> (the settings XML of the header and query string types) and never prints it: the server does not return it,
/// and the query string of the url, which may carry a key, is not printed either. Steps are bound with
/// <c>xrmd plugin step add --webhook</c>.
/// </summary>
public static class WebhookCommands
{
    private const int WebhookContract = 8;
    private const string Select = "serviceendpointid,name,url,contract,authtype,isauthvalueset";
    private const string StepsWithEndpoint =
        "sdkmessageprocessingsteps?$select=sdkmessageprocessingstepid,name,stage,mode,rank,_sdkmessageid_value,_sdkmessagefilterid_value"
        + "&$expand=eventhandler_serviceendpoint($select=serviceendpointid,name)";

    private static readonly Dictionary<string, int> AuthTypes = new(StringComparer.Ordinal) { ["key"] = 4, ["header"] = 5, ["query"] = 6 };

    public static Command Build(CliContext ctx) => new("webhook", "Register and list webhooks (Web API serviceendpoints, contract Webhook).")
    {
        Create(ctx),
        List(ctx),
        Show(ctx),
        Update(ctx),
        Delete(ctx),
    };

    private static Option<string> AuthOption(bool required)
    {
        var auth = new Option<string>("--auth") { Description = "Authentication: key (a Webhook Key sent as the 'code' query parameter), header or query.", Required = required };
        auth.AcceptOnlyFromAmong("key", "header", "query");
        return auth;
    }

    private static Option<string[]> ValueOption() => new("--value")
    {
        Description = "With key: the key. With header or query: K=V, repeatable.",
        AllowMultipleArgumentsPerToken = false,
    };

    /// <summary>The <c>authvalue</c> for the auth type: the key as it is, or the settings XML of the K=V pairs. Exit 2 for a malformed value; the value is never echoed.</summary>
    internal static string BuildAuthValue(string auth, string[]? values)
    {
        if (values is null || values.Length == 0) throw new CliException(ExitCodes.Usage, $"--auth {auth} needs --value.");
        if (auth == "key")
        {
            if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]))
                throw new CliException(ExitCodes.Usage, "--auth key takes exactly one non-blank --value.");
            return values[0];
        }

        var settings = new XElement("settings");
        for (var i = 0; i < values.Length; i++)
        {
            var at = values[i].IndexOf('=');
            if (at < 1 || string.IsNullOrWhiteSpace(values[i][..at]))
                throw new CliException(ExitCodes.Usage, $"--value #{i + 1} for --auth {auth} must be K=V with a non-blank name.");
            settings.Add(new XElement("setting", new XAttribute("name", values[i][..at]), new XAttribute("value", values[i][(at + 1)..])));
        }

        return settings.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The url without its query string and fragment: a query may carry a key.</summary>
    internal static string? WithoutQuery(string? url)
    {
        if (url is null) return null;
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    }

    private static string AuthName(int? authtype) => authtype switch { 4 => "key", 5 => "header", 6 => "query", { } n => n.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => "" };

    private static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool Bool(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The webhook named <paramref name="name"/> (exit 4 when there is none, 5 when the name is ambiguous).</summary>
    internal static async Task<JsonElement> FindAsync(Http.XrmdClient client, string name, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync(
            $"serviceendpoints?$select={Select}&$filter=name eq {UserRoleLookup.Literal(name)} and contract eq {WebhookContract}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"Webhook '{name}' not found.");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} webhooks are named '{name}'.");
        return rows[0];
    }

    private static Command Create(CliContext ctx)
    {
        var name = new Option<string>("--name") { Description = "Name of the webhook.", Required = true };
        var url = new Option<string>("--url") { Description = "https or http url of the receiver (port 80 or 443 unless Pipeline:WebhookAllowAnyPort is set).", Required = true };
        var auth = AuthOption(required: true);
        var value = ValueOption();
        var command = new Command("create", "Register a webhook; prints its id.") { name, url, auth, value };
        command.SetAction(async (parse, ct) =>
        {
            var authName = parse.GetValue(auth)!;
            var authValue = BuildAuthValue(authName, parse.GetValue(value));
            var body = new Dictionary<string, object?>
            {
                ["name"] = parse.GetValue(name),
                ["url"] = parse.GetValue(url),
                ["contract"] = WebhookContract,
                ["authtype"] = AuthTypes[authName],
                ["authvalue"] = authValue,
            };
            var response = await ctx.CreateClient().SendJsonAsync(HttpMethod.Post, "serviceendpoints", body, cancellationToken: ct);
            var id = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server returned no id for the new webhook.");
            ctx.WriteResult(
                new { serviceendpointid = id, name = parse.GetValue(name), url = WithoutQuery(parse.GetValue(url)), authtype = authName },
                $"Created webhook '{parse.GetValue(name)}' ({id}).");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static IEnumerable<(Guid Endpoint, JsonElement Step)> WebhookSteps(IEnumerable<JsonElement> steps)
    {
        foreach (var step in steps)
            if (step.TryGetProperty("eventhandler_serviceendpoint", out var e) && e.ValueKind == JsonValueKind.Object
                && Guid.TryParse(UserRoleLookup.Str(e, "serviceendpointid"), out var id))
                yield return (id, step);
    }

    private static Command List(CliContext ctx)
    {
        var command = new Command("list", "List the webhooks: name, url without its query, auth type and step count.");
        command.SetAction(async (_, ct) =>
        {
            var client = ctx.CreateClient();
            var endpoints = await client.GetAllPagesAsync($"serviceendpoints?$select={Select}&$filter=contract eq {WebhookContract}&$orderby=name", ct);
            var counts = WebhookSteps(await client.GetAllPagesAsync(StepsWithEndpoint, ct)).GroupBy(s => s.Endpoint).ToDictionary(g => g.Key, g => g.Count());
            ctx.Write(endpoints.Select(e => new
            {
                name = UserRoleLookup.Str(e, "name"),
                url = WithoutQuery(UserRoleLookup.Str(e, "url")),
                authtype = AuthName(Int(e, "authtype")),
                steps = Guid.TryParse(UserRoleLookup.Str(e, "serviceendpointid"), out var id) && counts.TryGetValue(id, out var n) ? n : 0,
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Show(CliContext ctx)
    {
        var name = new Argument<string>("name") { Description = "Name of the webhook." };
        var command = new Command("show", "Show a webhook and its steps (the auth value is never shown).") { name };
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var hook = await FindAsync(client, parse.GetValue(name)!, ct);
            var id = Guid.Parse(UserRoleLookup.Str(hook, "serviceendpointid")!);
            var messages = (await client.GetAllPagesAsync("sdkmessages?$select=sdkmessageid,name", ct))
                .ToDictionary(m => UserRoleLookup.Str(m, "sdkmessageid")!, m => UserRoleLookup.Str(m, "name"), StringComparer.OrdinalIgnoreCase);
            var filters = (await client.GetAllPagesAsync("sdkmessagefilters?$select=sdkmessagefilterid,primaryobjecttypecode", ct))
                .ToDictionary(f => UserRoleLookup.Str(f, "sdkmessagefilterid")!, f => UserRoleLookup.Str(f, "primaryobjecttypecode"), StringComparer.OrdinalIgnoreCase);
            var steps = WebhookSteps(await client.GetAllPagesAsync(StepsWithEndpoint, ct)).Where(s => s.Endpoint == id).Select(s => new
            {
                stepId = UserRoleLookup.Str(s.Step, "sdkmessageprocessingstepid"),
                name = UserRoleLookup.Str(s.Step, "name"),
                message = UserRoleLookup.Str(s.Step, "_sdkmessageid_value") is { } m && messages.TryGetValue(m, out var mn) ? mn : null,
                entity = UserRoleLookup.Str(s.Step, "_sdkmessagefilterid_value") is { } f && filters.TryGetValue(f, out var fn) ? fn : null,
                stage = Int(s.Step, "stage"),
                mode = Int(s.Step, "mode") == 1 ? "async" : "sync",
                rank = Int(s.Step, "rank"),
            }).ToList();
            ctx.Write(new
            {
                serviceendpointid = id,
                name = UserRoleLookup.Str(hook, "name"),
                url = WithoutQuery(UserRoleLookup.Str(hook, "url")),
                authtype = AuthName(Int(hook, "authtype")),
                isauthvalueset = Bool(hook, "isauthvalueset"),
                steps,
            });
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Update(CliContext ctx)
    {
        var name = new Argument<string>("name") { Description = "Name of the webhook." };
        var url = new Option<string?>("--url") { Description = "New url." };
        var auth = AuthOption(required: false);
        var value = ValueOption();
        var command = new Command("update", "Change the url and/or the authentication of a webhook (--auth needs --value).") { name, url, auth, value };
        command.SetAction(async (parse, ct) =>
        {
            var newUrl = parse.GetValue(url);
            var authName = parse.GetResult(auth) is null ? null : parse.GetValue(auth);
            var values = parse.GetValue(value);
            if (newUrl is null && authName is null && (values is null || values.Length == 0))
                throw new CliException(ExitCodes.Usage, "Nothing to update: give --url and/or --auth with --value.");
            if (authName is null && values is { Length: > 0 }) throw new CliException(ExitCodes.Usage, "--value needs --auth.");

            var body = new Dictionary<string, object?>();
            if (newUrl is not null) body["url"] = newUrl;
            if (authName is not null)
            {
                body["authtype"] = AuthTypes[authName];
                body["authvalue"] = BuildAuthValue(authName, values);
            }

            var client = ctx.CreateClient();
            var hook = await FindAsync(client, parse.GetValue(name)!, ct);
            var id = Guid.Parse(UserRoleLookup.Str(hook, "serviceendpointid")!);
            await client.SendJsonAsync(new HttpMethod("PATCH"), $"serviceendpoints({id})", body, cancellationToken: ct);
            ctx.WriteResult(new { serviceendpointid = id, name = parse.GetValue(name), updated = true }, $"Updated webhook '{parse.GetValue(name)}' ({id}).");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Delete(CliContext ctx)
    {
        var name = new Argument<string>("name") { Description = "Name of the webhook." };
        var command = new Command("delete", "Delete a webhook: deletes the endpoint, its steps and their images; queued jobs are cancelled (3/32).") { name, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var hook = await FindAsync(client, parse.GetValue(name)!, ct);
            var id = Guid.Parse(UserRoleLookup.Str(hook, "serviceendpointid")!);
            await ctx.ConfirmDestructiveAsync($"This deletes webhook '{parse.GetValue(name)}'. Continue?");
            await client.SendJsonAsync(HttpMethod.Delete, $"serviceendpoints({id})", cancellationToken: ct);
            ctx.WriteResult(new { serviceendpointid = id, name = parse.GetValue(name), deleted = true }, $"Deleted webhook '{parse.GetValue(name)}' ({id}).");
            return ExitCodes.Ok;
        });
        return command;
    }
}

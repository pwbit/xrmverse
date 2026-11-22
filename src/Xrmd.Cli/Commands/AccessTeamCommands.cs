using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd access-team template list|create|delete</c>, <c>enable|disable &lt;table&gt;</c> and <c>add|remove</c> over the Web API as the
/// signed-in user (plan 2.16): the <c>teamtemplates</c> entity set, the <c>AutoCreateAccessTeams</c> flag of a table (a PUT of its
/// definition, as <c>fieldsecurity secure</c> does) and the bound actions <c>AddUserToRecordTeam</c> and <c>RemoveUserFromRecordTeam</c>.
/// Every refusal is the server's: a missing privilege is a 403 (exit 3), an invalid mask, table or limit a 400 (exit 5), an unknown
/// table, template or user exit 4; a template name that is not unique for the table is exit 5.
/// </summary>
public static class AccessTeamCommands
{
    /// <summary>The rights a template can grant: the <c>AccessRights</c> values the server accepts.</summary>
    private static readonly (string Name, int Bit)[] Rights =
    [
        ("read", 1), ("write", 2), ("append", 4), ("appendto", 16), ("delete", 65536), ("share", 262144), ("assign", 524288),
    ];

    private const string ActionPrefix = "Microsoft.Dynamics.CRM.";

    public static Command Build(CliContext ctx) => new("access-team", "Manage access team templates, switch tables on for access teams, and add or remove users on a record's access team (Web API).")
    {
        Template(ctx),
        Switch(ctx, enable: true),
        Switch(ctx, enable: false),
        Membership(ctx, add: true),
        Membership(ctx, add: false),
    };

    private static string Literal(string value) => UserRoleLookup.Literal(value);

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static Option<string> Required(string name, string description) => new(name) { Description = description, Required = true };

    /// <summary>The mask of <c>read,write,...</c> (case does not matter); exit 2 for an unknown right or an empty list.</summary>
    private static int ParseRights(string text)
    {
        var mask = 0;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var right = Array.FindIndex(Rights, r => string.Equals(r.Name, part, StringComparison.OrdinalIgnoreCase));
            if (right < 0)
                throw new CliException(ExitCodes.Usage, $"Unknown access right '{part}'. Use: {string.Join(", ", Rights.Select(r => r.Name))}.");
            mask |= Rights[right].Bit;
        }
        return mask != 0 ? mask : throw new CliException(ExitCodes.Usage, "Give at least one access right with --rights.");
    }

    private static string RightsText(int mask) => string.Join(",", Rights.Where(r => (mask & r.Bit) != 0).Select(r => r.Name));

    private sealed record TableInfo(string LogicalName, int ObjectTypeCode, string PrimaryId);

    private static async Task<TableInfo> TableAsync(XrmdClient client, string table, CancellationToken ct)
    {
        var entity = await client.GetJsonAsync($"EntityDefinitions(LogicalName={Literal(table)})?$select=LogicalName,ObjectTypeCode,PrimaryIdAttribute", ct);
        var code = entity.TryGetProperty("ObjectTypeCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : (int?)null;
        return new TableInfo(
            Str(entity, "LogicalName") ?? table,
            code ?? throw new CliException(ExitCodes.Unexpected, $"The server did not report the object type code of '{table}'."),
            Str(entity, "PrimaryIdAttribute") ?? throw new CliException(ExitCodes.Unexpected, $"The server did not report the primary id of '{table}'."));
    }

    // ---- templates ----------------------------------------------------------------------------------------------------

    private static Command Template(CliContext ctx)
    {
        var listTable = new Option<string?>("--table") { Description = "Only the templates of this table (logical name)." };
        var list = new Command("list", "List the access team templates.") { listTable };
        list.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var filter = "";
            if (parse.GetValue(listTable) is { } tableName)
                filter = $"&$filter=objecttypecode eq {(await TableAsync(client, tableName, ct)).ObjectTypeCode}";
            var rows = await client.GetAllPagesAsync(
                $"teamtemplates?$select=teamtemplateid,teamtemplatename,objecttypecode,defaultaccessrightsmask,description&$orderby=teamtemplatename{filter}", ct);
            var tables = (await client.GetAllPagesAsync("EntityDefinitions?$select=LogicalName,ObjectTypeCode", ct))
                .Where(e => e.TryGetProperty("ObjectTypeCode", out var c) && c.ValueKind == JsonValueKind.Number)
                .GroupBy(e => e.GetProperty("ObjectTypeCode").GetInt32())
                .ToDictionary(g => g.Key, g => Str(g.First(), "LogicalName"));
            ctx.Write(rows.Select(r =>
            {
                var code = r.GetProperty("objecttypecode").GetInt32();
                var mask = r.GetProperty("defaultaccessrightsmask").GetInt32();
                return new
                {
                    teamtemplateid = Str(r, "teamtemplateid"),
                    table = tables.TryGetValue(code, out var table) ? table : code.ToString(),
                    name = Str(r, "teamtemplatename"),
                    rights = RightsText(mask),
                    mask,
                    description = Str(r, "description"),
                };
            }).ToList());
            return ExitCodes.Ok;
        });

        var table = Required("--table", "Logical name of the table (it must have access teams switched on).");
        var name = Required("--name", "Name of the template.");
        var rights = Required("--rights", "The rights the access team gets, comma separated: read, write, append, appendto, delete, share, assign.");
        var description = new Option<string?>("--description") { Description = "Description of the template." };
        var create = new Command("create", "Create an access team template for a table.") { table, name, rights, description };
        create.SetAction(async (parse, ct) =>
        {
            var mask = ParseRights(parse.GetValue(rights)!);
            var client = ctx.CreateClient();
            var info = await TableAsync(client, parse.GetValue(table)!, ct);
            var templateName = parse.GetValue(name)!;
            var body = new Dictionary<string, object?>
            {
                ["teamtemplatename"] = templateName,
                ["objecttypecode"] = info.ObjectTypeCode,
                ["defaultaccessrightsmask"] = mask,
            };
            if (parse.GetValue(description) is { } text) body["description"] = text;
            var response = await client.SendJsonAsync(HttpMethod.Post, "teamtemplates", body, cancellationToken: ct);
            var id = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server did not return the id of the new template.");
            ctx.WriteResult(new { teamtemplateid = id, table = info.LogicalName, name = templateName, rights = RightsText(mask), mask },
                $"Created access team template '{templateName}' ({id}) on {info.LogicalName}: {RightsText(mask)}.");
            return ExitCodes.Ok;
        });

        var deletedId = new Argument<string>("id") { Description = "Id of the template." };
        var delete = new Command("delete", "Delete an access team template; the access teams made from it are deleted too.") { deletedId, ctx.YesOption };
        delete.SetAction(async (parse, ct) =>
        {
            var text = parse.GetValue(deletedId)!;
            if (!Guid.TryParse(text, out var id)) throw new CliException(ExitCodes.Usage, $"'{text}' is not a template id.");
            await ctx.ConfirmDestructiveAsync($"This deletes access team template {id} and the access teams made from it. Continue?");
            await ctx.CreateClient().SendJsonAsync(HttpMethod.Delete, $"teamtemplates({id})", cancellationToken: ct);
            ctx.WriteResult(new { teamtemplateid = id, deleted = true }, $"Deleted access team template {id}.");
            return ExitCodes.Ok;
        });

        return new Command("template", "List, create or delete access team templates.") { list, create, delete };
    }

    // ---- enable / disable ---------------------------------------------------------------------------------------------

    private static Command Switch(CliContext ctx, bool enable)
    {
        var table = new Argument<string>("table") { Description = "Logical name of the table." };
        var command = new Command(enable ? "enable" : "disable", enable
            ? "Switch Auto-create access teams on for a table (user- or team-owned tables only)."
            : "Switch Auto-create access teams off for a table (refused while it has templates).") { table };
        command.SetAction(async (parse, ct) =>
        {
            var tableName = parse.GetValue(table)!;
            var client = ctx.CreateClient();
            var path = $"EntityDefinitions(LogicalName={Literal(tableName)})";
            var response = await client.SendJsonAsync(HttpMethod.Get, path, cancellationToken: ct);
            var body = JsonNode.Parse((response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET {path}.")).GetRawText())!.AsObject();
            body.Remove("@odata.context");
            body["AutoCreateAccessTeams"] = enable;
            await client.SendJsonAsync(HttpMethod.Put, path, body, cancellationToken: ct);
            ctx.WriteResult(new { table = tableName, autoCreateAccessTeams = enable },
                $"Table {tableName}: Auto-create access teams is {(enable ? "on" : "off")}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- add / remove -------------------------------------------------------------------------------------------------

    /// <summary>A user by id (its <c>systemuserid</c> or Entra object id) or by domain name; exit 4 when there is none.</summary>
    private static async Task<Guid> UserIdAsync(XrmdClient client, string user, CancellationToken ct)
    {
        var filter = Guid.TryParse(user, out var id)
            ? $"systemuserid eq {id} or azureactivedirectoryobjectid eq {id}"
            : $"domainname eq {Literal(user)}";
        var rows = await client.GetAllPagesAsync($"systemusers?$select=systemuserid&$filter={filter}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"User '{user}' not found.");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} users match '{user}'.");
        return Guid.Parse(Str(rows[0], "systemuserid")!);
    }

    /// <summary>A template of the table by id, or by name; exit 4 when there is none, 5 when the name is not unique for the table.</summary>
    private static async Task<Guid> TemplateIdAsync(XrmdClient client, TableInfo table, string template, CancellationToken ct)
    {
        if (Guid.TryParse(template, out var id)) return id;
        var rows = await client.GetAllPagesAsync(
            $"teamtemplates?$select=teamtemplateid&$filter=teamtemplatename eq {Literal(template)} and objecttypecode eq {table.ObjectTypeCode}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"Access team template '{template}' not found for {table.LogicalName}.");
        if (rows.Count > 1)
            throw new CliException(ExitCodes.Rejected, $"{rows.Count} access team templates of {table.LogicalName} are named '{template}'. Use the template id.");
        return Guid.Parse(Str(rows[0], "teamtemplateid")!);
    }

    private static Command Membership(CliContext ctx, bool add)
    {
        var table = Required("--table", "Logical name of the record's table.");
        var record = Required("--record", "Id of the record.");
        var user = Required("--user", "The user: its id (systemuserid or Entra object id) or domain name.");
        var template = Required("--template", "The access team template: its id, or its name (unique for the table).");
        var command = new Command(add ? "add" : "remove", add
            ? "Add a user to the access team of a record; the team is created from the template when the record has none."
            : "Remove a user from the access team of a record.") { table, record, user, template };
        command.SetAction(async (parse, ct) =>
        {
            var recordText = parse.GetValue(record)!;
            if (!Guid.TryParse(recordText, out var recordId)) throw new CliException(ExitCodes.Usage, $"'{recordText}' is not a record id.");
            var client = ctx.CreateClient();
            var info = await TableAsync(client, parse.GetValue(table)!, ct);
            var userId = await UserIdAsync(client, parse.GetValue(user)!, ct);
            var templateId = await TemplateIdAsync(client, info, parse.GetValue(template)!, ct);
            var body = new Dictionary<string, object?>
            {
                ["Record"] = new Dictionary<string, object?> { ["@odata.type"] = ActionPrefix + info.LogicalName, [info.PrimaryId] = recordId },
                ["TeamTemplate"] = new Dictionary<string, object?> { ["teamtemplateid"] = templateId },
            };
            var action = add ? "AddUserToRecordTeam" : "RemoveUserFromRecordTeam";
            var response = await client.SendJsonAsync(HttpMethod.Post, $"systemusers({userId})/{ActionPrefix}{action}", body, cancellationToken: ct);
            var teamId = response.Json is { } json ? Str(json, "AccessTeamId") : null;
            ctx.WriteResult(new { accessTeamId = teamId },
                $"{(add ? "Added" : "Removed")} user '{parse.GetValue(user)}' {(add ? "to" : "from")} the access team ({teamId}) of {info.LogicalName} {recordId}.");
            return ExitCodes.Ok;
        });
        return command;
    }
}

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd fieldsecurity profiles|profile|permission|assign|unassign|secure|unsecure</c> over the Web API as the signed-in user
/// (plan 14): the field security profiles (<c>fieldsecurityprofiles</c>), their column permissions (<c>fieldpermissions</c>),
/// the users and teams that hold them (<c>systemuserprofiles_association</c>, <c>teamprofiles_association</c>) and the
/// <c>IsSecured</c> flag of a column (the metadata endpoints). Every refusal is the server's: a missing privilege is a 403
/// (exit 3) whose message names it, a column that cannot be secured a 400 (exit 5), an unknown profile, user, team or
/// permission exit 4.
/// </summary>
public static class FieldSecurityCommands
{
    /// <summary>The values of <c>canread</c>, <c>cancreate</c> and <c>canupdate</c>.</summary>
    private const int Allowed = 4, NotAllowed = 0;
    private const string SystemAdministratorProfileId = "572329c1-a042-4e22-be47-367c6374ea45";

    /// <summary>The attribute properties the server computes: never sent back on a PUT.</summary>
    private static readonly string[] ServerOwned = ["@odata.context", "CanBeSecuredForCreate", "CanBeSecuredForRead", "CanBeSecuredForUpdate"];

    public static Command Build(CliContext ctx) => new("fieldsecurity", "Manage field security: profiles, column permissions, who holds a profile, and secured columns (Web API).")
    {
        Profiles(ctx),
        Profile(ctx),
        Permission(ctx),
        Assign(ctx, assign: true),
        Assign(ctx, assign: false),
        Secure(ctx, secure: true),
        Secure(ctx, secure: false),
    };

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static string Literal(string value) => UserRoleLookup.Literal(value);

    private static string RecordUrl(XrmdClient client, string path) => new Uri(client.BaseUrl, XrmdClient.WebApiPath + path).AbsoluteUri;

    /// <summary>The id of the profile named <paramref name="name"/>; exit 4 when there is none, 5 when the name is ambiguous.</summary>
    private static async Task<Guid> ProfileIdAsync(XrmdClient client, string name, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync($"fieldsecurityprofiles?$select=fieldsecurityprofileid&$filter=name eq {Literal(name)}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"Field security profile '{name}' not found.");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} field security profiles are named '{name}'.");
        return Guid.Parse(Str(rows[0], "fieldsecurityprofileid")!);
    }

    // ---- profiles -----------------------------------------------------------------------------------------------------

    private static Command Profiles(CliContext ctx)
    {
        var command = new Command("profiles", "List the field security profiles.");
        command.SetAction(async (_, ct) =>
        {
            var rows = await ctx.CreateClient().GetAllPagesAsync("fieldsecurityprofiles?$select=fieldsecurityprofileid,name,description&$orderby=name", ct);
            ctx.Write(rows.Select(r => new
            {
                fieldsecurityprofileid = Str(r, "fieldsecurityprofileid"),
                name = Str(r, "name"),
                description = Str(r, "description"),
                readOnly = string.Equals(Str(r, "fieldsecurityprofileid"), SystemAdministratorProfileId, StringComparison.OrdinalIgnoreCase),
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Profile(CliContext ctx)
    {
        var name = new Argument<string>("name") { Description = "Name of the profile." };
        var description = new Option<string?>("--description") { Description = "Description of the profile." };
        var create = new Command("create", "Create a field security profile.") { name, description };
        create.SetAction(async (parse, ct) =>
        {
            var profileName = parse.GetValue(name)!;
            var response = await ctx.CreateClient().SendJsonAsync(HttpMethod.Post, "fieldsecurityprofiles",
                new Dictionary<string, object?> { ["name"] = profileName, ["description"] = parse.GetValue(description) }, cancellationToken: ct);
            var id = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server did not return the id of the new profile.");
            ctx.WriteResult(new { fieldsecurityprofileid = id, name = profileName }, $"Created field security profile '{profileName}' ({id}).");
            return ExitCodes.Ok;
        });

        var deletedName = new Argument<string>("name") { Description = "Name of the profile." };
        var delete = new Command("delete", "Delete a field security profile with its permissions; its users and teams lose it.") { deletedName, ctx.YesOption };
        delete.SetAction(async (parse, ct) =>
        {
            var profileName = parse.GetValue(deletedName)!;
            var client = ctx.CreateClient();
            var id = await ProfileIdAsync(client, profileName, ct);
            await ctx.ConfirmDestructiveAsync($"This deletes field security profile '{profileName}' and its permissions. Continue?");
            await client.SendJsonAsync(HttpMethod.Delete, $"fieldsecurityprofiles({id})", cancellationToken: ct);
            ctx.WriteResult(new { fieldsecurityprofileid = id, name = profileName, deleted = true }, $"Deleted field security profile '{profileName}'.");
            return ExitCodes.Ok;
        });

        return new Command("profile", "Create or delete a field security profile.") { create, delete };
    }

    // ---- permissions --------------------------------------------------------------------------------------------------

    /// <summary>The id of the permission of the profile on the column; null when there is none.</summary>
    private static async Task<Guid?> PermissionIdAsync(XrmdClient client, Guid profile, string table, string column, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync(
            $"fieldpermissions?$select=fieldpermissionid&$filter=_fieldsecurityprofileid_value eq {profile} and entityname eq {Literal(table)} and attributelogicalname eq {Literal(column)}",
            ct);
        return rows.Count > 0 && Guid.TryParse(Str(rows[0], "fieldpermissionid"), out var id) ? id : null;
    }

    private static Command Permission(CliContext ctx)
    {
        (Argument<string> Profile, Argument<string> Table, Argument<string> Column) Arguments() => (
            new Argument<string>("profile") { Description = "Name of the profile." },
            new Argument<string>("table") { Description = "Logical name of the table." },
            new Argument<string>("column") { Description = "Logical name of the secured column." });

        var (profile, table, column) = Arguments();
        var read = new Option<bool>("--read") { Description = "Allow reading the column." };
        var create = new Option<bool>("--create") { Description = "Allow setting the column on create." };
        var update = new Option<bool>("--update") { Description = "Allow updating the column." };
        var set = new Command("set", "Set what the profile allows on a secured column; an option left out is not allowed.") { profile, table, column, read, create, update };
        set.SetAction(async (parse, ct) =>
        {
            var (profileName, tableName, columnName) = (parse.GetValue(profile)!, parse.GetValue(table)!, parse.GetValue(column)!);
            var client = ctx.CreateClient();
            var profileId = await ProfileIdAsync(client, profileName, ct);
            var body = new Dictionary<string, object?>
            {
                ["canread"] = parse.GetValue(read) ? Allowed : NotAllowed,
                ["cancreate"] = parse.GetValue(create) ? Allowed : NotAllowed,
                ["canupdate"] = parse.GetValue(update) ? Allowed : NotAllowed,
            };
            if (await PermissionIdAsync(client, profileId, tableName, columnName, ct) is { } existing)
            {
                await client.SendJsonAsync(HttpMethod.Patch, $"fieldpermissions({existing})", body, cancellationToken: ct);
            }
            else
            {
                body["fieldsecurityprofileid@odata.bind"] = $"fieldsecurityprofiles({profileId})";
                body["entityname"] = tableName;
                body["attributelogicalname"] = columnName;
                await client.SendJsonAsync(HttpMethod.Post, "fieldpermissions", body, cancellationToken: ct);
            }
            var allowed = new[] { ("read", parse.GetValue(read)), ("create", parse.GetValue(create)), ("update", parse.GetValue(update)) }
                .Where(p => p.Item2).Select(p => p.Item1).ToList();
            ctx.WriteResult(new { profile = profileName, table = tableName, column = columnName, read = parse.GetValue(read), create = parse.GetValue(create), update = parse.GetValue(update) },
                $"Profile '{profileName}' on {tableName}.{columnName}: {(allowed.Count == 0 ? "nothing allowed" : string.Join(", ", allowed))}.");
            return ExitCodes.Ok;
        });

        var (removedProfile, removedTable, removedColumn) = Arguments();
        var remove = new Command("remove", "Remove the permission of a profile on a column.") { removedProfile, removedTable, removedColumn };
        remove.SetAction(async (parse, ct) =>
        {
            var (profileName, tableName, columnName) = (parse.GetValue(removedProfile)!, parse.GetValue(removedTable)!, parse.GetValue(removedColumn)!);
            var client = ctx.CreateClient();
            var profileId = await ProfileIdAsync(client, profileName, ct);
            var id = await PermissionIdAsync(client, profileId, tableName, columnName, ct)
                     ?? throw new CliException(ExitCodes.NotFound, $"Profile '{profileName}' has no permission on {tableName}.{columnName}.");
            await client.SendJsonAsync(HttpMethod.Delete, $"fieldpermissions({id})", cancellationToken: ct);
            ctx.WriteResult(new { profile = profileName, table = tableName, column = columnName, removed = true },
                $"Removed the permission of profile '{profileName}' on {tableName}.{columnName}.");
            return ExitCodes.Ok;
        });

        return new Command("permission", "Set or remove the permission of a profile on a secured column.") { set, remove };
    }

    // ---- assign / unassign --------------------------------------------------------------------------------------------

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

    private static async Task<Guid> TeamIdAsync(XrmdClient client, string team, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync($"teams?$select=teamid&$filter=name eq {Literal(team)}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"Team '{team}' not found.");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} teams are named '{team}'.");
        return Guid.Parse(Str(rows[0], "teamid")!);
    }

    private static Command Assign(CliContext ctx, bool assign)
    {
        var verb = assign ? "assign" : "unassign";
        var profile = new Argument<string>("profile") { Description = "Name of the profile." };
        var user = new Option<string?>("--user") { Description = "The user: its id (systemuserid or Entra object id) or domain name." };
        var team = new Option<string?>("--team") { Description = "The team, by name." };
        var command = new Command(verb, assign
            ? "Give a profile to a user (--user) or a team (--team)."
            : "Take a profile from a user (--user) or a team (--team).") { profile, user, team };
        command.SetAction(async (parse, ct) =>
        {
            var profileName = parse.GetValue(profile)!;
            var userText = parse.GetValue(user);
            var teamText = parse.GetValue(team);
            if (userText is null == teamText is null) throw new CliException(ExitCodes.Usage, "Give either --user or --team.");
            var client = ctx.CreateClient();
            var profileId = await ProfileIdAsync(client, profileName, ct);
            var (set, principal, navigation) = userText is not null
                ? ("systemusers", await UserIdAsync(client, userText, ct), "systemuserprofiles_association")
                : ("teams", await TeamIdAsync(client, teamText!, ct), "teamprofiles_association");
            if (assign)
                await client.SendJsonAsync(HttpMethod.Post, $"{set}({principal})/{navigation}/$ref",
                    new Dictionary<string, string> { ["@odata.id"] = RecordUrl(client, $"fieldsecurityprofiles({profileId})") }, cancellationToken: ct);
            else
                await client.SendJsonAsync(HttpMethod.Delete, $"{set}({principal})/{navigation}({profileId})/$ref", cancellationToken: ct);
            var who = userText is not null ? $"user '{userText}'" : $"team '{teamText}'";
            ctx.WriteResult(new { profile = profileName, user = userText, team = teamText, assigned = assign },
                assign ? $"Gave profile '{profileName}' to {who}." : $"Took profile '{profileName}' from {who}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- secure / unsecure --------------------------------------------------------------------------------------------

    private static Command Secure(CliContext ctx, bool secure)
    {
        var verb = secure ? "secure" : "unsecure";
        var table = new Argument<string>("table") { Description = "Logical name of the table." };
        var column = new Argument<string>("column") { Description = "Logical name of the column." };
        var command = new Command(verb, secure
            ? "Secure a column: only System Administrators and the profiles that allow it can then read or write it."
            : "Stop securing a column; its permissions and field shares are removed.") { table, column };
        command.SetAction(async (parse, ct) =>
        {
            var (tableName, columnName) = (parse.GetValue(table)!, parse.GetValue(column)!);
            var client = ctx.CreateClient();
            var path = $"EntityDefinitions(LogicalName={Literal(tableName)})/Attributes(LogicalName={Literal(columnName)})";
            var response = await client.SendJsonAsync(HttpMethod.Get, path, cancellationToken: ct);
            var body = JsonNode.Parse((response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET {path}.")).GetRawText())!.AsObject();
            foreach (var name in ServerOwned) body.Remove(name);
            body["IsSecured"] = secure;
            await client.SendJsonAsync(HttpMethod.Put, path, body, cancellationToken: ct);
            ctx.WriteResult(new { table = tableName, column = columnName, secured = secure },
                $"Column {tableName}.{columnName} is {(secure ? "now secured" : "no longer secured")}.");
            return ExitCodes.Ok;
        });
        return command;
    }
}

using System.CommandLine;
using System.Text.Json;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>Shared lookups of <c>xrmd user</c> and <c>xrmd role</c>; both groups use the Web API (systemusers, roles), as the caller.</summary>
internal static class UserRoleLookup
{
    /// <summary>An OData string literal: quotes doubled, then percent-encoded for the query string.</summary>
    public static string Literal(string value) => Uri.EscapeDataString("'" + value.Replace("'", "''") + "'");

    public static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The logical name of the table's primary id column (<c>PrimaryIdAttribute</c>), which is not always <c>&lt;table&gt;id</c> (activities: <c>activityid</c>).</summary>
    public static async Task<string> PrimaryIdAsync(XrmdClient client, string table, CancellationToken ct)
    {
        var entity = await client.GetJsonAsync($"EntityDefinitions(LogicalName={Literal(table)})?$select=PrimaryIdAttribute", ct);
        return Str(entity, "PrimaryIdAttribute") ?? throw new CliException(ExitCodes.Unexpected, $"The server did not report the primary id of '{table}'.");
    }

    public static async Task<Guid> RootBusinessUnitAsync(XrmdClient client, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync("businessunits?$select=businessunitid&$filter=_parentbusinessunitid_value eq null", ct);
        return rows.Count > 0 && Guid.TryParse(Str(rows[0], "businessunitid"), out var id)
            ? id
            : throw new CliException(ExitCodes.NotFound, "The root business unit was not found.");
    }

    public static async Task<Guid> BusinessUnitByNameAsync(XrmdClient client, string name, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync($"businessunits?$select=businessunitid&$filter=name eq {Literal(name)}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"Business unit '{name}' not found.");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} business units are named '{name}'.");
        return Guid.Parse(Str(rows[0], "businessunitid")!);
    }

    /// <summary>A user by Entra object id (when <paramref name="user"/> is a GUID) or by domain name; exit 4 when there is none.</summary>
    public static async Task<(Guid Id, Guid? BusinessUnitId)> UserAsync(XrmdClient client, string user, CancellationToken ct)
    {
        var filter = Guid.TryParse(user, out var oid)
            ? $"azureactivedirectoryobjectid eq {oid}"
            : $"domainname eq {Literal(user)}";
        var rows = await client.GetAllPagesAsync($"systemusers?$select=systemuserid,_businessunitid_value&$filter={filter}", ct);
        if (rows.Count == 0)
            throw new CliException(ExitCodes.NotFound, $"User '{user}' not found (matched as {(Guid.TryParse(user, out _) ? "Entra object id" : "domain name")}).");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} users match '{user}'.");
        return (Guid.Parse(Str(rows[0], "systemuserid")!), Guid.TryParse(Str(rows[0], "_businessunitid_value"), out var bu) ? bu : null);
    }
}

/// <summary><c>xrmd user add|list</c>.</summary>
public static class UserCommands
{
    public static Command Build(CliContext ctx) => new("user", "Add and list users (Web API systemusers).")
    {
        Add(ctx),
        List(ctx),
    };

    private static Command Add(CliContext ctx)
    {
        var objectId = new Option<string>("--object-id") { Description = "Entra object id (GUID) of the user.", Required = true };
        var name = new Option<string>("--name") { Description = "Full name.", Required = true };
        var domainName = new Option<string?>("--domain-name") { Description = "Domain name (sign-in name); default: the object id." };
        var businessUnit = new Option<string?>("--business-unit") { Description = "Name of the business unit; default: the root business unit." };
        var command = new Command("add", "Create a user from an Entra identity.") { objectId, name, domainName, businessUnit };
        command.SetAction(async (parse, ct) =>
        {
            var oidText = parse.GetValue(objectId)!;
            if (!Guid.TryParse(oidText, out var oid)) throw new CliException(ExitCodes.Usage, $"--object-id must be a GUID: '{oidText}'.");
            var client = ctx.CreateClient();
            var bu = parse.GetValue(businessUnit) is { } buName
                ? await UserRoleLookup.BusinessUnitByNameAsync(client, buName, ct)
                : await UserRoleLookup.RootBusinessUnitAsync(client, ct);
            var body = new Dictionary<string, object?>
            {
                ["fullname"] = parse.GetValue(name),
                ["domainname"] = parse.GetValue(domainName) ?? oid.ToString(),
                ["azureactivedirectoryobjectid"] = oid,
                ["businessunitid@odata.bind"] = $"/businessunits({bu})",
            };
            var response = await client.SendJsonAsync(HttpMethod.Post, "systemusers", body, cancellationToken: ct);
            var id = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server returned no OData-EntityId for the new user.");
            ctx.WriteResult(new { systemuserid = id, azureactivedirectoryobjectid = oid, businessunitid = bu }, $"Created user {id} (object id {oid}).");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command List(CliContext ctx)
    {
        var command = new Command("list", "List the users.");
        command.SetAction(async (_, ct) =>
        {
            var rows = await ctx.CreateClient().GetAllPagesAsync(
                "systemusers?$select=systemuserid,fullname,domainname,azureactivedirectoryobjectid,isdisabled,_businessunitid_value&$orderby=fullname", ct);
            ctx.Write(rows.Select(r => new
            {
                systemuserid = UserRoleLookup.Str(r, "systemuserid"),
                fullname = UserRoleLookup.Str(r, "fullname"),
                domainname = UserRoleLookup.Str(r, "domainname"),
                azureactivedirectoryobjectid = UserRoleLookup.Str(r, "azureactivedirectoryobjectid"),
                isdisabled = r.TryGetProperty("isdisabled", out var d) && d.ValueKind is JsonValueKind.True or JsonValueKind.False && d.GetBoolean(),
                businessunitid = UserRoleLookup.Str(r, "_businessunitid_value"),
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }
}

/// <summary><c>xrmd role list|assign</c>.</summary>
public static class RoleCommands
{
    public static Command Build(CliContext ctx) => new("role", "List roles and assign them to users (Web API roles).")
    {
        List(ctx),
        Assign(ctx),
    };

    private static Command List(CliContext ctx)
    {
        var command = new Command("list", "List the roles.");
        command.SetAction(async (_, ct) =>
        {
            var rows = await ctx.CreateClient().GetAllPagesAsync("roles?$select=roleid,name,_businessunitid_value&$orderby=name", ct);
            ctx.Write(rows.Select(r => new
            {
                roleid = UserRoleLookup.Str(r, "roleid"),
                name = UserRoleLookup.Str(r, "name"),
                businessunitid = UserRoleLookup.Str(r, "_businessunitid_value"),
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Assign(CliContext ctx)
    {
        var role = new Argument<string>("role") { Description = "Role name (roles are per business unit: the one of the user's business unit)." };
        var user = new Option<string>("--user") { Description = "The user: Entra object id (GUID) or domain name.", Required = true };
        var command = new Command("assign", "Assign a role to a user.") { role, user };
        command.SetAction(async (parse, ct) =>
        {
            var roleName = parse.GetValue(role)!;
            var userText = parse.GetValue(user)!;
            var client = ctx.CreateClient();
            var (userId, businessUnit) = await UserRoleLookup.UserAsync(client, userText, ct);

            var filter = $"name eq {UserRoleLookup.Literal(roleName)}" + (businessUnit is { } bu ? $" and _businessunitid_value eq {bu}" : "");
            var roles = await client.GetAllPagesAsync($"roles?$select=roleid&$filter={filter}", ct);
            if (roles.Count == 0)
                throw new CliException(ExitCodes.NotFound, $"Role '{roleName}' not found" + (businessUnit is { } b ? $" in the business unit of user '{userText}' ({b})." : "."));
            var roleId = Guid.Parse(UserRoleLookup.Str(roles[0], "roleid")!);

            var link = new Dictionary<string, string> { ["@odata.id"] = new Uri(client.BaseUrl, $"{XrmdClient.WebApiPath}roles({roleId})").AbsoluteUri };
            await client.SendJsonAsync(HttpMethod.Post, $"systemusers({userId})/systemuserroles_association/$ref", link, cancellationToken: ct);
            ctx.WriteResult(new { systemuserid = userId, roleid = roleId, role = roleName }, $"Assigned role '{roleName}' to user {userId}.");
            return ExitCodes.Ok;
        });
        return command;
    }
}

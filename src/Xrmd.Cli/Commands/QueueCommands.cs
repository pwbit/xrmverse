using System.CommandLine;
using System.Text.Json;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd queue list|create|items</c> and <c>queue member add</c> over the Web API as the signed-in user (plan 4.1a, task 7): the
/// <c>queues</c> and <c>queueitems</c> entity sets and the bound action <c>AddPrincipalToQueue</c>. A queue is named by its name
/// or its id. Every refusal is the server's; an unknown queue, user or team is exit 4 (not found), an ambiguous name exit 5.
/// </summary>
public static class QueueCommands
{
    private const string ActionPrefix = "Microsoft.Dynamics.CRM.";

    public static Command Build(CliContext ctx) => new("queue", "List and create queues, add users or teams to a queue, and list a queue's items (Web API).")
    {
        List(ctx),
        Create(ctx),
        Member(ctx),
        Items(ctx),
    };

    private static string Literal(string value) => UserRoleLookup.Literal(value);

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static int Int(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    /// <summary>A queue by id or by name; exit 4 when there is none, 5 when the name is not unique.</summary>
    private static async Task<(Guid Id, string Name)> QueueAsync(XrmdClient client, string queue, CancellationToken ct)
    {
        var filter = Guid.TryParse(queue, out var id) ? $"queueid eq {id}" : $"name eq {Literal(queue)}";
        var rows = await client.GetAllPagesAsync($"queues?$select=queueid,name&$filter={filter}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"Queue '{queue}' not found.");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} queues are named '{queue}'. Use the queue id.");
        return (Guid.Parse(Str(rows[0], "queueid")!), Str(rows[0], "name") ?? queue);
    }

    private static Command List(CliContext ctx)
    {
        var publicOnly = new Option<bool>("--public") { Description = "Only the public queues." };
        var list = new Command("list", "List the queues.") { publicOnly };
        list.SetAction(async (parse, ct) =>
        {
            var filter = parse.GetValue(publicOnly) ? "&$filter=queueviewtype eq 0" : "";
            var rows = await ctx.CreateClient().GetAllPagesAsync(
                $"queues?$select=queueid,name,queueviewtype,statecode&$orderby=name{filter}", ct);
            ctx.Write(rows.Select(r => new
            {
                queueid = Str(r, "queueid"),
                name = Str(r, "name"),
                visibility = Int(r, "queueviewtype") == 1 ? "private" : "public",
                state = Int(r, "statecode") == 0 ? "Active" : "Inactive",
            }).ToList());
            return ExitCodes.Ok;
        });
        return list;
    }

    private static Command Create(CliContext ctx)
    {
        var name = new Argument<string>("name") { Description = "Name of the queue." };
        var isPrivate = new Option<bool>("--private") { Description = "Make the queue private (public by default)." };
        var create = new Command("create", "Create a queue.") { name, isPrivate };
        create.SetAction(async (parse, ct) =>
        {
            var queueName = parse.GetValue(name)!;
            var visibility = parse.GetValue(isPrivate) ? 1 : 0;
            var response = await ctx.CreateClient().SendJsonAsync(HttpMethod.Post, "queues",
                new Dictionary<string, object?> { ["name"] = queueName, ["queueviewtype"] = visibility }, cancellationToken: ct);
            var id = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server did not return the id of the new queue.");
            ctx.WriteResult(new { queueid = id, name = queueName, visibility = visibility == 1 ? "private" : "public" },
                $"Created {(visibility == 1 ? "private" : "public")} queue '{queueName}' ({id}).");
            return ExitCodes.Ok;
        });
        return create;
    }

    private static Command Member(CliContext ctx)
    {
        var queue = new Argument<string>("queue") { Description = "The queue: its name or id." };
        var principal = new Argument<string>("principal") { Description = "A user or a team: its id (a user's Entra object id too), a user's domain name, or a team's name." };
        var add = new Command("add", "Add a user or a team to a queue.") { queue, principal };
        add.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var target = await QueueAsync(client, parse.GetValue(queue)!, ct);
            var (table, key, id) = await PrincipalAsync(client, parse.GetValue(principal)!, ct);
            await client.SendJsonAsync(HttpMethod.Post, $"queues({target.Id})/{ActionPrefix}AddPrincipalToQueue",
                new Dictionary<string, object?> { ["Principal"] = new Dictionary<string, object?> { ["@odata.type"] = ActionPrefix + table, [key] = id } },
                cancellationToken: ct);
            ctx.WriteResult(new { queueid = target.Id, principalType = table, principalId = id },
                $"Added {table} {id} to queue '{target.Name}'.");
            return ExitCodes.Ok;
        });
        return new Command("member", "Manage who works a queue.") { add };
    }

    /// <summary>A user (id, Entra object id or domain name) or a team (id or name); exit 4 when there is none.</summary>
    private static async Task<(string Table, string Key, Guid Id)> PrincipalAsync(XrmdClient client, string text, CancellationToken ct)
    {
        var userFilter = Guid.TryParse(text, out var guid)
            ? $"systemuserid eq {guid} or azureactivedirectoryobjectid eq {guid}"
            : $"domainname eq {Literal(text)}";
        var users = await client.GetAllPagesAsync($"systemusers?$select=systemuserid&$filter={userFilter}", ct);
        if (users.Count == 1) return ("systemuser", "systemuserid", Guid.Parse(Str(users[0], "systemuserid")!));
        var teamFilter = guid != Guid.Empty ? $"teamid eq {guid}" : $"name eq {Literal(text)}";
        var teams = await client.GetAllPagesAsync($"teams?$select=teamid&$filter={teamFilter}", ct);
        if (teams.Count == 1) return ("team", "teamid", Guid.Parse(Str(teams[0], "teamid")!));
        if (users.Count + teams.Count > 1) throw new CliException(ExitCodes.Rejected, $"More than one user or team matches '{text}'. Use an id.");
        throw new CliException(ExitCodes.NotFound, $"User or team '{text}' not found.");
    }

    private static Command Items(CliContext ctx)
    {
        var queue = new Argument<string>("queue") { Description = "The queue: its name or id." };
        var items = new Command("items", "List the items of a queue.") { queue };
        items.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var target = await QueueAsync(client, parse.GetValue(queue)!, ct);
            var rows = await client.GetAllPagesAsync(
                $"queueitems?$select=queueitemid,title,statecode,enteredon,_workerid_value&$filter=_queueid_value eq {target.Id}&$orderby=enteredon", ct);
            ctx.Write(rows.Select(r => new
            {
                queueitemid = Str(r, "queueitemid"),
                title = Str(r, "title"),
                state = Int(r, "statecode") == 0 ? "Active" : "Inactive",
                enteredon = Str(r, "enteredon"),
                worker = Str(r, "_workerid_value"),
            }).ToList());
            return ExitCodes.Ok;
        });
        return items;
    }
}

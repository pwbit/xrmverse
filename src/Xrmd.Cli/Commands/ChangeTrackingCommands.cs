using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd change-tracking status|enable|retention</c> over the Web API as the signed-in user (plan 2.13): the
/// <c>ChangeTrackingEnabled</c> flag of a table (a PUT of its definition, as <c>access-team enable</c> does) and the organization's
/// <c>expirechangetrackingindays</c> (<c>organizations</c> PATCH). Change tracking is one-way, so there is no <c>disable</c>. Every
/// refusal is the server's: a missing privilege is a 403 (exit 3), an unknown table exit 4, an ineligible table or a retention outside
/// 0 to 365 a 400 (exit 5).
/// </summary>
public static class ChangeTrackingCommands
{
    public static Command Build(CliContext ctx) => new("change-tracking", "Switch change tracking on for a table and set how long deleted rows are kept for delta queries (Web API).")
    {
        Status(ctx),
        Enable(ctx),
        Retention(ctx),
    };

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static bool Flag(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The value of a managed boolean (<c>{ "Value": bool }</c>).</summary>
    private static bool Managed(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("Value", out var b) && b.ValueKind == JsonValueKind.True;

    private static async Task<(Guid Id, int Days)> OrganizationAsync(XrmdClient client, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync("organizations?$select=organizationid,expirechangetrackingindays", ct);
        if (rows.Count == 0 || !Guid.TryParse(Str(rows[0], "organizationid"), out var id))
            throw new CliException(ExitCodes.NotFound, "The organization was not found.");
        return (id, rows[0].TryGetProperty("expirechangetrackingindays", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetInt32()
            : throw new CliException(ExitCodes.Unexpected, "The server returned no expirechangetrackingindays for the organization."));
    }

    private static string DaysText(int days) => days == 1 ? "1 day" : $"{days} days";

    // ---- status -------------------------------------------------------------------------------------------------------

    private static Command Status(CliContext ctx)
    {
        var table = new Option<string?>("--table") { Description = "Show this table only (logical name)." };
        var command = new Command("status", "Show the tracked tables and the retention, or with --table whether that table is tracked and can be.") { table };
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            if (parse.GetValue(table) is { } tableName)
            {
                var entity = await client.GetJsonAsync(
                    $"EntityDefinitions(LogicalName={UserRoleLookup.Literal(tableName)})?$select=LogicalName,ChangeTrackingEnabled,CanChangeTrackingBeEnabled", ct);
                var enabled = Flag(entity, "ChangeTrackingEnabled");
                var can = Managed(entity, "CanChangeTrackingBeEnabled");
                ctx.WriteResult(new { table = Str(entity, "LogicalName") ?? tableName, changeTrackingEnabled = enabled, canChangeTrackingBeEnabled = can },
                    $"Table {tableName}: change tracking is {(enabled ? "on" : "off")}; it {(can ? "can" : "cannot")} be turned on.");
                return ExitCodes.Ok;
            }

            var (_, days) = await OrganizationAsync(client, ct);
            var tables = (await client.GetAllPagesAsync("EntityDefinitions?$select=LogicalName,ChangeTrackingEnabled", ct))
                .Where(t => Flag(t, "ChangeTrackingEnabled"))
                .Select(t => Str(t, "LogicalName")!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            if (ctx.Format == Output.OutputFormat.Json)
            {
                ctx.Write(new { retentionDays = days, tables });
                return ExitCodes.Ok;
            }
            ctx.Env.Out.WriteLine($"Deletions are kept for {DaysText(days)}.");
            ctx.Env.Out.WriteLine(tables.Count == 0 ? "No table is tracked." : $"Tracked tables: {string.Join(", ", tables)}");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- enable -------------------------------------------------------------------------------------------------------

    private static Command Enable(CliContext ctx)
    {
        var table = new Argument<string>("table") { Description = "Logical name of the table." };
        var command = new Command("enable", "Switch change tracking on for a table. This cannot be undone.") { table, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var tableName = parse.GetValue(table)!;
            var client = ctx.CreateClient();
            var path = $"EntityDefinitions(LogicalName={UserRoleLookup.Literal(tableName)})";
            var response = await client.SendJsonAsync(HttpMethod.Get, path, cancellationToken: ct);
            var json = response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET {path}.");
            if (Flag(json, "ChangeTrackingEnabled"))
            {
                ctx.WriteResult(new { table = tableName, changeTrackingEnabled = true, alreadyEnabled = true },
                    $"Change tracking is already on for table {tableName}.");
                return ExitCodes.Ok;
            }
            await ctx.ConfirmDestructiveAsync($"Change tracking cannot be turned off again once it is on for {tableName}. Continue?");
            var body = JsonNode.Parse(json.GetRawText())!.AsObject();
            body.Remove("@odata.context");
            body.Remove("CanChangeTrackingBeEnabled");
            body["ChangeTrackingEnabled"] = true;
            await client.SendJsonAsync(HttpMethod.Put, path, body, cancellationToken: ct);
            ctx.WriteResult(new { table = tableName, changeTrackingEnabled = true, alreadyEnabled = false },
                $"Table {tableName}: change tracking is on.");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- retention ----------------------------------------------------------------------------------------------------

    private static Command Retention(CliContext ctx)
    {
        var days = new Argument<int>("days") { Description = "Days to keep the record of deletions: 0 to 365." };
        var command = new Command("retention", "Set how long deleted rows stay in the change log (0 to 365 days).") { days };
        command.SetAction(async (parse, ct) =>
        {
            var value = parse.GetValue(days);
            var client = ctx.CreateClient();
            var (id, _) = await OrganizationAsync(client, ct);
            await client.SendJsonAsync(HttpMethod.Patch, $"organizations({id})", new Dictionary<string, object?> { ["expirechangetrackingindays"] = value }, cancellationToken: ct);
            ctx.WriteResult(new { retentionDays = value }, $"Change tracking retention set to {DaysText(value)}.");
            return ExitCodes.Ok;
        });
        return command;
    }
}

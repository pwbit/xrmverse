using System.CommandLine;
using System.Text.Json;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd hierarchy-security show|enable|disable|depth|exclude|include</c> over the Admin API (plan 2.5, task 6):
/// <c>GET|PUT /admin/hierarchy-security</c>, for System Administrators (a 403 is exit 3). Every refusal is the server's:
/// an unknown table or a depth outside 1 to 100 is a 400 (exit 5).
/// </summary>
public static class HierarchySecurityCommands
{
    private const string Path = "hierarchy-security";

    private sealed record View(bool Enabled, int Depth, IReadOnlyList<string> ExcludedTables);

    public static Command Build(CliContext ctx) => new("hierarchy-security", "Show and change the manager hierarchy security model: on or off, depth, and the tables left out (Admin API).")
    {
        Show(ctx),
        Switch(ctx, enable: true),
        Switch(ctx, enable: false),
        Depth(ctx),
        Exclusion(ctx, exclude: true),
        Exclusion(ctx, exclude: false),
    };

    private static View Read(XrmdResponse response)
    {
        var json = response.Json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for the hierarchy security settings.");
        return new View(
            json.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True,
            json.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0,
            json.TryGetProperty("excludedTables", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : []);
    }

    private static async Task<View> GetAsync(XrmdClient client, CancellationToken ct) =>
        Read(await client.AdminAsync(HttpMethod.Get, Path, cancellationToken: ct));

    private static async Task<View> PutAsync(XrmdClient client, object body, CancellationToken ct) =>
        Read(await client.AdminAsync(HttpMethod.Put, Path, XrmdClient.JsonContent(body), ct));

    private static string Tables(View v) => v.ExcludedTables.Count == 0 ? "none" : string.Join(", ", v.ExcludedTables);

    private static string Summary(View v) =>
        $"Hierarchy security is {(v.Enabled ? "on" : "off")}, depth {v.Depth}, excluded tables: {Tables(v)}.";

    private static Command Show(CliContext ctx)
    {
        var command = new Command("show", "Show whether hierarchy security is on, its depth and the excluded tables.");
        command.SetAction(async (_, ct) =>
        {
            var view = await GetAsync(ctx.CreateClient(), ct);
            ctx.WriteResult(view, Summary(view));
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Switch(CliContext ctx, bool enable)
    {
        var command = new Command(enable ? "enable" : "disable",
            enable ? "Switch the manager hierarchy security model on." : "Switch the manager hierarchy security model off.");
        command.SetAction(async (_, ct) =>
        {
            var view = await PutAsync(ctx.CreateClient(), new { enabled = enable }, ct);
            ctx.WriteResult(view, $"Hierarchy security {(enable ? "enabled" : "disabled")}. {Summary(view)}");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Depth(CliContext ctx)
    {
        var depth = new Argument<int>("n") { Description = "How many levels of reports a manager reaches: 1 to 100." };
        var command = new Command("depth", "Set how many levels of reports a manager reaches (1 to 100).") { depth };
        command.SetAction(async (parse, ct) =>
        {
            var view = await PutAsync(ctx.CreateClient(), new { depth = parse.GetValue(depth) }, ct);
            ctx.WriteResult(view, $"Hierarchy depth set to {view.Depth}. {Summary(view)}");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Exclusion(CliContext ctx, bool exclude)
    {
        var table = new Argument<string>("table") { Description = "Logical name of the table." };
        var command = new Command(exclude ? "exclude" : "include",
            exclude ? "Leave a table out of hierarchy security." : "Put a table back under hierarchy security.") { table };
        command.SetAction(async (parse, ct) =>
        {
            var name = parse.GetValue(table)!;
            var client = ctx.CreateClient();
            var current = await GetAsync(client, ct);
            var tables = current.ExcludedTables.Where(t => !string.Equals(t, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exclude) tables.Add(name);
            var view = await PutAsync(client, new { excludedTables = tables }, ct);
            ctx.WriteResult(view, $"Table '{name}' {(exclude ? "excluded from" : "included in")} hierarchy security. {Summary(view)}");
            return ExitCodes.Ok;
        });
        return command;
    }
}

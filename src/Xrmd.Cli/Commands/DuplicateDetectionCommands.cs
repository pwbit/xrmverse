using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd duplicate-detection status|enable|disable|rule|find|bulk</c> over the Web API as the signed-in user (plan 2.15): the
/// organization's switches (<c>organizations</c> PATCH), the <c>IsDuplicateDetectionEnabled</c> flag of a table (a PUT of its
/// definition, as <c>change-tracking enable</c> does), the <c>duplicaterules</c> and <c>duplicateruleconditions</c> entity sets, the
/// actions <c>PublishDuplicateRule</c>, <c>UnpublishDuplicateRule</c> and <c>BulkDetectDuplicates</c> and the function
/// <c>RetrieveDuplicates</c>. Every refusal is the server's: a missing privilege is a 403 (exit 3), an unknown table or rule exit 4,
/// a 400 or 412 exit 5; the server's text is printed as it is. With <c>--wait</c>, a system job still running after <c>--timeout</c>
/// minutes is exit 6 (the job keeps running on the server); a <c>--timeout</c> of 0 or less is exit 2.
/// </summary>
public static class DuplicateDetectionCommands
{
    private const string ActionPrefix = "Microsoft.Dynamics.CRM.";
    private const int Completed = 3;
    private const int Succeeded = 30;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private const string GlobalSwitch = "isduplicatedetectionenabled";
    private const string CreateUpdateSwitch = "isduplicatedetectionenabledforonlinecreateupdate";
    private const string ImportSwitch = "isduplicatedetectionenabledforimport";
    private const string OfflineSwitch = "isduplicatedetectionenabledforofflinesync";

    private static readonly Dictionary<string, int> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["exact"] = 0, ["first"] = 1, ["last"] = 2, ["date"] = 3, ["datetime"] = 4, ["value"] = 6,
    };

    public static Command Build(CliContext ctx) => new("duplicate-detection", "Switch duplicate detection on or off, manage duplicate detection rules, and look for duplicates (Web API).")
    {
        Status(ctx),
        Switch(ctx, enable: true),
        Switch(ctx, enable: false),
        Rule(ctx),
        Find(ctx),
        Bulk(ctx),
    };

    private static string Literal(string value) => UserRoleLookup.Literal(value);

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static bool Flag(JsonElement row, string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool Managed(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("Value", out var b) && b.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string StatusText(int statuscode) => statuscode switch { 1 => "Publishing", 2 => "Published", _ => "Unpublished" };

    private static Option<string> Required(string name, string description) => new(name) { Description = description, Required = true };

    private const string OrganizationSelect =
        "organizations?$select=organizationid," + GlobalSwitch + "," + CreateUpdateSwitch + "," + ImportSwitch + "," + OfflineSwitch;

    private static async Task<JsonElement> OrganizationAsync(XrmdClient client, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync(OrganizationSelect, ct);
        return rows.Count > 0 ? rows[0] : throw new CliException(ExitCodes.NotFound, "The organization was not found.");
    }

    private const string RuleSelect =
        "duplicaterules?$select=duplicateruleid,name,uniquename,baseentityname,matchingentityname,statecode,statuscode,excludeinactiverecords,iscasesensitive&$orderby=name";

    private static object RuleRow(JsonElement r) => new
    {
        duplicateruleid = Str(r, "duplicateruleid"),
        name = Str(r, "name"),
        uniquename = Str(r, "uniquename"),
        baseTable = Str(r, "baseentityname"),
        matchingTable = Str(r, "matchingentityname"),
        status = StatusText(Int(r, "statuscode")),
        statuscode = Int(r, "statuscode"),
        active = Int(r, "statecode") == 1,
    };

    // ---- status -------------------------------------------------------------------------------------------------------

    private static Command Status(CliContext ctx)
    {
        var command = new Command("status", "Show the organization's switches, the tables with duplicate detection on and the rules.");
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var org = await OrganizationAsync(client, ct);
            var tables = (await client.GetAllPagesAsync("EntityDefinitions?$select=LogicalName,IsDuplicateDetectionEnabled", ct))
                .Where(t => Managed(t, "IsDuplicateDetectionEnabled"))
                .Select(t => Str(t, "LogicalName")!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            var rules = (await client.GetAllPagesAsync(RuleSelect, ct)).Select(RuleRow).ToList();
            var switches = new
            {
                enabled = Flag(org, GlobalSwitch),
                enabledForCreateUpdate = Flag(org, CreateUpdateSwitch),
                enabledForImport = Flag(org, ImportSwitch),
                enabledForOfflineSync = Flag(org, OfflineSwitch),
            };
            if (ctx.Format == Output.OutputFormat.Json)
            {
                ctx.Write(new { switches, tables, rules });
                return ExitCodes.Ok;
            }
            static string OnOff(bool on) => on ? "on" : "off";
            var o = ctx.Env.Out;
            o.WriteLine($"Duplicate detection: {OnOff(switches.enabled)}");
            o.WriteLine($"  when a row is created or updated: {OnOff(switches.enabledForCreateUpdate)}");
            o.WriteLine($"  on import: {OnOff(switches.enabledForImport)} (stored only: xrmverse has no bulk import)");
            o.WriteLine($"  on offline sync: {OnOff(switches.enabledForOfflineSync)} (stored only: xrmverse has no offline client)");
            o.WriteLine(tables.Count == 0 ? "No table has duplicate detection on." : $"Tables with duplicate detection: {string.Join(", ", tables)}");
            if (rules.Count == 0) o.WriteLine("There are no duplicate detection rules.");
            else ctx.Write(rules);
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- enable / disable ---------------------------------------------------------------------------------------------

    private static Command Switch(CliContext ctx, bool enable)
    {
        var createUpdate = new Option<bool>("--create-update") { Description = "Change the 'when a row is created or updated' switch." };
        var table = new Option<string?>("--table") { Description = "Change the duplicate detection flag of this table (logical name)." };
        var verb = enable ? "on" : "off";
        var command = new Command(enable ? "enable" : "disable",
            $"Turn duplicate detection {verb}: the organization's main switch, or with --create-update the create/update switch, or with --table a table's flag.")
        { createUpdate, table };
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var tableName = parse.GetValue(table);
            var onCreateUpdate = parse.GetValue(createUpdate);
            var done = new List<string>();
            var result = new Dictionary<string, object?>();
            if (tableName is not null)
            {
                var path = $"EntityDefinitions(LogicalName={Literal(tableName)})";
                var response = await client.SendJsonAsync(HttpMethod.Get, path, cancellationToken: ct);
                var body = JsonNode.Parse((response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET {path}.")).GetRawText())!.AsObject();
                body.Remove("@odata.context");
                body.Remove("CanChangeTrackingBeEnabled");
                var flag = body["IsDuplicateDetectionEnabled"] as JsonObject ?? new JsonObject();
                flag["Value"] = enable;
                body["IsDuplicateDetectionEnabled"] = flag;
                await client.SendJsonAsync(HttpMethod.Put, path, body, cancellationToken: ct);
                result["table"] = tableName;
                result["tableEnabled"] = enable;
                done.Add($"table {tableName}");
            }
            if (onCreateUpdate || tableName is null)
            {
                var org = await OrganizationAsync(client, ct);
                var column = onCreateUpdate ? CreateUpdateSwitch : GlobalSwitch;
                await client.SendJsonAsync(HttpMethod.Patch, $"organizations({Str(org, "organizationid")})",
                    new Dictionary<string, object?> { [column] = enable }, cancellationToken: ct);
                result[onCreateUpdate ? "enabledForCreateUpdate" : "enabled"] = enable;
                done.Add(onCreateUpdate ? "the create/update switch" : "the organization switch");
            }
            ctx.WriteResult(result, $"Duplicate detection is {verb} for {string.Join(" and ", done)}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- rules --------------------------------------------------------------------------------------------------------

    /// <summary>One <c>--condition</c>: <c>attr[:matchattr]:exact|first:N|last:N|date|datetime|value[:ignore-blank]</c>.</summary>
    internal sealed record ConditionSpec(string BaseAttribute, string MatchingAttribute, int Operator, int? Parameter, bool IgnoreBlank);

    internal static ConditionSpec ParseCondition(string text)
    {
        CliException Bad(string why) => new(ExitCodes.Usage,
            $"Bad --condition '{text}': {why}. Use <attr>[:<matchattr>]:<exact|first:N|last:N|date|datetime|value>[:ignore-blank].");
        var parts = text.Split(':').Select(p => p.Trim()).ToList();
        var ignore = parts.Count > 1 && string.Equals(parts[^1], "ignore-blank", StringComparison.OrdinalIgnoreCase);
        if (ignore) parts.RemoveAt(parts.Count - 1);
        if (parts.Any(p => p.Length == 0)) throw Bad("an empty part");
        if (parts.Count < 2) throw Bad("the operator is missing");
        if (parts.Count == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && (string.Equals(parts[0], "first", StringComparison.OrdinalIgnoreCase) || string.Equals(parts[0], "last", StringComparison.OrdinalIgnoreCase)))
            throw Bad("the column is missing");

        int? param = null;
        string op;
        if (parts.Count >= 3 && int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && (string.Equals(parts[^2], "first", StringComparison.OrdinalIgnoreCase) || string.Equals(parts[^2], "last", StringComparison.OrdinalIgnoreCase)))
        {
            if (n < 1) throw Bad("N must be 1 or more");
            op = parts[^2];
            param = n;
            parts.RemoveRange(parts.Count - 2, 2);
        }
        else
        {
            op = parts[^1];
            parts.RemoveAt(parts.Count - 1);
            if (string.Equals(op, "first", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "last", StringComparison.OrdinalIgnoreCase))
                throw Bad($"'{op}' needs a number, as in {op.ToLowerInvariant()}:5");
        }
        if (!Operators.TryGetValue(op, out var code)) throw Bad($"unknown operator '{op}'");
        if (parts.Count is < 1 or > 2) throw Bad("give one column, or a column of the base table and one of the matching table");
        return new ConditionSpec(parts[0], parts.Count == 2 ? parts[1] : parts[0], code, param, ignore);
    }

    private static async Task RequireTableAsync(XrmdClient client, string table, CancellationToken ct) =>
        await client.GetJsonAsync($"EntityDefinitions(LogicalName={Literal(table)})?$select=LogicalName", ct);

    /// <summary>A rule by id, or by unique name; exit 4 when there is none.</summary>
    private static async Task<Guid> RuleIdAsync(XrmdClient client, string rule, CancellationToken ct)
    {
        if (Guid.TryParse(rule, out var id)) return id;
        var rows = await client.GetAllPagesAsync($"duplicaterules?$select=duplicateruleid&$filter=uniquename eq {Literal(rule)}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"Duplicate detection rule '{rule}' not found.");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} rules have the unique name '{rule}'. Use the rule id.");
        return Guid.Parse(Str(rows[0], "duplicateruleid")!);
    }

    private static Command Rule(CliContext ctx)
    {
        var list = new Command("list", "List the duplicate detection rules.");
        list.SetAction(async (parse, ct) =>
        {
            ctx.Write((await ctx.CreateClient().GetAllPagesAsync(RuleSelect, ct)).Select(RuleRow).ToList());
            return ExitCodes.Ok;
        });

        var baseTable = Required("--base", "Logical name of the base table.");
        var matching = Required("--matching", "Logical name of the matching table.");
        var name = Required("--name", "Name of the rule.");
        var conditions = new Option<string[]>("--condition")
        {
            Description = "A condition: <attr>[:<matchattr>]:<exact|first:N|last:N|date|datetime|value>[:ignore-blank]. Repeat for more. The matching column defaults to the base column.",
            Required = true,
            AllowMultipleArgumentsPerToken = false,
        };
        var caseSensitive = new Option<bool>("--case-sensitive") { Description = "Compare text with case." };
        var excludeInactive = new Option<bool>("--exclude-inactive") { Description = "Ignore inactive rows of the matching table." };
        var description = new Option<string?>("--description") { Description = "Description of the rule." };
        var create = new Command("create", "Create a duplicate detection rule with its conditions (it is not published).")
            { baseTable, matching, name, conditions, caseSensitive, excludeInactive, description };
        create.SetAction(async (parse, ct) =>
        {
            var specs = (parse.GetValue(conditions) ?? []).Select(ParseCondition).ToList();
            if (specs.Count == 0) throw new CliException(ExitCodes.Usage, "Give at least one --condition.");
            var client = ctx.CreateClient();
            var baseName = parse.GetValue(baseTable)!;
            var matchingName = parse.GetValue(matching)!;
            await RequireTableAsync(client, baseName, ct);
            if (!string.Equals(baseName, matchingName, StringComparison.OrdinalIgnoreCase)) await RequireTableAsync(client, matchingName, ct);
            var ruleName = parse.GetValue(name)!;
            var body = new Dictionary<string, object?>
            {
                ["name"] = ruleName,
                ["baseentityname"] = baseName,
                ["matchingentityname"] = matchingName,
                ["iscasesensitive"] = parse.GetValue(caseSensitive),
                ["excludeinactiverecords"] = parse.GetValue(excludeInactive),
            };
            if (parse.GetValue(description) is { } text) body["description"] = text;
            var response = await client.SendJsonAsync(HttpMethod.Post, "duplicaterules", body, cancellationToken: ct);
            var id = response.EntityId ?? throw new CliException(ExitCodes.Unexpected, "The server did not return the id of the new rule.");
            try
            {
                foreach (var spec in specs)
                {
                    var condition = new Dictionary<string, object?>
                    {
                        ["baseattributename"] = spec.BaseAttribute,
                        ["matchingattributename"] = spec.MatchingAttribute,
                        ["operatorcode"] = spec.Operator,
                        ["ignoreblankvalues"] = spec.IgnoreBlank,
                        ["regardingobjectid@odata.bind"] = $"duplicaterules({id})",
                    };
                    if (spec.Parameter is { } p) condition["operatorparam"] = p;
                    await client.SendJsonAsync(HttpMethod.Post, "duplicateruleconditions", condition, cancellationToken: ct);
                }
            }
            catch
            {
                // A rule without the conditions that were asked for is worse than none: take it back, then report the refusal.
                try { await client.SendJsonAsync(HttpMethod.Delete, $"duplicaterules({id})", cancellationToken: CancellationToken.None); }
                catch (CliException) { /* the original refusal is the one to show */ }
                throw;
            }
            ctx.WriteResult(new { duplicateruleid = id, name = ruleName, baseTable = baseName, matchingTable = matchingName, conditions = specs.Count },
                $"Created duplicate detection rule '{ruleName}' ({id}) with {specs.Count} condition(s). It is not published yet.");
            return ExitCodes.Ok;
        });

        var publishRule = new Argument<string>("rule") { Description = "Id or unique name of the rule." };
        var wait = new Option<bool>("--wait") { Description = "Wait until the publish job has ended." };
        var timeout = new Option<int>("--timeout") { Description = "With --wait: minutes to wait.", DefaultValueFactory = _ => 10 };
        var publish = new Command("publish", "Publish a rule: the server builds its match codes in a system job.") { publishRule, wait, timeout };
        publish.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var id = await RuleIdAsync(client, parse.GetValue(publishRule)!, ct);
            var response = await client.SendJsonAsync(HttpMethod.Post, $"duplicaterules({id})/{ActionPrefix}PublishDuplicateRule", cancellationToken: ct);
            var job = response.Json is { } json && Guid.TryParse(Str(json, "asyncoperationid"), out var jobId)
                ? jobId
                : throw new CliException(ExitCodes.Unexpected, "PublishDuplicateRule returned no asyncoperationid.");
            if (!parse.GetValue(wait))
            {
                ctx.WriteResult(new { duplicateruleid = id, asyncoperationid = job, status = "publishing" },
                    $"Publishing rule {id} (system job {job}).");
                return ExitCodes.Ok;
            }
            await WaitAsync(ctx, client, job, Minutes(parse.GetValue(timeout)), "Publishing the rule", ct);
            ctx.WriteResult(new { duplicateruleid = id, asyncoperationid = job, status = "published" }, $"Rule {id} is published (system job {job}).");
            return ExitCodes.Ok;
        });

        var unpublishRule = new Argument<string>("rule") { Description = "Id or unique name of the rule." };
        var unpublish = new Command("unpublish", "Unpublish a rule and drop its match codes.") { unpublishRule };
        unpublish.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var id = await RuleIdAsync(client, parse.GetValue(unpublishRule)!, ct);
            await client.SendJsonAsync(HttpMethod.Post, "UnpublishDuplicateRule", new Dictionary<string, object?> { ["DuplicateRuleId"] = id }, cancellationToken: ct);
            ctx.WriteResult(new { duplicateruleid = id, status = "unpublished" }, $"Rule {id} is unpublished.");
            return ExitCodes.Ok;
        });

        var deleteRule = new Argument<string>("rule") { Description = "Id or unique name of the rule." };
        var delete = new Command("delete", "Delete a rule and its conditions.") { deleteRule, ctx.YesOption };
        delete.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var id = await RuleIdAsync(client, parse.GetValue(deleteRule)!, ct);
            await ctx.ConfirmDestructiveAsync($"This deletes duplicate detection rule {id} and its conditions. Continue?");
            await client.SendJsonAsync(HttpMethod.Delete, $"duplicaterules({id})", cancellationToken: ct);
            ctx.WriteResult(new { duplicateruleid = id, deleted = true }, $"Deleted duplicate detection rule {id}.");
            return ExitCodes.Ok;
        });

        return new Command("rule", "List, create, publish, unpublish or delete duplicate detection rules.") { list, create, publish, unpublish, delete };
    }

    private static TimeSpan Minutes(int minutes) =>
        minutes > 0 ? TimeSpan.FromMinutes(minutes) : throw new CliException(ExitCodes.Usage, "--timeout must be 1 or more minutes.");

    /// <summary>Polls a system job on the injected clock until it has ended; a job that did not succeed is exit 5 with its message.</summary>
    private static async Task<JsonElement> WaitAsync(CliContext ctx, XrmdClient client, Guid job, TimeSpan timeout, string what, CancellationToken ct)
    {
        var deadline = ctx.Env.Clock.GetUtcNow() + timeout;
        string? last = null;
        while (true)
        {
            var operation = await client.GetJsonAsync($"asyncoperations({job})?$select=statecode,statuscode,message", ct);
            var state = Int(operation, "statecode");
            var status = Int(operation, "statuscode");
            var line = state switch { 0 => "waiting", 1 => "suspended", 2 => "running", _ => "ended" };
            if (line != last)
            {
                await ctx.Env.Err.WriteLineAsync($"System job {job}: {line}.");
                last = line;
            }
            if (state == Completed)
            {
                if (status == Succeeded) return operation;
                throw new CliException(ExitCodes.Rejected, $"{what} did not succeed: {Str(operation, "message") ?? $"system job {job} ended with status {status}"}");
            }
            if (ctx.Env.Clock.GetUtcNow() >= deadline)
                throw new CliException(ExitCodes.Unreachable,
                    $"System job {job} still running after {timeout.TotalMinutes:0.##} minute(s); stopped waiting. Check asyncoperation {job}.");
            await Task.Delay(PollInterval, ctx.Env.Clock, ct);
        }
    }

    // ---- find ---------------------------------------------------------------------------------------------------------

    private static Command Find(CliContext ctx)
    {
        var table = Required("--table", "Logical name of the table of the row.");
        var id = Required("--id", "Id of the row.");
        var matching = new Option<string?>("--matching") { Description = "Table to look in (default: the table of the row)." };
        var command = new Command("find", "List the rows that duplicate a row, by the published rules.") { table, id, matching };
        command.SetAction(async (parse, ct) =>
        {
            var tableName = parse.GetValue(table)!;
            var idText = parse.GetValue(id)!;
            if (!Guid.TryParse(idText, out var rowId)) throw new CliException(ExitCodes.Usage, $"'{idText}' is not a row id.");
            var matchingName = parse.GetValue(matching) ?? tableName;
            var client = ctx.CreateClient();
            var baseEntity = await client.GetJsonAsync($"EntityDefinitions(LogicalName={Literal(tableName)})?$select=LogicalName,PrimaryIdAttribute", ct);
            var baseKey = Str(baseEntity, "PrimaryIdAttribute") ?? throw new CliException(ExitCodes.Unexpected, $"The server did not report the primary id of '{tableName}'.");
            var target = await client.GetJsonAsync($"EntityDefinitions(LogicalName={Literal(matchingName)})?$select=LogicalName,PrimaryIdAttribute,PrimaryNameAttribute", ct);
            var key = Str(target, "PrimaryIdAttribute") ?? throw new CliException(ExitCodes.Unexpected, $"The server did not report the primary id of '{matchingName}'.");
            var nameColumn = Str(target, "PrimaryNameAttribute");

            var entity = new JsonObject { ["@odata.type"] = ActionPrefix + tableName, [baseKey] = rowId.ToString() };
            var rows = new List<object>();
            for (var page = 1; ; page++)
            {
                var paging = new JsonObject { ["PageNumber"] = page, ["Count"] = 5000 };
                var path = $"RetrieveDuplicates(BusinessEntity=@p1,MatchingEntityName=@p2,PagingInfo=@p3)?@p1={Uri.EscapeDataString(entity.ToJsonString())}"
                    + $"&@p2={Literal(matchingName)}&@p3={Uri.EscapeDataString(paging.ToJsonString())}";
                var json = await client.GetJsonAsync(path, ct);
                if (json.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                    rows.AddRange(value.EnumerateArray().Select(v => (object)new { id = Str(v, key), name = nameColumn is null ? null : Str(v, nameColumn) }));
                if (!Flag(json, "@Microsoft.Dynamics.CRM.morerecords")) break;
            }
            if (ctx.Format != Output.OutputFormat.Json && rows.Count == 0)
            {
                ctx.Env.Out.WriteLine($"No duplicates of {tableName} {rowId} in {matchingName}.");
                return ExitCodes.Ok;
            }
            ctx.Write(rows);
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- bulk ---------------------------------------------------------------------------------------------------------

    private static Command Bulk(CliContext ctx)
    {
        var table = Required("--table", "Logical name of the table to scan.");
        var name = new Option<string?>("--name") { Description = "Name of the job." };
        var wait = new Option<bool>("--wait") { Description = "Wait until the job has ended." };
        var timeout = new Option<int>("--timeout") { Description = "With --wait: minutes to wait.", DefaultValueFactory = _ => 30 };
        var command = new Command("bulk", "Start a system job that looks for duplicates among all rows of a table (it stops at 5000 rows).") { table, name, wait, timeout };
        command.SetAction(async (parse, ct) =>
        {
            var tableName = parse.GetValue(table)!;
            var jobName = parse.GetValue(name) ?? $"Duplicate detection of {tableName}";
            var client = ctx.CreateClient();
            var body = new Dictionary<string, object?>
            {
                ["Query"] = new Dictionary<string, object?> { ["@odata.type"] = "#Microsoft.Dynamics.CRM.QueryExpression", ["EntityName"] = tableName },
                ["JobName"] = jobName,
                ["SendEmailNotification"] = false,
            };
            var response = await client.SendJsonAsync(HttpMethod.Post, "BulkDetectDuplicates", body, cancellationToken: ct);
            var job = response.Json is { } json && Guid.TryParse(Str(json, "JobId"), out var jobId)
                ? jobId
                : throw new CliException(ExitCodes.Unexpected, "BulkDetectDuplicates returned no JobId.");
            if (!parse.GetValue(wait))
            {
                ctx.WriteResult(new { jobId = job, name = jobName, status = "queued" }, $"Started duplicate detection job '{jobName}' ({job}).");
                return ExitCodes.Ok;
            }
            var operation = await WaitAsync(ctx, client, job, Minutes(parse.GetValue(timeout)), "Duplicate detection", ct);
            var note = Str(operation, "message");
            ctx.WriteResult(new { jobId = job, name = jobName, status = "completed", message = note },
                $"Duplicate detection job '{jobName}' ({job}) finished.{(note is null ? "" : " " + note)}");
            return ExitCodes.Ok;
        });
        return command;
    }
}

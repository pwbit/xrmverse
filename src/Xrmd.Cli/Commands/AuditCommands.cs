using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd audit status|enable|disable|retention|history|delete</c> over the Web API as the signed-in user (plan 13): the
/// organization switches and retention (<c>organizations</c>), the table and column switches (the metadata endpoints), the
/// history of a record (<c>audits</c> and <c>RetrieveAuditDetails</c>) and the deletion actions
/// (<c>DeleteRecordChangeHistory</c>, <c>DeleteAuditData</c>). Every refusal is the server's: a missing privilege is a 403
/// (exit 3) whose message names it, an out-of-range retention a 400 (exit 5).
/// </summary>
public static class AuditCommands
{
    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";
    private const string Annotations = "odata.include-annotations=\"*\"";
    private const int DefaultTop = 20;
    private const string RetentionNote =
        "The new period applies to audit rows written from now on; rows already written keep the period they were written with. " +
        "Remove older rows with `xrmd audit delete --before <date>`.";

    public static Command Build(CliContext ctx) => new("audit", "Switch auditing on and off, set its retention, read and delete the audit history (Web API).")
    {
        Status(ctx),
        Switch(ctx, enable: true),
        Switch(ctx, enable: false),
        Retention(ctx),
        History(ctx),
        Delete(ctx),
    };

    private static string EntityPath(string table) => $"EntityDefinitions(LogicalName={UserRoleLookup.Literal(table)})";

    private static string? Str(JsonElement row, string name) => UserRoleLookup.Str(row, name);

    private static IReadOnlyDictionary<string, string> PreferAnnotations { get; } = new Dictionary<string, string> { ["Prefer"] = Annotations };

    private static async Task<(Guid Id, bool Enabled, int Retention)> OrganizationAsync(XrmdClient client, CancellationToken ct)
    {
        var rows = await client.GetAllPagesAsync("organizations?$select=organizationid,isauditenabled,auditretentionperiodv2", ct);
        if (rows.Count == 0 || !Guid.TryParse(Str(rows[0], "organizationid"), out var id))
            throw new CliException(ExitCodes.NotFound, "The organization was not found.");
        return (id,
            rows[0].TryGetProperty("isauditenabled", out var e) && e.ValueKind == JsonValueKind.True,
            rows[0].TryGetProperty("auditretentionperiodv2", out var r) && r.ValueKind == JsonValueKind.Number
                ? r.GetInt32()
                : throw new CliException(ExitCodes.Unexpected, "The server returned no auditretentionperiodv2 for the organization."));
    }

    private static string RetentionText(int days) => days == -1 ? "forever" : $"{days} days";

    // ---- status -------------------------------------------------------------------------------------------------------

    private static Command Status(CliContext ctx)
    {
        var command = new Command("status", "Show whether the organization is audited, the retention, and the audited tables.");
        command.SetAction(async (_, ct) =>
        {
            var client = ctx.CreateClient();
            var (_, enabled, retention) = await OrganizationAsync(client, ct);
            var tables = (await client.GetAllPagesAsync("EntityDefinitions?$select=LogicalName,IsAuditEnabled", ct))
                .Where(t => t.TryGetProperty("IsAuditEnabled", out var a) && a.ValueKind == JsonValueKind.Object
                            && a.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.True)
                .Select(t => Str(t, "LogicalName")!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            ctx.Write(new { enabled, retentionDays = retention, retention = RetentionText(retention), tables });
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- enable / disable ---------------------------------------------------------------------------------------------

    private static Command Switch(CliContext ctx, bool enable)
    {
        var verb = enable ? "enable" : "disable";
        var table = new Option<string?>("--table") { Description = "Logical name of the table; without it, the organization." };
        var column = new Option<string?>("--column") { Description = "Logical name of a column of --table." };
        var command = new Command(verb, $"{(enable ? "Switch on" : "Switch off")} auditing for the organization, a table (--table) or a column (--table and --column).") { table, column };
        command.SetAction(async (parse, ct) =>
        {
            var tableName = parse.GetValue(table);
            var columnName = parse.GetValue(column);
            if (columnName is not null && tableName is null) throw new CliException(ExitCodes.Usage, "--column needs --table.");
            var client = ctx.CreateClient();
            if (tableName is null)
            {
                var (id, _, _) = await OrganizationAsync(client, ct);
                await client.SendJsonAsync(HttpMethod.Patch, $"organizations({id})", new Dictionary<string, object?> { ["isauditenabled"] = enable }, cancellationToken: ct);
                ctx.WriteResult(new { scope = "organization", enabled = enable }, $"Auditing {(enable ? "enabled" : "disabled")} for the organization.");
                return ExitCodes.Ok;
            }

            var path = columnName is null
                ? EntityPath(tableName)
                : $"{EntityPath(tableName)}/Attributes(LogicalName={UserRoleLookup.Literal(columnName)})";
            var response = await client.SendJsonAsync(HttpMethod.Get, path, cancellationToken: ct);
            var body = JsonNode.Parse((response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET {path}.")).GetRawText())!.AsObject();
            if (body["IsAuditEnabled"] is not JsonObject setting) body["IsAuditEnabled"] = setting = new JsonObject();
            setting["Value"] = enable;
            await client.SendJsonAsync(HttpMethod.Put, path, body, cancellationToken: ct);
            var what = columnName is null ? $"table '{tableName}'" : $"column '{tableName}.{columnName}'";
            ctx.WriteResult(new { scope = columnName is null ? "table" : "column", table = tableName, column = columnName, enabled = enable },
                $"Auditing {(enable ? "enabled" : "disabled")} for {what}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- retention ----------------------------------------------------------------------------------------------------

    private static Command Retention(CliContext ctx)
    {
        var days = new Argument<int?>("days") { Description = "Days to keep audit rows: 1 to 365000.", Arity = ArgumentArity.ZeroOrOne };
        var forever = new Option<bool>("--forever") { Description = "Keep audit rows forever (retention -1: nothing is deleted automatically)." };
        var command = new Command("retention", "Set how long audit rows are kept: <days> (1 to 365000) or --forever.") { days, forever };
        command.SetAction(async (parse, ct) =>
        {
            var given = parse.GetValue(days);
            var keepForever = parse.GetValue(forever);
            if (given is null == !keepForever)
                throw new CliException(ExitCodes.Usage, "Give either <days> (1 to 365000) or --forever.");
            var value = keepForever ? -1 : given!.Value;
            var client = ctx.CreateClient();
            var (id, _, _) = await OrganizationAsync(client, ct);
            await client.SendJsonAsync(HttpMethod.Patch, $"organizations({id})", new Dictionary<string, object?> { ["auditretentionperiodv2"] = value }, cancellationToken: ct);
            if (ctx.Format == Output.OutputFormat.Json)
                ctx.Write(new { retentionDays = value, retention = RetentionText(value), note = RetentionNote });
            else
                ctx.Env.Out.WriteLine($"Audit retention set to {RetentionText(value)}. {RetentionNote}");
            return ExitCodes.Ok;
        });
        return command;
    }

    // ---- history ------------------------------------------------------------------------------------------------------

    private sealed record Change(string Date, string? User, string Action, string Column, string Old, string New);

    private static Command History(CliContext ctx)
    {
        var table = new Argument<string>("table") { Description = "Logical name of the table." };
        var id = new Argument<string>("id") { Description = "Id of the record." };
        var column = new Option<string?>("--column") { Description = "Only the changes of this column." };
        var top = new Option<int>("--top") { Description = "How many audit entries, newest first.", DefaultValueFactory = _ => DefaultTop };
        var command = new Command("history", "Show the change history of a record, newest first: date, user, action, column, old → new.") { table, id, column, top };
        command.SetAction(async (parse, ct) =>
        {
            var tableName = parse.GetValue(table)!;
            if (!Guid.TryParse(parse.GetValue(id), out var recordId))
                throw new CliException(ExitCodes.Usage, $"The id must be a GUID: '{parse.GetValue(id)}'.");
            var limit = parse.GetValue(top);
            if (limit < 1) throw new CliException(ExitCodes.Usage, "--top must be at least 1.");
            var columnName = parse.GetValue(column);
            var client = ctx.CreateClient();

            // Always ask the history function first (one row): the history privilege is then demanded whether or not entries exist,
            // as the SDK and Web API history messages do. The target's key is the table's primary id attribute (activities: activityid).
            var primaryId = await UserRoleLookup.PrimaryIdAsync(client, tableName, ct);
            await client.SendJsonAsync(HttpMethod.Get,
                "RetrieveRecordChangeHistory(Target=@target,PagingInfo=@paginginfo)?@target=" +
                Uri.EscapeDataString($"{{\"@odata.type\":\"Microsoft.Dynamics.CRM.{tableName}\",\"{primaryId}\":\"{recordId}\"}}") +
                "&@paginginfo=" + Uri.EscapeDataString("{\"PageNumber\":1,\"Count\":1}"),
                cancellationToken: ct);

            var changes = new List<Change>();
            var events = 0;
            // Without --column at most --top entries are asked for; with it the pages are read lazily until --top entries match.
            var query = $"audits?$select=auditid,createdon,action,_userid_value&$filter=_objectid_value eq {recordId} and objecttypecode eq {UserRoleLookup.Literal(tableName)}" +
                        "&$orderby=createdon desc,auditid asc" + (columnName is null ? $"&$top={limit}" : "");
            await foreach (var row in AuditRowsAsync(client, query, ct))
            {
                var auditId = Str(row, "auditid");
                var detail = await client.SendJsonAsync(HttpMethod.Get, $"audits({auditId})/Microsoft.Dynamics.CRM.RetrieveAuditDetails", null, PreferAnnotations, ct);
                var audit = detail.Json is { } json && json.TryGetProperty("AuditDetail", out var d) ? d : default;
                var date = Str(row, "createdon") ?? "";
                var user = Str(row, "_userid_value" + Formatted) ?? Str(row, "_userid_value");
                var action = Str(row, "action" + Formatted) ?? (row.TryGetProperty("action", out var a) ? a.ToString() : "");
                var entries = Entries(audit, date, user, action);
                if (columnName is not null) entries = entries.Where(e => string.Equals(e.Column, columnName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (entries.Count == 0) continue;
                changes.AddRange(entries);
                if (++events >= limit) break;
            }

            if (ctx.Format == Output.OutputFormat.Json)
                ctx.Write(changes.Select(c => new { date = c.Date, user = c.User, action = c.Action, column = c.Column, oldValue = c.Old, newValue = c.New }).ToList());
            else
                ctx.Write(changes.Select(c => new { c.Date, c.User, c.Action, c.Column, Change = $"{(c.Old.Length == 0 ? "(empty)" : c.Old)} → {(c.New.Length == 0 ? "(empty)" : c.New)}" }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>The rows of an <c>audits</c> query, one page at a time (50 rows), so that a consumer that stops early stops the requests.</summary>
    private static async IAsyncEnumerable<JsonElement> AuditRowsAsync(XrmdClient client, string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var prefer = new Dictionary<string, string> { ["Prefer"] = Annotations + ",odata.maxpagesize=50" };
        var prefix = new Uri(client.BaseUrl, XrmdClient.WebApiPath).AbsoluteUri;
        string? next = path;
        while (next is not null)
        {
            var response = await client.SendJsonAsync(HttpMethod.Get, next, null, prefer, ct);
            var page = response.Json ?? throw new CliException(ExitCodes.Unexpected, $"The server returned no JSON for GET {next}.");
            if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                throw new CliException(ExitCodes.Unexpected, $"GET {next} did not return a collection.");
            foreach (var row in value.EnumerateArray()) yield return row;
            next = null;
            if (page.TryGetProperty("@odata.nextLink", out var link) && link.GetString() is { } url)
                next = url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? url[prefix.Length..]
                    : throw new CliException(ExitCodes.Unexpected, $"The server returned a next link outside the Web API: {url}");
        }
    }

    /// <summary>The changes of one audit entry: a row per changed column of an attribute detail, one row for the other kinds.</summary>
    private static List<Change> Entries(JsonElement detail, string date, string? user, string action)
    {
        var list = new List<Change>();
        if (detail.ValueKind != JsonValueKind.Object) return list;
        var type = Str(detail, "@odata.type") ?? "";
        Change One(string column, string o, string n) => new(date, user, action, column, o, n);
        if (type.EndsWith("RelationshipAuditDetail", StringComparison.Ordinal))
        {
            var targets = detail.TryGetProperty("TargetRecords", out var t) && t.ValueKind == JsonValueKind.Array
                ? string.Join(", ", t.EnumerateArray().Select(r => Str(r, "@odata.type")?.Split('.').Last() + ":" + r.EnumerateObject().FirstOrDefault(p => !p.Name.StartsWith('@')).Value))
                : "";
            list.Add(One(Str(detail, "RelationshipName") ?? "", "", targets));
        }
        else if (type.EndsWith("ShareAuditDetail", StringComparison.Ordinal))
        {
            var principal = detail.TryGetProperty("Principal", out var p) && p.ValueKind == JsonValueKind.Object
                ? Str(p, "@odata.type")?.Split('.').Last() + ":" + p.EnumerateObject().FirstOrDefault(x => !x.Name.StartsWith('@')).Value
                : "";
            list.Add(One("share " + principal, Str(detail, "OldPrivileges") ?? "", Str(detail, "NewPrivileges") ?? ""));
        }
        else if (type.EndsWith("RolePrivilegeAuditDetail", StringComparison.Ordinal))
        {
            int Count(string name) => detail.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
            list.Add(One("privileges", $"{Count("OldRolePrivileges")} privileges", $"{Count("NewRolePrivileges")} privileges"));
        }
        else
        {
            var old = detail.TryGetProperty("OldValue", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
            var now = detail.TryGetProperty("NewValue", out var n) && n.ValueKind == JsonValueKind.Object ? n : default;
            var keys = new List<string>();
            foreach (var record in new[] { old, now })
                if (record.ValueKind == JsonValueKind.Object)
                    foreach (var property in record.EnumerateObject())
                        if (!property.Name.Contains('@') && !keys.Contains(property.Name)) keys.Add(property.Name);
            foreach (var key in keys)
            {
                var name = key.StartsWith('_') && key.EndsWith("_value", StringComparison.Ordinal) ? key[1..^6] : key;
                list.Add(One(name, ValueText(old, key), ValueText(now, key)));
            }
        }
        return list;
    }

    private static string ValueText(JsonElement record, string key)
    {
        if (record.ValueKind != JsonValueKind.Object) return "";
        if (record.TryGetProperty(key + Formatted, out var f) && f.ValueKind == JsonValueKind.String) return f.GetString()!;
        if (!record.TryGetProperty(key, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.Null => "",
            JsonValueKind.String => v.GetString()!,
            _ => v.GetRawText(),
        };
    }

    // ---- delete -------------------------------------------------------------------------------------------------------

    private static Command Delete(CliContext ctx)
    {
        var table = new Option<string?>("--table") { Description = "Logical name of the table whose record history is deleted (with --id)." };
        var id = new Option<string?>("--id") { Description = "Id of the record whose history is deleted (with --table)." };
        var before = new Option<string?>("--before") { Description = "Delete the audit data of the calendar quarters (UTC) that ended on or before this date; never the current quarter." };
        var command = new Command("delete", "Delete the audit history of one record (--table and --id) or the audit data older than a date (--before).") { table, id, before, ctx.YesOption };
        command.SetAction(async (parse, ct) =>
        {
            var tableName = parse.GetValue(table);
            var idText = parse.GetValue(id);
            var beforeText = parse.GetValue(before);
            var forRecord = tableName is not null || idText is not null;
            if (forRecord == (beforeText is not null))
                throw new CliException(ExitCodes.Usage, "Give either --table and --id (one record's history) or --before <date> (audit data by date).");

            if (forRecord)
            {
                if (tableName is null || idText is null) throw new CliException(ExitCodes.Usage, "--table and --id go together.");
                if (!Guid.TryParse(idText, out var recordId)) throw new CliException(ExitCodes.Usage, $"--id must be a GUID: '{idText}'.");
                await ctx.ConfirmDestructiveAsync($"This permanently deletes the audit history of {tableName} {recordId}. Continue?");
                var client = ctx.CreateClient();
                var primaryId = await UserRoleLookup.PrimaryIdAsync(client, tableName, ct);
                var body = new Dictionary<string, object?>
                {
                    ["Target"] = new Dictionary<string, object?> { ["@odata.type"] = "Microsoft.Dynamics.CRM." + tableName, [primaryId] = recordId },
                };
                var response = await client.SendJsonAsync(HttpMethod.Post, "DeleteRecordChangeHistory", body, cancellationToken: ct);
                var count = response.Json is { } json && json.TryGetProperty("DeletedEntriesCount", out var c) ? c.GetInt32() : 0;
                ctx.WriteResult(new { deleted = count }, $"Deleted {count} audit entries of {tableName} {recordId}.");
                return ExitCodes.Ok;
            }

            if (!DateTimeOffset.TryParse(beforeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end))
                throw new CliException(ExitCodes.Usage, $"--before must be a date (e.g. 2026-01-31): '{beforeText}'.");
            await ctx.ConfirmDestructiveAsync($"This permanently deletes the audit data of every closed calendar quarter that ended on or before {end:yyyy-MM-dd}. Continue?");
            var data = await ctx.CreateClient().SendJsonAsync(HttpMethod.Post, "DeleteAuditData",
                new Dictionary<string, object?> { ["EndDate"] = end.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) }, cancellationToken: ct);
            var partitions = data.Json is { } result && result.TryGetProperty("PartitionsDeleted", out var p) ? p.GetInt32() : 0;
            ctx.WriteResult(new { partitionsDeleted = partitions }, $"Deleted the audit data of {partitions} quarter(s) that ended on or before {end:yyyy-MM-dd}.");
            return ExitCodes.Ok;
        });
        return command;
    }
}

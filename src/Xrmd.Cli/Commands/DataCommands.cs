using System.Collections.Concurrent;
using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd data</c>: query, export and import records through the Web API as the signed-in user.
/// <para>
/// File formats (the same for export and import, so a file round-trips): one column per logical name; lookups as
/// <c>table:guid</c> (<c>systemuser:00000000-…</c>); choices as their integer value (multi-select as <c>"1,2"</c>);
/// dates as ISO 8601 UTC; the primary id column is included, and an import creates the row with that id. CSV is RFC 4180;
/// an empty cell is null (an empty string cannot be written). Only columns that are valid for create are exported.
/// </para>
/// </summary>
public static class DataCommands
{
    private const string MoreHint = "More records available; use --all.";
    private const string PagingCookie = "@Microsoft.Dynamics.CRM.fetchxmlpagingcookie";
    private const string MoreRecords = "@Microsoft.Dynamics.CRM.morerecords";
    private const string LookupLogicalName = "@Microsoft.Dynamics.CRM.lookuplogicalname";

    public static Command Build(CliContext ctx) => new("data", "Query, export and import records (Web API).")
    {
        Query(ctx),
        Export(ctx),
        Import(ctx),
    };

    // ---- query ------------------------------------------------------------------------------------------------------

    private static Command Query(CliContext ctx)
    {
        var fetch = new Option<string?>("--fetch") { Description = "FetchXML, or @file to read it from a file." };
        var odata = new Option<string?>("--odata") { Description = "A Web API query relative to the service root, e.g. \"accounts?$select=name&$filter=...\"." };
        var all = new Option<bool>("--all") { Description = "Follow every page and return all records as one array." };
        var top = new Option<int?>("--top") { Description = "Return at most n records." };
        var command = new Command("query", "Run a FetchXML or OData query and print the records.") { fetch, odata, all, top };
        command.SetAction(async (parse, ct) =>
        {
            var fetchText = parse.GetValue(fetch);
            var odataText = parse.GetValue(odata);
            if ((fetchText is null) == (odataText is null))
                throw new CliException(ExitCodes.Usage, "Give exactly one of --fetch and --odata.");
            if (parse.GetValue(top) is <= 0)
                throw new CliException(ExitCodes.Usage, "--top must be a positive number.");

            var client = ctx.CreateClient();
            var rows = fetchText is not null
                ? await RunFetchAsync(client, ReadFetch(fetchText), parse.GetValue(all), parse.GetValue(top), ctx.Env.Err, ct)
                : await RunODataAsync(client, odataText!, parse.GetValue(all), parse.GetValue(top), ctx.Env.Err, ct);
            ctx.Write(rows);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static XDocument ReadFetch(string text)
    {
        if (text.StartsWith('@'))
        {
            var path = text[1..];
            if (!File.Exists(path)) throw new CliException(ExitCodes.Usage, $"The FetchXML file '{path}' does not exist.");
            text = File.ReadAllText(path);
        }
        try
        {
            var doc = XDocument.Parse(text);
            return doc.Root?.Name.LocalName == "fetch" ? doc : throw new CliException(ExitCodes.Usage, "The FetchXML root element must be <fetch>.");
        }
        catch (System.Xml.XmlException ex)
        {
            throw new CliException(ExitCodes.Usage, "The FetchXML is not valid XML: " + ex.Message);
        }
    }

    private static async Task<JsonElement> RunODataAsync(XrmdClient client, string query, bool all, int? top, TextWriter err, CancellationToken ct)
    {
        query = query.TrimStart('/');
        if (query.Length == 0) throw new CliException(ExitCodes.Usage, "--odata needs a query such as \"accounts?$top=5\".");
        var queryStart = query.IndexOf('?');
        var path = queryStart < 0 ? query : query[..queryStart];
        var singleRecord = path.EndsWith(')') && path.LastIndexOf('(') > path.LastIndexOf('/'); // accounts(<id>), not a navigation to a collection
        if (singleRecord)
        {
            if (top is not null) throw new CliException(ExitCodes.Usage, "--top does not apply to a single record.");
            var record = (await client.SendJsonAsync(HttpMethod.Get, query, cancellationToken: ct)).Json
                         ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for the query.");
            return ToArray([record]); // one-element array, as for every other query
        }

        if (top is { } n)
        {
            var options = queryStart < 0 ? [] : query[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries);
            if (options.Any(o => Uri.UnescapeDataString(o.Split('=', 2)[0]).Equals("$top", StringComparison.OrdinalIgnoreCase)))
                throw new CliException(ExitCodes.Usage, "The query already has $top: drop --top or the $top option.");
            query += (queryStart < 0 ? "?" : "&") + "$top=" + n.ToString(CultureInfo.InvariantCulture);
        }

        if (all) return ToArray(await client.GetAllPagesAsync(query, ct));

        var page = (await client.SendJsonAsync(HttpMethod.Get, query, cancellationToken: ct)).Json
                   ?? throw new CliException(ExitCodes.Unexpected, "The server returned no JSON for the query.");
        if (page.TryGetProperty("@odata.nextLink", out _)) await err.WriteLineAsync(MoreHint);
        return Value(page);
    }

    private static async Task<JsonElement> RunFetchAsync(XrmdClient client, XDocument doc, bool all, int? top, TextWriter err, CancellationToken ct)
    {
        var fetch = doc.Root!;
        var table = fetch.Element("entity")?.Attribute("name")?.Value
                    ?? throw new CliException(ExitCodes.Usage, "The FetchXML needs an <entity name=\"…\"> element.");
        if (top is { } n)
        {
            if (fetch.Attribute("top") is not null || fetch.Attribute("count") is not null || fetch.Attribute("page") is not null)
                throw new CliException(ExitCodes.Usage, "The FetchXML already has top, count or page: drop --top or those attributes.");
            fetch.SetAttributeValue("top", n);
        }

        var set = await DataTable.EntitySetAsync(client, table, ct);
        if (!all)
        {
            var first = await FetchPageAsync(client, set, fetch, ct);
            if (first.TryGetProperty(MoreRecords, out var more) && more.ValueKind == JsonValueKind.True) await err.WriteLineAsync(MoreHint);
            return Value(first);
        }

        // Page with the paging cookie the server hands back; a fetch without a page size gets the largest one.
        if (fetch.Attribute("count") is null && fetch.Attribute("top") is null) fetch.SetAttributeValue("count", XrmdClient.MaxPageSize);
        var page = int.TryParse(fetch.Attribute("page")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ? start : 1;
        var rows = new List<JsonElement>();
        while (true)
        {
            fetch.SetAttributeValue("page", page);
            var result = await FetchPageAsync(client, set, fetch, ct);
            rows.AddRange(result.GetProperty("value").EnumerateArray());
            if (!(result.TryGetProperty(MoreRecords, out var more) && more.ValueKind == JsonValueKind.True)) break;

            var cookie = result.TryGetProperty(PagingCookie, out var annotation) && annotation.GetString() is { } text
                ? XElement.Parse(text).Attribute("pagingcookie")?.Value
                : null;
            if (cookie is null) throw new CliException(ExitCodes.Unexpected, "The server reported more records without a paging cookie.");
            fetch.SetAttributeValue("paging-cookie", Uri.UnescapeDataString(Uri.UnescapeDataString(cookie))); // the cookie is URL-encoded twice
            page++;
        }
        return ToArray(rows);
    }

    private static async Task<JsonElement> FetchPageAsync(XrmdClient client, string set, XElement fetch, CancellationToken ct)
    {
        var response = await client.SendJsonAsync(HttpMethod.Get, $"{set}?fetchXml={Uri.EscapeDataString(fetch.ToString(SaveOptions.DisableFormatting))}", cancellationToken: ct);
        return response.Json is { ValueKind: JsonValueKind.Object } page && page.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? page
            : throw new CliException(ExitCodes.Unexpected, "The server did not return a collection for the FetchXML.");
    }

    private static JsonElement Value(JsonElement page) =>
        page.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new CliException(ExitCodes.Unexpected, "The server did not return a collection.");

    private static JsonElement ToArray(IReadOnlyList<JsonElement> rows) => JsonSerializer.SerializeToElement(rows);

    // ---- export -----------------------------------------------------------------------------------------------------

    private static Option<string> FormatOption()
    {
        var format = new Option<string>("--format") { Description = "File format: csv or json.", Required = true };
        format.AcceptOnlyFromAmong("csv", "json");
        return format;
    }

    private static Command Export(CliContext ctx)
    {
        var table = new Argument<string>("table") { Description = "Logical name of the table." };
        var output = new Option<string>("--out") { Description = "File to write.", Required = true };
        var format = FormatOption();
        var filter = new Option<string?>("--filter") { Description = "OData $filter expression, e.g. \"statecode eq 0\"." };
        var command = new Command("export", "Export a table's rows to a CSV or JSON file (importable with `data import`).") { table, output, format, filter };
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var info = await DataTable.LoadAsync(client, parse.GetValue(table)!, ct);
            var columns = info.Columns.Where(c => info.IsWritable(c) && c.IsValidForRead != false)
                .OrderBy(c => c.LogicalName == info.PrimaryId ? 0 : c.LogicalName == info.PrimaryName ? 1 : 2)
                .ThenBy(c => c.LogicalName, StringComparer.Ordinal).ToList();

            foreach (var (skipped, type) in info.Unsupported)
                await ctx.Env.Err.WriteLineAsync(info.SkipMessage(skipped, type, "data export", "not exported"));

            var select = string.Join(',', columns.Select(c => c is LookupAttributeMetadata ? $"_{c.LogicalName}_value" : c.LogicalName));
            var query = $"{info.EntitySet}?$select={select}&$orderby={info.PrimaryId}";
            if (parse.GetValue(filter) is { Length: > 0 } f) query += "&$filter=" + Uri.EscapeDataString(f);
            var records = await client.GetAllPagesAsync(query, "odata.include-annotations=\"*\"", ct);

            var file = parse.GetValue(output)!;
            var csv = parse.GetValue(format) == "csv";
            try
            {
                await using var stream = new StreamWriter(file, false, new UTF8Encoding(false));
                if (csv)
                {
                    Csv.WriteRecord(stream, columns.Select(c => c.LogicalName));
                    foreach (var record in records) Csv.WriteRecord(stream, columns.Select(c => Cell(ExportValue(c, record))));
                }
                else
                {
                    var rows = records.Select(r => new JsonObject(columns.Select(c => KeyValuePair.Create(c.LogicalName, ExportValue(c, r)))));
                    await stream.WriteAsync(new JsonArray(rows.ToArray<JsonNode?>()).ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                }
            }
            catch (IOException ex)
            {
                throw new CliException(ExitCodes.Unexpected, $"Cannot write '{file}': {ex.Message}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new CliException(ExitCodes.Unexpected, $"Cannot write '{file}': {ex.Message}", ex);
            }

            ctx.WriteResult(new { table = info.Name, file, format = csv ? "csv" : "json", rows = records.Count },
                $"Exported {records.Count} rows of '{info.Name}' to {file}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>The exported value of a column of a Web API record: <c>table:guid</c> for a lookup, the JSON value otherwise.</summary>
    private static JsonNode? ExportValue(AttributeMetadata column, JsonElement record)
    {
        if (column is LookupAttributeMetadata lookup)
        {
            var key = $"_{column.LogicalName}_value";
            if (!record.TryGetProperty(key, out var id) || id.ValueKind != JsonValueKind.String) return null;
            var target = record.TryGetProperty(key + LookupLogicalName, out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()
                : lookup.Targets is { Length: 1 } single ? single[0]
                : throw new CliException(ExitCodes.Unexpected, $"The server did not report the table of the lookup '{column.LogicalName}'.");
            return JsonValue.Create($"{target}:{id.GetString()}");
        }
        return record.TryGetProperty(column.LogicalName, out var value) && value.ValueKind != JsonValueKind.Null
            ? JsonNode.Parse(value.GetRawText())
            : null;
    }

    private static string Cell(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value.ToJsonString(),
    };

    // ---- import -----------------------------------------------------------------------------------------------------

    private static Command Import(CliContext ctx)
    {
        var table = new Argument<string>("table") { Description = "Logical name of the table." };
        var file = new Option<string>("--file") { Description = "CSV or JSON file to import.", Required = true };
        var format = FormatOption();
        var batch = new Option<int>("--batch") { Description = "Rows sent to the server at once (one request each, in parallel); a failing row never affects the others.", DefaultValueFactory = _ => 100 };
        var command = new Command("import", "Create rows from a CSV or JSON file; failed rows are listed at the end and make the exit code 5.") { table, file, format, batch };
        command.SetAction(async (parse, ct) =>
        {
            var path = parse.GetValue(file)!;
            if (!File.Exists(path)) throw new CliException(ExitCodes.Usage, $"The file '{path}' does not exist.");
            if (parse.GetValue(batch) is < 1 or > 1000) throw new CliException(ExitCodes.Usage, "--batch must be between 1 and 1000.");

            var client = ctx.CreateClient();
            var info = await DataTable.LoadAsync(client, parse.GetValue(table)!, ct);
            var rows = ReadRows(await File.ReadAllTextAsync(path, ct), parse.GetValue(format) == "csv");
            var mapper = await RowMapper.CreateAsync(client, info, rows.Columns, ct);

            var failures = new List<RowFailure>();
            var bodies = new List<(int Row, JsonObject Body)>();
            foreach (var (number, cells) in rows.Rows)
            {
                try { bodies.Add((number, mapper.ToBody(cells))); }
                catch (FormatException ex) { failures.Add(new RowFailure(number, ex.Message)); }
            }
            var run = await RowWriter.CreateAsync(client, info.EntitySet, bodies, parse.GetValue(batch), ct);
            failures.AddRange(run.Failures);
            failures.Sort((a, b) => a.Row.CompareTo(b.Row));
            if (run.Aborted is not null) return await RowWriter.ReportAsync(ctx.Env.Err, rows.Rows.Count, failures, run);

            var total = rows.Rows.Count;
            ctx.WriteResult(new { table = info.Name, file = path, rows = total, imported = total - failures.Count, failed = failures.Count, failures },
                $"Imported {total - failures.Count} of {total} rows into '{info.Name}'.");
            return await RowWriter.ReportAsync(ctx.Env.Err, total, failures);
        });
        return command;
    }

    private sealed record ParsedRows(IReadOnlyList<string> Columns, IReadOnlyList<(int Number, IReadOnlyDictionary<string, JsonElement> Cells)> Rows);

    /// <summary>
    /// The rows of an import file. Row numbers: CSV, the physical line the record starts on (the header is on line 1, as in a
    /// spreadsheet); JSON, the position in the array starting at 1.
    /// </summary>
    private static ParsedRows ReadRows(string text, bool csv)
    {
        try
        {
            return csv ? ReadCsv(text) : ReadJson(text);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new CliException(ExitCodes.Usage, $"The {(csv ? "CSV" : "JSON")} file is invalid: {ex.Message}");
        }
    }

    private static ParsedRows ReadCsv(string text)
    {
        // Row numbers are physical line numbers (the header is on line 1 unless blank lines precede it). An empty line is skipped,
        // except when the header has a single column, where it is a legitimate row with an empty cell.
        var records = Csv.Parse(text).SkipWhile(r => r.IsBlankLine).ToList();
        if (records.Count == 0) throw new FormatException("it has no header row.");
        var header = records[0].Fields.Select(h => h.Trim()).ToList();
        if (header.Any(h => h.Length == 0)) throw new FormatException("the header has an empty column name.");

        var rows = new List<(int, IReadOnlyDictionary<string, JsonElement>)>();
        foreach (var record in records.Skip(1))
        {
            if (record.IsBlankLine && header.Count > 1) continue;
            var cells = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (record.Fields.Count != header.Count)
            {
                // Kept as a row so that it is reported as a failed row, not an abort of the whole file.
                cells[RowMapper.BadRow] = JsonSerializer.SerializeToElement($"it has {record.Fields.Count} fields, the header has {header.Count}.");
            }
            else
            {
                for (var c = 0; c < header.Count; c++)
                    if (record.Fields[c].Length > 0) cells[header[c]] = JsonSerializer.SerializeToElement(record.Fields[c]);
            }
            rows.Add((record.Line, cells));
        }
        return new ParsedRows(header, rows);
    }

    private static ParsedRows ReadJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("the top-level value must be an array of objects.");
        var columns = new List<string>();
        var rows = new List<(int, IReadOnlyDictionary<string, JsonElement>)>();
        var number = 0;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            number++;
            var cells = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (item.ValueKind != JsonValueKind.Object)
                cells[RowMapper.BadRow] = JsonSerializer.SerializeToElement("it is not a JSON object.");
            else
                foreach (var p in item.EnumerateObject())
                {
                    if (!columns.Contains(p.Name)) columns.Add(p.Name);
                    if (p.Value.ValueKind != JsonValueKind.Null) cells[p.Name] = p.Value.Clone();
                }
            rows.Add((number, cells));
        }
        return new ParsedRows(columns, rows);
    }
}

/// <summary>A row that was not created: its number in the file and why.</summary>
internal sealed record RowFailure(int Row, string Error);

/// <summary>Creates rows with one request each and collects the failures; shared by <c>data import</c> and <c>dev seed</c>.</summary>
internal static class RowWriter
{
    /// <summary>
    /// POST every body to <paramref name="set"/>, <paramref name="concurrency"/> at a time. A rejected row (including a 403
    /// on one row, e.g. no append-to privilege on its lookup target) is recorded and the rest go on (no shared transaction:
    /// one failing row never rolls back another); a row the server chose as a deadlock victim is sent again, up to
    /// <see cref="DeadlockAttempts"/> times in all. Only a failure that every following row would share stops the run: a 401
    /// (the token is wrong) or an unreachable server; the result then carries it in <see cref="RowRun.Aborted"/>.
    /// </summary>
    /// <summary>Tries of a row the server rolled back as a deadlock victim, the first included.</summary>
    private const int DeadlockAttempts = 3;

    /// <summary>The server's deadlock fault (500, Generic SQL error 0x80044150): the row was rolled back, retrying it is safe.</summary>
    private static bool IsDeadlock(CliException ex)
    {
        if (ex.HttpStatus != 500 || ex.ResponseBody is not { } body) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)
                && string.Equals(code.GetString(), "0x80044150", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task<RowRun> CreateAsync(XrmdClient client, string set, IReadOnlyList<(int Row, JsonObject Body)> rows,
        int concurrency, CancellationToken ct)
    {
        var run = new RowRun();
        var failures = new ConcurrentBag<RowFailure>();
        var created = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = stop.Token }, async (row, token) =>
            {
                try
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            await client.SendJsonAsync(HttpMethod.Post, set, row.Body, cancellationToken: token);
                            break;
                        }
                        catch (CliException ex) when (attempt < DeadlockAttempts && IsDeadlock(ex))
                        {
                            // The server rolled the row back and asks for a retry (rows written in parallel can deadlock).
                            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), token);
                        }
                    }
                    Interlocked.Increment(ref created);
                }
                catch (CliException ex) when (ex.ExitCode == ExitCodes.Unreachable || (ex.ExitCode == ExitCodes.Auth && ex.HttpStatus is null or 401))
                {
                    Interlocked.CompareExchange(ref run.AbortedField, ex, null);
                    await stop.CancelAsync();
                }
                catch (CliException ex)
                {
                    failures.Add(new RowFailure(row.Row, ex.Message));
                }
            });
        }
        catch (OperationCanceledException) when (run.AbortedField is not null && !ct.IsCancellationRequested)
        {
            // Stopped by the abort above.
        }
        run.Created = created;
        run.Failures = failures.OrderBy(f => f.Row).ToList();
        return run;
    }

    /// <summary>
    /// Print the failed rows to stderr (and, after an abort, how far the run got), then exit 5 when any row failed; an aborted
    /// run rethrows the failure that stopped it.
    /// </summary>
    public static async Task<int> ReportAsync(TextWriter err, int total, IReadOnlyList<RowFailure> failures, RowRun? run = null)
    {
        foreach (var f in failures) await err.WriteLineAsync($"Row {f.Row}: {f.Error}");
        if (run?.Aborted is { } aborted)
        {
            await err.WriteLineAsync($"Stopped: {run.Created} of {total} rows created, {failures.Count} failed before the run was stopped.");
            throw aborted;
        }
        if (failures.Count == 0) return ExitCodes.Ok;
        await err.WriteLineAsync($"{failures.Count} of {total} rows failed.");
        return ExitCodes.Rejected;
    }
}

/// <summary>The outcome of <see cref="RowWriter.CreateAsync"/>.</summary>
internal sealed class RowRun
{
    internal CliException? AbortedField;
    public int Created { get; set; }
    public List<RowFailure> Failures { get; set; } = [];
    public CliException? Aborted => AbortedField;
}

/// <summary>Turns the cells of an import file into a Web API create body for one table.</summary>
internal sealed class RowMapper
{
    /// <summary>Pseudo column that marks a row the reader could not split into cells; its value is the reason.</summary>
    public const string BadRow = "\0row";

    private readonly DataTable _table;
    private readonly Dictionary<string, string> _sets; // table logical name → entity set, for the lookup targets

    private RowMapper(DataTable table, Dictionary<string, string> sets)
    {
        _table = table;
        _sets = sets;
    }

    /// <exception cref="CliException">Usage: a column that does not exist or cannot be written on create.</exception>
    public static async Task<RowMapper> CreateAsync(XrmdClient client, DataTable table, IReadOnlyList<string> columns, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in columns)
        {
            if (table.Unsupported.TryGetValue(name, out var unsupportedType))
                throw new CliException(ExitCodes.Usage, $"The column '{name}' of '{table.Name}' has the type {unsupportedType}, which data import does not support.");
            var column = table.Find(name) ?? throw new CliException(ExitCodes.Usage, $"'{table.Name}' has no column '{name}'.");
            if (!table.IsWritable(column))
                throw new CliException(ExitCodes.Usage, $"The column '{name}' of '{table.Name}' cannot be set on create (read-only or not a data column).");
            if (!seen.Add(name)) throw new CliException(ExitCodes.Usage, $"The column '{name}' appears twice.");
            if (column is LookupAttributeMetadata lookup)
                foreach (var target in lookup.Targets ?? [])
                    if (!sets.ContainsKey(target))
                        sets[target] = await DataTable.EntitySetAsync(client, target, ct);
        }
        return new RowMapper(table, sets);
    }

    /// <exception cref="FormatException">A cell that is not valid for its column.</exception>
    public JsonObject ToBody(IReadOnlyDictionary<string, JsonElement> cells)
    {
        if (cells.TryGetValue(BadRow, out var bad)) throw new FormatException(bad.GetString());
        var body = new JsonObject();
        foreach (var (name, value) in cells)
        {
            var column = _table.Find(name)!;
            try
            {
                if (column is LookupAttributeMetadata lookup)
                {
                    var (target, reference) = Bind(lookup, value);
                    body[_table.Navigation(lookup, target) + "@odata.bind"] = reference;
                }
                else body[name] = Convert(column, value);
            }
            catch (FormatException ex)
            {
                throw new FormatException($"column '{name}': {ex.Message}");
            }
        }
        return body;
    }

    private (string Target, JsonNode Reference) Bind(LookupAttributeMetadata lookup, JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        var colon = text.IndexOf(':');
        if (colon <= 0 || !Guid.TryParse(text[(colon + 1)..], out var id))
            throw new FormatException($"'{text}' is not a lookup value: write table:guid, e.g. {lookup.Targets?.FirstOrDefault() ?? "account"}:{Guid.Empty}.");
        var target = text[..colon];
        return _sets.TryGetValue(target, out var set) && (lookup.Targets ?? []).Contains(target)
            ? (target, JsonValue.Create($"/{set}({id})")!)
            : throw new FormatException($"'{target}' is not a target of this lookup (targets: {string.Join(", ", lookup.Targets ?? [])}).");
    }

    private static JsonNode? Convert(AttributeMetadata column, JsonElement v)
    {
        string Text() => v.ValueKind == JsonValueKind.String ? v.GetString()!
            : throw new FormatException($"expected text, got {v.ValueKind.ToString().ToLowerInvariant()}.");
        var invariant = CultureInfo.InvariantCulture;

        switch (column)
        {
            case StringAttributeMetadata or MemoAttributeMetadata:
                return JsonValue.Create(Text());
            case IntegerAttributeMetadata or BigIntAttributeMetadata or PicklistAttributeMetadata or StateAttributeMetadata or StatusAttributeMetadata:
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var whole)) return JsonValue.Create(whole);
                return long.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : "", NumberStyles.AllowLeadingSign, invariant, out var parsed)
                    ? JsonValue.Create(parsed)
                    : throw new FormatException($"'{v}' is not a whole number.");
            case DecimalAttributeMetadata or MoneyAttributeMetadata:
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var number)) return JsonValue.Create(number);
                return decimal.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : "", NumberStyles.Float, invariant, out var money)
                    ? JsonValue.Create(money)
                    : throw new FormatException($"'{v}' is not a number.");
            case DoubleAttributeMetadata:
                if (v.ValueKind == JsonValueKind.Number) return JsonNode.Parse(v.GetRawText());
                return double.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : "", NumberStyles.Float, invariant, out var real) && double.IsFinite(real)
                    ? JsonValue.Create(real)
                    : throw new FormatException($"'{v}' is not a number.");
            case BooleanAttributeMetadata:
                if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return JsonValue.Create(v.GetBoolean());
                return v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var flag)
                    ? JsonValue.Create(flag)
                    : throw new FormatException($"'{v}' is not true or false.");
            case DateTimeAttributeMetadata:
                return DateTimeOffset.TryParse(Text(), invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
                    ? JsonValue.Create(when.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", invariant))
                    : throw new FormatException($"'{v}' is not a date (use ISO 8601, e.g. 2024-03-01T08:30:00Z).");
            case MultiSelectPicklistAttributeMetadata:
                return JsonValue.Create(v.ValueKind == JsonValueKind.Number ? v.GetRawText() : Text());
            case UniqueIdentifierAttributeMetadata:
                return Guid.TryParse(Text(), out var guid) ? JsonValue.Create(guid.ToString("D")) : throw new FormatException($"'{v}' is not a GUID.");
            default:
                throw new FormatException($"the type {column.AttributeType} cannot be imported.");
        }
    }
}

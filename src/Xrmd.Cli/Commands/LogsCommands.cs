using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Xrmd.Cli.Output;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd logs</c>: the server's in-memory log buffer (<c>GET /admin/logs</c>). Not <c>server logs</c>, which shows the
/// container logs. The server filters by level and time; <c>--category</c> is applied here (case-insensitive substring).
/// </summary>
public static class LogsCommands
{
    private const int PageSize = 1000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public static Command Build(CliContext ctx)
    {
        var since = new Option<string?>("--since") { Description = "Only entries not before this ISO 8601 date and time." };
        var level = new Option<string?>("--level") { Description = "Minimum level: Trace, Debug, Information, Warning, Error or Critical." };
        var category = new Option<string?>("--category") { Description = "Only entries whose category contains this text (case-insensitive)." };
        var take = new Option<int?>("--take") { Description = $"Number of most recent entries (server default 100, at most {PageSize})." };
        var follow = new Option<bool>("--follow", "-f") { Description = "Keep polling for new entries until cancelled (json: one object per line)." };
        var command = new Command("logs", "Show the server's recent log entries (GET /admin/logs).") { since, level, category, take, follow };
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var sinceText = parse.GetValue(since);
            var levelText = parse.GetValue(level);
            var categoryText = parse.GetValue(category);
            var takeValue = parse.GetValue(take);

            if (!parse.GetValue(follow))
            {
                var entries = await FetchAsync(client, sinceText, levelText, takeValue, ct);
                ctx.Write(Filter(entries, categoryText).Select(e => e.Element).ToList());
                return ExitCodes.Ok;
            }

            // Follow: poll with `since` = the newest timestamp seen (inclusive on the server, so entries at that
            // instant are de-duplicated here; the cursor advances over filtered-out entries too).
            DateTimeOffset? last = null;
            var seen = new HashSet<string>();
            var cursor = sinceText;
            var firstTake = takeValue; // like without --follow: the server default (100) unless --take
            try
            {
                while (true)
                {
                    foreach (var entry in await FetchAsync(client, cursor, levelText, firstTake, ct))
                    {
                        if (last is { } l && entry.Timestamp < l) continue;
                        if (last != entry.Timestamp) { last = entry.Timestamp; seen.Clear(); }
                        if (!seen.Add(entry.Element.GetRawText())) continue;
                        if (Matches(entry, categoryText)) Print(ctx, entry.Element);
                    }
                    if (last is { } newest) cursor = newest.ToString("O", CultureInfo.InvariantCulture);
                    firstTake = takeValue ?? PageSize; // later polls must not miss entries between polls
                    await Task.Delay(PollInterval, ctx.Env.Clock, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return ExitCodes.Ok; // Ctrl+C is how --follow ends
            }
        });
        return command;
    }

    private sealed record Entry(DateTimeOffset Timestamp, string? Category, JsonElement Element);

    private static async Task<List<Entry>> FetchAsync(Http.XrmdClient client, string? since, string? level, int? take, CancellationToken ct)
    {
        var query = new List<string>();
        if (since is not null) query.Add("since=" + Uri.EscapeDataString(since));
        if (level is not null) query.Add("level=" + Uri.EscapeDataString(level));
        if (take is { } t) query.Add("take=" + t.ToString(CultureInfo.InvariantCulture));
        var response = await client.AdminAsync(HttpMethod.Get, "logs" + (query.Count == 0 ? "" : "?" + string.Join('&', query)), cancellationToken: ct);
        var json = response.Json;
        if (json is not { ValueKind: JsonValueKind.Object } body || !body.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new CliException(ExitCodes.Unexpected, "GET /admin/logs did not return a value array.");
        return value.EnumerateArray().Select(e => new Entry(
            e.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var parsed) ? parsed : DateTimeOffset.MinValue,
            e.TryGetProperty("category", out var c) ? c.GetString() : null,
            e)).ToList();
    }

    private static bool Matches(Entry entry, string? category) =>
        category is null || entry.Category?.Contains(category, StringComparison.OrdinalIgnoreCase) == true;

    private static IEnumerable<Entry> Filter(IEnumerable<Entry> entries, string? category) => entries.Where(e => Matches(e, category));

    private static void Print(CliContext ctx, JsonElement entry)
    {
        if (ctx.Format == OutputFormat.Json)
        {
            ctx.Env.Out.WriteLine(entry.GetRawText());
            return;
        }
        string Field(string name) => entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        ctx.Env.Out.WriteLine($"{Field("timestamp")}  {Field("level"),-11}  {Field("category")}  {Field("message")}");
        if (Field("exception") is { Length: > 0 } ex) ctx.Env.Out.WriteLine(ex);
    }
}

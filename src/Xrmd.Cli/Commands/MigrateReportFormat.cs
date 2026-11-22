using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Xrmd.Cli.Commands;

/// <summary>The report of a run (<c>GET /admin/migrate/runs/{id}/report</c>) as Markdown, JSON or CSV (the issues) (plan M4b task 4).</summary>
internal static class MigrateReportFormat
{
    public static string Markdown(JsonElement report)
    {
        var md = new StringBuilder();
        md.AppendLine("# Migration report").AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Run `{Str(report, "runId")}`, state **{Str(report, "state")}**.").AppendLine();

        md.AppendLine("## Tables").AppendLine().AppendLine("| Table | Exported | Imported | Failed |").AppendLine("|---|---:|---:|---:|");
        foreach (var t in Array(report, "tables"))
            md.AppendLine(CultureInfo.InvariantCulture, $"| {Str(t, "table")} | {Str(t, "exported")} | {Str(t, "imported")} | {Str(t, "failed")} |");

        md.AppendLine().AppendLine("## Users").AppendLine().AppendLine("| Action | Users |").AppendLine("|---|---:|");
        if (report.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Object)
            foreach (var u in users.EnumerateObject()) md.AppendLine(CultureInfo.InvariantCulture, $"| {u.Name} | {u.Value} |");

        var lookups = Array(report, "lookupsNulled").ToList();
        if (lookups.Count > 0)
        {
            md.AppendLine().AppendLine("## Lookups left empty").AppendLine().AppendLine("| Table | Column | Records |").AppendLine("|---|---|---:|");
            foreach (var l in lookups) md.AppendLine(CultureInfo.InvariantCulture, $"| {Str(l, "table")} | {Str(l, "column")} | {Str(l, "count")} |");
        }

        var off = Array(report, "deactivated").ToList();
        if (off.Count > 0)
        {
            md.AppendLine().AppendLine("## Business logic left off").AppendLine()
                .AppendLine("Switch it back on with `xrmd migrate activate`.").AppendLine()
                .AppendLine("| Table | Name | Id | Source state | Activated on |").AppendLine("|---|---|---|---:|---|");
            foreach (var d in off)
                md.AppendLine(CultureInfo.InvariantCulture,
                    $"| {Str(d, "table")} | {Cell(Str(d, "name"))} | {Str(d, "id")} | {Str(d, "sourceState")} | {Str(d, "activatedOn")} |");
        }

        var issues = Array(report, "issues").ToList();
        md.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## Issues ({issues.Count}{(Bool(report, "issuesTruncated") ? ", truncated" : "")})").AppendLine();
        if (issues.Count > 0)
        {
            md.AppendLine("| Phase | Severity | Code | Table | Record | Column | Message |").AppendLine("|---:|---|---|---|---|---|---|");
            foreach (var i in issues)
                md.AppendLine(CultureInfo.InvariantCulture,
                    $"| {Str(i, "phase")} | {Str(i, "severity")} | {Str(i, "code")} | {Str(i, "table")} | {Str(i, "recordId")} | {Str(i, "column")} | {Cell(Str(i, "message"))} |");
        }

        md.AppendLine().AppendLine("## Phases").AppendLine().AppendLine("| Phase | Name | Seconds |").AppendLine("|---:|---|---:|");
        foreach (var p in Array(report, "phases")) md.AppendLine(CultureInfo.InvariantCulture, $"| {Str(p, "phase")} | {Str(p, "name")} | {Str(p, "seconds")} |");
        return md.ToString();
    }

    /// <summary>The issues as CSV (RFC 4180), one per line, with a header.</summary>
    public static string Csv(JsonElement report)
    {
        var csv = new StringBuilder("phase,severity,code,table,recordId,column,message\n");
        foreach (var i in Array(report, "issues"))
            csv.Append(string.Join(",", new[] { "phase", "severity", "code", "table", "recordId", "column", "message" }.Select(k => Quote(Str(i, k))))).Append('\n');
        return csv.ToString();
    }

    private static IEnumerable<JsonElement> Array(JsonElement o, string name) =>
        o.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var b) && b.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement o, string name) =>
        !o.TryGetProperty(name, out var v) ? "" : v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.GetRawText(),
        };

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

using System.Text.Encodings.Web;
using System.Text.Json;

namespace Xrmd.Cli.Output;

/// <summary>The values of the global <c>--output</c> option.</summary>
public enum OutputFormat
{
    Table,
    Json,
}

/// <summary>
/// Writes command results to stdout. <c>json</c>: one indented JSON value and nothing else. <c>table</c>:
/// a collection is a table with aligned columns (union of the row properties, in order of first appearance);
/// a single object is one aligned <c>property  value</c> line per property. Nested values are compact JSON.
/// Objects are serialized with web defaults (camelCase); <see cref="JsonElement"/>s are written as they are.
/// </summary>
public sealed class Renderer(TextWriter output)
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // terminal output, not HTML
    };
    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private const string Gap = "  ";

    public void Write(object rows, OutputFormat format)
    {
        var element = rows as JsonElement? ?? JsonSerializer.SerializeToElement(rows, rows.GetType(), Compact);
        if (format == OutputFormat.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(element, Indented));
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                WriteTable(element.EnumerateArray().ToList());
                break;
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                var width = properties.Count == 0 ? 0 : properties.Max(p => p.Name.Length);
                foreach (var p in properties)
                    output.WriteLine((p.Name.PadRight(width) + Gap + Cell(p.Value)).TrimEnd());
                break;
            default:
                output.WriteLine(Cell(element));
                break;
        }
    }

    private void WriteTable(List<JsonElement> rows)
    {
        if (rows.Count == 0)
        {
            output.WriteLine("(no rows)");
            return;
        }

        var columns = new List<string>();
        foreach (var row in rows.Where(r => r.ValueKind == JsonValueKind.Object))
            foreach (var p in row.EnumerateObject())
                if (!columns.Contains(p.Name)) columns.Add(p.Name);
        if (columns.Count == 0 || rows.Any(r => r.ValueKind != JsonValueKind.Object))
        {
            foreach (var row in rows) output.WriteLine(Cell(row));
            return;
        }

        var cells = rows.Select(r => columns.Select(c => r.TryGetProperty(c, out var v) ? Cell(v) : "").ToArray()).ToList();
        var widths = columns.Select((c, i) => Math.Max(c.Length, cells.Max(r => r[i].Length))).ToArray();
        output.WriteLine(Line(columns.ToArray(), widths));
        foreach (var row in cells) output.WriteLine(Line(row, widths));
    }

    private static string Line(string[] values, int[] widths) =>
        string.Join(Gap, values.Select((v, i) => v.PadRight(widths[i]))).TrimEnd();

    private static string Cell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString()!.ReplaceLineEndings(" "),
        JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(value, Compact),
        _ => value.GetRawText(),
    };
}

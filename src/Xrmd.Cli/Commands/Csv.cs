using System.Text;

namespace Xrmd.Cli.Commands;

/// <summary>RFC 4180 CSV: comma separated, fields with commas, quotes or line breaks in double quotes, quotes doubled, CRLF records.</summary>
internal static class Csv
{
    /// <summary>One CSV record: its fields, the physical line it starts on (1-based, as a spreadsheet or editor numbers it) and whether that line was empty.</summary>
    public sealed record Record(int Line, IReadOnlyList<string> Fields, bool IsBlankLine);

    /// <summary>
    /// The records of <paramref name="text"/> (a leading BOM is skipped). An empty line is a record with one empty field and
    /// <see cref="Record.IsBlankLine"/> (the caller decides: with a single column it is a value, otherwise it is skipped);
    /// empty lines at the end of the text are dropped. Line breaks are CRLF, LF or a bare CR, also inside quotes.
    /// </summary>
    /// <exception cref="FormatException">An unterminated quote, or a quote in the middle of an unquoted field.</exception>
    public static List<Record> Parse(string text)
    {
        var records = new List<Record>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var i = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        var line = 1;
        var recordLine = 1;
        var quoted = false;      // inside a quoted field
        var hasContent = false;  // the current record has any character

        void EndRecord()
        {
            if (hasContent) { fields.Add(field.ToString()); records.Add(new Record(recordLine, fields, false)); }
            else records.Add(new Record(recordLine, [""], true));
            field.Clear();
            fields = [];
            hasContent = false;
            recordLine = line;
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    quoted = false;
                    i++;
                    if (i < text.Length && text[i] is not (',' or '\r' or '\n'))
                        throw new FormatException($"line {line}: unexpected character '{text[i]}' after a closing quote.");
                    continue;
                }
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { field.Append("\r\n"); i += 2; line++; continue; }
                if (c is '\r' or '\n') line++;
                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    hasContent = true;
                    i++;
                    break;
                case '"':
                    throw new FormatException($"line {line}: a quote in the middle of an unquoted field.");
                case ',':
                    hasContent = true;
                    fields.Add(field.ToString());
                    field.Clear();
                    i++;
                    break;
                case '\r' or '\n':
                    i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                    line++;
                    EndRecord();
                    break;
                default:
                    field.Append(c);
                    hasContent = true;
                    i++;
                    break;
            }
        }

        if (quoted) throw new FormatException($"line {recordLine}: unterminated quoted field.");
        if (hasContent) EndRecord();
        while (records.Count > 0 && records[^1].IsBlankLine) records.RemoveAt(records.Count - 1);
        return records;
    }

    public static void WriteRecord(TextWriter writer, IEnumerable<string> fields)
    {
        writer.Write(string.Join(',', fields.Select(Escape)));
        writer.Write("\r\n");
    }

    private static string Escape(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
}

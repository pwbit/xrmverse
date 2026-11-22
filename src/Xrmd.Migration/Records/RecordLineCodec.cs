using System.Globalization;
using System.Text;
using System.Text.Json;
using Xrmd.Migration.Packaging;

namespace Xrmd.Migration.Records;

/// <summary>
/// One record per JSON line: <c>{"id":"…","v":{…}}</c>. A JSON string is a <see cref="StringValue"/> and <c>null</c> a
/// <see cref="NullValue"/>; every other value is an object with exactly one tag: <c>int</c>, <c>dec</c> (string),
/// <c>dbl</c>, <c>money</c> (string), <c>bool</c>, <c>dt</c> (UTC, <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>), <c>date</c>
/// (<c>yyyy-MM-dd</c>), <c>opt</c>, <c>opts</c>, <c>guid</c>, <c>ref</c> + <c>id</c>, <c>parties</c>, <c>file</c> +
/// <c>name</c> + <c>mime</c> + <c>bytes</c>, <c>image</c>. Decimals travel as strings so no digit is lost.
/// </summary>
public static class RecordLineCodec
{
    private const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string DateFormat = "yyyy-MM-dd";

    public static string Write(MigrationRecord record)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("id", record.Id);
            w.WriteStartObject("v");
            foreach (var (column, value) in record.Values)
            {
                w.WritePropertyName(column);
                WriteValue(w, value);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter w, MigrationValue value)
    {
        switch (value)
        {
            case NullValue: w.WriteNullValue(); return;
            case StringValue s: w.WriteStringValue(s.Value); return;
        }
        w.WriteStartObject();
        switch (value)
        {
            case IntValue v: w.WriteNumber("int", v.Value); break;
            case DecimalValue v: w.WriteString("dec", v.Value.ToString(CultureInfo.InvariantCulture)); break;
            case DoubleValue v: w.WriteNumber("dbl", v.Value); break;
            case MoneyValue v: w.WriteString("money", v.Value.ToString(CultureInfo.InvariantCulture)); break;
            case BoolValue v: w.WriteBoolean("bool", v.Value); break;
            case DateTimeValue v: w.WriteString("dt", v.Utc.ToString(DateTimeFormat, CultureInfo.InvariantCulture)); break;
            case DateOnlyValue v: w.WriteString("date", v.Value.ToString(DateFormat, CultureInfo.InvariantCulture)); break;
            case OptionValue v: w.WriteNumber("opt", v.Value); break;
            case OptionsValue v:
                w.WriteStartArray("opts");
                foreach (var o in v.Values) w.WriteNumberValue(o);
                w.WriteEndArray();
                break;
            case GuidValue v: w.WriteString("guid", v.Value); break;
            case LookupValue v:
                w.WriteString("ref", v.Table);
                w.WriteString("id", v.Id);
                break;
            case PartyListValue v:
                w.WriteStartArray("parties");
                foreach (var p in v.Parties)
                {
                    w.WriteStartObject();
                    w.WriteNumber("mask", p.ParticipationMask);
                    if (p.Table is not null) w.WriteString("ref", p.Table);
                    if (p.Id is { } id) w.WriteString("id", id);
                    if (p.Address is not null) w.WriteString("addr", p.Address);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
            case FileValue v:
                w.WriteString("file", v.Path);
                w.WriteString("name", v.FileName);
                w.WriteString("mime", v.MimeType);
                w.WriteNumber("bytes", v.Bytes);
                break;
            case ImageValue v: w.WriteString("image", v.Path); break;
            default: throw new ArgumentException($"Unknown value type {value.GetType().Name}.", nameof(value));
        }
        w.WriteEndObject();
    }

    /// <exception cref="PackageFormatException">The line is not a record, or a value has an unknown tag.</exception>
    public static MigrationRecord Read(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetGuid();
            var values = new Dictionary<string, MigrationValue>(StringComparer.Ordinal);
            foreach (var column in root.GetProperty("v").EnumerateObject())
                values[column.Name] = ReadValue(column.Name, column.Value);
            return new MigrationRecord(id, values);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new PackageFormatException($"A record line is not valid: {e.Message}");
        }
    }

    private static MigrationValue ReadValue(string column, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null: return NullValue.Instance;
            case JsonValueKind.String: return new StringValue(e.GetString()!);
            case JsonValueKind.Object: break;
            default: throw new PackageFormatException($"Column '{column}': a value must be a string, null or a tagged object.");
        }
        var tag = e.EnumerateObject().FirstOrDefault().Name;
        return tag switch
        {
            "int" => new IntValue(e.GetProperty("int").GetInt64()),
            "dec" => new DecimalValue(decimal.Parse(e.GetProperty("dec").GetString()!, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)),
            "dbl" => new DoubleValue(e.GetProperty("dbl").GetDouble()),
            "money" => new MoneyValue(decimal.Parse(e.GetProperty("money").GetString()!, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)),
            "bool" => new BoolValue(e.GetProperty("bool").GetBoolean()),
            "dt" => new DateTimeValue(DateTime.ParseExact(e.GetProperty("dt").GetString()!, DateTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)),
            "date" => new DateOnlyValue(DateOnly.ParseExact(e.GetProperty("date").GetString()!, DateFormat, CultureInfo.InvariantCulture)),
            "opt" => new OptionValue(e.GetProperty("opt").GetInt32()),
            "opts" => new OptionsValue([.. e.GetProperty("opts").EnumerateArray().Select(o => o.GetInt32())]),
            "guid" => new GuidValue(e.GetProperty("guid").GetGuid()),
            "ref" => new LookupValue(e.GetProperty("ref").GetString()!, e.GetProperty("id").GetGuid()),
            "parties" => new PartyListValue([.. e.GetProperty("parties").EnumerateArray().Select(p => new Party(
                p.GetProperty("mask").GetInt32(),
                p.TryGetProperty("ref", out var r) ? r.GetString() : null,
                p.TryGetProperty("id", out var i) ? i.GetGuid() : null,
                p.TryGetProperty("addr", out var a) ? a.GetString() : null))]),
            "file" => new FileValue(e.GetProperty("file").GetString()!, e.GetProperty("name").GetString()!, e.GetProperty("mime").GetString()!, e.GetProperty("bytes").GetInt64()),
            "image" => new ImageValue(e.GetProperty("image").GetString()!),
            _ => throw new PackageFormatException($"Column '{column}': unknown value tag '{tag}'."),
        };
    }
}

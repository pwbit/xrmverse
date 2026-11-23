using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xrmd.Model.Metadata;

/// <summary>
/// Writes a key/value map as the Web API metadata JSON has it: an array of <c>{"Key":...,"Value":...}</c> pairs
/// (<c>EntityReference.KeyAttributes</c>), not a JSON object. Reads that array, and a JSON object too.
/// </summary>
internal sealed class KeyValueArrayConverter : JsonConverter<Dictionary<string, JsonElement>>
{
    private const string KeyName = "Key";
    private const string ValueName = "Value";

    public override Dictionary<string, JsonElement> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var pair in root.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Object || !pair.TryGetProperty(KeyName, out var key) || key.ValueKind != JsonValueKind.String)
                        throw new JsonException("A key/value pair needs a string \"Key\".");
                    result[key.GetString()!] = pair.TryGetProperty(ValueName, out var value) ? value.Clone() : default;
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in root.EnumerateObject()) result[property.Name] = property.Value.Clone();
                break;
            default:
                throw new JsonException($"Expected an array of key/value pairs, found {root.ValueKind}.");
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, JsonElement> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var (key, element) in value)
        {
            writer.WriteStartObject();
            writer.WriteString(KeyName, key);
            writer.WritePropertyName(ValueName);
            if (element.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
            else element.WriteTo(writer);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}

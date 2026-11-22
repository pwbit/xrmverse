using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Xrmd.Audit;

/// <summary>
/// The codec of <c>audit.changedata</c> (plan 13, ruling A3): xrmverse's own versioned JSON, which clients never parse (the
/// audit messages are their contract). It decodes without metadata, so the rows of a table or column deleted since stay readable.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Attribute events: <c>{"v":1,"old":{name:value},"new":{...}}</c>.</item>
/// <item>Values: a JSON string, number or boolean for text, whole numbers and booleans; tagged objects for the rest
/// (<c>$lookup</c> with id, logicalName and name, <c>$option</c>, <c>$options</c>, <c>$money</c> and <c>$decimal</c> as invariant
/// strings, <c>$double</c>, <c>$datetime</c> as ISO 8601 UTC with milliseconds, <c>$guid</c>); JSON null for a cleared value.</item>
/// <item>Text longer than <see cref="MaxTextLength"/> characters is cut to that length and ends with <c>…</c>.</item>
/// <item>Other events: <c>{"v":1,"kind":"relationship"|"share"|"roleprivileges",...}</c>.</item>
/// </list>
/// A whole number decodes as <see cref="int"/> when it fits and as <see cref="long"/> otherwise: the format does not tell an
/// integer column from a big integer one, so a reader with the metadata converts a small big integer back to <see cref="long"/>.
/// </remarks>
public static class ChangeData
{
    /// <summary>The version this codec writes and the only one it reads.</summary>
    public const int Version = 1;

    /// <summary>The longest text kept as it is; a longer one is cut and marked with <see cref="Ellipsis"/>.</summary>
    public const int MaxTextLength = 5000;

    /// <summary>The mark ending a cut text.</summary>
    public const string Ellipsis = "…";

    private const string V = "v", Kind = "kind", Old = "old", New = "new";
    private const string Relationship = "relationship", Share = "share", RolePrivileges = "roleprivileges";
    private const string Lookup = "$lookup", Option = "$option", Options = "$options", MoneyTag = "$money", DecimalTag = "$decimal",
        DoubleTag = "$double", DateTimeTag = "$datetime", GuidTag = "$guid";
    private const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>The JSON of <paramref name="change"/>.</summary>
    /// <exception cref="NotSupportedException">A value of a type the format has no representation for (e.g. an image's bytes).</exception>
    public static string Serialize(AuditChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber(V, Version);
            switch (change)
            {
                case AttributeChange a:
                    WriteValues(w, Old, a.Old);
                    WriteValues(w, New, a.New);
                    break;
                case RelationshipChange r:
                    w.WriteString(Kind, Relationship);
                    w.WriteString(Relationship, r.Relationship);
                    w.WriteStartArray("targets");
                    foreach (var target in r.Targets) WriteLookup(w, target);
                    w.WriteEndArray();
                    break;
                case ShareChange s:
                    w.WriteString(Kind, Share);
                    w.WritePropertyName("principal");
                    WriteLookup(w, s.Principal);
                    w.WriteNumber(Old, (int)s.Old);
                    w.WriteNumber(New, (int)s.New);
                    break;
                case RolePrivilegesChange p:
                    w.WriteString(Kind, RolePrivileges);
                    WritePrivileges(w, Old, p.Old);
                    WritePrivileges(w, New, p.New);
                    break;
                default:
                    throw new NotSupportedException($"The audit change '{change.GetType().Name}' has no change data format.");
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The change stored as <paramref name="json"/>.</summary>
    /// <exception cref="FormatException">Not change data of <see cref="Version"/>: invalid JSON, another or no version, an
    /// unknown kind or value tag.</exception>
    public static AuditChange Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The audit change data is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(V, out var version) || version.ValueKind != JsonValueKind.Number)
                throw new FormatException("The audit change data has no version.");
            if (!version.TryGetInt32(out var v) || v != Version)
                throw new FormatException($"The audit change data has version {version.GetRawText()}; this server reads version {Version} only.");

            try
            {
                var kind = root.TryGetProperty(Kind, out var k) ? k.GetString() : null;
                return kind switch
                {
                    null => new AttributeChange(ReadValues(root, Old), ReadValues(root, New)),
                    Relationship => new RelationshipChange(Required(root, Relationship).GetString() ?? "",
                        Required(root, "targets").EnumerateArray().Select(ReadLookup).ToList()),
                    Share => new ShareChange(ReadLookup(Required(root, "principal")), (AccessRights)Required(root, Old).GetInt32(),
                        (AccessRights)Required(root, New).GetInt32()),
                    RolePrivileges => new RolePrivilegesChange(ReadPrivileges(Required(root, Old)), ReadPrivileges(Required(root, New))),
                    _ => throw new FormatException($"The audit change data has the unknown kind '{kind}'."),
                };
            }
            catch (InvalidOperationException ex)
            {
                // A member of the wrong JSON type (e.g. a string where a number belongs).
                throw new FormatException("The audit change data is malformed: " + ex.Message, ex);
            }
        }
    }

    // ---- writing ----

    private static void WriteValues(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, object?> values)
    {
        w.WriteStartObject(name);
        foreach (var (attribute, value) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            w.WritePropertyName(attribute);
            WriteValue(w, attribute, value);
        }
        w.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter w, string attribute, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case string text:
                w.WriteStringValue(Cap(text));
                break;
            case bool flag:
                w.WriteBooleanValue(flag);
                break;
            case int number:
                w.WriteNumberValue(number);
                break;
            case long number:
                w.WriteNumberValue(number);
                break;
            case EntityReference reference:
                WriteLookup(w, reference);
                break;
            case OptionSetValueCollection options:
                w.WriteStartObject();
                w.WriteStartArray(Options);
                foreach (var option in options) w.WriteNumberValue(option.Value);
                w.WriteEndArray();
                w.WriteEndObject();
                break;
            case OptionSetValue option:
                Tagged(w, Option, () => w.WriteNumberValue(option.Value));
                break;
            case Money money:
                Tagged(w, MoneyTag, () => w.WriteStringValue(money.Value.ToString(CultureInfo.InvariantCulture)));
                break;
            case decimal number:
                Tagged(w, DecimalTag, () => w.WriteStringValue(number.ToString(CultureInfo.InvariantCulture)));
                break;
            case double number:
                Tagged(w, DoubleTag, () => w.WriteNumberValue(number));
                break;
            case DateTime date:
                var utc = date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc);
                Tagged(w, DateTimeTag, () => w.WriteStringValue(utc.ToString(DateTimeFormat, CultureInfo.InvariantCulture)));
                break;
            case Guid id:
                Tagged(w, GuidTag, () => w.WriteStringValue(id));
                break;
            default:
                throw new NotSupportedException(
                    $"The value of '{attribute}' ({value.GetType().Name}) cannot be stored in the audit change data.");
        }
    }

    private static void Tagged(Utf8JsonWriter w, string tag, Action write)
    {
        w.WriteStartObject();
        w.WritePropertyName(tag);
        write();
        w.WriteEndObject();
    }

    private static void WriteLookup(Utf8JsonWriter w, EntityReference reference)
    {
        w.WriteStartObject();
        w.WriteStartObject(Lookup);
        w.WriteString("id", reference.Id);
        w.WriteString("logicalName", reference.LogicalName);
        if (reference.Name is null) w.WriteNull("name");
        else w.WriteString("name", Cap(reference.Name));
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WritePrivileges(Utf8JsonWriter w, string name, IReadOnlyList<RolePrivilege> privileges)
    {
        w.WriteStartArray(name);
        foreach (var privilege in privileges)
        {
            w.WriteStartObject();
            w.WriteString("id", privilege.PrivilegeId);
            w.WriteNumber("depth", (int)privilege.Depth);
            w.WriteString("businessUnitId", privilege.BusinessUnitId);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary><paramref name="text"/>, cut to <see cref="MaxTextLength"/> characters (never inside a surrogate pair) plus <see cref="Ellipsis"/> when longer.</summary>
    private static string Cap(string text)
    {
        if (text.Length <= MaxTextLength) return text;
        var length = char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
        return string.Concat(text.AsSpan(0, length), Ellipsis);
    }

    // ---- reading ----

    private static JsonElement Required(JsonElement e, string name) =>
        e.TryGetProperty(name, out var value) ? value : throw new FormatException($"The audit change data has no '{name}'.");

    private static Dictionary<string, object?> ReadValues(JsonElement root, string name)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var element = Required(root, name);
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException($"The audit change data's '{name}' is not an object.");
        foreach (var property in element.EnumerateObject()) values[property.Name] = ReadValue(property.Value);
        return values;
    }

    private static object? ReadValue(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                if (e.TryGetInt32(out var small)) return small;
                if (e.TryGetInt64(out var big)) return big;
                throw new FormatException($"The audit change data holds the number {e.GetRawText()}, which is not a whole number.");
            case JsonValueKind.Object:
                var members = e.EnumerateObject().ToList();
                if (members.Count != 1) throw new FormatException("A tagged value of the audit change data has more than one member.");
                var (tag, value) = (members[0].Name, members[0].Value);
                return tag switch
                {
                    Lookup => ReadLookup(e),
                    Option => new OptionSetValue(value.GetInt32()),
                    Options => new OptionSetValueCollection(value.EnumerateArray().Select(o => new OptionSetValue(o.GetInt32())).ToList()),
                    MoneyTag => new Money(decimal.Parse(value.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)),
                    DecimalTag => decimal.Parse(value.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture),
                    DoubleTag => value.GetDouble(),
                    DateTimeTag => DateTime.SpecifyKind(DateTime.ParseExact(value.GetString()!, DateTimeFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), DateTimeKind.Utc),
                    GuidTag => value.GetGuid(),
                    _ => throw new FormatException($"The audit change data holds the unknown value tag '{tag}'."),
                };
            default:
                throw new FormatException($"The audit change data holds an unexpected {e.ValueKind}.");
        }
    }

    private static EntityReference ReadLookup(JsonElement e)
    {
        var lookup = Required(e, Lookup);
        return new EntityReference(Required(lookup, "logicalName").GetString(), Required(lookup, "id").GetGuid())
        {
            Name = lookup.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
        };
    }

    private static List<RolePrivilege> ReadPrivileges(JsonElement e) =>
        e.EnumerateArray().Select(p => new RolePrivilege(Required(p, "depth").GetInt32(), Required(p, "id").GetGuid(),
            Required(p, "businessUnitId").GetGuid())).ToList();
}

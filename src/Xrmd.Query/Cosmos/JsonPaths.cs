using System.Text;
using System.Text.RegularExpressions;

namespace Xrmd.Query.Cosmos;

/// <summary>Builds SQL/JSON path strings (lax mode) from the steps of a parsed Cosmos path (plan #78).</summary>
public static partial class JsonPaths
{
    /// <summary>True for a string column with the JSON format.</summary>
    public static bool IsJson(Microsoft.Xrm.Sdk.Metadata.AttributeMetadata attribute) =>
        attribute is Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { FormatName.Value: { } f } && f == Microsoft.Xrm.Sdk.Metadata.StringFormatName.Json.Value;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    /// <summary>
    /// <c>$</c> followed by one segment per step: an identifier-shaped name as <c>.name</c>, any other name (dot, space, quote,
    /// <c>$</c>, non-ASCII, empty) as <c>."name"</c> with <c>"</c> and <c>\</c> escaped, an index as <c>[n]</c>. The result is
    /// only ever bound as a SQL parameter. <c>\b \f \n \r \t</c> are escaped; any other character below U+0020 is refused.
    /// </summary>
    public static string Format(IEnumerable<CosmosPathStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var sb = new StringBuilder("$");
        foreach (var step in steps)
        {
            switch (step)
            {
                case IndexStep i:
                    sb.Append('[').Append(i.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                    break;
                case PropertyStep { Name: var name } when Identifier().IsMatch(name):
                    sb.Append('.').Append(name);
                    break;
                case PropertyStep { Name: var name }:
                    sb.Append(".\"").Append(Escape(name)).Append('"');
                    break;
                default:
                    throw new ArgumentException($"Unknown path step {step.GetType().Name}.", nameof(steps));
            }
        }
        return sb.ToString();
    }

    private static string Escape(string name)
    {
        var sb = new StringBuilder(name.Length + 2);
        foreach (var ch in name)
        {
            switch (ch)
            {
                case '"' or '\\': sb.Append('\\').Append(ch); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                // The path parser has no \uXXXX escape and rejects other raw control characters with an SQL error.
                case < ' ':
                    throw new Xrmd.Abstractions.XrmdValidationException(
                        "A JSON property name with a control character is not supported in a JSON path.");
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}

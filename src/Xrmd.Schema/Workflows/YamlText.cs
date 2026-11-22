using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Xrmd.Schema;

/// <summary>Renders the canonical workflow YAML text from a node tree (see <see cref="WorkflowYaml.Write"/>).</summary>
internal static class YamlText
{
    internal static bool IsNullText(string value) => value is "" or "~" or "null" or "Null" or "NULL";

    // The YamlDotNet emitter writes flow mappings as {a: b} and cannot be made to write { a: b }, so the (small) canonical
    // form is rendered here from the node tree: block mappings and lists, flow mappings and lists on one line, scalars
    // plain unless that would change what they read back as.

    internal static bool IsBlock(YamlNode node) => node switch
    {
        YamlMappingNode { Style: not MappingStyle.Flow } map => map.Children.Count > 0,
        YamlSequenceNode { Style: not SequenceStyle.Flow } list => list.Children.Count > 0,
        _ => false,
    };

    internal static void Block(StringBuilder text, YamlNode node, int indent)
    {
        if (node is YamlMappingNode map)
        {
            BlockMapping(text, map, indent, firstInline: false);
            return;
        }
        foreach (var item in ((YamlSequenceNode)node).Children)
        {
            text.Append(' ', indent).Append("- ");
            if (item is YamlMappingNode itemMap && IsBlock(itemMap)) BlockMapping(text, itemMap, indent + 2, firstInline: true);
            else text.Append(FlowText(item)).Append('\n');
        }
    }

    internal static void BlockMapping(StringBuilder text, YamlMappingNode map, int indent, bool firstInline)
    {
        var first = true;
        foreach (var (key, value) in map.Children)
        {
            if (!(first && firstInline)) text.Append(' ', indent);
            first = false;
            text.Append(ScalarText((YamlScalarNode)key)).Append(':');
            if (IsBlock(value))
            {
                text.Append('\n');
                Block(text, value, indent + 2);
            }
            else text.Append(' ').Append(FlowText(value)).Append('\n');
        }
    }

    internal static string FlowText(YamlNode node) => node switch
    {
        YamlScalarNode scalar => ScalarText(scalar),
        YamlMappingNode { Children.Count: 0 } => "{}",
        YamlSequenceNode { Children.Count: 0 } => "[]",
        YamlMappingNode map => "{ " + string.Join(", ", map.Children.Select(e => $"{ScalarText((YamlScalarNode)e.Key)}: {FlowText(e.Value)}")) + " }",
        YamlSequenceNode list => "[" + string.Join(", ", list.Children.Select(FlowText)) + "]",
        _ => throw new ArgumentException($"Unknown node type {node.GetType().Name}."),
    };

    internal static string ScalarText(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style == ScalarStyle.Plain || (scalar.Style == ScalarStyle.Any && IsPlainSafe(value))) return value;
        if (value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029' or '\uFEFF') || HasLoneSurrogate(value) || value.Contains('\n'))
        {
            var quoted = new StringBuilder("\"");
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    quoted.Append(c).Append(value[++i]);
                    continue;
                }
                quoted.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\0' => "\\0",
                    _ when char.IsControl(c) || char.IsSurrogate(c) || c is '\u2028' or '\u2029' or '\uFEFF' => $"\\u{(int)c:X4}",
                    _ => c.ToString(),
                });
            }
            return quoted.Append('"').ToString();
        }
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Whether <paramref name="value"/> holds a surrogate that is not part of a valid pair (checked at every position).</summary>
    private static bool HasLoneSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            else if (char.IsSurrogate(value[i])) return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="value"/> can be written without quotes, in a block or a flow context, and read back as the same text.</summary>
    private static bool IsPlainSafe(string value)
    {
        if (value.Length == 0 || IsNullText(value) || value.Contains('\\')) return false;
        if (value[0] == ' ' || value[^1] == ' ' || value.StartsWith("---", StringComparison.Ordinal) || value.StartsWith("...", StringComparison.Ordinal)) return false;
        if ("#,[]{}&*!|>'\"%@`:".Contains(value[0])) return false;
        if (value[0] is '-' or '?' && (value.Length == 1 || value[1] is ' ' or ',' or '[' or ']' or '{' or '}')) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c) || c is '\u2028' or '\u2029' or '\uFEFF' or ',' or '[' or ']' or '{' or '}' || char.IsSurrogate(c)) return false;
            if (c == ':' && (i + 1 == value.Length || value[i + 1] is ' ' or ',' or '[' or ']' or '{' or '}')) return false;
            if (c == '#' && i > 0 && value[i - 1] == ' ') return false;
        }
        return true;
    }
}

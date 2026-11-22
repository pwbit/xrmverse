using System.Globalization;
using System.Text;
using Xrmd.Core.Errors;
using Xrmd.Metadata.Computed;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Xrmd.Solution;

/// <summary>
/// The formula files of the formula columns (Power Fx, plan 2.12c, ruling F3): one per table,
/// <c>Formulas/&lt;table&gt;-FormulaDefinitions.yaml</c>, which every formula column of the table names in its
/// <c>FormulaDefinitionFileName</c>. It is a YAML mapping with one line per column, <c>&lt;logical name&gt;: =&lt;formula&gt;</c>, in
/// the table's attribute order and with CRLF line ends (parity export <c>xrmCalcTest_1_0_0_4.zip</c>). A value may also be a quoted
/// or block scalar.
/// </summary>
public static class FormulaFiles
{
    /// <summary>The archive path (no leading <c>/</c>) of the formula file of <paramref name="entity"/>.</summary>
    public static string PathOf(string entity) => $"Formulas/{entity}-FormulaDefinitions.yaml";

    /// <summary>
    /// The formulas of the file <paramref name="path"/> (named in the refusals as given), by column (ignoring case), each without its
    /// leading <c>=</c> and surrounding whitespace as the server stores it (ruling F2). An empty file has none.
    /// </summary>
    /// <exception cref="XrmdFaultException">400: not valid YAML, more than one document, not a mapping, or a value that is not text.</exception>
    public static IReadOnlyDictionary<string, string> Read(string yaml, string path)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(path);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml.TrimStart('\uFEFF')));
        }
        catch (YamlException ex)
        {
            throw Invalid($"The formula file '{path}' is not valid YAML: {(ex.InnerException ?? ex).Message}");
        }
        var formulas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (stream.Documents.Count == 0) return formulas;
        if (stream.Documents.Count > 1) throw Invalid($"The formula file '{path}' holds more than one YAML document.");
        var root = stream.Documents[0].RootNode;
        if (root is YamlScalarNode { Value: null or "" }) return formulas;
        if (root is not YamlMappingNode mapping)
            throw Invalid($"The formula file '{path}' is not a mapping of column names to formulas.");
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { Length: > 0 } key })
                throw Invalid($"The formula file '{path}' is not a mapping of column names to formulas.");
            if (valueNode is not YamlScalarNode { Value: { } formula })
                throw Invalid($"The formula file '{path}' has no text formula for '{key}'.");
            if (!formulas.TryAdd(key, FormulaColumnRules.Normalize(formula)))
                throw Invalid($"The formula file '{path}' names the column '{key}' more than once (column names ignore case).");
        }
        return formulas;
    }

    /// <summary>True for the archive path of a formula file (<see cref="PathOf"/>, ignoring case and a leading <c>/</c>).</summary>
    public static bool IsFormulaFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var p = path.Replace('\\', '/').TrimStart('/');
        return p.StartsWith("Formulas/", StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', "Formulas/".Length) < 0
            && p.EndsWith("-FormulaDefinitions.yaml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="first"/> and <paramref name="second"/>, two formula files of one table (the layer payloads of its formula
    /// columns, each its own line), as one file: the lines of <paramref name="first"/>, then those of <paramref name="second"/> for the
    /// columns <paramref name="first"/> has not (the first wins, as among payloads).
    /// </summary>
    /// <exception cref="XrmdFaultException">400: either is not a formula file (<see cref="Read"/>).</exception>
    public static byte[] Merge(byte[] first, byte[] second, string path)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var encoding = new UTF8Encoding(false);
        var merged = new Dictionary<string, string>(Read(encoding.GetString(first), path), StringComparer.OrdinalIgnoreCase);
        var order = merged.Keys.ToList();
        foreach (var (column, formula) in Read(encoding.GetString(second), path))
            if (merged.TryAdd(column, formula)) order.Add(column);
        return encoding.GetBytes(Write(order.Select(c => (c, merged[c]))));
    }

    /// <summary>
    /// The formula from <paramref name="formulas"/> (read from <paramref name="fileName"/>, as the attribute names it) of the formula
    /// column <paramref name="entity"/>.<paramref name="column"/>.
    /// </summary>
    /// <exception cref="XrmdFaultException">400: the file has no formula for the column.</exception>
    public static string FormulaOf(IReadOnlyDictionary<string, string> formulas, string entity, string column, string fileName)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        return formulas.TryGetValue(column, out var formula)
            ? formula
            : throw Invalid($"The formula file '{fileName}' has no formula for the formula column '{entity}.{column}'.");
    }

    /// <summary>
    /// The text of a formula file: one line <c>&lt;column&gt;: =&lt;formula&gt;</c> per column of <paramref name="formulas"/>, in their
    /// order, each ended by CRLF (no byte order mark, as the parity export). A formula a plain YAML scalar cannot hold as it is
    /// (a line break, a <c>": "</c> or <c>" #"</c>, surrounding whitespace, a control character) is written double-quoted with escapes,
    /// so that <see cref="Read"/> gives it back.
    /// </summary>
    public static string Write(IEnumerable<(string Column, string Formula)> formulas)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        var text = new StringBuilder();
        foreach (var (column, formula) in formulas)
        {
            var value = "=" + formula;
            text.Append(column).Append(": ").Append(IsPlain(value) ? value : DoubleQuoted(value)).Append("\r\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// True when <paramref name="value"/> (which starts with <c>=</c>, so no indicator starts it) reads back unchanged as a plain
    /// scalar of a block mapping: one line, no <c>": "</c> or <c>" #"</c>, no trailing <c>:</c>, no surrounding whitespace, printable.
    /// </summary>
    private static bool IsPlain(string value) =>
        value.Trim() == value && !value.EndsWith(':')
        && !value.Contains(": ", StringComparison.Ordinal) && !value.Contains(" #", StringComparison.Ordinal)
        && !value.Contains(":\t", StringComparison.Ordinal) && !value.Contains("\t#", StringComparison.Ordinal)
        && value.All(c => !char.IsControl(c) && c != '\uFEFF' && !char.IsSurrogate(c) && c is not ('\u0085' or '\u2028' or '\u2029'));

    private static string DoubleQuoted(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) || c is '\uFEFF' or '\u0085' or '\u2028' or '\u2029'
                    => "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }
        return text.Append('"').ToString();
    }

    private static XrmdFaultException Invalid(string message) => XrmdFaultException.InvalidArgument(message);
}

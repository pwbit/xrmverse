using System.Globalization;
using System.Text.RegularExpressions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Xrmd.Schema;

/// <summary>Reads and writes the schema YAML (<see cref="SchemaDocument"/>).</summary>
/// <remarks>
/// <code>
/// publisher: xv                 # prefix: every name starts with "xv_"
/// solution: XrmverseFixture     # optional: SolutionUniqueName of the requests
/// choices:
///   - name: xv_priority
///     label: Priority
///     options: [{ value: 100000000, label: Low }, { value: 100000001, label: High }]
/// tables:
///   - name: xv_project
///     label: Project
///     pluralLabel: Projects
///     ownership: user           # user | organization
///     hasNotes: true            # optional, one-way: notes on the records
///     hasActivities: true       # optional, one-way: activities regarding the records
///     primaryName: { name: xv_name, label: Name, maxLength: 200 }
///     columns:
///       - { name: xv_budget, type: money, label: Budget }
///     manyToMany:
///       - { name: xv_project_xv_tag, with: xv_tag }
/// </code>
/// A calculated column (plan 2.12a) takes <c>calculated:</c>, the XAML of its definition (written as a literal block <c>|</c>); a
/// rollup column (plan 2.12b) takes <c>rollup:</c> the same way.
/// Tables and columns also take <c>schemaName</c> (default: the logical name) and <c>description</c>; choices and the
/// primary name take <c>description</c>. A custom activity is a table with <c>isActivity: true</c> whose primaryName is
/// <c>subject</c> (no prefix; SchemaName <c>Subject</c>). Every value is read as text (a label <c>true</c> is the text "true"). Nothing is
/// ignored: an unknown key, a missing required key, a name without the publisher prefix, a duplicate name, a type that is
/// not one of <see cref="SchemaColumnTypes.Names"/> or a parameter its type does not take is a
/// <see cref="SchemaFormatException"/> with its line and column.
/// </remarks>
public static partial class SchemaYaml
{
    private static readonly string[] DocumentKeys = ["publisher", "solution", "choices", "tables"];
    private static readonly string[] ChoiceKeys = ["name", "label", "description", "options"];
    private static readonly string[] OptionKeys = ["value", "label"];
    private static readonly string[] TableKeys = ["name", "schemaName", "label", "pluralLabel", "description", "ownership", "tableType", "isActivity", "hasNotes", "hasActivities", "primaryName", "columns", "manyToMany"];
    private static readonly string[] PrimaryNameKeys = ["name", "schemaName", "label", "description", "maxLength", "autoNumberFormat"];
    private static readonly string[] ColumnKeys = ["name", "schemaName", "type", "label", "description", "maxLength", "autoNumberFormat", "precision", "choice", "options", "target", "maxSizeInKB", "canStoreFullImage", "primary", "calculated", "rollup", "formula"];
    private static readonly string[] ManyToManyKeys = ["name", "with"];

    /// <summary>Reads a schema YAML document.</summary>
    /// <exception cref="SchemaFormatException">The document is not valid YAML or not a valid schema (line and column of the problem).</exception>
    public static SchemaDocument Read(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new SchemaFormatException(Inner(ex).Message, ex.Start.Line, ex.Start.Column, ex);
        }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
            throw new SchemaFormatException("The schema is empty.", 1, 1);
        if (stream.Documents.Count > 1)
            throw Error(stream.Documents[1].RootNode, "The schema must be a single YAML document.");
        return new DocumentReader().Read(stream.Documents[0].RootNode);
    }

    /// <summary>Writes <paramref name="document"/> in the format <see cref="Read"/> reads (labels quoted where needed).</summary>
    public static string Write(SchemaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = new YamlMappingNode { { "publisher", Scalar(document.Publisher) } };
        if (document.Solution is { } solution) root.Add("solution", Scalar(solution));
        if (document.Choices.Count > 0)
            root.Add("choices", new YamlSequenceNode(document.Choices.Select(c =>
            {
                var choice = new YamlMappingNode { { "name", Scalar(c.Name) } };
                AddOptional(choice, "label", c.Label);
                AddOptional(choice, "description", c.Description);
                choice.Add("options", Options(c.Options));
                return (YamlNode)choice;
            })));
        if (document.Tables.Count > 0)
            root.Add("tables", new YamlSequenceNode(document.Tables.Select(t => (YamlNode)Table(t))));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        // A byte order mark is not allowed inside a YAML document: every scalar holding one is double-quoted (Scalar, Column),
        // where the escape \uFEFF writes it.
        var text = writer.ToString().ReplaceLineEndings("\n").Replace("\uFEFF", "\\uFEFF", StringComparison.Ordinal);
        return text.EndsWith("...\n", StringComparison.Ordinal) ? text[..^4] : text;
    }

    private static YamlMappingNode Table(SchemaTable t)
    {
        var table = new YamlMappingNode { { "name", Scalar(t.Name) } };
        AddOptional(table, "schemaName", t.SchemaName);
        table.Add("label", Scalar(t.Label));
        AddOptional(table, "pluralLabel", t.PluralLabel);
        AddOptional(table, "description", t.Description);
        table.Add("ownership", Scalar(t.Ownership == SchemaOwnership.Organization ? "organization" : "user"));
        if (string.Equals(t.TableType, ElasticTables.TableTypeElastic, StringComparison.OrdinalIgnoreCase))
            table.Add("tableType", Scalar(ElasticTables.TableTypeElastic));
        if (t.IsActivity) AddOptional(table, "isActivity", true);
        AddOptional(table, "hasNotes", t.HasNotes);
        AddOptional(table, "hasActivities", t.HasActivities);
        var primary = new YamlMappingNode { { "name", Scalar(t.PrimaryName.Name) } };
        primary.Style = MappingStyle.Flow;
        AddOptional(primary, "schemaName", t.PrimaryName.SchemaName);
        AddOptional(primary, "label", t.PrimaryName.Label);
        AddOptional(primary, "description", t.PrimaryName.Description);
        AddOptional(primary, "maxLength", t.PrimaryName.MaxLength);
        AddOptional(primary, "autoNumberFormat", t.PrimaryName.AutoNumberFormat);
        table.Add("primaryName", primary);
        if (t.Columns.Count > 0) table.Add("columns", new YamlSequenceNode(t.Columns.Select(c => (YamlNode)Column(c))));
        if (t.ManyToMany.Count > 0)
            table.Add("manyToMany", new YamlSequenceNode(t.ManyToMany.Select(m =>
                (YamlNode)new YamlMappingNode { { "name", Scalar(m.Name) }, { "with", Scalar(m.With) } }.Flow())));
        return table;
    }

    private static YamlMappingNode Column(SchemaColumn c)
    {
        var column = new YamlMappingNode { { "name", Scalar(c.Name) } };
        AddOptional(column, "schemaName", c.SchemaName);
        column.Add("type", Scalar(c.Type));
        AddOptional(column, "label", c.Label);
        AddOptional(column, "description", c.Description);
        AddOptional(column, "maxLength", c.MaxLength);
        AddOptional(column, "autoNumberFormat", c.AutoNumberFormat);
        AddOptional(column, "precision", c.Precision);
        AddOptional(column, "choice", c.Choice);
        AddOptional(column, "target", c.Target);
        AddOptional(column, "maxSizeInKB", c.MaxSizeInKB);
        AddOptional(column, "canStoreFullImage", c.CanStoreFullImage);
        AddOptional(column, "primary", c.Primary);
        if (c.Options is { } options) column.Add("options", Options(options));
        // A formula column's Power Fx text (plan 2.12c): on one line, a scalar of the column's flow mapping; on several, a literal
        // block as the XAML below.
        if (c.Formula is { } fx && !fx.Contains('\n') && !fx.Contains('\r'))
        {
            column.Add("formula", Scalar(fx));
            return column.Flow();
        }
        if (c.Formula is not null)
        {
            var special = c.Formula.Contains('\r') || c.Formula.Contains('\uFEFF');
            column.Add("formula", new YamlScalarNode(c.Formula) { Style = special ? ScalarStyle.DoubleQuoted : ScalarStyle.Literal });
            return column;
        }
        var (key, formula) = c.Calculated is { } calculated ? ("calculated", calculated) : c.Rollup is { } rollup ? ("rollup", rollup) : (null, null);
        if (key is null || formula is null) return column.Flow();
        // The XAML as a literal block (a block mapping, the key last); text a literal block cannot hold exactly (a carriage
        // return, which a reader turns into a line feed; a byte order mark, not allowed inside a YAML document) is double-quoted
        // with escapes, so the text is read back as it was written.
        var quoted = formula.Contains('\r') || formula.Contains('\uFEFF');
        column.Add(key, new YamlScalarNode(formula) { Style = quoted ? ScalarStyle.DoubleQuoted : ScalarStyle.Literal });
        return column;
    }

    private static YamlSequenceNode Options(IEnumerable<SchemaOption> options) =>
        new(options.Select(o => (YamlNode)new YamlMappingNode
        {
            { "value", Scalar(o.Value.ToString(CultureInfo.InvariantCulture)) }, { "label", Scalar(o.Label) },
        }.Flow())) { Style = SequenceStyle.Flow };

    private static YamlMappingNode Flow(this YamlMappingNode node)
    {
        node.Style = MappingStyle.Flow;
        return node;
    }

    private static void AddOptional(YamlMappingNode node, string key, string? value)
    {
        if (value is not null) node.Add(key, Scalar(value));
    }

    private static void AddOptional(YamlMappingNode node, string key, int? value)
    {
        if (value is { } number) node.Add(key, Scalar(number.ToString(CultureInfo.InvariantCulture)));
    }

    private static void AddOptional(YamlMappingNode node, string key, bool? value)
    {
        if (value is { } flag) node.Add(key, new YamlScalarNode(flag ? "true" : "false") { Style = ScalarStyle.Plain });
    }

    /// <summary>
    /// A text scalar; one a plain scalar would read as no value (empty, <c>~</c>, <c>null</c>) is quoted, and so is one holding a
    /// byte order mark (escaped by <see cref="Write"/>).
    /// </summary>
    private static YamlScalarNode Scalar(string value) => new(value)
    {
        Style = IsNullText(value) || value.Contains('\uFEFF') ? ScalarStyle.DoubleQuoted : ScalarStyle.Any,
    };

    private static bool IsNullText(string value) => value is "" or "~" or "null" or "Null" or "NULL";

    private static Exception Inner(YamlException ex) => ex.InnerException is YamlException inner ? Inner(inner) : ex.InnerException ?? ex;

    private static SchemaFormatException Error(YamlNode node, string message) => new(message, node.Start.Line, node.Start.Column);

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LogicalNamePattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9]{1,7}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    /// <summary>Reads one document, checking every node (the prefix is known once <c>publisher</c> is read).</summary>
    private sealed class DocumentReader
    {
        private string _prefix = "";

        public SchemaDocument Read(YamlNode root)
        {
            var map = Mapping(root, "The schema");
            Keys(map, DocumentKeys, "the schema");
            var publisherNode = Required(map, "publisher", "The schema");
            var publisher = Text(publisherNode, "publisher")!;
            if (!PrefixPattern().IsMatch(publisher))
                throw Error(publisherNode, $"The publisher prefix '{publisher}' is not valid: 2 to 8 lowercase letters or digits, starting with a letter.");
            _prefix = publisher + "_";
            var document = new SchemaDocument { Publisher = publisher, Solution = OptionalText(map, "solution") };

            var choiceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Sequence(map, "choices"))
            {
                var choice = Choice(node);
                if (!choiceNames.Add(choice.Name)) throw Error(node, $"The choice '{choice.Name}' is defined more than once.");
                document.Choices.Add(choice);
            }

            var tableNames = new HashSet<string>(StringComparer.Ordinal);
            var relationshipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Sequence(map, "tables"))
            {
                var table = Table(node, relationshipNames);
                if (!tableNames.Add(table.Name)) throw Error(node, $"The table '{table.Name}' is defined more than once.");
                document.Tables.Add(table);
            }
            return document;
        }

        private SchemaChoice Choice(YamlNode node)
        {
            var map = Mapping(node, "A choice");
            var name = Name(map, "A choice");
            Keys(map, ChoiceKeys, $"choice '{name}'");
            var choice = new SchemaChoice
            {
                Name = name, Label = OptionalText(map, "label"), Description = OptionalText(map, "description"),
            };
            Required(map, "options", $"The choice '{name}'");
            choice.Options = OptionList(map, $"choice '{name}'");
            return choice;
        }

        private List<SchemaOption> OptionList(YamlMappingNode owner, string where)
        {
            var options = new List<SchemaOption>();
            foreach (var node in Sequence(owner, "options"))
            {
                var map = Mapping(node, $"An option of {where}");
                Keys(map, OptionKeys, $"an option of {where}");
                var value = Int(Required(map, "value", $"An option of {where}"), "value");
                var label = Text(Required(map, "label", $"The option {value} of {where}"), "label")!;
                if (options.Any(o => o.Value == value))
                    throw Error(node, $"The option value {value} appears more than once in {where}.");
                options.Add(new SchemaOption { Value = value, Label = label });
            }
            return options;
        }

        private SchemaTable Table(YamlNode node, HashSet<string> relationshipNames)
        {
            var map = Mapping(node, "A table");
            var name = Name(map, "A table");
            Keys(map, TableKeys, $"table '{name}'");
            var table = new SchemaTable
            {
                Name = name,
                SchemaName = SchemaName(map, name),
                Label = Text(Required(map, "label", $"The table '{name}'"), "label")!,
                PluralLabel = OptionalText(map, "pluralLabel"),
                Description = OptionalText(map, "description"),
            };
            if (map.Children.TryGetValue(new YamlScalarNode("ownership"), out var ownershipNode))
                table.Ownership = Text(ownershipNode, "ownership") switch
                {
                    "user" => SchemaOwnership.User,
                    "organization" => SchemaOwnership.Organization,
                    var other => throw Error(ownershipNode, $"The table '{name}' has the ownership '{other}', which is not supported (user or organization)."),
                };
            if (map.Children.TryGetValue(new YamlScalarNode("tableType"), out var typeNode))
            {
                var typeText = Text(typeNode, "tableType");
                try
                {
                    table.TableType = ElasticTables.NormalizeTableType(typeText);
                }
                catch (XrmdFaultException)
                {
                    throw Error(typeNode, $"The table '{name}' has the tableType '{typeText}', which is not supported (Standard or Elastic).");
                }
            }

            table.IsActivity = OptionalBool(map, "isActivity") ?? false;
            table.HasNotes = OptionalBool(map, "hasNotes");
            table.HasActivities = OptionalBool(map, "hasActivities");
            if (table.IsActivity)
            {
                // The rules of a custom activity (plan 2.11 A4), refused here rather than by the server half-way through an apply.
                if (table.HasActivities == true)
                    throw Error(map.Children[new YamlScalarNode("hasActivities")], $"The table '{name}' is an activity: an activity cannot have hasActivities.");
                if (table.HasNotes == false)
                    throw Error(map.Children[new YamlScalarNode("hasNotes")], $"The table '{name}' is an activity: an activity always has hasNotes true.");
                if (table.Ownership != SchemaOwnership.User)
                    throw Error(map.Children[new YamlScalarNode("ownership")], $"The table '{name}' is an activity: an activity has the ownership 'user'.");
            }

            var primaryMap = Mapping(Required(map, "primaryName", $"The table '{name}'"), $"The primaryName of table '{name}'");
            var primaryName = table.IsActivity ? ActivityPrimaryName(primaryMap, name) : Name(primaryMap, $"The primaryName of table '{name}'");
            Keys(primaryMap, PrimaryNameKeys, $"the primaryName of table '{name}'");
            table.PrimaryName = new SchemaPrimaryName
            {
                Name = primaryName,
                SchemaName = SchemaName(primaryMap, primaryName),
                Label = OptionalText(primaryMap, "label"),
                Description = OptionalText(primaryMap, "description"),
                MaxLength = OptionalInt(primaryMap, "maxLength"),
                AutoNumberFormat = OptionalText(primaryMap, "autoNumberFormat"),
            };
            if (table.PrimaryName.MaxLength is <= 0)
                throw Error(primaryMap, $"The primaryName of table '{name}': maxLength must be positive (it is {table.PrimaryName.MaxLength}).");

            var columnNames = new HashSet<string>(StringComparer.Ordinal) { primaryName };
            string? primaryImage = null;
            foreach (var columnNode in Sequence(map, "columns"))
            {
                var column = Column(columnNode, name);
                if (!columnNames.Add(column.Name))
                    throw Error(columnNode, $"The column '{name}.{column.Name}' is defined more than once.");
                if (column.Primary == true)
                {
                    if (primaryImage is not null)
                        throw Error(columnNode, $"Table '{name}' has more than one primary image ({primaryImage}, {column.Name}): a table has one primary image.");
                    primaryImage = column.Name;
                }
                table.Columns.Add(column);
            }

            foreach (var relationshipNode in Sequence(map, "manyToMany"))
            {
                var relationshipMap = Mapping(relationshipNode, $"A manyToMany of table '{name}'");
                var relationship = Name(relationshipMap, $"A manyToMany of table '{name}'");
                Keys(relationshipMap, ManyToManyKeys, $"manyToMany '{relationship}'");
                var withNode = Required(relationshipMap, "with", $"The manyToMany '{relationship}'");
                var with = Text(withNode, "with")!;
                if (!LogicalNamePattern().IsMatch(with)) throw Error(withNode, $"The manyToMany '{relationship}': '{with}' is not a valid table name.");
                if (!relationshipNames.Add(relationship))
                    throw Error(relationshipNode, $"The manyToMany '{relationship}' is defined more than once.");
                table.ManyToMany.Add(new SchemaManyToMany { Name = relationship, With = with });
            }
            return table;
        }

        private SchemaColumn Column(YamlNode node, string table)
        {
            var map = Mapping(node, $"A column of table '{table}'");
            var name = Name(map, $"A column of table '{table}'");
            Keys(map, ColumnKeys, $"column '{table}.{name}'");
            var column = new SchemaColumn
            {
                Name = name,
                SchemaName = SchemaName(map, name),
                Type = Text(Required(map, "type", $"The column '{table}.{name}'"), "type")!,
                Label = OptionalText(map, "label"),
                Description = OptionalText(map, "description"),
                MaxLength = OptionalInt(map, "maxLength"),
                AutoNumberFormat = OptionalText(map, "autoNumberFormat"),
                Precision = OptionalInt(map, "precision"),
                Choice = OptionalText(map, "choice"),
                Target = OptionalText(map, "target"),
                MaxSizeInKB = OptionalInt(map, "maxSizeInKB"),
                CanStoreFullImage = OptionalBool(map, "canStoreFullImage"),
                Primary = OptionalBool(map, "primary"),
                Calculated = OptionalText(map, "calculated"),
                Rollup = OptionalText(map, "rollup"),
                Formula = OptionalText(map, "formula"),
            };
            if (map.Children.ContainsKey(new YamlScalarNode("options"))) column.Options = OptionList(map, $"column '{table}.{name}'");
            if (SchemaColumnTypes.Validate(column) is { } problem) throw Error(node, problem.Replace($"Column '{name}'", $"Column '{table}.{name}'", StringComparison.Ordinal));
            if (column.Target is { } target && !LogicalNamePattern().IsMatch(target))
                throw Error(map.Children[new YamlScalarNode("target")], $"Column '{table}.{name}': '{target}' is not a valid table name.");
            return column;
        }

        /// <summary>The <c>name</c> of the primary column of an activity: <c>subject</c>, without the publisher prefix.</summary>
        private static string ActivityPrimaryName(YamlMappingNode map, string table)
        {
            var node = Required(map, "name", $"The primaryName of table '{table}'");
            var name = Text(node, "name")!;
            return name == SchemaTable.ActivityPrimaryName
                ? name
                : throw Error(node, $"The table '{table}' is an activity: its primaryName is '{SchemaTable.ActivityPrimaryName}', not '{name}'.");
        }

        /// <summary>The <c>name</c> of a component: a valid logical name with the publisher prefix.</summary>
        private string Name(YamlMappingNode map, string what)
        {
            var node = Required(map, "name", what);
            var name = Text(node, "name")!;
            if (!LogicalNamePattern().IsMatch(name))
                throw Error(node, $"The name '{name}' is not a valid logical name (lowercase letters, digits and '_', starting with a letter, at most 64).");
            if (!name.StartsWith(_prefix, StringComparison.Ordinal) || name.Length == _prefix.Length)
                throw Error(node, $"The name '{name}' does not start with the publisher prefix '{_prefix}'.");
            return name;
        }

        /// <summary>The optional <c>schemaName</c>: the logical name with other casing.</summary>
        private static string? SchemaName(YamlMappingNode map, string name)
        {
            if (!map.Children.TryGetValue(new YamlScalarNode("schemaName"), out var node)) return null;
            var schemaName = Text(node, "schemaName")!;
            if (!string.Equals(schemaName.ToLowerInvariant(), name, StringComparison.Ordinal))
                throw Error(node, $"The schemaName '{schemaName}' of '{name}' must be the logical name with other casing.");
            return schemaName;
        }

        private static YamlMappingNode Mapping(YamlNode node, string what) =>
            node as YamlMappingNode ?? throw Error(node, $"{what} must be a mapping (key: value).");

        private static void Keys(YamlMappingNode map, string[] allowed, string where)
        {
            foreach (var key in map.Children.Keys)
            {
                if (key is not YamlScalarNode { Value: { } text } || !allowed.Contains(text, StringComparer.Ordinal))
                    throw Error(key, $"Unknown key '{(key as YamlScalarNode)?.Value ?? key.ToString()}' in {where} (allowed: {string.Join(", ", allowed)}).");
            }
        }

        private static YamlNode Required(YamlMappingNode map, string key, string what) =>
            map.Children.TryGetValue(new YamlScalarNode(key), out var node) && !IsNull(node)
                ? node
                : throw Error(map, $"{what} needs the key '{key}'.");

        private static IEnumerable<YamlNode> Sequence(YamlMappingNode map, string key)
        {
            if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node) || IsNull(node)) return [];
            return node as YamlSequenceNode ?? throw Error(node, $"'{key}' must be a list.");
        }

        private static string? OptionalText(YamlMappingNode map, string key) =>
            map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? Text(node, key) : null;

        private static int? OptionalInt(YamlMappingNode map, string key) =>
            map.Children.TryGetValue(new YamlScalarNode(key), out var node) && !IsNull(node) ? Int(node, key) : null;

        /// <summary>The text of a scalar; null for a plain empty, <c>~</c> or <c>null</c>.</summary>
        private static string? Text(YamlNode node, string key)
        {
            if (node is not YamlScalarNode scalar) throw Error(node, $"'{key}' must be a single value.");
            return IsNull(scalar) ? null : scalar.Value ?? "";
        }

        private static bool? OptionalBool(YamlMappingNode map, string key) =>
            map.Children.TryGetValue(new YamlScalarNode(key), out var node) && !IsNull(node) ? Bool(node, key) : null;

        private static bool Bool(YamlNode node, string key)
        {
            var text = Text(node, key);
            return text switch
            {
                "true" => true,
                "false" => false,
                _ => throw Error(node, $"'{key}' must be true or false, not '{text}'."),
            };
        }

        private static int Int(YamlNode node, string key)
        {
            var text = Text(node, key);
            return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw Error(node, $"'{key}' must be an integer, not '{text}'.");
        }

        private static bool IsNull(YamlNode node) =>
            node is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any } scalar && (scalar.Value is null || IsNullText(scalar.Value));
    }
}

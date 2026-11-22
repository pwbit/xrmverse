using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Schema;

/// <summary>
/// The column types of the schema YAML and their SDK attributes, in both directions: the only mapping between a type name
/// (with its parameters) and an <see cref="AttributeMetadata"/> (the diff, the exporter and the CLI's <c>table column add</c>
/// all use it).
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><c>string</c> (<c>maxLength</c>)</term><description><see cref="StringAttributeMetadata"/></description></item>
/// <item><term><c>memo</c> (<c>maxLength</c>)</term><description><see cref="MemoAttributeMetadata"/></description></item>
/// <item><term><c>int</c></term><description><see cref="IntegerAttributeMetadata"/></description></item>
/// <item><term><c>decimal</c> (<c>precision</c>)</term><description><see cref="DecimalAttributeMetadata"/></description></item>
/// <item><term><c>money</c></term><description><see cref="MoneyAttributeMetadata"/></description></item>
/// <item><term><c>bool</c></term><description><see cref="BooleanAttributeMetadata"/> (options Yes = 1, No = 0)</description></item>
/// <item><term><c>datetime</c></term><description><see cref="DateTimeAttributeMetadata"/></description></item>
/// <item><term><c>choice</c> (<c>choice</c> | <c>options</c>)</term><description><see cref="PicklistAttributeMetadata"/></description></item>
/// <item><term><c>choices</c> (<c>choice</c> | <c>options</c>)</term><description><see cref="MultiSelectPicklistAttributeMetadata"/></description></item>
/// <item><term><c>lookup</c> (<c>target</c>)</term><description><see cref="LookupAttributeMetadata"/>, created with CreateOneToMany</description></item>
/// <item><term><c>customer</c></term><description><see cref="LookupAttributeMetadata"/> of type Customer (an <c>account</c> or a <c>contact</c>), created with CreateCustomerRelationships; it takes no <c>target</c></description></item>
/// <item><term><c>file</c> (<c>maxSizeInKB</c>)</term><description><see cref="FileAttributeMetadata"/></description></item>
/// <item><term><c>image</c> (<c>maxSizeInKB</c>, <c>canStoreFullImage</c>, <c>primary</c>)</term><description><see cref="ImageAttributeMetadata"/></description></item>
/// </list>
/// A column of the types <see cref="CalculatedTypes"/> may be calculated (<c>calculated:</c>, plan 2.12a): its attribute has
/// <c>SourceType</c> 1 and the XAML as its <c>FormulaDefinition</c>. A column of the types <see cref="RollupTypes"/> may be a rollup
/// (<c>rollup:</c>, plan 2.12b): <c>SourceType</c> 2 and the XAML as its <c>FormulaDefinition</c>. A column of the types
/// <see cref="FormulaTypes"/> may be a formula column (<c>formula:</c>, plan 2.12c): <c>SourceType</c> 3 and the Power Fx text as its
/// <c>FormulaDefinition</c>.
/// The attributes carry only what the column states (names, labels, the type's parameters): every other setting is the
/// server's default, which is what the server's metadata rules accept on a create.
/// </remarks>
public static class SchemaColumnTypes
{
    /// <summary>The language of the labels written and compared (the one the server seeds).</summary>
    public const int LanguageCode = 1033;

    public const string String = "string";
    public const string Memo = "memo";
    public const string Int = "int";
    public const string Decimal = "decimal";
    public const string Money = "money";
    public const string Bool = "bool";
    public const string DateTime = "datetime";
    public const string Choice = "choice";
    public const string Choices = "choices";
    public const string Lookup = "lookup";
    public const string Customer = "customer";
    public const string File = "file";
    public const string Image = "image";

    /// <summary>Every type name, in the order the documentation lists them.</summary>
    public static IReadOnlyList<string> Names { get; } = [String, Memo, Int, Decimal, Money, Bool, DateTime, Choice, Choices, Lookup, Customer, File, Image];

    /// <summary>The types a calculated column (<c>calculated:</c>, plan 2.12a) can have, in the order of <see cref="Names"/>.</summary>
    public static IReadOnlyList<string> CalculatedTypes { get; } = [String, Int, Decimal, Money, Bool, DateTime, Choice];

    /// <summary>The types a rollup column (<c>rollup:</c>, plan 2.12b) can have, in the order of <see cref="Names"/>.</summary>
    public static IReadOnlyList<string> RollupTypes { get; } = [Int, Decimal, Money, DateTime];

    /// <summary>
    /// The types a formula column (<c>formula:</c>, plan 2.12c) can have, in the order of <see cref="Names"/>: the server's String,
    /// Integer, Decimal, Boolean and DateTime (its Double has no type here; Money and Choice formulas are refused).
    /// </summary>
    public static IReadOnlyList<string> FormulaTypes { get; } = [String, Int, Decimal, Bool, DateTime];

    /// <summary>True for a type of <see cref="Names"/> (exact, lowercase).</summary>
    public static bool IsKnown(string? type) => type is not null && Names.Contains(type, StringComparer.Ordinal);

    /// <summary>
    /// Why <paramref name="column"/> is not a valid column (an unknown type, a parameter its type does not take or a missing
    /// one); null when it is valid.
    /// </summary>
    public static string? Validate(SchemaColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var type = column.Type;
        if (!IsKnown(type))
            return $"Column '{column.Name}' has the type '{type}', which is not supported (supported: {string.Join(", ", Names)}).";
        if (column.Calculated is { } formula)
        {
            if (!CalculatedTypes.Contains(type, StringComparer.Ordinal))
                return $"Column '{column.Name}': calculated applies only to the types {string.Join(", ", CalculatedTypes.SkipLast(1))} and {CalculatedTypes[^1]}, not to '{type}'.";
            if (string.IsNullOrWhiteSpace(formula)) return $"Column '{column.Name}': calculated cannot be empty.";
            if (column.AutoNumberFormat is not null) return $"Column '{column.Name}': a calculated column has no autoNumberFormat.";
        }
        if (column.Formula is { } fx)
        {
            if (column.Calculated is not null || column.Rollup is not null)
                return $"Column '{column.Name}': a column is either calculated, rollup or formula, not more than one.";
            if (!FormulaTypes.Contains(type, StringComparer.Ordinal))
                return $"Column '{column.Name}': formula applies only to the types {string.Join(", ", FormulaTypes.SkipLast(1))} and {FormulaTypes[^1]}, not to '{type}'.";
            if (string.IsNullOrWhiteSpace(fx)) return $"Column '{column.Name}': formula cannot be empty.";
            if (column.AutoNumberFormat is not null) return $"Column '{column.Name}': a formula column has no autoNumberFormat.";
        }
        if (column.Rollup is { } rollup)
        {
            if (column.Calculated is not null) return $"Column '{column.Name}': a column is either calculated or rollup, not both.";
            if (!RollupTypes.Contains(type, StringComparer.Ordinal))
                return $"Column '{column.Name}': rollup applies only to the types {string.Join(", ", RollupTypes.SkipLast(1))} and {RollupTypes[^1]}, not to '{type}'.";
            if (string.IsNullOrWhiteSpace(rollup)) return $"Column '{column.Name}': rollup cannot be empty.";
        }
        if (column.MaxLength is not null && type is not (String or Memo))
            return $"Column '{column.Name}': maxLength applies only to the types {String} and {Memo}, not to '{type}'.";
        if (column.MaxLength is <= 0)
            return $"Column '{column.Name}': maxLength must be positive (it is {column.MaxLength}).";
        if (column.AutoNumberFormat is not null && type != String)
            return $"Column '{column.Name}': autoNumberFormat applies only to the type {String}, not to '{type}'.";
        if (column.Precision is not null && type != Decimal)
            return $"Column '{column.Name}': precision applies only to the type {Decimal}, not to '{type}'.";
        var enumType = type is Choice or Choices;
        if ((column.Choice is not null || column.Options is not null) && !enumType)
            return $"Column '{column.Name}': choice and options apply only to the types {Choice} and {Choices}, not to '{type}'.";
        if (enumType && (column.Choice is null) == (column.Options is null))
            return $"Column '{column.Name}' of type '{type}' needs either choice (a global choice) or options (its own), not both.";
        if (column.Options is { } options && options.GroupBy(o => o.Value).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            return $"Column '{column.Name}': the option value {duplicate.Key} appears more than once.";
        if (column.Target is not null && type != Lookup)
            return type == Customer
                ? $"Column '{column.Name}': a {Customer} column has no target (it always refers to {CustomerColumns.Account} and {CustomerColumns.Contact})."
                : $"Column '{column.Name}': target applies only to the type {Lookup}, not to '{type}'.";
        if (type == Lookup && string.IsNullOrEmpty(column.Target))
            return $"Column '{column.Name}' of type '{Lookup}' needs a target.";
        if (column.MaxSizeInKB is not null && type is not (File or Image))
            return $"Column '{column.Name}': maxSizeInKB applies only to the types {File} and {Image}, not to '{type}'.";
        var maxSize = type == File ? FileColumns.MaxFileSizeInKB : FileColumns.MaxImageSizeInKB;
        if (column.MaxSizeInKB is { } size && (size < FileColumns.MinSizeInKB || size > maxSize))
            return $"Column '{column.Name}': maxSizeInKB of a {type} column is 1 to {maxSize} (it is {size}).";
        if (column.CanStoreFullImage is not null && type != Image)
            return $"Column '{column.Name}': canStoreFullImage applies only to the type {Image}, not to '{type}'.";
        if (column.Primary is not null && type != Image)
            return $"Column '{column.Name}': primary applies only to the type {Image}, not to '{type}'.";
        if (column.Primary == false)
            return $"Column '{column.Name}': primary can only be true (the primary image moves when another image column takes it; it is never dropped).";
        return null;
    }

    /// <summary>
    /// The attribute to create for <paramref name="column"/>: logical name, SchemaName (the logical name unless given),
    /// labels and the type's parameters. A lookup's has <c>Targets</c> = [target] (CreateOneToMany takes it as its Lookup).
    /// </summary>
    /// <exception cref="ArgumentException">The column is not valid (<see cref="Validate"/>).</exception>
    public static AttributeMetadata ToAttribute(SchemaColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (Validate(column) is { } problem) throw new ArgumentException(problem, nameof(column));
        AttributeMetadata attribute = column.Type switch
        {
            String => new StringAttributeMetadata { MaxLength = column.MaxLength, AutoNumberFormat = column.AutoNumberFormat },
            Memo => new MemoAttributeMetadata { MaxLength = column.MaxLength },
            Int => new IntegerAttributeMetadata(),
            Decimal => new DecimalAttributeMetadata { Precision = column.Precision },
            Money => new MoneyAttributeMetadata(),
            Bool => new BooleanAttributeMetadata
            {
                OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(Label("Yes"), 1), new OptionMetadata(Label("No"), 0)),
            },
            DateTime => new DateTimeAttributeMetadata(),
            Choice => new PicklistAttributeMetadata { OptionSet = OptionSet(column) },
            Choices => new MultiSelectPicklistAttributeMetadata { OptionSet = OptionSet(column) },
            Lookup => new LookupAttributeMetadata { Targets = [column.Target!] },
            // The server states the Customer type and both targets itself (CreateCustomerRelationships), as Dataverse does.
            Customer => new LookupAttributeMetadata(),
            File => new FileAttributeMetadata { MaxSizeInKB = column.MaxSizeInKB },
            Image => new ImageAttributeMetadata
            {
                MaxSizeInKB = column.MaxSizeInKB, CanStoreFullImage = column.CanStoreFullImage, IsPrimaryImage = column.Primary,
            },
            _ => throw new ArgumentException($"Unknown column type '{column.Type}'.", nameof(column)),
        };
        attribute.LogicalName = column.Name;
        attribute.SchemaName = column.SchemaName ?? column.Name;
        if (column.Calculated is { } formula)
        {
            // A calculated column (plan 2.12a, ruling K3): the server checks the definition and makes the column read-only.
            attribute.SourceType = ComputedColumnRules.Calculated;
            ComputedColumnRules.SetFormula(attribute, formula);
        }
        if (column.Rollup is { } rollup)
        {
            // A rollup column (plan 2.12b, ruling R15): the server checks the definition, makes the column read-only and adds its
            // companions.
            attribute.SourceType = RollupColumns.Rollup;
            ComputedColumnRules.SetFormula(attribute, rollup);
        }
        if (column.Formula is { } fx)
        {
            // A formula column (plan 2.12c): the server checks the formula and makes the column read-only. The schema file may write
            // the formula with a leading '=' (as a solution's formula file does); the request has none, which the server refuses.
            attribute.SourceType = ComputedColumnRules.Formula;
            ComputedColumnRules.SetFormula(attribute, FormulaColumnRules.Normalize(fx));
        }
        if (column.Label is { } label) attribute.DisplayName = Label(label);
        if (column.Description is { } description) attribute.Description = Label(description);
        return attribute;
    }

    /// <summary>The primary name column of <paramref name="table"/>, with its SchemaName (<see cref="SchemaTable.PrimarySchemaName"/>).</summary>
    public static StringAttributeMetadata ToPrimaryAttribute(SchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var attribute = ToAttribute(table.PrimaryName);
        attribute.SchemaName = table.PrimarySchemaName;
        return attribute;
    }

    /// <summary>The primary name attribute of a table (a string).</summary>
    public static StringAttributeMetadata ToAttribute(SchemaPrimaryName primaryName)
    {
        ArgumentNullException.ThrowIfNull(primaryName);
        var attribute = new StringAttributeMetadata
        {
            LogicalName = primaryName.Name,
            SchemaName = primaryName.SchemaName ?? primaryName.Name,
            MaxLength = primaryName.MaxLength,
            AutoNumberFormat = primaryName.AutoNumberFormat,
        };
        if (primaryName.Label is { } label) attribute.DisplayName = Label(label);
        if (primaryName.Description is { } description) attribute.Description = Label(description);
        return attribute;
    }

    /// <summary>
    /// True for an attribute of <paramref name="entity"/> the server generates with another, which a schema never describes nor
    /// diffs: a money's <c>_base</c>, an attribute of another (<c>AttributeOf</c>) and the currency pair a money column brings
    /// (<c>transactioncurrencyid</c>, <c>exchangerate</c>; plan 2.10, X4).
    /// </summary>
    public static bool IsGenerated(EntityMetadata entity, AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(attribute);
        return attribute is MoneyAttributeMetadata { IsBaseCurrency: true } || attribute.AttributeOf is not null
               || CurrencySchema.IsPairAttribute(entity, attribute);
    }

    /// <summary>The type name of an existing attribute; null when no type of <see cref="Names"/> represents it.</summary>
    public static string? TypeNameOf(AttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return attribute switch
        {
            StringAttributeMetadata => String,
            MemoAttributeMetadata => Memo,
            IntegerAttributeMetadata => Int,
            DecimalAttributeMetadata => Decimal,
            MoneyAttributeMetadata { IsBaseCurrency: true } => null,
            MoneyAttributeMetadata => Money,
            BooleanAttributeMetadata => Bool,
            DateTimeAttributeMetadata => DateTime,
            MultiSelectPicklistAttributeMetadata => Choices,
            PicklistAttributeMetadata => Choice,
            LookupAttributeMetadata { AttributeType: AttributeTypeCode.Customer } => Customer,
            LookupAttributeMetadata { AttributeType: null or AttributeTypeCode.Lookup, Targets: [_] } => Lookup,
            FileAttributeMetadata => File,
            ImageAttributeMetadata => Image,
            _ => null,
        };
    }

    /// <summary>
    /// The column <paramref name="attribute"/> is (type, parameters, labels, its options or global choice, its target);
    /// null when <see cref="TypeNameOf"/> has no type for it.
    /// </summary>
    public static SchemaColumn? ToColumn(AttributeMetadata attribute)
    {
        if (TypeNameOf(attribute) is not { } type) return null;
        var column = new SchemaColumn
        {
            Name = attribute.LogicalName,
            SchemaName = attribute.SchemaName is { } schemaName && schemaName != attribute.LogicalName ? schemaName : null,
            Type = type,
            Label = Text(attribute.DisplayName),
            Description = Text(attribute.Description),
            Calculated = attribute.SourceType == ComputedColumnRules.Calculated ? ComputedColumnRules.FormulaOf(attribute) : null,
            Rollup = RollupColumns.IsRollup(attribute) ? ComputedColumnRules.FormulaOf(attribute) : null,
            Formula = attribute.SourceType == ComputedColumnRules.Formula ? ComputedColumnRules.FormulaOf(attribute) : null,
        };
        switch (attribute)
        {
            case StringAttributeMetadata s:
                column.MaxLength = s.MaxLength;
                column.AutoNumberFormat = string.IsNullOrEmpty(s.AutoNumberFormat) ? null : s.AutoNumberFormat;
                break;
            case MemoAttributeMetadata m: column.MaxLength = m.MaxLength; break;
            case DecimalAttributeMetadata d: column.Precision = d.Precision; break;
            case LookupAttributeMetadata { AttributeType: AttributeTypeCode.Customer }: break; // no target: always account and contact
            case LookupAttributeMetadata l: column.Target = l.Targets?.FirstOrDefault(); break;
            case FileAttributeMetadata f: column.MaxSizeInKB = f.MaxSizeInKB; break;
            case ImageAttributeMetadata i:
                column.MaxSizeInKB = i.MaxSizeInKB;
                column.CanStoreFullImage = i.CanStoreFullImage == true ? true : null;
                column.Primary = i.IsPrimaryImage == true ? true : null;
                break;
            case EnumAttributeMetadata e:
                if (e.OptionSet is { IsGlobal: true, Name: { Length: > 0 } global }) column.Choice = global;
                else column.Options = Options(e.OptionSet);
                break;
        }
        return column;
    }

    /// <summary>The options of an option set, by value.</summary>
    internal static List<SchemaOption> Options(OptionSetMetadata? optionSet) =>
        (optionSet?.Options ?? [])
        .Where(o => o.Value is not null)
        .OrderBy(o => o.Value)
        .Select(o => new SchemaOption { Value = o.Value!.Value, Label = Text(o.Label) ?? "" })
        .ToList();

    /// <summary>A label in <see cref="LanguageCode"/>, with <c>UserLocalizedLabel</c> set.</summary>
    public static Label Label(string text) =>
        new(new LocalizedLabel(text, LanguageCode), new[] { new LocalizedLabel(text, LanguageCode) });

    /// <summary>The <see cref="LanguageCode"/> text of a label; null when it has none.</summary>
    public static string? Text(Label? label) =>
        label?.LocalizedLabels?.FirstOrDefault(l => l.LanguageCode == LanguageCode)?.Label
        ?? (label?.UserLocalizedLabel is { LanguageCode: LanguageCode } user ? user.Label : null);

    private static OptionSetMetadata OptionSet(SchemaColumn column) => column.Choice is { } global
        ? new OptionSetMetadata { IsGlobal = true, Name = global, OptionSetType = OptionSetType.Picklist }
        : new OptionSetMetadata(new OptionMetadataCollection(column.Options!.Select(o => new OptionMetadata(Label(o.Label), o.Value)).ToList()))
        {
            IsGlobal = false, OptionSetType = OptionSetType.Picklist,
        };
}

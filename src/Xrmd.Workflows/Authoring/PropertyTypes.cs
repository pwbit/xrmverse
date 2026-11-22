using System.Globalization;
using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Workflows.Authoring;

/// <summary>
/// The type of a destination or value as the designer writes it (plan 2.21, A11): the <c>WorkflowPropertyType</c> member of a
/// <c>CreateCrmType</c>, the crm type name the designer adds after the value (<see cref="Label"/>; none for some types), the XAML
/// type of <c>TargetType</c> and the CLR type the 2.14 parser resolves it to.
/// </summary>
/// <param name="Name">The name used in messages.</param>
/// <param name="Member">The <c>WorkflowPropertyType</c> member, null when no literal of the type exists (party list).</param>
/// <param name="Label">The crm type name after the value (<c>"String"</c>, <c>"Picklist"</c>, <c>"Lookup"</c>, <c>"Key"</c>), or null.</param>
/// <param name="Xaml">The <c>TargetType</c> (<c>x:String</c>, <c>mxs:OptionSetValue</c>, ...).</param>
/// <param name="Clr">The full CLR type name.</param>
public sealed record PropertyType(string Name, string? Member, string? Label, string Xaml, string Clr)
{
    // Confirmed by the export (A11): String, Picklist, Decimal, Boolean, Guid, Lookup, party list. The others are assumed.
    public static readonly PropertyType String = new("String", "String", "String", "x:String", "System.String");
    public static readonly PropertyType OptionSet = new("OptionSetValue", "OptionSetValue", "Picklist", "mxs:OptionSetValue", "Microsoft.Xrm.Sdk.OptionSetValue");
    public static readonly PropertyType Boolean = new("Boolean", "Boolean", null, "x:Boolean", "System.Boolean");
    public static readonly PropertyType Decimal = new("Decimal", "Decimal", null, "x:Decimal", "System.Decimal");
    public static readonly PropertyType Integer = new("Integer", "Integer", null, "x:Int32", "System.Int32");
    public static readonly PropertyType Double = new("Double", "Double", null, "x:Double", "System.Double");
    public static readonly PropertyType Money = new("Money", "Money", null, "mxs:Money", "Microsoft.Xrm.Sdk.Money");
    public static readonly PropertyType DateTime = new("DateTime", "DateTime", null, "s:DateTime", "System.DateTime");
    public static readonly PropertyType EntityReference = new("EntityReference", "EntityReference", "Lookup", "mxs:EntityReference", "Microsoft.Xrm.Sdk.EntityReference");
    public static readonly PropertyType EntityCollection = new("party list", null, null, "mxs:EntityCollection", "Microsoft.Xrm.Sdk.EntityCollection");
    public static readonly PropertyType Guid = new("Guid", "Guid", "Key", "s:Guid", "System.Guid");

    /// <summary>A value whose type xrmverse cannot know at compile time (an output of a perform action or custom activity step).</summary>
    public static readonly PropertyType Object = new("value of unknown type", null, null, "x:Object", "System.Object");

    public bool IsNumber => this == Integer || this == Decimal || this == Double || this == Money;

    public bool IsText => this == String;

    public override string ToString() => Name;
}

/// <summary>The A11 table: the <see cref="PropertyType"/> of a column, and the literal text each type accepts.</summary>
public static class PropertyTypes
{
    /// <summary>The type of a column, or null when the designer has no value of that column type (multi-select, file, ...).</summary>
    public static PropertyType? Of(AttributeMetadata column) => column.AttributeType switch
    {
        AttributeTypeCode.String or AttributeTypeCode.Memo => PropertyType.String,
        AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status => PropertyType.OptionSet,
        AttributeTypeCode.Boolean => PropertyType.Boolean,
        AttributeTypeCode.Decimal => PropertyType.Decimal,
        AttributeTypeCode.Integer => PropertyType.Integer,
        AttributeTypeCode.Double => PropertyType.Double,
        AttributeTypeCode.Money => PropertyType.Money,
        AttributeTypeCode.DateTime => PropertyType.DateTime,
        AttributeTypeCode.Lookup or AttributeTypeCode.Owner or AttributeTypeCode.Customer => PropertyType.EntityReference,
        AttributeTypeCode.PartyList => PropertyType.EntityCollection,
        AttributeTypeCode.Uniqueidentifier => PropertyType.Guid,
        _ => null,
    };

    private static readonly PropertyType[] All =
    [
        PropertyType.String, PropertyType.OptionSet, PropertyType.Boolean, PropertyType.Decimal, PropertyType.Integer, PropertyType.Double,
        PropertyType.Money, PropertyType.DateTime, PropertyType.EntityReference, PropertyType.EntityCollection, PropertyType.Guid,
    ];

    /// <summary>The type of a CLR type name (an argument of a message or custom activity, ruling R6), or null when no designer type is it.</summary>
    public static PropertyType? OfClr(string clrType) => All.FirstOrDefault(t => t.Clr == clrType);

    /// <summary>The type of an action argument's YAML <c>type</c> (A15), or null for another text.</summary>
    public static PropertyType? OfArgument(string type) => type switch
    {
        "boolean" => PropertyType.Boolean,
        "dateTime" => PropertyType.DateTime,
        "decimal" => PropertyType.Decimal,
        "double" => PropertyType.Double,
        "entityReference" => PropertyType.EntityReference,
        "integer" => PropertyType.Integer,
        "money" => PropertyType.Money,
        "picklist" => PropertyType.OptionSet,
        "string" => PropertyType.String,
        _ => null,
    };

    /// <summary>The option values of a choice, state or status column (empty for any other column).</summary>
    public static IReadOnlyList<int> Options(AttributeMetadata column)
    {
        var options = column switch
        {
            EnumAttributeMetadata { OptionSet.Options: { } list } => list,
            _ => null,
        };
        return options?.Where(o => o.Value is not null).Select(o => o.Value!.Value).ToList() ?? [];
    }

    /// <summary>
    /// The text of a literal of <paramref name="type"/> as <c>CreateCrmType</c> takes it (invariant culture; Boolean <c>True</c>/
    /// <c>False</c>; DateTime ISO 8601, assumed), or null when <paramref name="text"/> is not one. The text is kept as written
    /// once it parses as the 2.14 parser reads it, so the export gives the same text back.
    /// </summary>
    public static string? LiteralText(PropertyType type, string text)
    {
        var inv = CultureInfo.InvariantCulture;
        if (type == PropertyType.String)
            return text;
        if (type == PropertyType.Boolean)
            return text.Equals("true", StringComparison.OrdinalIgnoreCase) ? "True" : text.Equals("false", StringComparison.OrdinalIgnoreCase) ? "False" : null;
        if (type == PropertyType.OptionSet || type == PropertyType.Integer)
            return int.TryParse(text, NumberStyles.Integer, inv, out _) ? text : null;
        if (type == PropertyType.Decimal || type == PropertyType.Money)
            return decimal.TryParse(text, NumberStyles.Number, inv, out _) ? text : null;
        if (type == PropertyType.Double)
            return double.TryParse(text, NumberStyles.Float, inv, out _) ? text : null;
        if (type == PropertyType.DateTime)
            return System.DateTime.TryParse(text, inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _) ? text : null;
        if (type == PropertyType.Guid)
            return System.Guid.TryParse(text, out _) ? text : null;
        return null;
    }
}

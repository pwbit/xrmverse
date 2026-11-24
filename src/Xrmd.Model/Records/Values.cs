namespace Xrmd.Model.Records;

/// <summary>Our counterpart of the SDK's public <c>OptionSetValue</c>.</summary>
public sealed record ChoiceValue(int Value);

/// <summary>Our counterpart of the SDK's public <c>OptionSetValueCollection</c>.</summary>
public sealed class ChoiceValues : List<ChoiceValue>;

/// <summary>Our counterpart of the SDK's public <c>Money</c>.</summary>
public sealed record MoneyValue(decimal Value);

/// <summary>Our counterpart of the SDK's public <c>AliasedValue</c>.</summary>
public sealed record AliasedColumnValue(string? EntityLogicalName, string? AttributeLogicalName, object? Value);

/// <summary>Our counterpart of the SDK's public <c>ColumnSet</c>.</summary>
public sealed class ColumnSelection
{
    public bool AllColumns { get; set; }

    public IList<string> Columns { get; set; } = new List<string>();
}

/// <summary>Our counterpart of the SDK's public <c>Relationship</c> name; value equality so it can key a dictionary.</summary>
public sealed record RelationshipName(string SchemaName, RecordRole? PrimaryEntityRole);

// Member names and numeric values are the SDK's public ones (parity tested against the SDK).

/// <summary>Our counterpart of the SDK's public <c>EntityRole</c>.</summary>
public enum RecordRole { Referencing = 0, Referenced = 1 }

/// <summary>Our counterpart of the SDK's public <c>EntityState</c>.</summary>
public enum RecordState { Unchanged = 0, Created = 1, Changed = 2 }

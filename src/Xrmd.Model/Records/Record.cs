namespace Xrmd.Model.Records;

/// <summary>
/// Ordinal, case-sensitive attribute bag. Like the SDK's attribute collection, enumeration follows insertion order
/// as long as nothing is removed.
/// </summary>
public class RecordAttributes : Dictionary<string, object?>
{
    public RecordAttributes() : base(StringComparer.Ordinal) { }
}

/// <summary>Our counterpart of the SDK's public <c>Entity</c>; same observable behaviour where noted.</summary>
public sealed class Record
{
    public Record() { }

    public Record(string logicalName) => LogicalName = logicalName;

    public Record(string logicalName, Guid id)
    {
        LogicalName = logicalName;
        Id = id;
    }

    public string LogicalName { get; set; } = "";

    public Guid Id { get; set; }

    public RecordAttributes Attributes { get; set; } = new();

    public IDictionary<string, string> FormattedValues { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IDictionary<string, object?> KeyAttributes { get; set; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public string? RowVersion { get; set; }

    public RecordState? State { get; set; }

    public IDictionary<RelationshipName, RecordCollection> RelatedRecords { get; set; } = new Dictionary<RelationshipName, RecordCollection>();

    /// <summary>Like the SDK's <c>Entity[string]</c>: the getter throws <see cref="KeyNotFoundException"/> when absent.</summary>
    public object? this[string attributeName]
    {
        get => Attributes[attributeName];
        set => Attributes[attributeName] = value;
    }

    public bool Contains(string attributeName) => Attributes.ContainsKey(attributeName);

    /// <summary>
    /// Like the SDK's <c>GetAttributeValue&lt;T&gt;</c>: default(T) when the attribute is absent or null; a stored value
    /// that is not a <typeparamref name="T"/> throws <see cref="InvalidCastException"/> (no conversion, so int is not long).
    /// </summary>
    public T? GetValue<T>(string attributeName)
    {
        if (!Attributes.TryGetValue(attributeName, out var value) || value is null) return default;
        return (T)value;
    }

    /// <summary>
    /// Like the SDK's <c>Entity.ToEntityReference()</c>: copies LogicalName, Id and RowVersion only; KeyAttributes and
    /// Name are not copied.
    /// </summary>
    public RecordReference ToReference() => new()
    {
        LogicalName = LogicalName,
        Id = Id,
        RowVersion = RowVersion,
    };
}

/// <summary>Our counterpart of the SDK's public <c>EntityReference</c>.</summary>
public sealed class RecordReference
{
    public string LogicalName { get; set; } = "";

    public Guid Id { get; set; }

    public string? Name { get; set; }

    public IDictionary<string, object?> KeyAttributes { get; set; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public string? RowVersion { get; set; }
}

/// <summary>Our counterpart of the SDK's public <c>EntityCollection</c>.</summary>
public sealed class RecordCollection
{
    public IList<Record> Records { get; set; } = new List<Record>();

    public string? EntityName { get; set; }

    public bool MoreRecords { get; set; }

    public string? PagingCookie { get; set; }

    public int TotalRecordCount { get; set; }

    public bool TotalRecordCountLimitExceeded { get; set; }

    public string? MinActiveRowVersion { get; set; }

    public int RecordCountLessThanMarv { get; set; }
}

/// <summary>Our counterpart of the SDK's public <c>EntityReferenceCollection</c>.</summary>
public sealed class RecordReferenceCollection : List<RecordReference>;

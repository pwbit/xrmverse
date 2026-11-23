namespace Xrmd.Model.Metadata;

/// <summary>The display name of a column type, such as BigIntType (Web API <c>AttributeTypeDisplayName</c>).</summary>
[WireType("AttributeTypeDisplayName")]
public class ColumnTypeName
{
    public string? Value { get; set; }
}

/// <summary>The behavior of a date and time column (Web API <c>DateTimeBehavior</c>).</summary>
[WireType("DateTimeBehavior")]
public class DateTimeBehaviorName
{
    public string? Value { get; set; }
}

/// <summary>The format name of a memo column (Web API <c>MemoFormatName</c>).</summary>
[WireType("MemoFormatName")]
public class MemoFormatId
{
    public string? Value { get; set; }
}

/// <summary>The format name of a text column (Web API <c>StringFormatName</c>).</summary>
[WireType("StringFormatName")]
public class TextFormatId
{
    public string? Value { get; set; }
}

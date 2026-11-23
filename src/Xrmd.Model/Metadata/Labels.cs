namespace Xrmd.Model.Metadata;

/// <summary>A user-visible text in several languages (Web API <c>Label</c>).</summary>
[WireType("Label")]
public class LabelSet
{
    /// <summary>Never null, like the SDK: a missing key or an explicit <c>null</c> reads as an empty list.</summary>
    public List<LocalizedText>? LocalizedLabels { get; set => field = value ?? []; } = [];
    public LocalizedText? UserLocalizedLabel { get; set; }
}

/// <summary>One language of a <see cref="LabelSet"/> (Web API <c>LocalizedLabel</c>).</summary>
[WireType("LocalizedLabel")]
public class LocalizedText
{
    public string? Label { get; set; }
    public int LanguageCode { get; set; }
    public bool? IsManaged { get; set; }
    public Guid? MetadataId { get; set; }
    public bool? HasChanged { get; set; }
}

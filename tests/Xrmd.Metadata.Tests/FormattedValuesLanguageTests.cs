using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Metadata;
using Xunit;

namespace Xrmd.Metadata.Tests;

/// <summary>#109: a label in the caller's language, else 1033, else the first label.</summary>
public class FormattedValuesLanguageTests
{
    private static Label Of(params (string Text, int Lcid)[] labels)
    {
        var label = new Label();
        foreach (var (text, lcid) in labels) label.LocalizedLabels.Add(new LocalizedLabel(text, lcid));
        return label;
    }

    [Fact]
    public void Uses_the_label_in_the_requested_language() =>
        FormattedValues.LabelText(Of(("High", 1033), ("Alta", 1040)), 1040).Should().Be("Alta");

    [Fact]
    public void Missing_language_falls_back_to_1033_then_first()
    {
        FormattedValues.LabelText(Of(("Hoch", 1031), ("High", 1033)), 1040).Should().Be("High");
        FormattedValues.LabelText(Of(("Hoch", 1031)), 1040).Should().Be("Hoch");
    }

    [Fact]
    public void Default_language_is_1033() =>
        FormattedValues.LabelText(Of(("Alta", 1040), ("High", 1033))).Should().Be("High");
}

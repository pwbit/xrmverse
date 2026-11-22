using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Solution;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// A calculated column's definition in the metadata request rules (plan 2.12a, Task 4, rulings K3, K4 and K12): a Web API body with
/// <c>SourceType</c> and <c>FormulaDefinition</c> passes the create check with both kept, an update may change the formula (the
/// store re-validates it and refuses a <c>SourceType</c> change), and <c>SourceTypeMask</c> is the server's.
/// </summary>
public class CalculatedAttributeWebApiTests
{
    private const string Formula = "<Activity>formula</Activity>";

    private static MoneyAttributeMetadata FromBody(string json) =>
        JsonNode.Parse(json)!.Deserialize<AttributeMetadata>(MetadataSerializer.Options).Should().BeOfType<MoneyAttributeMetadata>().Subject;

    private static void SetMask(AttributeMetadata a, int mask) =>
        a.GetType().GetProperty(ServerOwnedProperties.SourceTypeMask)!.GetSetMethod(nonPublic: true)!.Invoke(a, [mask]);

    private static MoneyAttributeMetadata Calculated(string formula = Formula)
    {
        var a = new MoneyAttributeMetadata { LogicalName = "new_total", SchemaName = "new_Total", SourceType = 1 };
        ComputedColumnRules.SetFormula(a, formula);
        return a;
    }

    [Fact]
    public void Create_body_keeps_SourceType_and_FormulaDefinition()
    {
        var attribute = FromBody($$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.MoneyAttributeMetadata",
              "LogicalName": "new_total", "SchemaName": "new_Total", "PrecisionSource": 0, "Precision": 2,
              "SourceType": 1, "FormulaDefinition": {{JsonSerializer.Serialize(Formula)}}
            }
            """);

        attribute.SourceType.Should().Be(1);
        ComputedColumnRules.FormulaOf(attribute).Should().Be(Formula);
        FluentActions.Invoking(() => MetadataRequestRules.DemandOnlyModelledAttributeSettings(attribute, "new_order")).Should().NotThrow();
    }

    [Fact]
    public void Update_may_change_the_formula_and_ignores_the_mask()
    {
        var current = Calculated();
        SetMask(current, 1);

        var changed = Calculated("<Activity>v2</Activity>");
        SetMask(changed, 8);
        MetadataRequestRules.UnappliedAttributeChanges(current, changed, labelsOnly: false).Should().BeEmpty();

        // A SourceType change reaches the store, which refuses it with the column named (K4).
        var simple = Calculated();
        simple.SourceType = 0;
        MetadataRequestRules.UnappliedAttributeChanges(current, simple, labelsOnly: false).Should().BeEmpty();

        // A caller limited to labels cannot change the formula.
        MetadataRequestRules.UnappliedAttributeChanges(current, changed, labelsOnly: true).Should().Contain("FormulaDefinition");
    }
}

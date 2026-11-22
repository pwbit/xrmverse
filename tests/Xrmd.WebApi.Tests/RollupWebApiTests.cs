using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Solution;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// The Web API side of rollup columns (plan 2.12b, Task 5; rulings R10, R15), without a database: the parameters of the
/// <c>CalculateRollupField</c> function, its name reserved as an entity set name, and a Web API body that defines a rollup column
/// (<c>SourceType</c> 2 and <c>FormulaDefinition</c> kept by the create check).
/// </summary>
public class RollupWebApiTests
{
    private static readonly Guid Id = Guid.Parse("4b1e2c3d-0000-4000-8000-000000000001");

    private static FakeStore Store()
    {
        var account = new EntityMetadata { LogicalName = "account", SchemaName = "Account", EntitySetName = "accounts" };
        account.SetAttributes([new UniqueIdentifierAttributeMetadata { LogicalName = "accountid", SchemaName = "AccountId" }]);
        return new FakeStore(account);
    }

    private static Func<string, string?> Aliases(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Theory]
    [InlineData("{'@odata.id':'accounts(4b1e2c3d-0000-4000-8000-000000000001)'}")]
    [InlineData("{\"@odata.id\":\"https://org.example.com/api/data/v9.2/accounts(4b1e2c3d-0000-4000-8000-000000000001)\"}")]
    public void Reads_aliased_parameters(string target)
    {
        var request = RollupActions.ParseRequest("Target=@t,FieldName=@f", Aliases(("@t", target), ("@f", "'xrmv_ordertotal'")), Store());

        (request.Target.LogicalName, request.Target.Id, request.FieldName).Should().Be(("account", Id, "xrmv_ordertotal"));
    }

    [Fact]
    public void Reads_inline_values()
    {
        var request = RollupActions.ParseRequest($"Target={{'@odata.id':'accounts({Id})'}},FieldName='xrmv_ordercount'", Aliases(), Store());

        (request.Target.LogicalName, request.Target.Id, request.FieldName).Should().Be(("account", Id, "xrmv_ordercount"));
    }

    [Theory]
    [InlineData("Target=@t", "FieldName")]
    [InlineData("FieldName='xrmv_ordertotal'", "Target")]
    [InlineData("Target=@t,FieldName='xrmv_ordertotal',Other='x'", "Other")]
    [InlineData("Target=@t,FieldName='a',FieldName='b'", "FieldName")]
    [InlineData("Target=@t,FieldName=@missing", "@missing")]
    [InlineData("Target=@t,FieldName=xrmv_ordertotal", "FieldName")]
    [InlineData("Target='accounts',FieldName='xrmv_ordertotal'", "Target")]
    public void Wrong_parameters_are_400_naming_them(string parameters, string named)
    {
        var fault = FluentActions.Invoking(() => RollupActions.ParseRequest(parameters,
                Aliases(("@t", $"{{'@odata.id':'accounts({Id})'}}")), Store()))
            .Should().Throw<XrmdFaultException>().Which;
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.InvalidArgument, 400));
        fault.Message.Should().Contain(named);
    }

    [Theory]
    [InlineData("Target=@t,FieldName='x'", "@t", "{'@odata.id':'<b>bad</b>'}")]
    [InlineData("Target=@t,FieldName=@f", "@f", "<b>bad</b>")]
    public void Refusal_does_not_echo_the_value_given(string parameters, string alias, string value)
    {
        var fault = FluentActions.Invoking(() => RollupActions.ParseRequest(parameters,
                Aliases(("@t", alias == "@t" ? value : $"{{'@odata.id':'accounts({Id})'}}"), ("@f", value)), Store()))
            .Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().NotContain("<b>bad</b>", "the caller's text is not reflected; the expected shape is described instead");
    }

    [Fact]
    public void Unknown_entity_set_is_404()
    {
        var fault = FluentActions.Invoking(() => RollupActions.ParseRequest("Target=@t,FieldName='x'",
                Aliases(("@t", $"{{'@odata.id':'nothings({Id})'}}")), Store()))
            .Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(404);
    }

    [Fact]
    public void Function_name_is_a_reserved_entity_set_name()
    {
        RollupActions.OperationNames.Should().Equal("CalculateRollupField");
        ActionController.OperationNames.Should().Contain(RollupActions.OperationNames);
        foreach (var name in RollupActions.OperationNames)
            LogicalNameValidator.ReservedEntitySetNames.Contains(name.ToLowerInvariant()).Should().BeTrue($"'{name}' is reserved, ignoring case");
    }

    [Fact]
    public void Create_body_keeps_SourceType_2_and_FormulaDefinition()
    {
        const string formula = "<Activity>rollup</Activity>";
        var attribute = JsonNode.Parse($$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.DecimalAttributeMetadata",
              "LogicalName": "xrmv_ordertotal", "SchemaName": "xrmv_OrderTotal", "Precision": 2,
              "SourceType": 2, "FormulaDefinition": {{JsonSerializer.Serialize(formula)}}
            }
            """)!.Deserialize<AttributeMetadata>(MetadataSerializer.Options).Should().BeOfType<DecimalAttributeMetadata>().Subject;

        attribute.SourceType.Should().Be(RollupColumns.Rollup);
        ComputedColumnRules.FormulaOf(attribute).Should().Be(formula);
        FluentActions.Invoking(() => MetadataRequestRules.DemandOnlyModelledAttributeSettings(attribute, "account")).Should().NotThrow();
    }
}

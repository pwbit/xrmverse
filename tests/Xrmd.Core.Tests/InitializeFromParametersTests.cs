using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core;
using Xrmd.Core.Errors;

namespace Xrmd.Core.Tests;

/// <summary>#143, task 4: the parameters of InitializeFrom and its <c>TargetFieldType</c> filter (no database).</summary>
public class InitializeFromParametersTests
{
    private static readonly Guid Id = Guid.Parse("2fc1a20c-0000-4000-8000-000000000001");

    [Fact]
    public void Reads_the_sdk_request()
    {
        var request = new InitializeFromRequest
        {
            EntityMoniker = new EntityReference("account", Id), TargetEntityName = "Contact", TargetFieldType = TargetFieldType.ValidForUpdate,
            SkipParentalRelationshipMapping = true,
        };

        InitializeFrom.Parameters(request).Should().Be(
            new InitializeFrom.Request(new EntityReference("account", Id), "contact", TargetFieldType.ValidForUpdate, true));
    }

    [Theory]
    [InlineData(0, TargetFieldType.All)]
    [InlineData(3, TargetFieldType.ValidForRead)]
    public void Takes_the_type_as_a_number(int number, TargetFieldType expected)
    {
        var request = new OrganizationRequest("InitializeFrom")
        {
            ["EntityMoniker"] = new EntityReference("account", Id), ["TargetEntityName"] = "contact", ["TargetFieldType"] = number,
        };

        var parameters = InitializeFrom.Parameters(request);
        (parameters.FieldType, parameters.SkipParentalRelationshipMapping).Should().Be((expected, false));
    }

    [Theory]
    [InlineData("EntityMoniker")]
    [InlineData("TargetEntityName")]
    [InlineData("TargetFieldType")]
    public void Missing_parameter_is_400(string missing)
    {
        var request = new OrganizationRequest("InitializeFrom")
        {
            ["EntityMoniker"] = new EntityReference("account", Id), ["TargetEntityName"] = "contact", ["TargetFieldType"] = 1,
        };
        request.Parameters.Remove(missing);

        var act = () => InitializeFrom.Parameters(request);
        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Contain(missing);
    }

    [Fact]
    public void Type_out_of_range_is_400()
    {
        var request = new OrganizationRequest("InitializeFrom")
        {
            ["EntityMoniker"] = new EntityReference("account", Id), ["TargetEntityName"] = "contact", ["TargetFieldType"] = 4,
        };

        var act = () => InitializeFrom.Parameters(request);
        act.Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
    }

    [Fact]
    public void Keeps_columns_by_their_flags()
    {
        var createOnly = new StringAttributeMetadata { LogicalName = "a" };
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.IsValidForUpdate))!.SetValue(createOnly, false);

        InitializeFrom.Keeps(createOnly, TargetFieldType.All).Should().BeTrue();
        InitializeFrom.Keeps(createOnly, TargetFieldType.ValidForCreate).Should().BeTrue();
        InitializeFrom.Keeps(createOnly, TargetFieldType.ValidForUpdate).Should().BeFalse();
        InitializeFrom.Keeps(createOnly, TargetFieldType.ValidForRead).Should().BeTrue();
    }
}

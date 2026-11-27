using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions.Sdk;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using FieldType = Xrmd.Core.InitializeFrom.TargetFieldType;
using ModelRequest = Xrmd.Model.Messages.Request;

namespace Xrmd.Core.Tests;

/// <summary>
/// #143, task 4: the parameters of InitializeFrom and its <c>TargetFieldType</c> filter (no database). S.2e3c1 (Task 6): read from the
/// model request, as an SDK request crosses the mirror.
/// </summary>
public class InitializeFromParametersTests
{
    private static readonly Guid Id = Guid.Parse("2fc1a20c-0000-4000-8000-000000000001");

    private static ModelRequest Model(OrganizationRequest request) => new SdkMirror().In(request)!;

    [Fact]
    public void Reads_the_sdk_request()
    {
        var request = new InitializeFromRequest
        {
            EntityMoniker = new EntityReference("account", Id), TargetEntityName = "Contact", TargetFieldType = TargetFieldType.ValidForUpdate,
            SkipParentalRelationshipMapping = true,
        };

        var parameters = InitializeFrom.Parameters(Model(request));

        (parameters.Source.LogicalName, parameters.Source.Id, parameters.Target, parameters.FieldType, parameters.SkipParentalRelationshipMapping)
            .Should().Be(("account", Id, "contact", FieldType.ValidForUpdate, true));
    }

    [Theory]
    [InlineData(0, FieldType.All)]
    [InlineData(3, FieldType.ValidForRead)]
    public void Takes_the_type_as_a_number(int number, FieldType expected)
    {
        var request = new OrganizationRequest("InitializeFrom")
        {
            ["EntityMoniker"] = new EntityReference("account", Id), ["TargetEntityName"] = "contact", ["TargetFieldType"] = number,
        };

        var parameters = InitializeFrom.Parameters(Model(request));
        (parameters.FieldType, parameters.SkipParentalRelationshipMapping).Should().Be((expected, false));
    }

    [Fact]
    public void Takes_the_type_as_a_choice_or_the_model_enum()
    {
        var request = new ModelRequest { Name = "InitializeFrom" };
        request.Parameters["EntityMoniker"] = new RecordReference { LogicalName = "account", Id = Id };
        request.Parameters["TargetEntityName"] = "contact";
        request.Parameters["TargetFieldType"] = new ChoiceValue(2);
        InitializeFrom.Parameters(request).FieldType.Should().Be(FieldType.ValidForUpdate);

        request.Parameters["TargetFieldType"] = FieldType.ValidForCreate;
        InitializeFrom.Parameters(request).FieldType.Should().Be(FieldType.ValidForCreate);
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

        var act = () => InitializeFrom.Parameters(Model(request));
        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Be($"Required parameter '{missing}' of request 'InitializeFrom' is missing.");
    }

    [Fact]
    public void Type_out_of_range_is_400()
    {
        var request = new OrganizationRequest("InitializeFrom")
        {
            ["EntityMoniker"] = new EntityReference("account", Id), ["TargetEntityName"] = "contact", ["TargetFieldType"] = 4,
        };

        var act = () => InitializeFrom.Parameters(Model(request));
        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Be(
            "Invalid TargetFieldType '4': expected All (0), ValidForCreate (1), ValidForUpdate (2) or ValidForRead (3).");
    }

    [Fact]
    public void Keeps_columns_by_their_flags()
    {
        var createOnly = new TextColumn { LogicalName = "a", IsValidForUpdate = false };

        InitializeFrom.Keeps(createOnly, FieldType.All).Should().BeTrue();
        InitializeFrom.Keeps(createOnly, FieldType.ValidForCreate).Should().BeTrue();
        InitializeFrom.Keeps(createOnly, FieldType.ValidForUpdate).Should().BeFalse();
        InitializeFrom.Keeps(createOnly, FieldType.ValidForRead).Should().BeTrue();
    }

    /// <summary>The text of an out-of-range choice value is today's (the SDK <c>OptionSetValue</c>'s).</summary>
    [Fact]
    public void Choice_out_of_range_is_400_with_todays_text()
    {
        var request = new OrganizationRequest("InitializeFrom")
        {
            ["EntityMoniker"] = new EntityReference("account", Id), ["TargetEntityName"] = "contact", ["TargetFieldType"] = new OptionSetValue(7),
        };

        var act = () => InitializeFrom.Parameters(Model(request));
        act.Should().Throw<XrmdFaultException>().Which.Message.Should().Be(
            $"Invalid TargetFieldType '{new OptionSetValue(7)}': expected All (0), ValidForCreate (1), ValidForUpdate (2) or ValidForRead (3).");
    }

    /// <summary>The text of any other SDK class value is today's: the SDK type name, not the name of the model value it crossed as.</summary>
    [Fact]
    public void Other_sdk_values_out_of_range_print_the_sdk_type_name()
    {
        var values = new (object Value, string Text)[]
        {
            (new EntityReference("account", Id), "Microsoft.Xrm.Sdk.EntityReference"),
            (new Money(3m), "Microsoft.Xrm.Sdk.Money"),
            (new Entity("account"), "Microsoft.Xrm.Sdk.Entity"),
            (new EntityCollection(), "Microsoft.Xrm.Sdk.EntityCollection"),
            (new OptionSetValueCollection(), "Microsoft.Xrm.Sdk.OptionSetValueCollection"),
            (new EntityReferenceCollection(), "Microsoft.Xrm.Sdk.EntityReferenceCollection"),
            ("abc", "abc"),
        };
        foreach (var (value, text) in values)
        {
            var request = new OrganizationRequest("InitializeFrom")
            {
                ["EntityMoniker"] = new EntityReference("account", Id), ["TargetEntityName"] = "contact", ["TargetFieldType"] = value,
            };

            var act = () => InitializeFrom.Parameters(Model(request));
            act.Should().Throw<XrmdFaultException>().Which.Message.Should().Be(
                $"Invalid TargetFieldType '{text}': expected All (0), ValidForCreate (1), ValidForUpdate (2) or ValidForRead (3).");
        }
    }
}

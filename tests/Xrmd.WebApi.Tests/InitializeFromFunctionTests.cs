using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi.Tests;

/// <summary>#143, task 5: the parameters and the JSON of the InitializeFrom Web API function (no database).</summary>
public class InitializeFromFunctionTests
{
    private static readonly Guid Account = Guid.Parse("2fc1a20c-0000-4000-8000-000000000001");
    private const string Aliases = "EntityMoniker=@p1,TargetEntityName=@p2,TargetFieldType=@p3";

    private static FakeStore Store()
    {
        var account = new EntityMetadata { LogicalName = "account", SchemaName = "Account", EntitySetName = "accounts" };
        account.SetAttributes([new UniqueIdentifierAttributeMetadata { LogicalName = "accountid", SchemaName = "AccountId" }]);
        var contact = new EntityMetadata { LogicalName = "contact", SchemaName = "Contact", EntitySetName = "contacts" };
        contact.SetAttributes(
        [
            new UniqueIdentifierAttributeMetadata { LogicalName = "contactid", SchemaName = "ContactId" },
            new StringAttributeMetadata { LogicalName = "telephone1", SchemaName = "Telephone1" },
            new IntegerAttributeMetadata { LogicalName = "numberofchildren", SchemaName = "NumberOfChildren" },
            new LookupAttributeMetadata { LogicalName = "parentcustomerid", SchemaName = "ParentCustomerId", Targets = ["account", "contact"] },
        ]);
        var store = new FakeStore(account, contact);
        foreach (var target in new[] { "account", "contact" })
            store.OneToMany.Add(new OneToManyRelationshipMetadata
            {
                SchemaName = $"contact_customer_{target}s", ReferencedEntity = target, ReferencingEntity = "contact",
                ReferencingAttribute = "parentcustomerid", ReferencingEntityNavigationPropertyName = $"parentcustomerid_{target}",
            });
        return store;
    }

    private static HttpRequest Request(params (string Name, string Value)[] query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(query.Select(q => new KeyValuePair<string, string?>(q.Name, q.Value)));
        return context.Request;
    }

    [Theory]
    [InlineData("{'@odata.id':'accounts(2fc1a20c-0000-4000-8000-000000000001)'}", "Microsoft.Dynamics.CRM.TargetFieldType'ValidForCreate'", TargetFieldType.ValidForCreate)]
    [InlineData("{\"@odata.id\":\"https://org.example.com/api/data/v9.2/accounts(2fc1a20c-0000-4000-8000-000000000001)\"}", "'ValidForRead'", TargetFieldType.ValidForRead)]
    public void Reads_aliased_parameters(string moniker, string type, TargetFieldType expected)
    {
        var request = FunctionController.ParseInitializeFrom(Request(("@p1", moniker), ("@p2", "'contact'"), ("@p3", type)), Store(), Aliases);

        (request.EntityMoniker.LogicalName, request.EntityMoniker.Id, request.TargetEntityName, request.TargetFieldType)
            .Should().Be(("account", Account, "contact", expected));
        request.Parameters.ContainsKey("SkipParentalRelationshipMapping").Should().BeFalse();
    }

    [Fact]
    public void Reads_inline_values_and_the_skip_flag()
    {
        var request = FunctionController.ParseInitializeFrom(Request(("@p1", $"{{'@odata.id':'accounts({Account})'}}")), Store(),
            "EntityMoniker=@p1,TargetEntityName='contact',TargetFieldType=Microsoft.Dynamics.CRM.TargetFieldType'All',SkipParentalRelationshipMapping=true");

        (request.TargetEntityName, request.TargetFieldType, request.SkipParentalRelationshipMapping).Should().Be(("contact", TargetFieldType.All, true));
    }

    [Fact]
    public void A_numeric_target_field_type_is_refused_with_0x80060888()
    {
        var fault = FluentActions.Invoking(() => FunctionController.ParseInitializeFrom(
                Request(("@p1", $"{{'@odata.id':'accounts({Account})'}}"), ("@p2", "'contact'"), ("@p3", "1")), Store(), Aliases))
            .Should().Throw<XrmdFaultException>().Which;
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.ResourceNotFound, 400));
    }

    [Fact]
    public void Missing_parameter_is_400_and_unknown_set_is_404()
    {
        FluentActions.Invoking(() => FunctionController.ParseInitializeFrom(
                Request(("@p1", $"{{'@odata.id':'accounts({Account})'}}"), ("@p2", "'contact'")), Store(), "EntityMoniker=@p1,TargetEntityName=@p2"))
            .Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(400);
        FluentActions.Invoking(() => FunctionController.ParseInitializeFrom(
                Request(("@p1", $"{{'@odata.id':'nothings({Account})'}}"), ("@p2", "'contact'"), ("@p3", "'All'")), Store(), Aliases))
            .Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(404);
    }

    [Fact]
    public void Writes_binds_and_values_without_nulls()
    {
        var entity = new Entity("contact")
        {
            ["parentcustomerid"] = new EntityReference("account", Account) { Name = "Contoso" },
            ["telephone1"] = "555 0100",
            ["numberofchildren"] = null,
        };

        var json = FunctionController.InitializedJson("https://org.example.com/api/data/v9.2/", Store(), entity);

        JsonNode.DeepEquals(json, JsonNode.Parse($$"""
            {
              "@odata.context": "https://org.example.com/api/data/v9.2/$metadata#contacts/$entity",
              "@odata.type": "#Microsoft.Dynamics.CRM.contact",
              "parentcustomerid_account@odata.bind": "accounts({{Account}})",
              "telephone1": "555 0100"
            }
            """)).Should().BeTrue(json.ToJsonString());
    }
}

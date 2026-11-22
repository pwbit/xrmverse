using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi.Tests;

/// <summary>RetrieveUserPrivileges as a Web API function bound to systemuser (plan 3.5 UI-1a task 3).</summary>
public class RetrieveUserPrivilegesTests
{
    private static readonly Guid User = Guid.Parse("611e7713-68d7-4622-b552-85060af450bc");

    private static FakeStore Store()
    {
        var user = new EntityMetadata { LogicalName = "systemuser", SchemaName = "SystemUser", EntitySetName = "systemusers" };
        user.SetAttributes([new UniqueIdentifierAttributeMetadata { LogicalName = "systemuserid", SchemaName = "SystemUserId" }]);
        var account = new EntityMetadata { LogicalName = "account", SchemaName = "Account", EntitySetName = "accounts" };
        account.SetAttributes([new UniqueIdentifierAttributeMetadata { LogicalName = "accountid", SchemaName = "AccountId" }]);
        return new FakeStore(user, account);
    }

    [Fact]
    public void The_bound_user_is_read_from_the_segment()
    {
        FunctionController.ParseUser(Store(), $"systemusers({User})").Should().Be(User);
    }

    [Theory]
    [InlineData("systemusers")]
    [InlineData("systemusers(not-a-guid)")]
    public void A_segment_without_a_user_key_is_400(string segment)
    {
        var fault = FluentActions.Invoking(() => FunctionController.ParseUser(Store(), segment))
            .Should().Throw<XrmdFaultException>().Which;
        fault.HttpStatus.Should().Be(400);
    }

    [Fact]
    public void Another_entity_set_or_an_unknown_one_is_404()
    {
        foreach (var segment in new[] { $"accounts({User})", $"nothing({User})" })
            FluentActions.Invoking(() => FunctionController.ParseUser(Store(), segment))
                .Should().Throw<XrmdFaultException>().Which.HttpStatus.Should().Be(404);
    }

    [Fact]
    public void The_response_is_written_with_the_learn_shape()
    {
        var business = Guid.NewGuid();
        var privilege = Guid.NewGuid();
        var json = FunctionController.ToJson("https://host/api/data/v9.2/$metadata#Microsoft.Dynamics.CRM.RetrieveUserPrivilegesResponse",
        [
            new RolePrivilege { Depth = PrivilegeDepth.Global, PrivilegeId = privilege, BusinessUnitId = business, PrivilegeName = "prvReadAccount" },
        ]);

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.RetrieveUserPrivilegesResponse");
        var item = json["RolePrivileges"]!.AsArray().Should().ContainSingle().Which!.AsObject();
        item["Depth"]!.GetValue<string>().Should().Be("Global");
        item["PrivilegeId"]!.GetValue<string>().Should().Be(privilege.ToString());
        item["BusinessUnitId"]!.GetValue<string>().Should().Be(business.ToString());
        item["PrivilegeName"]!.GetValue<string>().Should().Be("prvReadAccount");
    }

    [Fact]
    public async Task Metadata_declares_the_function_and_the_privilege_name()
    {
        var registry = new Xrmd.Pipeline.CustomApis.CustomApiRegistry(_ => Task.FromResult<IReadOnlyList<Xrmd.Pipeline.CustomApis.CustomApiDefinition>>([]));
        await registry.AllAsync(CancellationToken.None);
        var model = new ODataEdmModelBuilder(new FakeStore(Role(), Privilege(), SystemUser()), registry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ODataEdmModelBuilder>.Instance).Build();

        var function = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.RetrieveUserPrivileges").Should().ContainSingle().Which;
        function.IsBound.Should().BeTrue();
        function.Parameters.First().Type.FullName().Should().Be("Microsoft.Dynamics.CRM.systemuser");
        var response = function.GetReturn().Type.Definition.Should().BeAssignableTo<IEdmComplexType>().Which;
        response.FullTypeName().Should().Be("Microsoft.Dynamics.CRM.RetrieveUserPrivilegesResponse");
        response.FindProperty("RolePrivileges").Type.IsCollection().Should().BeTrue();
        var rolePrivilege = (IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.RolePrivilege");
        rolePrivilege.FindProperty("PrivilegeName").Should().NotBeNull();
    }

    private static EntityMetadata Entity(string logical, string set, string schema)
    {
        var entity = new EntityMetadata { LogicalName = logical, SchemaName = schema, EntitySetName = set };
        entity.SetAttributes([new UniqueIdentifierAttributeMetadata { LogicalName = logical + "id", SchemaName = schema + "Id" }]);
        return entity;
    }

    private static EntityMetadata Role() => Entity("role", "roles", "Role");
    private static EntityMetadata Privilege() => Entity("privilege", "privileges", "Privilege");
    private static EntityMetadata SystemUser() => Entity("systemuser", "systemusers", "SystemUser");
}

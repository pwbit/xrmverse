using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Validation;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// The access and sharing messages of the Web API (plan 3.5 UI-1b task 2): the Learn request shapes of RetrievePrincipalAccess,
/// RetrieveSharedPrincipalsAndAccess, GrantAccess, ModifyAccess and RevokeAccess translated into the SDK requests, the
/// <c>AccessRights</c> string form, the response JSON, the operations in <c>$metadata</c> and their reserved names.
/// </summary>
public class AccessActionsJsonTests
{
    private static readonly Guid Record = Guid.Parse("611e7713-68d7-4622-b552-85060af450bc");
    private static readonly Guid User = Guid.Parse("d249d106-38b5-ec11-983f-002248296cd0");
    private static readonly Guid Team = Guid.Parse("0b7c1d7e-2f3a-4f0a-9b7e-4c2f9d1a6e55");

    private static EntityMetadata Table(string name, string set)
    {
        var table = new EntityMetadata { LogicalName = name, SchemaName = name, EntitySetName = set };
        table.SetAttributes([new UniqueIdentifierAttributeMetadata { LogicalName = name + "id", SchemaName = name + "Id" }]);
        return table;
    }

    private static FakeStore Store() => new(Table("account", "accounts"), Table("systemuser", "systemusers"), Table("team", "teams"));

    private static XrmdFaultException Refused(Action act) =>
        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400).Which;

    private static JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();

    private static string UserJson => $$"""{ "@odata.type": "Microsoft.Dynamics.CRM.systemuser", "systemuserid": "{{User}}" }""";

    private static string TargetJson => $$"""{ "@odata.type": "Microsoft.Dynamics.CRM.account", "accountid": "{{Record}}" }""";

    [Fact]
    public void Retrieve_principal_access_reads_the_target_alias_and_the_bound_user()
    {
        // The Learn request shape: systemusers({id})/Microsoft.Dynamics.CRM.RetrievePrincipalAccess(Target=@t)?@t={"@odata.id":"accounts({id})"}
        var request = AccessActions.ToRetrievePrincipalAccess($"systemusers({User})", "Target=@t",
            a => a == "@t" ? $$"""{"@odata.id":"accounts({{Record}})"}""" : null, Store());

        request.Should().BeOfType<RetrievePrincipalAccessRequest>();
        request["Target"].Should().BeEquivalentTo(new EntityReference("account", Record));
        request["Principal"].Should().BeEquivalentTo(new EntityReference("systemuser", User));
    }

    [Fact]
    public void Retrieve_principal_access_of_a_team_and_an_inline_entity_target()
    {
        var request = AccessActions.ToRetrievePrincipalAccess($"teams({Team})", $"Target={TargetJson}", _ => null, Store());

        request["Principal"].Should().BeEquivalentTo(new EntityReference("team", Team));
        request["Target"].Should().BeEquivalentTo(new EntityReference("account", Record));
    }

    [Fact]
    public void Retrieve_principal_access_is_bound_to_systemuser_or_team_only()
    {
        FluentActions.Invoking(() => AccessActions.ToRetrievePrincipalAccess($"accounts({Record})", "Target=@t", _ => "{}", Store()))
            .Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 404);
        FluentActions.Invoking(() => AccessActions.ToRetrievePrincipalAccess("nothing(" + User + ")", "Target=@t", _ => "{}", Store()))
            .Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 404);
        Refused(() => AccessActions.ToRetrievePrincipalAccess("systemusers", "Target=@t", _ => "{}", Store()));
        Refused(() => AccessActions.ToRetrievePrincipalAccess("systemusers(not-a-guid)", "Target=@t", _ => "{}", Store()));
    }

    [Fact]
    public void Function_parameters_are_checked()
    {
        Refused(() => AccessActions.ToRetrievePrincipalAccess($"systemusers({User})", "", _ => null, Store())).Message.Should().Contain("Target");
        Refused(() => AccessActions.ToRetrievePrincipalAccess($"systemusers({User})", "Target=@t", _ => null, Store())).Message.Should().Contain("@t");
        Refused(() => AccessActions.ToRetrievePrincipalAccess($"systemusers({User})", "Principal=@t", _ => "{}", Store())).Message.Should().Contain("Principal");
        Refused(() => AccessActions.ToRetrieveShared("Target=@t", _ => "[1]", Store())).Message.Should().Contain("Target");
    }

    [Fact]
    public void Retrieve_shared_principals_reads_the_target()
    {
        var request = AccessActions.ToRetrieveShared("Target=@t", a => $$"""{'@odata.id':'accounts({{Record}})'}""", Store());

        request.Should().BeOfType<RetrieveSharedPrincipalsAndAccessRequest>();
        request["Target"].Should().BeEquivalentTo(new EntityReference("account", Record));
    }

    [Fact]
    public void Grant_and_modify_read_the_target_and_the_principal_access()
    {
        var body = Body($$"""{ "Target": {{TargetJson}}, "PrincipalAccess": { "AccessMask": "ReadAccess, WriteAccess", "Principal": {{UserJson}} } }""");

        var grant = AccessActions.ToActionRequest("GrantAccess", body, Store()).Should().BeOfType<GrantAccessRequest>().Subject;
        grant.Target.Should().BeEquivalentTo(new EntityReference("account", Record));
        grant.PrincipalAccess.AccessMask.Should().Be(AccessRights.ReadAccess | AccessRights.WriteAccess);
        grant.PrincipalAccess.Principal.Should().BeEquivalentTo(new EntityReference("systemuser", User));

        var modify = AccessActions.ToActionRequest("ModifyAccess", body, Store()).Should().BeOfType<ModifyAccessRequest>().Subject;
        modify.PrincipalAccess.AccessMask.Should().Be(AccessRights.ReadAccess | AccessRights.WriteAccess);
    }

    [Fact]
    public void Revoke_reads_the_target_and_the_revokee()
    {
        var body = Body($$"""{ "Target": {{TargetJson}}, "Revokee": {{UserJson}} }""");

        var revoke = AccessActions.ToActionRequest("RevokeAccess", body, Store()).Should().BeOfType<RevokeAccessRequest>().Subject;
        revoke.Target.Should().BeEquivalentTo(new EntityReference("account", Record));
        revoke.Revokee.Should().BeEquivalentTo(new EntityReference("systemuser", User));
    }

    [Theory]
    [InlineData("ReadAccess", AccessRights.ReadAccess)]
    [InlineData("readaccess,ShareAccess", AccessRights.ReadAccess | AccessRights.ShareAccess)]
    [InlineData(" DeleteAccess , AssignAccess ", AccessRights.DeleteAccess | AccessRights.AssignAccess)]
    [InlineData("ReadAccess, WriteAccess, AppendAccess, AppendToAccess, CreateAccess, DeleteAccess, ShareAccess, AssignAccess",
        AccessRights.ReadAccess | AccessRights.WriteAccess | AccessRights.AppendAccess | AccessRights.AppendToAccess
        | AccessRights.CreateAccess | AccessRights.DeleteAccess | AccessRights.ShareAccess | AccessRights.AssignAccess)]
    public void Access_rights_are_a_comma_separated_list_of_member_names(string text, AccessRights expected)
    {
        AccessActions.ReadAccessRights("GrantAccess", "AccessMask", JsonValue.Create(text)).Should().Be(expected);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("ReadAccess, Bogus")]
    [InlineData("3")]
    [InlineData("")]
    [InlineData("ReadAccess,,WriteAccess")]
    public void A_bad_access_mask_is_400(string text)
    {
        Refused(() => AccessActions.ReadAccessRights("GrantAccess", "AccessMask", JsonValue.Create(text))).Message.Should().Contain("AccessMask");
    }

    [Fact]
    public void An_access_mask_that_is_not_a_string_is_400()
    {
        Refused(() => AccessActions.ReadAccessRights("GrantAccess", "AccessMask", JsonValue.Create(3))).Message.Should().Contain("AccessMask");
        Refused(() => AccessActions.ReadAccessRights("GrantAccess", "AccessMask", new JsonObject())).Message.Should().Contain("AccessMask");
    }

    [Fact]
    public void Rights_are_written_as_member_names()
    {
        AccessActions.FormatRights(AccessRights.ReadAccess | AccessRights.WriteAccess | AccessRights.AssignAccess)
            .Should().Be("ReadAccess, WriteAccess, AssignAccess");
        AccessActions.FormatRights(AccessRights.None).Should().Be("None");
    }

    [Theory]
    [InlineData("""{ "PrincipalAccess": { "AccessMask": "ReadAccess", "Principal": USER } }""", "Target")]
    [InlineData("""{ "Target": TARGET }""", "PrincipalAccess")]
    [InlineData("""{ "Target": TARGET, "PrincipalAccess": { "Principal": USER } }""", "AccessMask")]
    [InlineData("""{ "Target": TARGET, "PrincipalAccess": { "AccessMask": "ReadAccess" } }""", "Principal")]
    [InlineData("""{ "Target": TARGET, "PrincipalAccess": { "AccessMask": "Bogus", "Principal": USER } }""", "AccessMask")]
    [InlineData("""{ "Target": TARGET, "PrincipalAccess": { "AccessMask": "ReadAccess", "Principal": USER, "Extra": 1 } }""", "Extra")]
    [InlineData("""{ "Target": TARGET, "PrincipalAccess": { "AccessMask": "ReadAccess", "Principal": USER }, "Revokee": USER }""", "Revokee")]
    public void A_grant_with_a_missing_or_wrong_parameter_is_400(string template, string mentions)
    {
        var body = Body(template.Replace("USER", UserJson).Replace("TARGET", TargetJson));
        Refused(() => AccessActions.ToActionRequest("GrantAccess", body, Store())).Message.Should().Contain(mentions);
    }

    [Fact]
    public void A_revoke_with_a_missing_or_wrong_parameter_is_400()
    {
        Refused(() => AccessActions.ToActionRequest("RevokeAccess", Body($$"""{ "Target": {{TargetJson}} }"""), Store())).Message.Should().Contain("Revokee");
        Refused(() => AccessActions.ToActionRequest("RevokeAccess", Body($$"""{ "Revokee": {{UserJson}} }"""), Store())).Message.Should().Contain("Target");
        Refused(() => AccessActions.ToActionRequest("RevokeAccess",
            Body($$"""{ "Target": {{TargetJson}}, "Revokee": {{UserJson}}, "PrincipalAccess": {} }"""), Store())).Message.Should().Contain("PrincipalAccess");
    }

    [Fact]
    public void A_target_of_an_unknown_table_is_400()
    {
        var body = Body($$"""{ "Target": { "@odata.type": "Microsoft.Dynamics.CRM.nothing", "nothingid": "{{Record}}" }, "Revokee": {{UserJson}} }""");
        Refused(() => AccessActions.ToActionRequest("RevokeAccess", body, Store())).Message.Should().Contain("nothing");
    }

    [Fact]
    public void The_responses_are_written_with_the_learn_shapes()
    {
        var principal = AccessActions.ToJson("https://host/$metadata#Microsoft.Dynamics.CRM.RetrievePrincipalAccessResponse",
            AccessRights.ReadAccess | AccessRights.WriteAccess);
        principal["@odata.context"]!.GetValue<string>().Should().EndWith("RetrievePrincipalAccessResponse");
        principal["AccessRights"]!.GetValue<string>().Should().Be("ReadAccess, WriteAccess");

        var shared = AccessActions.ToJson("https://host/$metadata#Microsoft.Dynamics.CRM.RetrieveSharedPrincipalsAndAccessResponse",
            [new PrincipalAccess { AccessMask = AccessRights.ReadAccess, Principal = new EntityReference("systemuser", User) },
             new PrincipalAccess { AccessMask = AccessRights.DeleteAccess | AccessRights.ShareAccess, Principal = new EntityReference("team", Team) }],
            Store());
        var items = shared["PrincipalAccesses"]!.AsArray();
        items.Should().HaveCount(2);
        items[0]!["AccessMask"]!.GetValue<string>().Should().Be("ReadAccess");
        items[0]!["Principal"]!["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.systemuser");
        items[0]!["Principal"]!["systemuserid"]!.GetValue<string>().Should().Be(User.ToString());
        items[1]!["AccessMask"]!.GetValue<string>().Should().Be("DeleteAccess, ShareAccess");
        items[1]!["Principal"]!["teamid"]!.GetValue<string>().Should().Be(Team.ToString());
    }

    [Fact]
    public void Metadata_declares_the_access_operations()
    {
        var model = new ODataEdmModelBuilder(Store(), TestCustomApis.None()).Build();
        model.Validate(out var errors).Should().BeTrue(string.Join("; ", errors.Select(e => e.ErrorMessage)));

        var access = (IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.PrincipalAccess")!;
        access.FindProperty("AccessMask")!.Type.FullName().Should().Be("Microsoft.Dynamics.CRM.AccessRights");
        access.DeclaredNavigationProperties().Select(p => p.Name).Should().Equal("Principal");

        var principal = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.RetrievePrincipalAccess");
        principal.Should().HaveCount(2); // bound to systemuser and to team
        foreach (var function in principal)
        {
            function.IsFunction().Should().BeTrue();
            function.IsBound.Should().BeTrue();
            function.Parameters.Select(p => p.Name).Should().Equal("entity", "Target");
            function.GetReturn().Type.FullName().Should().Be("Microsoft.Dynamics.CRM.RetrievePrincipalAccessResponse");
        }
        principal.Select(f => f.Parameters.First().Type.FullName()).Should()
            .BeEquivalentTo("Microsoft.Dynamics.CRM.systemuser", "Microsoft.Dynamics.CRM.team");
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.RetrievePrincipalAccessResponse")!)
            .FindProperty("AccessRights")!.Type.FullName().Should().Be("Microsoft.Dynamics.CRM.AccessRights");

        var shared = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.RetrieveSharedPrincipalsAndAccess").Should().ContainSingle().Which;
        shared.IsFunction().Should().BeTrue();
        shared.IsBound.Should().BeFalse();
        shared.Parameters.Select(p => p.Name).Should().Equal("Target");
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.RetrieveSharedPrincipalsAndAccessResponse")!)
            .FindProperty("PrincipalAccesses")!.Type.FullName().Should().Be("Collection(Microsoft.Dynamics.CRM.PrincipalAccess)");
        model.EntityContainer.FindOperationImports("RetrieveSharedPrincipalsAndAccess").Should().ContainSingle();

        foreach (var name in new[] { "GrantAccess", "ModifyAccess" })
        {
            var action = model.FindDeclaredOperations("Microsoft.Dynamics.CRM." + name).Should().ContainSingle().Which;
            action.IsAction().Should().BeTrue();
            action.IsBound.Should().BeFalse();
            action.Parameters.Select(p => p.Name).Should().Equal("Target", "PrincipalAccess");
            model.EntityContainer.FindOperationImports(name).Should().ContainSingle();
        }
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.RevokeAccess").Should().ContainSingle()
            .Which.Parameters.Select(p => p.Name).Should().Equal("Target", "Revokee");
    }

    [Fact]
    public void Every_unbound_access_operation_name_is_a_reserved_entity_set_name()
    {
        AccessActions.OperationNames.Should().BeEquivalentTo(["RetrieveSharedPrincipalsAndAccess", "GrantAccess", "ModifyAccess", "RevokeAccess"]);
        ActionController.OperationNames.Should().Contain(AccessActions.OperationNames);
        foreach (var name in AccessActions.OperationNames)
            LogicalNameValidator.ReservedEntitySetNames.Contains(name.ToLowerInvariant()).Should().BeTrue($"'{name}' is reserved, ignoring case");
    }
}

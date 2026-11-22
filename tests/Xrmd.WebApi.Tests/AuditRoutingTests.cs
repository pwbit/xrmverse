using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Validation;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// The audit operations of the Web API (plan 13, task 5): the Learn request shapes translated into the SDK requests, the
/// <c>AuditDetail</c> JSON of the Learn samples, the operations in <c>$metadata</c> and their reserved names.
/// </summary>
public class AuditRoutingTests
{
    private static readonly Guid Id = Guid.Parse("611e7713-68d7-4622-b552-85060af450bc");
    private static readonly Guid Other = Guid.Parse("d249d106-38b5-ec11-983f-002248296cd0");

    private static EntityMetadata Account()
    {
        var account = new EntityMetadata { LogicalName = "account", SchemaName = "Account", EntitySetName = "accounts" };
        account.SetAttributes(SystemAttributes.Complete(account,
        [
            new StringAttributeMetadata { LogicalName = "name", SchemaName = "Name", MaxLength = 160 },
            new MemoAttributeMetadata { LogicalName = "description", SchemaName = "Description", MaxLength = 2000 },
            new LookupAttributeMetadata { LogicalName = "parentaccountid", SchemaName = "ParentAccountId", Targets = ["account"] },
        ]));
        return account;
    }

    private static EntityMetadata AuditEntity()
    {
        var audit = new EntityMetadata { LogicalName = "audit", SchemaName = "Audit", EntitySetName = "audits" };
        audit.SetAttributes([new UniqueIdentifierAttributeMetadata { LogicalName = "auditid", SchemaName = "AuditId" }]);
        return audit;
    }

    private static FakeStore Store() => new(Account(), AuditEntity());

    private static OrganizationRequest Function(string name, string parameters, params (string Alias, string Value)[] aliases) =>
        AuditEndpoints.ToFunctionRequest(name, parameters, a => aliases.FirstOrDefault(x => x.Alias == a).Value, Store());

    private static XrmdFaultException Refused(Action act) =>
        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400).Which;

    [Fact]
    public void Get_retrieve_record_change_history_with_aliases()
    {
        // The Learn request shape: the target as an @odata.id in single quotes, the paging info as JSON.
        var request = Function("RetrieveRecordChangeHistory", "Target=@target,PagingInfo=@paginginfo",
            ("@target", $"{{ '@odata.id':'accounts({Id})'}}"),
            ("@paginginfo", """{ "PageNumber": 1, "Count": 2, "ReturnTotalRecordCount": true }"""));

        request.RequestName.Should().Be("RetrieveRecordChangeHistory");
        request["Target"].Should().BeEquivalentTo(new EntityReference("account", Id));
        var paging = request["PagingInfo"].Should().BeOfType<PagingInfo>().Subject;
        (paging.PageNumber, paging.Count, paging.ReturnTotalRecordCount).Should().Be((1, 2, true));

        // Inline, without paging info, and the target as a full URL.
        var inline = Function("RetrieveRecordChangeHistory", $"Target={{\"@odata.id\":\"https://host/api/data/v9.2/accounts({Id})\"}}");
        inline["Target"].Should().BeEquivalentTo(new EntityReference("account", Id));
        inline.Parameters.ContainsKey("PagingInfo").Should().BeFalse();

        var attribute = Function("RetrieveAttributeChangeHistory", "Target=@t,AttributeLogicalName=@a,PagingInfo=@p",
            ("@t", $"{{'@odata.id':'accounts({Id})'}}"), ("@a", "'description'"),
            ("@p", """{"PageNumber":2,"Count":1,"PagingCookie":"<cookie page=\"1\" />"}"""));
        attribute["AttributeLogicalName"].Should().Be("description");
        ((PagingInfo)attribute["PagingInfo"]).PagingCookie.Should().Be("<cookie page=\"1\" />");
    }

    [Fact]
    public void Function_parameters_are_checked()
    {
        Refused(() => Function("RetrieveRecordChangeHistory", "Target=@t")).Message.Should().Contain("@t");
        Refused(() => Function("RetrieveRecordChangeHistory", "Record=@t", ("@t", "{}"))).Message.Should().Contain("Record");
        Refused(() => Function("RetrieveRecordChangeHistory", "PagingInfo=@p", ("@p", "{}"))).Message.Should().Contain("Target");
        Refused(() => Function("RetrieveRecordChangeHistory", "Target=@t", ("@t", "{'@odata.id':'nothings(" + Id + ")'}")))
            .Message.Should().Contain("nothings");
        Refused(() => Function("RetrieveRecordChangeHistory", "Target=@t,PagingInfo=@p", ("@t", $"{{'@odata.id':'accounts({Id})'}}"),
            ("@p", """{"Page":1}"""))).Message.Should().Contain("Page");
        Refused(() => Function("RetrieveAttributeChangeHistory", "Target=@t", ("@t", $"{{'@odata.id':'accounts({Id})'}}")))
            .Message.Should().Contain("AttributeLogicalName");
    }

    [Fact]
    public void Post_bodies_become_the_sdk_requests()
    {
        var delete = AuditEndpoints.ToActionRequest("DeleteRecordChangeHistory",
            JsonNode.Parse($$"""{ "Target": { "@odata.type": "Microsoft.Dynamics.CRM.account", "accountid": "{{Id}}" } }""")!.AsObject(), Store());
        delete.RequestName.Should().Be("DeleteRecordChangeHistory");
        delete["Target"].Should().BeEquivalentTo(new EntityReference("account", Id));

        var data = AuditEndpoints.ToActionRequest("DeleteAuditData",
            JsonNode.Parse("""{ "EndDate": "2026-06-30T00:00:00+02:00" }""")!.AsObject(), Store());
        data.RequestName.Should().Be("DeleteAuditData");
        data["EndDate"].Should().Be(new DateTime(2026, 6, 29, 22, 0, 0, DateTimeKind.Utc));

        Refused(() => AuditEndpoints.ToActionRequest("DeleteAuditData", JsonNode.Parse("""{ "EndDate": "soon" }""")!.AsObject(), Store()))
            .Message.Should().Contain("EndDate");
        Refused(() => AuditEndpoints.ToActionRequest("DeleteAuditData", JsonNode.Parse("""{ "Until": "2026-06-30T00:00:00Z" }""")!.AsObject(), Store()))
            .Message.Should().Contain("Until");
    }

    [Fact]
    public void Attribute_audit_detail_json_follows_the_learn_sample()
    {
        var detail = new AttributeAuditDetail
        {
            OldValue = new Entity("account"),
            NewValue = new Entity("account") { ["parentaccountid"] = new EntityReference("account", Other) { Name = "A. Datum" }, ["description"] = "new" },
        };
        detail.NewValue.FormattedValues["parentaccountid"] = "A. Datum";
        detail.DeletedAttributes[0] = "new_gone";

        var json = AuditEndpoints.ToJson(detail, Store(), "https://host", includeFormattedValues: true);

        json["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.AttributeAuditDetail");
        json["InvalidNewValueAttributes"]!.AsArray().Should().BeEmpty();
        json["LocLabelLanguageCode"]!.GetValue<int>().Should().Be(0);
        json["DeletedAttributes"]!["Count"]!.GetValue<int>().Should().Be(1);
        json["DeletedAttributes"]!["Keys"]![0]!.GetValue<int>().Should().Be(0);
        json["DeletedAttributes"]!["Values"]![0]!.GetValue<string>().Should().Be("new_gone");
        json["OldValue"]!.AsObject().Select(p => p.Key).Should().Equal("@odata.type");
        json["OldValue"]!["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.account");
        var newValue = json["NewValue"]!;
        newValue["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.account");
        newValue["description"]!.GetValue<string>().Should().Be("new");
        newValue["_parentaccountid_value"]!.GetValue<string>().Should().Be(Other.ToString());
        newValue["_parentaccountid_value@OData.Community.Display.V1.FormattedValue"]!.GetValue<string>().Should().Be("A. Datum");
        newValue["_parentaccountid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be("account");
        json.ContainsKey("AuditRecord").Should().BeFalse("the Web API does not return the audit record");
    }

    [Fact]
    public void Other_audit_detail_types_have_their_json()
    {
        var store = Store();
        var relationship = new RelationshipAuditDetail { RelationshipName = "new_account_tag" };
        relationship.TargetRecords.Add(new EntityReference("account", Other) { Name = "x" });
        var r = AuditEndpoints.ToJson(relationship, store, "https://host");
        r["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.RelationshipAuditDetail");
        r["RelationshipName"]!.GetValue<string>().Should().Be("new_account_tag");
        r["TargetRecords"]![0]!["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.account");
        r["TargetRecords"]![0]!["accountid"]!.GetValue<string>().Should().Be(Other.ToString());

        var share = AuditEndpoints.ToJson(new ShareAuditDetail
        {
            Principal = new EntityReference("systemuser", Other), OldPrivileges = AccessRights.None,
            NewPrivileges = AccessRights.ReadAccess | AccessRights.WriteAccess,
        }, store, "https://host");
        share["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.ShareAuditDetail");
        share["OldPrivileges"]!.GetValue<string>().Should().Be("None");
        share["NewPrivileges"]!.GetValue<string>().Should().Be("ReadAccess, WriteAccess");
        share["Principal"]!["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.systemuser");
        share["Principal"]!["systemuserid"]!.GetValue<string>().Should().Be(Other.ToString());

        var role = AuditEndpoints.ToJson(new RolePrivilegeAuditDetail
        {
            OldRolePrivileges = [], NewRolePrivileges = [new RolePrivilege(3, Id, Other)],
        }, store, "https://host");
        role["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.RolePrivilegeAuditDetail");
        role["OldRolePrivileges"]!.AsArray().Should().BeEmpty();
        role["NewRolePrivileges"]![0]!["Depth"]!.GetValue<string>().Should().Be("Global");
        role["NewRolePrivileges"]![0]!["PrivilegeId"]!.GetValue<string>().Should().Be(Id.ToString());
        role["NewRolePrivileges"]![0]!["BusinessUnitId"]!.GetValue<string>().Should().Be(Other.ToString());
        role["InvalidNewPrivileges"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Deleted_table_values_carry_their_type_only()
    {
        var detail = new AttributeAuditDetail { OldValue = new Entity("new_gone"), NewValue = new Entity("new_gone") };
        var json = AuditEndpoints.ToJson(detail, Store(), "https://host");
        json["OldValue"]!.AsObject().Select(p => p.Key).Should().Equal("@odata.type");
        json["NewValue"]!["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.new_gone");
    }

    [Fact]
    public void Metadata_declares_the_audit_operations()
    {
        var model = new ODataEdmModelBuilder(Store(), TestCustomApis.None()).Build();

        model.Validate(out var errors).Should().BeTrue(string.Join("; ", errors.Select(e => e.ErrorMessage)));
        var details = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.RetrieveAuditDetails").Should().ContainSingle().Which;
        details.IsFunction().Should().BeTrue();
        details.IsBound.Should().BeTrue();
        details.Parameters.First().Type.Definition.Should().Be(model.FindDeclaredType("Microsoft.Dynamics.CRM.audit"));
        details.GetReturn().Type.FullName().Should().Be("Microsoft.Dynamics.CRM.RetrieveAuditDetailsResponse");

        var record = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.RetrieveRecordChangeHistory").Should().ContainSingle().Which;
        record.IsFunction().Should().BeTrue();
        record.Parameters.Select(p => p.Name).Should().Equal("Target", "PagingInfo");
        model.FindDeclaredOperations("Microsoft.Dynamics.CRM.RetrieveAttributeChangeHistory").Should().ContainSingle()
            .Which.Parameters.Select(p => p.Name).Should().Equal("Target", "AttributeLogicalName", "PagingInfo");
        var deleteHistory = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.DeleteRecordChangeHistory").Should().ContainSingle().Which;
        deleteHistory.IsAction().Should().BeTrue();
        deleteHistory.GetReturn().Type.FullName().Should().Be("Microsoft.Dynamics.CRM.DeleteRecordChangeHistoryResponse");
        var deleteData = model.FindDeclaredOperations("Microsoft.Dynamics.CRM.DeleteAuditData").Should().ContainSingle().Which;
        deleteData.Parameters.Single().Type.FullName().Should().Be("Edm.DateTimeOffset");

        foreach (var type in new[] { "AuditDetail", "AttributeAuditDetail", "RelationshipAuditDetail", "ShareAuditDetail",
                     "RolePrivilegeAuditDetail", "AuditDetailCollection", "PagingInfo" })
            model.FindDeclaredType("Microsoft.Dynamics.CRM." + type).Should().NotBeNull(type);
        ((IEdmComplexType)model.FindDeclaredType("Microsoft.Dynamics.CRM.AttributeAuditDetail")!).BaseComplexType().Name.Should().Be("AuditDetail");
        model.EntityContainer.FindOperationImports("RetrieveRecordChangeHistory").Should().ContainSingle();
        model.EntityContainer.FindOperationImports("DeleteAuditData").Should().ContainSingle();
    }

    [Fact]
    public void Every_unbound_audit_operation_name_is_a_reserved_entity_set_name()
    {
        AuditEndpoints.OperationNames.Should().BeEquivalentTo(
            ["RetrieveRecordChangeHistory", "RetrieveAttributeChangeHistory", "DeleteRecordChangeHistory", "DeleteAuditData"]);
        foreach (var name in AuditEndpoints.OperationNames)
            LogicalNameValidator.ReservedEntitySetNames.Contains(name.ToLowerInvariant()).Should().BeTrue($"'{name}' is reserved");
    }
}

using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The seeded <c>audit</c> table and the organization audit settings (plan 13, task 1): columns, options, relationships,
/// the read-only rule, the read privilege, the organization update rules and the Web API view without <c>changedata</c>.
/// </summary>
[Collection("xrmd")]
public sealed class AuditTableTests(XrmdFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>The tests that grant audit privileges to a role take them back (the catalog test counts them over the shared database).</summary>
    public Task DisposeAsync() => SecurityTestHelper.RevokeAuditPrivilegesAsync(fx);

    private const string Api = "/api/data/v9.2/";
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<HttpResponseMessage> GetAsync(string pathAndQuery, Guid? oid = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + pathAndQuery);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid ?? fx.AdminObjectId, fx.BaseUrl));
        return fx.Client.SendAsync(req);
    }

    private async Task<Guid> OrganizationIdAsync() =>
        (await Admin.RetrieveMultipleAsync(new QueryExpression("organization") { ColumnSet = new ColumnSet("organizationid") })).Entities.Single().Id;

    private static IEnumerable<int> OptionValues(AttributeMetadata a) =>
        ((EnumAttributeMetadata)a).OptionSet!.Options.Select(o => o.Value!.Value);

    [Fact]
    public void Audit_table_is_seeded_with_learn_columns()
    {
        var audit = Store.GetEntity("audit")!;
        audit.Should().NotBeNull();
        audit.EntitySetName.Should().Be("audits");
        audit.SchemaName.Should().Be("Audit");
        audit.ObjectTypeCode.Should().Be(4567);
        audit.OwnershipType.Should().Be(OwnershipTypes.None);
        audit.PrimaryIdAttribute.Should().Be("auditid");
        audit.PrimaryNameAttribute.Should().BeNull();

        AttributeMetadata Column(string name) => audit.Attributes.Single(a => a.LogicalName == name);
        Column("action").Should().BeOfType<PicklistAttributeMetadata>();
        Column("operation").Should().BeOfType<PicklistAttributeMetadata>();
        Column("objectid").Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().BeEmpty();
        Column("regardingobjectid").Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().BeEmpty();
        Column("userid").Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().Equal("systemuser");
        Column("callinguserid").Should().BeOfType<LookupAttributeMetadata>().Which.Targets.Should().Equal("systemuser");
        Column("objecttypecode").Should().BeOfType<StringAttributeMetadata>();
        Column("createdon").Should().BeOfType<DateTimeAttributeMetadata>();
        Column("changedata").Should().BeOfType<MemoAttributeMetadata>().Which.MaxLength.Should().Be(2000);
        Column("attributemask").Should().BeOfType<MemoAttributeMetadata>().Which.MaxLength.Should().Be(2000);
        Column("additionalinfo").Should().BeOfType<MemoAttributeMetadata>().Which.MaxLength.Should().Be(2000);
        Column("transactionid").Should().BeOfType<UniqueIdentifierAttributeMetadata>();
        Column("timetoliveinseconds").Should().BeOfType<IntegerAttributeMetadata>();
        Column("useradditionalinfo").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(350);

        OptionValues(Column("action")).Should().Contain([1, 2, 3, 13, 14, 33, 34, 41, 48, 49, 53, 54, 55, 56, 57, 58, 59]);
        OptionValues(Column("operation")).Should().BeEquivalentTo([1, 2, 3, 4, 5, 115, 116, 117, 118, 200]);
        ((EnumAttributeMetadata)Column("action")).OptionSet!.Name.Should().Be("audit_action");
        ((EnumAttributeMetadata)Column("operation")).OptionSet!.Name.Should().Be("audit_operation");
        Store.GetOptionSet("audit_action").Should().NotBeNull();
        Store.GetOptionSet("audit_operation").Should().NotBeNull();

        var relationships = Store.GetOneToManyRelationships();
        foreach (var (name, column) in new[] { ("lk_audit_userid", "userid"), ("lk_audit_callinguserid", "callinguserid") })
        {
            var r = relationships.Single(x => x.SchemaName == name);
            r.ReferencedEntity.Should().Be("systemuser");
            r.ReferencingEntity.Should().Be("audit");
            r.ReferencingAttribute.Should().Be(column);
            r.CascadeConfiguration.Delete.Should().Be(CascadeType.NoCascade);
            r.CascadeConfiguration.Assign.Should().Be(CascadeType.NoCascade);
        }
    }

    [Fact]
    public async Task Organization_has_audit_settings_with_defaults()
    {
        var org = Store.GetEntity("organization")!;
        org.Attributes.Single(a => a.LogicalName == "isauditenabled").Should().BeOfType<BooleanAttributeMetadata>();
        org.Attributes.Single(a => a.LogicalName == "isuseraccessauditenabled").Should().BeOfType<BooleanAttributeMetadata>();
        org.Attributes.Single(a => a.LogicalName == "auditretentionperiodv2").Should().BeOfType<IntegerAttributeMetadata>();
        org.Attributes.Single(a => a.LogicalName == "useraccessauditinginterval").Should().BeOfType<IntegerAttributeMetadata>();
        org.Attributes.Single(a => a.LogicalName == "auditsettings").Should().BeOfType<StringAttributeMetadata>();

        async Task AssertDefaultsAsync()
        {
            var row = (await Admin.RetrieveMultipleAsync(new QueryExpression("organization")
            {
                ColumnSet = new ColumnSet("isauditenabled", "auditretentionperiodv2", "isuseraccessauditenabled", "useraccessauditinginterval", "auditsettings"),
            })).Entities.Single();
            row.GetAttributeValue<bool>("isauditenabled").Should().BeFalse();
            row.GetAttributeValue<int>("auditretentionperiodv2").Should().Be(30);
            row.GetAttributeValue<bool>("isuseraccessauditenabled").Should().BeFalse();
            row.GetAttributeValue<int>("useraccessauditinginterval").Should().Be(4);
            row.GetAttributeValue<string>("auditsettings").Should().BeNull();
        }
        await AssertDefaultsAsync();

        // An organization made before this plan has the columns empty: the seed fills them again.
        await ExecuteAsync("UPDATE data.organization SET isauditenabled = NULL, auditretentionperiodv2 = NULL, isuseraccessauditenabled = NULL, useraccessauditinginterval = NULL");
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await new SystemSeed(Store, factory).EnsureAsync(new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), fx.AdminObjectId, XrmdFixture.AdminName));
        await AssertDefaultsAsync();
    }

    [Fact]
    public async Task Audit_table_is_read_only_and_needs_read_audit_summary()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO data.audit (auditid, action, operation, objecttypecode, createdon) VALUES ('{id}', 1, 1, N'contact', SYSUTCDATETIME())");

        var create = async () => await Admin.CreateAsync(new Entity("audit") { ["action"] = new OptionSetValue(1) });
        (await create.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
        var update = async () => await Admin.UpdateAsync(new Entity("audit", id) { ["useradditionalinfo"] = "x" });
        (await update.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
        var delete = async () => await Admin.DeleteAsync("audit", id);
        (await delete.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.audit WHERE auditid = '{id}'")).Should().Be(1);

        var query = new QueryExpression("audit") { ColumnSet = new ColumnSet("action") };
        query.Criteria.AddCondition("auditid", ConditionOperator.Equal, id);

        // A user with full access to the records of the organization but without prvReadAuditSummary.
        var businessUnit = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, businessUnit, oid);
        await GrantAsync(fx, user, false, ("prvReadContact", DepthMask.Global), ("prvReadSystemUser", DepthMask.Global));
        var denied = async () => await ServiceFor(fx, oid).RetrieveMultipleAsync(query);
        var failure = (await denied.Should().ThrowAsync<XrmdFaultException>()).Which;
        failure.HttpStatus.Should().Be(403);
        failure.Message.Should().Contain("prvReadAuditSummary");
        (await GetAsync("audits", oid)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync("audits?$expand=userid($select=fullname)", oid)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await GrantAsync(fx, user, false, (PrivilegeNames.ReadAuditSummary, DepthMask.Global));
        (await ServiceFor(fx, oid).RetrieveMultipleAsync(query)).Entities.Select(e => e.Id).Should().Equal(id);
        (await GetAsync("audits", oid)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Audit_privileges_are_in_the_catalog_and_held_by_system_administrator_only()
    {
        PrivilegeNames.Audit.Should().BeEquivalentTo("prvReadAuditSummary", "prvReadRecordAuditHistory", "prvDeleteRecordChangeHistory", "prvDeleteAuditPartitions");
        PrivilegeNames.ReadAuditSummary.Should().Be("prvReadAuditSummary");
        PrivilegeNames.ReadRecordAuditHistory.Should().Be("prvReadRecordAuditHistory");
        PrivilegeNames.DeleteRecordChangeHistory.Should().Be("prvDeleteRecordChangeHistory");
        PrivilegeNames.DeleteAuditPartitions.Should().Be("prvDeleteAuditPartitions");
        PrivilegeNames.NonEntity.Should().Contain(PrivilegeNames.Audit);
        PrivilegeNames.For(AccessRights.ReadAccess, Store.GetEntity("audit")!).Should().Be("prvReadAuditSummary");

        foreach (var name in PrivilegeNames.Audit)
        {
            (await ScalarAsync($"SELECT COUNT(*) FROM data.privilege WHERE name = '{name}' AND privilegeid = '{PrivilegeNames.IdOf(name)}' " +
                               "AND accessright = 0 AND canbeglobal = 1 AND canbebasic = 0 AND canbelocal = 0 AND canbedeep = 0")).Should().Be(1, name);
            (await ScalarAsync($"SELECT COUNT(*) FROM data.roleprivileges WHERE privilegeid = '{PrivilegeNames.IdOf(name)}'")).Should().Be(0, name);
        }
        // No entity privileges for the table itself.
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilege WHERE name LIKE 'prv%Audit' AND name <> 'prvReadAuditSummary' AND name NOT IN ('prvDeleteAuditPartitions')")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.privilegeobjecttypecodes WHERE objecttypecode = 'audit'")).Should().Be(0);

        var cache = fx.App.Services.GetRequiredService<SecurityCache>();
        var admin = await cache.GetProfileAsync(await ScalarGuidAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = '" + fx.AdminObjectId + "'"), default);
        foreach (var name in PrivilegeNames.Audit) admin.DepthOf(name).Should().Be(DepthMask.Global, name);
    }

    private async Task<Guid> ScalarGuidAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Organization_refuses_user_access_auditing_and_bad_retention()
    {
        var org = await OrganizationIdAsync();
        async Task<XrmdFaultException> RefusedAsync(string column, object? value)
        {
            var act = async () => await Admin.UpdateAsync(new Entity("organization", org) { [column] = value });
            var failure = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
            failure.HttpStatus.Should().Be(400);
            return failure;
        }

        (await RefusedAsync("isuseraccessauditenabled", true)).Message.Should().Contain("isuseraccessauditenabled");
        (await RefusedAsync("auditretentionperiodv2", 0)).Message.Should().Contain("auditretentionperiodv2");
        await RefusedAsync("auditretentionperiodv2", 400000);
        await RefusedAsync("auditretentionperiodv2", -2);
        await RefusedAsync("auditsettings", "{\"StoreLabelNameforPicklistAudits\":true}");
        await RefusedAsync("auditsettings", "{\"Other\":1}");
        await RefusedAsync("auditsettings", "not json");

        try
        {
            await Admin.UpdateAsync(new Entity("organization", org) { ["isuseraccessauditenabled"] = false });
            await Admin.UpdateAsync(new Entity("organization", org) { ["auditretentionperiodv2"] = -1 });
            (await ScalarAsync("SELECT auditretentionperiodv2 FROM data.organization")).Should().Be(-1);
            await Admin.UpdateAsync(new Entity("organization", org) { ["auditretentionperiodv2"] = 365000 });
            (await ScalarAsync("SELECT auditretentionperiodv2 FROM data.organization")).Should().Be(365000);
            await Admin.UpdateAsync(new Entity("organization", org) { ["auditsettings"] = "{\"StoreLabelNameforPicklistAudits\":false}" });
            await Admin.UpdateAsync(new Entity("organization", org) { ["auditsettings"] = null });
        }
        finally
        {
            await ExecuteAsync("UPDATE data.organization SET auditretentionperiodv2 = 30, auditsettings = NULL");
        }
    }

    [Fact]
    public async Task Web_api_audit_has_no_changedata()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO data.audit (auditid, action, operation, objecttypecode, changedata, createdon) VALUES ('{id}', 2, 2, N'contact', N'{{\"v\":1}}', SYSUTCDATETIME())");

        var metadata = await (await GetAsync("$metadata")).Content.ReadAsStringAsync();
        metadata.Should().Contain("Name=\"audit\"").And.Contain("Name=\"objecttypecode\"");
        var auditType = metadata[metadata.IndexOf("EntityType Name=\"audit\"", StringComparison.Ordinal)..];
        auditType = auditType[..auditType.IndexOf("</EntityType>", StringComparison.Ordinal)];
        auditType.Should().NotContain("changedata").And.Contain("attributemask");

        var select = await GetAsync("audits?$select=changedata");
        select.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var list = await GetAsync($"audits?$filter=auditid eq {id}");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = JsonNode.Parse(await list.Content.ReadAsStringAsync())!["value"]!.AsArray().Single()!.AsObject();
        row.ContainsKey("changedata").Should().BeFalse();
        row["action"]!.GetValue<int>().Should().Be(2);

        var one = await GetAsync($"audits({id})");
        one.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await one.Content.ReadAsStringAsync())!.AsObject().ContainsKey("changedata").Should().BeFalse();

        // Plan 2.11 review m1: a single record's $select of changedata keeps the audit wording (not the party list one).
        var selectOne = await GetAsync($"audits({id})?$select=changedata");
        selectOne.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await selectOne.Content.ReadAsStringAsync()).Should().Contain("audit messages").And.NotContain("party");
    }

    [Fact]
    public async Task Web_api_fetchxml_never_writes_changedata_and_refuses_naming_it()
    {
        var id = Guid.NewGuid();
        var user = await ScalarGuidAsync($"SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = '{fx.AdminObjectId}'");
        await ExecuteAsync($"""
            INSERT INTO data.audit (auditid, action, operation, objecttypecode, userid, changedata, createdon)
            VALUES ('{id}', 2, 2, N'contact', '{user}', N'secret-change-data', SYSUTCDATETIME())
            """);

        Task<HttpResponseMessage> FetchAsync(string set, string fetch) => GetAsync($"{set}?fetchXml={Uri.EscapeDataString(fetch)}");

        // A link-entity with all its columns: the hidden column comes back from the query but is never written.
        var linked = await FetchAsync("systemusers", $"""
            <fetch><entity name="systemuser"><attribute name="systemuserid"/>
            <filter><condition attribute="systemuserid" operator="eq" value="{user}"/></filter>
            <link-entity name="audit" from="userid" to="systemuserid" alias="a"><all-attributes/>
            <filter><condition attribute="auditid" operator="eq" value="{id}"/></filter></link-entity></entity></fetch>
            """);
        var linkedBody = await linked.Content.ReadAsStringAsync();
        linked.StatusCode.Should().Be(HttpStatusCode.OK, linkedBody);
        var linkedRow = JsonNode.Parse(linkedBody)!["value"]!.AsArray().Single()!.AsObject();
        linkedRow["a.action"]!.GetValue<int>().Should().Be(2);
        linkedRow.Select(p => p.Key).Should().NotContain(k => k.Contains("changedata"));
        linkedBody.Should().NotContain("secret-change-data");

        // The root with all its columns: absent too.
        var all = await FetchAsync("audits", $"""
            <fetch><entity name="audit"><all-attributes/><filter><condition attribute="auditid" operator="eq" value="{id}"/></filter></entity></fetch>
            """);
        var allBody = await all.Content.ReadAsStringAsync();
        all.StatusCode.Should().Be(HttpStatusCode.OK, allBody);
        allBody.Should().NotContain("changedata").And.NotContain("secret-change-data");

        // Naming the hidden column is refused, as $select, $filter and $orderby refuse it.
        string[] refused =
        [
            $"""<fetch><entity name="audit"><attribute name="auditid"/><attribute name="changedata" alias="cd"/></entity></fetch>""",
            $"""<fetch><entity name="audit"><attribute name="auditid"/><filter><condition attribute="changedata" operator="like" value="%secret%"/></filter></entity></fetch>""",
            $"""<fetch><entity name="audit"><attribute name="auditid"/><order attribute="changedata"/></entity></fetch>""",
            $"""<fetch aggregate="true"><entity name="audit"><attribute name="changedata" aggregate="countcolumn" alias="n"/></entity></fetch>""",
        ];
        foreach (var fetch in refused)
        {
            var res = await FetchAsync("audits", fetch);
            var body = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, fetch);
            body.Should().Contain("changedata", fetch);
        }
        string[] refusedLinked =
        [
            $"""<fetch><entity name="systemuser"><attribute name="systemuserid"/><link-entity name="audit" from="userid" to="systemuserid" alias="a"><attribute name="changedata"/></link-entity></entity></fetch>""",
            $"""<fetch><entity name="systemuser"><attribute name="systemuserid"/><link-entity name="audit" from="userid" to="systemuserid" alias="a"><filter><condition attribute="changedata" operator="like" value="%secret%"/></filter></link-entity></entity></fetch>""",
            $"""<fetch><entity name="systemuser"><attribute name="systemuserid"/><filter><condition entityname="a" attribute="changedata" operator="like" value="%secret%"/></filter><link-entity name="audit" from="userid" to="systemuserid" alias="a"/></entity></fetch>""",
        ];
        foreach (var fetch in refusedLinked)
        {
            var res = await FetchAsync("systemusers", fetch);
            var body = await res.Content.ReadAsStringAsync();
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, fetch);
            body.Should().Contain("changedata", fetch);
        }

        // The SDK path still reads the column (it is part of the metadata there).
        var sdk = await Admin.RetrieveMultipleAsync(new FetchExpression($"""
            <fetch><entity name="audit"><attribute name="changedata"/><filter><condition attribute="auditid" operator="eq" value="{id}"/></filter></entity></fetch>
            """));
        sdk.Entities.Single().GetAttributeValue<string>("changedata").Should().Be("secret-change-data");
    }

    [Fact]
    public async Task Audit_lookup_indexes_exist_after_init_and_after_a_second_upgrade()
    {
        async Task<string?> KeysAsync(string name) =>
            await TestSql.IndexAsync(fx.ConnectionString, "data", "audit", name) is { } index ? string.Join(",", index.Keys) : null;
        async Task AssertIndexesAsync(string when)
        {
            (await KeysAsync("IX_audit_objectid")).Should().Be("objectid,objecttypecode,createdon DESC", when);
            (await KeysAsync("IX_audit_createdon")).Should().Be("createdon", when);
        }

        await AssertIndexesAsync("after init");

        // A database made before the indexes: the next startup creates them, and the one after changes nothing.
        await ExecuteAsync(TestSql.Dialect.DropIndexIfExists("data", "audit", "IX_audit_objectid"));
        await ExecuteAsync(TestSql.Dialect.DropIndexIfExists("data", "audit", "IX_audit_createdon"));
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        var seed = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), fx.AdminObjectId, XrmdFixture.AdminName);
        await new SystemSeed(Store, factory).EnsureAsync(seed);
        await AssertIndexesAsync("after the upgrade");
        await new SystemSeed(Store, factory).EnsureAsync(seed);
        await AssertIndexesAsync("after a second upgrade");
    }

    [Fact]
    public async Task Fetch_on_audit_objecttypecode_accepts_type_code_or_name()
    {
        var contact = Guid.NewGuid();
        var account = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO data.audit (auditid, action, operation, objecttypecode, createdon, timetoliveinseconds) VALUES
            ('{contact}', 1, 1, N'contact', SYSUTCDATETIME(), 777001), ('{account}', 1, 1, N'account', SYSUTCDATETIME(), 777001)
            """);
        Store.GetEntity("contact")!.ObjectTypeCode.Should().Be(2);

        async Task<Guid[]> FetchAsync(string value)
        {
            var fetch = $"""
                <fetch><entity name="audit"><attribute name="auditid"/>
                <filter><condition attribute="timetoliveinseconds" operator="eq" value="777001"/>
                <condition attribute="objecttypecode" operator="eq" value="{value}"/></filter></entity></fetch>
                """;
            return (await Admin.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities.Select(e => e.Id).Order().ToArray();
        }

        var byCode = await FetchAsync("2");
        byCode.Should().Equal(contact);
        (await FetchAsync("contact")).Should().Equal(byCode);
        (await FetchAsync("1")).Should().Equal(account);
        var unknown = async () => await FetchAsync("999999");
        (await unknown.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Audit_has_only_auditid_and_createdon_of_the_generic_columns()
    {
        string[] absent = ["statecode", "statuscode", "modifiedon", "modifiedby", "createdby", "owninguser", "owningteam", "owningbusinessunit", "versionnumber"];
        var audit = Store.GetEntity("audit")!;
        audit.Attributes.Select(a => a.LogicalName).Should().Contain(["auditid", "createdon"]).And.NotContain(absent);

        var metadata = await (await GetAsync("$metadata")).Content.ReadAsStringAsync();
        var type = metadata[metadata.IndexOf("EntityType Name=\"audit\"", StringComparison.Ordinal)..];
        type = type[..type.IndexOf("</EntityType>", StringComparison.Ordinal)];
        type.Should().Contain("Name=\"createdon\"").And.NotContain("statecode").And.NotContain("modifiedon");

        var definition = await GetAsync("EntityDefinitions(LogicalName='audit')?$expand=Attributes($select=LogicalName)");
        definition.StatusCode.Should().Be(HttpStatusCode.OK);
        var names = JsonNode.Parse(await definition.Content.ReadAsStringAsync())!["Attributes"]!.AsArray().Select(a => a!["LogicalName"]!.GetValue<string>()).ToList();
        names.Should().Contain("createdon").And.NotContain("statecode").And.NotContain("modifiedon");

        (await GetAsync("audits?$select=statecode")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync("audits?$filter=changedata eq 'x'")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync("audits?$orderby=changedata")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static string TextOf(OptionSetMetadata set, int value) =>
        (set.Options.Single(o => o.Value == value).Label.UserLocalizedLabel ?? set.Options.Single(o => o.Value == value).Label.LocalizedLabels.First()).Label;

    [Fact]
    public async Task Seed_keeps_an_edited_audit_action_label_and_its_options()
    {
        var before = (OptionSetMetadata)Store.GetOptionSet("audit_action")!;
        var count = before.Options.Count;
        var original = TextOf(before, 1);
        var changes = new OptionSetMetadata { Name = "audit_action", IsGlobal = true, OptionSetType = OptionSetType.Picklist };
        changes.Options.Add(new OptionMetadata(new Label("Created here", 1033), 1));
        await Admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.UpdateOptionSetRequest { OptionSet = changes });
        try
        {
            var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
            await new SystemSeed(Store, factory).EnsureAsync(new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), fx.AdminObjectId, XrmdFixture.AdminName));

            var after = (OptionSetMetadata)Store.GetOptionSet("audit_action")!;
            after.Options.Should().HaveCount(count);
            TextOf(after, 1).Should().Be("Created here");
        }
        finally
        {
            var restore = new OptionSetMetadata { Name = "audit_action", IsGlobal = true, OptionSetType = OptionSetType.Picklist };
            restore.Options.Add(new OptionMetadata(new Label(original, 1033), 1));
            await Admin.ExecuteAsync(new Microsoft.Xrm.Sdk.Messages.UpdateOptionSetRequest { OptionSet = restore });
        }
    }
}

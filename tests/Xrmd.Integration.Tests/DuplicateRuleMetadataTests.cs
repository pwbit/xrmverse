using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.15, task 1: the duplicate detection seed tables and organization settings, the table flag
/// <c>IsDuplicateDetectionEnabled</c>, the default state of a new rule (D3, RF5) and the rules of rule and condition writes
/// (D4 to D6).
/// </summary>
[Collection("xrmd")]
public sealed class DuplicateRuleMetadataTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private string Name(string label) => $"new_{label}{_run}";

    private static CreateEntityRequest CreateEntity(string logicalName, Action<EntityMetadata>? configure = null)
    {
        var entity = TestHelpers.Entity(logicalName, logicalName, "Table " + logicalName);
        configure?.Invoke(entity);
        return new CreateEntityRequest
        {
            Entity = entity,
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
        };
    }

    private async Task<EntityMetadata> RetrieveEntityAsync(string name) =>
        ((RetrieveEntityResponse)await Admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = name, EntityFilters = EntityFilters.Entity })).EntityMetadata;

    private Task SetFlagAsync(string name, bool value) =>
        Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name, IsDuplicateDetectionEnabled = new BooleanManagedProperty(value) } });

    private async Task AddAttributeAsync(string entity, AttributeMetadata attribute) =>
        await Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = entity, Attribute = attribute });

    private async Task AddLookupAsync(string entity, string target, string name) =>
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = $"{entity}_{name}", ReferencedEntity = target, ReferencingEntity = entity,
                CascadeConfiguration = new CascadeConfiguration { Delete = CascadeType.RemoveLink },
            },
            Lookup = new LookupAttributeMetadata { LogicalName = name, SchemaName = name, DisplayName = Label("Lookup") },
        });

    /// <summary>A custom table with duplicate detection on, a text, a date and a lookup to account.</summary>
    private async Task<string> EnabledTableAsync(string label)
    {
        var name = Name(label);
        await Admin.ExecuteAsync(CreateEntity(name, e => e.IsDuplicateDetectionEnabled = new BooleanManagedProperty(true)));
        await AddAttributeAsync(name, new StringAttributeMetadata { LogicalName = "new_code", SchemaName = "new_Code", MaxLength = 50, DisplayName = Label("Code") });
        await AddAttributeAsync(name, new DateTimeAttributeMetadata { LogicalName = "new_when", SchemaName = "new_When", Format = DateTimeFormat.DateAndTime, DisplayName = Label("When") });
        await AddLookupAsync(name, "account", "new_accountid");
        return name;
    }

    private static Entity Rule(string baseTable, string matchingTable, string name = "Rule") => new(DuplicateDetectionSchema.Rule)
    {
        ["name"] = name, ["baseentityname"] = baseTable, ["matchingentityname"] = matchingTable,
    };

    private static Entity Condition(Guid rule, string baseAttribute, string matchingAttribute, int operatorCode, int? param = null)
    {
        var condition = new Entity(DuplicateDetectionSchema.Condition)
        {
            ["regardingobjectid"] = new EntityReference(DuplicateDetectionSchema.Rule, rule),
            ["baseattributename"] = baseAttribute, ["matchingattributename"] = matchingAttribute,
            ["operatorcode"] = new OptionSetValue(operatorCode),
        };
        if (param is { } p) condition["operatorparam"] = p;
        return condition;
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.AddParameter(name, value);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    // ---- seed ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Seed_creates_duplicate_tables()
    {
        var rule = Store.GetEntity("duplicaterule")!;
        var condition = Store.GetEntity("duplicaterulecondition")!;
        var record = Store.GetEntity("duplicaterecord")!;
        (rule.EntitySetName, rule.ObjectTypeCode, rule.OwnershipType, rule.PrimaryIdAttribute, rule.PrimaryNameAttribute)
            .Should().Be(("duplicaterules", 4414, OwnershipTypes.UserOwned, "duplicateruleid", "name"));
        (condition.EntitySetName, condition.ObjectTypeCode, condition.OwnershipType, condition.PrimaryIdAttribute)
            .Should().Be(("duplicateruleconditions", 4416, OwnershipTypes.OrganizationOwned, "duplicateruleconditionid"));
        (record.EntitySetName, record.ObjectTypeCode, record.OwnershipType, record.PrimaryIdAttribute)
            .Should().Be(("duplicaterecords", 4415, OwnershipTypes.UserOwned, "duplicateid"));
        record.Attributes.Single(a => a.LogicalName == "duplicaterecordid").Should().BeOfType<LookupAttributeMetadata>();
        TypeMapping.ToColumns(record.Attributes.Single(a => a.LogicalName == "baserecordid")).Select(c => c.Name)
            .Should().Equal("baserecordid", "baserecordidtype");

        var cascade = ((OneToManyRelationshipMetadata)Store.GetRelationship("DuplicateRule_DuplicateRuleConditions")!).CascadeConfiguration;
        cascade.Delete.Should().Be(CascadeType.Cascade);
        foreach (var other in new[] { cascade.Assign, cascade.Merge, cascade.Reparent, cascade.Share, cascade.Unshare })
            other.Should().Be(CascadeType.NoCascade);
        ((OneToManyRelationshipMetadata)Store.GetRelationship("DuplicateRule_DuplicateBaseRecord")!).CascadeConfiguration.Delete.Should().Be(CascadeType.RemoveLink);
        ((OneToManyRelationshipMetadata)Store.GetRelationship("AsyncOperation_DuplicateBaseRecord")!).CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);

        var states = ((StateAttributeMetadata)rule.Attributes.Single(a => a.LogicalName == "statecode")).OptionSet.Options.Cast<StateOptionMetadata>()
            .Select(o => (o.Value, o.DefaultStatus));
        states.Should().Equal((0, 0), (1, 2));
        var statuses = ((StatusAttributeMetadata)rule.Attributes.Single(a => a.LogicalName == "statuscode")).OptionSet.Options.Cast<StatusOptionMetadata>()
            .Select(o => (o.Value, o.State));
        statuses.Should().Equal((0, 0), (1, 0), (2, 1));
        ((PicklistAttributeMetadata)condition.Attributes.Single(a => a.LogicalName == "operatorcode")).OptionSet.Options.Select(o => o.Value!.Value)
            .Should().Equal(0, 1, 2, 3, 4, 5, 6);
        ((PicklistAttributeMetadata)Store.GetEntity("asyncoperation")!.Attributes.Single(a => a.LogicalName == "operationtype")).OptionSet.Options
            .Select(o => o.Value!.Value).Should().Contain([7, 8]);

        foreach (var column in DuplicateDetectionSchema.OrganizationColumns)
        {
            var attribute = Store.GetEntity("organization")!.Attributes.Single(a => a.LogicalName == column);
            attribute.Should().BeOfType<BooleanAttributeMetadata>();
            attribute.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.SystemRequired);
            Convert.ToInt32(await ScalarAsync($"SELECT COUNT(*) FROM data.organization WHERE {column} IS NULL OR {column} <> {TestSql.False}")).Should().Be(0, column);
        }
    }

    [Fact]
    public async Task Older_organization_gets_duplicate_tables_settings_and_job_types()
    {
        var database = "xrmd_dd_" + Guid.NewGuid().ToString("N");
        await ScalarAsync($"CREATE DATABASE [{database}]");
        var factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(factory).MigrateAsync();
        var options = new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
        async Task<MetadataStore> NewStoreAsync()
        {
            var store = new MetadataStore(factory);
            await store.LoadAsync();
            return store;
        }
        await new SystemSeed(await NewStoreAsync(), factory).EnsureAsync(options);

        // An organization seeded before this plan: no duplicate tables, no job types 7 and 8, the settings empty.
        var seeded = await NewStoreAsync();
        var operationType = (PicklistAttributeMetadata)MetadataSerializer.DeserializeAttribute(
            MetadataSerializer.SerializeAttribute(seeded.GetEntity("asyncoperation")!.Attributes.Single(a => a.LogicalName == "operationtype")));
        foreach (var option in operationType.OptionSet.Options.Where(o => o.Value is 7 or 8).ToList()) operationType.OptionSet.Options.Remove(option);
        await using (var conn = await factory.OpenAsync())
        await using (var cmd = TestSql.Command(conn, """
            DELETE FROM meta.relationship WHERE schema_name IN ('DuplicateRule_DuplicateRuleConditions', 'DuplicateRule_DuplicateBaseRecord', 'AsyncOperation_DuplicateBaseRecord');
            DELETE FROM meta.attribute WHERE entity_logical_name IN ('duplicaterule', 'duplicaterulecondition', 'duplicaterecord');
            DELETE FROM meta.entity WHERE logical_name IN ('duplicaterule', 'duplicaterulecondition', 'duplicaterecord');
            DROP TABLE data.duplicaterecord; DROP TABLE data.duplicaterulecondition; DROP TABLE data.duplicaterule;
            UPDATE meta.attribute SET definition = @definition WHERE entity_logical_name = 'asyncoperation' AND logical_name = 'operationtype';
            UPDATE data.organization SET isduplicatedetectionenabled = NULL, isduplicatedetectionenabledforimport = NULL;
            """))
        {
            cmd.AddParameter("@definition", MetadataSerializer.SerializeAttribute(operationType));
            await cmd.ExecuteNonQueryAsync();
        }
        var stale = await NewStoreAsync();
        stale.GetEntity("duplicaterule").Should().BeNull();
        ((PicklistAttributeMetadata)stale.GetEntity("asyncoperation")!.Attributes.Single(a => a.LogicalName == "operationtype")).OptionSet.Options
            .Should().NotContain(o => o.Value == 7);

        await new SystemSeed(stale, factory).EnsureAsync(options);
        var upgraded = await NewStoreAsync();
        upgraded.GetEntity("duplicaterule")!.ObjectTypeCode.Should().Be(4414);
        upgraded.GetEntity("duplicaterecord")!.PrimaryIdAttribute.Should().Be("duplicateid");
        ((PicklistAttributeMetadata)upgraded.GetEntity("asyncoperation")!.Attributes.Single(a => a.LogicalName == "operationtype")).OptionSet.Options
            .Select(o => o.Value!.Value).Should().Contain([1, 7, 8, 10, 203]).And.BeInAscendingOrder();
        await using (var conn = await factory.OpenAsync())
        await using (var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.organization WHERE isduplicatedetectionenabled = 0 AND isduplicatedetectionenabledforimport = 0"))
            (Convert.ToInt32(await cmd.ExecuteScalarAsync())).Should().Be(1);
        var stamp = upgraded.VersionStamp;
        await new SystemSeed(upgraded, factory).EnsureAsync(options);
        upgraded.VersionStamp.Should().Be(stamp, "a second run changes nothing");
    }

    // ---- D3: the state of a new rule (RF5) -----------------------------------------------------------------------------

    [Fact]
    public async Task New_rule_is_unpublished()
    {
        await SetFlagAsync("contact", true);
        try
        {
            var id = await Admin.CreateAsync(Rule("contact", "contact", "Contacts " + _run));
            var rule = await Admin.RetrieveAsync("duplicaterule", id, new ColumnSet(true));
            rule.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
            rule.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(0);
            rule.GetAttributeValue<OptionSetValue>("baseentitytypecode").Value.Should().Be(2);
            rule.GetAttributeValue<OptionSetValue>("matchingentitytypecode").Value.Should().Be(2);
            rule.GetAttributeValue<string>("uniquename").Should().Be("duplicaterule_" + id.ToString("N"));
            rule.GetAttributeValue<bool>("excludeinactiverecords").Should().BeFalse();
            rule.GetAttributeValue<bool>("iscasesensitive").Should().BeFalse();

            var condition = await Admin.CreateAsync(Condition(id, "lastname", "lastname", 0));
            (await Admin.RetrieveAsync("duplicaterulecondition", condition, new ColumnSet("uniquerulename", "ignoreblankvalues")))
                .GetAttributeValue<string>("uniquerulename").Should().Be("duplicaterule_" + id.ToString("N"));

            // A new unique name reaches the conditions.
            await Admin.UpdateAsync(new Entity("duplicaterule", id) { ["uniquename"] = "contacts_" + _run });
            (await Admin.RetrieveAsync("duplicaterulecondition", condition, new ColumnSet("uniquerulename")))
                .GetAttributeValue<string>("uniquerulename").Should().Be("contacts_" + _run);
            await Admin.DeleteAsync("duplicaterule", id);
        }
        finally
        {
            await SetFlagAsync("contact", false);
        }
    }

    [Fact]
    public async Task Account_create_still_defaults_status_1()
    {
        var id = await Admin.CreateAsync(new Entity("account") { ["name"] = "Status " + _run });
        var account = await Admin.RetrieveAsync("account", id, new ColumnSet("statecode", "statuscode"));
        account.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        account.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(1);
        var table = Name("st");
        await Admin.ExecuteAsync(CreateEntity(table));
        var row = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "x" });
        (await Admin.RetrieveAsync(table, row, new ColumnSet("statuscode"))).GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(1);
    }

    // ---- D4, D5: rule writes ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rule_for_table_without_flag_is_refused()
    {
        var off = Name("off");
        await Admin.ExecuteAsync(CreateEntity(off));
        var on = await EnabledTableAsync("on");
        (await BadRequestAsync(() => Admin.CreateAsync(Rule(off, off)))).Should().Contain("IsDuplicateDetectionEnabled");
        (await BadRequestAsync(() => Admin.CreateAsync(Rule(on, off)))).Should().Contain("IsDuplicateDetectionEnabled");
        (await BadRequestAsync(() => Admin.CreateAsync(Rule(on, "new_nosuchtable")))).Should().Contain("names no table");
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity("duplicaterule") { ["name"] = "x", ["baseentityname"] = on })))
            .Should().Contain("matchingentityname");
        (await Admin.CreateAsync(Rule(on, on))).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Rule_tables_cannot_change()
    {
        var first = await EnabledTableAsync("a");
        var second = await EnabledTableAsync("b");
        var id = await Admin.CreateAsync(Rule(first, first));
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("duplicaterule", id) { ["baseentityname"] = second }))).Should().Contain("baseentityname");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("duplicaterule", id) { ["matchingentityname"] = second }))).Should().Contain("matchingentityname");
        // The stored value sent back passes.
        await Admin.UpdateAsync(new Entity("duplicaterule", id) { ["baseentityname"] = first, ["description"] = "same tables" });
    }

    [Fact]
    public async Task Server_filled_columns_are_refused()
    {
        var table = await EnabledTableAsync("sf");
        foreach (var column in DuplicateDetectionSchema.ServerFilledRuleColumns)
        {
            var rule = Rule(table, table);
            rule[column] = column.EndsWith("typecode", StringComparison.Ordinal) ? new OptionSetValue(1) : "x";
            (await BadRequestAsync(() => Admin.CreateAsync(rule))).Should().Contain(column);
        }
        var id = await Admin.CreateAsync(Rule(table, table));
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("duplicaterule", id) { ["baseentitytypecode"] = new OptionSetValue(2) })))
            .Should().Contain("baseentitytypecode");
        var condition = Condition(id, "new_code", "new_code", 0);
        condition["uniquerulename"] = "mine";
        (await BadRequestAsync(() => Admin.CreateAsync(condition))).Should().Contain("uniquerulename");
    }

    [Fact]
    public async Task Rule_status_cannot_be_written()
    {
        var table = await EnabledTableAsync("ss");
        var published = Rule(table, table);
        published["statuscode"] = new OptionSetValue(2);
        (await BadRequestAsync(() => Admin.CreateAsync(published))).Should().Contain("PublishDuplicateRule");
        var active = Rule(table, table);
        active["statecode"] = new OptionSetValue(1);
        (await BadRequestAsync(() => Admin.CreateAsync(active))).Should().Contain("PublishDuplicateRule");

        var id = await Admin.CreateAsync(Rule(table, table));
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("duplicaterule", id) { ["statuscode"] = new OptionSetValue(1) })))
            .Should().Contain("PublishDuplicateRule");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference("duplicaterule", id), State = new OptionSetValue(1), Status = new OptionSetValue(2),
        }))).Should().Contain("PublishDuplicateRule");
        // The stored values sent back pass.
        await Admin.UpdateAsync(new Entity("duplicaterule", id) { ["statecode"] = new OptionSetValue(0), ["statuscode"] = new OptionSetValue(0) });

        // A rule in Published (as the publishing message will leave it) is not updated, nor are its conditions written.
        var condition = await Admin.CreateAsync(Condition(id, "new_code", "new_code", 0));
        await ScalarAsync("UPDATE data.duplicaterule SET statecode = 1, statuscode = 2 WHERE duplicateruleid = @id", ("@id", id));
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("duplicaterule", id) { ["description"] = "x" }))).Should().Contain("unpublish");
        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_name", "new_name", 0)))).Should().Contain("unpublish");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("duplicaterulecondition", condition) { ["ignoreblankvalues"] = true }))).Should().Contain("unpublish");
        (await BadRequestAsync(() => Admin.DeleteAsync("duplicaterulecondition", condition))).Should().Contain("unpublish");
        // Deleting a published rule is allowed, with its conditions.
        await Admin.DeleteAsync("duplicaterule", id);
        (await ScalarAsync("SELECT COUNT(*) FROM data.duplicaterulecondition WHERE duplicateruleconditionid = @id", ("@id", condition))).Should().Be(0);
    }

    // ---- D6: condition writes --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Condition_type_and_operator_checks()
    {
        var table = await EnabledTableAsync("op");
        var other = await EnabledTableAsync("op2");
        var id = await Admin.CreateAsync(Rule(table, other));

        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_code", "new_code", 1)))).Should().Contain("operatorparam");
        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_code", "new_code", 1, 0)))).Should().Contain("operatorparam");
        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_when", "new_when", 1, 3)))).Should().Contain("new_when");
        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_code", "new_code", 0, 3)))).Should().Contain("operatorparam");
        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_code", "new_code", 5)))).Should().Contain("Pick List Label");
        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_when", "new_when", 0)))).Should().Contain("new_when");
        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_code", "new_when", 0)))).Should().Contain("cannot be compared");
        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_code", "new_nosuchcolumn", 0)))).Should().Contain("new_nosuchcolumn");
        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_code", "new_code", 6)))).Should().Contain("operatorcode");
        (await BadRequestAsync(() => Admin.CreateAsync(Condition(id, "new_accountid", "ownerid", 0)))).Should().Contain("cannot be compared");
        var noRule = Condition(id, "new_code", "new_code", 0);
        noRule.Attributes.Remove("regardingobjectid");
        (await BadRequestAsync(() => Admin.CreateAsync(noRule))).Should().Contain("regardingobjectid");

        // The ones that fit.
        await Admin.CreateAsync(Condition(id, "new_code", "new_code", 1, 3));
        await Admin.CreateAsync(Condition(id, "new_code", "new_name", 2, 4));
        await Admin.CreateAsync(Condition(id, "new_when", "new_when", 3));
        await Admin.CreateAsync(Condition(id, "new_when", "new_when", 4));
        await Admin.CreateAsync(Condition(id, "statuscode", "statuscode", 6));
        var lookup = await Admin.CreateAsync(Condition(id, "new_accountid", "new_accountid", 0));
        (await Admin.RetrieveAsync("duplicaterulecondition", lookup, new ColumnSet("ignoreblankvalues")))
            .GetAttributeValue<bool>("ignoreblankvalues").Should().BeFalse();
        // An Update is checked on the resulting values.
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("duplicaterulecondition", lookup) { ["operatorcode"] = new OptionSetValue(1) })))
            .Should().Contain("new_accountid");
    }

    [Fact]
    public async Task Condition_write_needs_write_on_rule()
    {
        var table = await EnabledTableAsync("wr");
        var rule = await Admin.CreateAsync(Rule(table, table));
        var condition = await Admin.CreateAsync(Condition(rule, "new_code", "new_code", 0));

        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        var conditions = Store.GetEntity("duplicaterulecondition")!;
        var rules = Store.GetEntity("duplicaterule")!;
        await GrantAsync(fx, user, false,
            (PrivilegeNames.For(AccessRights.CreateAccess, conditions), DepthMask.Global),
            (PrivilegeNames.For(AccessRights.ReadAccess, conditions), DepthMask.Global),
            (PrivilegeNames.For(AccessRights.WriteAccess, conditions), DepthMask.Global),
            (PrivilegeNames.For(AccessRights.DeleteAccess, conditions), DepthMask.Global),
            (PrivilegeNames.For(AccessRights.AppendAccess, conditions), DepthMask.Global),
            (PrivilegeNames.For(AccessRights.ReadAccess, rules), DepthMask.Global),
            (PrivilegeNames.For(AccessRights.AppendToAccess, rules), DepthMask.Global));
        var service = ServiceFor(fx, oid);

        (await FaultAsync(() => service.CreateAsync(Condition(rule, "new_name", "new_name", 0)))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => service.UpdateAsync(new Entity("duplicaterulecondition", condition) { ["ignoreblankvalues"] = true }))).HttpStatus.Should().Be(403);
        (await FaultAsync(() => service.DeleteAsync("duplicaterulecondition", condition))).HttpStatus.Should().Be(403);
        // D6 over the conditions relationship: moving or unlinking a condition needs Write on the rules too (Append/AppendTo
        // alone are not enough).
        var other = await Admin.CreateAsync(Rule(table, table));
        var conditionsRelationship = new Relationship("DuplicateRule_DuplicateRuleConditions");
        var moved = new EntityReferenceCollection { new EntityReference("duplicaterulecondition", condition) };
        (await FaultAsync(() => service.ExecuteAsync(new AssociateRequest { Target = new EntityReference("duplicaterule", other), Relationship = conditionsRelationship, RelatedEntities = moved }))).HttpStatus.Should().Be(403);
        // Disassociate would leave the condition without its rule: refused for every caller (SystemTableRules run first, 400).
        (await FaultAsync(() => service.ExecuteAsync(new DisassociateRequest { Target = new EntityReference("duplicaterule", rule), Relationship = conditionsRelationship, RelatedEntities = moved }))).HttpStatus.Should().Be(400);
        (await ScalarAsync("SELECT COUNT(*) FROM data.duplicaterulecondition WHERE duplicateruleconditionid = @id AND regardingobjectid = @rule",
            ("@id", condition), ("@rule", rule))).Should().Be(1);

        await GrantAsync(fx, user, false, (PrivilegeNames.For(AccessRights.WriteAccess, rules), DepthMask.Global));
        await service.ExecuteAsync(new AssociateRequest { Target = new EntityReference("duplicaterule", other), Relationship = conditionsRelationship, RelatedEntities = moved });
        (await ScalarAsync("SELECT COUNT(*) FROM data.duplicaterulecondition WHERE duplicateruleconditionid = @id AND regardingobjectid = @rule",
            ("@id", condition), ("@rule", other))).Should().Be(1);
        await service.CreateAsync(Condition(rule, "new_name", "new_name", 0));
        await service.UpdateAsync(new Entity("duplicaterulecondition", condition) { ["ignoreblankvalues"] = true });
        await service.DeleteAsync("duplicaterulecondition", condition);
    }

    // ---- D6: metadata dependencies ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_attribute_used_by_condition_is_refused()
    {
        var table = await EnabledTableAsync("da");
        var other = await EnabledTableAsync("db");
        var rule = await Admin.CreateAsync(Rule(table, other, "Dependency " + _run));
        await Admin.CreateAsync(Condition(rule, "new_code", "new_code", 0));

        (await BadRequestAsync(() => Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_code" })))
            .Should().Contain("Dependency " + _run);
        (await BadRequestAsync(() => Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = other, LogicalName = "new_code" })))
            .Should().Contain("Dependency " + _run);
        // A column no condition names goes.
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = other, LogicalName = "new_when" });
    }

    [Fact]
    public async Task Delete_entity_deletes_its_rules()
    {
        var table = await EnabledTableAsync("de");
        var other = await EnabledTableAsync("df");
        var asBase = await Admin.CreateAsync(Rule(table, table));
        var asMatching = await Admin.CreateAsync(Rule(other, table));
        var kept = await Admin.CreateAsync(Rule(other, other));
        var condition = await Admin.CreateAsync(Condition(asMatching, "new_code", "new_code", 0));
        Store.DuplicateDetectionTables.Should().Contain(table);
        ((LookupAttributeMetadata)Store.GetEntity("duplicaterecord")!.Attributes.Single(a => a.LogicalName == "baserecordid")).Targets.Should().Contain(table);

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });

        (await ScalarAsync("SELECT COUNT(*) FROM data.duplicaterule WHERE duplicateruleid IN (@a, @b)", ("@a", asBase), ("@b", asMatching))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.duplicaterulecondition WHERE duplicateruleconditionid = @id", ("@id", condition))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.duplicaterule WHERE duplicateruleid = @id", ("@id", kept))).Should().Be(1);
        Store.DuplicateDetectionTables.Should().NotContain(table);
        foreach (var lookup in new[] { "baserecordid", "duplicaterecordid" })
            ((LookupAttributeMetadata)Store.GetEntity("duplicaterecord")!.Attributes.Single(a => a.LogicalName == lookup)).Targets.Should().NotContain(table);
    }

    // ---- the table flag ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IsDuplicateDetectionEnabled_round_trips()
    {
        (await RetrieveEntityAsync("contact")).IsDuplicateDetectionEnabled.Value.Should().BeFalse();
        await SetFlagAsync("contact", true);
        try
        {
            (await RetrieveEntityAsync("contact")).IsDuplicateDetectionEnabled.Value.Should().BeTrue();
            Store.DuplicateDetectionTables.Should().Contain("contact");
            ((LookupAttributeMetadata)Store.GetEntity("duplicaterecord")!.Attributes.Single(a => a.LogicalName == "duplicaterecordid")).Targets
                .Should().Contain("contact");
        }
        finally
        {
            await SetFlagAsync("contact", false);
        }
        (await RetrieveEntityAsync("contact")).IsDuplicateDetectionEnabled.Value.Should().BeFalse();
        Store.DuplicateDetectionTables.Should().NotContain("contact");

        var name = Name("rt");
        await Admin.ExecuteAsync(CreateEntity(name));
        (await RetrieveEntityAsync(name)).IsDuplicateDetectionEnabled.Value.Should().BeFalse("a created table starts without duplicate detection");
        await SetFlagAsync(name, true);
        (await RetrieveEntityAsync(name)).IsDuplicateDetectionEnabled.Value.Should().BeTrue();

        // Web API: shown on EntityDefinitions, settable through PUT.
        var path = $"EntityDefinitions(LogicalName='{name}')";
        var get = await SendAsync(HttpMethod.Get, path);
        var body = JsonNode.Parse(await get.Content.ReadAsStringAsync())!.AsObject();
        body["IsDuplicateDetectionEnabled"]!["Value"]!.GetValue<bool>().Should().BeTrue();
        body["IsDuplicateDetectionEnabled"]!["Value"] = false;
        var put = await SendAsync(HttpMethod.Put, path, body.ToJsonString());
        put.StatusCode.Should().Be(HttpStatusCode.NoContent, await put.Content.ReadAsStringAsync());
        (await RetrieveEntityAsync(name)).IsDuplicateDetectionEnabled.Value.Should().BeFalse();

        // CreateEntity takes it too.
        var created = Name("rc");
        await Admin.ExecuteAsync(CreateEntity(created, e => e.IsDuplicateDetectionEnabled = new BooleanManagedProperty(true)));
        (await RetrieveEntityAsync(created)).IsDuplicateDetectionEnabled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task IsDuplicateDetectionEnabled_refused_on_intersect_and_system_tables()
    {
        var left = Name("l");
        var right = Name("r");
        await Admin.ExecuteAsync(CreateEntity(left));
        await Admin.ExecuteAsync(CreateEntity(right));
        var intersect = Name("lr");
        await Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata { SchemaName = intersect, Entity1LogicalName = left, Entity2LogicalName = right },
            IntersectEntitySchemaName = intersect,
        });
        foreach (var table in new[] { intersect, "principalobjectaccess", "systemuser", "duplicaterule" })
        {
            (await BadRequestAsync(() => SetFlagAsync(table, true))).Should().Contain("IsDuplicateDetectionEnabled", table);
            (await RetrieveEntityAsync(table)).IsDuplicateDetectionEnabled.Value.Should().BeFalse(table);
        }
        (await BadRequestAsync(() => Admin.CreateAsync(Rule("systemuser", "systemuser")))).Should().Contain("IsDuplicateDetectionEnabled");
    }

    // ---- solutions -----------------------------------------------------------------------------------------------------

    private async Task<Guid> ImportFixtureAsync(Action<XDocument> change)
    {
        var job = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(change), ImportJobId = job });
        return job;
    }

    private static XElement Project(XDocument customizations) =>
        customizations.Descendants("entity").Single(e => (string?)e.Attribute("Name") == "xv_Project");

    private async Task<XDocument> ExportAsync()
    {
        var zip = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = "XrmverseFixture" })).ExportSolutionFile;
        return SolutionZip.Read(zip).Customizations;
    }

    [Fact]
    public async Task Import_sets_flag_keeps_DuplicateRules_node_and_upgrade_applies_kept_1()
    {
        var job = await ImportFixtureAsync(c =>
        {
            Project(c).Element("IsDuplicateCheckSupported")!.Value = "1";
            c.Root!.Element("Entities")!.AddAfterSelf(new XElement("DuplicateRules", new XElement("DuplicateRule", new XAttribute("uniquename", "xv_rule"))));
        });
        Store.GetEntity("xv_project")!.IsDuplicateDetectionEnabled!.Value.Should().BeTrue();
        var exported = await ExportAsync();
        Project(exported).Element("IsDuplicateCheckSupported")!.Value.Should().Be("1");
        exported.Root!.Element("DuplicateRules")!.Element("DuplicateRule")!.Attribute("uniquename")!.Value.Should().Be("xv_rule", "kept for export (D18)");
        var warnings = (string?)await ScalarAsync("SELECT data FROM data.importjob WHERE importjobid = @id", ("@id", job));
        warnings.Should().Contain("DuplicateRules");

        await ImportFixtureAsync(c => Project(c).Element("IsDuplicateCheckSupported")!.Value = "0");
        Store.GetEntity("xv_project")!.IsDuplicateDetectionEnabled!.Value.Should().BeFalse("a re-import applies the file's setting");

        // An organization that imported the table before the setting was modelled: the value in the kept XML, none in the metadata.
        var kept = XElement.Parse(Store.GetUnmodelledXml(UnmodelledXml.EntityKey("xv_project").Item1, "xv_project")!);
        UnmodelledXml.IsMarker(kept.Element("IsDuplicateCheckSupported")!).Should().BeTrue();
        kept.Element("IsDuplicateCheckSupported")!.ReplaceWith(new XElement("IsDuplicateCheckSupported", "1"));
        await ScalarAsync("UPDATE meta.entity SET unmodelled_xml = @x, definition = JSON_MODIFY(definition, '$.IsDuplicateDetectionEnabled', NULL) WHERE logical_name = N'xv_project'",
            ("@x", kept.ToString(SaveOptions.DisableFormatting)));
        var old = new MetadataStore(fx.App.Services.GetRequiredService<IDbConnectionFactory>());
        await old.LoadAsync();
        old.GetEntity("xv_project")!.IsDuplicateDetectionEnabled.Should().BeNull();

        await DuplicateDetectionSettingsUpgrade.EnsureAsync(old);
        old.GetEntity("xv_project")!.IsDuplicateDetectionEnabled!.Value.Should().BeTrue("the kept 1 is applied");
        UnmodelledXml.IsMarker(XElement.Parse(old.GetUnmodelledXml(UnmodelledXml.EntityKey("xv_project").Item1, "xv_project")!)
            .Element("IsDuplicateCheckSupported")!).Should().BeTrue("the element is a marker now");
        var stamp = old.VersionStamp;
        await DuplicateDetectionSettingsUpgrade.EnsureAsync(old);
        old.VersionStamp.Should().Be(stamp, "a second run changes nothing");

        await Store.LoadAsync();
        Project(await ExportAsync()).Element("IsDuplicateCheckSupported")!.Value.Should().Be("1");
        await SetFlagAsync("xv_project", false);
    }
}

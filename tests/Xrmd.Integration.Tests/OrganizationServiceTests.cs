using System.Data;
using Microsoft.Crm.Sdk.Messages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Query.Sql;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public sealed class OrganizationServiceTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;
    private static readonly DateTime Start = new(2026, 11, 7, 10, 30, 15, DateTimeKind.Utc);

    private IMetadataStore _store = null!;
    private IDbConnectionFactory _factory = null!;
    private OrganizationService _svc = null!;
    private Guid _adminId;
    private Guid _rootBuId;

    public async Task InitializeAsync()
    {
        _store = fx.App.Services.GetRequiredService<IMetadataStore>();
        _factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await TestEntities.EnsureProjectAsync(_store);
        _svc = NewService(fx.AdminObjectId);

        await using var conn = await _factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT systemuserid, businessunitid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.AddParameter("@oid", DbType.Guid, fx.AdminObjectId);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        _adminId = rd.GetGuid(0);
        _rootBuId = rd.GetGuid(1);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private OrganizationService NewService(Guid objectId) => TestServices.NewOrganizationService(fx.App.Services, objectId);

    private Entity NewProject(string name = "Apollo") => new(Project)
    {
        ["pb_name"] = name,
        ["pb_budget"] = new Money(1234.5m),
        ["pb_customerid"] = new EntityReference("systemuser", _adminId),
        ["pb_startdate"] = Start,
        ["pb_priority"] = new OptionSetValue(2),
        ["pb_count"] = 7,
    };

    [Fact]
    public void Create_then_Retrieve_returns_same_values()
    {
        var id = _svc.Create(NewProject());

        var e = _svc.Retrieve(Project, id, new ColumnSet("pb_name", "pb_budget", "pb_customerid", "pb_startdate", "pb_priority", "pb_count"));

        e.Id.Should().Be(id);
        e.LogicalName.Should().Be(Project);
        e["pb_projectid"].Should().Be(id);
        e["pb_name"].Should().Be("Apollo");
        e.GetAttributeValue<Money>("pb_budget").Value.Should().Be(1234.5m);
        var customer = e.GetAttributeValue<EntityReference>("pb_customerid");
        customer.LogicalName.Should().Be("systemuser");
        customer.Id.Should().Be(_adminId);
        var start = e.GetAttributeValue<DateTime>("pb_startdate");
        start.Should().Be(Start);
        start.Kind.Should().Be(DateTimeKind.Utc);
        e.GetAttributeValue<OptionSetValue>("pb_priority").Value.Should().Be(2);
        e["pb_count"].Should().Be(7);
    }

    [Fact]
    public void Create_honors_Entity_Id()
    {
        var wanted = Guid.NewGuid();
        var entity = NewProject();
        entity.Id = wanted;

        _svc.Create(entity).Should().Be(wanted);
        _svc.Retrieve(Project, wanted, new ColumnSet(false)).Id.Should().Be(wanted);
    }

    [Fact]
    public void Create_with_existing_id_throws_DuplicateRecord()
    {
        var entity = NewProject();
        entity.Id = _svc.Create(NewProject());

        var act = () => _svc.Create(entity);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.DuplicateRecord && e.HttpStatus == 412);
    }

    [Fact]
    public void Create_with_local_DateTime_stores_UTC()
    {
        var local = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local);
        var entity = NewProject();
        entity["pb_startdate"] = local;

        var id = _svc.Create(entity);

        _svc.Retrieve(Project, id, new ColumnSet("pb_startdate")).GetAttributeValue<DateTime>("pb_startdate")
            .Should().Be(local.ToUniversalTime());
    }

    [Fact]
    public void Retrieve_with_ColumnSet_true_returns_all_columns()
    {
        var before = DateTime.UtcNow.AddSeconds(-5);
        var id = _svc.Create(NewProject());

        var e = _svc.Retrieve(Project, id, new ColumnSet(true));

        e.Attributes.Keys.Should().Contain(
        [
            "pb_projectid", "pb_name", "pb_budget", "pb_budget_base", "pb_customerid", "pb_startdate", "pb_priority", "pb_count",
            "createdon", "createdby", "modifiedon", "modifiedby", "ownerid", "owninguser", "owningbusinessunit",
            "statecode", "statuscode", "versionnumber",
        ]);
        e.Attributes.Keys.Should().NotContain("owningteam", "null values are omitted");
        e.GetAttributeValue<Money>("pb_budget_base").Value.Should().Be(1234.5m);
        e.GetAttributeValue<DateTime>("createdon").Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(5));
        e.GetAttributeValue<DateTime>("createdon").Kind.Should().Be(DateTimeKind.Utc);
        e.GetAttributeValue<DateTime>("modifiedon").Should().Be(e.GetAttributeValue<DateTime>("createdon"));
        e.GetAttributeValue<EntityReference>("createdby").Should().Match<EntityReference>(r => r.LogicalName == "systemuser" && r.Id == _adminId);
        e.GetAttributeValue<EntityReference>("modifiedby").Id.Should().Be(_adminId);
        e.GetAttributeValue<EntityReference>("ownerid").Should().Match<EntityReference>(r => r.LogicalName == "systemuser" && r.Id == _adminId);
        e.GetAttributeValue<EntityReference>("owninguser").Id.Should().Be(_adminId);
        Scalar($"SELECT owneridtype FROM data.pb_project WHERE pb_projectid = '{id}'").Should().Be(8, "systemuser object type code");
        e.GetAttributeValue<EntityReference>("owningbusinessunit")
            .Should().Match<EntityReference>(r => r.LogicalName == "businessunit" && r.Id == _rootBuId);
        e.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
        e.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(1);
        e["versionnumber"].Should().BeOfType<long>().Which.Should().BePositive();
        e.RowVersion.Should().Be(e["versionnumber"].ToString());
    }

    [Fact]
    public void Retrieve_returns_formatted_values_and_lookup_names()
    {
        var id = _svc.Create(NewProject());

        var e = _svc.Retrieve(Project, id, new ColumnSet(true));

        e.FormattedValues["pb_priority"].Should().Be("Medium");
        e.FormattedValues["pb_budget"].Should().Be("€1,234.50"); // the base currency (plan 2.10, ruling X11)
        e.FormattedValues["pb_startdate"].Should().Be("11/7/2026 10:30 AM");
        e.FormattedValues["pb_count"].Should().Be("7");
        e.FormattedValues["statecode"].Should().Be("Active");
        e.FormattedValues["statuscode"].Should().Be("Active");
        e.GetAttributeValue<EntityReference>("pb_customerid").Name.Should().Be(XrmdFixture.AdminName);
        e.FormattedValues["pb_customerid"].Should().Be(XrmdFixture.AdminName);
        e.GetAttributeValue<EntityReference>("ownerid").Name.Should().Be(XrmdFixture.AdminName);
        e.FormattedValues["ownerid"].Should().Be(XrmdFixture.AdminName);
        e.GetAttributeValue<EntityReference>("owningbusinessunit").Name.Should().NotBeNullOrEmpty();
        e.FormattedValues.Should().NotContainKey("pb_name").And.NotContainKey("pb_projectid").And.NotContainKey("versionnumber");
        e.Id.Should().Be(id);
        e.RowVersion.Should().Be(e["versionnumber"].ToString());
    }

    [Fact]
    public void Retrieve_with_ColumnSet_false_returns_only_id()
    {
        var id = _svc.Create(NewProject());

        var e = _svc.Retrieve(Project, id, new ColumnSet(false));

        e.Id.Should().Be(id);
        e.Attributes.Keys.Should().BeEquivalentTo(["pb_projectid"]);
    }

    [Fact]
    public void Retrieve_unknown_column_throws_InvalidArgument()
    {
        var id = _svc.Create(NewProject());

        var act = () => _svc.Retrieve(Project, id, new ColumnSet("pb_nope"));

        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400).WithMessage("*pb_nope*");
    }

    [Fact]
    public void Retrieve_missing_record_throws_ObjectDoesNotExist()
    {
        var id = Guid.NewGuid();

        var act = () => _svc.Retrieve(Project, id, new ColumnSet(true));

        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404)
            .WithMessage($"pb_project With Id = {id} Does Not Exist");
    }

    [Fact]
    public void Update_changes_only_given_attributes()
    {
        var id = _svc.Create(NewProject());
        var before = _svc.Retrieve(Project, id, new ColumnSet(true));

        _svc.Update(new Entity(Project, id) { ["pb_name"] = "Gemini", ["pb_budget"] = new Money(99m), ["pb_count"] = null });

        var after = _svc.Retrieve(Project, id, new ColumnSet(true));
        after["pb_name"].Should().Be("Gemini");
        after.GetAttributeValue<Money>("pb_budget").Value.Should().Be(99m);
        after.GetAttributeValue<Money>("pb_budget_base").Value.Should().Be(99m);
        after.Contains("pb_count").Should().BeFalse("it was set to null");
        foreach (var unchanged in new[] { "pb_customerid", "pb_startdate", "pb_priority", "createdon", "createdby", "ownerid", "owningbusinessunit", "statecode" })
            after[unchanged].Should().BeEquivalentTo(before[unchanged], unchanged);
        after.GetAttributeValue<DateTime>("modifiedon").Should().BeOnOrAfter(before.GetAttributeValue<DateTime>("modifiedon"));
        after.GetAttributeValue<long>("versionnumber").Should().BeGreaterThan(before.GetAttributeValue<long>("versionnumber"));
    }

    [Fact]
    public void Update_with_retrieved_entity_ignores_read_only_attributes()
    {
        var id = _svc.Create(NewProject());
        var retrieved = _svc.Retrieve(Project, id, new ColumnSet(true));
        retrieved["pb_name"] = "Round trip";
        retrieved["createdon"] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _svc.Update(retrieved);

        var after = _svc.Retrieve(Project, id, new ColumnSet("pb_name", "createdon"));
        after["pb_name"].Should().Be("Round trip");
        after["createdon"].Should().NotBe(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Update_missing_record_throws_ObjectDoesNotExist()
    {
        var act = () => _svc.Update(new Entity(Project, Guid.NewGuid()) { ["pb_name"] = "x" });

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404);
    }

    [Fact]
    public void Delete_then_Retrieve_throws_ObjectDoesNotExist()
    {
        var id = _svc.Create(NewProject());

        _svc.Delete(Project, id);

        var act = () => _svc.Retrieve(Project, id, new ColumnSet(true));
        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404);
    }

    [Fact]
    public void Delete_missing_record_throws_ObjectDoesNotExist()
    {
        var act = () => _svc.Delete(Project, Guid.NewGuid());

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404);
    }

    [Fact]
    public void Create_with_lookup_to_missing_record_throws_ObjectDoesNotExist()
    {
        var missing = Guid.NewGuid();
        var entity = NewProject("Orphan");
        entity["pb_customerid"] = new EntityReference("systemuser", missing);

        var act = () => _svc.Create(entity);

        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404)
            .WithMessage($"systemuser With Id = {missing} Does Not Exist");
        CountProjects("Orphan").Should().Be(0);
    }

    [Fact]
    public void Update_with_lookup_to_missing_record_throws_ObjectDoesNotExist()
    {
        var id = _svc.Create(NewProject());

        var act = () => _svc.Update(new Entity(Project, id) { ["pb_customerid"] = new EntityReference("systemuser", Guid.NewGuid()) });

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404);
        _svc.Retrieve(Project, id, new ColumnSet("pb_customerid")).GetAttributeValue<EntityReference>("pb_customerid").Id.Should().Be(_adminId);
    }

    [Fact]
    public void Create_with_unknown_attribute_throws_InvalidArgument()
    {
        var entity = NewProject();
        entity["pb_nope"] = "x";

        var act = () => _svc.Create(entity);

        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400).WithMessage("*'pb_nope'*");
    }

    [Fact]
    public void Create_with_lookup_without_LogicalName_throws_InvalidArgument()
    {
        var entity = NewProject();
        entity["pb_customerid"] = new EntityReference { Id = _adminId };

        var act = () => _svc.Create(entity);

        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400).WithMessage("*pb_customerid*");
    }

    [Fact]
    public void Create_with_string_over_maxlength_throws_InvalidArgument_and_writes_nothing()
    {
        var name = new string('x', 201);

        var act = () => _svc.Create(NewProject(name));

        act.Should().Throw<XrmdFaultException>()
            .Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.HttpStatus == 400).WithMessage("*pb_name*");
        CountProjects(name[..200]).Should().Be(0, "no silent truncation");
    }

    [Fact]
    public void Create_with_missing_team_owner_throws_ObjectDoesNotExist()
    {
        var entity = NewProject();
        entity["ownerid"] = new EntityReference("team", Guid.NewGuid());

        var act = () => _svc.Create(entity);

        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist && e.HttpStatus == 404);
    }

    [Fact]
    public void Owner_can_be_another_systemuser_on_create_and_update()
    {
        var otherBu = _rootBuId;
        var other = _svc.Create(new Entity("systemuser")
        {
            ["fullname"] = "Other User",
            ["azureactivedirectoryobjectid"] = Guid.NewGuid(),
            ["businessunitid"] = new EntityReference("businessunit", otherBu),
        });
        var entity = NewProject();
        entity["ownerid"] = new EntityReference("systemuser", other);

        var id = _svc.Create(entity);
        var created = _svc.Retrieve(Project, id, new ColumnSet("ownerid", "owninguser", "createdby"));
        created.GetAttributeValue<EntityReference>("ownerid").Should().Match<EntityReference>(r => r.LogicalName == "systemuser" && r.Id == other);
        created.GetAttributeValue<EntityReference>("owninguser").Id.Should().Be(other);
        created.GetAttributeValue<EntityReference>("createdby").Id.Should().Be(_adminId);

        _svc.Update(new Entity(Project, id) { ["ownerid"] = new EntityReference("systemuser", _adminId) });
        _svc.Retrieve(Project, id, new ColumnSet("owninguser")).GetAttributeValue<EntityReference>("owninguser").Id.Should().Be(_adminId);
    }

    [Fact]
    public void WhoAmI_returns_current_systemuser_and_bu()
    {
        var response = (WhoAmIResponse)_svc.Execute(new WhoAmIRequest());

        response.UserId.Should().Be(_adminId);
        response.BusinessUnitId.Should().Be(_rootBuId);
        response.OrganizationId.Should().NotBeEmpty();
    }

    [Fact]
    public void Caller_without_systemuser_gets_403()
    {
        var svc = NewService(Guid.NewGuid());

        var act = () => svc.Execute(new WhoAmIRequest());

        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 403 && e.ErrorCode == ErrorCodes.NotMemberOfOrganization);
    }

    [Fact]
    public void Execute_dispatches_Create_Retrieve_Update_Delete()
    {
        var id = ((CreateResponse)_svc.Execute(new CreateRequest { Target = NewProject("Exec") })).id;

        var retrieve = new RetrieveRequest { Target = new EntityReference(Project, id), ColumnSet = new ColumnSet("pb_name") };
        ((RetrieveResponse)_svc.Execute(retrieve)).Entity.GetAttributeValue<string>("pb_name").Should().Be("Exec");

        _svc.Execute(new UpdateRequest { Target = new Entity(Project, id) { ["pb_name"] = "Exec 2" } }).Should().BeOfType<UpdateResponse>();
        ((RetrieveResponse)_svc.Execute(retrieve)).Entity.GetAttributeValue<string>("pb_name").Should().Be("Exec 2");

        _svc.Execute(new DeleteRequest { Target = new EntityReference(Project, id) }).Should().BeOfType<DeleteResponse>();
        ((Action)(() => _svc.Execute(retrieve))).Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist);
    }

    [Fact]
    public void Execute_accepts_plain_OrganizationRequest_by_name()
    {
        var request = new OrganizationRequest("WhoAmI");

        _svc.Execute(request).Should().BeOfType<WhoAmIResponse>().Which.UserId.Should().Be(_adminId);
    }

    [Fact]
    public void Execute_RetrieveEntity_returns_a_copy_with_lookup_relationships()
    {
        var response = (RetrieveEntityResponse)_svc.Execute(new RetrieveEntityRequest { LogicalName = Project, EntityFilters = EntityFilters.All });

        var m = response.EntityMetadata;
        m.Should().NotBeSameAs(_store.GetEntity(Project));
        m.EntitySetName.Should().Be(TestEntities.ProjectSet);
        m.Attributes.Should().Contain(a => a.LogicalName == "pb_customerid");
        var customer = m.ManyToOneRelationships.Should().ContainSingle(r => r.ReferencingAttribute == "pb_customerid").Subject;
        customer.ReferencedEntity.Should().Be("systemuser");
        customer.ReferencedAttribute.Should().Be("systemuserid");
        customer.ReferencingEntityNavigationPropertyName.Should().Be("pb_CustomerId");
        // Run alone the project has no N:N relationship; OnlyContain would fail on an empty collection.
        m.ManyToManyRelationships.Where(r => r.Entity1LogicalName != Project && r.Entity2LogicalName != Project).Should().BeEmpty();

        var user = ((RetrieveEntityResponse)_svc.Execute(new RetrieveEntityRequest { LogicalName = "systemuser", EntityFilters = EntityFilters.Relationships })).EntityMetadata;
        user.OneToManyRelationships.Should().Contain(r => r.ReferencingEntity == Project && r.ReferencingAttribute == "pb_customerid");
    }

    [Fact]
    public void Execute_RetrieveAttribute_returns_the_attribute()
    {
        var response = (RetrieveAttributeResponse)_svc.Execute(new RetrieveAttributeRequest { EntityLogicalName = Project, LogicalName = "pb_budget" });

        response.AttributeMetadata.Should().BeOfType<MoneyAttributeMetadata>().Which.LogicalName.Should().Be("pb_budget");
        ((Action)(() => _svc.Execute(new RetrieveAttributeRequest { EntityLogicalName = Project, LogicalName = "pb_nope" })))
            .Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist);
        ((Action)(() => _svc.Execute(new RetrieveEntityRequest { LogicalName = "pb_nope" })))
            .Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.ObjectDoesNotExist);
    }

    [Fact]
    public void Execute_without_required_parameter_throws_InvalidArgument()
    {
        ((Action)(() => _svc.Execute(new OrganizationRequest("Create"))))
            .Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.Message.Contains("Target"));
    }

    [Fact]
    public void Unsupported_operations_throw_NotSupportedException()
    {
        ((Action)(() => _svc.Execute(new OrganizationRequest("QualifyLead")))).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Associate_and_Disassociate_of_unknown_relationship_are_400_not_NotSupportedException()
    {
        ((Action)(() => _svc.Associate(Project, Guid.NewGuid(), new Relationship("x"), [])))
            .Should().Throw<XrmdFaultException>().Where(f => f.HttpStatus == 400 && f.Message == "Relationship 'x' is not supported");
        ((Action)(() => _svc.Disassociate(Project, Guid.NewGuid(), new Relationship("x"), [])))
            .Should().Throw<XrmdFaultException>().Where(f => f.HttpStatus == 400 && f.Message == "Relationship 'x' is not supported");
    }

    [Fact]
    public void Host_registers_OrganizationService_scoped()
    {
        using var scope = fx.App.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IOrganizationService>().Should().BeOfType<OrganizationService>();
        scope.ServiceProvider.GetRequiredService<OrganizationService>().Should().NotBeNull();
    }

    private object Scalar(string sql)
    {
        using var conn = _factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = TestSql.Command(conn, sql);
        return cmd.ExecuteScalar()!;
    }

    private int CountProjects(string name)
    {
        using var conn = _factory.OpenAsync().GetAwaiter().GetResult();
        using var cmd = TestSql.Command(conn, "SELECT COUNT(*) FROM data.pb_project WHERE pb_name = @n");
        cmd.AddParameter("@n", DbType.String, name, -1);
        return Convert.ToInt32(cmd.ExecuteScalar()); // bigint on PostgreSQL
    }
}

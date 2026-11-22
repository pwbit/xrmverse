using Xrmd.Abstractions.Storage;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Many-to-many relationships (<c>meta.relationship</c>): the intersect entity and table created with the relationship,
/// Associate/Disassociate from either side, duplicates, queries through the intersect, the privileges they need, the
/// intersect closed to direct writes, and the relationship's deletion.
/// </summary>
[Collection("xrmd")]
public sealed class ManyToManyTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Project = TestEntities.Project;
    private const string Tag = TestEntities.Tag;
    private const string ProjectTag = TestEntities.ProjectTag;

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService _admin = null!;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectTagAsync(Store, fx.ConnectionString);
        _admin = ServiceFor(fx, fx.AdminObjectId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private Task InSessionAsync(Func<DataSession, Task> write) => TestEntities.InSessionAsync(fx.ConnectionString, write);

    private Task<Guid> ProjectAsync(string label, Guid? owner = null)
    {
        var project = new Entity(Project) { ["pb_name"] = $"m2m-{_run}-{label}" };
        if (owner is { } id) project["ownerid"] = new EntityReference("systemuser", id);
        return _admin.CreateAsync(project);
    }

    private Task<Guid> TagAsync(string label, Guid? owner = null)
    {
        var tag = new Entity(Tag) { ["new_name"] = $"m2m-{_run}-{label}" };
        if (owner is { } id) tag["ownerid"] = new EntityReference("systemuser", id);
        return _admin.CreateAsync(tag);
    }

    private static AssociateRequest Associate(string relationship, EntityReference target, params EntityReference[] related) =>
        new() { Target = target, Relationship = new Relationship(relationship), RelatedEntities = [.. related] };

    private static DisassociateRequest Disassociate(string relationship, EntityReference target, params EntityReference[] related) =>
        new() { Target = target, Relationship = new Relationship(relationship), RelatedEntities = [.. related] };

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<string[]> ColumnsAsync(string table)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'data' AND TABLE_NAME = @t");
        cmd.Parameters.AddWithValue("@t", table);
        var names = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return [.. names];
    }

    /// <summary>The ids of the <c>new_tag</c> records associated with <paramref name="project"/>, through the intersect (QueryExpression).</summary>
    private async Task<Guid[]> TagsOfAsync(OrganizationService svc, Guid project)
    {
        var query = new QueryExpression(Tag) { ColumnSet = new ColumnSet("new_name") };
        var link = query.AddLink(ProjectTag, "new_tagid", "new_tagid");
        link.LinkCriteria.AddCondition("pb_projectid", ConditionOperator.Equal, project);
        return (await svc.RetrieveMultipleAsync(query)).Entities.Select(e => e.Id).Order().ToArray();
    }

    /// <summary>Same as <see cref="TagsOfAsync"/> in FetchXML, through the intersect and on to <c>pb_project</c>.</summary>
    private async Task<Guid[]> TagsOfFetchAsync(Guid project)
    {
        var fetch = $"""
            <fetch>
              <entity name="{Tag}">
                <attribute name="new_name" />
                <link-entity name="{ProjectTag}" from="new_tagid" to="new_tagid" intersect="true">
                  <link-entity name="{Project}" from="pb_projectid" to="pb_projectid" alias="p">
                    <filter><condition attribute="pb_projectid" operator="eq" value="{project}" /></filter>
                  </link-entity>
                </link-entity>
              </entity>
            </fetch>
            """;
        return (await _admin.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities.Select(e => e.Id).Order().ToArray();
    }

    private static Task FaultAsync(Func<Task> act, int code, int status, string? message = null) =>
        act.Should().ThrowAsync<XrmdFaultException>()
            .Where(e => e.ErrorCode == code && e.HttpStatus == status && (message == null || e.Message.Contains(message)));

    /// <summary>A new user-owned entity <c>new_m2m…</c> with a primary name.</summary>
    private async Task<string> NewEntityAsync()
    {
        var name = "new_m2m" + Guid.NewGuid().ToString("N")[..10];
        var entity = new EntityMetadata { LogicalName = name, SchemaName = name, OwnershipType = OwnershipTypes.UserOwned };
        entity.SetAttributes([]);
        await Store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        return name;
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ManyToMany_creates_intersect_and_associates_both_ways()
    {
        var relationship = Store.GetRelationship(ProjectTag).Should().BeOfType<ManyToManyRelationshipMetadata>().Subject;
        relationship.IntersectEntityName.Should().Be(ProjectTag);
        relationship.Entity1IntersectAttribute.Should().Be("pb_projectid");
        relationship.Entity2IntersectAttribute.Should().Be("new_tagid");
        var intersect = Store.GetEntity(ProjectTag)!;
        intersect.IsIntersect.Should().BeTrue();
        (await ColumnsAsync(ProjectTag)).Should().Contain(["new_project_new_tagid", "pb_projectid", "new_tagid"]);
        var projectMetadata = ((RetrieveEntityResponse)await _admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = Project }))
            .EntityMetadata;
        projectMetadata.ManyToManyRelationships.Should().Contain(r => r.SchemaName == ProjectTag);

        var p1 = await ProjectAsync("p1");
        var p2 = await ProjectAsync("p2");
        var t1 = await TagAsync("t1");
        var t2 = await TagAsync("t2");

        // From the pb_project side, then from the new_tag side.
        await _admin.ExecuteAsync(Associate(ProjectTag, new EntityReference(Project, p1), new EntityReference(Tag, t1), new EntityReference(Tag, t2)));
        await _admin.ExecuteAsync(Associate(ProjectTag, new EntityReference(Tag, t1), new EntityReference(Project, p2)));

        await FaultAsync(() => _admin.ExecuteAsync(Associate(ProjectTag, new EntityReference(Project, p1), new EntityReference(Tag, t1))),
            ErrorCodes.DuplicateRecord, 412);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(ProjectTag, new EntityReference(Tag, t2), new EntityReference(Project, p1))),
            ErrorCodes.DuplicateRecord, 412);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(ProjectTag, new EntityReference(Project, p1), new EntityReference(Project, p2))),
            ErrorCodes.InvalidArgument, 400);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(ProjectTag, new EntityReference(Project, p1), new EntityReference(Tag, Guid.NewGuid()))),
            ErrorCodes.ObjectDoesNotExist, 404);

        (await TagsOfAsync(_admin, p1)).Should().Equal(new[] { t1, t2 }.Order());
        (await TagsOfAsync(_admin, p2)).Should().Equal(t1);
        (await TagsOfFetchAsync(p1)).Should().Equal(new[] { t1, t2 }.Order());
        (await TagsOfFetchAsync(p2)).Should().Equal(t1);

        await _admin.ExecuteAsync(Disassociate(ProjectTag, new EntityReference(Project, p1), new EntityReference(Tag, t2)));
        await _admin.ExecuteAsync(Disassociate(ProjectTag, new EntityReference(Tag, t1), new EntityReference(Project, p2)));
        (await TagsOfAsync(_admin, p1)).Should().Equal(t1);
        (await TagsOfFetchAsync(p1)).Should().Equal(t1);
        (await TagsOfAsync(_admin, p2)).Should().BeEmpty();
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{ProjectTag} WHERE pb_projectid IN (@p1, @p2)", ("@p1", p1), ("@p2", p2)))
            .Should().Be(1);

        // Disassociating a pair that is not associated is not an error.
        await _admin.ExecuteAsync(Disassociate(ProjectTag, new EntityReference(Project, p2), new EntityReference(Tag, t2)));
    }

    [Fact]
    public async Task Self_referential_ManyToMany_uses_idone_idtwo()
    {
        var entity = await NewEntityAsync();
        var name = entity + "_self";
        await InSessionAsync(s => Store.CreateManyToManyAsync(s, new ManyToManyRelationshipMetadata
        {
            SchemaName = name, Entity1LogicalName = entity, Entity2LogicalName = entity,
        }, default));

        var relationship = (ManyToManyRelationshipMetadata)Store.GetRelationship(name)!;
        relationship.IntersectEntityName.Should().Be(name);
        relationship.Entity1IntersectAttribute.Should().Be(entity + "idone");
        relationship.Entity2IntersectAttribute.Should().Be(entity + "idtwo");
        (await ColumnsAsync(name)).Should().Contain([entity + "idone", entity + "idtwo"]).And.NotContain(entity + "id");

        var a = await _admin.CreateAsync(new Entity(entity) { ["new_name"] = "a" });
        var b = await _admin.CreateAsync(new Entity(entity) { ["new_name"] = "b" });
        await _admin.ExecuteAsync(Associate(name, new EntityReference(entity, a), new EntityReference(entity, b)));
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{name} WHERE {entity}idone = @a AND {entity}idtwo = @b", ("@a", a), ("@b", b)))
            .Should().Be(1);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(name, new EntityReference(entity, a), new EntityReference(entity, b))),
            ErrorCodes.DuplicateRecord, 412);

        // Disassociate from the other record removes the pair too.
        await _admin.ExecuteAsync(Disassociate(name, new EntityReference(entity, b), new EntityReference(entity, a)));
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{name}")).Should().Be(0);
    }

    [Fact]
    public async Task Intersect_rows_cannot_be_written_directly()
    {
        var p = await ProjectAsync("direct");
        var t = await TagAsync("direct");

        await FaultAsync(() => _admin.CreateAsync(new Entity(ProjectTag) { ["pb_projectid"] = p, ["new_tagid"] = t }),
            ErrorCodes.PrivilegeDenied, 403, "maintained by the system");
        await FaultAsync(() => _admin.DeleteAsync(ProjectTag, Guid.NewGuid()), ErrorCodes.PrivilegeDenied, 403, "maintained by the system");
        await FaultAsync(() => _admin.UpdateAsync(new Entity(ProjectTag, Guid.NewGuid()) { ["new_tagid"] = t }),
            ErrorCodes.PrivilegeDenied, 403, "maintained by the system");

        // The lookups of the intersect (createdby, …) imply no relationship that Associate could write through.
        Store.GetRelationship($"{ProjectTag}_createdby_systemuser").Should().BeNull();
        var systemUser = ((RetrieveEntityResponse)await _admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = "systemuser" }))
            .EntityMetadata;
        systemUser.OneToManyRelationships.Should().NotContain(r => r.ReferencingEntity == ProjectTag);
    }

    [Fact]
    public async Task ManyToMany_associate_requires_append_and_appendto()
    {
        var root = await RootBusinessUnitAsync(fx);
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, root, oid);
        var role = await CreateRoleAsync(fx, "m2m-" + _run,
            ("prvReadpb_Project", DepthMask.Global), ("prvAppendpb_Project", DepthMask.Global), ("prvAppendTopb_Project", DepthMask.Global),
            ("prvReadnew_tag", DepthMask.Global), ("prvAppendnew_tag", DepthMask.Global));
        await AssignRoleAsync(fx, user, role);
        var svc = ServiceFor(fx, oid);
        var p = await ProjectAsync("sec");
        var t = await TagAsync("sec");

        await FaultAsync(() => svc.ExecuteAsync(Associate(ProjectTag, new EntityReference(Project, p), new EntityReference(Tag, t))),
            ErrorCodes.PrivilegeDenied, 403, "prvAppendTonew_tag");
        await FaultAsync(() => svc.ExecuteAsync(Associate(ProjectTag, new EntityReference(Tag, t), new EntityReference(Project, p))),
            ErrorCodes.PrivilegeDenied, 403, "prvAppendTonew_tag");

        await AddPrivilegeAsync(fx, role, "prvAppendTonew_tag", DepthMask.Global);
        Cache(fx).Invalidate();
        await svc.ExecuteAsync(Associate(ProjectTag, new EntityReference(Project, p), new EntityReference(Tag, t)));
        (await TagsOfAsync(svc, p)).Should().Equal(t);

        // Append on the target is required too: without prvAppendpb_Project the Associate from pb_project is refused.
        var oid2 = Guid.NewGuid();
        var user2 = await CreateUserAsync(fx, root, oid2);
        await GrantAsync(fx, user2, false,
            ("prvReadpb_Project", DepthMask.Global), ("prvAppendTopb_Project", DepthMask.Global),
            ("prvReadnew_tag", DepthMask.Global), ("prvAppendnew_tag", DepthMask.Global), ("prvAppendTonew_tag", DepthMask.Global));
        await FaultAsync(() => ServiceFor(fx, oid2).ExecuteAsync(Disassociate(ProjectTag, new EntityReference(Project, p), new EntityReference(Tag, t))),
            ErrorCodes.PrivilegeDenied, 403, "prvAppendpb_Project");
    }

    [Fact]
    public async Task Deleting_a_relationship_drops_intersect()
    {
        var entity = await NewEntityAsync();
        var name = entity + "_" + Tag;
        await InSessionAsync(s => Store.CreateManyToManyAsync(s, new ManyToManyRelationshipMetadata
        {
            SchemaName = name, Entity1LogicalName = entity, Entity2LogicalName = Tag,
        }, default));
        (await TestSql.TableExistsAsync(fx.ConnectionString, "data", name)).Should().BeTrue();
        var e = await _admin.CreateAsync(new Entity(entity) { ["new_name"] = "e" });
        var t = await TagAsync("del");
        await _admin.ExecuteAsync(Associate(name, new EntityReference(entity, e), new EntityReference(Tag, t)));

        // Names are unique ignoring case; the intersect and the sides cannot be deleted while the relationship exists.
        await FluentActions.Awaiting(() => InSessionAsync(s => Store.CreateManyToManyAsync(s, new ManyToManyRelationshipMetadata
        {
            SchemaName = name.ToUpperInvariant(), IntersectEntityName = name + "x", Entity1LogicalName = entity, Entity2LogicalName = Tag,
        }, default))).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => InSessionAsync(s => Store.DeleteEntityAsync(s, name, default))).Should().ThrowAsync<XrmdFaultException>()
            .Where(x => x.HttpStatus == 400 && x.Message.Contains($"relationship '{name}'"));
        await FluentActions.Awaiting(() => InSessionAsync(s => Store.DeleteEntityAsync(s, entity, default))).Should().ThrowAsync<XrmdFaultException>()
            .Where(x => x.HttpStatus == 400 && x.Message.Contains($"relationship '{name}'"));

        await InSessionAsync(s => Store.DeleteRelationshipAsync(s, name.ToUpperInvariant(), default));

        Store.GetRelationship(name).Should().BeNull();
        Store.GetEntity(name).Should().BeNull();
        (await TestSql.TableExistsAsync(fx.ConnectionString, "data", name)).Should().BeFalse();
        (await ScalarAsync("SELECT COUNT(*) FROM meta.relationship WHERE schema_name = @n", ("@n", name))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.entity WHERE logical_name = @n", ("@n", name))).Should().Be(0);
        await FaultAsync(() => _admin.ExecuteAsync(Associate(name, new EntityReference(entity, e), new EntityReference(Tag, t))),
            ErrorCodes.InvalidArgument, 400);

        // A restart (reload of meta.*) sees the same relationships.
        await Store.LoadAsync();
        Store.GetRelationship(name).Should().BeNull();
        Store.GetRelationship(ProjectTag).Should().BeOfType<ManyToManyRelationshipMetadata>();
        await InSessionAsync(s => Store.DeleteEntityAsync(s, entity, default));
    }

    [Fact]
    public async Task Deleting_a_record_removes_its_associations()
    {
        var p = await ProjectAsync("gone");
        var keep = await ProjectAsync("keep");
        var t = await TagAsync("gone");
        await _admin.ExecuteAsync(Associate(ProjectTag, new EntityReference(Project, p), new EntityReference(Tag, t)));
        await _admin.ExecuteAsync(Associate(ProjectTag, new EntityReference(Project, keep), new EntityReference(Tag, t)));

        await _admin.DeleteAsync(Project, p);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{ProjectTag} WHERE pb_projectid = @p", ("@p", p))).Should().Be(0);
        (await TagsOfAsync(_admin, keep)).Should().Equal(t);

        // Deleting the other side's record removes its remaining associations too.
        await _admin.DeleteAsync(Tag, t);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{ProjectTag} WHERE new_tagid = @t", ("@t", t))).Should().Be(0);

        // Self-referential: the record goes from both columns.
        var entity = await NewEntityAsync();
        var self = entity + "_self";
        await InSessionAsync(s => Store.CreateManyToManyAsync(s, new ManyToManyRelationshipMetadata
        {
            SchemaName = self, Entity1LogicalName = entity, Entity2LogicalName = entity,
        }, default));
        var a = await _admin.CreateAsync(new Entity(entity) { ["new_name"] = "a" });
        var b = await _admin.CreateAsync(new Entity(entity) { ["new_name"] = "b" });
        var c = await _admin.CreateAsync(new Entity(entity) { ["new_name"] = "c" });
        await _admin.ExecuteAsync(Associate(self, new EntityReference(entity, a), new EntityReference(entity, b)));
        await _admin.ExecuteAsync(Associate(self, new EntityReference(entity, c), new EntityReference(entity, a)));
        await _admin.ExecuteAsync(Associate(self, new EntityReference(entity, b), new EntityReference(entity, c)));

        await _admin.DeleteAsync(entity, a);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{self} WHERE {entity}idone = @a OR {entity}idtwo = @a", ("@a", a))).Should().Be(0);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{self}")).Should().Be(1);
    }
}

using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
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
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Lookups with more than one target: the <c>&lt;name&gt;type</c> column with the target's object type code, the
/// EntityReference read back with its entity, targets outside the list refused, the Web API annotation, one one-to-many
/// relationship per target.
/// </summary>
[Collection("xrmd")]
public sealed class PolymorphicLookupTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private const string Project = TestEntities.Project;
    private const string Tag = TestEntities.Tag;
    private const string Regarding = "new_regardingid";

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<JsonObject> GetJsonAsync(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    [Fact]
    public async Task Polymorphic_lookup_stores_type_and_resolves_entity_reference()
    {
        await TestEntities.EnsureProjectTagAsync(Store, fx.ConnectionString);
        var admin = ServiceFor(fx, fx.AdminObjectId);
        var entity = "new_poly" + Guid.NewGuid().ToString("N")[..10];
        var definition = new EntityMetadata { LogicalName = entity, SchemaName = entity, OwnershipType = OwnershipTypes.UserOwned };
        definition.SetAttributes([]);
        await Store.CreateEntityAsync(definition, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        await Store.AddAttributeAsync(entity,
            new LookupAttributeMetadata { LogicalName = Regarding, SchemaName = "new_RegardingId", Targets = [Project, Tag] });

        (await TestSql.ColumnExistsAsync(fx.ConnectionString, "data", entity, Regarding + "type")).Should().BeTrue();

        var project = await admin.CreateAsync(new Entity(Project) { ["pb_name"] = "poly" });
        var tag = await admin.CreateAsync(new Entity(Tag) { ["new_name"] = "poly" });
        var onProject = await admin.CreateAsync(new Entity(entity) { ["new_name"] = "p", [Regarding] = new EntityReference(Project, project) });
        var onTag = await admin.CreateAsync(new Entity(entity) { ["new_name"] = "t", [Regarding] = new EntityReference(Tag, tag) });

        (await admin.RetrieveAsync(entity, onProject, new ColumnSet(Regarding))).GetAttributeValue<EntityReference>(Regarding)
            .Should().Match<EntityReference>(r => r.LogicalName == Project && r.Id == project && r.Name == "poly");
        (await admin.RetrieveAsync(entity, onTag, new ColumnSet(Regarding))).GetAttributeValue<EntityReference>(Regarding)
            .Should().Match<EntityReference>(r => r.LogicalName == Tag && r.Id == tag && r.Name == "poly");
        (await ScalarAsync($"SELECT {Regarding}type FROM data.{entity} WHERE {entity}id = @id", ("@id", onTag)))
            .Should().Be(Store.GetEntity(Tag)!.ObjectTypeCode);

        // Update to the other target moves the type with it.
        await admin.UpdateAsync(new Entity(entity, onTag) { [Regarding] = new EntityReference(Project, project) });
        (await admin.RetrieveAsync(entity, onTag, new ColumnSet(Regarding))).GetAttributeValue<EntityReference>(Regarding).LogicalName
            .Should().Be(Project);

        await admin.Invoking(a => a.CreateAsync(new Entity(entity) { [Regarding] = new EntityReference("systemuser", Guid.NewGuid()) }))
            .Should().ThrowAsync<XrmdFaultException>().Where(e => e.HttpStatus == 400);

        var json = await GetJsonAsync($"{entity}s({onProject})?$select={Regarding}");
        json[$"_{Regarding}_value"]!.GetValue<string>().Should().Be(project.ToString());
        json[$"_{Regarding}_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be(Project);

        // One one-to-many relationship per target.
        Store.GetRelationship($"{entity}_{Regarding}_{Project}").Should().BeOfType<OneToManyRelationshipMetadata>();
        var metadata = ((RetrieveEntityResponse)await admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = entity })).EntityMetadata;
        metadata.ManyToOneRelationships.Where(r => r.ReferencingAttribute == Regarding).Select(r => r.ReferencedEntity)
            .Should().BeEquivalentTo(Project, Tag);
        await FluentActions.Awaiting(() => InSessionAsync(s => Store.DeleteRelationshipAsync(s, $"{entity}_{Regarding}_{Tag}", default)))
            .Should().ThrowAsync<XrmdValidationException>();
    }

    [Fact]
    public async Task Polymorphic_lookup_in_a_new_entity_gets_its_type_column()
    {
        await TestEntities.EnsureProjectTagAsync(Store, fx.ConnectionString);
        var entity = "new_poly" + Guid.NewGuid().ToString("N")[..10];
        var definition = new EntityMetadata { LogicalName = entity, SchemaName = entity, OwnershipType = OwnershipTypes.UserOwned };
        definition.SetAttributes([new LookupAttributeMetadata { LogicalName = Regarding, SchemaName = "new_RegardingId", Targets = [Project, Tag] }]);
        await Store.CreateEntityAsync(definition, new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 });
        (await TestSql.ColumnExistsAsync(fx.ConnectionString, "data", entity, Regarding + "type")).Should().BeTrue();

        // A lookup without target, or naming one twice, is refused.
        await Store.Invoking(s => s.AddAttributeAsync(entity, new LookupAttributeMetadata { LogicalName = "new_none", Targets = [] }))
            .Should().ThrowAsync<XrmdValidationException>();
        await Store.Invoking(s => s.AddAttributeAsync(entity, new LookupAttributeMetadata { LogicalName = "new_twice", Targets = [Tag, Tag] }))
            .Should().ThrowAsync<XrmdValidationException>();

        // Deleting the lookup drops both columns.
        await InSessionAsync(s => Store.DeleteAttributeAsync(s, entity, Regarding, default));
        (await TestSql.ColumnExistsAsync(fx.ConnectionString, "data", entity, Regarding + "type")).Should().BeFalse();
    }

    private Task InSessionAsync(Func<DataSession, Task> write) => TestEntities.InSessionAsync(fx.ConnectionString, write);
}

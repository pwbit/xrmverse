using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>#167, task 1 (needs a database): the seeded environment variable tables and their write rules (ruling E2).</summary>
[Collection("xrmd")]
public sealed class EnvironmentVariableTablesTests(XrmdFixture fx)
{
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private static string NewName() => "xv_Var" + Guid.NewGuid().ToString("N")[..10];

    private Task<Guid> CreateDefinitionAsync(string schemaName, string? defaultValue = null)
    {
        var definition = new Entity(EnvironmentVariableSchema.Definition)
        {
            [EnvironmentVariableSchema.SchemaName] = schemaName,
            [EnvironmentVariableSchema.DisplayName] = schemaName,
            [EnvironmentVariableSchema.Type] = new OptionSetValue((int)VariableType.String),
        };
        if (defaultValue is not null) definition[EnvironmentVariableSchema.DefaultValue] = defaultValue;
        return ServiceFor(fx, fx.AdminObjectId).CreateAsync(definition);
    }

    private Task<Guid> CreateValueAsync(Guid definitionId, string value, string? schemaName = null)
    {
        var row = new Entity(EnvironmentVariableSchema.Value)
        {
            [EnvironmentVariableSchema.ValueColumn] = value,
            [EnvironmentVariableSchema.DefinitionLookup] = new EntityReference(EnvironmentVariableSchema.Definition, definitionId),
        };
        if (schemaName is not null) row[EnvironmentVariableSchema.SchemaName] = schemaName;
        return ServiceFor(fx, fx.AdminObjectId).CreateAsync(row);
    }

    private async Task<Guid> BasicUserAsync(Guid objectId)
    {
        var userId = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, userId, StandardRoles.BasicUser);
        return userId;
    }

    [Theory]
    [InlineData("environmentvariabledefinition", "environmentvariabledefinitions", "environmentvariabledefinitionid", 380)]
    [InlineData("environmentvariablevalue", "environmentvariablevalues", "environmentvariablevalueid", 381)]
    public void Tables_exist_with_dataverse_names(string table, string set, string id, int otc)
    {
        var e = Store.GetEntity(table);
        e.Should().NotBeNull();
        (e!.EntitySetName, e.PrimaryIdAttribute, e.PrimaryNameAttribute, e.ObjectTypeCode, e.OwnershipType)
            .Should().Be((set, id, EnvironmentVariableSchema.SchemaName, otc, OwnershipTypes.UserOwned));
        Store.GetEntityByObjectTypeCode(otc)!.LogicalName.Should().Be(table);
        Store.GetRelationship(EnvironmentVariableSchema.Relationship).Should().NotBeNull();
    }

    [Fact]
    public async Task Basic_user_reads_definitions_and_values()
    {
        var definition = await CreateDefinitionAsync(NewName(), "fallback");
        await CreateValueAsync(definition, "current");
        var alice = Guid.NewGuid();
        await BasicUserAsync(alice);
        foreach (var set in new[] { "environmentvariabledefinitions", "environmentvariablevalues" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/data/v9.2/{set}?$top=5");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(alice, fx.BaseUrl));
            var response = await fx.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, set + ": " + body);
            body.Should().Contain("schemaname");
        }
    }

    [Fact]
    public async Task Duplicate_schema_name_is_refused()
    {
        var name = NewName();
        await CreateDefinitionAsync(name);
        var act = () => CreateDefinitionAsync(name.ToLowerInvariant());
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(name.ToLowerInvariant());
    }

    [Fact]
    public async Task Schema_name_cannot_change()
    {
        var name = NewName();
        var id = await CreateDefinitionAsync(name);
        var service = ServiceFor(fx, fx.AdminObjectId);
        // Writing the same name again (or changing another column) is allowed.
        await service.UpdateAsync(new Entity(EnvironmentVariableSchema.Definition, id)
        {
            [EnvironmentVariableSchema.SchemaName] = name, [EnvironmentVariableSchema.DefaultValue] = "x",
        });
        var act = () => service.UpdateAsync(new Entity(EnvironmentVariableSchema.Definition, id) { [EnvironmentVariableSchema.SchemaName] = name + "2" });
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(name);
    }

    [Fact]
    public async Task Second_value_is_refused()
    {
        var name = NewName();
        var definition = await CreateDefinitionAsync(name);
        await CreateValueAsync(definition, "one");
        var act = () => CreateValueAsync(definition, "two");
        var fault = (await act.Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain(name);

        // Moving another definition's value onto this one is refused too.
        var other = await CreateDefinitionAsync(NewName());
        var otherValue = await CreateValueAsync(other, "three");
        var move = () => ServiceFor(fx, fx.AdminObjectId).UpdateAsync(new Entity(EnvironmentVariableSchema.Value, otherValue)
        {
            [EnvironmentVariableSchema.DefinitionLookup] = new EntityReference(EnvironmentVariableSchema.Definition, definition),
        });
        (await move.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Value_needs_an_existing_definition()
    {
        var missing = () => CreateValueAsync(Guid.NewGuid(), "orphan");
        (await missing.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
        var none = () => ServiceFor(fx, fx.AdminObjectId).CreateAsync(new Entity(EnvironmentVariableSchema.Value)
        {
            [EnvironmentVariableSchema.ValueColumn] = "orphan",
        });
        (await none.Should().ThrowAsync<XrmdFaultException>()).Which.HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Value_schema_name_defaults_to_definition()
    {
        var name = NewName();
        var definition = await CreateDefinitionAsync(name);
        var value = await CreateValueAsync(definition, "v");
        var row = await ServiceFor(fx, fx.AdminObjectId).RetrieveAsync(EnvironmentVariableSchema.Value, value,
            new ColumnSet(EnvironmentVariableSchema.SchemaName));
        row.GetAttributeValue<string>(EnvironmentVariableSchema.SchemaName).Should().Be(name);

        var given = await CreateValueAsync(await CreateDefinitionAsync(NewName()), "w", schemaName: "xv_Own");
        (await ServiceFor(fx, fx.AdminObjectId).RetrieveAsync(EnvironmentVariableSchema.Value, given, new ColumnSet(EnvironmentVariableSchema.SchemaName)))
            .GetAttributeValue<string>(EnvironmentVariableSchema.SchemaName).Should().Be("xv_Own");
    }

    [Fact]
    public async Task Deleting_definition_deletes_value()
    {
        var definition = await CreateDefinitionAsync(NewName());
        var value = await CreateValueAsync(definition, "gone");
        var service = ServiceFor(fx, fx.AdminObjectId);
        await service.DeleteAsync(EnvironmentVariableSchema.Definition, definition);
        var query = new QueryExpression(EnvironmentVariableSchema.Value) { ColumnSet = new ColumnSet(false) };
        query.Criteria.AddCondition(EnvironmentVariableSchema.Value + "id", ConditionOperator.Equal, value);
        (await service.RetrieveMultipleAsync(query)).Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Plugin_style_query_with_outer_link_returns_value()
    {
        var withValue = NewName();
        await CreateValueAsync(await CreateDefinitionAsync(withValue, "default-a"), "value-a");
        var withoutValue = NewName();
        await CreateDefinitionAsync(withoutValue, "default-b");
        var bob = Guid.NewGuid();
        await BasicUserAsync(bob);

        var query = new QueryExpression(EnvironmentVariableSchema.Definition)
        {
            ColumnSet = new ColumnSet(EnvironmentVariableSchema.SchemaName, EnvironmentVariableSchema.DefaultValue),
        };
        query.Criteria.AddCondition(EnvironmentVariableSchema.SchemaName, ConditionOperator.In, withValue, withoutValue);
        var link = query.AddLink(EnvironmentVariableSchema.Value, "environmentvariabledefinitionid", EnvironmentVariableSchema.DefinitionLookup,
            JoinOperator.LeftOuter);
        link.EntityAlias = "v";
        link.Columns = new ColumnSet(EnvironmentVariableSchema.ValueColumn);

        var rows = (await ServiceFor(fx, bob).RetrieveMultipleAsync(query)).Entities;
        rows.Should().HaveCount(2);
        var a = rows.Single(r => r.GetAttributeValue<string>(EnvironmentVariableSchema.SchemaName) == withValue);
        a.GetAttributeValue<string>(EnvironmentVariableSchema.DefaultValue).Should().Be("default-a");
        a.GetAttributeValue<AliasedValue>("v.value").Value.Should().Be("value-a");
        var b = rows.Single(r => r.GetAttributeValue<string>(EnvironmentVariableSchema.SchemaName) == withoutValue);
        b.GetAttributeValue<string>(EnvironmentVariableSchema.DefaultValue).Should().Be("default-b");
        b.Contains("v.value").Should().BeFalse();
    }
}

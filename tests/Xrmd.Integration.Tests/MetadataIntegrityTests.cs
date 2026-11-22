using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// What the metadata writes refuse instead of storing a definition xrmverse would not honor: attribute writes on an
/// intersect entity, lookups to a missing or intersect entity.
/// </summary>
[Collection("xrmd")]
public sealed class MetadataIntegrityTests(XrmdFixture fx)
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private string Name(string label) => $"new_{label}{_run}";

    private static CreateEntityRequest CreateEntity(string logicalName, params AttributeMetadata[] attributes) => new()
    {
        Entity = TestHelpers.Entity(logicalName, logicalName, "Table " + logicalName, attributes),
        PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name") },
    };

    private async Task<string> NewEntityAsync(string label)
    {
        var name = Name(label);
        await Admin.ExecuteAsync(CreateEntity(name));
        return name;
    }

    /// <summary>A many-to-many relationship between two new entities; its intersect entity's logical name.</summary>
    private async Task<string> NewIntersectAsync()
    {
        var left = await NewEntityAsync("l");
        var right = await NewEntityAsync("r");
        var schemaName = Name("lr");
        await Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata { SchemaName = schemaName, Entity1LogicalName = left, Entity2LogicalName = right },
            IntersectEntitySchemaName = schemaName,
        });
        Store.GetEntity(schemaName)!.IsIntersect.Should().BeTrue();
        return schemaName;
    }

    private static LookupAttributeMetadata Lookup(string logicalName, params string[] targets) =>
        new() { LogicalName = logicalName, SchemaName = logicalName, Targets = targets, DisplayName = Label("Lookup") };

    // ---- intersect entities -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Attribute_writes_on_an_intersect_entity_are_400()
    {
        var intersect = await NewIntersectAsync();
        var column = Store.GetEntity(intersect)!.Attributes.First(a => a.IsPrimaryId != true).LogicalName;

        var add = await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = intersect,
            Attribute = new IntegerAttributeMetadata { LogicalName = "new_count", SchemaName = "new_Count", DisplayName = Label("Count") },
        }));
        add.Should().Contain(intersect).And.Contain("intersect");

        var update = await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = intersect,
            Attribute = new StringAttributeMetadata { LogicalName = column, DisplayName = Label("Relabelled") }.WithType(Store, intersect),
        }));
        update.Should().Contain(intersect).And.Contain("intersect");

        var delete = await BadRequestAsync(() => Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = intersect, LogicalName = column }));
        delete.Should().Contain(intersect).And.Contain("intersect");

        Store.GetEntity(intersect)!.Attributes.Should().Contain(a => a.LogicalName == column);

        // The Web API answers 400 too.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/data/v9.2/EntityDefinitions(LogicalName='{intersect}')/Attributes")
        {
            Content = new StringContent("""
                { "@odata.type": "Microsoft.Dynamics.CRM.IntegerAttributeMetadata", "SchemaName": "new_Web", "LogicalName": "new_web",
                  "DisplayName": { "LocalizedLabels": [ { "Label": "Web", "LanguageCode": 1033 } ] } }
                """, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("intersect");
    }

    // ---- lookup targets ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAttribute_of_a_lookup_to_a_missing_entity_is_400_naming_it()
    {
        var entity = await NewEntityAsync("la");
        var missing = Name("missing");

        var fault = await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = entity, Attribute = Lookup("new_parentid", missing) }));
        fault.Should().Contain(missing);
        Store.GetEntity(entity)!.Attributes.Should().NotContain(a => a.LogicalName == "new_parentid");
    }

    [Fact]
    public async Task CreateAttribute_of_a_lookup_to_an_intersect_entity_is_400_naming_it()
    {
        var entity = await NewEntityAsync("li");
        var intersect = await NewIntersectAsync();

        var fault = await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = entity, Attribute = Lookup("new_pairid", intersect) }));
        fault.Should().Contain(intersect).And.Contain("intersect");
    }

    [Fact]
    public async Task CreateEntity_with_a_lookup_to_a_missing_or_intersect_entity_is_400_naming_it()
    {
        var missing = Name("gone");
        var intersect = await NewIntersectAsync();

        var toMissing = Name("em");
        var fault = await BadRequestAsync(() => Admin.ExecuteAsync(CreateEntity(toMissing, Lookup("new_parentid", missing))));
        fault.Should().Contain(missing);
        Store.GetEntity(toMissing).Should().BeNull();

        var toIntersect = Name("ei");
        fault = await BadRequestAsync(() => Admin.ExecuteAsync(CreateEntity(toIntersect, Lookup("new_pairid", intersect))));
        fault.Should().Contain(intersect).And.Contain("intersect");
        Store.GetEntity(toIntersect).Should().BeNull();
    }

    [Fact]
    public async Task CreateEntity_with_a_lookup_to_itself_or_an_existing_entity_succeeds()
    {
        var parent = await NewEntityAsync("p");
        var name = Name("self");

        await Admin.ExecuteAsync(CreateEntity(name, Lookup("new_selfid", name), Lookup("new_parentid", parent)));

        Store.GetEntity(name)!.Attributes.Should().Contain(a => a.LogicalName == "new_selfid").And.Contain(a => a.LogicalName == "new_parentid");
    }
}

/// <summary>Builds an UpdateAttribute body of the stored attribute's type.</summary>
file static class AttributeChanges
{
    /// <summary>An attribute of the stored type of <paramref name="changes"/>' column, with its LogicalName and DisplayName.</summary>
    public static AttributeMetadata WithType(this AttributeMetadata changes, IMetadataStore store, string entity)
    {
        var current = store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == changes.LogicalName);
        var typed = (AttributeMetadata)Activator.CreateInstance(current.GetType())!;
        typed.LogicalName = changes.LogicalName;
        typed.DisplayName = changes.DisplayName;
        return typed;
    }
}

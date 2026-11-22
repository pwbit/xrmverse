using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
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
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The SDK metadata messages (CreateEntity, CreateAttribute, CreateOneToMany, CreateManyToMany, CreateOptionSet, Update*,
/// InsertOptionValue, Delete*, Retrieve*, PublishXml, PublishAllXml) through <see cref="OrganizationService"/> and through
/// the SOAP endpoint, with their privileges and the components a delete must not orphan.
/// </summary>
[Collection("xrmd")]
public sealed class MetadataMessageTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string SoapPath = "XRMServices/2011/Organization.svc/web";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Services = "http://schemas.microsoft.com/xrm/2011/Contracts/Services";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => TestEntities.EnsureProjectTagAsync(Store, fx.ConnectionString);

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private string Name(string label) => $"new_{label}{_run}";

    private static CreateEntityRequest CreateEntity(string logicalName) => new()
    {
        Entity = new EntityMetadata
        {
            LogicalName = logicalName,
            SchemaName = logicalName,
            DisplayName = Label("Table " + logicalName),
            DisplayCollectionName = Label("Tables " + logicalName),
            OwnershipType = OwnershipTypes.UserOwned,
        },
        PrimaryAttribute = new StringAttributeMetadata
        {
            LogicalName = "new_name",
            SchemaName = "new_Name",
            MaxLength = 100,
            DisplayName = Label("Name"),
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
        },
        SolutionUniqueName = "Default",
    };

    private async Task<string> NewEntityAsync(string label, OrganizationService? service = null)
    {
        var name = Name(label);
        await (service ?? Admin).ExecuteAsync(CreateEntity(name));
        return name;
    }

    private async Task<(Guid ObjectId, OrganizationService Service)> UserWithRoleAsync(Guid role)
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await AssignRoleAsync(fx, user, role);
        return (objectId, ServiceFor(fx, objectId));
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    // ---- create -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateEntity_returns_EntityId_and_AttributeId()
    {
        var name = Name("ce");

        var response = (CreateEntityResponse)await Admin.ExecuteAsync(CreateEntity(name));

        var entity = Store.GetEntity(name)!;
        response.EntityId.Should().Be(entity.MetadataId!.Value);
        response.AttributeId.Should().Be(entity.Attributes.Single(a => a.LogicalName == "new_name").MetadataId!.Value);
        entity.PrimaryNameAttribute.Should().Be("new_name");

        // Usable right away.
        var id = await Admin.CreateAsync(new Entity(name) { ["new_name"] = "first" });
        (await Admin.RetrieveAsync(name, id, new ColumnSet("new_name"))).GetAttributeValue<string>("new_name").Should().Be("first");

        // CreateAttribute adds a column.
        var attribute = (CreateAttributeResponse)await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new IntegerAttributeMetadata { LogicalName = "new_count", SchemaName = "new_Count", MinValue = 0, MaxValue = 100 },
            SolutionUniqueName = "Default",
        });
        attribute.AttributeId.Should().Be(Store.GetEntity(name)!.Attributes.Single(a => a.LogicalName == "new_count").MetadataId!.Value);
        await Admin.UpdateAsync(new Entity(name, id) { ["new_count"] = 7 });

        // A second create of the same entity or attribute is a DuplicateRecord.
        var duplicate = await FaultAsync(() => Admin.ExecuteAsync(CreateEntity(name)));
        (duplicate.ErrorCode, duplicate.HttpStatus).Should().Be((ErrorCodes.DuplicateRecord, 412));
        duplicate = await FaultAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new IntegerAttributeMetadata { LogicalName = "new_count", SchemaName = "new_Count" },
        }));
        (duplicate.ErrorCode, duplicate.HttpStatus).Should().Be((ErrorCodes.DuplicateRecord, 412));
    }

    [Fact]
    public async Task CreateOneToMany_creates_lookup_and_relationship()
    {
        var parent = await NewEntityAsync("par");
        var child = await NewEntityAsync("chi");
        var schemaName = $"new_parent_children_{_run}";

        var response = (CreateOneToManyResponse)await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = schemaName, ReferencedEntity = parent, ReferencingEntity = child,
                ReferencedAttribute = parent + "id",
            },
            Lookup = new LookupAttributeMetadata { LogicalName = "new_parentid", SchemaName = "new_ParentId", DisplayName = Label("Parent") },
            SolutionUniqueName = "Default",
        });

        var lookup = Store.GetEntity(child)!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "new_parentid");
        response.AttributeId.Should().Be(lookup.MetadataId!.Value);
        lookup.Targets.Should().Equal(parent);

        var retrieved = (RetrieveRelationshipResponse)await Admin.ExecuteAsync(new RetrieveRelationshipRequest { Name = schemaName });
        var relationship = retrieved.RelationshipMetadata.Should().BeOfType<OneToManyRelationshipMetadata>().Subject;
        relationship.SchemaName.Should().Be(schemaName);
        relationship.MetadataId.Should().Be(response.RelationshipId);
        (relationship.ReferencedEntity, relationship.ReferencingEntity, relationship.ReferencingAttribute)
            .Should().Be((parent, child, "new_parentid"));
        ((RetrieveRelationshipResponse)await Admin.ExecuteAsync(new RetrieveRelationshipRequest { MetadataId = response.RelationshipId }))
            .RelationshipMetadata.SchemaName.Should().Be(schemaName);

        // The derived name is gone; RetrieveEntity lists the relationship under its SchemaName on both sides.
        Store.GetRelationship($"{child}_new_parentid_{parent}").Should().BeNull();
        var childMetadata = ((RetrieveEntityResponse)await Admin.ExecuteAsync(
            new RetrieveEntityRequest { LogicalName = child, EntityFilters = EntityFilters.All })).EntityMetadata;
        childMetadata.ManyToOneRelationships.Should().ContainSingle(r => r.SchemaName == schemaName);
        var parentMetadata = ((RetrieveEntityResponse)await Admin.ExecuteAsync(
            new RetrieveEntityRequest { LogicalName = parent, EntityFilters = EntityFilters.All })).EntityMetadata;
        parentMetadata.OneToManyRelationships.Should().ContainSingle(r => r.SchemaName == schemaName);

        // Associate through the relationship sets the lookup.
        var p = await Admin.CreateAsync(new Entity(parent) { ["new_name"] = "p" });
        var c = await Admin.CreateAsync(new Entity(child) { ["new_name"] = "c" });
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference(parent, p), Relationship = new Relationship(schemaName),
            RelatedEntities = [new EntityReference(child, c)],
        });
        (await Admin.RetrieveAsync(child, c, new ColumnSet("new_parentid"))).GetAttributeValue<EntityReference>("new_parentid").Id.Should().Be(p);

        // The saved name survives a reload of the cache.
        await Store.LoadAsync();
        Store.GetRelationship(schemaName).Should().BeOfType<OneToManyRelationshipMetadata>().Which.MetadataId.Should().Be(response.RelationshipId);

        // Deleting the relationship deletes its lookup and its row.
        await Admin.ExecuteAsync(new DeleteRelationshipRequest { Name = schemaName });
        Store.GetRelationship(schemaName).Should().BeNull();
        Store.GetEntity(child)!.Attributes.Should().NotContain(a => a.LogicalName == "new_parentid");
        (await ScalarAsync("SELECT COUNT(*) FROM meta.relationship WHERE schema_name = @n", ("@n", schemaName))).Should().Be(0);
    }

    [Fact]
    public async Task CreateManyToMany_and_CreateOptionSet_work()
    {
        var left = await NewEntityAsync("mml");
        var right = await NewEntityAsync("mmr");
        var schemaName = $"new_mm_{_run}";

        var created = (CreateManyToManyResponse)await Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata
            {
                SchemaName = schemaName, Entity1LogicalName = left, Entity2LogicalName = right,
            },
            IntersectEntitySchemaName = schemaName,
            SolutionUniqueName = "Default",
        });
        var relationship = ((RetrieveRelationshipResponse)await Admin.ExecuteAsync(
                new RetrieveRelationshipRequest { MetadataId = created.ManyToManyRelationshipId }))
            .RelationshipMetadata.Should().BeOfType<ManyToManyRelationshipMetadata>().Subject;
        relationship.SchemaName.Should().Be(schemaName);
        relationship.IntersectEntityName.Should().Be(schemaName);
        Store.GetEntity(schemaName)!.IsIntersect.Should().BeTrue();

        // A many-to-many relationship named like a security relationship is refused.
        var security = await FaultAsync(() => Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata
            {
                SchemaName = "systemuserroles_association", Entity1LogicalName = left, Entity2LogicalName = right,
                IntersectEntityName = "new_x" + _run,
            },
            IntersectEntitySchemaName = "new_x" + _run,
        }));
        security.HttpStatus.Should().Be(400);

        // A global option set: create, retrieve, add a value, relabel, bind, delete.
        var optionSetName = $"new_colors{_run}";
        var optionSet = (CreateOptionSetResponse)await Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(Label("Red"), 100000000)]) { Name = optionSetName, DisplayName = Label("Colors") },
            SolutionUniqueName = "Default",
        });
        var retrieved = ((RetrieveOptionSetResponse)await Admin.ExecuteAsync(new RetrieveOptionSetRequest { Name = optionSetName }))
            .OptionSetMetadata.Should().BeOfType<OptionSetMetadata>().Subject;
        retrieved.MetadataId.Should().Be(optionSet.OptionSetId);
        ((RetrieveOptionSetResponse)await Admin.ExecuteAsync(new RetrieveOptionSetRequest { MetadataId = optionSet.OptionSetId }))
            .OptionSetMetadata.Name.Should().Be(optionSetName);

        var inserted = (InsertOptionValueResponse)await Admin.ExecuteAsync(new InsertOptionValueRequest
        {
            OptionSetName = optionSetName, Label = Label("Green"),
        });
        inserted.NewOptionValue.Should().Be(100000001);
        var given = (InsertOptionValueResponse)await Admin.ExecuteAsync(new InsertOptionValueRequest
        {
            OptionSetName = optionSetName, Label = Label("Blue"), Value = 100000010,
        });
        given.NewOptionValue.Should().Be(100000010);
        var taken = await FaultAsync(() => Admin.ExecuteAsync(new InsertOptionValueRequest
        {
            OptionSetName = optionSetName, Label = Label("Again"), Value = 100000010,
        }));
        (taken.ErrorCode, taken.HttpStatus).Should().Be((ErrorCodes.DuplicateRecord, 412));

        await Admin.ExecuteAsync(new UpdateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata { Name = optionSetName, DisplayName = Label("Palette") },
        });
        var all = ((RetrieveAllOptionSetsResponse)await Admin.ExecuteAsync(new RetrieveAllOptionSetsRequest())).OptionSetMetadata;
        var palette = all.Should().ContainSingle(o => o.Name == optionSetName).Which.Should().BeOfType<OptionSetMetadata>().Subject;
        palette.DisplayName.LocalizedLabels.Single().Label.Should().Be("Palette");
        palette.Options.Select(o => o.Value).Should().Equal(100000000, 100000001, 100000010);

        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = left,
            Attribute = new PicklistAttributeMetadata
            {
                LogicalName = "new_color", SchemaName = "new_Color", OptionSet = new OptionSetMetadata { IsGlobal = true, Name = optionSetName },
            },
        });
        var bound = await FaultAsync(() => Admin.ExecuteAsync(new DeleteOptionSetRequest { Name = optionSetName }));
        bound.HttpStatus.Should().Be(400);
        bound.Message.Should().Be($"The component cannot be deleted because it is referenced by attribute '{left}.new_color'.");

        // A local option set gets a value through InsertOptionValue too.
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = right,
            Attribute = new PicklistAttributeMetadata
            {
                LogicalName = "new_size", SchemaName = "new_Size",
                OptionSet = new OptionSetMetadata([new OptionMetadata(Label("S"), 1)]) { IsGlobal = false },
            },
        });
        var local = (InsertOptionValueResponse)await Admin.ExecuteAsync(new InsertOptionValueRequest
        {
            EntityLogicalName = right, AttributeLogicalName = "new_size", Label = Label("M"),
        });
        local.NewOptionValue.Should().Be(100000000);
        ((PicklistAttributeMetadata)Store.GetEntity(right)!.Attributes.Single(a => a.LogicalName == "new_size"))
            .OptionSet.Options.Select(o => o.Value).Should().Equal(1, 100000000);

        // Update, then delete: the attribute, the relationship, the option set.
        await Admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = left, DisplayName = Label("Renamed") },
        });
        Store.GetEntity(left)!.DisplayName.LocalizedLabels.Single().Label.Should().Be("Renamed");
        await Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = left, Attribute = new StringAttributeMetadata { LogicalName = "new_name", MaxLength = 200 },
        });
        ((StringAttributeMetadata)Store.GetEntity(left)!.Attributes.Single(a => a.LogicalName == "new_name")).MaxLength.Should().Be(200);

        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = left, LogicalName = "new_color" });
        await Admin.ExecuteAsync(new DeleteOptionSetRequest { Name = optionSetName });
        Store.GetOptionSet(optionSetName).Should().BeNull();
        await Admin.ExecuteAsync(new DeleteRelationshipRequest { Name = schemaName });
        Store.GetRelationship(schemaName).Should().BeNull();
        Store.GetEntity(schemaName).Should().BeNull();

        var entities = ((RetrieveAllEntitiesResponse)await Admin.ExecuteAsync(
            new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity })).EntityMetadata;
        entities.Select(e => e.LogicalName).Should().Contain([left, right, "systemuser"]).And.NotContain(schemaName);
    }

    // ---- privileges -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Metadata_writes_require_their_privilege()
    {
        var (_, basic) = await UserWithRoleAsync(StandardRoles.BasicUser);
        var (_, customizer) = await UserWithRoleAsync(StandardRoles.SystemCustomizer);
        var existing = await NewEntityAsync("prv");
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = existing, Attribute = new IntegerAttributeMetadata { LogicalName = "new_n", SchemaName = "new_N" },
        });

        async Task DeniedAsync(OrganizationRequest request, string privilege)
        {
            var fault = await FaultAsync(() => basic.ExecuteAsync(request));
            (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.PrivilegeDenied, 403));
            fault.Message.Should().Contain(privilege);
        }

        await DeniedAsync(CreateEntity(Name("den")), PrivilegeNames.CreateEntity);
        await DeniedAsync(new CreateAttributeRequest
        {
            EntityName = existing, Attribute = new IntegerAttributeMetadata { LogicalName = "new_m", SchemaName = "new_M" },
        }, PrivilegeNames.CreateAttribute);
        await DeniedAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = existing, DisplayName = Label("x") } },
            "prvWriteEntity");
        await DeniedAsync(new UpdateAttributeRequest
        {
            EntityName = existing, Attribute = new IntegerAttributeMetadata { LogicalName = "new_n", DisplayName = Label("x") },
        }, "prvWriteAttribute");
        await DeniedAsync(new DeleteAttributeRequest { EntityLogicalName = existing, LogicalName = "new_n" }, "prvDeleteAttribute");
        await DeniedAsync(new DeleteEntityRequest { LogicalName = existing }, "prvDeleteEntity");
        await DeniedAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata
            {
                SchemaName = "new_denied" + _run, Entity1LogicalName = existing, Entity2LogicalName = existing,
            },
            IntersectEntitySchemaName = "new_denied" + _run,
        }, "prvCreateRelationship");
        await DeniedAsync(new DeleteRelationshipRequest { Name = TestEntities.ProjectTag }, "prvDeleteRelationship");
        await DeniedAsync(new CreateOptionSetRequest { OptionSet = new OptionSetMetadata { Name = "new_denied" + _run } }, "prvCreateOptionSet");
        await DeniedAsync(new UpdateOptionSetRequest { OptionSet = new OptionSetMetadata { Name = "new_denied" + _run } }, "prvWriteOptionSet");
        await DeniedAsync(new DeleteOptionSetRequest { Name = "new_denied" + _run }, "prvDeleteOptionSet");
        await DeniedAsync(new PublishAllXmlRequest(), "prvPublishCustomization");
        Store.GetEntity(existing)!.Attributes.Should().Contain(a => a.LogicalName == "new_n");

        // The reads need no privilege.
        (await basic.ExecuteAsync(new RetrieveAllOptionSetsRequest())).Should().BeOfType<RetrieveAllOptionSetsResponse>();

        // System Customizer may do all of it.
        var mine = await NewEntityAsync("cus", customizer);
        await customizer.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = mine, DisplayName = Label("Mine") } });
        await customizer.ExecuteAsync(new CreateOptionSetRequest { OptionSet = new OptionSetMetadata { Name = "new_cus" + _run } });
        await customizer.ExecuteAsync(new DeleteOptionSetRequest { Name = "new_cus" + _run });
        await customizer.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = existing, LogicalName = "new_n" });
        await customizer.ExecuteAsync(new PublishAllXmlRequest());
        await customizer.ExecuteAsync(new DeleteEntityRequest { LogicalName = mine });
        Store.GetEntity(mine).Should().BeNull();
    }

    [Fact]
    public async Task System_components_change_only_their_labels_for_a_customizer()
    {
        var (_, customizer) = await UserWithRoleAsync(StandardRoles.SystemCustomizer);
        var custom = await NewEntityAsync("sys", customizer);

        async Task OnlyLabelsAsync(OrganizationRequest request)
        {
            var fault = await FaultAsync(() => customizer.ExecuteAsync(request));
            (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.PrivilegeDenied, 403));
        }

        // Attributes of a system entity: RequiredLevel, MaxLength (an ALTER COLUMN), new options; InsertOptionValue.
        await OnlyLabelsAsync(new UpdateAttributeRequest
        {
            EntityName = "systemuser",
            Attribute = new StringAttributeMetadata
            {
                LogicalName = "domainname", RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
            },
        });
        await OnlyLabelsAsync(new UpdateAttributeRequest
        {
            EntityName = "systemuser", Attribute = new StringAttributeMetadata { LogicalName = "domainname", MaxLength = 2000 },
        });
        await OnlyLabelsAsync(new UpdateAttributeRequest
        {
            EntityName = "systemuser",
            Attribute = new StatusAttributeMetadata
            {
                LogicalName = "statuscode", OptionSet = new OptionSetMetadata([new StatusOptionMetadata(3, 1) { Label = Label("Gone") }]),
            },
        });
        await OnlyLabelsAsync(new InsertOptionValueRequest
        {
            EntityLogicalName = "systemuser", AttributeLogicalName = "statuscode", Label = Label("Gone"),
        });
        // A system attribute of a custom entity (its primary name).
        await OnlyLabelsAsync(new UpdateAttributeRequest
        {
            EntityName = custom, Attribute = new StringAttributeMetadata { LogicalName = "new_name", MaxLength = 300 },
        });
        ((StringAttributeMetadata)Store.GetEntity("systemuser")!.Attributes.Single(a => a.LogicalName == "domainname")).MaxLength.Should().Be(1024);
        Store.GetEntity("systemuser")!.Attributes.OfType<StatusAttributeMetadata>().Single().OptionSet.Options.Should().HaveCount(2);

        // Deleting a system entity or attribute: 403.
        await OnlyLabelsAsync(new DeleteEntityRequest { LogicalName = "systemuser" });
        await OnlyLabelsAsync(new DeleteAttributeRequest { EntityLogicalName = "systemuser", LogicalName = "domainname" });

        // Labels are allowed, and so is resending the current values.
        var label = Store.GetEntity("systemuser")!.Attributes.Single(a => a.LogicalName == "domainname").DisplayName;
        await customizer.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = "systemuser",
            Attribute = new StringAttributeMetadata { LogicalName = "domainname", DisplayName = Label("Domain " + _run), MaxLength = 1024 },
        });
        Store.GetEntity("systemuser")!.Attributes.Single(a => a.LogicalName == "domainname").DisplayName.LocalizedLabels.Single().Label
            .Should().Be("Domain " + _run);
        await customizer.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = "systemuser", Description = Label("Users " + _run) },
        });
        if (label is { LocalizedLabels.Count: > 0 })
            await customizer.ExecuteAsync(new UpdateAttributeRequest
            {
                EntityName = "systemuser", Attribute = new StringAttributeMetadata { LogicalName = "domainname", DisplayName = label },
            });

        // An unrestricted caller may change more.
        await Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = custom, Attribute = new StringAttributeMetadata { LogicalName = "new_name", MaxLength = 300 },
        });
        ((StringAttributeMetadata)Store.GetEntity(custom)!.Attributes.Single(a => a.LogicalName == "new_name")).MaxLength.Should().Be(300);
    }

    [Fact]
    public async Task InsertOptionValue_takes_a_colour()
    {
        var optionSetName = $"new_hue{_run}";
        await Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(Label("Blue"), 100000000)]) { Name = optionSetName, DisplayName = Label("Hue") },
        });

        var inserted = (InsertOptionValueResponse)await Admin.ExecuteAsync(new InsertOptionValueRequest
        {
            OptionSetName = optionSetName, Label = Label("Red"), Parameters = { ["Color"] = "#D13438" },
        });

        var set = (OptionSetMetadata)((RetrieveOptionSetResponse)await Admin.ExecuteAsync(new RetrieveOptionSetRequest { Name = optionSetName })).OptionSetMetadata;
        set.Options.Single(o => o.Value == inserted.NewOptionValue).Color.Should().Be("#D13438");
        set.Options.Single(o => o.Value == 100000000).Color.Should().BeNullOrEmpty();

        var bad = await BadRequestAsync(() => Admin.ExecuteAsync(new InsertOptionValueRequest
        {
            OptionSetName = optionSetName, Label = Label("Pink"), Parameters = { ["Color"] = "red" },
        }));
        bad.Should().Contain("not #RRGGBB").And.Contain("'red'");
        ((OptionSetMetadata)Store.GetOptionSet(optionSetName)!).Options.Should().HaveCount(2);
    }

    [Fact]
    public async Task InsertOptionValue_with_an_empty_OptionSetName_requires_prvWriteAttribute()
    {
        var entity = await NewEntityAsync("iov");
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = entity,
            Attribute = new PicklistAttributeMetadata
            {
                LogicalName = "new_size", SchemaName = "new_Size",
                OptionSet = new OptionSetMetadata([new OptionMetadata(Label("S"), 1)]) { IsGlobal = false },
            },
        });
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, (PrivilegeNames.WriteOptionSet, DepthMask.Global));

        var fault = await FaultAsync(() => ServiceFor(fx, objectId).ExecuteAsync(new InsertOptionValueRequest
        {
            OptionSetName = "", EntityLogicalName = entity, AttributeLogicalName = "new_size", Label = Label("M"),
        }));

        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.PrivilegeDenied, 403));
        fault.Message.Should().Contain(PrivilegeNames.WriteAttribute);
        ((PicklistAttributeMetadata)Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == "new_size")).OptionSet.Options.Should().HaveCount(1);
    }

    // ---- UpdateOptionValue ------------------------------------------------------------------------------------------

    private static UpdateOptionValueRequest UpdateOption(int value, Label? label = null, bool mergeLabels = true, object? color = null)
    {
        var request = new UpdateOptionValueRequest { Value = value, MergeLabels = mergeLabels };
        if (label is not null) request.Label = label;
        if (color is not null) request.Parameters["Color"] = color;
        return request;
    }

    private static OptionMetadata LocalOption(EntityMetadata entity, string attribute, int value) =>
        ((EnumAttributeMetadata)entity.Attributes.Single(a => a.LogicalName == attribute)).OptionSet.Options.Single(o => o.Value == value);

    [Fact]
    public async Task UpdateOptionValue_recolours_and_relabels()
    {
        // A global option set, with a label in two languages.
        var optionSetName = $"new_upd{_run}";
        var blue = Label("Blue");
        blue.LocalizedLabels.Add(new LocalizedLabel("Bleu", 1036));
        await Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(blue, 100000000), new OptionMetadata(Label("Red"), 100000001)])
            {
                Name = optionSetName, DisplayName = Label("Upd"),
            },
        });
        async Task<OptionMetadata> GlobalOptionAsync(int value) =>
            ((OptionSetMetadata)((RetrieveOptionSetResponse)await Admin.ExecuteAsync(new RetrieveOptionSetRequest { Name = optionSetName }))
                .OptionSetMetadata).Options.Single(o => o.Value == value);

        var response = await Admin.ExecuteAsync(Global(UpdateOption(100000000, Label("Azure"), mergeLabels: true, color: "#0078D4")));
        response.Should().BeOfType<UpdateOptionValueResponse>();
        var merged = await GlobalOptionAsync(100000000);
        merged.Color.Should().Be("#0078D4");
        merged.Label.LocalizedLabels.Select(l => (l.Label, l.LanguageCode)).Should().BeEquivalentTo([("Azure", 1033), ("Bleu", 1036)]);
        (await GlobalOptionAsync(100000001)).Color.Should().BeNullOrEmpty();

        // MergeLabels false: the label given is the whole label; no Color leaves the colour.
        await Admin.ExecuteAsync(Global(UpdateOption(100000000, Label("Sky"), mergeLabels: false)));
        var replaced = await GlobalOptionAsync(100000000);
        replaced.Label.LocalizedLabels.Select(l => (l.Label, l.LanguageCode)).Should().BeEquivalentTo([("Sky", 1033)]);
        replaced.Color.Should().Be("#0078D4");

        // An empty Color clears it (ruling R1); the label stays.
        await Admin.ExecuteAsync(Global(UpdateOption(100000000, color: "")));
        var cleared = await GlobalOptionAsync(100000000);
        cleared.Color.Should().BeNullOrEmpty();
        cleared.Label.LocalizedLabels.Single().Label.Should().Be("Sky");

        // A local Picklist and the statuscode of a custom table.
        var entity = await NewEntityAsync("uov");
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = entity,
            Attribute = new PicklistAttributeMetadata
            {
                LogicalName = "new_size", SchemaName = "new_Size",
                OptionSet = new OptionSetMetadata([new OptionMetadata(Label("S"), 1)]) { IsGlobal = false },
            },
        });
        await Admin.ExecuteAsync(Local(UpdateOption(1, Label("Small"), color: "#107C10"), entity, "new_size"));
        await Admin.ExecuteAsync(Local(UpdateOption(1, Label("Live"), color: "#0078D4"), entity, "statuscode"));

        var local = LocalOption(Store.GetEntity(entity)!, "new_size", 1);
        (local.Color, local.Label.LocalizedLabels.Single().Label).Should().Be(("#107C10", "Small"));
        var status = LocalOption(Store.GetEntity(entity)!, "statuscode", 1);
        (status.Color, status.Label.LocalizedLabels.Single(l => l.LanguageCode == 1033).Label).Should().Be(("#0078D4", "Live"));
        status.Should().BeOfType<StatusOptionMetadata>().Which.State.Should().Be(0);

        UpdateOptionValueRequest Global(UpdateOptionValueRequest request)
        {
            request.OptionSetName = optionSetName;
            return request;
        }
    }

    private static UpdateOptionValueRequest Local(UpdateOptionValueRequest request, string entity, string attribute)
    {
        request.EntityLogicalName = entity;
        request.AttributeLogicalName = attribute;
        return request;
    }

    [Fact]
    public async Task UpdateOptionValue_by_a_customizer_on_a_system_choice_changes_only_colour_and_labels()
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, false, (PrivilegeNames.WriteAttribute, DepthMask.Global));
        var customizer = ServiceFor(fx, objectId);

        var label = MetadataSerializer.Clone(LocalOption(Store.GetEntity("systemuser")!, "statuscode", 1).Label);
        await customizer.ExecuteAsync(Local(UpdateOption(1, label, color: "#0078D4"), "systemuser", "statuscode"));
        LocalOption(Store.GetEntity("systemuser")!, "statuscode", 1).Color.Should().Be("#0078D4");

        // Cleared again: the seeded table is shared by every test.
        await customizer.ExecuteAsync(Local(UpdateOption(1, color: ""), "systemuser", "statuscode"));
        LocalOption(Store.GetEntity("systemuser")!, "statuscode", 1).Color.Should().BeNullOrEmpty();

        // A new option is still more than a label.
        var fault = await FaultAsync(() => customizer.ExecuteAsync(new InsertOptionValueRequest
        {
            EntityLogicalName = "systemuser", AttributeLogicalName = "statuscode", Label = Label("Gone"),
        }));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.PrivilegeDenied, 403));

        // Without prvWriteOptionSet, a global option set is out of reach.
        var global = await FaultAsync(() => customizer.ExecuteAsync(new UpdateOptionValueRequest
        {
            OptionSetName = "new_any" + _run, Value = 1, MergeLabels = true,
        }));
        (global.ErrorCode, global.HttpStatus).Should().Be((ErrorCodes.PrivilegeDenied, 403));
        global.Message.Should().Contain(PrivilegeNames.WriteOptionSet);
    }

    [Fact]
    public async Task The_option_value_actions_work_on_the_Web_API()
    {
        var optionSetName = $"new_uow{_run}";
        await Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(Label("One"), 1)]) { Name = optionSetName, DisplayName = Label("Uow") },
        });
        async Task<(HttpStatusCode Status, string Body)> PostAsync(string action, string json)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/" + action)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            var res = await fx.Client.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }

        var inserted = await PostAsync("InsertOptionValue", $$"""
            { "OptionSetName": "{{optionSetName}}", "Value": 2, "Color": "#D13438",
              "Label": { "LocalizedLabels": [{ "Label": "Two", "LanguageCode": 1033 }] } }
            """);
        inserted.Status.Should().Be(HttpStatusCode.OK, inserted.Body);
        System.Text.Json.Nodes.JsonNode.Parse(inserted.Body)!["NewOptionValue"]!.GetValue<int>().Should().Be(2);

        var updated = await PostAsync("UpdateOptionValue", $$"""
            { "OptionSetName": "{{optionSetName}}", "Value": 1, "Color": "#0078D4", "MergeLabels": true,
              "Label": { "LocalizedLabels": [{ "Label": "Uno", "LanguageCode": 1033 }] } }
            """);
        updated.Status.Should().Be(HttpStatusCode.NoContent, updated.Body);
        var cleared = await PostAsync("UpdateOptionValue", $$"""{ "OptionSetName": "{{optionSetName}}", "Value": 2, "Color": null, "MergeLabels": true }""");
        cleared.Status.Should().Be(HttpStatusCode.NoContent, cleared.Body);

        var options = ((OptionSetMetadata)Store.GetOptionSet(optionSetName)!).Options;
        options.Single(o => o.Value == 1).Should().Match<OptionMetadata>(o => o.Color == "#0078D4" && o.Label.LocalizedLabels.Single().Label == "Uno");
        options.Single(o => o.Value == 2).Color.Should().BeNullOrEmpty();
        options.Single(o => o.Value == 2).Label.LocalizedLabels.Single().Label.Should().Be("Two");

        var refused = await PostAsync("UpdateOptionValue", $$"""{ "OptionSetName": "{{optionSetName}}", "Value": 1, "MergeLabels": true, "Color": 5 }""");
        refused.Status.Should().Be(HttpStatusCode.BadRequest);
        refused.Body.Should().Contain("Color");
    }

    [Fact]
    public async Task UpdateOptionValue_refusals()
    {
        var optionSetName = $"new_uor{_run}";
        await Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(Label("One"), 1)]) { Name = optionSetName, DisplayName = Label("Uor") },
        });
        UpdateOptionValueRequest Global(int value = 1, object? color = null)
        {
            var request = UpdateOption(value, color: color);
            request.OptionSetName = optionSetName;
            return request;
        }

        var missing = await FaultAsync(() => Admin.ExecuteAsync(Global(999)));
        (missing.ErrorCode, missing.HttpStatus).Should().Be((ErrorCodes.ObjectDoesNotExist, 404));
        missing.Message.Should().Contain("Option value 999 does not exist in");

        var noValue = Global();
        noValue.Parameters.Remove("Value");
        (await BadRequestAsync(() => Admin.ExecuteAsync(noValue))).Should().Contain("Value");

        var parents = Global();
        parents.ParentValues = [1];
        (await BadRequestAsync(() => Admin.ExecuteAsync(parents))).Should().Contain("ParentValues").And.Contain("not supported by xrmverse");
        var external = Global();
        external.Parameters["ExternalValue"] = "x";
        (await BadRequestAsync(() => Admin.ExecuteAsync(external))).Should().Contain("ExternalValue");
        var hidden = Global();
        hidden.Parameters["IsHidden"] = true;
        (await BadRequestAsync(() => Admin.ExecuteAsync(hidden))).Should().Contain("IsHidden");
        var hiddenInsert = new InsertOptionValueRequest { OptionSetName = optionSetName, Label = Label("Hidden") };
        hiddenInsert.Parameters["IsHidden"] = true;
        (await BadRequestAsync(() => Admin.ExecuteAsync(hiddenInsert))).Should().Contain("IsHidden");

        // A Label without localized labels would clear the label: refused, naming Label.
        var noLabels = Global();
        noLabels.Label = new Label();
        (await BadRequestAsync(() => Admin.ExecuteAsync(noLabels))).Should().Contain("Label");
        ((OptionSetMetadata)Store.GetOptionSet(optionSetName)!).Options.Single().Label.LocalizedLabels.Single().Label.Should().Be("One");

        (await BadRequestAsync(() => Admin.ExecuteAsync(Global(color: "nope")))).Should().Contain("'nope'");
        (await BadRequestAsync(() => Admin.ExecuteAsync(Global(color: 5)))).Should().Contain("Color");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new InsertOptionValueRequest
        {
            OptionSetName = optionSetName, Label = Label("Two"), Parameters = { ["Color"] = 5 },
        }))).Should().Contain("Color");

        var entity = await NewEntityAsync("uos");
        var state = await BadRequestAsync(() => Admin.ExecuteAsync(Local(UpdateOption(0, Label("On"), color: "#FFFFFF"), entity, "statecode")));
        state.Should().Contain("statecode").And.Contain("State");
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = entity,
            Attribute = new BooleanAttributeMetadata
            {
                LogicalName = "new_flag", SchemaName = "new_Flag",
                OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(Label("Yes"), 1), new OptionMetadata(Label("No"), 0)),
            },
        });
        var boolean = await BadRequestAsync(() => Admin.ExecuteAsync(Local(UpdateOption(1, Label("Sure"), color: "#FFFFFF"), entity, "new_flag")));
        boolean.Should().Contain("new_flag").And.Contain("Boolean");
        ((BooleanAttributeMetadata)Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == "new_flag")).OptionSet.TrueOption.Color
            .Should().BeNullOrEmpty();

        var set = (OptionSetMetadata)Store.GetOptionSet(optionSetName)!;
        set.Options.Should().ContainSingle().Which.Color.Should().BeNullOrEmpty();
        LocalOption(Store.GetEntity(entity)!, "statecode", 0).Color.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Option_value_messages_accept_IsHidden_false_as_the_default()
    {
        var optionSetName = $"new_uoh{_run}";
        await Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(Label("One"), 1)]) { Name = optionSetName, DisplayName = Label("Uoh") },
        });

        var update = UpdateOption(1, color: "#0078D4");
        update.OptionSetName = optionSetName;
        update.Parameters["IsHidden"] = false;
        await Admin.ExecuteAsync(update);
        var insert = new InsertOptionValueRequest { OptionSetName = optionSetName, Value = 2, Label = Label("Two") };
        insert.Parameters["IsHidden"] = false;
        await Admin.ExecuteAsync(insert);

        var options = ((OptionSetMetadata)Store.GetOptionSet(optionSetName)!).Options;
        options.Single(o => o.Value == 1).Color.Should().Be("#0078D4");
        options.Select(o => o.Value).Should().BeEquivalentTo([1, 2]);

        // The Web API, where IsHidden false is the default Learn documents.
        async Task<(HttpStatusCode Status, string Body)> PostAsync(string action, string json)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/" + action)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            var res = await fx.Client.SendAsync(req);
            return (res.StatusCode, await res.Content.ReadAsStringAsync());
        }
        var accepted = await PostAsync("UpdateOptionValue", $$"""{ "OptionSetName": "{{optionSetName}}", "Value": 2, "Color": "#D13438", "MergeLabels": true, "IsHidden": false }""");
        accepted.Status.Should().Be(HttpStatusCode.NoContent, accepted.Body);
        var refused = await PostAsync("InsertOptionValue", $$"""
            { "OptionSetName": "{{optionSetName}}", "Value": 3, "IsHidden": true,
              "Label": { "LocalizedLabels": [{ "Label": "Three", "LanguageCode": 1033 }] } }
            """);
        refused.Status.Should().Be(HttpStatusCode.BadRequest);
        refused.Body.Should().Contain("IsHidden");
        var noLabels = await PostAsync("UpdateOptionValue", $$"""{ "OptionSetName": "{{optionSetName}}", "Value": 2, "MergeLabels": false, "Label": { "LocalizedLabels": [] } }""");
        noLabels.Status.Should().Be(HttpStatusCode.BadRequest);
        noLabels.Body.Should().Contain("Label");

        options = ((OptionSetMetadata)Store.GetOptionSet(optionSetName)!).Options;
        options.Single(o => o.Value == 2).Should().Match<OptionMetadata>(o => o.Color == "#D13438" && o.Label.LocalizedLabels.Single().Label == "Two");
        options.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateOptionValue_on_a_MultiSelectPicklist()
    {
        var entity = await NewEntityAsync("uom");
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = entity,
            Attribute = new MultiSelectPicklistAttributeMetadata
            {
                LogicalName = "new_tags", SchemaName = "new_Tags",
                OptionSet = new OptionSetMetadata([new OptionMetadata(Label("Red"), 1), new OptionMetadata(Label("Blue"), 2)]) { IsGlobal = false },
            },
        });

        await Admin.ExecuteAsync(Local(UpdateOption(2, Label("Navy"), color: "#0078D4"), entity, "new_tags"));

        var updated = LocalOption(Store.GetEntity(entity)!, "new_tags", 2);
        (updated.Color, updated.Label.LocalizedLabels.Single().Label).Should().Be(("#0078D4", "Navy"));
        LocalOption(Store.GetEntity(entity)!, "new_tags", 1).Color.Should().BeNullOrEmpty();
        Store.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == "new_tags").Should().BeOfType<MultiSelectPicklistAttributeMetadata>();
    }

    [Theory]
    [InlineData("relationshipdefinitions")]
    [InlineData("whoami")]
    [InlineData("entitydefinitions")]
    public async Task CreateEntity_refuses_a_reserved_EntitySetName(string setName)
    {
        var request = CreateEntity(Name("res"));
        request.Entity.EntitySetName = setName;

        var fault = await FaultAsync(() => Admin.ExecuteAsync(request));

        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.InvalidArgument, 400));
        Store.GetEntity(request.Entity.LogicalName).Should().BeNull();
    }

    [Fact]
    public async Task CreateOneToMany_refuses_a_security_relationship_name()
    {
        var parent = await NewEntityAsync("sp");
        var child = await NewEntityAsync("sc");

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = "teammembership_association", ReferencedEntity = parent, ReferencingEntity = child,
            },
            Lookup = new LookupAttributeMetadata { LogicalName = "new_parentid", SchemaName = "new_ParentId" },
        }));

        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.InvalidArgument, 400));
        Store.GetEntity(child)!.Attributes.Should().NotContain(a => a.LogicalName == "new_parentid");
    }

    // ---- delete -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_of_a_referenced_component_is_refused()
    {
        var target = await NewEntityAsync("tgt");
        var referencing = await NewEntityAsync("ref");
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = referencing,
            Attribute = new LookupAttributeMetadata { LogicalName = "new_targetid", SchemaName = "new_TargetId", Targets = [target] },
        });

        var lookup = await FaultAsync(() => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = target }));
        (lookup.ErrorCode, lookup.HttpStatus).Should().Be((ErrorCodes.InvalidArgument, 400));
        lookup.Message.Should().Be($"The component cannot be deleted because it is referenced by attribute '{referencing}.new_targetid'.");

        // A step registered on the entity.
        await ExecAsync("""
            INSERT INTO data.sdkmessageprocessingstep (sdkmessageprocessingstepid, name, sdkmessageid, sdkmessagefilterid, stage, mode, rank, statecode, statuscode)
            VALUES (NEWID(), @name, @message, @filter, 20, 0, 1, 0, 1);
            """, ("@name", "step on " + referencing), ("@message", SdkMessageCatalog.MessageId("Create")),
            ("@filter", SdkMessageCatalog.FilterId("Create", referencing)));
        var step = await FaultAsync(() => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = referencing }));
        (step.ErrorCode, step.HttpStatus).Should().Be((ErrorCodes.InvalidArgument, 400));
        step.Message.Should().Be($"The component cannot be deleted because it is referenced by step 'step on {referencing}'.");
        Store.GetEntity(referencing).Should().NotBeNull();

        // A side of a many-to-many relationship.
        var side = await FaultAsync(() => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = TestEntities.Tag }));
        side.HttpStatus.Should().Be(400);

        // System components: 403; unknown ones: 404.
        var system = await FaultAsync(() => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = "systemuser" }));
        (system.ErrorCode, system.HttpStatus).Should().Be((ErrorCodes.PrivilegeDenied, 403));
        var systemAttribute = await FaultAsync(() => Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = target, LogicalName = "createdon" }));
        (systemAttribute.ErrorCode, systemAttribute.HttpStatus).Should().Be((ErrorCodes.PrivilegeDenied, 403));
        (await FaultAsync(() => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = "new_nosuch" + _run }))).HttpStatus.Should().Be(404);
        (await FaultAsync(() => Admin.ExecuteAsync(new DeleteOptionSetRequest { Name = "new_nosuch" + _run }))).HttpStatus.Should().Be(404);
        (await FaultAsync(() => Admin.ExecuteAsync(new DeleteRelationshipRequest { Name = "new_nosuch" + _run }))).HttpStatus.Should().Be(404);
        (await FaultAsync(() => Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = target, LogicalName = "new_nosuch" })))
            .HttpStatus.Should().Be(404);

        // Without the lookup the target goes, with its table.
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = referencing, LogicalName = "new_targetid" });
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = target });
        Store.GetEntity(target).Should().BeNull();
        (await TestSql.TableExistsAsync(fx.ConnectionString, "data", target)).Should().BeFalse();
    }

    [Fact]
    public async Task PublishAllXml_is_a_successful_noop()
    {
        var name = await NewEntityAsync("pub");

        (await Admin.ExecuteAsync(new PublishAllXmlRequest())).Should().BeOfType<PublishAllXmlResponse>();
        (await Admin.ExecuteAsync(new PublishXmlRequest { ParameterXml = $"<importexportxml><entities><entity>{name}</entity></entities></importexportxml>" }))
            .Should().BeOfType<PublishXmlResponse>();

        Store.GetEntity(name).Should().NotBeNull();
    }

    [Fact]
    public async Task Metadata_writes_are_refused_inside_a_plugin()
    {
        var request = CreateEntity(Name("plg"));
        var core = fx.App.Services.CreateScope().ServiceProvider.GetRequiredService<CoreOperations>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);

        core.IsNonPipelineMessage("CreateEntity").Should().BeTrue();
        var fault = await FaultAsync(() => core.ExecuteNonPipelineAsync(request, Caller.Internal, new DataSession(conn, null), default));
        fault.HttpStatus.Should().Be(400);
        Store.GetEntity(request.Entity.LogicalName).Should().BeNull();

        // Reads are answered.
        var read = await FaultAsync(() => core.ExecuteNonPipelineAsync(new RetrieveOptionSetRequest { Name = "nonexistent" + _run },
            Caller.Internal, new DataSession(conn, null), default));
        read.HttpStatus.Should().Be(404);
    }

    // ---- SOAP -------------------------------------------------------------------------------------------------------

    private async Task<OrganizationResponse> SoapAsync(OrganizationRequest request)
    {
        var serializer = new DataContractSerializer(typeof(OrganizationRequest), new DataContractSerializerSettings
        {
            RootName = new XmlDictionary().Add("request"),
            RootNamespace = new XmlDictionary().Add(Services.NamespaceName),
            DataContractResolver = new KnownTypesResolver(),
        });
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            w.WriteStartElement("s", "Envelope", Soap.NamespaceName);
            w.WriteStartElement("s", "Body", Soap.NamespaceName);
            w.WriteStartElement("Execute", Services.NamespaceName);
            serializer.WriteObject(w, request);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, SoapPath) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var result = XDocument.Parse(body).Root!.Element(Soap + "Body")!.Element(Services + "ExecuteResponse")!.Element(Services + "ExecuteResult")!;
        var reader = new DataContractSerializer(typeof(OrganizationResponse), new DataContractSerializerSettings
        {
            RootName = new XmlDictionary().Add("ExecuteResult"),
            RootNamespace = new XmlDictionary().Add(Services.NamespaceName),
            DataContractResolver = new KnownTypesResolver(),
        });
        using var xml = result.CreateReader();
        return (OrganizationResponse)reader.ReadObject(xml)!;
    }

    [Fact]
    public async Task Metadata_messages_round_trip_through_soap()
    {
        var optionSetName = $"new_soapcolors{_run}";
        var optionSet = await SoapAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(Label("Red"), 100000000)]) { Name = optionSetName, DisplayName = Label("Colors") },
        });
        ((Guid)optionSet.Results["OptionSetId"]).Should().Be(Store.GetOptionSet(optionSetName)!.MetadataId!.Value);

        var name = Name("soap");
        var entity = await SoapAsync(CreateEntity(name));
        ((Guid)entity.Results["EntityId"]).Should().Be(Store.GetEntity(name)!.MetadataId!.Value);

        var attribute = await SoapAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new PicklistAttributeMetadata
            {
                LogicalName = "new_color", SchemaName = "new_Color", DisplayName = Label("Color"),
                OptionSet = new OptionSetMetadata { IsGlobal = true, Name = optionSetName },
            },
        });
        ((Guid)attribute.Results["AttributeId"]).Should().NotBeEmpty();

        var relationshipName = $"new_soap_mm_{_run}";
        await SoapAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata
            {
                SchemaName = relationshipName, Entity1LogicalName = name, Entity2LogicalName = TestEntities.Project,
            },
            IntersectEntitySchemaName = relationshipName,
        });

        var relationship = await SoapAsync(new RetrieveRelationshipRequest { Name = relationshipName });
        relationship.Results["RelationshipMetadata"].Should().BeOfType<ManyToManyRelationshipMetadata>()
            .Which.Entity2LogicalName.Should().Be(TestEntities.Project);
        var retrieved = await SoapAsync(new RetrieveOptionSetRequest { Name = optionSetName });
        retrieved.Results["OptionSetMetadata"].Should().BeOfType<OptionSetMetadata>()
            .Which.Options.Single().Label.LocalizedLabels.Single().Label.Should().Be("Red");

        await SoapAsync(new DeleteRelationshipRequest { Name = relationshipName });
        await SoapAsync(new DeleteEntityRequest { LogicalName = name });
        Store.GetEntity(name).Should().BeNull();
    }
}

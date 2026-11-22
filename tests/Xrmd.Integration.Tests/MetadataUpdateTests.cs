using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// UpdateEntity, UpdateAttribute (SDK and Web API <c>PUT</c>) apply labels, the required level, a larger MaxLength and
/// options; any other property the request changes is a 400 naming it (never dropped silently), while a full
/// retrieve-modify-update body that only changes what is applied passes. CreateEntity (SDK and Web API) refuses what
/// xrmverse does not model: activity tables, ownerships other than user and organization, non-default settings.
/// </summary>
[Collection("xrmd")]
public sealed class MetadataUpdateTests(XrmdFixture fx)
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

    private async Task<string> NewEntityAsync(string label)
    {
        var name = Name(label);
        await Admin.ExecuteAsync(CreateEntity(name));
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new IntegerAttributeMetadata { LogicalName = "new_count", SchemaName = "new_Count", MinValue = 0, MaxValue = 100, DisplayName = Label("Count") },
        });
        return name;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task<JsonObject> GetJsonAsync(string path)
    {
        var res = await SendAsync(HttpMethod.Get, path);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task<string> StatusAsync(HttpResponseMessage res, HttpStatusCode status)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        return body;
    }

    private static JsonNode JsonLabel(string text) =>
        JsonNode.Parse($$"""{ "LocalizedLabels": [ { "Label": "{{text}}", "LanguageCode": 1033 } ] }""")!;

    // ---- UpdateEntity -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateEntity_changing_a_property_it_does_not_apply_is_400_naming_it()
    {
        var name = await NewEntityAsync("ue");

        var message = await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = name, DisplayName = Label("Renamed"), IsQuickCreateEnabled = true },
        }));
        message.Should().Contain("IsQuickCreateEnabled");
        message = await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = name, OwnershipType = OwnershipTypes.OrganizationOwned },
        }));
        message.Should().Contain("OwnershipType");

        Store.GetEntity(name)!.DisplayName.LocalizedLabels.Single().Label.Should().Be("Table " + name, "nothing of a refused request is applied");
        Store.GetEntity(name)!.IsAuditEnabled!.Value.Should().BeFalse("IsAuditEnabled is applied, not refused (a created table starts off)");
    }

    [Fact]
    public async Task UpdateEntity_of_a_retrieved_definition_that_changes_only_labels_passes()
    {
        var name = await NewEntityAsync("rme");
        var entity = ((RetrieveEntityResponse)await Admin.ExecuteAsync(
            new RetrieveEntityRequest { LogicalName = name, EntityFilters = EntityFilters.All })).EntityMetadata;

        entity.DisplayName = Label("Relabelled");
        entity.Description = Label("Described");
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = entity });

        Store.GetEntity(name)!.DisplayName.LocalizedLabels.Single().Label.Should().Be("Relabelled");
        Store.GetEntity(name)!.Description.LocalizedLabels.Single().Label.Should().Be("Described");
    }

    [Fact]
    public async Task Web_API_PUT_of_an_entity_applies_labels_and_refuses_other_changes_naming_them()
    {
        var name = await NewEntityAsync("pe");
        var path = $"EntityDefinitions(LogicalName='{name}')";

        var body = await GetJsonAsync(path);
        body["DisplayName"] = JsonLabel("Put label");
        await StatusAsync(await SendAsync(HttpMethod.Put, path, body.ToJsonString()), HttpStatusCode.NoContent);
        Store.GetEntity(name)!.DisplayName.LocalizedLabels.Single().Label.Should().Be("Put label");

        body = await GetJsonAsync(path);
        body["IsQuickCreateEnabled"] = true;
        (await StatusAsync(await SendAsync(HttpMethod.Put, path, body.ToJsonString()), HttpStatusCode.BadRequest))
            .Should().Contain("IsQuickCreateEnabled");
    }

    // ---- UpdateAttribute --------------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateAttribute_changing_a_property_it_does_not_apply_is_400_naming_it()
    {
        var name = await NewEntityAsync("ua");

        var message = await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = name,
            Attribute = new IntegerAttributeMetadata { LogicalName = "new_count", DisplayName = Label("Renamed"), MaxValue = 500 },
        }));
        message.Should().Contain("MaxValue");
        message = await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = name,
            Attribute = new StringAttributeMetadata { LogicalName = "new_name", FormatName = StringFormatName.Email },
        }));
        message.Should().Contain("FormatName");
        message = await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = name,
            Attribute = new IntegerAttributeMetadata { LogicalName = "new_count", IsSortableEnabled = new BooleanManagedProperty(true) },
        }));
        message.Should().Contain("IsSortableEnabled");

        var count = (IntegerAttributeMetadata)Store.GetEntity(name)!.Attributes.Single(a => a.LogicalName == "new_count");
        count.MaxValue.Should().Be(100);
        count.DisplayName.LocalizedLabels.Single().Label.Should().Be("Count");
    }

    [Fact]
    public async Task UpdateAttribute_of_a_retrieved_definition_that_changes_what_is_applied_passes()
    {
        var name = await NewEntityAsync("rma");
        var attribute = (StringAttributeMetadata)((RetrieveAttributeResponse)await Admin.ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = name, LogicalName = "new_name" })).AttributeMetadata;

        attribute.DisplayName = Label("Title");
        attribute.MaxLength = 200;
        attribute.RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired);
        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = name, Attribute = attribute });

        var stored = (StringAttributeMetadata)Store.GetEntity(name)!.Attributes.Single(a => a.LogicalName == "new_name");
        stored.DisplayName.LocalizedLabels.Single().Label.Should().Be("Title");
        stored.MaxLength.Should().Be(200);
        stored.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.ApplicationRequired);
    }

    [Fact]
    public async Task UpdateAttribute_changes_the_colour_of_an_existing_option()
    {
        var name = await NewEntityAsync("clr");
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new PicklistAttributeMetadata
            {
                LogicalName = "new_size", SchemaName = "new_Size", DisplayName = Label("Size"),
                OptionSet = new OptionSetMetadata([new OptionMetadata(Label("S"), 1)]) { IsGlobal = false },
            },
        });
        var attribute = (PicklistAttributeMetadata)((RetrieveAttributeResponse)await Admin.ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = name, LogicalName = "new_size" })).AttributeMetadata;
        attribute.OptionSet.Options.Single().Color = "#107C10";

        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = name, Attribute = attribute });

        var option = ((PicklistAttributeMetadata)Store.GetEntity(name)!.Attributes.Single(a => a.LogicalName == "new_size")).OptionSet.Options.Single();
        option.Color.Should().Be("#107C10");
        option.Label.LocalizedLabels.Single().Label.Should().Be("S");
    }

    [Fact]
    public async Task UpdateOptionSet_changes_the_colour_of_an_existing_option()
    {
        var optionSet = Name("clrset");
        await Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(Label("Red"), 100000000)]) { Name = optionSet, DisplayName = Label("Colors") },
        });
        var retrieved = (OptionSetMetadata)((RetrieveOptionSetResponse)await Admin.ExecuteAsync(
            new RetrieveOptionSetRequest { Name = optionSet })).OptionSetMetadata;
        retrieved.Options.Single().Color = "#107C10";

        await Admin.ExecuteAsync(new UpdateOptionSetRequest { OptionSet = retrieved });

        var option = ((OptionSetMetadata)Store.GetOptionSet(optionSet)!).Options.Single();
        option.Color.Should().Be("#107C10");
        option.Label.LocalizedLabels.Single().Label.Should().Be("Red");

        retrieved.Options.Single().Color = "green";
        (await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateOptionSetRequest { OptionSet = retrieved })))
            .Should().Contain("100000000").And.Contain("'green'").And.Contain("not #RRGGBB");
    }

    [Fact]
    public async Task UpdateAttribute_of_a_retrieved_global_picklist_that_changes_only_its_label_passes()
    {
        var name = await NewEntityAsync("gp");
        var optionSet = Name("colors");
        await Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata([new OptionMetadata(Label("Red"), 100000000)]) { Name = optionSet, DisplayName = Label("Colors") },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new PicklistAttributeMetadata
            {
                LogicalName = "new_color", SchemaName = "new_Color", DisplayName = Label("Color"),
                OptionSet = new OptionSetMetadata { IsGlobal = true, Name = optionSet },
            },
        });
        var attribute = ((RetrieveAttributeResponse)await Admin.ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = name, LogicalName = "new_color" })).AttributeMetadata;

        attribute.DisplayName = Label("Colour");
        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = name, Attribute = attribute });

        Store.GetEntity(name)!.Attributes.Single(a => a.LogicalName == "new_color").DisplayName.LocalizedLabels.Single().Label.Should().Be("Colour");
    }

    [Fact]
    public async Task Web_API_PUT_of_an_attribute_applies_labels_and_refuses_other_changes_naming_them()
    {
        var name = await NewEntityAsync("pa");
        var path = $"EntityDefinitions(LogicalName='{name}')/Attributes(LogicalName='new_count')";

        var body = await GetJsonAsync(path);
        body["DisplayName"] = JsonLabel("Put count");
        await StatusAsync(await SendAsync(HttpMethod.Put, path, body.ToJsonString()), HttpStatusCode.NoContent);
        Store.GetEntity(name)!.Attributes.Single(a => a.LogicalName == "new_count").DisplayName.LocalizedLabels.Single().Label.Should().Be("Put count");

        body = await GetJsonAsync(path);
        body["MinValue"] = -5;
        (await StatusAsync(await SendAsync(HttpMethod.Put, path, body.ToJsonString()), HttpStatusCode.BadRequest)).Should().Contain("MinValue");
    }

    // ---- CreateEntity -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateEntity_of_what_xrmverse_does_not_model_is_400_naming_it()
    {
        foreach (var (configure, property) in new (Action<EntityMetadata>, string)[]
                 {
                     (e => e.OwnershipType = OwnershipTypes.TeamOwned, "TeamOwned"),
                     (e => e.IsQuickCreateEnabled = true, "IsQuickCreateEnabled"),
                     (e => e.EntityColor = "#ff0000", "EntityColor"),
                 })
        {
            var name = Name("cx");
            var message = await BadRequestAsync(() => Admin.ExecuteAsync(CreateEntity(name, configure)));
            message.Should().Contain(property);
            Store.GetEntity(name).Should().BeNull();
        }
    }

    [Fact]
    public async Task CreateEntity_with_the_default_settings_passes()
    {
        var name = Name("cd");
        await Admin.ExecuteAsync(CreateEntity(name, e =>
        {
            e.IsActivity = false;
            e.HasNotes = false;
            e.HasActivities = false;
            e.IsAuditEnabled = new BooleanManagedProperty(false);
            e.ChangeTrackingEnabled = false;
            e.OwnershipType = OwnershipTypes.OrganizationOwned;
        }));
        Store.GetEntity(name)!.OwnershipType.Should().Be(OwnershipTypes.OrganizationOwned);
    }

    [Fact]
    public async Task Web_API_POST_EntityDefinitions_of_what_xrmverse_does_not_model_is_400_naming_it()
    {
        foreach (var (extra, property) in new[]
                 {
                     (@"""OwnershipType"": ""TeamOwned""", "TeamOwned"), (@"""IsQuickCreateEnabled"": true", "IsQuickCreateEnabled"),
                     (@"""EntityColor"": ""#ff0000""", "EntityColor"),
                 })
        {
            var name = Name("wx");
            var body = $$"""
                {
                  "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "SchemaName": "{{name}}", "LogicalName": "{{name}}",
                  "DisplayName": { "LocalizedLabels": [ { "Label": "T", "LanguageCode": 1033 } ] }, {{extra}},
                  "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name",
                                    "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true } ]
                }
                """;
            (await StatusAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", body), HttpStatusCode.BadRequest)).Should().Contain(property);
            Store.GetEntity(name).Should().BeNull();
        }

        var ok = Name("wd");
        await StatusAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "SchemaName": "{{ok}}", "LogicalName": "{{ok}}",
              "DisplayName": { "LocalizedLabels": [ { "Label": "T", "LanguageCode": 1033 } ] },
              "HasActivities": false, "HasNotes": false, "IsActivity": false, "OwnershipType": "UserOwned",
              "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name",
                                "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true } ]
            }
            """), HttpStatusCode.NoContent);
        Store.GetEntity(ok).Should().NotBeNull();
    }

    // ---- the settings the export writes, on the read path -----------------------------------------------------------

    [Fact]
    public async Task Retrieve_of_a_table_created_in_xrmverse_shows_the_settings_the_export_writes()
    {
        var name = await NewEntityAsync("rs");

        var entity = ((RetrieveEntityResponse)await Admin.ExecuteAsync(
            new RetrieveEntityRequest { LogicalName = name, EntityFilters = EntityFilters.All })).EntityMetadata;
        entity.ChangeTrackingEnabled.Should().BeFalse();
        entity.IsAuditEnabled.Value.Should().BeFalse();
        entity.HasNotes.Should().BeFalse();
        entity.HasActivities.Should().BeFalse();
        entity.IsMappable.Value.Should().BeTrue();
        entity.IsQuickCreateEnabled.Should().BeFalse();
        var count = entity.Attributes.Single(a => a.LogicalName == "new_count");
        count.IsAuditEnabled.Value.Should().BeTrue();
        count.IsSortableEnabled.Value.Should().BeFalse();

        var all = ((RetrieveAllEntitiesResponse)await Admin.ExecuteAsync(
            new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity })).EntityMetadata.Single(e => e.LogicalName == name);
        all.ChangeTrackingEnabled.Should().BeFalse();
        all.IsAuditEnabled.Value.Should().BeFalse();

        var attribute = ((RetrieveAttributeResponse)await Admin.ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = name, LogicalName = "new_name" })).AttributeMetadata;
        attribute.IsAuditEnabled.Value.Should().BeTrue();
        ((StringAttributeMetadata)attribute).ImeMode.Should().Be(ImeMode.Auto);

        var json = await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')?$expand=Attributes");
        json["ChangeTrackingEnabled"]!.GetValue<bool>().Should().BeFalse();
        json["IsAuditEnabled"]!["Value"]!.GetValue<bool>().Should().BeFalse();
        json["HasNotes"]!.GetValue<bool>().Should().BeFalse();
        json["Attributes"]!.AsArray().Single(a => (string?)a!["LogicalName"] == "new_count")!["IsAuditEnabled"]!["Value"]!.GetValue<bool>().Should().BeTrue();
        (await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')/Attributes(LogicalName='new_count')"))["IsAuditEnabled"]!["Value"]!
            .GetValue<bool>().Should().BeTrue();
        (await GetJsonAsync($"EntityDefinitions?$filter=LogicalName eq '{name}'"))["value"]![0]!["ChangeTrackingEnabled"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task UpdateEntity_and_UpdateAttribute_of_retrieved_settings_sent_back_unchanged_pass()
    {
        var name = await NewEntityAsync("rt");
        var entity = ((RetrieveEntityResponse)await Admin.ExecuteAsync(
            new RetrieveEntityRequest { LogicalName = name, EntityFilters = EntityFilters.Entity })).EntityMetadata;
        entity.IsAuditEnabled.Should().NotBeNull("the retrieve shows the settings");
        entity.DisplayName = Label("Round trip");
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = entity });
        Store.GetEntity(name)!.DisplayName.LocalizedLabels.Single().Label.Should().Be("Round trip");

        var attribute = ((RetrieveAttributeResponse)await Admin.ExecuteAsync(
            new RetrieveAttributeRequest { EntityLogicalName = name, LogicalName = "new_count" })).AttributeMetadata;
        attribute.IsAuditEnabled.Should().NotBeNull();
        attribute.DisplayName = Label("Round count");
        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = name, Attribute = attribute });
        Store.GetEntity(name)!.Attributes.Single(a => a.LogicalName == "new_count").DisplayName.LocalizedLabels.Single().Label.Should().Be("Round count");

        var path = $"EntityDefinitions(LogicalName='{name}')/Attributes(LogicalName='new_count')";
        var body = await GetJsonAsync(path);
        body["DisplayName"] = JsonLabel("Put round count");
        await StatusAsync(await SendAsync(HttpMethod.Put, path, body.ToJsonString()), HttpStatusCode.NoContent);
    }

    // ---- column settings xrmverse does not model, on create ---------------------------------------------------------

    [Fact]
    public async Task CreateAttribute_with_a_column_setting_xrmverse_does_not_model_is_400_naming_it()
    {
        var name = await NewEntityAsync("ca");
        foreach (var (attribute, property) in new (AttributeMetadata, string)[]
                 {
                     (new IntegerAttributeMetadata { LogicalName = "new_b", SchemaName = "new_B", DisplayName = Label("B"), IsSortableEnabled = new BooleanManagedProperty(true) }, "IsSortableEnabled"),
                     (new StringAttributeMetadata { LogicalName = "new_c", SchemaName = "new_C", DisplayName = Label("C"), MaxLength = 10, ImeMode = ImeMode.Disabled }, "ImeMode"),
                 })
        {
            (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = name, Attribute = attribute }))).Should().Contain(property);
            Store.GetEntity(name)!.Attributes.Should().NotContain(a => a.LogicalName == attribute.LogicalName);
        }

        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new IntegerAttributeMetadata
            {
                LogicalName = "new_d", SchemaName = "new_D", DisplayName = Label("D"), IsAuditEnabled = new BooleanManagedProperty(true),
                IsSortableEnabled = new BooleanManagedProperty(false),
            },
        });
        Store.GetEntity(name)!.Attributes.Should().Contain(a => a.LogicalName == "new_d");
    }

    /// <summary>Sets a property the SDK exposes read-only (the server sets it; a Web API body or a SOAP request can carry it).</summary>
    private static void SetReadOnly(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    [Fact]
    public async Task CreateAttribute_with_an_unmodelled_setting_is_400()
    {
        // Settings the export does not write from the column's definition (the DisplayMask bits, the deprecation and
        // inheritance markers, the column number, a formula): refused, never dropped silently.
        var name = await NewEntityAsync("cu");
        var index = 0;
        foreach (var (configure, property) in new (Action<StringAttributeMetadata>, string)[]
                 {
                     (a => a.IsValidForAdvancedFind = new BooleanManagedProperty(false), "IsValidForAdvancedFind"),
                     (a => a.IsValidForForm = false, "IsValidForForm"),
                     (a => a.IsValidForGrid = false, "IsValidForGrid"),
                     (a => SetReadOnly(a, "IsValidForRead", false), "IsValidForRead"),
                     (a => SetReadOnly(a, "DeprecatedVersion", "9.0.0.0"), "DeprecatedVersion"),
                     (a => SetReadOnly(a, "InheritsFrom", "new_other"), "InheritsFrom"),
                     (a => SetReadOnly(a, "ColumnNumber", 7), "ColumnNumber"),
                     (a => a.LinkedAttributeId = Guid.NewGuid(), "LinkedAttributeId"),
                 })
        {
            var attribute = new StringAttributeMetadata
            {
                LogicalName = $"new_u{index}", SchemaName = $"new_U{index}", MaxLength = 10, DisplayName = Label("U"),
            };
            index++;
            configure(attribute);
            (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = name, Attribute = attribute }))).Should().Contain(property);
            Store.GetEntity(name)!.Attributes.Should().NotContain(a => a.LogicalName == attribute.LogicalName);
        }

        // The default values (what a retrieved definition shows) are accepted.
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new StringAttributeMetadata
            {
                LogicalName = "new_ok", SchemaName = "new_Ok", MaxLength = 10, DisplayName = Label("Ok"),
                IsValidForAdvancedFind = new BooleanManagedProperty(true), IsValidForForm = true, IsValidForGrid = true,
            },
        });
        Store.GetEntity(name)!.Attributes.Should().Contain(a => a.LogicalName == "new_ok");

        // The secured-column flags are server-owned (plan 14): a given value is ignored, and reads compute them.
        var flagged = new StringAttributeMetadata { LogicalName = "new_fl", SchemaName = "new_Fl", MaxLength = 10, DisplayName = Label("Fl") };
        SetReadOnly(flagged, "CanBeSecuredForRead", false);
        SetReadOnly(flagged, "CanBeSecuredForCreate", false);
        await Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = name, Attribute = flagged });
        var read = ((RetrieveAttributeResponse)await Admin.ExecuteAsync(new RetrieveAttributeRequest { EntityLogicalName = name, LogicalName = "new_fl" }))
            .AttributeMetadata;
        (read.CanBeSecuredForCreate, read.CanBeSecuredForRead, read.CanBeSecuredForUpdate).Should().Be((true, true, true));
    }

    [Fact]
    public async Task A_retrieved_column_definition_can_be_created_on_another_entity()
    {
        // Whatever a read shows (including the properties a create refuses to set) is accepted back by a create.
        var source = await NewEntityAsync("rt1");
        var target = await NewEntityAsync("rt2");
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = source,
            Attribute = new StringAttributeMetadata { LogicalName = "new_text", SchemaName = "new_Text", MaxLength = 50, DisplayName = Label("Text") },
        });

        foreach (var column in new[] { "new_count", "new_text" })
        {
            var retrieved = ((RetrieveAttributeResponse)await Admin.ExecuteAsync(
                new RetrieveAttributeRequest { EntityLogicalName = source, LogicalName = column })).AttributeMetadata;
            retrieved.LogicalName = "new_copy_" + column;
            retrieved.SchemaName = "new_Copy_" + column;
            retrieved.MetadataId = null;
            await Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = target, Attribute = retrieved });
            Store.GetEntity(target)!.Attributes.Should().Contain(a => a.LogicalName == "new_copy_" + column);

            var json = await GetJsonAsync($"EntityDefinitions(LogicalName='{source}')/Attributes(LogicalName='{column}')");
            json["LogicalName"] = "new_web_" + column;
            json["SchemaName"] = "new_Web_" + column;
            json.Remove("MetadataId");
            await StatusAsync(await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{target}')/Attributes", json.ToJsonString()),
                HttpStatusCode.NoContent);
            Store.GetEntity(target)!.Attributes.Should().Contain(a => a.LogicalName == "new_web_" + column);
        }
    }

    [Fact]
    public async Task CreateEntity_and_CreateOneToMany_with_a_column_setting_xrmverse_does_not_model_are_400_naming_it()
    {
        var name = Name("cp");
        var request = CreateEntity(name);
        request.PrimaryAttribute.IsSortableEnabled = new BooleanManagedProperty(true);
        (await BadRequestAsync(() => Admin.ExecuteAsync(request))).Should().Contain("IsSortableEnabled");
        Store.GetEntity(name).Should().BeNull();

        var parent = await NewEntityAsync("lp");
        var child = await NewEntityAsync("lc");
        var schemaName = $"new_lp_lc_{_run}";
        (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = schemaName, ReferencedEntity = parent, ReferencingEntity = child, ReferencedAttribute = parent + "id",
            },
            Lookup = new LookupAttributeMetadata
            {
                LogicalName = "new_parentid", SchemaName = "new_ParentId", DisplayName = Label("Parent"), IsSortableEnabled = new BooleanManagedProperty(true),
            },
        }))).Should().Contain("IsSortableEnabled");
        Store.GetRelationship(schemaName).Should().BeNull();
    }

    [Fact]
    public async Task Web_API_POST_of_a_column_setting_xrmverse_does_not_model_is_400_naming_it()
    {
        var name = Name("wc");
        (await StatusAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "SchemaName": "{{name}}", "LogicalName": "{{name}}",
              "DisplayName": { "LocalizedLabels": [ { "Label": "T", "LanguageCode": 1033 } ] },
              "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name",
                                "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true, "IsSortableEnabled": { "Value": true } } ]
            }
            """), HttpStatusCode.BadRequest)).Should().Contain("IsSortableEnabled");
        Store.GetEntity(name).Should().BeNull();

        var existing = await NewEntityAsync("wa");
        (await StatusAsync(await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{existing}')/Attributes", """
            {
              "@odata.type": "Microsoft.Dynamics.CRM.IntegerAttributeMetadata", "LogicalName": "new_web", "SchemaName": "new_Web",
              "DisplayName": { "LocalizedLabels": [ { "Label": "W", "LanguageCode": 1033 } ] }, "IsSortableEnabled": { "Value": true }
            }
            """), HttpStatusCode.BadRequest)).Should().Contain("IsSortableEnabled");
        Store.GetEntity(existing)!.Attributes.Should().NotContain(a => a.LogicalName == "new_web");
    }
}

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
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// <c>IsAuditEnabled</c> of tables and columns (plan 13, task 2): the defaults of seeded and created components, the
/// updates and the <c>CanBeChanged</c> rule on every front end, and what a create accepts. The solution round trip and the
/// startup upgrade are in <see cref="SolutionExportTests"/>.
/// </summary>
[Collection("xrmd")]
public sealed class AuditEnablementTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private string Name(string label) => $"new_{label}{_run}";

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

    private async Task<EntityMetadata> RetrieveEntityAsync(string name) =>
        ((RetrieveEntityResponse)await Admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = name, EntityFilters = EntityFilters.All })).EntityMetadata;

    private async Task<AttributeMetadata> RetrieveAttributeAsync(string entity, string attribute) =>
        ((RetrieveAttributeResponse)await Admin.ExecuteAsync(new RetrieveAttributeRequest { EntityLogicalName = entity, LogicalName = attribute })).AttributeMetadata;

    private static void Setting(BooleanManagedProperty? actual, bool value, bool canBeChanged, string what)
    {
        actual.Should().NotBeNull(what);
        (actual!.Value, actual.CanBeChanged).Should().Be((value, canBeChanged), what);
        actual.ManagedPropertyLogicalName.Should().Be("canmodifyauditsettings", what);
    }

    // ---- defaults -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Seeded_and_created_tables_have_dataverse_audit_defaults()
    {
        foreach (var audited in new[] { "account", "contact", "systemuser", "team", "role", "businessunit" })
            Setting((await RetrieveEntityAsync(audited)).IsAuditEnabled, true, true, audited);
        foreach (var fixedOff in new[] { "audit", "organization", "asyncoperation", "fileattachment", "privilege", "solution", "plugintype" })
            Setting((await RetrieveEntityAsync(fixedOff)).IsAuditEnabled, false, false, fixedOff);

        var account = await RetrieveEntityAsync("account");
        Setting(account.Attributes.Single(a => a.LogicalName == "name").IsAuditEnabled, true, true, "account.name");
        Setting(account.Attributes.Single(a => a.LogicalName == "ownerid").IsAuditEnabled, true, true, "account.ownerid");
        Setting(account.Attributes.Single(a => a.LogicalName == "statecode").IsAuditEnabled, true, true, "account.statecode");
        Setting(account.Attributes.Single(a => a.LogicalName == "statuscode").IsAuditEnabled, true, true, "account.statuscode");
        foreach (var fixedOff in new[] { "accountid", "createdon", "createdby", "modifiedon", "modifiedby", "owninguser", "owningteam", "owningbusinessunit", "versionnumber" })
            Setting(account.Attributes.Single(a => a.LogicalName == fixedOff).IsAuditEnabled, false, false, "account." + fixedOff);
        Setting((await RetrieveAttributeAsync("account", "modifiedon")).IsAuditEnabled, false, false, "RetrieveAttribute account.modifiedon");
        Setting((await RetrieveEntityAsync("asyncoperation")).Attributes.Single(a => a.LogicalName == "name").IsAuditEnabled, true, true,
            "a column of a table that cannot be audited still has its own setting");

        var name = Name("df");
        await Admin.ExecuteAsync(CreateEntity(name));
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new MoneyAttributeMetadata { LogicalName = "new_amount", SchemaName = "new_Amount", DisplayName = Label("Amount") },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_Doc", DisplayName = Label("Doc") },
        });
        var created = await RetrieveEntityAsync(name);
        Setting(created.IsAuditEnabled, false, true, "a table created in xrmverse");
        Setting(created.Attributes.Single(a => a.LogicalName == "new_name").IsAuditEnabled, true, true, "primary name");
        Setting(created.Attributes.Single(a => a.LogicalName == "new_amount").IsAuditEnabled, true, true, "money");
        Setting(created.Attributes.Single(a => a.LogicalName == "new_amount_base").IsAuditEnabled, false, false, "money base");
        Setting(created.Attributes.Single(a => a.LogicalName == "new_doc").IsAuditEnabled, false, false, "file column");
        foreach (var companion in created.Attributes.Where(a => a.AttributeOf == "new_doc"))
            Setting(companion.IsAuditEnabled, false, false, "file companion " + companion.LogicalName);
        Setting(created.Attributes.Single(a => a.LogicalName == name + "id").IsAuditEnabled, false, false, "primary key");
        Setting(created.Attributes.Single(a => a.LogicalName == "ownerid").IsAuditEnabled, true, true, "ownerid");
        Setting(created.Attributes.Single(a => a.LogicalName == "createdon").IsAuditEnabled, false, false, "createdon");

        var json = await GetJsonAsync($"EntityDefinitions(LogicalName='{name}')?$expand=Attributes");
        json["IsAuditEnabled"]!["CanBeChanged"]!.GetValue<bool>().Should().BeTrue();
        json["IsAuditEnabled"]!["ManagedPropertyLogicalName"]!.GetValue<string>().Should().Be("canmodifyauditsettings");
        json["Attributes"]!.AsArray().Single(a => (string?)a!["LogicalName"] == "createdon")!["IsAuditEnabled"]!["CanBeChanged"]!
            .GetValue<bool>().Should().BeFalse();
    }

    // ---- updates ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Upgrade_gives_a_custom_intersect_table_the_fixed_off_setting()
    {
        var left = await NewEntityAsync("ixl");
        var right = await NewEntityAsync("ixr");
        var intersect = $"new_ix_{_run}";
        await Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata { SchemaName = intersect, Entity1LogicalName = left, Entity2LogicalName = right },
            IntersectEntitySchemaName = intersect,
        });
        Setting(Store.GetEntity(intersect)!.IsAuditEnabled, false, false, "a new intersect");

        // An organization made before the setting was modelled: the intersect has none.
        (await TestSql.ExecAsync(fx.ConnectionString,
            "UPDATE meta.entity SET definition = JSON_MODIFY(definition, '$.IsAuditEnabled', NULL) WHERE logical_name = @n", ("@n", intersect)))
            .Should().Be(1);
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        var old = new Xrmd.Metadata.MetadataStore(factory);
        await old.LoadAsync();
        old.GetEntity(intersect)!.IsAuditEnabled.Should().BeNull();

        await Xrmd.Solution.AuditSettingsUpgrade.EnsureAsync(old, factory);
        Setting(old.GetEntity(intersect)!.IsAuditEnabled, false, false, "an upgraded intersect");
        await Store.LoadAsync();
        Setting(Store.GetEntity(intersect)!.IsAuditEnabled, false, false, "the shared store after the upgrade");
    }

    [Fact]
    public async Task Update_entity_and_attribute_toggle_is_audit_enabled()
    {
        var name = await NewEntityAsync("up");

        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name, IsAuditEnabled = new BooleanManagedProperty(true) } });
        Setting((await RetrieveEntityAsync(name)).IsAuditEnabled, true, true, "UpdateEntity");
        await Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = name, Attribute = new IntegerAttributeMetadata { LogicalName = "new_count", IsAuditEnabled = new BooleanManagedProperty(false) },
        });
        Setting((await RetrieveAttributeAsync(name, "new_count")).IsAuditEnabled, false, true, "UpdateAttribute");
        Setting(Store.GetEntity(name)!.Attributes.Single(a => a.LogicalName == "new_count").IsAuditEnabled, false, true, "the store");

        // A retrieved definition sent back unchanged still passes, and a changed value in it is applied.
        var entity = (await RetrieveEntityAsync(name));
        entity.DisplayName = Label("Round trip");
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = entity });
        Setting((await RetrieveEntityAsync(name)).IsAuditEnabled, true, true, "unchanged");
        var attribute = await RetrieveAttributeAsync(name, "new_count");
        attribute.IsAuditEnabled = new BooleanManagedProperty(true);
        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = name, Attribute = attribute });
        Setting((await RetrieveAttributeAsync(name, "new_count")).IsAuditEnabled, true, true, "retrieved and changed");

        // The Web API PUT.
        var path = $"EntityDefinitions(LogicalName='{name}')";
        var body = await GetJsonAsync(path);
        body["IsAuditEnabled"]!["Value"] = false;
        await StatusAsync(await SendAsync(HttpMethod.Put, path, body.ToJsonString()), HttpStatusCode.NoContent);
        Setting((await RetrieveEntityAsync(name)).IsAuditEnabled, false, true, "PUT EntityDefinitions");
        var attributePath = path + "/Attributes(LogicalName='new_count')";
        var attributeBody = await GetJsonAsync(attributePath);
        attributeBody["IsAuditEnabled"]!["Value"] = false;
        await StatusAsync(await SendAsync(HttpMethod.Put, attributePath, attributeBody.ToJsonString()), HttpStatusCode.NoContent);
        Setting((await RetrieveAttributeAsync(name, "new_count")).IsAuditEnabled, false, true, "PUT Attributes");
        // The unchanged bodies pass too.
        await StatusAsync(await SendAsync(HttpMethod.Put, path, (await GetJsonAsync(path)).ToJsonString()), HttpStatusCode.NoContent);
        await StatusAsync(await SendAsync(HttpMethod.Put, attributePath, (await GetJsonAsync(attributePath)).ToJsonString()), HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Is_audit_enabled_cannot_change_when_can_be_changed_is_false()
    {
        // asyncoperation (a table that cannot be audited) and account.modifiedon (a fixed column).
        (await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = "asyncoperation", IsAuditEnabled = new BooleanManagedProperty(true) },
        }))).Should().Contain("IsAuditEnabled");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = "account", Attribute = new DateTimeAttributeMetadata { LogicalName = "modifiedon", IsAuditEnabled = new BooleanManagedProperty(true) },
        }))).Should().Contain("IsAuditEnabled");
        Setting((await RetrieveEntityAsync("asyncoperation")).IsAuditEnabled, false, false, "asyncoperation");
        Setting((await RetrieveAttributeAsync("account", "modifiedon")).IsAuditEnabled, false, false, "account.modifiedon");

        var entityPath = "EntityDefinitions(LogicalName='asyncoperation')";
        var body = await GetJsonAsync(entityPath);
        body["IsAuditEnabled"]!["Value"] = true;
        (await StatusAsync(await SendAsync(HttpMethod.Put, entityPath, body.ToJsonString()), HttpStatusCode.BadRequest)).Should().Contain("IsAuditEnabled");
        var attributePath = "EntityDefinitions(LogicalName='account')/Attributes(LogicalName='modifiedon')";
        var attributeBody = await GetJsonAsync(attributePath);
        attributeBody["IsAuditEnabled"]!["Value"] = true;
        (await StatusAsync(await SendAsync(HttpMethod.Put, attributePath, attributeBody.ToJsonString()), HttpStatusCode.BadRequest)).Should().Contain("IsAuditEnabled");

        // The retrieved definitions sent back unchanged pass (their CanBeChanged is false).
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = await RetrieveEntityAsync("asyncoperation") });
        await Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = "account", Attribute = await RetrieveAttributeAsync("account", "modifiedon") });

        // CanBeChanged itself cannot be changed (managed properties are not modelled).
        var name = await NewEntityAsync("cb");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = name, IsAuditEnabled = new BooleanManagedProperty(false) { CanBeChanged = false } },
        }))).Should().Contain("IsAuditEnabled");
        Setting((await RetrieveEntityAsync(name)).IsAuditEnabled, false, true, "unchanged by the refused request");
    }

    // ---- create -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_entity_and_attribute_accept_is_audit_enabled()
    {
        foreach (var value in new[] { true, false })
        {
            var name = Name("ce" + (value ? "t" : "f"));
            var request = CreateEntity(name, e => e.IsAuditEnabled = new BooleanManagedProperty(value));
            request.PrimaryAttribute.IsAuditEnabled = new BooleanManagedProperty(value);
            await Admin.ExecuteAsync(request);
            Setting((await RetrieveEntityAsync(name)).IsAuditEnabled, value, true, "CreateEntity " + value);
            Setting((await RetrieveAttributeAsync(name, "new_name")).IsAuditEnabled, value, true, "its primary name");
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new IntegerAttributeMetadata
                {
                    LogicalName = "new_n", SchemaName = "new_N", DisplayName = Label("N"), IsAuditEnabled = new BooleanManagedProperty(value),
                },
            });
            Setting((await RetrieveAttributeAsync(name, "new_n")).IsAuditEnabled, value, true, "CreateAttribute " + value);
        }

        // The Web API.
        foreach (var value in new[] { "true", "false" })
        {
            var name = Name("cw" + value[0]);
            await StatusAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", $$"""
                {
                  "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "SchemaName": "{{name}}", "LogicalName": "{{name}}",
                  "DisplayName": { "LocalizedLabels": [ { "Label": "T", "LanguageCode": 1033 } ] },
                  "IsAuditEnabled": { "Value": {{value}} },
                  "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name",
                                    "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true, "IsAuditEnabled": { "Value": {{value}} } } ]
                }
                """), HttpStatusCode.NoContent);
            Setting((await RetrieveEntityAsync(name)).IsAuditEnabled, bool.Parse(value), true, "POST EntityDefinitions " + value);
            Setting((await RetrieveAttributeAsync(name, "new_name")).IsAuditEnabled, bool.Parse(value), true, "its primary name");
            await StatusAsync(await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{name}')/Attributes", $$"""
                {
                  "@odata.type": "Microsoft.Dynamics.CRM.IntegerAttributeMetadata", "LogicalName": "new_w", "SchemaName": "new_W",
                  "DisplayName": { "LocalizedLabels": [ { "Label": "W", "LanguageCode": 1033 } ] }, "IsAuditEnabled": { "Value": {{value}} }
                }
                """), HttpStatusCode.NoContent);
            Setting((await RetrieveAttributeAsync(name, "new_w")).IsAuditEnabled, bool.Parse(value), true, "POST Attributes " + value);
        }

        // What cannot be honoured is refused: a setting that cannot change, a column that is always off.
        var refused = Name("cr");
        (await BadRequestAsync(() => Admin.ExecuteAsync(CreateEntity(refused, e => e.IsAuditEnabled = new BooleanManagedProperty(true) { CanBeChanged = false }))))
            .Should().Contain("IsAuditEnabled");
        Store.GetEntity(refused).Should().BeNull();
        var table = await NewEntityAsync("cf");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = table,
            Attribute = new FileAttributeMetadata { LogicalName = "new_doc", SchemaName = "new_Doc", DisplayName = Label("Doc"), IsAuditEnabled = new BooleanManagedProperty(true) },
        }))).Should().Contain("IsAuditEnabled");
        Store.GetEntity(table)!.Attributes.Should().NotContain(a => a.LogicalName == "new_doc");
    }
}

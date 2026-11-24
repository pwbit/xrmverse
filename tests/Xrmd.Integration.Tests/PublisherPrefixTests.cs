using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The publisher prefix rule: a custom name supplied by a caller is <c>{prefix}_{rest}</c> with the prefix of an existing
/// publisher, on every path that creates a custom component (SDK messages, Web API, solution import). Seeded and generated
/// names are exempt.
/// </summary>
[Collection("xrmd")]
public sealed class PublisherPrefixTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("pb_x", true)]
    [InlineData("pb_x_y", true)]
    [InlineData("pb__x", true)]
    [InlineData("xv_thing", true)]
    [InlineData("new_thing", true)]
    [InlineData("PB_x", false)]            // the prefix is compared exactly
    [InlineData("Pb_x", false)]
    [InlineData("pb_", false)]             // no rest
    [InlineData("pb", false)]              // no underscore
    [InlineData("_x", false)]              // no prefix
    [InlineData("pbx_y", false)]           // a longer prefix is another prefix
    [InlineData("p_y", false)]
    [InlineData("zz_thing", false)]
    [InlineData("", false)]
    public void Prefix_rule_cases(string name, bool accepted) =>
        PublisherPrefixRule.HasPrefix(name, ["pb", "xv", "new"]).Should().Be(accepted);

    [Fact]
    public void Prefix_rule_with_only_pb_refuses_xv_and_the_prefix_alone()
    {
        string[] only = ["pb"];
        PublisherPrefixRule.HasPrefix("PB_x", only).Should().BeFalse();
        PublisherPrefixRule.HasPrefix("pb_", only).Should().BeFalse();
        PublisherPrefixRule.HasPrefix("pbx_y", only).Should().BeFalse();
        PublisherPrefixRule.HasPrefix("pb_x", only).Should().BeTrue();
    }

    [Fact]
    public void The_fixture_publishers_are_known()
    {
        fx.App.Services.GetRequiredService<PublisherPrefixRule>().Prefixes.Should().Contain(["new", "pb", "xv"]);
    }

    private static CreateEntityRequest CreateEntity(string logicalName, string primary = "new_name") => new()
    {
        Entity = new EntityMetadata
        {
            LogicalName = logicalName, SchemaName = logicalName, DisplayName = Label("T"), DisplayCollectionName = Label("Ts"),
            OwnershipType = OwnershipTypes.UserOwned,
        },
        PrimaryAttribute = new StringAttributeMetadata { LogicalName = primary, SchemaName = primary, MaxLength = 100, DisplayName = Label("Name") },
    };

    [Fact]
    public async Task Create_entity_without_an_existing_prefix_fails()
    {
        // SDK: the table name, then the primary name column.
        var message = await BadRequestAsync(() => Admin.ExecuteAsync(CreateEntity("zz_thing" + _run)));
        message.Should().Contain($"zz_thing{_run}").And.Contain("'new'").And.Contain("'pb'").And.Contain("xrmd publisher create");
        await BadRequestAsync(() => Admin.ExecuteAsync(CreateEntity("pb_ok" + _run, primary: "name")));
        Store.GetEntity("zz_thing" + _run).Should().BeNull();
        Store.GetEntity("pb_ok" + _run).Should().BeNull();

        // Web API: the table name, then a column of the body.
        var body = (string table, string column) => $$"""
            { "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "LogicalName": "{{table}}", "SchemaName": "{{table}}",
              "DisplayName": { "LocalizedLabels": [ { "Label": "T", "LanguageCode": 1033 } ] }, "OwnershipType": "UserOwned",
              "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "{{column}}", "SchemaName": "{{column}}",
                "MaxLength": 100, "IsPrimaryName": true, "DisplayName": { "LocalizedLabels": [ { "Label": "N", "LanguageCode": 1033 } ] } } ] }
            """;
        (await SendAsync(HttpMethod.Post, "EntityDefinitions", body("zz_web" + _run, "pb_name"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "EntityDefinitions", body("pb_web" + _run, "name"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Store.GetEntity("zz_web" + _run).Should().BeNull();
        Store.GetEntity("pb_web" + _run).Should().BeNull();
        var created = await SendAsync(HttpMethod.Post, "EntityDefinitions", body("pb_web" + _run, "pb_name"));
        created.StatusCode.Should().Be(HttpStatusCode.NoContent, await created.Content.ReadAsStringAsync());
        Store.GetEntity("pb_web" + _run).Should().NotBeNull();
    }

    [Fact]
    public async Task Create_attribute_on_a_seeded_table_needs_a_prefix()
    {
        // SDK, on a seeded table (and a custom one).
        await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = "role", Attribute = new StringAttributeMetadata { LogicalName = "zz_note", SchemaName = "zz_note", MaxLength = 50 },
        }));
        Store.GetEntity("role")!.Attributes.Should().NotContain(a => a.LogicalName == "zz_note");

        var name = $"pb_note{_run}";
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = "role", Attribute = new StringAttributeMetadata { LogicalName = name, SchemaName = name, MaxLength = 50 },
        });
        Store.GetEntity("role")!.Attributes.Should().Contain(a => a.LogicalName == name);

        // Web API on a seeded table.
        var column = (string n) => $$"""
            { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "{{n}}", "SchemaName": "{{n}}", "MaxLength": 50,
              "DisplayName": { "LocalizedLabels": [ { "Label": "N", "LanguageCode": 1033 } ] } }
            """;
        (await SendAsync(HttpMethod.Post, "EntityDefinitions(LogicalName='role')/Attributes", column("zz_web" + _run)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var ok = await SendAsync(HttpMethod.Post, "EntityDefinitions(LogicalName='role')/Attributes", column($"xv_web{_run}"));
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent, await ok.Content.ReadAsStringAsync());

        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = "role", LogicalName = name });
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = "role", LogicalName = $"xv_web{_run}" });
    }

    [Fact]
    public async Task Relationships_and_choices_need_a_prefix_and_generated_names_are_exempt()
    {
        var table = $"pb_rel{_run}";
        await Admin.ExecuteAsync(CreateEntity(table, "pb_name"));

        // A choice and a lookup without a prefix.
        await BadRequestAsync(() => Admin.ExecuteAsync(new CreateOptionSetRequest
        {
            OptionSet = new OptionSetMetadata(new OptionMetadataCollection { new OptionMetadata(Label("A"), 1) })
                { Name = "zz_choice" + _run, IsGlobal = true, OptionSetType = OptionSetType.Picklist },
        }));
        await BadRequestAsync(() => Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
                { SchemaName = $"pb_rel_{_run}", ReferencedEntity = "systemuser", ReferencingEntity = table },
            Lookup = new LookupAttributeMetadata { LogicalName = "zz_owner", SchemaName = "zz_owner" },
        }));
        // A relationship name without one (the lookup is fine).
        await BadRequestAsync(() => Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
                { SchemaName = $"zz_rel_{_run}", ReferencedEntity = "systemuser", ReferencingEntity = table },
            Lookup = new LookupAttributeMetadata { LogicalName = "pb_owner", SchemaName = "pb_owner" },
        }));
        await BadRequestAsync(() => Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata
                { SchemaName = $"zz_m2m_{_run}", Entity1LogicalName = table, Entity2LogicalName = "systemuser" },
            IntersectEntitySchemaName = $"zz_m2m_{_run}",
        }));

        // Prefixed: accepted. A money column brings its generated _base column, which carries no prefix of its own rule.
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = table, Attribute = new MoneyAttributeMetadata { LogicalName = "pb_amount", SchemaName = "pb_amount" },
        });
        Store.GetEntity(table)!.Attributes.Should().Contain(a => a.LogicalName == "pb_amount_base");
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
                { SchemaName = $"pb_rel_{_run}", ReferencedEntity = "systemuser", ReferencingEntity = table },
            Lookup = new LookupAttributeMetadata { LogicalName = "pb_owner", SchemaName = "pb_owner" },
        });
        // No SchemaName: the relationship name the store derives from the lookup is a generated name, not checked.
        var seeded = Store.GetOneToManyRelationships().Where(r => r.ReferencingEntity == table).Select(r => r.SchemaName);
        seeded.Should().Contain($"pb_rel_{_run}");
        await Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata
                { SchemaName = $"pb_m2m_{_run}", Entity1LogicalName = table, Entity2LogicalName = "systemuser" },
            IntersectEntitySchemaName = $"pb_m2m_{_run}",
        });
        Store.GetEntity($"pb_m2m_{_run}").Should().NotBeNull(); // the intersect table follows the relationship's name

        await Admin.ExecuteAsync(new DeleteRelationshipRequest { Name = $"pb_m2m_{_run}" });
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });
    }

    [Fact]
    public async Task New_publisher_prefix_is_usable_at_once()
    {
        var prefix = "np" + _run[..4];
        var table = $"{prefix}_thing";
        // Primes the cache with a refusal, then creates the publisher through the Web API and uses the prefix right away.
        await BadRequestAsync(() => Admin.ExecuteAsync(CreateEntity(table, $"{prefix}_name")));
        var publisher = await SendAsync(HttpMethod.Post, "publishers", $$"""
            { "uniquename": "publisher{{_run}}", "friendlyname": "P", "customizationprefix": "{{prefix}}", "customizationoptionvalueprefix": 12345 }
            """);
        publisher.StatusCode.Should().Be(HttpStatusCode.NoContent, await publisher.Content.ReadAsStringAsync());
        fx.App.Services.GetRequiredService<PublisherPrefixRule>().Prefixes.Should().Contain(prefix);

        await Admin.ExecuteAsync(CreateEntity(table, $"{prefix}_name"));
        Store.GetEntity(table).Should().NotBeNull();
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });

        // Deleted again: refused again (the write invalidates the cache).
        var id = publisher.Headers.GetValues("OData-EntityId").Single();
        (await SendAsync(HttpMethod.Delete, id[(id.IndexOf("publishers", StringComparison.Ordinal))..])).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await BadRequestAsync(() => Admin.ExecuteAsync(CreateEntity(table, $"{prefix}_name")));
    }

    [Fact]
    public async Task Solution_import_counts_its_own_publisher()
    {
        // A host on an empty organization: no pb/xv publisher. The fixture solution's publisher (prefix xv) is created by the
        // import itself and counts for its components; another prefix in the file does not.
        await using var app = fx.WithOwnDatabase(_ => { });
        var store = app.Services.GetRequiredService<IMetadataStore>();
        await TestEntities.EnsurePipeAsync(store);
        var admin = TestServices.NewOrganizationService(app.Services, fx.AdminObjectId);
        app.Services.GetRequiredService<PublisherPrefixRule>().Prefixes.Should().NotContain("xv");

        var foreign = SolutionFixtureZip.Build(solution: s => s.Descendants("CustomizationPrefix").Single().Value = "zz");
        var message = await BadRequestAsync(() => admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = foreign, ImportJobId = Guid.NewGuid() }));
        message.Should().Contain("xv_").And.Contain("'zz'");
        store.GetEntity("xv_project").Should().BeNull("the import is refused before any change");

        await admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = Guid.NewGuid() });
        store.GetEntity("xv_project").Should().NotBeNull();
        app.Services.GetRequiredService<PublisherPrefixRule>().Prefixes.Should().Contain("xv");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, path.StartsWith("http", StringComparison.Ordinal) ? path : Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>Plan 3.4, task 1: the table type, the elastic system columns and key, and the features elastic tables refuse (E9).</summary>
[Collection("xrmd")]
public sealed class ElasticMetadataTests(XrmdFixture fx)
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

    private Task CreateElasticAsync(string name, Action<EntityMetadata>? configure = null) =>
        Admin.ExecuteAsync(CreateEntity(name, e =>
        {
            e.TableType = "Elastic";
            configure?.Invoke(e);
        }));

    private async Task<EntityMetadata> RetrieveEntityAsync(string name, EntityFilters filters = EntityFilters.Entity | EntityFilters.Attributes) =>
        ((RetrieveEntityResponse)await Admin.ExecuteAsync(new RetrieveEntityRequest { LogicalName = name, EntityFilters = filters })).EntityMetadata;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    [Fact]
    public async Task CreateEntity_elastic_reads_back_with_type_key_and_system_columns()
    {
        var name = Name("el");
        await CreateElasticAsync(name);

        var entity = await RetrieveEntityAsync(name);

        entity.TableType.Should().Be("Elastic");
        var key = entity.Keys.Should().ContainSingle().Which;
        key.LogicalName.Should().Be("keyfornosqlentitywithpkpartitionid");
        key.KeyAttributes.Should().Equal($"{name}id", "partitionid");
        var names = entity.Attributes.Select(a => a.LogicalName).ToList();
        names.Should().Contain(["partitionid", "ttlinseconds", "ownerid"]).And.NotContain(["statecode", "statuscode"]);
        entity.Attributes.Single(a => a.LogicalName == "partitionid").Should().BeOfType<StringAttributeMetadata>().Which.MaxLength.Should().Be(100);
        var ttl = entity.Attributes.Single(a => a.LogicalName == "ttlinseconds").Should().BeOfType<IntegerAttributeMetadata>().Which;
        ttl.MinValue.Should().Be(1);
        // Computed on read, never stored.
        Store.GetEntity(name)!.Keys.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Standard_tables_read_Standard_and_have_no_key()
    {
        var account = await RetrieveEntityAsync("account");
        account.TableType.Should().Be("Standard");
        account.Keys.Should().BeNullOrEmpty();

        var name = Name("std");
        await Admin.ExecuteAsync(CreateEntity(name, e => e.TableType = "standard"));
        (await RetrieveEntityAsync(name)).TableType.Should().Be("Standard");
    }

    [Fact]
    public async Task CreateEntity_refuses_an_unknown_table_type()
    {
        var name = Name("virt");
        (await BadRequestAsync(() => Admin.ExecuteAsync(CreateEntity(name, e => e.TableType = "Virtual")))).Should().Contain("Virtual");
        Store.GetEntity(name).Should().BeNull();
    }

    [Fact]
    public async Task WebApi_creates_and_reads_an_elastic_table()
    {
        var name = Name("web");
        var body = $$"""
            {
              "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
              "LogicalName": "{{name}}", "SchemaName": "{{name}}", "OwnershipType": "OrganizationOwned", "TableType": "ELASTIC",
              "DisplayName": { "LocalizedLabels": [ { "Label": "Web", "LanguageCode": 1033 } ] },
              "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name",
                                "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true } ]
            }
            """;
        var created = await SendAsync(HttpMethod.Post, "EntityDefinitions", body);
        created.StatusCode.Should().Be(HttpStatusCode.NoContent, await created.Content.ReadAsStringAsync());

        var res = await SendAsync(HttpMethod.Get, $"EntityDefinitions(LogicalName='{name}')?$select=TableType");
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        JsonNode.Parse(text)!["TableType"]!.GetValue<string>().Should().Be("Elastic");

        var keys = await SendAsync(HttpMethod.Get, $"EntityDefinitions(LogicalName='{name}')/Keys");
        var keysText = await keys.Content.ReadAsStringAsync();
        keys.StatusCode.Should().Be(HttpStatusCode.OK, keysText);
        var value = JsonNode.Parse(keysText)!["value"]!.AsArray();
        value.Should().ContainSingle();
        value[0]!["LogicalName"]!.GetValue<string>().Should().Be("keyfornosqlentitywithpkpartitionid");
        value[0]!["KeyAttributes"]!.AsArray().Select(k => k!.GetValue<string>()).Should().Equal($"{name}id", "partitionid");
    }

    [Fact]
    public async Task WebApi_refuses_an_unknown_table_type_and_a_team_owned_elastic_table()
    {
        foreach (var (type, ownership) in new[] { ("Virtual", "UserOwned"), ("Elastic", "TeamOwned") })
        {
            var name = Name("bad" + type[..2] + ownership[..2]);
            var body = $$"""
                {
                  "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
                  "LogicalName": "{{name}}", "SchemaName": "{{name}}", "OwnershipType": "{{ownership}}", "TableType": "{{type}}",
                  "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name",
                                    "SchemaName": "new_Name", "MaxLength": 100, "IsPrimaryName": true } ]
                }
                """;
            var res = await SendAsync(HttpMethod.Post, "EntityDefinitions", body);
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
            Store.GetEntity(name).Should().BeNull();
        }
    }

    [Fact]
    public async Task WebApi_refuses_a_money_column_on_an_elastic_table()
    {
        var name = Name("webm");
        await CreateElasticAsync(name);
        var body = """{ "@odata.type": "Microsoft.Dynamics.CRM.MoneyAttributeMetadata", "LogicalName": "new_amount", "SchemaName": "new_Amount" }""";

        var res = await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{name}')/Attributes", body);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateEntity_cannot_change_the_table_type()
    {
        var elastic = Name("upe");
        var standard = Name("ups");
        await CreateElasticAsync(elastic);
        await Admin.ExecuteAsync(CreateEntity(standard));

        (await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = standard, TableType = "Elastic" } })))
            .Should().Contain("TableType");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = elastic, TableType = "Standard" } })))
            .Should().Contain("TableType");
        (await RetrieveEntityAsync(standard)).TableType.Should().Be("Standard");

        // The same value, and a retrieved definition sent back, pass.
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = elastic, TableType = "elastic" } });
        var retrieved = await RetrieveEntityAsync(elastic);
        retrieved.DisplayName = Label("Relabelled");
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = retrieved });
    }

    [Fact]
    public async Task CreateManyToMany_with_an_elastic_side_is_refused()
    {
        var elastic = Name("mme");
        var standard = Name("mms");
        await CreateElasticAsync(elastic);
        await Admin.ExecuteAsync(CreateEntity(standard));
        var schema = Name("mmrel");

        (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata { SchemaName = schema, Entity1LogicalName = elastic, Entity2LogicalName = standard },
            IntersectEntitySchemaName = schema,
        }))).Should().Contain("elastic table");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateManyToManyRequest
        {
            ManyToManyRelationship = new ManyToManyRelationshipMetadata { SchemaName = schema, Entity1LogicalName = standard, Entity2LogicalName = elastic },
            IntersectEntitySchemaName = schema,
        }))).Should().Contain("elastic table");
        Store.GetEntity(schema).Should().BeNull();
    }

    private Task<Microsoft.Xrm.Sdk.OrganizationResponse> OneToManyAsync(string referenced, string referencing, string lookup, CascadeConfiguration? cascade) =>
        Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata
            {
                SchemaName = $"{referencing}_{lookup}", ReferencedEntity = referenced, ReferencingEntity = referencing, CascadeConfiguration = cascade,
            },
            Lookup = new LookupAttributeMetadata { LogicalName = lookup, SchemaName = lookup, DisplayName = Label("Lookup") },
        });

    [Fact]
    public async Task CreateOneToMany_to_an_elastic_table_refuses_cascade_and_adds_the_pid_column()
    {
        var elastic = Name("oe");
        var standard = Name("os");
        await CreateElasticAsync(elastic);
        await Admin.ExecuteAsync(CreateEntity(standard));

        (await BadRequestAsync(() => OneToManyAsync(elastic, standard, "new_cascade", new CascadeConfiguration { Delete = CascadeType.Cascade })))
            .Should().Contain("Delete");
        Store.GetEntity(standard)!.Attributes.Should().NotContain(a => a.LogicalName == "new_cascade" || a.LogicalName == "new_cascadepid");

        await OneToManyAsync(elastic, standard, "new_sensorid", new CascadeConfiguration
        {
            Assign = CascadeType.NoCascade, Delete = CascadeType.NoCascade, Merge = CascadeType.NoCascade, Reparent = CascadeType.NoCascade,
            Share = CascadeType.NoCascade, Unshare = CascadeType.NoCascade, RollupView = CascadeType.NoCascade,
        });

        var attributes = (await RetrieveEntityAsync(standard)).Attributes;
        attributes.Should().Contain(a => a.LogicalName == "new_sensorid");
        var pid = attributes.Single(a => a.LogicalName == "new_sensoridpid").Should().BeOfType<StringAttributeMetadata>().Which;
        pid.MaxLength.Should().Be(100);
        pid.RequiredLevel.Value.Should().Be(AttributeRequiredLevel.None);
    }

    [Fact]
    public async Task CreateOneToMany_between_standard_tables_has_no_pid_column()
    {
        var a = Name("sa");
        var b = Name("sb");
        await Admin.ExecuteAsync(CreateEntity(a));
        await Admin.ExecuteAsync(CreateEntity(b));

        await OneToManyAsync(a, b, "new_parentid", new CascadeConfiguration { Delete = CascadeType.Cascade });

        (await RetrieveEntityAsync(b)).Attributes.Should().NotContain(x => x.LogicalName == "new_parentidpid");
    }

    [Fact]
    public async Task Elastic_referencing_table_cannot_cascade_either()
    {
        var elastic = Name("re");
        var standard = Name("rs");
        await CreateElasticAsync(elastic);
        await Admin.ExecuteAsync(CreateEntity(standard));

        (await BadRequestAsync(() => OneToManyAsync(standard, elastic, "new_parentid", new CascadeConfiguration { Assign = CascadeType.Cascade })))
            .Should().Contain("Assign");
        await OneToManyAsync(standard, elastic, "new_parentid", null);
        (await RetrieveEntityAsync(elastic)).Attributes.Should().NotContain(x => x.LogicalName == "new_parentidpid");
    }

    [Fact]
    public async Task CreateEntity_refuses_team_owned_activity_and_the_other_E9_table_settings()
    {
        var team = Name("tm");
        var act = Name("ac");
        var access = Name("at");
        var duplicates = Name("dd");
        (await BadRequestAsync(() => CreateElasticAsync(team, e => e.OwnershipType = OwnershipTypes.TeamOwned))).Should().NotBeEmpty();
        (await BadRequestAsync(() => CreateElasticAsync(act, e => e.IsActivity = true))).Should().NotBeEmpty();
        (await BadRequestAsync(() => CreateElasticAsync(access, e => e.AutoCreateAccessTeams = true))).Should().Contain("access teams");
        (await BadRequestAsync(() => CreateElasticAsync(duplicates, e => e.IsDuplicateDetectionEnabled = new Microsoft.Xrm.Sdk.BooleanManagedProperty(true))))
            .Should().Contain("duplicate detection");
        foreach (var n in new[] { team, act, access, duplicates }) Store.GetEntity(n).Should().BeNull(n);
    }

    [Fact]
    public async Task CreateEntity_refuses_an_E9_column_in_the_attributes()
    {
        var name = Name("ecol");
        var request = CreateEntity(name, e => e.TableType = "Elastic");
        request.Entity.SetAttributes([new MoneyAttributeMetadata { LogicalName = "new_amount", SchemaName = "new_Amount" }]);

        (await BadRequestAsync(() => Admin.ExecuteAsync(request))).Should().Contain("Money");
        Store.GetEntity(name).Should().BeNull();
    }

    [Fact]
    public async Task CreateAttribute_refuses_the_E9_types_and_accepts_json_strings()
    {
        var name = Name("attr");
        await CreateElasticAsync(name);

        AttributeMetadata[] refused =
        [
            new MoneyAttributeMetadata { LogicalName = "new_money", SchemaName = "new_Money" },
            new MultiSelectPicklistAttributeMetadata { LogicalName = "new_multi", SchemaName = "new_Multi" },
            new ImageAttributeMetadata { LogicalName = "new_image", SchemaName = "new_Image" },
            new IntegerAttributeMetadata { LogicalName = "new_duration", SchemaName = "new_Duration", Format = IntegerFormat.Duration },
        ];
        foreach (var attribute in refused)
        {
            (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = name, Attribute = attribute })))
                .Should().Contain("elastic table", attribute.LogicalName);
        }

        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new StringAttributeMetadata { LogicalName = "new_payload", SchemaName = "new_Payload", MaxLength = 4000, FormatName = StringFormatName.Json },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name,
            Attribute = new IntegerAttributeMetadata { LogicalName = "new_value", SchemaName = "new_Value", Format = IntegerFormat.None },
        });
        (await RetrieveEntityAsync(name)).Attributes.Select(a => a.LogicalName).Should().Contain(["new_payload", "new_value"]);
    }

    [Fact]
    public async Task CreateAttribute_refuses_the_names_of_the_system_columns()
    {
        var name = Name("sys");
        await CreateElasticAsync(name);

        (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = name, Attribute = new StringAttributeMetadata { LogicalName = "partitionid", SchemaName = "PartitionId", MaxLength = 10 },
        }))).Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateCustomerRelationships_on_an_elastic_table_is_refused()
    {
        var name = Name("cust");
        await CreateElasticAsync(name);
        var lookup = new LookupAttributeMetadata { LogicalName = "new_customerid", SchemaName = "new_CustomerId", DisplayName = Label("Customer") };
        OneToManyRelationshipMetadata Relationship(string target) => new()
        {
            SchemaName = $"{name}_customer_{target}", ReferencedEntity = target, ReferencingEntity = name, ReferencedAttribute = target + "id",
        };

        var request = new CreateCustomerRelationshipsRequest
        {
            Lookup = lookup,
            OneToManyRelationships = [Relationship("account"), Relationship("contact")],
        };

        (await BadRequestAsync(() => Admin.ExecuteAsync(request))).Should().Contain("elastic table");
        (await RetrieveEntityAsync(name)).Attributes.Should().NotContain(a => a.LogicalName == "new_customerid");
    }

    [Fact]
    public async Task Deleting_the_relationship_or_the_lookup_drops_the_pid_column_and_allows_re_creation()
    {
        var elastic = Name("de");
        var standard = Name("ds");
        await CreateElasticAsync(elastic);
        await Admin.ExecuteAsync(CreateEntity(standard));

        foreach (var viaAttribute in new[] { false, true })
        {
            await OneToManyAsync(elastic, standard, "new_sensorid", null);
            (await RetrieveEntityAsync(standard)).Attributes.Should().Contain(a => a.LogicalName == "new_sensoridpid");

            if (viaAttribute)
                await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = standard, LogicalName = "new_sensorid" });
            else
                await Admin.ExecuteAsync(new DeleteRelationshipRequest { Name = $"{standard}_new_sensorid" });

            var names = (await RetrieveEntityAsync(standard)).Attributes.Select(a => a.LogicalName).ToList();
            names.Should().NotContain(["new_sensorid", "new_sensoridpid"]);
        }
    }

    [Fact]
    public async Task Deleting_the_elastic_target_is_refused_while_a_lookup_points_at_it_so_no_pid_column_is_orphaned_whatever_the_order()
    {
        // Guard for #79: DeleteEntity refuses a table that a lookup still targets, so the <lookup>pid column cannot outlive its
        // target. Once the lookup goes (taking its pid column), the target can be deleted.
        var elastic = Name("ge");
        var standard = Name("gs");
        await CreateElasticAsync(elastic);
        await Admin.ExecuteAsync(CreateEntity(standard));
        await OneToManyAsync(elastic, standard, "new_sensorid", null);

        (await BadRequestAsync(() => Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = elastic }))).Should().Contain("new_sensorid");
        Store.GetEntity(elastic).Should().NotBeNull();
        (await RetrieveEntityAsync(standard)).Attributes.Select(a => a.LogicalName).Should().Contain(["new_sensorid", "new_sensoridpid"]);

        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = standard, LogicalName = "new_sensorid" });
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = elastic });

        Store.GetEntity(elastic).Should().BeNull();
        (await RetrieveEntityAsync(standard)).Attributes.Select(a => a.LogicalName).Should().NotContain(a => a.StartsWith("new_sensorid"));
        (await TestSql.ColumnsAsync(fx.ConnectionString, "data", standard)).Should().NotContain(c => c.StartsWith("new_sensorid"));
    }

    [Fact]
    public async Task A_failing_pid_column_leaves_no_lookup_behind()
    {
        var elastic = Name("ae");
        var standard = Name("as");
        await CreateElasticAsync(elastic);
        await Admin.ExecuteAsync(CreateEntity(standard));
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = standard, Attribute = new StringAttributeMetadata { LogicalName = "new_refpid", SchemaName = "new_RefPid", MaxLength = 10 },
        });

        await FluentActions.Awaiting(() => OneToManyAsync(elastic, standard, "new_ref", null)).Should().ThrowAsync<Exception>();

        var names = (await RetrieveEntityAsync(standard)).Attributes.Select(a => a.LogicalName).ToList();
        names.Should().NotContain("new_ref");
        Store.GetOneToManyRelationships().Should().NotContain(r => r.ReferencingEntity == standard && r.ReferencingAttribute == "new_ref");
    }

    [Fact]
    public async Task ExportSolution_of_a_solution_holding_an_elastic_table_is_refused_naming_it()
    {
        var table = Name("exp");
        var solution = "elexp" + _run;
        await Admin.CreateAsync(new Microsoft.Xrm.Sdk.Entity("solution")
        {
            ["uniquename"] = solution, ["friendlyname"] = solution, ["version"] = "1.0.0.0",
            ["publisherid"] = new Microsoft.Xrm.Sdk.EntityReference("publisher", SystemSeed.DefaultPublisherId),
        });
        await CreateElasticAsync(table);
        await Admin.ExecuteAsync(new Microsoft.Crm.Sdk.Messages.AddSolutionComponentRequest
        {
            ComponentType = Xrmd.Solution.ComponentType.Entity, ComponentId = Store.GetEntity(table)!.MetadataId!.Value, SolutionUniqueName = solution,
            AddRequiredComponents = false,
        });

        var message = await BadRequestAsync(() => Admin.ExecuteAsync(new Microsoft.Crm.Sdk.Messages.ExportSolutionRequest { SolutionName = solution, Managed = false }));

        message.Should().Contain(table).And.Contain("elastic table").And.Contain("#77");
    }

    [Fact]
    public async Task Schema_apply_creates_an_elastic_table_and_get_schema_exports_its_type()
    {
        var table = "zq_el" + _run;
        var yaml = $"publisher: zq\ntables:\n  - name: {table}\n    label: Sensor\n    tableType: elastic\n    primaryName: {{ name: zq_name }}\n";
        async Task<HttpResponseMessage> Post(string body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/admin/schema/apply?dryRun=false");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
            req.Content = new StringContent(body, Encoding.UTF8, "text/yaml");
            return await fx.Client.SendAsync(req);
        }

        var applied = await Post(yaml);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync());
        (await RetrieveEntityAsync(table)).TableType.Should().Be("Elastic");

        var get = new HttpRequestMessage(HttpMethod.Get, "/admin/schema?publisher=zq");
        get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var exported = await (await fx.Client.SendAsync(get)).Content.ReadAsStringAsync();
        exported.Should().Contain("tableType: Elastic");

        var standardYaml = yaml.Replace("tableType: elastic", "tableType: standard");
        var rejected = await Post(standardYaml);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain(table).And.Contain("Elastic");

        await TestEntities.InSessionAsync(fx.ConnectionString, s => Store.DeleteEntityAsync(s, table, default));
    }
}

using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
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
using Xrmd.Solution;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Customer columns end to end (plan 11, task 3): <c>CreateCustomerRelationships</c> (SDK and Web API, with the prefix rule),
/// the refusal of every other route, the targets, the Web API navigation property per target, the delete cascades of the seeded
/// and custom Customer relationships, and the solution export and import of a Customer column and of a table extended by a solution.
/// </summary>
[Collection("xrmd")]
public sealed class CustomerColumnTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => TestEntities.EnsureProjectTagAsync(Store, fx.ConnectionString);

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private string Name(string label) => $"new_{label}{_run}";

    private async Task<string> NewTableAsync(string label, string? solution = null)
    {
        var name = Name(label);
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = Label("Table " + name), DisplayCollectionName = Label("Tables " + name),
                OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
            SolutionUniqueName = solution,
        });
        return name;
    }

    private static OneToManyRelationshipMetadata Relationship(string schemaName, string referencing, string referenced) => new()
    {
        SchemaName = schemaName, ReferencingEntity = referencing, ReferencedEntity = referenced,
        CascadeConfiguration = new CascadeConfiguration { Delete = CascadeType.RemoveLink },
    };

    private static LookupAttributeMetadata CustomerLookup(string logicalName) => new()
    {
        LogicalName = logicalName, SchemaName = logicalName, DisplayName = Label("Customer"),
        RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
    };

    private static CreateCustomerRelationshipsRequest CreateCustomer(string table, string column, string prefix, string? solution = null) => new()
    {
        Lookup = CustomerLookup(column),
        OneToManyRelationships =
        [
            Relationship($"{prefix}_{table}_account", table, "account"),
            Relationship($"{prefix}_{table}_contact", table, "contact"),
        ],
        SolutionUniqueName = solution,
    };

    /// <summary>A table with a Customer column <c>new_customerid</c>.</summary>
    private async Task<string> NewCustomerTableAsync(string label, string? solution = null)
    {
        var table = await NewTableAsync(label, solution);
        await Admin.ExecuteAsync(CreateCustomer(table, "new_customerid", "new", solution));
        return table;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, string? solution = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (solution is not null) req.Headers.Add("MSCRM.SolutionUniqueName", solution);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage res, HttpStatusCode status)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        return JsonNode.Parse(body)!["error"]!["message"]!.GetValue<string>();
    }

    private async Task<JsonObject> GetJsonAsync(string path)
    {
        var res = await SendAsync(HttpMethod.Get, path);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static string LabelJson(string text) => $$"""{ "LocalizedLabels": [ { "Label": "{{text}}", "LanguageCode": 1033 } ] }""";

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private string CreateBody(string table, string column, string prefix, string extra = "") => $$"""
        {
          "Lookup": { "@odata.type": "Microsoft.Dynamics.CRM.LookupAttributeMetadata", "LogicalName": "{{column}}", "SchemaName": "{{column}}",
                      "DisplayName": {{LabelJson("Customer")}} },
          "OneToManyRelationships": [
            { "@odata.type": "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata", "SchemaName": "{{prefix}}_{{table}}_account",
              "ReferencedEntity": "account", "ReferencingEntity": "{{table}}",
              "CascadeConfiguration": { "Delete": "RemoveLink", "Assign": "NoCascade" } },
            { "@odata.type": "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata", "SchemaName": "{{prefix}}_{{table}}_contact",
              "ReferencedEntity": "contact", "ReferencingEntity": "{{table}}" }
          ]{{extra}}
        }
        """;

    // ---- C1: CreateCustomerRelationships ----------------------------------------------------------------------------

    [Fact]
    public async Task Create_customer_relationships_on_a_custom_table()
    {
        var table = await NewTableAsync("cust");
        var response = (CreateCustomerRelationshipsResponse)await Admin.ExecuteAsync(CreateCustomer(table, "new_customerid", "new", "Default"));

        response.AttributeId.Should().NotBeEmpty();
        response.RelationshipIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        var column = Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == "new_customerid")
            .Should().BeOfType<LookupAttributeMetadata>().Subject;
        column.AttributeType.Should().Be(AttributeTypeCode.Customer);
        column.Targets.Should().BeEquivalentTo("account", "contact");
        column.MetadataId.Should().Be(response.AttributeId);
        var relationships = Store.GetOneToManyRelationships().Where(r => r.ReferencingEntity == table && r.ReferencingAttribute == "new_customerid").ToList();
        relationships.Select(r => r.MetadataId!.Value).Should().BeEquivalentTo(response.RelationshipIds);
        relationships.Select(r => r.SchemaName).Should().BeEquivalentTo($"new_{table}_account", $"new_{table}_contact");
        relationships.Should().OnlyContain(r => r.IsCustomRelationship == true && r.CascadeConfiguration.Delete == CascadeType.RemoveLink);
        // Merge defaults to Cascade, the only valid option on Dataverse for account and contact.
        relationships.Should().OnlyContain(r => r.CascadeConfiguration.Merge == CascadeType.Cascade);
        relationships.Select(r => r.ReferencingEntityNavigationPropertyName).Should().BeEquivalentTo("new_customerid_account", "new_customerid_contact");
        (await TestSql.ColumnsAsync(fx.ConnectionString, "data", table)).Should().Contain(["new_customerid", "new_customeridtype"]);

        // Both targets hold a record; the lookup keeps the table of the value.
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Acme " + _run });
        var contact = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Doe" + _run });
        var onAccount = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", ["new_customerid"] = new EntityReference("account", account) });
        var onContact = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "c", ["new_customerid"] = new EntityReference("contact", contact) });
        (await Admin.RetrieveAsync(table, onAccount, new ColumnSet("new_customerid"))).GetAttributeValue<EntityReference>("new_customerid")
            .Should().Match<EntityReference>(r => r.LogicalName == "account" && r.Id == account);
        (await Admin.RetrieveAsync(table, onContact, new ColumnSet("new_customerid"))).GetAttributeValue<EntityReference>("new_customerid")
            .Should().Match<EntityReference>(r => r.LogicalName == "contact" && r.Id == contact);
        var team = await Admin.CreateAsync(new Entity("team") { ["name"] = "T" + _run, ["businessunitid"] = new EntityReference("businessunit", await RootBusinessUnitAsync(fx)) });
        await BadRequestAsync(() => Admin.CreateAsync(new Entity(table) { ["new_name"] = "x", ["new_customerid"] = new EntityReference("team", team) }));

        // The attribute and both relationships are in Default and in the named solution.
        foreach (var (type, id) in new[] { (ComponentType.Attribute, response.AttributeId), (ComponentType.EntityRelationship, response.RelationshipIds[0]),
                     (ComponentType.EntityRelationship, response.RelationshipIds[1]) })
            (await ScalarAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE solutionid = @s AND componenttype = @t AND objectid = @o",
                ("@s", SystemSeed.DefaultSolutionId), ("@t", type), ("@o", id))).Should().Be(1);

        // Deleting the column deletes both relationships.
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_customerid" });
        Store.GetRelationship($"new_{table}_account").Should().BeNull();
        Store.GetRelationship($"new_{table}_contact").Should().BeNull();
    }

    [Fact]
    public async Task Create_customer_relationships_through_the_web_api()
    {
        var table = await NewTableAsync("webcust");
        var res = await SendAsync(HttpMethod.Post, "CreateCustomerRelationships", CreateBody(table, "new_customerid", "new"));
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!.AsObject();
        var ids = json["RelationshipIds"]!.AsArray().Select(n => Guid.Parse(n!.GetValue<string>())).ToList();
        ids.Should().HaveCount(2);
        Guid.Parse(json["AttributeId"]!.GetValue<string>()).Should().Be(Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == "new_customerid").MetadataId!.Value);
        Store.GetOneToManyRelationships().Where(r => r.ReferencingAttribute == "new_customerid" && r.ReferencingEntity == table)
            .Select(r => r.MetadataId!.Value).Should().BeEquivalentTo(ids);

        // The prefix rule applies to the column and to each relationship, and a body parameter that does not exist is refused.
        var other = await NewTableAsync("webcust2");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "CreateCustomerRelationships", CreateBody(other, "zz_customerid", "new")), HttpStatusCode.BadRequest))
            .Should().Contain("zz_customerid");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "CreateCustomerRelationships", CreateBody(other, "new_customerid", "zz")), HttpStatusCode.BadRequest))
            .Should().Contain($"zz_{other}_account");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "CreateCustomerRelationships", CreateBody(other, "new_customerid", "new", ", \"Nope\": 1")),
            HttpStatusCode.BadRequest)).Should().Contain("Nope");
        Store.GetEntity(other)!.Attributes.Should().NotContain(a => a.LogicalName == "new_customerid");
    }

    [Fact]
    public async Task Create_customer_relationships_accepts_the_learn_example_body()
    {
        // The body of Learn's "Create a customer lookup column" example (create-update-column-definitions-using-web-api), with
        // the pb_ prefix and a table of this test: a ComplexLookupAttributeMetadata with a SchemaName and no LogicalName, relationships
        // without @odata.type or cascade, the solution in the MSCRM.SolutionUniqueName header.
        var table = await NewTableAsync("learn");
        var solution = await NewSolutionAsync("learn");
        var body = $$"""
            {
                "OneToManyRelationships": [{
                    "SchemaName": "pb_{{table}}_customer_account",
                    "ReferencedEntity": "account",
                    "ReferencingEntity": "{{table}}"
                }, {
                    "SchemaName": "pb_{{table}}_customer_contact",
                    "ReferencedEntity": "contact",
                    "ReferencingEntity": "{{table}}"
                }],
                "Lookup": {
                    "AttributeType": "Lookup",
                    "AttributeTypeName": {
                        "Value": "LookupType"
                    },
                    "Description": {
                        "@odata.type": "Microsoft.Dynamics.CRM.Label",
                        "LocalizedLabels": [{
                            "@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel",
                            "Label": "Sample Customer Lookup Attribute",
                            "LanguageCode": 1033
                        }],
                        "UserLocalizedLabel": {
                            "@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel",
                            "Label": "Sample Customer Lookup Attribute",
                            "LanguageCode": 1033
                        }
                    },
                    "DisplayName": {
                        "@odata.type": "Microsoft.Dynamics.CRM.Label",
                        "LocalizedLabels": [{
                            "@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel",
                            "Label": "Customer",
                            "LanguageCode": 1033
                        }],
                        "UserLocalizedLabel": {
                            "@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel",
                            "Label": "Customer",
                            "LanguageCode": 1033
                        }
                    },
                    "SchemaName": "pb_CustomerId",
                    "@odata.type": "Microsoft.Dynamics.CRM.ComplexLookupAttributeMetadata"
                }
            }
            """;
        var res = await SendAsync(HttpMethod.Post, "CreateCustomerRelationships", body, solution);
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var json = JsonNode.Parse(text)!.AsObject();
        json["RelationshipIds"]!.AsArray().Should().HaveCount(2);

        // The LogicalName is the SchemaName in lower case; the column is a Customer column with both relationships.
        var column = Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == "pb_customerid").Should().BeOfType<LookupAttributeMetadata>().Subject;
        column.SchemaName.Should().Be("pb_CustomerId");
        column.AttributeType.Should().Be(AttributeTypeCode.Customer);
        column.Targets.Should().Equal("account", "contact");
        column.MetadataId.Should().Be(Guid.Parse(json["AttributeId"]!.GetValue<string>()));
        var relationships = Store.GetOneToManyRelationships().Where(r => r.ReferencingEntity == table && r.ReferencingAttribute == "pb_customerid").ToList();
        relationships.Select(r => r.SchemaName).Should().BeEquivalentTo($"pb_{table}_customer_account", $"pb_{table}_customer_contact");
        relationships.Should().OnlyContain(r => r.CascadeConfiguration.Delete == CascadeType.RemoveLink && r.CascadeConfiguration.Merge == CascadeType.Cascade);
        (await ScalarAsync("SELECT COUNT(*) FROM data.solutioncomponent c JOIN data.solution s ON s.solutionid = c.solutionid WHERE s.uniquename = @s AND c.objectid = @o",
            ("@s", solution), ("@o", column.MetadataId!.Value))).Should().Be(1);

        // A lookup with neither LogicalName nor SchemaName is refused with the name rule, not the prefix rule.
        var other = await NewTableAsync("learn2");
        var nameless = body.Replace(table, other).Replace("\"SchemaName\": \"pb_CustomerId\",", "");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "CreateCustomerRelationships", nameless), HttpStatusCode.BadRequest))
            .Should().Contain("Invalid attribute logical name").And.NotContain("prefix");
    }

    [Fact]
    public async Task A_missing_logical_name_is_refused_before_the_prefix_rule()
    {
        var table = await NewTableAsync("noname");
        var attribute = new StringAttributeMetadata { SchemaName = "pb_Text", MaxLength = 10, DisplayName = Label("Text") };
        (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = attribute })))
            .Should().Contain("Invalid attribute logical name").And.NotContain("prefix");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata { SchemaName = $"new_{table}_x", ReferencedEntity = "account", ReferencingEntity = table },
            Lookup = new LookupAttributeMetadata { SchemaName = "new_X", DisplayName = Label("X") },
        }))).Should().Contain("Invalid attribute logical name").And.NotContain("prefix");
        var customer = CreateCustomer(table, "x", "new");
        customer.Lookup.LogicalName = null;
        (await BadRequestAsync(() => Admin.ExecuteAsync(customer))).Should().Contain("Invalid attribute logical name").And.NotContain("prefix");
        var web = """{ "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "SchemaName": "pb_Text", "MaxLength": 10 }""";
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{table}')/Attributes", web), HttpStatusCode.BadRequest))
            .Should().Contain("Invalid attribute logical name").And.NotContain("prefix");
    }

    [Fact]
    public async Task Customer_relationships_follow_the_prefix_rule()
    {
        var table = await NewTableAsync("prefix");
        var column = await BadRequestAsync(() => Admin.ExecuteAsync(CreateCustomer(table, "zz_customerid", "new")));
        column.Should().Contain("zz_customerid");
        var request = CreateCustomer(table, "new_customerid", "new");
        request.OneToManyRelationships[1].SchemaName = "zz_" + table + "_contact";
        (await BadRequestAsync(() => Admin.ExecuteAsync(request))).Should().Contain("zz_" + table + "_contact");
        Store.GetEntity(table)!.Attributes.Should().NotContain(a => a.LogicalName == "new_customerid");
    }

    [Fact]
    public async Task Create_attribute_with_customer_type_is_rejected()
    {
        var table = await NewTableAsync("noattr");
        var customer = CustomerLookup("new_customerid");
        SdkAttributeType(customer);

        // SDK CreateAttribute.
        (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = customer })))
            .Should().Contain("CreateCustomerRelationships");
        // SDK CreateOneToMany with a Customer lookup.
        customer.Targets = ["account"];
        (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            Lookup = customer, OneToManyRelationship = Relationship($"new_{table}_x", table, "account"),
        }))).Should().Contain("CreateCustomerRelationships");
        // Web API: the attribute route, the entity route and the relationship route.
        var attribute = $$"""
            { "@odata.type": "Microsoft.Dynamics.CRM.LookupAttributeMetadata", "LogicalName": "new_customerid", "SchemaName": "new_CustomerId",
              "AttributeType": "Customer", "Targets": ["account", "contact"] }
            """;
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{table}')/Attributes", attribute), HttpStatusCode.BadRequest))
            .Should().Contain("CreateCustomerRelationships");
        var entity = $$"""
            { "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata", "LogicalName": "{{Name("noattrent")}}", "SchemaName": "{{Name("noattrent")}}",
              "DisplayName": {{LabelJson("T")}}, "OwnershipType": "UserOwned",
              "Attributes": [ { "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata", "LogicalName": "new_name", "SchemaName": "new_Name",
                                "MaxLength": 100, "IsPrimaryName": true }, {{attribute}} ] }
            """;
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "EntityDefinitions", entity), HttpStatusCode.BadRequest))
            .Should().Contain("CreateCustomerRelationships");
        Store.GetEntity(Name("noattrent")).Should().BeNull();
        var relationship = $$"""
            { "@odata.type": "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata", "SchemaName": "new_{{table}}_y", "ReferencedEntity": "account",
              "ReferencingEntity": "{{table}}", "Lookup": {{attribute}} }
            """;
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "RelationshipDefinitions", relationship), HttpStatusCode.BadRequest))
            .Should().Contain("CreateCustomerRelationships");
        Store.GetEntity(table)!.Attributes.Should().NotContain(a => a.LogicalName == "new_customerid");
    }

    private static void SdkAttributeType(LookupAttributeMetadata lookup) =>
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!.SetValue(lookup, AttributeTypeCode.Customer);

    [Fact]
    public async Task Customer_targets_must_be_account_and_contact()
    {
        var table = await NewTableAsync("targets");
        var other = await NewTableAsync("targetsother");

        CreateCustomerRelationshipsRequest With(Action<CreateCustomerRelationshipsRequest> change)
        {
            var request = CreateCustomer(table, "new_customerid", "new");
            change(request);
            return request;
        }

        // Two relationships to the same table, to a custom table, to a system table; one or three relationships.
        foreach (var request in new[]
                 {
                     With(r => r.OneToManyRelationships[1].ReferencedEntity = "account"),
                     With(r => r.OneToManyRelationships[1].ReferencedEntity = other),
                     With(r => r.OneToManyRelationships[1].ReferencedEntity = "systemuser"),
                     With(r => r.OneToManyRelationships = [r.OneToManyRelationships[0]]),
                     With(r => r.OneToManyRelationships = [.. r.OneToManyRelationships, Relationship($"new_{table}_third", table, "systemuser")]),
                     With(r => r.Lookup.Targets = ["account", "contact", "systemuser"]),
                     With(r => r.Lookup.Targets = ["account"]),
                 })
            (await BadRequestAsync(() => Admin.ExecuteAsync(request))).Should().Contain("account").And.Contain("contact");
        // Relationships on two tables.
        var split = CreateCustomer(table, "new_customerid", "new");
        split.OneToManyRelationships[1].ReferencingEntity = other;
        await BadRequestAsync(() => Admin.ExecuteAsync(split));
        // Another lookup type than a Customer one.
        var owner = CustomerLookup("new_customerid");
        typeof(AttributeMetadata).GetProperty(nameof(AttributeMetadata.AttributeType))!.SetValue(owner, AttributeTypeCode.Owner);
        var ownerRequest = CreateCustomer(table, "new_customerid", "new");
        ownerRequest.Lookup = owner;
        await BadRequestAsync(() => Admin.ExecuteAsync(ownerRequest));
        // A Delete or Merge value that is not valid on Dataverse is refused, not stored.
        var cascade = CreateCustomer(table, "new_customerid", "new");
        cascade.OneToManyRelationships[0].CascadeConfiguration = new CascadeConfiguration { Delete = CascadeType.NoCascade };
        (await BadRequestAsync(() => Admin.ExecuteAsync(cascade))).Should().Contain("CascadeConfiguration.Delete");
        var merge = CreateCustomer(table, "new_customerid", "new");
        merge.OneToManyRelationships[0].CascadeConfiguration = new CascadeConfiguration { Delete = CascadeType.RemoveLink, Merge = CascadeType.Active };
        (await BadRequestAsync(() => Admin.ExecuteAsync(merge))).Should().Contain("CascadeConfiguration.Merge");

        Store.GetEntity(table)!.Attributes.Should().NotContain(a => a.LogicalName == "new_customerid");
        Store.GetOneToManyRelationships().Should().NotContain(r => r.SchemaName.StartsWith($"new_{table}_"));
        // The two valid relationships still go through, whichever order they are listed in.
        var reversed = CreateCustomer(table, "new_customerid", "new");
        reversed.OneToManyRelationships = [.. reversed.OneToManyRelationships.Reverse()];
        await Admin.ExecuteAsync(reversed);
        Store.GetEntity(table)!.Attributes.OfType<LookupAttributeMetadata>().Single(a => a.LogicalName == "new_customerid").Targets
            .Should().Equal("account", "contact");
    }

    // ---- C2: navigation properties, bind ----------------------------------------------------------------------------

    [Fact]
    public async Task Customer_bind_accepts_one_nav_per_target_and_rejects_others()
    {
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Acme " + _run });
        var parent = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Parent" + _run });

        async Task<Guid> PostAsync(string bind)
        {
            var res = await SendAsync(HttpMethod.Post, "contacts", $$"""{ "lastname": "Child{{_run}}", {{bind}} }""");
            res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
            var location = res.Headers.GetValues("OData-EntityId").Single();
            return Guid.Parse(location[(location.LastIndexOf('(') + 1)..^1]);
        }

        // A correct bind stores the right target type.
        var onAccount = await PostAsync($"\"parentcustomerid_account@odata.bind\": \"/accounts({account})\"");
        var onContact = await PostAsync($"\"parentcustomerid_contact@odata.bind\": \"{fx.BaseUrl}{Api}contacts({parent})\"");
        var read = await GetJsonAsync($"contacts({onAccount})?$select=_parentcustomerid_value");
        read["_parentcustomerid_value"]!.GetValue<string>().Should().Be(account.ToString());
        read["_parentcustomerid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be("account");
        read["_parentcustomerid_value@Microsoft.Dynamics.CRM.associatednavigationproperty"]!.GetValue<string>().Should().Be("parentcustomerid_account");
        read = await GetJsonAsync($"contacts({onContact})?$select=_parentcustomerid_value");
        read["_parentcustomerid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be("contact");
        read["_parentcustomerid_value@Microsoft.Dynamics.CRM.associatednavigationproperty"]!.GetValue<string>().Should().Be("parentcustomerid_contact");
        (await ScalarAsync("SELECT parentcustomeridtype FROM data.contact WHERE contactid = @id", ("@id", onAccount))).Should().Be(1);
        (await ScalarAsync("SELECT parentcustomeridtype FROM data.contact WHERE contactid = @id", ("@id", onContact))).Should().Be(2);

        // An update through the other navigation property changes the target; null clears the column.
        var patch = await SendAsync(HttpMethod.Patch, $"contacts({onAccount})", $$"""{ "parentcustomerid_contact@odata.bind": "/contacts({{parent}})" }""");
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent, await patch.Content.ReadAsStringAsync());
        (await Admin.RetrieveAsync("contact", onAccount, new ColumnSet("parentcustomerid"))).GetAttributeValue<EntityReference>("parentcustomerid")
            .Should().Match<EntityReference>(r => r.LogicalName == "contact" && r.Id == parent);
        patch = await SendAsync(HttpMethod.Patch, $"contacts({onAccount})", """{ "parentcustomerid_contact@odata.bind": null }""");
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent, await patch.Content.ReadAsStringAsync());
        (await Admin.RetrieveAsync("contact", onAccount, new ColumnSet("parentcustomerid"))).GetAttributeValue<EntityReference>("parentcustomerid").Should().BeNull();

        // Both navigation properties in one body, the bare name, the other table's record, the value property: 400.
        async Task<string> RejectedAsync(string json)
        {
            var res = await SendAsync(HttpMethod.Post, "contacts", json);
            return await ErrorMessageAsync(res, HttpStatusCode.BadRequest);
        }
        (await RejectedAsync($$"""{ "lastname": "X", "parentcustomerid_account@odata.bind": "/accounts({{account}})", "parentcustomerid_contact@odata.bind": "/contacts({{parent}})" }"""))
            .Should().Contain("parentcustomerid_account").And.Contain("parentcustomerid_contact").And.Contain("both");
        (await RejectedAsync($$"""{ "lastname": "X", "parentcustomerid@odata.bind": "/accounts({{account}})" }"""))
            .Should().Contain("parentcustomerid_account@odata.bind").And.Contain("parentcustomerid_contact@odata.bind");
        (await RejectedAsync($$"""{ "lastname": "X", "parentcustomerid_account@odata.bind": "/contacts({{parent}})" }"""))
            .Should().Contain("account").And.Contain("contact");
        (await RejectedAsync($$"""{ "lastname": "X", "_parentcustomerid_value": "{{account}}" }"""))
            .Should().Contain("parentcustomerid_account@odata.bind").And.Contain("parentcustomerid_contact@odata.bind");
        (await RejectedAsync($$"""{ "lastname": "X", "parentcustomerid": "{{account}}" }"""))
            .Should().Contain("parentcustomerid_account@odata.bind");
        (await RejectedAsync($$"""{ "lastname": "X", "parentcustomerid_account@odata.bind": "/teams({{Guid.NewGuid()}})" }"""))
            .Should().Contain("account");

        // $expand of a Customer column is refused, not answered with one target only.
        var expand = await SendAsync(HttpMethod.Get, $"contacts({onAccount})?$expand=parentcustomerid_account");
        (await ErrorMessageAsync(expand, HttpStatusCode.BadRequest)).Should().Contain("parentcustomerid_account");

        // ownerid is unchanged: one navigation property, bound by its logical name.
        var owner = await SendAsync(HttpMethod.Post, "contacts", $$"""{ "lastname": "Owned{{_run}}", "ownerid@odata.bind": "/systemusers({{await AdminUserIdAsync()}})" }""");
        owner.StatusCode.Should().Be(HttpStatusCode.NoContent, await owner.Content.ReadAsStringAsync());
    }

    private async Task<Guid> AdminUserIdAsync() =>
        (Guid)(await ScalarAsync("SELECT TOP 1 systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @id", ("@id", fx.AdminObjectId)))!;

    [Fact]
    public async Task Metadata_lists_one_nav_per_customer_target()
    {
        var table = await NewCustomerTableAsync("meta");
        var res = await SendAsync(HttpMethod.Get, "$metadata");
        var csdl = XDocument.Parse(await res.Content.ReadAsStringAsync());
        XNamespace edm = "http://docs.oasis-open.org/odata/ns/edm";

        List<string> Navigations(string entity, string column) => csdl.Descendants(edm + "EntityType").Single(e => (string)e.Attribute("Name")! == entity)
            .Elements(edm + "NavigationProperty").Where(n => ((string)n.Attribute("Name")!).StartsWith(column)).Select(n => (string)n.Attribute("Name")!).ToList();
        List<string> Targets(string entity, string nav) => csdl.Descendants(edm + "EntityType").Single(e => (string)e.Attribute("Name")! == entity)
            .Elements(edm + "NavigationProperty").Where(n => (string)n.Attribute("Name")! == nav).Select(n => (string)n.Attribute("Type")!).ToList();

        Navigations("contact", "parentcustomerid").Should().BeEquivalentTo("parentcustomerid_account", "parentcustomerid_contact");
        Targets("contact", "parentcustomerid_account").Should().Equal("Microsoft.Dynamics.CRM.account");
        Targets("contact", "parentcustomerid_contact").Should().Equal("Microsoft.Dynamics.CRM.contact");
        Navigations(table, "new_customerid").Should().BeEquivalentTo("new_customerid_account", "new_customerid_contact");
        Targets(table, "new_customerid_contact").Should().Equal("Microsoft.Dynamics.CRM.contact");
        // The value property is one, and ownerid keeps its single navigation property.
        csdl.Descendants(edm + "EntityType").Single(e => (string)e.Attribute("Name")! == "contact").Elements(edm + "Property")
            .Count(p => ((string)p.Attribute("Name")!).StartsWith("_parentcustomerid")).Should().Be(1);
        Navigations("contact", "ownerid").Should().Equal("ownerid");
    }

    // ---- RemoveLink -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_an_account_deletes_its_contacts_as_dataverse_does()
    {
        // contact_customer_accounts cascades Delete (a parental relationship); contact_customer_contacts, account_parent_account
        // and account_primary_contact remove the link.
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Gone " + _run });
        var other = await Admin.CreateAsync(new Entity("account") { ["name"] = "Stays " + _run });
        var child = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Child", ["parentcustomerid"] = new EntityReference("account", account) });
        var primary = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Primary", ["parentcustomerid"] = new EntityReference("account", account) });
        var sibling = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Sibling", ["parentcustomerid"] = new EntityReference("account", other) });
        var report = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Report", ["parentcustomerid"] = new EntityReference("contact", child) });
        await Admin.UpdateAsync(new Entity("account", other)
        {
            ["parentaccountid"] = new EntityReference("account", account), ["primarycontactid"] = new EntityReference("contact", primary),
        });

        await Admin.DeleteAsync("account", account);

        // The account's contacts are deleted.
        foreach (var gone in new[] { child, primary })
            (await FaultAsync(() => Admin.RetrieveAsync("contact", gone, new ColumnSet("lastname")))).HttpStatus.Should().Be(404);
        // The contact under a deleted contact keeps its row and loses the link (the id and its type column).
        var survivor = await Admin.RetrieveAsync("contact", report, new ColumnSet("parentcustomerid", "lastname"));
        survivor.GetAttributeValue<string>("lastname").Should().Be("Report");
        survivor.GetAttributeValue<EntityReference>("parentcustomerid").Should().BeNull();
        (await ScalarAsync("SELECT parentcustomeridtype FROM data.contact WHERE contactid = @id", ("@id", report))).Should().BeNull();
        // The other account loses its parent account and its primary contact (deleted by the cascade), and keeps its contacts.
        var stays = await Admin.RetrieveAsync("account", other, new ColumnSet("parentaccountid", "primarycontactid", "name"));
        stays.GetAttributeValue<EntityReference>("parentaccountid").Should().BeNull();
        stays.GetAttributeValue<EntityReference>("primarycontactid").Should().BeNull();
        (await Admin.RetrieveAsync("contact", sibling, new ColumnSet("parentcustomerid"))).GetAttributeValue<EntityReference>("parentcustomerid")
            .Id.Should().Be(other);

        // The same through the Web API.
        (await SendAsync(HttpMethod.Delete, $"accounts({other})")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await FaultAsync(() => Admin.RetrieveAsync("contact", sibling, new ColumnSet("lastname")))).HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task Deleting_a_contact_clears_the_account_lookups_and_the_child_contacts_that_point_to_it()
    {
        var boss = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Boss" + _run });
        var report = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Report", ["parentcustomerid"] = new EntityReference("contact", boss) });
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Primary " + _run, ["primarycontactid"] = new EntityReference("contact", boss) });
        var parent = await Admin.CreateAsync(new Entity("account") { ["name"] = "Parent " + _run });
        var subsidiary = await Admin.CreateAsync(new Entity("account") { ["name"] = "Sub " + _run, ["parentaccountid"] = new EntityReference("account", parent) });

        await Admin.DeleteAsync("contact", boss);
        (await Admin.RetrieveAsync("contact", report, new ColumnSet("parentcustomerid"))).GetAttributeValue<EntityReference>("parentcustomerid").Should().BeNull();
        (await Admin.RetrieveAsync("account", account, new ColumnSet("primarycontactid"))).GetAttributeValue<EntityReference>("primarycontactid").Should().BeNull();

        // account_parent_account removes the link: the subsidiary stays.
        await Admin.DeleteAsync("account", parent);
        (await Admin.RetrieveAsync("account", subsidiary, new ColumnSet("parentaccountid"))).GetAttributeValue<EntityReference>("parentaccountid").Should().BeNull();

        // A record that points to itself is deleted (account → account, contact → contact).
        var selfAccount = await Admin.CreateAsync(new Entity("account") { ["name"] = "Self " + _run });
        await Admin.UpdateAsync(new Entity("account", selfAccount) { ["parentaccountid"] = new EntityReference("account", selfAccount) });
        await Admin.DeleteAsync("account", selfAccount);
        var selfContact = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Self" + _run });
        await Admin.UpdateAsync(new Entity("contact", selfContact) { ["parentcustomerid"] = new EntityReference("contact", selfContact) });
        await Admin.DeleteAsync("contact", selfContact);
        (await ScalarAsync("SELECT COUNT(*) FROM data.account WHERE accountid = @id", ("@id", selfAccount))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.contact WHERE contactid = @id", ("@id", selfContact))).Should().Be(0);
    }

    [Fact]
    public async Task A_custom_customer_column_with_the_dataverse_cascades_is_created_and_deletes_with_its_parent()
    {
        var table = await NewTableAsync("cascade");
        var request = CreateCustomer(table, "new_customerid", "new");
        foreach (var r in request.OneToManyRelationships)
            r.CascadeConfiguration = new CascadeConfiguration
            {
                Assign = CascadeType.Cascade, Delete = CascadeType.Cascade, Merge = CascadeType.Cascade, Reparent = CascadeType.Cascade,
                Share = CascadeType.Cascade, Unshare = CascadeType.Cascade, Archive = CascadeType.NoCascade, RollupView = CascadeType.NoCascade,
            };
        await Admin.ExecuteAsync(request);
        foreach (var r in Store.GetOneToManyRelationships().Where(r => r.ReferencingEntity == table && r.ReferencingAttribute == "new_customerid"))
        {
            r.CascadeConfiguration.Delete.Should().Be(CascadeType.Cascade);
            r.CascadeConfiguration.Merge.Should().Be(CascadeType.Cascade);
            r.CascadeConfiguration.Share.Should().Be(CascadeType.Cascade); // stored; xrmverse does not enforce it (README)
        }

        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Owner " + _run });
        var contact = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Owner" + _run });
        var onAccount = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", ["new_customerid"] = new EntityReference("account", account) });
        var onContact = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "c", ["new_customerid"] = new EntityReference("contact", contact) });
        await Admin.DeleteAsync("account", account);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{table} WHERE {table}id = @id", ("@id", onAccount))).Should().Be(0);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{table} WHERE {table}id = @id", ("@id", onContact))).Should().Be(1);

        // Restrict: the contact cannot go while a record points to it.
        var restricted = await NewTableAsync("restrict");
        var restrict = CreateCustomer(restricted, "new_customerid", "new");
        foreach (var r in restrict.OneToManyRelationships) r.CascadeConfiguration = new CascadeConfiguration { Delete = CascadeType.Restrict };
        await Admin.ExecuteAsync(restrict);
        await Admin.CreateAsync(new Entity(restricted) { ["new_name"] = "r", ["new_customerid"] = new EntityReference("contact", contact) });
        (await BadRequestAsync(() => Admin.DeleteAsync("contact", contact))).Should().Contain($"new_{restricted}_contact");
    }

    [Fact]
    public async Task Deleting_the_parent_clears_a_custom_customer_column()
    {
        var table = await NewCustomerTableAsync("rl");
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Parent " + _run });
        var contact = await Admin.CreateAsync(new Entity("contact") { ["lastname"] = "Parent" + _run });
        var onAccount = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "a", ["new_customerid"] = new EntityReference("account", account) });
        var onContact = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "c", ["new_customerid"] = new EntityReference("contact", contact) });

        await Admin.DeleteAsync("account", account);
        (await Admin.RetrieveAsync(table, onAccount, new ColumnSet("new_customerid"))).GetAttributeValue<EntityReference>("new_customerid").Should().BeNull();
        (await Admin.RetrieveAsync(table, onContact, new ColumnSet("new_customerid"))).GetAttributeValue<EntityReference>("new_customerid")
            .Id.Should().Be(contact);
        await Admin.DeleteAsync("contact", contact);
        (await Admin.RetrieveAsync(table, onContact, new ColumnSet("new_customerid"))).GetAttributeValue<EntityReference>("new_customerid").Should().BeNull();
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{table} WHERE new_customerid IS NOT NULL OR new_customeridtype IS NOT NULL")).Should().Be(0);
    }

    [Fact]
    public async Task One_to_many_remove_link_nulls_the_child_lookup_and_keeps_the_child()
    {
        var parent = await NewTableAsync("rlp");
        var child = await NewTableAsync("rlc");
        var keep = await NewTableAsync("rlk");
        foreach (var (table, cascade, lookup) in new[] { (child, CascadeType.RemoveLink, "new_parentid"), (keep, CascadeType.NoCascade, "new_keepid") })
            await Admin.ExecuteAsync(new CreateOneToManyRequest
            {
                OneToManyRelationship = new OneToManyRelationshipMetadata
                {
                    SchemaName = $"new_{table}_{lookup}", ReferencedEntity = parent, ReferencingEntity = table,
                    CascadeConfiguration = new CascadeConfiguration { Delete = cascade },
                },
                Lookup = new LookupAttributeMetadata { LogicalName = lookup, SchemaName = lookup, DisplayName = Label("Parent") },
            });
        var p1 = await Admin.CreateAsync(new Entity(parent) { ["new_name"] = "p" });
        var c1 = await Admin.CreateAsync(new Entity(child) { ["new_name"] = "c", ["new_parentid"] = new EntityReference(parent, p1) });
        var p2 = await Admin.CreateAsync(new Entity(parent) { ["new_name"] = "p2" });
        var k1 = await Admin.CreateAsync(new Entity(keep) { ["new_name"] = "k", ["new_keepid"] = new EntityReference(parent, p2) });

        await Admin.DeleteAsync(parent, p1);
        var row = await Admin.RetrieveAsync(child, c1, new ColumnSet("new_parentid", "new_name"));
        row.GetAttributeValue<string>("new_name").Should().Be("c");
        row.GetAttributeValue<EntityReference>("new_parentid").Should().BeNull();
        // Not RemoveLink: the lookup is left as it is.
        (await Admin.RetrieveAsync(keep, k1, new ColumnSet("new_keepid"))).GetAttributeValue<EntityReference>("new_keepid").Should().NotBeNull();
    }

    [Fact]
    public async Task Customer_relationships_need_exactly_two_non_null_relationships()
    {
        var table = await NewTableAsync("twoonly");
        foreach (var relationships in new OneToManyRelationshipMetadata?[][] { [], [null, null], [Relationship("new_x_a", table, "account"), null] })
        {
            var request = CreateCustomer(table, "new_customerid", "new");
            request.OneToManyRelationships = relationships!;
            (await BadRequestAsync(() => Admin.ExecuteAsync(request))).Should().Contain("OneToManyRelationships");
        }
        Store.GetEntity(table)!.Attributes.Should().NotContain(a => a.LogicalName == "new_customerid");
    }

    [Fact]
    public async Task A_custom_table_keeps_false_HasNotes_and_HasActivities_through_export_and_import()
    {
        var solution = await NewSolutionAsync("flags");
        var table = await NewTableAsync("flags", solution);
        var zip = await ExportAsync(solution);
        var info = EntityElement(Customizations(zip), table)!.Element("EntityInfo")!.Element("entity")!;
        info.Element("HasRelatedNotes")!.Value.Should().Be("False");
        info.Element("HasRelatedActivities")!.Value.Should().Be("False");

        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        var imported = Store.GetEntity(table)!;
        imported.HasNotes.Should().NotBe(true);
        imported.HasActivities.Should().NotBe(true);
        var again = EntityElement(Customizations(await ExportAsync(solution)), table)!.Element("EntityInfo")!.Element("entity")!;
        again.Element("HasRelatedNotes")!.Value.Should().Be("False");
        again.Element("HasRelatedActivities")!.Value.Should().Be("False");
    }

    // ---- C9: solutions ----------------------------------------------------------------------------------------------

    private async Task<string> NewSolutionAsync(string label)
    {
        var name = $"{label}{_run}";
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = name, ["friendlyname"] = name, ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", SystemSeed.DefaultPublisherId),
        });
        return name;
    }

    private async Task<byte[]> ExportAsync(string solution) =>
        ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = solution, Managed = false })).ExportSolutionFile;

    private static XDocument Customizations(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var stream = archive.GetEntry("customizations.xml")!.Open();
        return XDocument.Load(stream);
    }

    private static XElement? EntityElement(XDocument customizations, string logicalName) =>
        customizations.Root!.Element("Entities")!.Elements("Entity")
            .SingleOrDefault(e => e.Element("Name")!.Value.Equals(logicalName, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Solution_with_a_customer_column_round_trips()
    {
        var solution = await NewSolutionAsync("custsol");
        var table = await NewCustomerTableAsync("sol", solution);
        var ids = Store.GetOneToManyRelationships().Where(r => r.ReferencingEntity == table && r.ReferencingAttribute == "new_customerid")
            .Select(r => r.SchemaName).Order().ToList();
        var attributeId = Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == "new_customerid").MetadataId!.Value;

        var zip = await ExportAsync(solution);
        var customizations = Customizations(zip);
        var column = EntityElement(customizations, table)!.Element("EntityInfo")!.Element("entity")!.Element("attributes")!.Elements("attribute")
            .Single(a => a.Element("Name")!.Value == "new_customerid");
        column.Element("Type")!.Value.Should().Be("customer");
        var exported = customizations.Root!.Element("EntityRelationships")!.Elements("EntityRelationship")
            .Where(r => r.Element("ReferencingEntityName")?.Value == table).ToList();
        exported.Select(r => (string)r.Attribute("Name")!).Order().Should().Equal(ids);
        exported.Select(r => r.Element("ReferencedEntityName")!.Value).Should().BeEquivalentTo("account", "contact");
        exported.Should().OnlyContain(r => r.Element("CascadeDelete")!.Value == "RemoveLink" && r.Element("ReferencingAttributeName")!.Value == "new_customerid");

        // The table goes away; the import brings it back, with the column and both relationships.
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });
        Store.GetEntity(table).Should().BeNull();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });

        var imported = Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == "new_customerid").Should().BeOfType<LookupAttributeMetadata>().Subject;
        imported.AttributeType.Should().Be(AttributeTypeCode.Customer);
        imported.Targets.Should().Equal("account", "contact");
        imported.MetadataId.Should().NotBe(attributeId);
        var back = Store.GetOneToManyRelationships().Where(r => r.ReferencingEntity == table && r.ReferencingAttribute == "new_customerid").ToList();
        back.Select(r => r.SchemaName).Order().Should().Equal(ids);
        back.Should().OnlyContain(r => r.IsCustomRelationship == true && r.CascadeConfiguration.Delete == CascadeType.RemoveLink);
        back.Select(r => r.ReferencingEntityNavigationPropertyName).Should().BeEquivalentTo("new_customerid_account", "new_customerid_contact");
        // It works: both targets, and RemoveLink.
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Imported " + _run });
        var record = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "r", ["new_customerid"] = new EntityReference("account", account) });
        await Admin.DeleteAsync("account", account);
        (await Admin.RetrieveAsync(table, record, new ColumnSet("new_customerid"))).GetAttributeValue<EntityReference>("new_customerid").Should().BeNull();
        // Both are components of the solution again.
        (await ScalarAsync("SELECT COUNT(*) FROM data.solutioncomponent c JOIN data.solution s ON s.solutionid = c.solutionid WHERE s.uniquename = @s AND c.componenttype = @t",
            ("@s", solution), ("@t", ComponentType.EntityRelationship))).Should().Be(2);

        // Importing the same file again changes nothing.
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        Store.GetOneToManyRelationships().Count(r => r.ReferencingEntity == table && r.ReferencingAttribute == "new_customerid").Should().Be(2);
    }

    [Fact]
    public async Task A_solution_with_the_cascades_of_a_dataverse_customer_column_imports()
    {
        var solution = await NewSolutionAsync("custdv");
        var table = await NewCustomerTableAsync("dv", solution);
        var zip = await ExportAsync(solution);
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });

        // The cascades of a parental Customer relationship as Dataverse exports them (contact_customer_accounts on Learn).
        using var input = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var entry in input.Entries)
            {
                using var source = entry.Open();
                using var target = archive.CreateEntry(entry.FullName).Open();
                if (entry.FullName != "customizations.xml") { source.CopyTo(target); continue; }
                var document = XDocument.Load(source);
                foreach (var relationship in document.Root!.Element("EntityRelationships")!.Elements("EntityRelationship")
                             .Where(r => r.Element("ReferencingEntityName")?.Value == table))
                    foreach (var (action, value) in new[]
                             {
                                 ("Assign", "Cascade"), ("Delete", "Cascade"), ("Merge", "Cascade"), ("Reparent", "Cascade"), ("Share", "Cascade"),
                                 ("Unshare", "Cascade"), ("Archive", "NoCascade"), ("RollupView", "NoCascade"),
                             })
                        relationship.SetElementValue("Cascade" + action, value);
                document.Save(target);
            }

        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = output.ToArray(), ImportJobId = Guid.NewGuid() });
        var imported = Store.GetOneToManyRelationships().Where(r => r.ReferencingEntity == table && r.ReferencingAttribute == "new_customerid").ToList();
        imported.Should().HaveCount(2);
        foreach (var c in imported.Select(r => r.CascadeConfiguration))
            (c.Assign, c.Delete, c.Merge, c.Reparent, c.Share, c.Unshare).Should().Be(
                (CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade));
        // Delete Cascade is enforced: the record goes with its account.
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Imported " + _run });
        var record = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "r", ["new_customerid"] = new EntityReference("account", account) });
        await Admin.DeleteAsync("account", account);
        (await ScalarAsync($"SELECT COUNT(*) FROM data.{table} WHERE {table}id = @id", ("@id", record))).Should().Be(0);
        // The export carries them back.
        var exported = Customizations(await ExportAsync(solution)).Root!.Element("EntityRelationships")!.Elements("EntityRelationship")
            .Where(r => r.Element("ReferencingEntityName")?.Value == table).ToList();
        exported.Should().HaveCount(2).And.OnlyContain(r => r.Element("CascadeDelete")!.Value == "Cascade" && r.Element("CascadeMerge")!.Value == "Cascade"
                                                             && r.Element("CascadeShare")!.Value == "Cascade");
    }

    [Fact]
    public async Task Import_refuses_other_polymorphic_lookups_and_a_customer_column_without_both_relationships()
    {
        var solution = await NewSolutionAsync("custbad");
        var table = await NewCustomerTableAsync("bad", solution);
        var zip = await ExportAsync(solution);
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });

        byte[] Rewrite(Action<XDocument> change)
        {
            using var input = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var entry in input.Entries)
                {
                    using var source = entry.Open();
                    using var target = archive.CreateEntry(entry.FullName).Open();
                    if (entry.FullName != "customizations.xml") { source.CopyTo(target); continue; }
                    var document = XDocument.Load(source);
                    change(document);
                    document.Save(target);
                }
            return output.ToArray();
        }

        // One relationship only.
        var oneRelationship = Rewrite(d => d.Root!.Element("EntityRelationships")!.Elements("EntityRelationship")
            .First(r => r.Element("ReferencedEntityName")!.Value == "contact" && r.Element("ReferencingEntityName")!.Value == table).Remove());
        (await BadRequestAsync(() => Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = oneRelationship, ImportJobId = Guid.NewGuid() })))
            .Should().Contain("account").And.Contain("contact");
        // A second relationship of a plain lookup stays refused.
        var plain = Rewrite(d =>
        {
            d.Descendants("attribute").Single(a => a.Element("Name")?.Value == "new_customerid").Element("Type")!.Value = "lookup";
        });
        (await BadRequestAsync(() => Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = plain, ImportJobId = Guid.NewGuid() })))
            .Should().Contain("polymorphic");
        Store.GetEntity(table).Should().BeNull();
    }

    [Fact]
    public async Task Solution_extending_account_exports_only_its_components()
    {
        var solution = await NewSolutionAsync("extacc");
        var column = Name("accextra");
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = "account",
            Attribute = new StringAttributeMetadata { LogicalName = column, SchemaName = column, MaxLength = 50, DisplayName = Label(column) },
            SolutionUniqueName = solution,
        });
        // Behaviour 1: the table is a component without its subcomponents.
        await Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentType = ComponentType.Entity, ComponentId = Store.GetEntity("account")!.MetadataId!.Value, SolutionUniqueName = solution,
            DoNotIncludeSubcomponents = true, AddRequiredComponents = false,
        });

        var zip = await ExportAsync(solution);
        var customizations = Customizations(zip);
        var account = EntityElement(customizations, "account")!;
        var names = account.Element("EntityInfo")!.Element("entity")!.Element("attributes")!.Elements("attribute").Select(a => a.Element("Name")!.Value).ToList();
        names.Should().BeEquivalentTo("accountid", "name", column);
        customizations.Root!.Element("Entities")!.Elements("Entity").Count().Should().Be(1);
        // The table is the system table's own: the solution holds only the root component of type 1 with behavior 1.
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
        {
            var root = XDocument.Load(archive.GetEntry("solution.xml")!.Open()).Descendants("RootComponent").Single(r => (string?)r.Attribute("type") == "1");
            ((string?)root.Attribute("schemaName")).Should().Be("account");
            ((string?)root.Attribute("behavior")).Should().Be("1");
        }

        // The extension goes away; the import brings the column back on account and leaves the other columns alone.
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = "account", LogicalName = column });
        Store.GetEntity("account")!.Attributes.Should().NotContain(a => a.LogicalName == column);
        var columnsBefore = Store.GetEntity("account")!.Attributes.Select(a => a.LogicalName).ToList();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });
        Store.GetEntity("account")!.Attributes.Select(a => a.LogicalName).Should().BeEquivalentTo([.. columnsBefore, column]);
        Store.GetEntity("account")!.ObjectTypeCode.Should().Be(1);
    }
}

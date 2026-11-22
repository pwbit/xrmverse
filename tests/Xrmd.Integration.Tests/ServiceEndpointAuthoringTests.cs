using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using Xrmd.Metadata;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.2, task 2: the endpoint rules on Create and Update of <c>serviceendpoint</c>, the write-only <c>authvalue</c> kept in
/// <c>meta.serviceendpoint_secret</c> (never returned by any read path), the rules of a step on a webhook, the Delete of an
/// endpoint with its steps, images and secret, and the solution refusals.
/// </summary>
[Collection("xrmd")]
public sealed class ServiceEndpointAuthoringTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Secret = "k3y-0f-the-h00k";
    private const string Refused = "Webhooks and their steps cannot be added to a solution yet.";

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    /// <summary>The solution of the tests that need a real one (created by <see cref="CreateSolutionAsync"/>).</summary>
    private readonly string _solution = "xt_hooks_" + Guid.NewGuid().ToString("N")[..8];

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await ExecAsync("DELETE FROM data.serviceendpoint; DELETE FROM meta.serviceendpoint_secret;");
        if (await ScalarAsync("SELECT publisherid FROM data.solution WHERE uniquename = @s", ("@s", _solution)) is not Guid publisher) return;
        await TestSql.ExecAsync(fx.ConnectionString,
            "DELETE FROM data.solutioncomponent WHERE solutionid IN (SELECT solutionid FROM data.solution WHERE uniquename = @s)", ("@s", _solution));
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.solution WHERE uniquename = @s", ("@s", _solution));
        await TestSql.ExecAsync(fx.ConnectionString, "DELETE FROM data.publisher WHERE publisherid = @p", ("@p", publisher));
    }

    private async Task CreateSolutionAsync()
    {
        var publisher = await Admin.CreateAsync(new Entity("publisher")
        {
            ["uniquename"] = _solution + "_pub", ["friendlyname"] = "Hooks", ["customizationprefix"] = "whk",
            ["customizationoptionvalueprefix"] = 17654,
        });
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = _solution, ["friendlyname"] = "Hooks", ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisher),
        });
    }

    private Task<Guid> CreateWebhookAsync(string name = "orders hook", string authValue = Secret) =>
        Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = name, ["contract"] = new OptionSetValue(ServiceEndpointSchema.WebhookContract),
            ["authtype"] = new OptionSetValue(ServiceEndpointSchema.WebhookKey), ["url"] = "https://hooks.example.invalid/receive",
            [ServiceEndpointSchema.AuthValue] = authValue,
        });

    private static Entity WebhookStep(Guid endpoint, string name = "webhook step") => new("sdkmessageprocessingstep")
    {
        ["name"] = name,
        ["sdkmessageid"] = new EntityReference("sdkmessage", SdkMessageCatalog.MessageId("Create")),
        ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", SdkMessageCatalog.FilterId("Create", "account")),
        ["eventhandler"] = new EntityReference(ServiceEndpointSchema.Table, endpoint),
        ["stage"] = new OptionSetValue(40),
        ["mode"] = new OptionSetValue(1),
        ["rank"] = 1,
    };

    private Task<object?> StoredSecretAsync(Guid endpoint) =>
        ScalarAsync("SELECT authvalue FROM meta.serviceendpoint_secret WHERE serviceendpointid = @id", ("@id", endpoint));

    [Fact]
    public async Task Create_webhook_stores_secret()
    {
        var id = await CreateWebhookAsync();

        (await StoredSecretAsync(id)).Should().Be(Secret);
        (await Admin.RetrieveAsync(ServiceEndpointSchema.Table, id, new ColumnSet(ServiceEndpointSchema.IsAuthValueSet)))
            .GetAttributeValue<bool>(ServiceEndpointSchema.IsAuthValueSet).Should().BeTrue();
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'data' AND TABLE_NAME = 'serviceendpoint' AND COLUMN_NAME = 'authvalue'")).Should().Be(0, "the value lives only in the secret table");

        // The Web API: an Http Header endpoint with its settings XML.
        const string settings = "<settings><setting name=\"X-Hook-Key\" value=\"h3ader-value\" /></settings>";
        var (status, body, headers) = await SendAsync(HttpMethod.Post, "serviceendpoints", new JsonObject
        {
            ["name"] = "header hook", ["contract"] = 8, ["authtype"] = 5, ["url"] = "https://hooks.example.invalid/headers", ["authvalue"] = settings,
        });
        status.Should().Be(HttpStatusCode.NoContent, body);
        var url = headers.GetValues("OData-EntityId").Single();
        var headerHook = Guid.Parse(url[(url.LastIndexOf('(') + 1)..^1]);
        (await StoredSecretAsync(headerHook)).Should().Be(settings);

        // A refused endpoint stores nothing.
        var message = await BadRequestAsync(() => Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = "queue", ["contract"] = new OptionSetValue(2), ["authtype"] = new OptionSetValue(4),
            ["url"] = "https://hooks.example.invalid/q", [ServiceEndpointSchema.AuthValue] = "never-stored",
        }));
        message.Should().Be("Service endpoint 'queue': contract 2 (Queue) is not supported; xrmverse delivers webhooks (contract 8) only.");
        (await ScalarAsync("SELECT COUNT(*) FROM meta.serviceendpoint_secret WHERE authvalue = N'never-stored'")).Should().Be(0);
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = "no key", ["contract"] = new OptionSetValue(8), ["authtype"] = new OptionSetValue(4), ["url"] = "https://hooks.example.invalid/k",
        }))).Should().Be("Webhook 'no key' needs an authvalue.");
        (await BadRequestAsync(() => Admin.CreateAsync(new Entity(ServiceEndpointSchema.Table)
        {
            ["name"] = "local", ["contract"] = new OptionSetValue(8), ["authtype"] = new OptionSetValue(4), ["url"] = "http://localhost:5099/hook",
            [ServiceEndpointSchema.AuthValue] = "k",
        }))).Should().Be("Webhook 'local': url must use port 80 or 443.");
    }

    [Fact]
    public async Task Authvalue_is_never_returned()
    {
        var id = await CreateWebhookAsync();

        var retrieved = await Admin.RetrieveAsync(ServiceEndpointSchema.Table, id, new ColumnSet(true));
        retrieved.Attributes.Keys.Should().NotContain(ServiceEndpointSchema.AuthValue).And.Contain(ServiceEndpointSchema.IsAuthValueSet);
        retrieved.Attributes.Values.OfType<string>().Should().NotContain(v => v.Contains(Secret));

        var multiple = await Admin.RetrieveMultipleAsync(new QueryExpression(ServiceEndpointSchema.Table) { ColumnSet = new ColumnSet(true) });
        multiple.Entities.Should().ContainSingle().Which.Attributes.Keys.Should().NotContain(ServiceEndpointSchema.AuthValue);

        var fetched = await Admin.RetrieveMultipleAsync(new FetchExpression("<fetch><entity name='serviceendpoint'><all-attributes /></entity></fetch>"));
        fetched.Entities.Should().ContainSingle().Which.Attributes.Keys.Should().NotContain(ServiceEndpointSchema.AuthValue);

        var (status, body, _) = await SendAsync(HttpMethod.Get, $"serviceendpoints({id})", null);
        status.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain("authvalue\"").And.NotContain(Secret).And.Contain("\"isauthvalueset\":true");

        // A change-tracking read with all columns (tracking, once on, stays on: harmless for the other tests of the table).
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = ServiceEndpointSchema.Table, ChangeTrackingEnabled = true } });
        var changes = (RetrieveEntityChangesResponse)await Admin.ExecuteAsync(new RetrieveEntityChangesRequest
        {
            EntityName = ServiceEndpointSchema.Table, Columns = new ColumnSet(true), PageInfo = new PagingInfo { Count = 50, PageNumber = 1 },
        });
        var changed = changes.EntityChanges.Changes.OfType<NewOrUpdatedItem>().Select(c => c.NewOrUpdatedEntity).ToList();
        changed.Should().ContainSingle(e => e.Id == id).Which.Attributes.Keys.Should().NotContain(ServiceEndpointSchema.AuthValue);

        // Asked for by name, or filtered on: the usual 400 of an unreadable column.
        const string Unreadable = "'serviceendpoint' entity doesn't contain attribute with Name = 'authvalue'.";
        (await BadRequestAsync(() => Admin.RetrieveAsync(ServiceEndpointSchema.Table, id, new ColumnSet(ServiceEndpointSchema.AuthValue))))
            .Should().Be(Unreadable);
        var filtered = new QueryExpression(ServiceEndpointSchema.Table) { ColumnSet = new ColumnSet("name") };
        filtered.Criteria.AddCondition(ServiceEndpointSchema.AuthValue, ConditionOperator.Equal, Secret);
        (await BadRequestAsync(() => Admin.RetrieveMultipleAsync(filtered))).Should().Be(Unreadable);
        var (filterStatus, filterBody, _) = await SendAsync(HttpMethod.Get, $"serviceendpoints?$filter=authvalue eq '{Secret}'", null);
        filterStatus.Should().Be(HttpStatusCode.BadRequest, filterBody);
        filterBody.Should().NotContain(Secret);
    }

    [Fact]
    public async Task Update_keeps_secret_when_absent()
    {
        var id = await CreateWebhookAsync();

        await Admin.UpdateAsync(new Entity(ServiceEndpointSchema.Table, id) { ["description"] = "no new key" });
        (await StoredSecretAsync(id)).Should().Be(Secret);

        await Admin.UpdateAsync(new Entity(ServiceEndpointSchema.Table, id) { [ServiceEndpointSchema.AuthValue] = "s3cond-key" });
        (await StoredSecretAsync(id)).Should().Be("s3cond-key");
        (await Admin.RetrieveAsync(ServiceEndpointSchema.Table, id, new ColumnSet(ServiceEndpointSchema.IsAuthValueSet)))
            .GetAttributeValue<bool>(ServiceEndpointSchema.IsAuthValueSet).Should().BeTrue();

        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(ServiceEndpointSchema.Table, id) { [ServiceEndpointSchema.AuthValue] = "" })))
            .Should().Be("Webhook 'orders hook' needs an authvalue.");
        // The rules hold for the row as it will be: the stored key is no settings XML for Http Header.
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(ServiceEndpointSchema.Table, id) { ["authtype"] = new OptionSetValue(5) })))
            .Should().Be("Webhook 'orders hook': authvalue is not a settings XML (<settings><setting name=\"...\" value=\"...\" /></settings>).");
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(ServiceEndpointSchema.Table, id) { ["path"] = "queue" })))
            .Should().Be("Webhook 'orders hook': path applies to Azure Service Bus endpoints, which are not supported.");
        (await StoredSecretAsync(id)).Should().Be("s3cond-key");
    }

    [Fact]
    public async Task Step_on_webhook_registered()
    {
        var endpoint = await CreateWebhookAsync();
        var step = await Admin.CreateAsync(WebhookStep(endpoint));

        (await Admin.RetrieveAsync("sdkmessageprocessingstep", step, new ColumnSet("eventhandler", "plugintypeid")))
            .Should().Match<Entity>(e => e.GetAttributeValue<EntityReference>("eventhandler").LogicalName == ServiceEndpointSchema.Table
                                        && e.GetAttributeValue<EntityReference>("eventhandler").Id == endpoint
                                        && e.GetAttributeValue<EntityReference>("plugintypeid") == null);
        (await ScalarAsync("SELECT eventhandlertype FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", step)))
            .Should().Be(ServiceEndpointSchema.ObjectTypeCode);

        // The plug-in step rules still apply.
        var asyncOnPre = WebhookStep(endpoint, "async on 20");
        asyncOnPre["stage"] = new OptionSetValue(20);
        (await BadRequestAsync(() => Admin.CreateAsync(asyncOnPre))).Should().Be("Asynchronous steps must be registered on stage 40.");
    }

    [Fact]
    public async Task Step_with_both_handlers_refused()
    {
        var endpoint = await CreateWebhookAsync();
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        var both = WebhookStep(endpoint, "both handlers");
        both["plugintypeid"] = new EntityReference("plugintype", type);

        (await BadRequestAsync(() => Admin.CreateAsync(both))).Should().Be("Step 'both handlers' names both a plug-in type and a service endpoint.");

        // An Update moving a plug-in step onto the endpoint while it keeps its plug-in type is refused the same way.
        var pluginStep = await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "account", 40, mode: 1);
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("sdkmessageprocessingstep", pluginStep)
        {
            ["eventhandler"] = new EntityReference(ServiceEndpointSchema.Table, endpoint),
        }))).Should().Be("Step 'Create:account:40:1' names both a plug-in type and a service endpoint.");
    }

    [Theory]
    [InlineData("serviceendpoint_sdkmessageprocessingstep", true)]
    [InlineData("serviceendpoint_sdkmessageprocessingstep", false)]
    [InlineData("plugintype_sdkmessageprocessingstep", true)]
    [InlineData("plugintype_sdkmessageprocessingstep", false)]
    public async Task Associate_over_eventhandler_refused(string relationship, bool associate)
    {
        var endpoint = await CreateWebhookAsync();
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        var pluginStep = await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "account", 40, mode: 1);
        var parent = relationship.StartsWith("serviceendpoint", StringComparison.Ordinal)
            ? new EntityReference(ServiceEndpointSchema.Table, endpoint) : new EntityReference("plugintype", type);
        var related = new EntityReferenceCollection { new EntityReference("sdkmessageprocessingstep", pluginStep) };
        OrganizationRequest request = associate
            ? new AssociateRequest { Target = parent, Relationship = new Relationship(relationship), RelatedEntities = related }
            : new DisassociateRequest { Target = parent, Relationship = new Relationship(relationship), RelatedEntities = related };

        (await BadRequestAsync(() => Admin.ExecuteAsync(request))).Should().Be(
            $"Relationship '{relationship}' cannot be used with Associate or Disassociate: update the step's eventhandler instead.");
        (await ScalarAsync("SELECT eventhandlertype FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", pluginStep)))
            .Should().Be(4602, "the plug-in step stays a plug-in step");
    }

    [Fact]
    public async Task Step_with_configuration_refused()
    {
        var endpoint = await CreateWebhookAsync();
        var configured = WebhookStep(endpoint, "configured");
        configured["configuration"] = "key=value";
        (await BadRequestAsync(() => Admin.CreateAsync(configured))).Should().Be("Step 'configured' on a webhook takes no configuration.");

        var step = await Admin.CreateAsync(WebhookStep(endpoint, "later configured"));
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity("sdkmessageprocessingstep", step) { ["configuration"] = "x" })))
            .Should().Be("Step 'later configured' on a webhook takes no configuration.");
        await Admin.UpdateAsync(new Entity("sdkmessageprocessingstep", step) { ["configuration"] = "" });
    }

    [Fact]
    public async Task Delete_endpoint_deletes_its_steps()
    {
        var endpoint = await CreateWebhookAsync();
        var step = await Admin.CreateAsync(WebhookStep(endpoint));
        var image = await PluginTestHelper.RegisterImageAsync(Admin, step, 1, "post");

        await Admin.DeleteAsync(ServiceEndpointSchema.Table, endpoint);

        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", step))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstepimage WHERE sdkmessageprocessingstepimageid = @id", ("@id", image)))
            .Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.serviceendpoint_secret WHERE serviceendpointid = @id", ("@id", endpoint))).Should().Be(0);
    }

    [Fact]
    public async Task Endpoint_with_solution_header_refused()
    {
        var (status, body, _) = await SendAsync(HttpMethod.Post, "serviceendpoints", new JsonObject
        {
            ["name"] = "in a solution", ["contract"] = 8, ["authtype"] = 4, ["url"] = "https://hooks.example.invalid/s", ["authvalue"] = "k",
        }, solution: "xt_Hooks");
        status.Should().Be(HttpStatusCode.BadRequest, body);
        var error = JsonNode.Parse(body)!["error"]!;
        (error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>()).Should().Be(("0x80040203", Refused));
        (await ScalarAsync("SELECT COUNT(*) FROM data.serviceendpoint")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.serviceendpoint_secret")).Should().Be(0);
    }

    [Fact]
    public async Task Webhook_step_with_solution_header_refused()
    {
        var endpoint = await CreateWebhookAsync();
        var create = new CreateRequest { Target = WebhookStep(endpoint), ["SolutionUniqueName"] = "xt_Hooks" };
        (await BadRequestAsync(() => Admin.ExecuteAsync(create))).Should().Be(Refused);

        var (status, body, _) = await SendAsync(HttpMethod.Post, "sdkmessageprocessingsteps", new JsonObject
        {
            ["name"] = "web step", ["stage"] = 40, ["mode"] = 1, ["rank"] = 1,
            ["sdkmessageid@odata.bind"] = $"/sdkmessages({SdkMessageCatalog.MessageId("Create")})",
            ["sdkmessagefilterid@odata.bind"] = $"/sdkmessagefilters({SdkMessageCatalog.FilterId("Create", "account")})",
            ["eventhandler_serviceendpoint@odata.bind"] = $"/serviceendpoints({endpoint})",
        }, solution: "xt_Hooks");
        status.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["error"]!["message"]!.GetValue<string>().Should().Be(Refused);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstep WHERE eventhandler = @id", ("@id", endpoint))).Should().Be(0);
    }

    [Fact]
    public async Task AddSolutionComponent_of_endpoint_refused()
    {
        var endpoint = await CreateWebhookAsync();
        var step = await Admin.CreateAsync(WebhookStep(endpoint));

        (await BadRequestAsync(() => Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentType = 95, ComponentId = endpoint, SolutionUniqueName = "xt_Hooks",
        }))).Should().Be(Refused);
        (await BadRequestAsync(() => Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentType = 92, ComponentId = step, SolutionUniqueName = "xt_Hooks",
        }))).Should().Be(Refused);
    }

    [Fact]
    public async Task Authvalue_longer_than_2048_refused()
    {
        // The generic MaxLength 400 comes first (before the pipeline); it never echoes the value.
        const string Generic = "Attribute 'authvalue' of entity 'serviceendpoint' is 2049 characters long; MaxLength is 2048.";
        var longValue = new string('k', 2049);
        var message = await BadRequestAsync(() => CreateWebhookAsync("long key", longValue));
        message.Should().Be(Generic).And.NotContain(longValue);
        (await ScalarAsync("SELECT COUNT(*) FROM meta.serviceendpoint_secret")).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.serviceendpoint")).Should().Be(0);

        var id = await CreateWebhookAsync();
        (await BadRequestAsync(() => Admin.UpdateAsync(new Entity(ServiceEndpointSchema.Table, id) { [ServiceEndpointSchema.AuthValue] = longValue })))
            .Should().Be(Generic).And.NotContain(longValue);
        (await StoredSecretAsync(id)).Should().Be(Secret);
    }

    [Fact]
    public async Task Step_on_serviceendpoint_table_refused()
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        (await BadRequestAsync(() => PluginTestHelper.RegisterStepAsync(Admin, type, "Create", ServiceEndpointSchema.Table, 20)))
            .Should().Be("Steps cannot be registered on the serviceendpoint table.");
        var endpoint = await CreateWebhookAsync();
        var onEndpoint = WebhookStep(endpoint, "on the table");
        onEndpoint["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", SdkMessageCatalog.FilterId("Create", ServiceEndpointSchema.Table));
        (await BadRequestAsync(() => Admin.CreateAsync(onEndpoint))).Should().Be("Steps cannot be registered on the serviceendpoint table.");
    }

    [Fact]
    public async Task Image_of_webhook_step_refused_in_solution()
    {
        var endpoint = await CreateWebhookAsync();
        var step = await Admin.CreateAsync(WebhookStep(endpoint));
        var image = new Entity("sdkmessageprocessingstepimage")
        {
            ["name"] = "post", ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", step),
            ["imagetype"] = new OptionSetValue(1), ["entityalias"] = "post", ["messagepropertyname"] = "Target",
        };
        (await BadRequestAsync(() => Admin.ExecuteAsync(new CreateRequest { Target = image, ["SolutionUniqueName"] = "xt_Hooks" })))
            .Should().Be(Refused);
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessageprocessingstepimage")).Should().Be(0);

        var imageId = await PluginTestHelper.RegisterImageAsync(Admin, step, 1, "post");
        (await BadRequestAsync(() => Admin.ExecuteAsync(new AddSolutionComponentRequest
        {
            ComponentType = 93, ComponentId = imageId, SolutionUniqueName = "xt_Hooks",
        }))).Should().Be(Refused);
    }

    [Fact]
    public async Task Step_in_solution_not_moved_onto_endpoint()
    {
        await CreateSolutionAsync();
        var endpoint = await CreateWebhookAsync();
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        var inSolution = await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "account", 40, mode: 1);
        await Admin.ExecuteAsync(new AddSolutionComponentRequest { ComponentType = 92, ComponentId = inSolution, SolutionUniqueName = _solution });
        var outside = await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "account", 40, mode: 1, rank: 2);

        Entity Move(Guid id) => new("sdkmessageprocessingstep", id)
        {
            ["eventhandler"] = new EntityReference(ServiceEndpointSchema.Table, endpoint), ["plugintypeid"] = null,
        };
        (await BadRequestAsync(() => Admin.UpdateAsync(Move(inSolution)))).Should().Be(Refused);
        await Admin.UpdateAsync(Move(outside));
        (await ScalarAsync("SELECT eventhandlertype FROM data.sdkmessageprocessingstep WHERE sdkmessageprocessingstepid = @id", ("@id", outside)))
            .Should().Be(ServiceEndpointSchema.ObjectTypeCode);
    }

    [Fact]
    public async Task Export_meeting_a_webhook_step_refused()
    {
        await CreateSolutionAsync();
        var endpoint = await CreateWebhookAsync();
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.SetAttributePlugin");
        var step = await PluginTestHelper.RegisterStepAsync(Admin, type, "Create", "account", 40, mode: 1);
        await Admin.ExecuteAsync(new AddSolutionComponentRequest { ComponentType = 92, ComponentId = step, SolutionUniqueName = _solution });
        // Written outside the rules, as a damaged or hand-edited database could hold it.
        await ExecAsync($"""
            UPDATE data.sdkmessageprocessingstep SET eventhandler = '{endpoint}', eventhandlertype = 4618, plugintypeid = NULL
            WHERE sdkmessageprocessingstepid = '{step}'
            """);

        (await BadRequestAsync(() => Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = _solution, Managed = false })))
            .Should().Be(Refused);
    }

    private async Task<(HttpStatusCode Status, string Body, HttpResponseHeaders Headers)> SendAsync(HttpMethod method, string path, JsonObject? body,
        string? solution = null)
    {
        var request = new HttpRequestMessage(method, Api + path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (solution is not null) request.Headers.Add("MSCRM.SolutionUniqueName", solution);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers);
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.Edm;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Workflows;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.WorkflowTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Issue #343: an activated XAML action (category 3, plan 2.14 W14) is a Web API operation as a custom API is: a global one is
/// <c>POST {message}</c>, a bound one <c>POST {set}({id})/Microsoft.Dynamics.CRM.{message}</c> with the record of the URL as
/// <c>Target</c>; its request runs through the organization service as over SOAP, and its outputs are the JSON body. A Draft
/// (or deactivated) action is the 404 of an unknown segment; <c>$metadata</c> declares the activated ones.
/// </summary>
[Collection("xrmd")]
public sealed class ActionWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = TestEntities.Order;
    private const string Recalculate = "Recalculate-9DF696A1-E6DE-F111-84E8-002248872B66";
    private const string Ns = "Microsoft.Dynamics.CRM.";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private string OrderSet => fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(Order)!.EntitySetName!;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureOrderAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ActionWorkflowTests.ClearActionsAsync(fx);
    }

    public Task DisposeAsync() => ActionWorkflowTests.ClearActionsAsync(fx);

    private string Name(string what) => $"{what} {_run}";

    private static WorkflowRow Action(string name, string uniqueName, string xaml, string primaryEntity) => new()
    {
        Id = Guid.NewGuid(), Name = name, Category = 3, Mode = 0, Type = 1, Scope = 4, RunAs = 1, PrimaryEntity = primaryEntity,
        UniqueName = uniqueName, Xaml = xaml,
    };

    /// <summary>The export's Recalculate (bound to xrmv_order: Increment Decimal in, Total Decimal out), with a prefixed unique name.</summary>
    private WorkflowRow RecalculateAction() => Action(Name("Recalculate"), $"new_Recalc{_run}", Real(Recalculate), Order);

    /// <summary>The provisional action-echo (global: InText required, Customer optional, OutText out) without its own action call.</summary>
    private WorkflowRow EchoAction() =>
        Action(Name("Echo"), $"new_Echo{_run}", Without(WorkflowFixtures.Xaml("action-echo"), "ActionStep2"), S.NoPrimaryEntity);

    private Task<WorkflowRow> ActivateAsync(WorkflowRow row) => WorkflowTestHelper.ActivateAsync(fx, Admin, row);

    private async Task<Guid> OrderAsync(decimal amount) =>
        await Admin.CreateAsync(new Entity(Order) { ["xrmv_name"] = Name("Order"), ["xrmv_amount"] = amount });

    private async Task<decimal?> AmountAsync(Guid order) =>
        (await Admin.RetrieveAsync(Order, order, new ColumnSet("xrmv_amount"))).GetAttributeValue<decimal?>("xrmv_amount");

    private async Task<(HttpStatusCode Status, JsonNode? Body, string Text)> SendAsync(HttpMethod method, string path, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/data/v9.2/" + path);
        if (method == HttpMethod.Post)
            request.Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 || text[0] == '<' ? null : JsonNode.Parse(text), text);
    }

    private async Task<string> ErrorAsync(string path, HttpStatusCode expected, string code, JsonNode? body = null)
    {
        var (status, json, text) = await SendAsync(HttpMethod.Post, path, body);
        status.Should().Be(expected, $"POST {path}: {text}");
        json!["error"]!["code"]!.GetValue<string>().Should().Be(code, text);
        return json["error"]!["message"]!.GetValue<string>();
    }

    private async Task<string> MetadataAsync()
    {
        var (status, _, text) = await SendAsync(HttpMethod.Get, "$metadata");
        status.Should().Be(HttpStatusCode.OK, text);
        return text;
    }

    private static IEdmModel Parse(string csdl)
    {
        using var reader = System.Xml.XmlReader.Create(new StringReader(csdl));
        Microsoft.OData.Edm.Csdl.CsdlReader.TryParse(reader, out var model, out var errors)
            .Should().BeTrue(string.Join("; ", errors?.Select(e => e.ErrorMessage) ?? []));
        return model;
    }

    [Fact]
    public async Task Global_action_runs_and_returns_its_outputs()
    {
        var echo = await ActivateAsync(EchoAction());
        var message = echo.UniqueName!;

        foreach (var path in new[] { message, Ns + message })
        {
            var (status, json, text) = await SendAsync(HttpMethod.Post, path, new JsonObject { ["InText"] = Name("Hello") });
            status.Should().Be(HttpStatusCode.OK, text);
            json!["@odata.context"]!.GetValue<string>().Should().EndWith($"$metadata#{Ns}{message}Response");
            json["OutText"]!.GetValue<string>().Should().Be(Name("Hello"));
        }
        (await Admin.RetrieveMultipleAsync(new QueryExpression("task")
        {
            Criteria = { Conditions = { new ConditionExpression("subject", ConditionOperator.Equal, Name("Hello")) } },
        })).Entities.Should().HaveCount(2, "the action's create step ran at each call");
    }

    [Fact]
    public async Task Bound_action_runs_on_the_record_of_the_url()
    {
        var recalculate = await ActivateAsync(RecalculateAction());
        var order = await OrderAsync(10m);

        var (status, json, text) = await SendAsync(HttpMethod.Post, $"{OrderSet}({order})/{Ns}{recalculate.UniqueName}",
            new JsonObject { ["Increment"] = 2.5m });

        status.Should().Be(HttpStatusCode.OK, text);
        json!["@odata.context"]!.GetValue<string>().Should().EndWith($"$metadata#{Ns}{recalculate.UniqueName}Response");
        json["Total"]!.GetValue<decimal>().Should().Be(12.5m);
        (await AmountAsync(order)).Should().Be(12.5m);

        // A bound action is not global, nor bound to another table, nor named without its namespace on the record.
        await ErrorAsync(recalculate.UniqueName!, HttpStatusCode.NotFound, "0x80060888", new JsonObject { ["Increment"] = 1m });
        await ErrorAsync($"accounts({Guid.NewGuid()})/{Ns}{recalculate.UniqueName}", HttpStatusCode.NotFound, "0x80060888",
            new JsonObject { ["Increment"] = 1m });
        (await AmountAsync(order)).Should().Be(12.5m);
    }

    [Fact]
    public async Task Missing_required_input_is_400_0x80040203()
    {
        var recalculate = await ActivateAsync(RecalculateAction());
        var echo = await ActivateAsync(EchoAction());
        var order = await OrderAsync(10m);

        (await ErrorAsync($"{OrderSet}({order})/{Ns}{recalculate.UniqueName}", HttpStatusCode.BadRequest, "0x80040203", new JsonObject()))
            .Should().Contain("'Increment'");
        (await ErrorAsync(echo.UniqueName!, HttpStatusCode.BadRequest, "0x80040203")).Should().Contain("'InText'");
        (await AmountAsync(order)).Should().Be(10m, "nothing of the action ran");
    }

    [Fact]
    public async Task Draft_or_deactivated_action_is_404()
    {
        var echo = EchoAction();
        await SaveAsync(fx, echo);
        await SetStateAsync(Admin, echo.Id, S.ActivatedState, S.ActivatedStatus);
        await DeactivateAsync(Admin, echo.Id);
        var recalculate = await ActivateAsync(RecalculateAction());
        await DeactivateAsync(Admin, recalculate.Id);
        var order = await OrderAsync(10m);

        (await ErrorAsync(echo.UniqueName!, HttpStatusCode.NotFound, "0x80060888", new JsonObject { ["InText"] = "x" }))
            .Should().Contain(echo.UniqueName);
        await ErrorAsync($"{OrderSet}({order})/{Ns}{recalculate.UniqueName}", HttpStatusCode.NotFound, "0x80060888",
            new JsonObject { ["Increment"] = 1m });
        (await AmountAsync(order)).Should().Be(10m);

        // Activated again, it answers again.
        await SetStateAsync(Admin, echo.Id, S.ActivatedState, S.ActivatedStatus);
        (await SendAsync(HttpMethod.Post, echo.UniqueName!, new JsonObject { ["InText"] = "x" })).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Metadata_declares_the_activated_actions()
    {
        var recalculate = await ActivateAsync(RecalculateAction());
        var echo = await ActivateAsync(EchoAction());

        var csdl = await MetadataAsync();
        csdl.Should().Contain($"<Action Name=\"{recalculate.UniqueName}\" IsBound=\"true\"")
            .And.Contain($"<ActionImport Name=\"{echo.UniqueName}\"");
        var model = Parse(csdl);
        var bound = model.FindDeclaredOperations(Ns + recalculate.UniqueName).Should().ContainSingle().Subject;
        bound.IsBound.Should().BeTrue();
        bound.Parameters.Select(p => (p.Name, p.Type.FullName())).Should().Equal(("entity", Ns + Order), ("Increment", "Edm.Decimal"));
        ((IEdmComplexType)bound.GetReturn().Type.Definition).DeclaredProperties.Select(p => p.Name).Should().Equal("Total");
        var global = model.FindDeclaredOperations(Ns + echo.UniqueName).Should().ContainSingle().Subject;
        global.IsBound.Should().BeFalse();
        global.Parameters.Select(p => (p.Name, p.Type.FullName(), p.Type.IsNullable))
            .Should().Equal(("InText", "Edm.String", false), ("Customer", "Microsoft.Dynamics.CRM.account", true));

        // A deactivated action leaves $metadata at once.
        await DeactivateAsync(Admin, recalculate.Id);
        (await MetadataAsync()).Should().NotContain($"Name=\"{recalculate.UniqueName}\"").And.Contain($"Name=\"{echo.UniqueName}\"");
    }
}

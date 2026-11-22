using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Custom APIs run through the pipeline (plan 15, task 4): the request is checked against the definition before anything
/// runs (C6), the execute privilege is demanded, the plug-in type runs as the main operation at stage 30 between the steps
/// of the API's message, and the response is completed to the declared properties (C7). Through the SDK and SOAP.
/// </summary>
[Collection("solution-import")]
public sealed class CustomApiExecutionTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Solution = "XtCustomApiRuns";
    private const string Echo = "Xrmd.TestPlugins.CustomApiEchoPlugin";
    private const string WrongType = "Xrmd.TestPlugins.CustomApiWrongTypePlugin";
    private const string Soap = "XRMServices/2011/Organization.svc/web";

    /// <summary>One request parameter of each of the 13 types, by name and type.</summary>
    private static readonly (string Name, int Type)[] AllTypes =
    [
        ("Bool", 0), ("Date", 1), ("Dec", 2), ("Ent", 3), ("Ents", 4), ("Ref", 5), ("Flt", 6), ("Int", 7), ("Cash", 8), ("Pick", 9),
        ("Text", 10), ("Texts", 11), ("Id", 12),
    ];

    /// <summary>The echo API: every type in (only <c>Text</c> required), the same plus <c>Out</c> back, and <c>Context</c>.</summary>
    private static readonly TestApi EchoApi = new("xt_Echo", AllowedStepType: 2, PluginTypeName: Echo,
        Requests: AllTypes.Select(t => (t.Name, t.Type, t.Name != "Text", (string?)null)).ToArray(),
        Responses: AllTypes.Select(t => (t.Name + "Out", t.Type)).Append(("Context", 10)).ToArray());

    private static readonly TestApi BoundApi = new("xt_Bound", BindingType: 1, BoundEntity: TestEntities.Pipe, PluginTypeName: Echo,
        Requests: [("Text", 10, false, null)], Responses: [("TextOut", 10), ("Context", 10)]);

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    // ---- helpers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Defines <paramref name="apis"/> through the SDK (plan 2.7a, Task 6: as the solution <see cref="Solution"/> would import them;
    /// the class runs on both providers, solution import is 2.7b on PostgreSQL).
    /// </summary>
    private Task ImportAsync(params TestApi[] apis) => CustomApiTestSolution.CreateAsync(Admin, apis);

    private async Task<Guid> AdminUserIdAsync() => (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;

    private static async Task<XrmdFaultException> InvalidAsync(Func<Task> act, params string[] contains)
    {
        var fault = await FaultAsync(act);
        fault.HttpStatus.Should().Be(400, fault.ToString());
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument, fault.ToString());
        foreach (var text in contains) fault.Message.Should().Contain(text);
        return fault;
    }

    private async Task<int> PipesNamedAsync(string name)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT COUNT(*) FROM data.{TestEntities.Pipe} WHERE pb_name = @n");
        cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private sealed class ListLogger : ILogger<PipelineExecutor>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    // ---- the call ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sdk_calls_a_global_action_and_gets_its_outputs()
    {
        await ImportAsync(EchoApi);
        var values = new Dictionary<string, object>
        {
            ["Bool"] = true,
            ["Date"] = new DateTime(2026, 11, 10, 8, 30, 0, DateTimeKind.Utc),
            ["Dec"] = 12.345m,
            ["Ent"] = new Entity("contact", Guid.NewGuid()) { ["firstname"] = "Ada" },
            ["Ents"] = new EntityCollection([new Entity("account") { ["name"] = "One" }, new Entity("account") { ["name"] = "Two" }]),
            ["Ref"] = new EntityReference("systemuser", Guid.NewGuid()),
            ["Flt"] = 2.5d,
            ["Int"] = 42,
            ["Cash"] = new Money(9.99m),
            ["Pick"] = new OptionSetValue(3),
            ["Text"] = "abc",
            ["Texts"] = new[] { "a", "b" },
            ["Id"] = Guid.NewGuid(),
        };
        var request = new OrganizationRequest("xt_Echo");
        foreach (var (name, value) in values) request[name] = value;

        var response = await Admin.ExecuteAsync(request);

        response.ResponseName.Should().Be("xt_Echo");
        response.Results.Keys.Should().BeEquivalentTo(AllTypes.Select(t => t.Name + "Out").Append("Context"));
        foreach (var (name, value) in values) response.Results[name + "Out"].Should().BeEquivalentTo(value, name);
        response.Results["Context"].Should().Be($"Stage=30;PrimaryEntityName=none;PrimaryEntityId={Guid.Empty};UserId={await AdminUserIdAsync()};Outputs=");
    }

    [Fact]
    public async Task Sdk_calls_an_entity_bound_api()
    {
        await ImportAsync(BoundApi);
        var target = new EntityReference(TestEntities.Pipe, Guid.NewGuid());

        var response = await Admin.ExecuteAsync(new OrganizationRequest("xt_Bound") { ["Target"] = target, ["Text"] = "bound" });

        response.Results["TextOut"].Should().Be("bound");
        ((string)response.Results["Context"]).Should().StartWith($"Stage=30;PrimaryEntityName={TestEntities.Pipe};PrimaryEntityId={target.Id};");
        response.Results.Keys.Should().BeEquivalentTo("TextOut", "Context");
    }

    [Fact]
    public async Task Target_of_another_table_is_refused()
    {
        await ImportAsync(BoundApi);

        await InvalidAsync(() => Admin.ExecuteAsync(new OrganizationRequest("xt_Bound")
        {
            ["Target"] = new EntityReference("systemuser", Guid.NewGuid()), ["Text"] = "x",
        }), "Target", "systemuser", TestEntities.Pipe);
        await InvalidAsync(() => Admin.ExecuteAsync(new OrganizationRequest("xt_Bound") { ["Text"] = "x" }), "Target");
    }

    [Fact]
    public async Task Validation_runs_before_any_stage()
    {
        await ImportAsync(EchoApi);
        var thrower = await PluginTestHelper.RegisterTypeAsync(Admin,
            await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes), "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, thrower, "xt_Echo", null, 10, config: "invalid:stage 10 ran");

        (await InvalidAsync(() => Admin.ExecuteAsync(new OrganizationRequest("xt_Echo") { ["Int"] = 1 }), "'Text'"))
            .Message.Should().NotContain("stage 10 ran");
        (await InvalidAsync(() => Admin.ExecuteAsync(new OrganizationRequest("xt_Echo") { ["Text"] = "x", ["Int"] = "one" }), "'Int'"))
            .Message.Should().NotContain("stage 10 ran");
        (await InvalidAsync(() => Admin.ExecuteAsync(new OrganizationRequest("xt_Echo") { ["Text"] = "x", ["Other"] = 1 }), "'Other'"))
            .Message.Should().NotContain("stage 10 ran");
        // A valid request reaches the step.
        (await FaultAsync(() => Admin.ExecuteAsync(new OrganizationRequest("xt_Echo") { ["Text"] = "x" }))).Message.Should().Contain("stage 10 ran");
    }

    [Fact]
    public async Task Steps_fire_around_the_main_operation()
    {
        await ImportAsync(EchoApi);
        var echo = CustomApiTestSolution.PluginTypeId(Echo);
        await PluginTestHelper.RegisterStepAsync(Admin, echo, "xt_Echo", null, 20, config: "input:!");
        await PluginTestHelper.RegisterStepAsync(Admin, echo, "xt_Echo", null, 40, config: "output:?");

        var response = await Admin.ExecuteAsync(new OrganizationRequest("xt_Echo") { ["Text"] = "abc" });

        // Stage 20 changed the input the main operation echoed; stage 40 saw the completed outputs and changed one.
        response.Results["TextOut"].Should().Be("abc!?");
        var context = (string)response.Results["Context"];
        context.Should().StartWith("Stage=40;PrimaryEntityName=none;");
        context.Should().EndWith("Outputs=" + string.Join(",", AllTypes.Select(t => t.Name + "Out").Append("Context").Order(StringComparer.Ordinal)));
        response.Results["IntOut"].Should().Be(0, "an output the main operation left unset has its type's default");
    }

    [Fact]
    public async Task Api_without_plugin_returns_defaults()
    {
        await ImportAsync(new TestApi("xt_Empty", Requests: [("Text", 10, true, null)],
            Responses: [("Count", 7), ("Flag", 0), ("Name", 10), ("Id", 12), ("When", 1), ("Amount", 2), ("Ratio", 6), ("Choice", 9)]));

        var response = await Admin.ExecuteAsync(new OrganizationRequest("xt_Empty") { ["Text"] = "ignored" });

        response.Results.Should().HaveCount(8);
        response.Results["Count"].Should().Be(0);
        response.Results["Flag"].Should().Be(false);
        response.Results["Name"].Should().BeNull();
        response.Results["Id"].Should().Be(Guid.Empty);
        response.Results["When"].Should().Be(DateTime.MinValue);
        response.Results["Amount"].Should().Be(0m);
        response.Results["Ratio"].Should().Be(0d);
        response.Results["Choice"].Should().BeNull();
    }

    [Fact]
    public async Task Main_operation_failure_rolls_back()
    {
        await ImportAsync(new TestApi("xt_Fail", PluginTypeName: Echo, Requests: [("CreateThenFail", 10, false, null)]));

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new OrganizationRequest("xt_Fail") { ["CreateThenFail"] = TestEntities.Pipe }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("The main operation failed after creating a record.");
        (await PipesNamedAsync("created by the main operation")).Should().Be(0, "the record the plug-in created is rolled back");
    }

    [Fact]
    public async Task Wrong_response_type_fails_the_call()
    {
        await ImportAsync(new TestApi("xt_WrongType", PluginTypeName: WrongType, Responses: [("Count", 7)]));

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new OrganizationRequest("xt_WrongType")));

        fault.HttpStatus.Should().Be(500);
        fault.ErrorCode.Should().Be(ErrorCodes.UnexpectedPluginException);
        fault.Message.Should().Contain("Count").And.Contain("xt_WrongType");
    }

    [Fact]
    public async Task Undeclared_outputs_are_dropped_and_logged_once()
    {
        await ImportAsync(new TestApi("xt_Partial", AllowedStepType: 2, PluginTypeName: Echo,
            Requests: [("Text", 10, false, null), ("Extra", 10, true, null)], Responses: [("TextOut", 10)]));
        // A second run of the echo at stage 40 sets the same undeclared keys again.
        await PluginTestHelper.RegisterStepAsync(Admin, CustomApiTestSolution.PluginTypeId(Echo), "xt_Partial", null, 40);
        var logger = new ListLogger();
        var executor = TestServices.NewPipelineExecutor(fx.App.Services, logger: logger);
        var caller = await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId);

        var response = await executor.ExecuteAsync(new OrganizationRequest("xt_Partial") { ["Text"] = "a", ["Extra"] = "b" },
            new InvocationContext(caller, caller.UserId, 1, null, null, Guid.NewGuid()), default);

        response.Results.Keys.Should().Equal("TextOut");
        logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Text.Contains("xt_Partial")).Should().ContainSingle()
            .Which.Text.Should().Contain("ExtraOut").And.Contain("Context");
    }

    [Fact]
    public async Task Execute_privilege_is_demanded()
    {
        await ImportAsync(new TestApi("xt_Guarded", PluginTypeName: Echo, PrivilegeName: "prvWriteRole",
            Requests: [("Text", 10, false, null)], Responses: [("TextOut", 10)]));
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await AssignRoleAsync(fx, user, StandardRoles.BasicUser);

        var fault = await FaultAsync(() => ServiceFor(fx, oid).ExecuteAsync(new OrganizationRequest("xt_Guarded") { ["Text"] = "x" }));

        fault.HttpStatus.Should().Be(403);
        fault.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);
        fault.Message.Should().Contain("prvWriteRole");
        (await Admin.ExecuteAsync(new OrganizationRequest("xt_Guarded") { ["Text"] = "x" })).Results["TextOut"].Should().Be("x");
    }

    [Fact]
    public async Task Private_api_is_callable_from_sdk_and_plugin()
    {
        await ImportAsync(
            new TestApi("xt_Private", IsPrivate: true, PluginTypeName: Echo, Requests: [("Text", 10, false, null)], Responses: [("TextOut", 10)]),
            new TestApi("xt_Caller", PluginTypeName: Echo, Requests: [("Text", 10, false, null), ("Call", 10, false, null)],
                Responses: [("TextOut", 10), ("CallOut", 10)]));

        (await Admin.ExecuteAsync(new OrganizationRequest("xt_Private") { ["Text"] = "direct" })).Results["TextOut"].Should().Be("direct");
        var nested = await Admin.ExecuteAsync(new OrganizationRequest("xt_Caller") { ["Text"] = "outer", ["Call"] = "xt_Private" });
        nested.Results["CallOut"].Should().Be("nested");
        nested.Results["TextOut"].Should().Be("outer");
    }

    [Fact]
    public async Task Plugin_type_removal_refusal_keeps_the_api_callable()
    {
        await ImportAsync(BoundApi with { BindingType = 0, BoundEntity = null, UniqueName = "xt_Kept" });

        await InvalidAsync(() => Admin.DeleteAsync("plugintype", CustomApiTestSolution.PluginTypeId(Echo)), "xt_Kept");
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes); // the Echo type's
        await InvalidAsync(() => Admin.DeleteAsync("pluginassembly", assembly), "xt_Kept");

        (await Admin.ExecuteAsync(new OrganizationRequest("xt_Kept") { ["Text"] = "still" })).Results["TextOut"].Should().Be("still");
    }

    [Fact]
    public async Task Soap_execute_calls_a_custom_api()
    {
        await ImportAsync(new TestApi("xt_Soap", PluginTypeName: Echo,
            Requests: [("Texts", 11, false, null), ("Ref", 5, false, null), ("Cash", 8, false, null), ("Id", 12, false, null)],
            Responses: [("TextsOut", 11), ("RefOut", 5), ("CashOut", 8), ("IdOut", 12)]));
        var reference = new EntityReference("systemuser", Guid.NewGuid());
        var id = Guid.NewGuid();
        var request = new OrganizationRequest("xt_Soap") { ["Texts"] = new[] { "x", "y" }, ["Ref"] = reference, ["Cash"] = new Money(4.5m), ["Id"] = id };

        using var http = new HttpRequestMessage(HttpMethod.Post, Soap)
        {
            Content = new StringContent(OrganizationSoapEndpointTests.Envelope(OrganizationSoapEndpointTests.Execute(request)), Encoding.UTF8, "text/xml"),
        };
        http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await OrganizationSoapEndpointTests.ExecuteResultAsync(await fx.Client.SendAsync(http));

        response.ResponseName.Should().Be("xt_Soap");
        response.Results["TextsOut"].Should().BeOfType<string[]>().Which.Should().Equal("x", "y");
        response.Results["RefOut"].Should().BeOfType<EntityReference>().Which.Id.Should().Be(reference.Id);
        ((EntityReference)response.Results["RefOut"]).LogicalName.Should().Be("systemuser");
        response.Results["CashOut"].Should().BeOfType<Money>().Which.Value.Should().Be(4.5m);
        response.Results["IdOut"].Should().Be(id);
    }
}

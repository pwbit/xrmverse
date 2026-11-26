using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Host.Ui;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Pipeline;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1e, task 1 (ruling E1): <c>GET /Ribbon/{entity}</c> serves the entity-level <c>RibbonDiffXml</c> every installed
/// solution kept (<c>solution.xrmverse_unsupportedxml</c>), oldest layer first, after a read check on <c>solution</c>.
/// </summary>
[Collection("solution-import")]
public sealed class RibbonEndpointTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Entity = "xv_project";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    // The ribbon of a form button, written for these tests (neutral names).
    private static XElement ButtonRibbon() =>
        new("RibbonDiffXml",
            new XElement("CustomActions",
                new XElement("CustomAction", new XAttribute("Id", "xv.project.Hello.CustomAction"),
                    new XAttribute("Location", "Mscrm.Form.xv_project.MainTab.Actions.Controls._children"), new XAttribute("Sequence", "10"),
                    new XElement("CommandUIDefinition",
                        new XElement("Button", new XAttribute("Id", "xv.project.Hello.Button"), new XAttribute("Command", "xv.project.Hello.Command"),
                            new XAttribute("LabelText", "Say hello"), new XAttribute("Sequence", "10"))))),
            new XElement("Templates", new XElement("RibbonTemplates", new XAttribute("Id", "Mscrm.Templates"))),
            new XElement("CommandDefinitions",
                new XElement("CommandDefinition", new XAttribute("Id", "xv.project.Hello.Command"),
                    new XElement("EnableRules"), new XElement("DisplayRules"),
                    new XElement("Actions", new XElement("JavaScriptFunction", new XAttribute("FunctionName", "xvHello"), new XAttribute("Library", "$webresource:xv_/hello.js"))))),
            new XElement("RuleDefinitions", new XElement("TabDisplayRules"), new XElement("DisplayRules"), new XElement("EnableRules")),
            new XElement("LocLabels"));

    /// <summary>
    /// What an import of the fixture with <see cref="ButtonRibbon"/> on <c>xv_Project</c> leaves for the route: the tables
    /// <c>xv_project</c> and <c>xv_task</c>, and the unmanaged solution <see cref="SolutionFixtureZip.FixtureName"/> keeping the
    /// ribbon in <c>xrmverse_unsupportedxml</c> (written by the internal caller, as the import writes it). The route is under
    /// test, not the import.
    /// </summary>
    private async Task ImportWithRibbonAsync()
    {
        foreach (var (logical, schema) in new[] { (Entity, "xv_Project"), ("xv_task", "xv_Task") })
        {
            if (Store.GetEntity(logical) is not null) continue;
            var table = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata
            {
                LogicalName = logical, SchemaName = schema, OwnershipType = Microsoft.Xrm.Sdk.Metadata.OwnershipTypes.UserOwned,
            };
            table.SetAttributes([]);
            await Store.CreateEntityAsync(table,
                new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = "xv_name", SchemaName = "xv_Name", MaxLength = 100 });
        }
        var publisher = await Admin.CreateAsync(new Entity("publisher")
        {
            ["uniquename"] = "xrmverse", ["friendlyname"] = "xrmverse", ["customizationprefix"] = "xv", ["customizationoptionvalueprefix"] = 10000,
        });
        using var scope = fx.App.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<CoreOperations>();
        await TestEntities.InSessionAsync(fx.ConnectionString, s => core.ExecuteCoreAsync(new Microsoft.Xrm.Sdk.Messages.CreateRequest
        {
            Target = new Entity("solution")
            {
                ["uniquename"] = SolutionFixtureZip.FixtureName, ["friendlyname"] = SolutionFixtureZip.FixtureName, ["version"] = "1.0.0.0",
                ["publisherid"] = new EntityReference("publisher", publisher), ["ismanaged"] = false,
                [SystemTableRules.UnsupportedXml] = Kept(Entity, ButtonRibbon()),
            },
        }, Caller.Internal, s, default));
    }

    private async Task<HttpResponseMessage> GetAsync(string path, Guid? objectId = null, string? ifNoneMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await fx.Client.SendAsync(req);
    }

    // ---- DiffsFor (pure) --------------------------------------------------------------------------------------------

    private static string Kept(string entity, params XElement[] parts) =>
        new XElement(KeptXml.Root, new XElement("Entity", new XAttribute("Name", entity), parts)).ToString(SaveOptions.DisableFormatting);

    [Fact]
    public void DiffsFor_orders_managed_by_install_then_unmanaged()
    {
        var ribbon = ButtonRibbon();
        var diffs = RibbonEndpoints.DiffsFor(Entity,
        [
            ("xv_unmanaged_old", false, T0, Kept(Entity, new XElement(ribbon))),
            ("xv_managed_late", true, T0.AddDays(5), Kept(Entity, new XElement(ribbon))),
            ("xv_managed_early", true, T0.AddDays(1), Kept(Entity, new XElement(ribbon))),
            ("xv_unmanaged_new", false, T0.AddDays(9), Kept(Entity, new XElement(ribbon))),
        ]);

        diffs.Select(d => d.Solution).Should().Equal("xv_managed_early", "xv_managed_late", "xv_unmanaged_old", "xv_unmanaged_new");
        diffs.Should().OnlyContain(d => d.Xml.StartsWith("<RibbonDiffXml"));
    }

    [Fact]
    public void DiffsFor_skips_empty_skeleton()
    {
        var skeleton = new XElement("RibbonDiffXml",
            new XElement("CustomActions"), new XElement("Templates", new XElement("RibbonTemplates", new XAttribute("Id", "Mscrm.Templates"))),
            new XElement("CommandDefinitions"), new XElement("RuleDefinitions", new XElement("TabDisplayRules"), new XElement("DisplayRules"), new XElement("EnableRules")),
            new XElement("LocLabels"));

        var diffs = RibbonEndpoints.DiffsFor(Entity,
        [
            ("xv_empty", true, T0, Kept(Entity, skeleton)),
            ("xv_none", true, T0, null),
            ("xv_other_entity", true, T0, Kept("xv_task", ButtonRibbon())),
            ("xv_other_part", true, T0, Kept(Entity, new XElement("Visualizations", new XElement("visualization")))),
            ("xv_real", true, T0.AddDays(1), Kept(Entity, ButtonRibbon())),
        ]);

        diffs.Should().ContainSingle().Which.Solution.Should().Be("xv_real");
    }

    [Fact]
    public void DiffsFor_skips_a_corrupt_row_and_logs_a_warning()
    {
        var logger = new ListLogger();
        var diffs = RibbonEndpoints.DiffsFor(Entity,
        [
            ("xv_corrupt", true, T0, "<not-xml"),
            ("xv_wrong_root", true, T0.AddDays(1), "<other/>"),
            ("xv_good", true, T0.AddDays(2), Kept(Entity, ButtonRibbon())),
        ], logger);

        diffs.Should().ContainSingle().Which.Solution.Should().Be("xv_good");
        logger.Warnings.Should().HaveCount(2);
        logger.Warnings[0].Should().Contain("xv_corrupt");
        logger.Warnings[1].Should().Contain("xv_wrong_root");
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    // ---- the route --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_ribbon_returns_imported_diff()
    {
        await ImportWithRibbonAsync();

        var response = await GetAsync("/Ribbon/" + Entity);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl!.MustRevalidate.Should().BeTrue();
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("entity").GetString().Should().Be(Entity);
        var diff = json.RootElement.GetProperty("diffs").EnumerateArray().Should().ContainSingle().Subject;
        diff.GetProperty("solution").GetString().Should().Be(SolutionFixtureZip.FixtureName);
        var xml = XElement.Parse(diff.GetProperty("xml").GetString()!);
        xml.Name.LocalName.Should().Be("RibbonDiffXml");
        xml.Descendants("Button").Should().ContainSingle().Which.Attribute("Id")!.Value.Should().Be("xv.project.Hello.Button");
    }

    [Fact]
    public async Task Entity_without_diff_answers_an_empty_list()
    {
        await ImportWithRibbonAsync();

        var response = await GetAsync("/Ribbon/xv_task");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("diffs").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Session_cookie_authenticates()
    {
        await ImportWithRibbonAsync();
        using var client = fx.App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var req = new HttpRequestMessage(HttpMethod.Get, "/Ribbon/" + Entity);
        req.Headers.TryAddWithoutValidation("Cookie", "xrmd.session=" + DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));

        (await client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/Ribbon/" + Entity)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_entity_is_404()
    {
        await ImportWithRibbonAsync();
        (await GetAsync("/Ribbon/xv_no_such_entity")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Etag_round_trip_304()
    {
        await ImportWithRibbonAsync();
        var first = await GetAsync("/Ribbon/" + Entity);
        var etag = first.Headers.ETag!.ToString();

        var second = await GetAsync("/Ribbon/" + Entity, ifNoneMatch: etag);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        second.Headers.ETag!.ToString().Should().Be(etag);
        (await GetAsync("/Ribbon/" + Entity, ifNoneMatch: "\"other\"")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Basic_user_can_read()
    {
        await ImportWithRibbonAsync();
        var root = await RootBusinessUnitAsync(fx);
        var basic = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, basic), StandardRoles.BasicUser);

        var response = await GetAsync("/Ribbon/" + Entity, basic);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("xv.project.Hello.Button");
    }

    [Fact]
    public async Task Needs_read_on_solution()
    {
        await ImportWithRibbonAsync();
        var root = await RootBusinessUnitAsync(fx);
        var other = Guid.NewGuid();
        await AssignRoleAsync(fx, await CreateUserAsync(fx, root, other),
            await CreateRoleAsync(fx, "no-solutions-" + Guid.NewGuid().ToString("N")[..8], ("prvReadAccount", DepthMask.Global)));

        (await GetAsync("/Ribbon/" + Entity, other)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

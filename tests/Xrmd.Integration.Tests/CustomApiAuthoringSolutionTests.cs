using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Custom APIs authored outside an import and solutions (plan 3.1, task 4): a Create in a solution (SDK parameter or Web API
/// header) tracks the API as a component and not its parameters or properties; an authored API, which has no stored component
/// XML, exports and imports into an empty organization with the same columns, and exports again to the same files.
/// </summary>
[Collection("solution-import")]
public sealed class CustomApiAuthoringSolutionTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Solution = "XtAuthoring";
    private const string Echo = "Xrmd.TestPlugins.CustomApiEchoPlugin";

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
    }

    public Task DisposeAsync() => SolutionImportTests.ClearImportAsync(fx);

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(sql, conn);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<int> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt32(await ScalarAsync(sql, parameters));

    /// <summary>The publisher with prefix <c>xt</c> and the empty solution <see cref="Solution"/>.</summary>
    private async Task CreateSolutionAsync()
    {
        var publisher = await Admin.CreateAsync(new Entity("publisher")
        {
            ["uniquename"] = CustomApiTestSolution.PublisherName, ["friendlyname"] = "APIs", ["customizationprefix"] = "xt",
            ["customizationoptionvalueprefix"] = 10100,
        });
        await Admin.CreateAsync(new Entity("solution")
        {
            ["uniquename"] = Solution, ["friendlyname"] = "Authoring", ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisher),
        });
    }

    private async Task<(Guid Api, Guid Parameter, Guid Property)> CreateApiAsync(string name)
    {
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var plugin = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, Echo);
        // The export must carry the plug-in type of the main operation, or an empty organization cannot import the API.
        await Admin.ExecuteAsync(new AddSolutionComponentRequest { ComponentType = 91, ComponentId = assembly, SolutionUniqueName = Solution });
        await Admin.ExecuteAsync(new AddSolutionComponentRequest { ComponentType = 90, ComponentId = plugin, SolutionUniqueName = Solution });
        var api = (Guid)(await Admin.ExecuteAsync(new CreateRequest
        {
            Target = new Entity(CustomApiTables.Api)
            {
                ["uniquename"] = name, ["name"] = name, ["displayname"] = "Echo it", ["description"] = "Echoes the text",
                ["bindingtype"] = new OptionSetValue(0), ["isfunction"] = true, ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),
                ["plugintypeid"] = new EntityReference("plugintype", plugin),
            },
            ["SolutionUniqueName"] = Solution,
        })).Results["id"];
        var parameter = await Admin.CreateAsync(new Entity("customapirequestparameter")
        {
            ["uniquename"] = "Text", ["name"] = "Text", ["displayname"] = "The text", ["type"] = new OptionSetValue(10), ["isoptional"] = false,
            ["customapiid"] = new EntityReference(CustomApiTables.Api, api),
        });
        var property = await Admin.CreateAsync(new Entity("customapiresponseproperty")
        {
            ["uniquename"] = "TextOut", ["name"] = "TextOut", ["displayname"] = "The echo", ["type"] = new OptionSetValue(10),
            ["customapiid"] = new EntityReference(CustomApiTables.Api, api),
        });
        return (api, parameter, property);
    }

    private Task<int> ComponentsAsync(Guid objectId) => CountAsync("""
        SELECT COUNT(*) FROM data.solutioncomponent sc JOIN data.solution s ON s.solutionid = sc.solutionid
        WHERE s.uniquename = @s AND sc.objectid = @id AND sc.componenttype = 9990
        """, ("@s", Solution), ("@id", objectId));

    private async Task<byte[]> ExportAsync() =>
        ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = Solution, Managed = false })).ExportSolutionFile;

    /// <summary>The <c>customapis/</c> files of the zip, by path, as XML.</summary>
    private static SortedDictionary<string, XElement> ApiFiles(byte[] zip)
    {
        var files = new SortedDictionary<string, XElement>(StringComparer.Ordinal);
        using var archive = new ZipArchive(new MemoryStream(zip));
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("customapis/", StringComparison.Ordinal) && e.Length > 0))
        {
            using var stream = entry.Open();
            files[entry.FullName] = XElement.Load(stream);
        }
        return files;
    }

    private async Task<List<string>> RowsAsync(string table, string columns)
    {
        var rows = new List<string>();
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command($"SELECT {columns} FROM data.{table} ORDER BY uniquename", conn);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, rd.FieldCount).Select(i => rd.IsDBNull(i) ? "<null>" : Convert.ToString(rd.GetValue(i)))));
        return rows;
    }

    // ---- tests ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_with_solution_tracks_component()
    {
        await CreateSolutionAsync();
        var (api, parameter, property) = await CreateApiAsync("xt_Tracked");

        (await ComponentsAsync(api)).Should().Be(1, "the API is a component of the solution");
        (await CountAsync("SELECT COUNT(*) FROM data.solutioncomponent WHERE objectid IN (@p, @q)", ("@p", parameter), ("@q", property)))
            .Should().Be(0, "the parameters and properties are not components");

        // The Web API header does the same.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/data/v9.2/customapis")
        {
            Content = new StringContent(new JsonObject
            {
                ["uniquename"] = "xt_Header", ["name"] = "xt_Header", ["displayname"] = "Header", ["bindingtype"] = 0, ["isfunction"] = false,
                ["allowedcustomprocessingsteptype"] = 0,
            }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("MSCRM.SolutionUniqueName", Solution);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var response = await fx.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        var url = response.Headers.GetValues("OData-EntityId").Single();
        var header = Guid.Parse(url[(url.LastIndexOf('(') + 1)..^1]);
        (await ComponentsAsync(header)).Should().Be(1);
    }

    [Fact]
    public async Task Authored_api_round_trips_through_export()
    {
        await CreateSolutionAsync();
        await CreateApiAsync("xt_Round");
        const string apiColumns = "uniquename, name, displayname, description, bindingtype, boundentitylogicalname, isfunction, isprivate, " +
            "allowedcustomprocessingsteptype, workflowsdkstepenabled, executeprivilegename, plugintypeid, sdkmessageid";
        const string parameterColumns = "uniquename, name, displayname, description, type, isoptional, logicalentityname";
        const string propertyColumns = "uniquename, name, displayname, description, type, logicalentityname";
        // The ids are not in the file: an import derives them again, so the rows are compared by their columns.
        var apis = await RowsAsync("customapi", apiColumns);
        var parameters = await RowsAsync("customapirequestparameter", parameterColumns);
        var properties = await RowsAsync("customapiresponseproperty", propertyColumns);
        var first = await ExportAsync();
        var files = ApiFiles(first);
        files.Keys.Should().Contain("customapis/xt_Round/customapi.xml");
        files.Keys.Should().HaveCount(3, "the API, its parameter and its property");

        await SolutionImportTests.ClearImportAsync(fx);
        (await CountAsync("SELECT COUNT(*) FROM data.customapi")).Should().Be(0);
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = first, ImportJobId = Guid.NewGuid() });

        (await RowsAsync("customapi", apiColumns)).Should().Equal(apis);
        (await RowsAsync("customapirequestparameter", parameterColumns)).Should().Equal(parameters);
        (await RowsAsync("customapiresponseproperty", propertyColumns)).Should().Equal(properties);

        var second = ApiFiles(await ExportAsync());
        second.Keys.Should().Equal(files.Keys);
        foreach (var (path, xml) in files)
            XNode.DeepEquals(Normalize(second[path]), Normalize(xml)).Should().BeTrue($"{path} is the same after the round trip:\n{xml}\n{second[path]}");
    }

    private static XElement Normalize(XElement element)
    {
        var copy = new XElement(element);
        foreach (var e in copy.DescendantsAndSelf())
            if (!e.HasElements) e.Value = e.Value.Trim();
        return copy;
    }
}

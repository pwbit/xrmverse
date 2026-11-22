using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.ServiceModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Solution;
using Xrmd.TestSupport;
using Xrmd.WebApi;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 5 acceptance (criterion B): the hand-written fixture solution imported with the real Dataverse
/// <see cref="ServiceClient"/> (the same <c>ImportSolution</c> message <c>pac solution import</c> sends), its plug-ins
/// firing on records created and updated through the client, its N:N queried, the asynchronous import polled to
/// completion and the export round-tripped.
/// </summary>
/// <remarks>Binds Kestrel to 127.0.0.1:80; skip with <c>dotnet test --filter Category!=Acceptance</c>.</remarks>
[Collection("serviceclient")]
[Trait("Category", "Acceptance")]
public sealed class SolutionAcceptanceTests(ServiceClientFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private const string Fixture = SolutionFixtureZip.FixtureName;
    private const string AssemblyEntry = "PluginAssemblies/XrmdTestPlugins-6B1C3F4E-1A2B-4C3D-8E9F-0A1B2C3D4E10/XrmdTestPlugins.dll";

    public async Task InitializeAsync()
    {
        fx.DrainRequestLog();
        // The fixture's post-operation Update step writes pb_pipelog records; its steps resolve against the message catalog.
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await ClearAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var line in fx.DrainRequestLog()) output.WriteLine(line);
        await ClearAsync();
    }

    private async Task ClearAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx.App.Services, fx.ConnectionString);
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command("DELETE FROM data.asyncoperation WHERE operationtype = 203", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private ServiceClient Connect() =>
        new(new Uri(ServiceClientFixture.BaseUrl), _ => Task.FromResult(DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl)),
            useUniqueInstance: true, new TestOutputLogger(output));

    private static Dictionary<string, byte[]> Entries(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
    }

    private static string Normalized(byte[] xml) => XmlNormalizer.Normalize(XDocument.Load(new MemoryStream(xml)));

    private static byte[] Export(ServiceClient client) =>
        ((ExportSolutionResponse)client.Execute(new ExportSolutionRequest { SolutionName = Fixture, Managed = false })).ExportSolutionFile;

    [Fact]
    public void Criterion_B_import_then_plugins_fire_through_ServiceClient()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var fixtureZip = SolutionFixtureZip.Build();

        // 1. ImportSolution: the fixture's tables, N:N, option set, role, plug-in assembly and steps.
        var importJobId = Guid.NewGuid();
        var imported = client.Execute(new ImportSolutionRequest { CustomizationFile = fixtureZip, ImportJobId = importJobId });
        imported.Should().BeOfType<ImportSolutionResponse>();
        var job = client.Retrieve("importjob", importJobId, new ColumnSet("solutionname", "progress", "completedon"));
        job.GetAttributeValue<string>("solutionname").Should().Be(Fixture);
        job.GetAttributeValue<double>("progress").Should().Be(100);
        job.GetAttributeValue<DateTime?>("completedon").Should().NotBeNull();

        // 2. Create: the imported pre-operation Create step sets xv_description.
        var projectId = client.Create(new Entity("xv_project") { ["xv_name"] = "Apollo" });
        client.Retrieve("xv_project", projectId, new ColumnSet("xv_description")).GetAttributeValue<string>("xv_description")
            .Should().Be("set by the fixture pre-operation create step");

        // 3. Update: the imported post-operation Update step records its context, with the pre-image the solution registers.
        client.Update(new Entity("xv_project", projectId) { ["xv_name"] = "Apollo 2" });
        var logs = new QueryExpression(TestEntities.PipeLog) { ColumnSet = new ColumnSet("pb_name", "pb_data") };
        var log = client.RetrieveMultiple(logs).Entities.Should().ContainSingle().Subject;
        log.GetAttributeValue<string>("pb_name").Should().Be("Update|40|0|1");
        log.GetAttributeValue<string>("pb_data").Split('\n').Should().Contain(l => l.StartsWith("pre=PreImage:", StringComparison.Ordinal)
            && l.Contains("xv_name", StringComparison.Ordinal));
        client.Retrieve("xv_project", projectId, new ColumnSet("xv_name")).GetAttributeValue<string>("xv_name").Should().Be("Apollo 2");

        // 4. The imported N:N: Associate, then RetrieveMultiple on the intersect entity and through a link-entity.
        var tagId = client.Create(new Entity("xv_tag") { ["xv_name"] = "urgent" });
        client.Associate("xv_project", projectId, new Relationship("xv_project_xv_tag"),
            new EntityReferenceCollection { new EntityReference("xv_tag", tagId) });
        var intersect = client.RetrieveMultiple(new QueryExpression("xv_project_xv_tag") { ColumnSet = new ColumnSet("xv_projectid", "xv_tagid") });
        var pair = intersect.Entities.Should().ContainSingle().Subject;
        pair.GetAttributeValue<Guid>("xv_projectid").Should().Be(projectId);
        pair.GetAttributeValue<Guid>("xv_tagid").Should().Be(tagId);
        var tagsOfProject = new QueryExpression("xv_tag") { ColumnSet = new ColumnSet("xv_name") };
        tagsOfProject.AddLink("xv_project_xv_tag", "xv_tagid", "xv_tagid").LinkCriteria.AddCondition("xv_projectid", ConditionOperator.Equal, projectId);
        client.RetrieveMultiple(tagsOfProject).Entities.Select(e => e.GetAttributeValue<string>("xv_name")).Should().Equal("urgent");

        // 5. ImportSolutionAsync (an upgrade of the same solution): the host's worker runs it; poll asyncoperation to 30.
        var asyncImportJobId = Guid.NewGuid();
        var queued = (ImportSolutionAsyncResponse)client.Execute(
            new ImportSolutionAsyncRequest { CustomizationFile = fixtureZip, ImportJobId = asyncImportJobId });
        queued.ImportJobKey.Should().Be(asyncImportJobId.ToString());
        var deadline = DateTime.UtcNow.AddMinutes(2);
        Entity operation;
        while (true)
        {
            operation = client.Retrieve("asyncoperation", queued.AsyncOperationId, new ColumnSet("statecode", "statuscode", "operationtype", "message"));
            if (operation.GetAttributeValue<OptionSetValue>("statecode").Value == 3 || DateTime.UtcNow > deadline) break;
            Thread.Sleep(500);
        }
        operation.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(30, operation.GetAttributeValue<string>("message"));
        operation.GetAttributeValue<OptionSetValue>("operationtype").Value.Should().Be(203);
        client.Retrieve("importjob", asyncImportJobId, new ColumnSet("progress")).GetAttributeValue<double>("progress").Should().Be(100);
        // The upgrade kept the records and the association.
        client.RetrieveMultiple(tagsOfProject).Entities.Should().ContainSingle();

        // 6. ExportSolution round trip: the plug-in assembly byte for byte, solution.xml equal to the fixture's, and the
        // exported file imported again through the client exports the same customizations.
        var exported = Export(client);
        var entries = Entries(exported);
        entries[AssemblyEntry].Should().Equal(SolutionFixtureZip.TestPluginBytes);
        Normalized(entries["solution.xml"]).Should().Be(Normalized(SolutionFixtureZip.ReadFixtureFile("solution.xml")));

        client.Execute(new ImportSolutionRequest { CustomizationFile = exported, ImportJobId = Guid.NewGuid() });
        var again = Entries(Export(client));
        Normalized(again["customizations.xml"]).Should().Be(Normalized(entries["customizations.xml"]));
        Normalized(again["solution.xml"]).Should().Be(Normalized(entries["solution.xml"]));
        again[AssemblyEntry].Should().Equal(SolutionFixtureZip.TestPluginBytes);

        // The imported steps still fire after the round trip.
        var second = client.Create(new Entity("xv_project") { ["xv_name"] = "Gemini" });
        client.Retrieve("xv_project", second, new ColumnSet("xv_description")).GetAttributeValue<string>("xv_description")
            .Should().Be("set by the fixture pre-operation create step");
    }

    /// <summary>
    /// The fixture with an extra stored (uncompressed) entry of <paramref name="size"/> random bytes: a solution file larger
    /// than Kestrel's default 30,000,000-byte body once base64 encoded (the importer ignores entries it does not read).
    /// </summary>
    private static byte[] LargeFixture(int size)
    {
        var padding = new byte[size];
        Random.Shared.NextBytes(padding);
        using var stream = new MemoryStream();
        stream.Write(SolutionFixtureZip.Build());
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("Padding/blob.bin", CompressionLevel.NoCompression).Open();
            entry.Write(padding);
        }
        return stream.ToArray();
    }

    private HttpClient WebApi()
    {
        var http = new HttpClient { BaseAddress = new Uri(ServiceClientFixture.BaseUrl + "api/data/v9.2/"), Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl));
        return http;
    }

    [Fact]
    public async Task Solution_file_over_the_default_body_limit_imports_through_SOAP_and_the_Web_API()
    {
        var zip = LargeFixture(40 * 1024 * 1024);
        Convert.ToBase64String(zip).Length.Should().BeGreaterThan(30_000_000, "the file must not fit Kestrel's default body limit");

        // SOAP (ServiceClient, as pac): the import routes take up to 128 MB, the CustomizationFile array included.
        using (var client = Connect())
        {
            client.IsReady.Should().BeTrue(client.LastError);
            client.Execute(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() })
                .Should().BeOfType<ImportSolutionResponse>();
        }

        // Web API ImportSolution: the same limit.
        using var http = WebApi();
        var body = new JsonObject { ["CustomizationFile"] = Convert.ToBase64String(zip), ["ImportJobId"] = Guid.NewGuid().ToString() };
        using (var response = await http.PostAsync("ImportSolution", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")))
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

        // Any other route keeps Kestrel's default: an oversized body is 413, never silently truncated.
        // Expect: 100-continue, so that the client reads the 413 instead of failing on the body the server never reads.
        var other = new JsonObject { ["ParameterXml"] = new string('x', 31_000_000) };
        using var oversized = new HttpRequestMessage(HttpMethod.Post, "PublishXml")
        {
            Content = new StringContent(other.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        oversized.Headers.ExpectContinue = true;
        using (var response = await http.SendAsync(oversized))
            response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Solution_file_over_95_MB_is_ImportFileTooLargeToUpload_through_SOAP_and_the_Web_API()
    {
        // 95 MB + 1 byte: once base64 encoded it still fits the 128 MB request limit, so the import path itself refuses it.
        var file = new byte[Xrmd.Solution.SolutionImporter.MaxCustomizationFileBytes + 1];

        using (var client = Connect())
        {
            client.IsReady.Should().BeTrue(client.LastError);
            var act = () => client.Execute(new ImportSolutionRequest { CustomizationFile = file, ImportJobId = Guid.NewGuid() });
            var fault = act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail;
            fault.ErrorCode.Should().Be(ErrorCodes.ImportFileTooLargeToUpload);
            fault.Message.Should().Be("The import file is too large to upload.");
        }

        using var http = WebApi();
        var body = new JsonObject { ["CustomizationFile"] = Convert.ToBase64String(file) };
        using var response = await http.PostAsync("ImportSolutionAsync", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        var error = JsonNode.Parse(text)!["error"]!;
        error["code"]!.GetValue<string>().Should().Be("0x80040375");
        error["message"]!.GetValue<string>().Should().Be("The import file is too large to upload.");
    }

    [Fact]
    public void Solution_adding_a_prefixed_column_to_account_exports_as_a_system_entity()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        const string solution = "AccountExtension";
        var publisherId = client.RetrieveMultiple(new QueryExpression("publisher")
        {
            ColumnSet = new ColumnSet("publisherid"), Criteria = { Conditions = { new ConditionExpression("customizationprefix", ConditionOperator.Equal, "xv") } },
        }).Entities.Should().ContainSingle().Subject.Id;
        var solutionId = client.Create(new Entity("solution")
        {
            ["uniquename"] = solution, ["friendlyname"] = solution, ["version"] = "1.0.0.0",
            ["publisherid"] = new EntityReference("publisher", publisherId),
        });
        try
        {
            client.Execute(new Microsoft.Xrm.Sdk.Messages.CreateAttributeRequest
            {
                EntityName = "account", SolutionUniqueName = solution,
                Attribute = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = "xv_note", SchemaName = "xv_Note", MaxLength = 200 },
            });

            var exported = ((ExportSolutionResponse)client.Execute(new ExportSolutionRequest { SolutionName = solution, Managed = false })).ExportSolutionFile;
            var customizations = XDocument.Parse(Encoding.UTF8.GetString(Entries(exported)["customizations.xml"]).TrimStart('\uFEFF'));
            var account = customizations.Root!.Element("Entities")!.Elements("Entity").Should().ContainSingle().Subject;
            account.Element("Name")!.Value.Should().BeEquivalentTo("account");
            // A system entity: the solution carries the account's key, primary name and the added column, but is not custom.
            var attributes = account.Descendants("attribute").ToDictionary(a => a.Element("Name")!.Value, a => a.Element("IsCustomField")!.Value);
            attributes.Should().BeEquivalentTo(new Dictionary<string, string> { ["accountid"] = "0", ["name"] = "0", ["xv_note"] = "1" });
            var root = XDocument.Parse(Encoding.UTF8.GetString(Entries(exported)["solution.xml"]).TrimStart('\uFEFF'))
                .Descendants("RootComponent").Should().ContainSingle().Subject;
            (root.Attribute("type")!.Value, root.Attribute("behavior")!.Value).Should().Be(("2", "0"), "the column is the root component, not the table");

            // The exported file imports again into the same organization without errors.
            client.Execute(new ImportSolutionRequest { CustomizationFile = exported, ImportJobId = Guid.NewGuid() });
        }
        finally
        {
            try { client.Execute(new Microsoft.Xrm.Sdk.Messages.DeleteAttributeRequest { EntityLogicalName = "account", LogicalName = "xv_note" }); }
            catch (FaultException) { /* not created */ }
            client.Delete("solution", solutionId);
        }
    }

    [Fact]
    public async Task SOAP_body_over_the_limit_is_413()
    {
        using var http = new HttpClient { BaseAddress = new Uri(ServiceClientFixture.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "XRMServices/2011/Organization.svc/web")
        {
            Content = new ByteArrayContent(new byte[SolutionFileSizeLimit.MaxBytes + 1]),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl));
        request.Headers.ExpectContinue = true; // the client reads the 413 instead of failing on the body the server never reads

        using var response = await http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }
}

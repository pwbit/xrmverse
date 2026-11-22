using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The solution actions and functions of the Web API, translated into the SDK requests and executed by
/// <see cref="OrganizationService.ExecuteAsync"/>: the same results as the SDK and the same refusals (status and message).
/// </summary>
[Collection("solution-import")]
public sealed class SolutionWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Fixture = SolutionFixtureZip.FixtureName;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await SolutionImportTests.ClearImportAsync(fx);
        await ExecAsync("DELETE FROM data.asyncoperation");
    }

    public async Task DisposeAsync()
    {
        await SolutionImportTests.ClearImportAsync(fx);
        await ExecAsync("DELETE FROM data.asyncoperation");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode status)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task NoContentAsync(HttpResponseMessage res) =>
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);

    private async Task<XrmdFaultException> SdkFaultAsync(OrganizationRequest request) =>
        (await ((Func<Task>)(() => Admin.ExecuteAsync(request))).Should().ThrowAsync<XrmdFaultException>()).Which;

    /// <summary>The Web API refuses <paramref name="json"/> exactly as the SDK refuses <paramref name="sdk"/>.</summary>
    private async Task SameRefusalAsync(string action, string json, OrganizationRequest sdk, string named)
    {
        var expected = await SdkFaultAsync(sdk);
        expected.Message.Should().Contain(named);
        var res = await SendAsync(HttpMethod.Post, action, json);
        var error = (await JsonAsync(res, (HttpStatusCode)expected.HttpStatus))["error"]!;
        error["message"]!.GetValue<string>().Should().Be(expected.Message);
        error["code"]!.GetValue<string>().Should().Be("0x" + unchecked((uint)expected.ErrorCode).ToString("x8"));
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

    // ---- actions ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ImportSolution_imports_the_fixture_and_answers_204()
    {
        var jobId = Guid.NewGuid();
        await NoContentAsync(await SendAsync(HttpMethod.Post, "ImportSolution",
            $$"""{ "CustomizationFile": "{{Base64(SolutionFixtureZip.Build())}}", "ImportJobId": "{{jobId}}", "OverwriteUnmanagedCustomizations": false, "PublishWorkflows": false }"""));

        Store.GetEntity("xv_project").Should().NotBeNull();
        (await ScalarAsync("SELECT progress FROM data.importjob WHERE importjobid = @id", ("@id", jobId))).Should().NotBeNull();
    }

    [Fact]
    public async Task ImportSolutionAsync_answers_200_with_the_two_ids()
    {
        var json = await JsonAsync(await SendAsync(HttpMethod.Post, "ImportSolutionAsync",
            $$"""{ "CustomizationFile": "{{Base64(SolutionFixtureZip.Build())}}", "OverwriteUnmanagedCustomizations": false, "PublishWorkflows": false }"""),
            HttpStatusCode.OK);

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.ImportSolutionAsyncResponse");
        var operationId = Guid.Parse(json["AsyncOperationId"]!.GetValue<string>());
        var importJobKey = json["ImportJobKey"]!.GetValue<string>();
        (await ScalarAsync("SELECT operationtype FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", operationId)))
            .Should().Be(203);
        (await ScalarAsync("SELECT xrmverse_importjobid FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", operationId)))
            .Should().Be(Guid.Parse(importJobKey));
    }

    [Fact]
    public async Task ExportSolution_answers_200_with_the_file_of_the_sdk_export()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build() });
        var sdk = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest { SolutionName = Fixture, Managed = false }))
            .ExportSolutionFile;

        var json = await JsonAsync(await SendAsync(HttpMethod.Post, "ExportSolution", $$"""{ "SolutionName": "{{Fixture}}", "Managed": false }"""),
            HttpStatusCode.OK);

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.ExportSolutionResponse");
        var file = Convert.FromBase64String(json["ExportSolutionFile"]!.GetValue<string>());
        // The zip's entry times are the export's; the content is the same.
        Entries(file).Should().BeEquivalentTo(Entries(sdk));
    }

    [Fact]
    public async Task Solution_components_are_added_and_removed()
    {
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build() });
        var tag = Store.GetEntity("xv_tag")!.MetadataId!.Value;

        var removed = await JsonAsync(await SendAsync(HttpMethod.Post, "RemoveSolutionComponent", $$"""
            { "SolutionComponent": { "@odata.type": "Microsoft.Dynamics.CRM.solutioncomponent", "solutioncomponentid": "{{tag}}" },
              "ComponentType": 1, "SolutionUniqueName": "{{Fixture}}" }
            """), HttpStatusCode.OK);
        removed["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.RemoveSolutionComponentResponse");
        Guid.Parse(removed["id"]!.GetValue<string>()).Should().NotBeEmpty();

        var added = await JsonAsync(await SendAsync(HttpMethod.Post, "AddSolutionComponent", $$"""
            { "ComponentId": "{{tag}}", "ComponentType": 1, "SolutionUniqueName": "{{Fixture}}", "AddRequiredComponents": false,
              "DoNotIncludeSubcomponents": false }
            """), HttpStatusCode.OK);
        var id = Guid.Parse(added["id"]!.GetValue<string>());
        (await ScalarAsync("SELECT objectid FROM data.solutioncomponent WHERE solutioncomponentid = @id", ("@id", id))).Should().Be(tag);
    }

    [Fact]
    public async Task Publish_actions_answer_204()
    {
        await NoContentAsync(await SendAsync(HttpMethod.Post, "PublishXml",
            """{ "ParameterXml": "<importexportxml><entities><entity>account</entity></entities></importexportxml>" }"""));
        await NoContentAsync(await SendAsync(HttpMethod.Post, "PublishAllXml"));
        await NoContentAsync(await SendAsync(HttpMethod.Post, "PublishAllXml", "{}"));
    }

    [Fact]
    public async Task RetrieveFormattedImportJobResults_returns_the_sdk_workbook()
    {
        var jobId = Guid.NewGuid();
        await Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = SolutionFixtureZip.Build(), ImportJobId = jobId });
        var sdk = ((RetrieveFormattedImportJobResultsResponse)await Admin.ExecuteAsync(
            new RetrieveFormattedImportJobResultsRequest { ImportJobId = jobId })).FormattedResults;

        foreach (var path in new[]
                 {
                     $"RetrieveFormattedImportJobResults(ImportJobId=@p1)?@p1={jobId}",
                     $"RetrieveFormattedImportJobResults(ImportJobId={jobId})",
                 })
        {
            var json = await JsonAsync(await SendAsync(HttpMethod.Get, path), HttpStatusCode.OK);
            json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.RetrieveFormattedImportJobResultsResponse");
            json["FormattedResults"]!.GetValue<string>().Should().Be(sdk);
        }

        await JsonAsync(await SendAsync(HttpMethod.Get, $"RetrieveFormattedImportJobResults(ImportJobId={Guid.NewGuid()})"),
            HttpStatusCode.NotFound);
    }

    // ---- refusals ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Import_refusals_are_the_sdk_refusals()
    {
        var zip = SolutionFixtureZip.Build();
        var file = Base64(zip);
        await SameRefusalAsync("ImportSolution",
            $$"""{ "CustomizationFile": "{{file}}", "ComponentParameters": [ { "@odata.type": "Microsoft.Dynamics.CRM.connectionreference" } ] }""",
            new ImportSolutionRequest { CustomizationFile = zip, ["ComponentParameters"] = new EntityCollection { Entities = { new Entity("connectionreference") } } },
            "ComponentParameters");
        var upload = Guid.NewGuid();
        await SameRefusalAsync("ImportSolutionAsync",
            $$"""{ "CustomizationFile": "{{file}}", "SolutionParameters": { "StageSolutionUploadId": "{{upload}}" } }""",
            new ImportSolutionAsyncRequest { CustomizationFile = zip, ["SolutionParameters"] = new SolutionParameters { StageSolutionUploadId = upload } },
            "SolutionParameters");
        await SameRefusalAsync("ImportSolution", $$"""{ "CustomizationFile": "{{file}}", "HoldingSolution": true }""",
            new ImportSolutionRequest { CustomizationFile = zip, HoldingSolution = true }, "HoldingSolution");
        await SameRefusalAsync("ImportSolution", $$"""{ "CustomizationFile": "{{file}}", "IsTemplateMode": true }""",
            new ImportSolutionRequest { CustomizationFile = zip, IsTemplateMode = true }, "IsTemplateMode");
        await SameRefusalAsync("ImportSolution", $$"""{ "CustomizationFile": "{{file}}", "TemplateSuffix": "_t" }""",
            new ImportSolutionRequest { CustomizationFile = zip, TemplateSuffix = "_t" }, "TemplateSuffix");
        await SameRefusalAsync("ImportSolutionAsync", $$"""{ "CustomizationFile": "{{file}}", "LayerDesiredOrder": { "Type": "Top" } }""",
            new ImportSolutionAsyncRequest { CustomizationFile = zip, LayerDesiredOrder = new LayerDesiredOrder() }, "LayerDesiredOrder");
        await SameRefusalAsync("ImportSolutionAsync", "{}", new ImportSolutionAsyncRequest(), "CustomizationFile");

        Store.GetEntity("xv_project").Should().BeNull();
        (await ScalarAsync("SELECT COUNT(*) FROM data.asyncoperation")).Should().Be(0);
    }

    [Fact]
    public async Task Export_and_component_refusals_are_the_sdk_refusals()
    {
        await SameRefusalAsync("ExportSolution", $$"""{ "SolutionName": "{{Fixture}}", "Managed": true }""",
            new ExportSolutionRequest { SolutionName = Fixture, Managed = true }, "Managed");
        await SameRefusalAsync("ExportSolution", $$"""{ "SolutionName": "{{Fixture}}", "Managed": false, "ExportCalendarSettings": true }""",
            new ExportSolutionRequest { SolutionName = Fixture, ExportCalendarSettings = true }, "ExportCalendarSettings");
        await SameRefusalAsync("ExportSolution", $$"""{ "SolutionName": "{{Fixture}}", "Managed": false, "TargetVersion": "9.0" }""",
            new ExportSolutionRequest { SolutionName = Fixture, TargetVersion = "9.0" }, "TargetVersion");
        await SameRefusalAsync("ExportSolution", $$"""{ "SolutionName": "{{Fixture}}", "Managed": false, "ExportComponentsParams": {} }""",
            new ExportSolutionRequest { SolutionName = Fixture, ExportComponentsParams = new ExportComponentsParams() }, "ExportComponentsParams");

        var tag = Guid.NewGuid();
        await SameRefusalAsync("AddSolutionComponent",
            $$"""{ "ComponentId": "{{tag}}", "ComponentType": 1, "SolutionUniqueName": "{{Fixture}}", "AddRequiredComponents": false, "IncludedComponentSettingsValues": ["x"] }""",
            new AddSolutionComponentRequest
            {
                ComponentId = tag, ComponentType = 1, SolutionUniqueName = Fixture, IncludedComponentSettingsValues = ["x"],
            },
            "IncludedComponentSettingsValues");
        await SameRefusalAsync("AddSolutionComponent",
            $$"""{ "ComponentId": "{{tag}}", "ComponentType": 1, "SolutionUniqueName": "Default", "AddRequiredComponents": false }""",
            new AddSolutionComponentRequest { ComponentId = tag, ComponentType = 1, SolutionUniqueName = "Default" }, tag.ToString());
    }

    [Fact]
    public async Task Unknown_parameters_are_400_naming_them()
    {
        var error = (await JsonAsync(await SendAsync(HttpMethod.Post, "ExportSolution", $$"""{ "SolutionName": "{{Fixture}}", "Format": "zip" }"""),
            HttpStatusCode.BadRequest))["error"]!;
        error["message"]!.GetValue<string>().Should().Contain("'Format'").And.Contain("ExportSolution");

        var malformed = (await JsonAsync(await SendAsync(HttpMethod.Post, "ImportSolution", "[1]"), HttpStatusCode.BadRequest))["error"]!;
        malformed["message"]!.GetValue<string>().Should().Contain("JSON object");
    }

    [Fact]
    public async Task Unknown_action_is_404_as_before()
    {
        var error = (await JsonAsync(await SendAsync(HttpMethod.Post, "UnknownAction", "{}"), HttpStatusCode.NotFound))["error"]!;
        error["message"]!.GetValue<string>().Should().Contain("UnknownAction");
    }

    [Fact]
    public async Task Delete_of_a_solution_row_goes_through_the_core_delete()
    {
        // Routing only: the Delete semantics of solution are Task 11's.
        var error = (await JsonAsync(await SendAsync(HttpMethod.Delete, $"solutions({Guid.NewGuid()})"), HttpStatusCode.NotFound))["error"]!;
        error["message"]!.GetValue<string>().Should().Contain("solution");
    }

    [Fact]
    public async Task Metadata_declares_the_solution_operations_and_the_metadata_sets()
    {
        var res = await SendAsync(HttpMethod.Get, "$metadata");
        var csdl = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, csdl);

        foreach (var action in new[] { "ImportSolution", "ImportSolutionAsync", "ExportSolution", "AddSolutionComponent",
                                       "RemoveSolutionComponent", "PublishXml", "PublishAllXml" })
        {
            csdl.Should().Contain($"<Action Name=\"{action}\"");
            csdl.Should().Contain($"<ActionImport Name=\"{action}\"");
        }
        csdl.Should().Contain("<Function Name=\"RetrieveFormattedImportJobResults\"");
        csdl.Should().Contain("<FunctionImport Name=\"RetrieveFormattedImportJobResults\"");
        csdl.Should().Contain("<EntitySet Name=\"EntityDefinitions\" EntityType=\"Microsoft.Dynamics.CRM.EntityMetadata\"");
        csdl.Should().Contain("<EntitySet Name=\"GlobalOptionSetDefinitions\" EntityType=\"Microsoft.Dynamics.CRM.OptionSetMetadataBase\"");
        csdl.Should().Contain("<EntitySet Name=\"RelationshipDefinitions\" EntityType=\"Microsoft.Dynamics.CRM.RelationshipMetadataBase\"");
    }

    [Fact]
    public async Task Duplicate_keys_in_an_action_body_are_400()
    {
        var error = await JsonAsync(await SendAsync(HttpMethod.Post, "PublishXml",
            """{ "ParameterXml": "<importexportxml/>", "ParameterXml": "<importexportxml/>" }"""), HttpStatusCode.BadRequest);

        error["error"]!["message"]!.GetValue<string>().Should().Contain("ParameterXml");
    }
}

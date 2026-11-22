using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Files;
using Xrmd.Metadata;
using Xrmd.Pipeline;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.CustomApis;
using Xrmd.Security;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// The file property routes (<c>{set}({id})/{column}</c>, <c>…/$value</c>) among the Web API's routes: the endpoint each
/// request selects with the real route table (<see cref="WebApiEndpoints.MapWebApiEndpoints"/>), so that the file routes do
/// not shadow <c>$ref</c>, the bound actions or the metadata routes; and the parsing of the file headers.
/// </summary>
public class FileRoutingTests
{
    private const string Root = "/api/data/v9.2";

    private static readonly Lazy<(WebApplication App, RequestDelegate Pipeline)> Host = new(Build);

    /// <summary>
    /// The Web API's routes on an application whose pipeline stops after routing: the endpoint is selected, never run. The
    /// handlers' services are registered (unresolvable) so that their parameters bind as services.
    /// </summary>
    private static (WebApplication, RequestDelegate) Build()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        foreach (var type in new[]
                 {
                     typeof(IMetadataStore), typeof(OrganizationService), typeof(CallerResolver), typeof(MetadataOperations),
                     typeof(ODataEdmModelBuilder), typeof(ODataQueryParser), typeof(FileColumnService), typeof(SecurityGuard),
                     typeof(IDbConnectionFactory), typeof(PipelineExecutor), typeof(AsyncOperationQueue), typeof(CustomApiRegistry),
                 })
            builder.Services.AddScoped(type, _ => throw new InvalidOperationException("Routing tests never run an endpoint."));
        var app = builder.Build();
        app.UseRouting();
        app.Use((HttpContext _, RequestDelegate _) => Task.CompletedTask);
        app.MapGroup(Root).MapWebApiEndpoints();
        return (app, ((IApplicationBuilder)app).Build());
    }

    /// <summary>The route pattern of the endpoint <paramref name="method"/> <paramref name="path"/> selects (null: none).</summary>
    private static async Task<string?> RouteAsync(string method, string path)
    {
        var (app, pipeline) = Host.Value;
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        context.Request.Path = Root + path;
        await pipeline(context);
        var endpoint = context.GetEndpoint() as RouteEndpoint;
        return endpoint?.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true
            ? endpoint.RoutePattern.RawText![Root.Length..]
            : null;
    }

    private const string Record = "accounts(0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70)";

    [Theory]
    [InlineData("PATCH", "/" + Record + "/new_doc", "/{segment}/{property}")]
    [InlineData("DELETE", "/" + Record + "/new_doc", "/{segment}/{property}")]
    [InlineData("GET", "/" + Record + "/new_doc", "/{segment}/{property}")]
    [InlineData("GET", "/" + Record + "/new_doc/$value", "/{segment}/{property}/$value")]
    // A navigation property reaches the file route, which refuses it naming it (FileEndpoints.Record).
    [InlineData("GET", "/" + Record + "/primarycontactid", "/{segment}/{property}")]
    // $count has a route of its own (plan 2.13: the count of a delta), which refuses it without a $deltatoken.
    [InlineData("GET", "/accounts/$count", "/{segment}/$count")]
    public async Task File_property_routes_are_selected(string method, string path, string route) =>
        (await RouteAsync(method, path)).Should().Be(route);

    [Theory]
    [InlineData("POST", "/" + Record + "/new_account_contacts/$ref", "/{segment}/{navigation}/$ref")]
    [InlineData("DELETE", "/" + Record + "/new_account_contacts/$ref", "/{segment}/{navigation}/$ref")]
    [InlineData("DELETE", "/" + Record + "/new_account_contacts(0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f71)/$ref", "/{segment}/{navigation}/$ref")]
    public async Task Ref_routes_are_not_shadowed(string method, string path, string route) =>
        (await RouteAsync(method, path)).Should().Be(route);

    [Theory]
    [InlineData("/roles(0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70)/Microsoft.Dynamics.CRM.AddPrivilegesRole",
        "/{segment}/Microsoft.Dynamics.CRM.AddPrivilegesRole")]
    [InlineData("/roles(0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70)/Microsoft.Dynamics.CRM.RemovePrivilegeRole",
        "/{segment}/Microsoft.Dynamics.CRM.RemovePrivilegeRole")]
    public async Task Bound_actions_are_not_shadowed(string path, string route) =>
        (await RouteAsync("POST", path)).Should().Be(route);

    [Theory]
    [InlineData("GET", "/EntityDefinitions(LogicalName='account')/Attributes", "/EntityDefinitions({key})/Attributes")]
    [InlineData("GET", "/" + Record, "/{segment}")]
    [InlineData("PATCH", "/" + Record, "/{segment}")]
    [InlineData("POST", "/UploadBlock", "/UploadBlock")]
    [InlineData("POST", "/InitializeFileBlocksUpload", "/InitializeFileBlocksUpload")]
    [InlineData("POST", "/CommitFileBlocksUpload", "/CommitFileBlocksUpload")]
    [InlineData("POST", "/InitializeFileBlocksDownload", "/InitializeFileBlocksDownload")]
    [InlineData("POST", "/DownloadBlock", "/DownloadBlock")]
    [InlineData("POST", "/DeleteFile", "/DeleteFile")]
    public async Task Other_routes_keep_their_endpoints(string method, string path, string route) =>
        (await RouteAsync(method, path)).Should().Be(route);

    [Fact]
    public void The_upload_routes_take_128_MB_bodies()
    {
        var (app, _) = Host.Value;
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();
        long? Limit(string route, string method) => endpoints
            .Single(e => e.RoutePattern.RawText == Root + route && e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method))
            .Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>()?.MaxRequestBodySize;

        Limit("/{segment}/{property}", "PATCH").Should().Be(128L * 1024 * 1024);
        Limit("/UploadBlock", "POST").Should().Be(128L * 1024 * 1024);
        Limit("/{segment}/{property}", "DELETE").Should().BeNull("only the routes that carry a file take more than Kestrel's default");
    }

    // ---- headers --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("bytes 0-4194303/25870370", 0L, 4194303L, 25870370L)]
    [InlineData("bytes 25165824-25870369/25870370", 25165824L, 25870369L, 25870370L)]
    public void Content_range_is_read(string header, long from, long to, long total) =>
        FileEndpoints.ParseContentRange(header).Should().Be((from, to, total));

    [Theory]
    [InlineData("")]
    [InlineData("bytes 0-9/*")]
    [InlineData("bytes */100")]
    [InlineData("items 0-9/100")]
    [InlineData("bytes 9-0/100")]
    [InlineData("bytes 0-100/100")]
    public void Invalid_content_ranges_are_refused(string header)
    {
        var act = () => FileEndpoints.ParseContentRange(header);
        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.Message.Contains("Content-Range"));
    }

    [Theory]
    [InlineData("bytes=0-99", 0L, 99L)]
    [InlineData("bytes=100-", 100L, null)]
    public void Range_is_read(string header, long from, long? to) =>
        FileEndpoints.ParseRange(header).Should().Be((from, to));

    [Theory]
    [InlineData("bytes=-100")]
    [InlineData("bytes=0-1,5-6")]
    [InlineData("items=0-1")]
    [InlineData("bytes=5-1")]
    public void Unsupported_ranges_are_refused(string header)
    {
        var act = () => FileEndpoints.ParseRange(header);
        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.Message.Contains("Range"));
    }

    [Theory]
    [InlineData("report.txt", null, "text/plain")]
    [InlineData("25mb.pdf", null, "application/pdf")]
    [InlineData("photo.PNG", null, "image/png")]
    [InlineData("blob.zzzunknown", null, "application/octet-stream")]
    [InlineData("noextension", null, "application/octet-stream")]
    [InlineData("report.txt", "application/x-custom", "application/x-custom")]
    public void Mime_type_is_the_header_or_follows_the_file_name(string fileName, string? header, string expected) =>
        FileEndpoints.MimeType(fileName, header).Should().Be(expected);

    [Theory]
    [InlineData("application/json; IEEE754Compatible=true", true)]
    [InlineData("application/json;odata.metadata=minimal;ieee754compatible=TRUE", true)]
    [InlineData("application/json; IEEE754Compatible=\"true\"", true)]
    [InlineData("application/json; IEEE754Compatible=false", false)]
    [InlineData("application/json", false)]
    [InlineData(null, false)]
    public void IEEE754Compatible_is_read_from_the_content_type(string? contentType, bool expected) =>
        ActionController.IsIeee754Compatible(contentType).Should().Be(expected);

    [Fact]
    public void Int64_strings_need_IEEE754Compatible()
    {
        var json = JsonNode.Parse("""{ "Offset": "5000000000", "BlockLength": "-1", "FileContinuationToken": "t" }""")!.AsObject();
        var request = ActionController.ToRequest("DownloadBlock", json, new FakeStore(), ieee754Compatible: true);
        request["Offset"].Should().Be(5000000000L);
        request["BlockLength"].Should().Be(-1L);

        var act = () => ActionController.ToRequest("DownloadBlock", json, new FakeStore());
        act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message.Contains("'Offset'") && e.Message.Contains("IEEE754Compatible"));
        var notNumber = JsonNode.Parse("""{ "Offset": "x", "BlockLength": 1, "FileContinuationToken": "t" }""")!.AsObject();
        var bad = () => ActionController.ToRequest("DownloadBlock", notNumber, new FakeStore(), ieee754Compatible: true);
        bad.Should().Throw<XrmdFaultException>().Where(e => e.Message.Contains("'Offset'"));
    }

    [Fact]
    public void Chunk_block_ids_round_trip_and_are_not_mistaken_for_sdk_ids()
    {
        var id = FileColumnService.ChunkBlockId(8388608, 25870370);
        FileColumnService.TryReadChunkBlockId(id, out var offset, out var total).Should().BeTrue();
        (offset, total).Should().Be((8388608L, 25870370L));
        FileColumnService.TryReadChunkBlockId(Convert.ToBase64String(Guid.NewGuid().ToByteArray()), out _, out _).Should().BeFalse();
        FileColumnService.TryReadChunkBlockId(Convert.ToBase64String(new byte[24]), out _, out _).Should().BeFalse("the marker is missing");
        FileColumnService.TryReadChunkBlockId("not base64", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void File_name_header_must_be_ascii()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-ms-file-name"] = "résumé.txt";
        var act = () => FileEndpoints.FileName(context.Request);
        act.Should().Throw<XrmdFaultException>().Where(e => e.ErrorCode == ErrorCodes.InvalidArgument && e.Message.Contains("query"));

        context.Request.Headers.Remove("x-ms-file-name");
        context.Request.QueryString = new QueryString("?x-ms-file-name=r%C3%A9sum%C3%A9.txt");
        FileEndpoints.FileName(context.Request).Should().Be("résumé.txt");
    }

    [Fact]
    public void File_actions_become_the_sdk_requests()
    {
        var store = new FakeStore(new EntityMetadata { LogicalName = "account", EntitySetName = "accounts" });
        var id = Guid.Parse("0b6c7f2e-91d4-4f0e-8a11-2b3c4d5e6f70");
        OrganizationRequest Translate(string action, string json) => ActionController.ToRequest(action, JsonNode.Parse(json)!.AsObject(), store);

        var init = Translate("InitializeFileBlocksUpload", $$"""
            { "Target": { "accountid": "{{id}}", "@odata.type": "Microsoft.Dynamics.CRM.account" }, "FileName": "a.pdf", "FileAttributeName": "new_doc" }
            """);
        init["Target"].Should().BeEquivalentTo(new EntityReference("account", id));
        init["FileName"].Should().Be("a.pdf");
        init["FileAttributeName"].Should().Be("new_doc");

        var block = Translate("UploadBlock", """{ "BlockId": "QQ==", "BlockData": "AQID", "FileContinuationToken": "t" }""");
        block["BlockData"].Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        Translate("CommitFileBlocksUpload", """{ "BlockList": ["QQ=="], "FileName": "a", "MimeType": "b", "FileContinuationToken": "t" }""")["BlockList"]
            .Should().BeEquivalentTo(new[] { "QQ==" });
        var download = Translate("DownloadBlock", """{ "Offset": 5000000000, "BlockLength": 10, "FileContinuationToken": "t" }""");
        download["Offset"].Should().Be(5000000000L);
        download["BlockLength"].Should().Be(10L);
        Translate("DeleteFile", $$"""{ "FileId": "{{id}}" }""")["FileId"].Should().Be(id);

        foreach (var target in new[]
                 {
                     $$"""{ "accountid": "{{id}}" }""",
                     $$"""{ "@odata.type": "Microsoft.Dynamics.CRM.nosuch", "nosuchid": "{{id}}" }""",
                     """{ "@odata.type": "Microsoft.Dynamics.CRM.account" }""",
                     $$"""{ "@odata.type": "Microsoft.Dynamics.CRM.account", "accountid": "{{id}}", "name": "x" }""",
                     "\"x\"",
                 })
        {
            var act = () => Translate("InitializeFileBlocksDownload", $$"""{ "Target": {{target}}, "FileAttributeName": "new_doc" }""");
            act.Should().Throw<XrmdFaultException>().Where(e => e.HttpStatus == 400 && e.Message.Contains("Target"), target);
        }
    }
}

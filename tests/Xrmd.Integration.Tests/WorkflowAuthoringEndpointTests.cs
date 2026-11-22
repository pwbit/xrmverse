using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows.Authoring;
using Xrmd.Schema;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.21, Task 6 (A18): the authoring API <c>/authoring/workflows</c>, beside the Admin API. It requires a valid token and no role:
/// every operation is a pipeline request as the caller, so the workflow privileges decide (A17). Errors use the Web API envelope.
/// </summary>
[Collection("real-export")]
public sealed class WorkflowAuthoringEndpointTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Root = "/authoring/workflows";
    private const string Order = "xrmv_order";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public async Task InitializeAsync()
    {
        await AuthoringTestHelper.EnsureParityAsync(fx);
        await WorkflowTestHelper.ClearAsync(fx);
    }

    public Task DisposeAsync() => WorkflowTestHelper.ClearAsync(fx);

    private string Name(string what) => $"{what} {_run}";

    private string Realtime(string name) => $$"""
        kind: workflow
        name: {{name}}
        entity: {{Order}}
        mode: realtime
        trigger:
          create: true
        steps:
          - create: { entity: task, set: { subject: Hi, regardingobjectid: { record: primary } } }

        """;

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Guid? objectId, string? yaml = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (objectId is { } oid) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        if (yaml is not null) request.Content = new StringContent(yaml, Encoding.UTF8, "text/yaml");
        return fx.Client.SendAsync(request);
    }

    private async Task<(HttpStatusCode Status, string Text)> CallAsync(HttpMethod method, string path, Guid? objectId, string? yaml = null)
    {
        var response = await SendAsync(method, path, objectId, yaml);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<int?> StateAsync(Guid id) =>
        await AuthoringTestHelper.ScalarAsync(fx, "SELECT statecode FROM data.workflow WHERE workflowid = @id", ("@id", id)) is int state ? state : null;

    private Guid IdOf(string name) => WorkflowIdentity.IdOf(WorkflowKind.Workflow, Order, name);

    [Fact]
    public async Task Unauthenticated_is_401()
    {
        (await SendAsync(HttpMethod.Post, Root + "/apply", null, Realtime(Name("Anonymous")))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, Root, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Get, Root + "/anything", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, Root + "/anything/activate", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Apply_dry_run_is_the_default()
    {
        var (status, text) = await CallAsync(HttpMethod.Post, Root + "/apply", fx.AdminObjectId, Realtime(Name("Default dry")));

        status.Should().Be(HttpStatusCode.OK, text);
        var body = JsonNode.Parse(text)!.AsObject();
        body["id"]!.GetValue<Guid>().Should().Be(IdOf(Name("Default dry")));
        body["name"]!.GetValue<string>().Should().Be(Name("Default dry"));
        body["action"]!.GetValue<string>().Should().Be("created");
        body["state"]!.GetValue<string>().Should().Be("draft");
        body["stateChanged"]!.GetValue<bool>().Should().BeFalse();
        body["assumedShapes"]!.AsArray().Should().BeEmpty();
        body["errors"]!.AsArray().Should().BeEmpty();
        (await StateAsync(IdOf(Name("Default dry")))).Should().BeNull("a dry run writes nothing");

        (status, text) = await CallAsync(HttpMethod.Post, Root + "/apply?dryRun=false&activate=true", fx.AdminObjectId, Realtime(Name("Default dry")));
        status.Should().Be(HttpStatusCode.OK, text);
        JsonNode.Parse(text)!["state"]!.GetValue<string>().Should().Be("activated");
        (await StateAsync(IdOf(Name("Default dry")))).Should().Be(S.ActivatedState);

        (status, text) = await CallAsync(HttpMethod.Post, Root + "/apply?dryRun=maybe", fx.AdminObjectId, Realtime(Name("Default dry")));
        status.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("0x80040203").And.Contain("dryRun");
    }

    [Fact]
    public async Task Apply_errors_are_400_with_body()
    {
        var yaml = Realtime(Name("Errors")).Replace("subject: Hi", "subjet: Hi", StringComparison.Ordinal);

        var (status, text) = await CallAsync(HttpMethod.Post, Root + "/apply?dryRun=false", fx.AdminObjectId, yaml);

        status.Should().Be(HttpStatusCode.BadRequest, text);
        var body = JsonNode.Parse(text)!.AsObject();
        body["id"]!.GetValue<Guid>().Should().Be(IdOf(Name("Errors")));
        var error = body["errors"]!.AsArray().Should().ContainSingle().Subject!;
        error["message"]!.GetValue<string>().Should().Contain("subjet");
        error["at"]!["line"]!.GetValue<int>().Should().Be(8);
        (await StateAsync(IdOf(Name("Errors")))).Should().BeNull();
    }

    [Fact]
    public async Task Export_format_xaml_is_text_xml()
    {
        (await CallAsync(HttpMethod.Post, Root + "/apply?dryRun=false", fx.AdminObjectId, Realtime(Name("Exported")))).Status.Should().Be(HttpStatusCode.OK);

        var xaml = await SendAsync(HttpMethod.Get, $"{Root}/{Uri.EscapeDataString(Name("Exported"))}?format=xaml", fx.AdminObjectId);
        var text = await xaml.Content.ReadAsStringAsync();
        xaml.StatusCode.Should().Be(HttpStatusCode.OK, text);
        xaml.Content.Headers.ContentType!.MediaType.Should().Be("text/xml");
        text.Should().StartWith("<Activity");

        var yaml = await SendAsync(HttpMethod.Get, $"{Root}/{IdOf(Name("Exported"))}", fx.AdminObjectId);
        yaml.Content.Headers.ContentType!.MediaType.Should().Be("text/yaml");
        (await yaml.Content.ReadAsStringAsync()).Should().Contain($"id: {IdOf(Name("Exported")):D}").And.Contain($"name: {Name("Exported")}");

        var (status, error) = await CallAsync(HttpMethod.Get, $"{Root}/{Uri.EscapeDataString(Name("Nobody"))}", fx.AdminObjectId);
        status.Should().Be(HttpStatusCode.NotFound, error);
        error.Should().Contain("0x80040217");
        (await CallAsync(HttpMethod.Get, $"{Root}/{IdOf(Name("Exported"))}?format=json", fx.AdminObjectId)).Status.Should().Be(HttpStatusCode.BadRequest);

        var (listed, list) = await CallAsync(HttpMethod.Get, $"{Root}?kind=workflow&entity={Order}", fx.AdminObjectId);
        listed.Should().Be(HttpStatusCode.OK, list);
        JsonNode.Parse(list)!.AsArray().Should().Contain(n => n!["id"]!.GetValue<Guid>() == IdOf(Name("Exported")) && n["mode"]!.GetValue<string>() == "realtime");
    }

    [Fact]
    public async Task Basic_user_without_workflow_privileges_is_403()
    {
        var (_, objectId) = await AuthoringTestHelper.UserWithRoleAndObjectIdAsync(fx, StandardRoles.BasicUser);

        var (status, text) = await CallAsync(HttpMethod.Post, Root + "/apply?dryRun=false", objectId, Realtime(Name("Basic")));

        status.Should().Be(HttpStatusCode.Forbidden, text);
        text.Should().Contain("0x80040220");
        (await StateAsync(IdOf(Name("Basic")))).Should().BeNull();
        (await CallAsync(HttpMethod.Get, Root, objectId)).Status.Should().Be(HttpStatusCode.Forbidden);
        // Fix round 1: a dry run demands prvReadWorkflow too.
        (status, text) = await CallAsync(HttpMethod.Post, Root + "/apply", objectId, Realtime(Name("Basic")));
        status.Should().Be(HttpStatusCode.Forbidden, text);
        text.Should().Contain("0x80040220").And.Contain("prvReadWorkflow");
    }

    [Fact]
    public async Task Oversized_body_is_400()
    {
        // Fix round 1, minor 2: the body is read through a bound of WorkflowYaml.MaxBytes.
        var yaml = Realtime(Name("Large")) + "#" + new string('x', WorkflowYaml.MaxBytes) + "\n";

        var (status, text) = await CallAsync(HttpMethod.Post, Root + "/apply", fx.AdminObjectId, yaml);

        status.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("0x80040203").And.Contain("larger than");
    }

    [Fact]
    public async Task No_admin_role_needed_with_privileges()
    {
        // A custom role with the workflow privileges (and the activation privilege of a real-time workflow): no administrator role.
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, team: false, ("prvCreateWorkflow", DepthMask.Global), ("prvReadWorkflow", DepthMask.Global),
            ("prvWriteWorkflow", DepthMask.Global), ("prvActivateSynchronousWorkflow", DepthMask.Global));

        var (status, text) = await CallAsync(HttpMethod.Post, Root + "/apply?dryRun=false&activate=true", objectId, Realtime(Name("Author")));

        status.Should().Be(HttpStatusCode.OK, text);
        (await StateAsync(IdOf(Name("Author")))).Should().Be(S.ActivatedState);
        (status, text) = await CallAsync(HttpMethod.Post, $"{Root}/{IdOf(Name("Author"))}/deactivate", objectId);
        status.Should().Be(HttpStatusCode.OK, text);
        JsonNode.Parse(text)!["state"]!.GetValue<string>().Should().Be("draft");
        (await StateAsync(IdOf(Name("Author")))).Should().Be(S.DraftState);
        (status, text) = await CallAsync(HttpMethod.Post, $"{Root}/{Uri.EscapeDataString(Name("Author"))}/activate", objectId);
        status.Should().Be(HttpStatusCode.OK, text);
        (await StateAsync(IdOf(Name("Author")))).Should().Be(S.ActivatedState);
        // The Admin API stays closed to this user.
        (await CallAsync(HttpMethod.Get, "/admin/health", objectId)).Status.Should().Be(HttpStatusCode.Forbidden);
    }
}

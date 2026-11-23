using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.11, task 6 (A17): SendEmail changes the email's state. IssueSend true leaves the email Pending Send; since plan 4.1b it also
/// queues the send job (<see cref="EmailSendTests"/>), which these tests do not run. It runs as a core message: through the pipeline
/// (its own plug-in steps, not the Update steps), the SDK, SOAP and the Web API bound action.
/// </summary>
[Collection("xrmd")]
public sealed class SendEmailTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        // Plan 4.1b: the send jobs these tests queue are not run.
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.asyncoperation WHERE operationtype = {Xrmd.Pipeline.Async.AsyncOperationQueue.EmailSend}");
    }

    // ---- helpers ----

    private async Task<EntityReference> ContactAsync(string first) =>
        new("contact", await Admin.CreateAsync(new Entity("contact")
        {
            ["firstname"] = first, ["lastname"] = "Example", ["emailaddress1"] = $"{first.ToLowerInvariant()}.{_run}@example.com",
        }));

    private static EntityCollection Parties(params EntityReference[] parties) =>
        new([.. parties.Select(p => new Entity("activityparty") { ["partyid"] = p })]);

    private async Task<Guid> DraftAsync(OrganizationService? service = null, bool withRecipient = true)
    {
        var email = new Entity("email") { ["subject"] = "Welcome " + _run };
        if (withRecipient) email["to"] = Parties(await ContactAsync("Ada"));
        return await (service ?? Admin).CreateAsync(email);
    }

    private static SendEmailRequest Send(Guid id, bool issueSend, string? token = null)
    {
        var request = new SendEmailRequest { EmailId = id, IssueSend = issueSend };
        if (token is not null) request.TrackingToken = token;
        return request;
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<Guid> AdminIdAsync() =>
        (Guid)(await ScalarAsync("SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid", ("@oid", fx.AdminObjectId)))!;

    private async Task<Guid> StepAsync(string plugin, string message, string entity, int stage, string? config = null)
    {
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins." + plugin);
        return await PluginTestHelper.RegisterStepAsync(Admin, type, message, entity, stage, config: config);
    }

    private async Task<List<string>> RecordedNamesAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_name FROM data.{TestEntities.PipeLog}");
        var names = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return names;
    }

    private async Task<HttpResponseMessage> HttpAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    // ---- state ----

    [Fact]
    public async Task IssueSend_false_marks_sent()
    {
        var id = await DraftAsync();
        var before = DateTime.UtcNow.AddSeconds(-5);

        var response = await Admin.ExecuteAsync(Send(id, issueSend: false));

        response.Should().BeOfType<SendEmailResponse>().Which.Subject.Should().Be("Welcome " + _run);
        var email = await Admin.RetrieveAsync("email", id, new ColumnSet("statecode", "statuscode", "senton", "directioncode", "actualend"));
        email.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        email.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(3);
        email.GetAttributeValue<DateTime?>("senton").Should().NotBeNull().And.BeOnOrAfter(before);
        email.GetAttributeValue<bool>("directioncode").Should().BeTrue();
        // The pointer follows in the same transaction (A1).
        var pointer = await Admin.RetrieveAsync("activitypointer", id, new ColumnSet("statecode", "statuscode"));
        pointer.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        pointer.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(3);
    }

    [Fact]
    public async Task IssueSend_true_without_provider_is_pending_send()
    {
        var id = await DraftAsync();

        var response = await Admin.ExecuteAsync(Send(id, issueSend: true));

        ((SendEmailResponse)response).Subject.Should().Be("Welcome " + _run);
        var email = await Admin.RetrieveAsync("email", id, new ColumnSet("statecode", "statuscode", "senton"));
        email.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1);
        email.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(6);
        email.GetAttributeValue<DateTime?>("senton").Should().BeNull("nothing was sent");
        (await ScalarAsync("SELECT statuscode FROM data.activitypointer WHERE activityid = @id", ("@id", id))).Should().Be(6);
    }

    [Fact]
    public async Task Tracking_token_is_stored()
    {
        var id = await DraftAsync();

        await Admin.ExecuteAsync(Send(id, issueSend: false, token: "XV:0001:" + _run));

        (await Admin.RetrieveAsync("email", id, new ColumnSet("trackingtoken"))).GetAttributeValue<string>("trackingtoken")
            .Should().Be("XV:0001:" + _run);
    }

    [Fact]
    public async Task From_defaults_to_the_impersonated_caller()
    {
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        await GrantAsync(fx, user, team: false, ("prvCreateActivity", DepthMask.Basic), ("prvReadActivity", DepthMask.Basic),
            ("prvWriteActivity", DepthMask.Basic), ("prvReadContact", DepthMask.Global));
        var asUser = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId, callerId: user);
        var id = await DraftAsync(asUser);

        await asUser.ExecuteAsync(Send(id, issueSend: false));

        var from = (await Admin.RetrieveAsync("email", id, new ColumnSet("from"))).GetAttributeValue<EntityCollection>("from");
        from.Entities.Should().ContainSingle().Which.GetAttributeValue<EntityReference>("partyid")
            .Should().Match<EntityReference>(p => p.LogicalName == "systemuser" && p.Id == user);
    }

    [Fact]
    public async Task From_already_set_is_kept()
    {
        // Ruling Z1: a sender already set must be the caller's own user.
        var ada = await ContactAsync("Ada");
        var me = new EntityReference("systemuser", await AdminIdAsync());
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "From " + _run, ["from"] = Parties(me), ["to"] = Parties(ada) });
        var before = await ScalarAsync("SELECT activitypartyid FROM data.activityparty WHERE activityid = @id AND participationtypemask = 1", ("@id", id));

        await Admin.ExecuteAsync(Send(id, issueSend: false));

        var from = (await Admin.RetrieveAsync("email", id, new ColumnSet("from"))).GetAttributeValue<EntityCollection>("from");
        from.Entities.Should().ContainSingle().Which.GetAttributeValue<EntityReference>("partyid").Id.Should().Be(me.Id);
        (await ScalarAsync("SELECT activitypartyid FROM data.activityparty WHERE activityid = @id AND participationtypemask = 1", ("@id", id)))
            .Should().Be(before, "the sender row is kept, not rewritten");
    }

    [Fact]
    public async Task From_another_user_is_refused()
    {
        var ada = await ContactAsync("Ada");
        var other = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx));
        var id = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = "Send as " + _run, ["from"] = Parties(new EntityReference("systemuser", other)), ["to"] = Parties(ada),
        });

        var fault = await FaultAsync(() => Admin.ExecuteAsync(Send(id, issueSend: true)));

        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain($"systemuser {other}").And.Contain("on behalf of another user").And.Contain("not supported");
        (await ScalarAsync("SELECT statecode FROM data.email WHERE activityid = @id", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task From_contact_is_refused()
    {
        var ada = await ContactAsync("Ada");
        var ben = await ContactAsync("Ben");
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "From contact " + _run, ["from"] = Parties(ben), ["to"] = Parties(ada) });

        var fault = await FaultAsync(() => Admin.ExecuteAsync(Send(id, issueSend: false)));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain($"contact {ben.Id}").And.Contain("not a user");
        (await ScalarAsync("SELECT statecode FROM data.email WHERE activityid = @id", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task Pending_send_of_a_failed_email_clears_senton()
    {
        // Review M3: a Failed email (state 0, status 8) keeps the senton of an earlier attempt until it is sent again.
        var id = await DraftAsync();
        await ScalarAsync("UPDATE data.email SET statuscode = 8, senton = '2026-01-02T03:04:05' WHERE activityid = @id", ("@id", id));

        await Admin.ExecuteAsync(Send(id, issueSend: true));

        var email = await Admin.RetrieveAsync("email", id, new ColumnSet("statuscode", "senton"));
        email.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(6);
        email.GetAttributeValue<DateTime?>("senton").Should().BeNull();
    }

    // ---- refusals ----

    [Fact]
    public async Task Send_of_completed_email_is_400()
    {
        var id = await DraftAsync();
        await Admin.ExecuteAsync(Send(id, issueSend: false));

        var fault = await FaultAsync(() => Admin.ExecuteAsync(Send(id, issueSend: false)));

        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Contain(id.ToString()).And.Contain("statecode 1");
    }

    [Fact]
    public async Task Send_without_recipients_is_400()
    {
        var ada = await ContactAsync("Ada");
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = "Lonely " + _run, ["related"] = Parties(ada) });

        var fault = await FaultAsync(() => Admin.ExecuteAsync(Send(id, issueSend: true)));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("recipient");
        (await ScalarAsync("SELECT statecode FROM data.email WHERE activityid = @id", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task Unknown_email_is_404()
    {
        var fault = await FaultAsync(() => Admin.ExecuteAsync(Send(Guid.NewGuid(), issueSend: false)));

        fault.HttpStatus.Should().Be(404);
        fault.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);
    }

    [Fact]
    public async Task Missing_EmailId_is_400()
    {
        var fault = await FaultAsync(() => Admin.ExecuteAsync(new OrganizationRequest("SendEmail") { ["IssueSend"] = false }));

        fault.HttpStatus.Should().Be(400);
        fault.Message.Should().Contain("EmailId");
    }

    [Fact]
    public async Task Send_needs_write_on_the_email()
    {
        var id = await DraftAsync();
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ("prvReadActivity", DepthMask.Global));

        var fault = await FaultAsync(() => ServiceFor(fx, oid).ExecuteAsync(Send(id, issueSend: false)));

        fault.HttpStatus.Should().Be(403);
        (await ScalarAsync("SELECT statecode FROM data.email WHERE activityid = @id", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task Send_needs_write_at_the_depth_of_the_email()
    {
        // Basic Write and Read on activities, but the email is the admin's: AccessDenied on the record.
        var id = await DraftAsync();
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ("prvReadActivity", DepthMask.Global), ("prvWriteActivity", DepthMask.Basic));

        var fault = await FaultAsync(() => ServiceFor(fx, oid).ExecuteAsync(Send(id, issueSend: false)));

        fault.HttpStatus.Should().Be(403);
        fault.ErrorCode.Should().Be(ErrorCodes.AccessDenied);
        (await ScalarAsync("SELECT statecode FROM data.email WHERE activityid = @id", ("@id", id))).Should().Be(0);
    }

    // ---- pipeline ----

    [Fact]
    public async Task Images_of_a_SendEmail_step_hold_the_email_before_and_after()
    {
        // Review I2 (ruling Z2): SendEmail steps get pre and post images of the email named by EmailId.
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        var step = await StepAsync("ContextRecorderPlugin", "SendEmail", "email", 40, TestEntities.PipeLog);
        await PluginTestHelper.RegisterImageAsync(Admin, step, 0, "pre", "statecode,statuscode,from,ownerid");
        await PluginTestHelper.RegisterImageAsync(Admin, step, 1, "post", "statecode,statuscode,from,ownerid");
        var id = await DraftAsync();
        var me = await AdminIdAsync();

        await Admin.ExecuteAsync(Send(id, issueSend: false));

        var lines = ((string)(await ScalarAsync($"SELECT pb_data FROM data.{TestEntities.PipeLog} WHERE pb_name = 'SendEmail|40|0|1'"))!).Split('\n');
        lines.Should().Contain("pre=pre:from,ownerid,statecode,statuscode").And.Contain("post=post:from,ownerid,statecode,statuscode");
        lines.Should().Contain($"preowner={me}").And.Contain($"postowner={me}");
        // The sender filled by the send is in the post image only.
        lines.Should().Contain($"postcollections=post:from:1:{me}");
    }


    [Fact]
    public async Task Plugin_step_on_SendEmail_fires()
    {
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        await StepAsync("ContextRecorderPlugin", "SendEmail", "email", 20, TestEntities.PipeLog);
        await StepAsync("ContextRecorderPlugin", "SendEmail", "email", 40, TestEntities.PipeLog);
        var id = await DraftAsync();

        await Admin.ExecuteAsync(Send(id, issueSend: false));

        (await RecordedNamesAsync()).Should().Contain(["SendEmail|20|0|1", "SendEmail|40|0|1"]);
        var post = (string)(await ScalarAsync($"SELECT pb_data FROM data.{TestEntities.PipeLog} WHERE pb_name = 'SendEmail|40|0|1'"))!;
        post.Split('\n').Should().Contain("output=Subject");
    }

    [Fact]
    public async Task Update_steps_do_not_fire_on_SendEmail()
    {
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");
        await StepAsync("ContextRecorderPlugin", "Update", "email", 20, TestEntities.PipeLog);
        await StepAsync("ContextRecorderPlugin", "Update", "email", 40, TestEntities.PipeLog);
        var id = await DraftAsync();

        await Admin.ExecuteAsync(Send(id, issueSend: false));

        (await RecordedNamesAsync()).Should().NotContain(n => n.StartsWith("Update|", StringComparison.Ordinal));
        (await ScalarAsync("SELECT statuscode FROM data.email WHERE activityid = @id", ("@id", id))).Should().Be(3);
    }

    [Fact]
    public async Task Failing_post_operation_rolls_the_send_back()
    {
        await StepAsync("ThrowPlugin", "SendEmail", "email", 40, "invalid:refused after the send");
        var id = await DraftAsync();

        await FaultAsync(() => Admin.ExecuteAsync(Send(id, issueSend: false)));

        (await ScalarAsync("SELECT statecode FROM data.email WHERE activityid = @id", ("@id", id))).Should().Be(0);
        (await ScalarAsync("SELECT statecode FROM data.activitypointer WHERE activityid = @id", ("@id", id))).Should().Be(0);
        (await ScalarAsync("SELECT COUNT(*) FROM data.activityparty WHERE activityid = @id AND participationtypemask = 1", ("@id", id)))
            .Should().Be(0);
    }

    // ---- Web API ----

    [Fact]
    public async Task Web_api_refused_function_and_bound_action_are_400()
    {
        var function = await HttpAsync(HttpMethod.Get, $"Rollup(Target=@t)?@t={{'@odata.id':'accounts({Guid.NewGuid()})'}}");
        var body = await function.Content.ReadAsStringAsync();
        function.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["error"]!["message"]!.GetValue<string>().Should().Contain("'Rollup'").And.Contain("not supported");

        var bound = await HttpAsync(HttpMethod.Post, $"appointments({Guid.NewGuid()})/Microsoft.Dynamics.CRM.AddRecurrence", "{}");
        body = await bound.Content.ReadAsStringAsync();
        bound.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["error"]!["message"]!.GetValue<string>().Should().Contain("'AddRecurrence'").And.Contain("not supported");
    }

    [Fact]
    public async Task Post_bound_SendEmail_returns_subject()
    {
        var id = await DraftAsync();

        var res = await HttpAsync(HttpMethod.Post, $"emails({id})/Microsoft.Dynamics.CRM.SendEmail",
            """{"IssueSend":false,"TrackingToken":"XV:web"}""");

        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var json = JsonNode.Parse(body)!.AsObject();
        json["Subject"]!.GetValue<string>().Should().Be("Welcome " + _run);
        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.SendEmailResponse");
        var email = await Admin.RetrieveAsync("email", id, new ColumnSet("statuscode", "trackingtoken"));
        email.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(3);
        email.GetAttributeValue<string>("trackingtoken").Should().Be("XV:web");
    }

    [Fact]
    public async Task Web_api_SendEmail_refuses_bad_requests()
    {
        var id = await DraftAsync();

        var unknown = await HttpAsync(HttpMethod.Post, $"emails({id})/Microsoft.Dynamics.CRM.SendEmail", """{"IssueSend":false,"Other":1}""");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest, await unknown.Content.ReadAsStringAsync());
        var missing = await HttpAsync(HttpMethod.Post, $"emails({id})/Microsoft.Dynamics.CRM.SendEmail", "{}");
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest, await missing.Content.ReadAsStringAsync());
        var wrongTable = await HttpAsync(HttpMethod.Post, $"tasks({id})/Microsoft.Dynamics.CRM.SendEmail", """{"IssueSend":false}""");
        wrongTable.StatusCode.Should().Be(HttpStatusCode.NotFound, await wrongTable.Content.ReadAsStringAsync());
        var gone = await HttpAsync(HttpMethod.Post, $"emails({Guid.NewGuid()})/Microsoft.Dynamics.CRM.SendEmail", """{"IssueSend":false}""");
        gone.StatusCode.Should().Be(HttpStatusCode.NotFound, await gone.Content.ReadAsStringAsync());
        (await ScalarAsync("SELECT statecode FROM data.email WHERE activityid = @id", ("@id", id))).Should().Be(0);
    }

    [Fact]
    public async Task Metadata_declares_the_bound_SendEmail_action()
    {
        var res = await HttpAsync(HttpMethod.Get, "$metadata");
        var xml = await res.Content.ReadAsStringAsync();

        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var action = doc.Descendants().Single(e => e.Name.LocalName == "Action" && (string?)e.Attribute("Name") == "SendEmail");
        ((string?)action.Attribute("IsBound")).Should().Be("true");
        action.Elements().Where(e => e.Name.LocalName == "Parameter").Select(e => ((string?)e.Attribute("Name"), (string?)e.Attribute("Type")))
            .Should().Equal(("entity", "Microsoft.Dynamics.CRM.email"), ("IssueSend", "Edm.Boolean"), ("TrackingToken", "Edm.String"));
        action.Elements().Single(e => e.Name.LocalName == "ReturnType").Attribute("Type")!.Value
            .Should().Be("Microsoft.Dynamics.CRM.SendEmailResponse");
    }

    [Fact]
    public async Task Web_api_refused_activity_action_is_400()
    {
        var res = await HttpAsync(HttpMethod.Post, "Book", """{"Target":{}}""");

        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonNode.Parse(body)!["error"]!["message"]!.GetValue<string>().Should().Contain("Book").And.Contain("not supported");
    }

    // ---- catalog ----

    [Fact]
    public async Task SendEmail_has_a_message_and_an_email_filter()
    {
        (await ScalarAsync("SELECT name FROM data.sdkmessage WHERE sdkmessageid = @id", ("@id", SdkMessageCatalog.MessageId("SendEmail"))))
            .Should().Be("SendEmail");
        (await ScalarAsync("SELECT primaryobjecttypecode FROM data.sdkmessagefilter WHERE sdkmessagefilterid = @id",
            ("@id", SdkMessageCatalog.FilterId("SendEmail", "email")))).Should().Be("email");
        (await ScalarAsync("SELECT COUNT(*) FROM data.sdkmessagefilter WHERE sdkmessageid = @id",
            ("@id", SdkMessageCatalog.MessageId("SendEmail")))).Should().Be(1, "SendEmail is bound to email only");
    }
}

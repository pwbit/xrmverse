using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.15, task 5: duplicate detection in the Web API (rulings D1, D10, D11, D12, D14, D15): the header
/// <c>MSCRM.SuppressDuplicateDetection</c> on POST and PATCH, the bound action <c>PublishDuplicateRule</c>, the unbound action
/// <c>UnpublishDuplicateRule</c>, the function <c>RetrieveDuplicates</c> and the action <c>BulkDetectDuplicates</c>, on a rule
/// contact → contact on <c>emailaddress1</c> (Exact Match).
/// </summary>
[Collection("xrmd")]
public sealed class DuplicateDetectionWebApiTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Ns = "Microsoft.Dynamics.CRM.";
    private const string Contact = DuplicateDetectionTestKit.Contact;
    private const string Email = DuplicateDetectionTestKit.Email;
    private const string Header = "MSCRM.SuppressDuplicateDetection";

    private readonly DuplicateDetectionTestKit _kit = new(fx);

    private OrganizationService Admin => _kit.Admin;

    public Task InitializeAsync() => _kit.InitializeAsync(onlineCreateUpdate: true, Contact);

    public async Task DisposeAsync()
    {
        await _kit.ExecAsync("DELETE FROM data.duplicaterecord; DELETE FROM data.asyncoperation WHERE operationtype = 8");
        await _kit.DisposeAsync();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private string Mail(string label) => _kit.Mail(label);

    private Task<Guid> ContactAsync(string label, string? email) =>
        Admin.CreateAsync(new Entity(Contact) { ["lastname"] = label + " " + _kit.Run, [Email] = email });

    private async Task<Guid> PublishedRuleAsync()
    {
        var rule = await _kit.RuleAsync();
        await _kit.PublishAndRunAsync(rule);
        return rule;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body = null, string? suppress = null,
        Guid? objectId = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
        if (suppress is not null) req.Headers.TryAddWithoutValidation(Header, suppress);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private JsonObject ContactBody(string label, string email) => new() { ["lastname"] = label + " " + _kit.Run, [Email] = email };

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private async Task<int> ContactCountAsync(string email) =>
        Convert.ToInt32(await _kit.ScalarAsync("SELECT COUNT(*) FROM data.contact WHERE emailaddress1 = @e", ("@e", email)));

    // ---- the header -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Post_with_header_false_and_duplicate_is_412_0x80040333()
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();

        var json = await JsonAsync(await SendAsync(HttpMethod.Post, "contacts", ContactBody("Copy", Mail("anna")), "false"),
            HttpStatusCode.PreconditionFailed);

        json["error"]!["code"]!.GetValue<string>().Should().Be("0x80040333");
        json["error"]!["message"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        (await ContactCountAsync(Mail("anna"))).Should().Be(1);
    }

    /// <summary>
    /// Plan 2.7b, Task 4: a case-insensitive rule on the name flags <c>ACME </c> (upper case, a trailing space) against <c>Acme</c>
    /// on both engines, as SQL Server's <c>_CI_AS</c> equality would.
    /// </summary>
    [Fact]
    public async Task Case_insensitive_rule_flags_upper_case_name_with_trailing_space()
    {
        var name = "Acme " + _kit.Run;
        await Admin.CreateAsync(new Entity(Contact) { ["lastname"] = name });
        var rule = await _kit.RuleAsync(attribute: "lastname");
        await _kit.PublishAndRunAsync(rule);

        var json = await JsonAsync(await SendAsync(HttpMethod.Post, "contacts",
                new JsonObject { ["lastname"] = name.ToUpperInvariant() + " " }, "false"),
            HttpStatusCode.PreconditionFailed);

        json["error"]!["code"]!.GetValue<string>().Should().Be("0x80040333");
        Convert.ToInt32(await _kit.ScalarAsync("SELECT COUNT(*) FROM data.contact WHERE lastname = @n", ("@n", name))).Should().Be(1);
    }

    [Fact]
    public async Task Post_with_header_true_creates()
    {
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();

        foreach (var value in new[] { "true", "TRUE" })
        {
            var response = await SendAsync(HttpMethod.Post, "contacts", ContactBody("Copy", Mail("anna")), value);
            response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        }
        // Without the header the check is suppressed too (Learn: the default).
        (await SendAsync(HttpMethod.Post, "contacts", ContactBody("Copy", Mail("anna")))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ContactCountAsync(Mail("anna"))).Should().Be(4);
    }

    [Fact]
    public async Task Patch_with_header_false_and_duplicate_is_412()
    {
        await ContactAsync("Anna", Mail("anna"));
        var other = await ContactAsync("Other", Mail("other"));
        await PublishedRuleAsync();

        var json = await JsonAsync(
            await SendAsync(HttpMethod.Patch, $"contacts({other})", new JsonObject { [Email] = Mail("anna") }, "False"),
            HttpStatusCode.PreconditionFailed);
        json["error"]!["code"]!.GetValue<string>().Should().Be("0x80040333");
        (await ContactCountAsync(Mail("anna"))).Should().Be(1, "the update was refused");

        (await SendAsync(HttpMethod.Patch, $"contacts({other})", new JsonObject { [Email] = Mail("anna") }, "true"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ContactCountAsync(Mail("anna"))).Should().Be(2);
    }

    [Fact]
    public async Task Invalid_header_value_is_400()
    {
        var other = await ContactAsync("Other", Mail("other"));
        foreach (var value in new[] { "yes", "1", "true, false" })
        {
            var post = await JsonAsync(await SendAsync(HttpMethod.Post, "contacts", ContactBody("Bad", Mail("bad")), value),
                HttpStatusCode.BadRequest);
            post["error"]!["message"]!.GetValue<string>().Should().Contain(Header);
            var patch = await JsonAsync(await SendAsync(HttpMethod.Patch, $"contacts({other})", new JsonObject { ["firstname"] = "x" }, value),
                HttpStatusCode.BadRequest);
            patch["error"]!["message"]!.GetValue<string>().Should().Contain(Header);
        }
        (await ContactCountAsync(Mail("bad"))).Should().Be(0);
    }

    [Fact]
    public async Task Batch_requests_read_their_own_header_not_the_batchs()
    {
        // #130: Learn, the batch's headers do not apply to its requests; each request of a $batch reads its own header.
        await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();
        string Part(string? suppress) =>
            "--batch_dd\r\nContent-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n" +
            "POST contacts HTTP/1.1\r\nContent-Type: application/json\r\n" + (suppress is null ? "" : $"{Header}: {suppress}\r\n") + "\r\n" +
            ContactBody("Copy", Mail("anna")).ToJsonString() + "\r\n";
        var req = new HttpRequestMessage(HttpMethod.Post, Api + "$batch");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        req.Headers.TryAddWithoutValidation(Header, "false");
        req.Headers.TryAddWithoutValidation("Prefer", "odata.continue-on-error");
        req.Content = new StringContent(Part(null) + Part("false") + "--batch_dd--\r\n", Encoding.UTF8);
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/mixed; boundary=batch_dd");

        var response = await fx.Client.SendAsync(req);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        Regex.Matches(text, @"HTTP/1\.1 (\d{3})").Select(m => m.Groups[1].Value).Should().Equal("204", "412");
        (await ContactCountAsync(Mail("anna"))).Should().Be(2);
    }

    // ---- PublishDuplicateRule / UnpublishDuplicateRule ----------------------------------------------------------------

    [Fact]
    public async Task Publish_bound_action_returns_asyncoperation()
    {
        var rule = await _kit.RuleAsync();

        var json = await JsonAsync(await SendAsync(HttpMethod.Post, $"duplicaterules({rule})/{Ns}PublishDuplicateRule"), HttpStatusCode.OK);

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#asyncoperations/$entity");
        var job = Guid.Parse(json["asyncoperationid"]!.GetValue<string>());
        json["operationtype"]!.GetValue<int>().Should().Be(7);
        (await _kit.ScalarAsync("SELECT statuscode FROM data.duplicaterule WHERE duplicateruleid = @id", ("@id", rule))).Should().Be(1, "Publishing");
        await _kit.RunJobAsync(job);
        (await _kit.ScalarAsync("SELECT statuscode FROM data.duplicaterule WHERE duplicateruleid = @id", ("@id", rule))).Should().Be(2, "Published");

        // Bound to duplicaterule only; an unknown parameter is refused.
        (await SendAsync(HttpMethod.Post, $"contacts({rule})/{Ns}PublishDuplicateRule")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, $"duplicaterules({rule})/{Ns}PublishDuplicateRule", new JsonObject { ["DuplicateRuleId"] = rule.ToString() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Publish_without_asyncoperation_read_returns_the_job_id()
    {
        var rule = await _kit.RuleAsync();
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ("prvReadDuplicateRule", DepthMask.Global), ("prvWriteDuplicateRule", DepthMask.Global));

        var json = await JsonAsync(await SendAsync(HttpMethod.Post, $"duplicaterules({rule})/{Ns}PublishDuplicateRule", objectId: oid),
            HttpStatusCode.OK);

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#asyncoperations/$entity");
        var job = Guid.Parse(json["asyncoperationid"]!.GetValue<string>());
        json["operationtype"]!.GetValue<int>().Should().Be(7);
        (await _kit.ScalarAsync("SELECT operationtype FROM data.asyncoperation WHERE asyncoperationid = @id AND regardingobjectid = @rule",
            ("@id", job), ("@rule", rule))).Should().Be(7, "the body carries the id of the job the publish queued");
        // The user cannot read the job: the body was not read back.
        (await SendAsync(HttpMethod.Get, $"asyncoperations({job})", objectId: oid)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _kit.RunJobAsync(job);
    }

    [Fact]
    public async Task Unpublish_returns_204()
    {
        var rule = await PublishedRuleAsync();

        var response = await SendAsync(HttpMethod.Post, "UnpublishDuplicateRule", new JsonObject { ["DuplicateRuleId"] = rule.ToString() });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        (await _kit.ScalarAsync("SELECT statuscode FROM data.duplicaterule WHERE duplicateruleid = @id", ("@id", rule))).Should().Be(0);
        (await SendAsync(HttpMethod.Post, "UnpublishDuplicateRule", new JsonObject { ["RuleId"] = rule.ToString() }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "UnpublishDuplicateRule", new JsonObject { ["DuplicateRuleId"] = "nope" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- RetrieveDuplicates -------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetrieveDuplicates_function_with_aliases()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        var copy = await ContactAsync("Copy", Mail("anna"));
        await ContactAsync("Other", Mail("other"));
        await PublishedRuleAsync();

        // Learn's request: single-quoted JSON in the aliases, only the id in the record.
        var p1 = Uri.EscapeDataString($"{{'@odata.type':'Microsoft.Dynamics.CRM.contact','contactid':'{anna}'}}");
        var p3 = Uri.EscapeDataString("{'PageNumber':1,'Count':50}");
        var json = await JsonAsync(await SendAsync(HttpMethod.Get,
            $"RetrieveDuplicates(BusinessEntity=@p1,MatchingEntityName=@p2,PagingInfo=@p3)?@p1={p1}&@p2='contact'&@p3={p3}"), HttpStatusCode.OK);

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#contacts");
        var value = json["value"]!.AsArray();
        value.Select(v => Guid.Parse(v!["contactid"]!.GetValue<string>())).Should().Equal(copy);
        value[0]!["lastname"]!.GetValue<string>().Should().Be("Copy " + _kit.Run, "the matches carry all columns");
        json.ContainsKey("@Microsoft.Dynamics.CRM.morerecords").Should().BeFalse("no page follows");
    }

    [Fact]
    public async Task RetrieveDuplicates_says_when_more_records_exist()
    {
        await ContactAsync("Anna", Mail("anna"));
        await ContactAsync("Copy", Mail("anna"));
        await PublishedRuleAsync();
        var entity = Uri.EscapeDataString($"{{'@odata.type':'Microsoft.Dynamics.CRM.contact','{Email}':'{Mail("anna")}'}}");

        async Task<JsonObject> PageAsync(int page) => await JsonAsync(await SendAsync(HttpMethod.Get,
            $"RetrieveDuplicates(BusinessEntity=@p1,MatchingEntityName='contact',PagingInfo=@p3)?@p1={entity}&@p3="
            + Uri.EscapeDataString($"{{'PageNumber':{page},'Count':1}}")), HttpStatusCode.OK);

        var first = await PageAsync(1);
        first["value"]!.AsArray().Should().HaveCount(1);
        first["@Microsoft.Dynamics.CRM.morerecords"]!.GetValue<bool>().Should().BeTrue();
        var second = await PageAsync(2);
        second["value"]!.AsArray().Should().HaveCount(1);
        second.ContainsKey("@Microsoft.Dynamics.CRM.morerecords").Should().BeFalse("the last page");
    }

    [Fact]
    public async Task RetrieveDuplicates_inline_parameters()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        await PublishedRuleAsync();

        var entity = Uri.EscapeDataString($"{{\"@odata.type\":\"Microsoft.Dynamics.CRM.contact\",\"{Email}\":\"{Mail("anna")}\"}}");
        var paging = Uri.EscapeDataString("{\"PageNumber\":1,\"Count\":10}");
        var json = await JsonAsync(await SendAsync(HttpMethod.Get,
            $"RetrieveDuplicates(BusinessEntity={entity},MatchingEntityName='contact',PagingInfo={paging})"), HttpStatusCode.OK);
        json["value"]!.AsArray().Select(v => Guid.Parse(v!["contactid"]!.GetValue<string>())).Should().Equal(anna);

        // PagingInfo is nullable; an entity without @odata.type, an unknown parameter and a missing one are refused.
        (await JsonAsync(await SendAsync(HttpMethod.Get, $"RetrieveDuplicates(BusinessEntity={entity},MatchingEntityName='contact')"),
            HttpStatusCode.OK))["value"]!.AsArray().Should().HaveCount(1);
        var untyped = Uri.EscapeDataString($"{{\"{Email}\":\"x\"}}");
        (await SendAsync(HttpMethod.Get, $"RetrieveDuplicates(BusinessEntity={untyped},MatchingEntityName='contact')"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, $"RetrieveDuplicates(BusinessEntity={entity},MatchingEntityName='contact',Other=1)"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "RetrieveDuplicates(MatchingEntityName='contact')")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var tooBig = Uri.EscapeDataString("{\"PageNumber\":1,\"Count\":5001}");
        (await SendAsync(HttpMethod.Get, $"RetrieveDuplicates(BusinessEntity={entity},MatchingEntityName='contact',PagingInfo={tooBig})"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- BulkDetectDuplicates -----------------------------------------------------------------------------------------

    [Fact]
    public async Task BulkDetectDuplicates_returns_JobId()
    {
        var anna = await ContactAsync("Anna", Mail("anna"));
        var copy = await ContactAsync("Copy", Mail("anna"));
        await PublishedRuleAsync();

        // Learn's body.
        JsonObject Body() => new()
        {
            ["Query"] = new JsonObject { ["@odata.type"] = "#Microsoft.Dynamics.CRM.QueryExpression", ["EntityName"] = Contact },
            ["JobName"] = "jobname1",
            ["SendEmailNotification"] = false,
            ["TemplateId"] = "07B94C1D-C85F-492F-B120-F0A743C540E6",
            ["ToRecipients"] = new JsonArray(),
            ["CCRecipients"] = new JsonArray(),
            ["RecurrencePattern"] = "",
            ["RecurrenceStartTime"] = "2015-07-15T05:30:00Z",
        };
        var json = await JsonAsync(await SendAsync(HttpMethod.Post, "BulkDetectDuplicates", Body()), HttpStatusCode.OK);

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.BulkDetectDuplicatesResponse");
        var job = Guid.Parse(json["JobId"]!.GetValue<string>());
        (await _kit.ScalarAsync("SELECT operationtype FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job))).Should().Be(8);
        (await _kit.ScalarAsync("SELECT name FROM data.asyncoperation WHERE asyncoperationid = @id", ("@id", job))).Should().Be("jobname1");
        await _kit.RunJobAsync(job);
        // The query reads every contact (other tests' rows too): both of this run's are among the base records.
        (await _kit.ScalarAsync("SELECT COUNT(*) FROM data.duplicaterecord WHERE asyncoperationid = @id AND baserecordid IN (@a, @c)",
            ("@id", job), ("@a", anna), ("@c", copy))).Should().Be(2);

        // A FetchExpression works too; the SDK path's refusals keep their status.
        var fetch = Body();
        fetch["Query"] = new JsonObject
        {
            ["@odata.type"] = "#Microsoft.Dynamics.CRM.FetchExpression", ["Query"] = $"<fetch><entity name='{Contact}' /></fetch>",
        };
        (await SendAsync(HttpMethod.Post, "BulkDetectDuplicates", fetch)).StatusCode.Should().Be(HttpStatusCode.OK);
        var email = Body();
        email["SendEmailNotification"] = true;
        (await SendAsync(HttpMethod.Post, "BulkDetectDuplicates", email)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var criteria = Body();
        criteria["Query"]!["Criteria"] = new JsonObject();
        var refused = await JsonAsync(await SendAsync(HttpMethod.Post, "BulkDetectDuplicates", criteria), HttpStatusCode.BadRequest);
        refused["error"]!["message"]!.GetValue<string>().Should().Contain("Criteria");
        var parties = Body();
        parties["ToRecipients"] = new JsonArray(new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.activityparty", ["partyid_systemuser@odata.bind"] = $"systemusers({Guid.NewGuid()})",
        });
        parties["CCRecipients"] = new JsonArray(Guid.NewGuid().ToString(), new JsonObject { ["participationtypemask"] = 3 });
        var accepted = await SendAsync(HttpMethod.Post, "BulkDetectDuplicates", parties);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        var notList = Body();
        notList["ToRecipients"] = "x";
        (await SendAsync(HttpMethod.Post, "BulkDetectDuplicates", notList)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var unknown = Body();
        unknown["Other"] = 1;
        (await SendAsync(HttpMethod.Post, "BulkDetectDuplicates", unknown)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Metadata_declares_duplicate_operations()
    {
        var response = await SendAsync(HttpMethod.Get, "$metadata");
        var csdl = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        csdl.Should().Contain("<Action Name=\"PublishDuplicateRule\" IsBound=\"true\">")
            .And.Contain("<Action Name=\"UnpublishDuplicateRule\">")
            .And.Contain("<Function Name=\"RetrieveDuplicates\">")
            .And.Contain("<ComplexType Name=\"BulkDetectDuplicatesResponse\">")
            .And.Contain("<FunctionImport Name=\"RetrieveDuplicates\"");
    }
}

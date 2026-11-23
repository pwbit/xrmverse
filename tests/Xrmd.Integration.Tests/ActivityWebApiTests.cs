using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Core;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.11, task 4: the activity model on the Web API (A8, A14). Party lists are not properties: they are written through the
/// collection navigation <c>{activity}_activity_parties</c> in POST and PATCH bodies and read with <c>$expand</c>; the pointer
/// and party sets are read-only; <c>activitytypecode</c> filters by table name.
/// </summary>
[Collection("xrmd")]
public sealed class ActivityWebApiTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private const string Formatted = "@OData.Community.Display.V1.FormattedValue";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    // ---- helpers ----

    private string Tag(string label) => $"{label} {_run}";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, bool annotations = false)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (annotations) req.Headers.Add("Prefer", "odata.include-annotations=\"*\"");
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage res, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(status, body);
        return JsonNode.Parse(body)!["error"]!["message"]!.GetValue<string>();
    }

    private async Task<JsonObject> GetJsonAsync(string path, bool annotations = false)
    {
        var res = await SendAsync(HttpMethod.Get, path, annotations: annotations);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private async Task<Guid> PostAsync(string set, string json)
    {
        var res = await SendAsync(HttpMethod.Post, set, json);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var url = res.Headers.GetValues("OData-EntityId").Single();
        return Guid.Parse(url[(url.LastIndexOf('(') + 1)..^1]);
    }

    private async Task<Guid> ContactAsync(string first) => await Admin.CreateAsync(new Entity("contact")
    {
        ["firstname"] = first, ["lastname"] = "Example", ["emailaddress1"] = $"{first.ToLowerInvariant()}.{_run}@example.com",
    });

    private Task<Guid> AccountAsync() => Admin.CreateAsync(new Entity("account") { ["name"] = Tag("Contoso Example") });

    private async Task<Guid> CallerIdAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.Parameters.AddWithValue("@oid", fx.AdminObjectId);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static EntityCollection Parties(params EntityReference[] parties) =>
        new([.. parties.Select(p => new Entity("activityparty") { ["partyid"] = p })]);

    private static IEnumerable<Guid> PartyIds(Entity email, string list) =>
        email.GetAttributeValue<EntityCollection>(list)?.Entities.Select(p => p.GetAttributeValue<EntityReference>("partyid").Id) ?? [];

    // ---- tests ----

    [Fact]
    public async Task Post_email_with_activity_parties_and_regarding_bind()
    {
        var account = await AccountAsync();
        var ada = await ContactAsync("Ada");
        var me = await CallerIdAsync();
        var id = await PostAsync("emails", $$"""
            {
              "subject": "{{Tag("Welcome")}}",
              "regardingobjectid_account_email@odata.bind": "/accounts({{account}})",
              "email_activity_parties": [
                { "participationtypemask": 2, "partyid_contact@odata.bind": "/contacts({{ada}})" },
                { "participationtypemask": 2, "addressused": "guest.{{_run}}@example.com" },
                { "participationtypemask": 1, "partyid_systemuser@odata.bind": "/systemusers({{me}})" }
              ]
            }
            """);

        var email = await Admin.RetrieveAsync("email", id, new ColumnSet("to", "from", "regardingobjectid"));
        email.GetAttributeValue<EntityReference>("regardingobjectid").Id.Should().Be(account);
        var to = email.GetAttributeValue<EntityCollection>("to").Entities;
        to.Should().HaveCount(2);
        to[0].GetAttributeValue<EntityReference>("partyid").Id.Should().Be(ada);
        to[0].GetAttributeValue<string>("addressused").Should().Be($"ada.{_run}@example.com");
        to[1].Contains("partyid").Should().BeFalse();
        to[1].GetAttributeValue<string>("addressused").Should().Be($"guest.{_run}@example.com");
        PartyIds(email, "from").Should().Equal(me);

        // The managed lookups have one navigation property per target: the bare name is refused, in words that do not call it a
        // Customer column.
        var bare = await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "emails",
            $$"""{ "subject": "x", "regardingobjectid@odata.bind": "/accounts({{account}})" }"""));
        bare.Should().Contain("regardingobjectid_account_email@odata.bind").And.NotContain("Customer");
        var twice = await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "emails", $$"""
            { "subject": "x", "regardingobjectid_account_email@odata.bind": "/accounts({{account}})",
              "regardingobjectid_contact_email@odata.bind": "/contacts({{ada}})" }
            """));
        twice.Should().Contain("regardingobjectid").And.NotContain("Customer");
        var partyBare = await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "emails", $$"""
            { "subject": "x", "email_activity_parties": [ { "participationtypemask": 2, "partyid@odata.bind": "/contacts({{ada}})" } ] }
            """));
        partyBare.Should().Contain("partyid_contact@odata.bind").And.NotContain("Customer");
    }

    [Fact]
    public async Task Get_activitypointers_filtered_by_regarding_value()
    {
        var account = await AccountAsync();
        var email = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Mail"), ["regardingobjectid"] = new EntityReference("account", account) });
        var task = await Admin.CreateAsync(new Entity("task") { ["subject"] = Tag("Task"), ["regardingobjectid"] = new EntityReference("account", account) });

        var json = await GetJsonAsync(
            $"activitypointers?$select=subject,activitytypecode,_regardingobjectid_value&$filter=_regardingobjectid_value eq {account}&$orderby=subject",
            annotations: true);
        var rows = json["value"]!.AsArray().Select(r => r!.AsObject()).ToList();
        rows.Select(r => Guid.Parse(r["activityid"]!.GetValue<string>())).Should().Equal(email, task);
        rows.Select(r => r["activitytypecode"]!.GetValue<string>()).Should().Equal("email", "task");
        rows[0]["activitytypecode" + Formatted]!.GetValue<string>().Should().Be("Email");
        rows[0]["_regardingobjectid_value"]!.GetValue<string>().Should().Be(account.ToString());
        rows[0]["_regardingobjectid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be("account");
        rows[0]["_regardingobjectid_value@Microsoft.Dynamics.CRM.associatednavigationproperty"]!.GetValue<string>()
            .Should().Be("regardingobjectid_account");
        rows[1]["statuscode" + Formatted].Should().BeNull("not selected");

        var single = await GetJsonAsync($"activitypointers({task})?$select=statuscode", annotations: true);
        single["statuscode" + Formatted]!.GetValue<string>().Should().Be("Not Started");
    }

    [Fact]
    public async Task Expand_email_activity_parties()
    {
        var ada = await ContactAsync("Ada");
        var bob = await ContactAsync("Bob");
        var id = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = Tag("Expand"), ["to"] = Parties(new EntityReference("contact", ada)), ["cc"] = Parties(new EntityReference("contact", bob)),
        });
        var bare = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Expand none") });

        var single = await GetJsonAsync(
            $"emails({id})?$select=subject&$expand=email_activity_parties($select=participationtypemask,addressused,_partyid_value)", annotations: true);
        single.ContainsKey("to").Should().BeFalse("a party list is not a property");
        var parties = single["email_activity_parties"]!.AsArray().Select(p => p!.AsObject()).ToList();
        parties.Select(p => p["participationtypemask"]!.GetValue<int>()).Should().Equal(2, 3, 9);
        parties[0]["_partyid_value"]!.GetValue<string>().Should().Be(ada.ToString());
        parties[0]["_partyid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be("contact");
        parties[0]["_partyid_value@Microsoft.Dynamics.CRM.associatednavigationproperty"]!.GetValue<string>().Should().Be("partyid_contact");
        parties[0]["_partyid_value" + Formatted]!.GetValue<string>().Should().Be("Example, Ada");
        parties[0]["addressused"]!.GetValue<string>().Should().Be($"ada.{_run}@example.com");
        parties[0].ContainsKey("activitypartyid").Should().BeTrue();
        parties[0].ContainsKey("ispartydeleted").Should().BeFalse("not selected");
        parties[2]["_partyid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be("systemuser");

        var collection = await GetJsonAsync($"emails?$select=subject&$filter=endswith(subject,'{_run}')&$orderby=subject&$expand=email_activity_parties");
        var rows = collection["value"]!.AsArray().Select(r => r!.AsObject()).ToList();
        rows.Select(r => Guid.Parse(r["activityid"]!.GetValue<string>())).Should().Equal(id, bare);
        rows[0]["email_activity_parties"]!.AsArray().Should().HaveCount(3);
        rows[0]["email_activity_parties"]![0]!["ispartydeleted"]!.GetValue<bool>().Should().BeFalse();
        rows[1]["email_activity_parties"]!.AsArray().Select(p => p!["participationtypemask"]!.GetValue<int>()).Should().Equal(9);

        // Party lists are not properties of the entity type, and only $select is supported inside this $expand.
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Get, $"emails({id})?$select=to"))).Should().Contain("to");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Get, $"emails({id})?$expand=email_activity_parties($top=1)")))
            .Should().Contain("email_activity_parties");
        var all = await GetJsonAsync($"emails({id})");
        all.ContainsKey("to").Should().BeFalse();
        all.ContainsKey("cc").Should().BeFalse();
    }

    [Fact]
    public async Task Filter_activitytypecode_eq_email()
    {
        await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Mail") });
        await Admin.CreateAsync(new Entity("task") { ["subject"] = Tag("Task") });

        var json = await GetJsonAsync($"activitypointers?$select=subject,activitytypecode&$filter=activitytypecode eq 'email' and endswith(subject,'{_run}')");
        json["value"]!.AsArray().Select(r => r!["subject"]!.GetValue<string>()).Should().Equal(Tag("Mail"));
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Get, "activitypointers?$filter=activitytypecode eq 'nosuchtable'")))
            .Should().Contain("nosuchtable");
    }

    /// <summary>
    /// Plan 4.1b, task 9 (ruling P12, checklist 3a): on PATCH the array is the activity's whole party set, as in Dataverse: every party
    /// list type of the table is replaced, a type with no item is cleared, and an empty array clears them all.
    /// </summary>
    [Fact]
    public async Task Patch_replaces_every_party_type()
    {
        var ada = await ContactAsync("Ada");
        var bob = await ContactAsync("Bob");
        var cy = await ContactAsync("Cy");
        var id = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = Tag("Patch"), ["to"] = Parties(new EntityReference("contact", ada)), ["cc"] = Parties(new EntityReference("contact", bob)),
        });

        var res = await SendAsync(HttpMethod.Patch, $"emails({id})", $$"""
            { "email_activity_parties": [ { "participationtypemask": 2, "partyid_contact@odata.bind": "/contacts({{cy}})" } ] }
            """);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var email = await Admin.RetrieveAsync("email", id, new ColumnSet("to", "cc"));
        PartyIds(email, "to").Should().Equal(cy);
        PartyIds(email, "cc").Should().BeEmpty("a type with no item in the array is cleared");

        // An empty array clears every party list.
        (await SendAsync(HttpMethod.Patch, $"emails({id})", """{ "email_activity_parties": [] }""")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        email = await Admin.RetrieveAsync("email", id, new ColumnSet("to", "cc"));
        PartyIds(email, "to").Should().BeEmpty();
        PartyIds(email, "cc").Should().BeEmpty();

        // Types the server keeps (Owner, Regarding) or the table does not have are refused.
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Patch, $"emails({id})", """{ "email_activity_parties": [ { "participationtypemask": 9 } ] }""")))
            .Should().Contain("9");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Patch, $"emails({id})", """{ "email_activity_parties": [ { "participationtypemask": 5 } ] }""")))
            .Should().Contain("5");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Patch, $"emails({id})", """{ "email_activity_parties": [ { "addressused": "a@example.com" } ] }""")))
            .Should().Contain("participationtypemask");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Patch, $"emails({id})", """{ "email_activity_parties": { "participationtypemask": 2 } }""")))
            .Should().Contain("email_activity_parties");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Patch, $"emails({id})",
                """{ "email_activity_parties": [ { "participationtypemask": 2, "addressused": "a@example.com", "ispartydeleted": true } ] }""")))
            .Should().Contain("ispartydeleted");
    }

    /// <summary>Plan 4.1b, task 9 (P12): a form that empties To sends the other lists whole; To loses its rows, the others keep theirs.</summary>
    [Fact]
    public async Task Patch_with_an_emptied_to_list_clears_to_and_keeps_the_others()
    {
        var caller = await CallerIdAsync();
        var ada = await ContactAsync("Ada");
        var bob = await ContactAsync("Bob");
        var id = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = Tag("Emptied"), ["from"] = Parties(new EntityReference("systemuser", caller)),
            ["to"] = Parties(new EntityReference("contact", ada)), ["cc"] = Parties(new EntityReference("contact", bob)),
        });

        var res = await SendAsync(HttpMethod.Patch, $"emails({id})", $$"""
            { "email_activity_parties": [
                { "participationtypemask": 1, "partyid_systemuser@odata.bind": "/systemusers({{caller}})" },
                { "participationtypemask": 3, "partyid_contact@odata.bind": "/contacts({{bob}})" } ] }
            """);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var email = await Admin.RetrieveAsync("email", id, new ColumnSet("from", "to", "cc"));
        PartyIds(email, "to").Should().BeEmpty();
        PartyIds(email, "cc").Should().Equal(bob);
        PartyIds(email, "from").Should().Equal(caller);
    }

    [Fact]
    public async Task Post_to_activitypointers_or_activityparties_is_400()
    {
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Read only") });
        (await SendAsync(HttpMethod.Post, "activitypointers", """{ "subject": "x" }""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Patch, $"activitypointers({id})", """{ "subject": "x" }""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Delete, $"activitypointers({id})")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "activityparties", """{ "addressused": "a@example.com" }""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var party = (await GetJsonAsync($"activityparties?$select=activitypartyid&$filter=_activityid_value eq {id}"))["value"]![0]!["activitypartyid"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Patch, $"activityparties({party})", """{ "addressused": "a@example.com" }""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Delete, $"activityparties({party})")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Admin.RetrieveAsync("activitypointer", id, new ColumnSet("subject"))).GetAttributeValue<string>("subject").Should().Be(Tag("Read only"));
    }

    [Fact]
    public async Task Other_deep_insert_is_400()
    {
        var account = await PostAsync("accounts", $$"""{ "name": "{{Tag("Deep")}}" }""");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "accounts",
                """{ "name": "x", "Account_Emails": [ { "subject": "inside" } ] }""")))
            .Should().Contain("Account_Emails").And.Contain("collection-valued");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Patch, $"accounts({account})",
                """{ "account_activity_parties": [ { "participationtypemask": 2 } ] }""")))
            .Should().Contain("account_activity_parties").And.Contain("collection-valued");
        // A task has no party list: its parties navigation takes no item.
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "tasks",
                """{ "subject": "x", "task_activity_parties": [ { "participationtypemask": 2, "addressused": "a@example.com" } ] }""")))
            .Should().Contain("2");
        (await ErrorMessageAsync(await SendAsync(HttpMethod.Post, "emails",
                """{ "subject": "x", "Email_Annotation": [ { "subject": "note" } ] }""")))
            .Should().Contain("Email_Annotation");
    }
    [Fact]
    public async Task Expand_per_target_navigations_check_the_lookup_type()
    {
        // Review I3, ruling Y4 (A14): a per-target navigation property joins its table on the id and the lookup's type column, so a
        // record of the wrong table, even one with the same id, expands to null.
        var account = await AccountAsync();
        var ada = await ContactAsync("Ada");
        await Admin.CreateAsync(new Entity("account", ada) { ["name"] = Tag("Same id as Ada") });
        var me = await CallerIdAsync();
        var email = await Admin.CreateAsync(new Entity("email")
        {
            ["subject"] = Tag("A mail"), ["regardingobjectid"] = new EntityReference("account", account),
            ["from"] = Parties(new EntityReference("systemuser", me)), ["to"] = Parties(new EntityReference("contact", ada)),
        });
        var task = await Admin.CreateAsync(new Entity("task") { ["subject"] = Tag("B task"), ["regardingobjectid"] = new EntityReference("contact", ada) });

        // regardingobjectid_{target} on the pointer.
        var pointers = (await GetJsonAsync(
                $"activitypointers?$select=subject&$filter=endswith(subject,'{_run}')&$orderby=subject&$expand=regardingobjectid_account($select=name)"))
            ["value"]!.AsArray().Select(r => r!.AsObject()).ToList();
        pointers.Select(r => Guid.Parse(r["activityid"]!.GetValue<string>())).Should().Equal(email, task);
        pointers[0]["regardingobjectid_account"]!["name"]!.GetValue<string>().Should().Be(Tag("Contoso Example"));
        pointers[1]["regardingobjectid_account"].Should().BeNull("the task is regarding a contact whose id an account also has");

        // regardingobjectid_{target}_{activity} on an activity table.
        (await GetJsonAsync($"emails({email})?$select=subject&$expand=regardingobjectid_account_email($select=name)"))
            ["regardingobjectid_account_email"]!["name"]!.GetValue<string>().Should().Be(Tag("Contoso Example"));
        (await GetJsonAsync($"tasks({task})?$select=subject&$expand=regardingobjectid_account_task($select=name)"))
            ["regardingobjectid_account_task"].Should().BeNull();
        (await GetJsonAsync($"tasks({task})?$select=subject&$expand=regardingobjectid_contact_task($select=fullname)"))
            ["regardingobjectid_contact_task"]!["fullname"]!.GetValue<string>().Should().Be("Example, Ada");

        // partyid_{target} on the party rows.
        var parties = (await GetJsonAsync(
                $"activityparties?$select=participationtypemask&$filter=_activityid_value eq {email}&$orderby=participationtypemask" +
                "&$expand=partyid_contact($select=fullname),partyid_account($select=name)"))
            ["value"]!.AsArray().Select(r => r!.AsObject()).ToList();
        parties.Select(p => p["participationtypemask"]!.GetValue<int>()).Should().Equal(1, 2, 8, 9);
        parties[0]["partyid_contact"].Should().BeNull("the sender is a user");
        parties[1]["partyid_contact"]!["fullname"]!.GetValue<string>().Should().Be("Example, Ada");
        parties[1]["partyid_account"].Should().BeNull("the account with Ada's id is not the party");
        parties[2]["partyid_account"]!["name"]!.GetValue<string>().Should().Be(Tag("Contoso Example"), "the regarding party");
        parties[2]["partyid_contact"].Should().BeNull();

        // activityid_{activity} on the party rows: the activity's own table, or the pointer whatever the table.
        var taskParties = (await GetJsonAsync(
                $"activityparties?$select=participationtypemask&$filter=_activityid_value eq {task}" +
                "&$expand=activityid_email($select=subject),activityid_task($select=subject),activityid_activitypointer($select=subject)"))
            ["value"]!.AsArray().Select(r => r!.AsObject()).ToList();
        taskParties.Should().HaveCount(2, "the regarding and owner parties");
        taskParties.Should().OnlyContain(p => p["activityid_email"] == null);
        taskParties.Should().OnlyContain(p => p["activityid_task"]!["subject"]!.GetValue<string>() == Tag("B task"));
        taskParties.Should().OnlyContain(p => p["activityid_activitypointer"]!["subject"]!.GetValue<string>() == Tag("B task"));
    }

    [Fact]
    public async Task Expand_activitypointer_activity_parties()
    {
        var ada = await ContactAsync("Ada");
        var id = await Admin.CreateAsync(new Entity("email") { ["subject"] = Tag("Pointer parties"), ["to"] = Parties(new EntityReference("contact", ada)) });

        var single = await GetJsonAsync($"activitypointers({id})?$select=subject&$expand=activitypointer_activity_parties($select=participationtypemask,_partyid_value)");
        var parties = single["activitypointer_activity_parties"]!.AsArray().Select(p => p!.AsObject()).ToList();
        parties.Select(p => p["participationtypemask"]!.GetValue<int>()).Should().Equal(2, 9);
        parties[0]["_partyid_value"]!.GetValue<string>().Should().Be(ada.ToString());

        var many = await GetJsonAsync($"activitypointers?$select=subject&$filter=endswith(subject,'{_run}')&$expand=activitypointer_activity_parties");
        many["value"]!.AsArray().Should().ContainSingle().Which!["activitypointer_activity_parties"]!.AsArray().Should().HaveCount(2);
    }
}

using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1a final review, end to end over the Web API (<c>?fetchXml=</c>): a like-family condition on a lookup matches the
/// related record's primary name (I1, #107), an order on a lookup sorts by that name and an order on a choice by its label (I2),
/// across pages of the paging cookie.
/// </summary>
[Collection("xrmd")]
public sealed class LookupNameQueryTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private readonly string _tag = "LNQ" + Guid.NewGuid().ToString("N")[..10];
    private OrganizationService _svc = null!;
    private string _adminName = null!;

    public async Task InitializeAsync()
    {
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        var factory = fx.App.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await factory.OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT fullname FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.AddParameter("@oid", DbType.Guid, fx.AdminObjectId);
        _adminName = (string)(await cmd.ExecuteScalarAsync())!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonArray> FetchAsync(string set, string fetchXml)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}{set}?fetchXml={Uri.EscapeDataString(fetchXml)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!["value"]!.AsArray();
    }

    private static List<string> Names(JsonArray rows, string column) => rows.Select(r => r![column]!.GetValue<string>()).ToList();

    private Task<Guid> AccountAsync(string name) => _svc.CreateAsync(new Entity("account") { ["name"] = name });

    private Task<Guid> ContactAsync(string firstName, EntityReference? parent) =>
        _svc.CreateAsync(new Entity("contact") { ["firstname"] = firstName, ["lastname"] = _tag, ["parentcustomerid"] = parent });

    private string Contacts(string condition, string order = "") =>
        $"<fetch><entity name='contact'><attribute name='firstname'/><filter><condition attribute='lastname' operator='eq' value='{_tag}'/>" +
        $"{condition}</filter>{order}</entity></fetch>";

    [Fact]
    public async Task Like_on_a_customer_lookup_matches_account_and_contact_names_and_not_like_excludes_empty_lookups()
    {
        var acme = await AccountAsync($"Acme{_tag} Ltd");
        var other = await AccountAsync($"Other{_tag}");
        // The parent contact has a last name only, so that its fullname is that name whatever the name convention.
        var person = await _svc.CreateAsync(new Entity("contact") { ["lastname"] = $"Acme{_tag}Person" });
        await ContactAsync("a", new EntityReference("account", acme));
        await ContactAsync("b", new EntityReference("account", other));
        await ContactAsync("c", new EntityReference("contact", person));
        await ContactAsync("d", null);

        var like = await FetchAsync("contacts", Contacts($"<condition attribute='parentcustomerid' operator='like' value='Acme{_tag}%'/>"));
        Names(like, "firstname").Should().BeEquivalentTo("a", "c");

        var begins = await FetchAsync("contacts", Contacts($"<condition attribute='parentcustomerid' operator='begins-with' value='Other{_tag}'/>"));
        Names(begins, "firstname").Should().BeEquivalentTo("b");

        // NOT LIKE leaves out the row without a parent, as Dataverse's NULL name column does.
        var notLike = await FetchAsync("contacts", Contacts($"<condition attribute='parentcustomerid' operator='not-like' value='Acme{_tag}%'/>"));
        Names(notLike, "firstname").Should().BeEquivalentTo("b");
    }

    [Fact]
    public async Task Like_on_ownerid_matches_the_owning_user_name()
    {
        await AccountAsync($"Own{_tag}");
        var fetch = $"<fetch><entity name='account'><attribute name='name'/><filter><condition attribute='name' operator='eq' value='Own{_tag}'/>" +
                    $"<condition attribute='ownerid' operator='like' value='{_adminName}'/></filter></entity></fetch>";
        Names(await FetchAsync("accounts", fetch), "name").Should().Equal($"Own{_tag}");

        var none = fetch.Replace($"value='{_adminName}'", $"value='Nobody{_tag}'", StringComparison.Ordinal);
        (await FetchAsync("accounts", none)).Should().BeEmpty();
    }

    [Fact]
    public async Task Order_by_a_lookup_sorts_by_the_related_name_across_pages()
    {
        // Accounts created in an order unrelated to their names, so that id order and name order differ.
        var ids = new Dictionary<string, Guid>();
        foreach (var name in new[] { "C", "A", "D", "B" }) ids[name] = await AccountAsync($"{_tag} {name}");
        foreach (var (contact, account) in new[] { ("w", "D"), ("x", "A"), ("y", "C"), ("z", "B") })
            await ContactAsync(contact, new EntityReference("account", ids[account]));

        var seen = new List<string>();
        for (var page = 1; page <= 2; page++)
        {
            var fetch = Contacts("", "<order attribute='parentcustomerid'/>").Replace("<fetch>", $"<fetch count='2' page='{page}'>", StringComparison.Ordinal);
            seen.AddRange(Names(await FetchAsync("contacts", fetch), "firstname"));
        }
        seen.Should().Equal("x", "z", "y", "w"); // A, B, C, D

        var descending = await FetchAsync("contacts", Contacts("", "<order attribute='parentcustomerid' descending='true'/>"));
        Names(descending, "firstname").Should().Equal("w", "y", "z", "x");
    }

    [Fact]
    public async Task Order_by_a_choice_sorts_by_its_label_unless_useraworderby()
    {
        foreach (var priority in new[] { 1, 2, 3 })
            await _svc.CreateAsync(new Entity(TestEntities.Project) { ["pb_name"] = $"{_tag}-{priority}", ["pb_priority"] = new OptionSetValue(priority) });
        string Fetch(string raw) =>
            $"<fetch{raw}><entity name='pb_project'><attribute name='pb_name'/><filter><condition attribute='pb_name' operator='begins-with' value='{_tag}'/>" +
            "</filter><order attribute='pb_priority'/></entity></fetch>";

        // Labels: 1 Low, 2 Medium, 3 High: High, Low, Medium.
        Names(await FetchAsync(TestEntities.ProjectSet, Fetch("")), "pb_name").Should().Equal($"{_tag}-3", $"{_tag}-1", $"{_tag}-2");
        Names(await FetchAsync(TestEntities.ProjectSet, Fetch(" useraworderby='true'")), "pb_name")
            .Should().Equal($"{_tag}-1", $"{_tag}-2", $"{_tag}-3");
    }
}

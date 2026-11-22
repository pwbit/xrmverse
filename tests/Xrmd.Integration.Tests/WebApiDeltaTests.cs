using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.13, task 4: the Web API delta queries (ruling C12): <c>Prefer: odata.track-changes</c>, <c>@odata.deltaLink</c>,
/// <c>$deltatoken</c>, the <c>$deletedEntity</c> entries, paging, <c>$count</c> and the refusals.
/// </summary>
[Collection("xrmd")]
public sealed class WebApiDeltaTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string TrackChanges = "odata.track-changes";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private string Base => fx.Client.BaseAddress!.ToString().TrimEnd('/');

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A new table (tracked unless told otherwise); returns its logical name and entity set name.</summary>
    private async Task<(string Table, string Set)> NewTableAsync(string label, bool tracked = true)
    {
        var name = $"new_{label}{_run}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = Label("Table " + name), DisplayCollectionName = Label("Tables " + name),
                OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });
        if (tracked)
            await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name, ChangeTrackingEnabled = true } });
        var set = fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(name)!.EntitySetName!;
        return (name, set);
    }

    private async Task<List<Guid>> CreateAsync(string table, int count, string prefix = "r")
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++) ids.Add(await Admin.CreateAsync(new Entity(table) { ["new_name"] = $"{prefix}{i}" }));
        return ids;
    }

    private Task<HttpResponseMessage> GetAsync(string pathAndQuery, params (string Name, string Value)[] headers) =>
        GetAsAsync(fx.AdminObjectId, pathAndQuery, headers);

    private Task<HttpResponseMessage> GetAsAsync(Guid objectId, string pathAndQuery, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, pathAndQuery.StartsWith("http", StringComparison.Ordinal) ? pathAndQuery : Api + pathAndQuery);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId, fx.BaseUrl));
        foreach (var (name, value) in headers) req.Headers.TryAddWithoutValidation(name, value);
        return fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(expected, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private static async Task<string> BadRequestAsync(HttpResponseMessage res) =>
        (await JsonAsync(res, HttpStatusCode.BadRequest))["error"]!["message"]!.GetValue<string>();

    private static string DeltaLink(JsonObject json) => json["@odata.deltaLink"]!.GetValue<string>();

    /// <summary>The <c>$deltatoken</c> of a delta link, URL-decoded.</summary>
    private static string TokenOf(string link)
    {
        var part = new Uri(link).Query.TrimStart('?').Split('&').Single(p => p.StartsWith("$deltatoken=", StringComparison.Ordinal));
        return Uri.UnescapeDataString(part["$deltatoken=".Length..]);
    }

    private static List<JsonObject> Values(JsonObject json) => json["value"]!.AsArray().Select(v => v!.AsObject()).ToList();

    private static bool IsDeleted(JsonObject entry) => entry.ContainsKey("reason");

    // ---- tests --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Track_changes_header_returns_delta_link()
    {
        var (table, set) = await NewTableAsync("wa");
        var ids = await CreateAsync(table, 2);

        var res = await GetAsync($"{set}?$select=new_name", ("Prefer", TrackChanges));
        var json = await JsonAsync(res);

        res.Headers.GetValues("Preference-Applied").Single().Should().Contain(TrackChanges);
        json["@odata.context"]!.GetValue<string>().Should().Be($"{Base}{Api}$metadata#{set}(new_name)");
        Values(json).Select(v => Guid.Parse(v[$"{table}id"]!.GetValue<string>())).Should().BeEquivalentTo(ids);
        Values(json).Should().OnlyContain(v => v["@odata.etag"] != null && v["new_name"] != null);
        json.ContainsKey("@odata.nextLink").Should().BeFalse();
        var link = DeltaLink(json);
        link.Should().StartWith($"{Base}{Api}{set}?$select=new_name&$deltatoken=");
        ChangeToken.TryParse(TokenOf(link), out _).Should().BeTrue(link);
        link.Should().NotContain(" ", "the token is URL-encoded");
    }

    [Fact]
    public async Task Delta_link_returns_changes_and_deleted_entities()
    {
        var (table, set) = await NewTableAsync("wb");
        var ids = await CreateAsync(table, 3);
        var first = DeltaLink(await JsonAsync(await GetAsync($"{set}?$select=new_name", ("Prefer", TrackChanges))));

        await Admin.UpdateAsync(new Entity(table, ids[0]) { ["new_name"] = "changed" });
        await Admin.DeleteAsync(table, ids[1]);
        var created = (await CreateAsync(table, 1, "n"))[0];

        var res = await GetAsync(first);
        var json = await JsonAsync(res);

        res.Headers.Contains("Preference-Applied").Should().BeFalse("the delta link needs no header");
        json["@odata.context"]!.GetValue<string>().Should().Be($"{Base}{Api}$metadata#{set}(new_name)/$delta");
        var values = Values(json);
        values.Where(v => !IsDeleted(v)).Select(v => (Guid.Parse(v[$"{table}id"]!.GetValue<string>()), v["new_name"]!.GetValue<string>()))
            .Should().Equal((ids[0], "changed"), (created, "n0"));
        var deleted = values.Where(IsDeleted).Should().ContainSingle().Subject;
        deleted.Select(p => p.Key).Should().BeEquivalentTo("@odata.context", "id", "reason");
        deleted["@odata.context"]!.GetValue<string>().Should().Be($"{Base}{Api}$metadata#{set}/$deletedEntity");
        deleted["id"]!.GetValue<string>().Should().Be(ids[1].ToString());
        deleted["reason"]!.GetValue<string>().Should().Be("deleted");
        var next = DeltaLink(json);
        next.Should().StartWith($"{Base}{Api}{set}?$select=new_name&$deltatoken=").And.NotBe(first);
        new Uri(next).Query.Split('&').Count(p => p.StartsWith("$deltatoken=", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Delta_without_changes_returns_empty_value()
    {
        var (table, set) = await NewTableAsync("wc");
        await CreateAsync(table, 2);
        var link = DeltaLink(await JsonAsync(await GetAsync(set, ("Prefer", TrackChanges))));

        var json = await JsonAsync(await GetAsync(link));

        json["@odata.context"]!.GetValue<string>().Should().Be($"{Base}{Api}$metadata#{set}/$delta");
        json["value"]!.AsArray().Should().BeEmpty();
        TokenOf(DeltaLink(json)).Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("$filter=new_name eq 'x'", false)]
    [InlineData("$orderby=new_name", false)]
    [InlineData("$expand=owninguser($select=fullname)", false)]
    [InlineData("$top=1", false)]
    [InlineData("$filter=new_name eq 'x'", true)]
    [InlineData("$orderby=new_name", true)]
    [InlineData("$expand=owninguser($select=fullname)", true)]
    [InlineData("$top=1", true)]
    public async Task Filter_orderby_expand_top_are_refused_with_tracking(string option, bool withToken)
    {
        var (table, set) = await NewTableAsync("wd");
        await CreateAsync(table, 1);
        var option0 = option.Split('=')[0];

        string message;
        if (withToken)
        {
            var link = DeltaLink(await JsonAsync(await GetAsync(set, ("Prefer", TrackChanges))));
            message = await BadRequestAsync(await GetAsync($"{link}&{option}"));
        }
        else
        {
            message = await BadRequestAsync(await GetAsync($"{set}?{option}", ("Prefer", TrackChanges)));
        }

        message.Should().Contain(option0).And.Contain("change tracking");
        // Without tracking, the same option is a plain query.
        (await GetAsync($"{set}?{option}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Count_with_deltatoken_returns_number_of_changes()
    {
        var (table, set) = await NewTableAsync("we");
        var ids = await CreateAsync(table, 3);
        var token = TokenOf(DeltaLink(await JsonAsync(await GetAsync(set, ("Prefer", TrackChanges)))));

        await Admin.UpdateAsync(new Entity(table, ids[0]) { ["new_name"] = "changed" });
        await Admin.DeleteAsync(table, ids[1]);
        await CreateAsync(table, 2, "n");

        var res = await GetAsync($"{set}/$count?$deltatoken={Uri.EscapeDataString(token)}");
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        body.Should().Be("4");
        // $count=true on the delta itself writes the same number.
        var counted = await JsonAsync(await GetAsync($"{set}?$deltatoken={Uri.EscapeDataString(token)}&$count=true", ("Prefer", "odata.maxpagesize=1")));
        counted["@odata.count"]!.GetValue<int>().Should().Be(4);
        counted["value"]!.AsArray().Should().HaveCount(1);
        // Without a token, $count is not a delta request.
        var plain = await GetAsync($"{set}/$count?$deltatoken=");
        plain.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Maxpagesize_pages_with_next_link_and_delta_link_on_last_page()
    {
        var (table, set) = await NewTableAsync("wf");
        var ids = await CreateAsync(table, 5);

        // The first sync, in pages of 2: the delta link comes only on the last page.
        var url = $"{set}?$select=new_name";
        var seen = new List<Guid>();
        string? delta = null;
        var pages = 0;
        while (url is not null)
        {
            var res = await GetAsync(url, ("Prefer", $"{TrackChanges},odata.maxpagesize=2"));
            var json = await JsonAsync(res);
            pages++;
            res.Headers.GetValues("Preference-Applied").Single().Should().Contain(TrackChanges).And.Contain("odata.maxpagesize=2");
            Values(json).Count.Should().BeLessOrEqualTo(2);
            seen.AddRange(Values(json).Select(v => Guid.Parse(v[$"{table}id"]!.GetValue<string>())));
            url = json["@odata.nextLink"]?.GetValue<string>();
            if (url is not null)
            {
                json.ContainsKey("@odata.deltaLink").Should().BeFalse();
                url.Should().StartWith($"{Base}{Api}{set}?$select=new_name&").And.Contain("$skiptoken=");
            }
            else delta = DeltaLink(json);
            pages.Should().BeLessThan(10);
        }
        pages.Should().Be(3);
        seen.Should().Equal(ids);

        // A delta in pages of 2 (4 updates and 1 deletion): the next links keep the token; a new delta link at the end.
        foreach (var id in ids.Take(3)) await Admin.UpdateAsync(new Entity(table, id) { ["new_name"] = "changed" });
        await Admin.DeleteAsync(table, ids[4]);
        var created = (await CreateAsync(table, 1, "n"))[0];
        url = delta;
        var updated = new List<Guid>();
        var deleted = new List<Guid>();
        pages = 0;
        string? next = null;
        while (url is not null)
        {
            var json = await JsonAsync(await GetAsync(url, ("Prefer", "odata.maxpagesize=2")));
            pages++;
            json["@odata.context"]!.GetValue<string>().Should().EndWith("/$delta");
            foreach (var v in Values(json))
                if (IsDeleted(v)) deleted.Add(Guid.Parse(v["id"]!.GetValue<string>()));
                else updated.Add(Guid.Parse(v[$"{table}id"]!.GetValue<string>()));
            url = json["@odata.nextLink"]?.GetValue<string>();
            if (url is not null)
            {
                json.ContainsKey("@odata.deltaLink").Should().BeFalse();
                url.Should().Contain("$deltatoken=").And.Contain("$skiptoken=");
                TokenOf(url).Should().Be(TokenOf(delta!));
            }
            else next = DeltaLink(json);
            pages.Should().BeLessThan(10);
        }
        pages.Should().Be(3);
        updated.Should().Equal(ids[0], ids[1], ids[2], created);
        deleted.Should().Equal(ids[4]);
        TokenOf(next!).Should().NotBe(TokenOf(delta!));
        new Uri(next!).Query.Should().NotContain("$skiptoken");
    }

    [Fact]
    public async Task Untracked_entity_set_refuses_header_and_token()
    {
        var (table, set) = await NewTableAsync("wg", tracked: false);
        await CreateAsync(table, 1);

        (await BadRequestAsync(await GetAsync(set, ("Prefer", TrackChanges)))).Should().Contain("change tracking");
        (await BadRequestAsync(await GetAsync($"{set}?$deltatoken={Uri.EscapeDataString("1!2026-01-01 00:00:00")}")))
            .Should().Contain("change tracking");
        (await GetAsync($"{set}/$count?$deltatoken=1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(set)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Expired_deltatoken_is_400()
    {
        var (table, set) = await NewTableAsync("wh");
        await CreateAsync(table, 1);

        // Version 1 predates the table's tracking: deletions before it were never logged.
        var message = await BadRequestAsync(await GetAsync($"{set}?$deltatoken={Uri.EscapeDataString("1!2026-01-01 00:00:00")}"));
        message.Should().Contain("expired");
        (await GetAsync($"{set}/$count?$deltatoken=1")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // A token that does not parse is a 400 too.
        (await GetAsync($"{set}?$deltatoken=garbage")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delta_runs_through_the_pipeline_guard_and_needs_global_read()
    {
        var (table, set) = await NewTableAsync("wi");
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ($"prvRead{table}", DepthMask.Local));

        var res = await GetAsAsync(oid, set, ("Prefer", TrackChanges));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
        (await GetAsAsync(oid, $"{set}/$count?$deltatoken=1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await GrantAsync(fx, user, team: false, ($"prvRead{table}", DepthMask.Global));
        (await GetAsAsync(oid, set, ("Prefer", TrackChanges))).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Metadata.Sdk;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// Plan 2.7a, Task 6 (Global Constraints: string comparisons, ordering and unique keys ignore case and keep accents, SQL Server's
/// <c>_CI_AS</c>; <c>citext</c> under the ICU <c>und</c> collation on PostgreSQL). Runs on the run's provider
/// (<c>XRMD_TEST_PROVIDER</c>) against constants, so both providers answer the same.
/// </summary>
/// <remarks>
/// The accounts of one class instance carry a tag of their own (<c>pb_paritytag</c>, added to <c>account</c>): other classes of
/// the collection create accounts named <c>Acme</c> too, so every query filters on the tag.
/// </remarks>
[Collection("xrmd")]
public sealed class CaseInsensitivityParityTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Tag = "pb_paritytag";
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static readonly string[] Names = ["Acme", "ACME Corp", "alpha", "Beta", "gamma", "Élan", "elan"];
    private readonly string _tag = Guid.NewGuid().ToString("N");

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Service => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (Store.GetEntity("account")!.Attributes.All(a => a.LogicalName != Tag))
                await Store.AddAttributeAsync("account", new StringAttributeMetadata { LogicalName = Tag, SchemaName = "pb_ParityTag", MaxLength = 50 });
        }
        finally
        {
            Lock.Release();
        }

        foreach (var name in Names)
            await Service.CreateAsync(new Entity("account") { ["name"] = name, [Tag] = _tag });
        await ParityTestKit.EnsureKeyTableAsync(fx);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string[]> NamesAsync(ConditionOperator op, string value)
    {
        var query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
        query.Criteria.AddCondition(Tag, ConditionOperator.Equal, _tag);
        query.Criteria.AddCondition("name", op, value);
        query.AddOrder("name", OrderType.Ascending);
        return (await Service.RetrieveMultipleAsync(query)).Entities.Select(e => e.GetAttributeValue<string>("name")).ToArray();
    }

    [Fact]
    public async Task Eq_ignores_case()
    {
        (await NamesAsync(ConditionOperator.Equal, "ACME")).Should().Equal("Acme");
    }

    [Fact]
    public async Task Begins_with_ignores_case()
    {
        (await NamesAsync(ConditionOperator.BeginsWith, "ac")).Should().Equal("Acme", "ACME Corp");
    }

    [Fact]
    public async Task Like_ignores_case()
    {
        (await NamesAsync(ConditionOperator.Like, "%corp")).Should().Equal("ACME Corp");
    }

    [Fact]
    public async Task Like_sets_ignore_case_and_keep_accents()
    {
        // SQL Server's character sets (fix round 1, review Important 1): a regular expression on PostgreSQL, case-insensitive on
        // citext, so the unaccented e of the set does not match É.
        (await NamesAsync(ConditionOperator.Like, "[ae]%")).Should().Equal("Acme", "ACME Corp", "alpha", "elan");
        (await NamesAsync(ConditionOperator.Like, "[é]lan")).Should().Equal("Élan");
        (await NamesAsync(ConditionOperator.NotLike, "[ae]%")).Should().Equal("Beta", "Élan", "gamma");
        (await NamesAsync(ConditionOperator.Like, "[^ae]%")).Should().Equal("Beta", "Élan", "gamma");
    }

    [Fact]
    public async Task Like_ranges_are_refused_on_postgresql_with_the_p10_fault()
    {
        // A range follows the collation's order on SQL Server ([a-c] matches á and B) and code points in a PostgreSQL regular
        // expression: plan 2.7a, ruling P15, refuses it with the 501 of the features not available yet.
        var fetch = $"<fetch><entity name='account'><attribute name='name' /><filter><condition attribute='{Tag}' operator='eq' value='{_tag}' />" +
                    "<condition attribute='name' operator='like' value='[a-c]%' /></filter><order attribute='name' /></entity></fetch>";
        var res = await ParityTestKit.SendAsync(fx, HttpMethod.Get, $"accounts?fetchXml={Uri.EscapeDataString(fetch)}");
        var body = await res.Content.ReadAsStringAsync();
        if (TestProvider.IsPostgres)
        {
            res.StatusCode.Should().Be(HttpStatusCode.NotImplemented, body);
            var error = JsonNode.Parse(body)!["error"]!;
            error["code"]!.GetValue<string>().Should().Be("0x80040216");
            error["message"]!.GetValue<string>().Should().Be("'Like patterns with character ranges' is not available on PostgreSQL organizations yet.");
        }
        else
        {
            res.StatusCode.Should().Be(HttpStatusCode.OK, body);
            JsonNode.Parse(body)!["value"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Should().Equal("Acme", "ACME Corp", "alpha", "Beta");
        }
    }

    [Fact]
    public async Task Eq_keeps_accents()
    {
        (await NamesAsync(ConditionOperator.Equal, "elan")).Should().Equal("elan");
    }

    [Fact]
    public async Task Orderby_ignores_case()
    {
        var res = await ParityTestKit.SendAsync(fx, HttpMethod.Get,
            $"accounts?$select=name&$filter={Tag} eq '{_tag}' and (name eq 'alpha' or name eq 'Beta' or name eq 'gamma')&$orderby=name");
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode.Parse(body)!["value"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()).Should().Equal("alpha", "Beta", "gamma");
    }

    [Fact]
    public async Task Alternate_key_refuses_a_value_differing_only_in_case()
    {
        var set = ParityTestKit.SetOf(fx);
        var name = "Acme " + Guid.NewGuid().ToString("N")[..8];
        var first = await ParityTestKit.SendAsync(fx, HttpMethod.Post, set, $$"""{ "pb_name": "{{name}}" }""");
        first.StatusCode.Should().Be(HttpStatusCode.NoContent, await first.Content.ReadAsStringAsync());

        var second = await ParityTestKit.SendAsync(fx, HttpMethod.Post, set, $$"""{ "pb_name": "{{name.ToLowerInvariant()}}" }""");

        var body = await second.Content.ReadAsStringAsync();
        second.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed, body);
        JsonNode.Parse(body)!["error"]!["code"]!.GetValue<string>().Should().Be("0x80040237");
    }
}

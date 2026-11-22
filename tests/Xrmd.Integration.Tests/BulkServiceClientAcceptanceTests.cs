using System.ServiceModel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xunit.Abstractions;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>Issue #76 acceptance: bulk messages on a standard table (<c>account</c>) seen by the real Dataverse <see cref="ServiceClient"/> over SOAP.</summary>
/// <remarks>Binds Kestrel to 127.0.0.1:80; skip with <c>dotnet test --filter Category!=Acceptance</c>.</remarks>
[Collection("serviceclient")]
[Trait("Category", "Acceptance")]
public sealed class BulkServiceClientAcceptanceTests(ServiceClientFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _tag = "bsc" + Guid.NewGuid().ToString("N")[..8];

    public async Task InitializeAsync()
    {
        fx.DrainRequestLog();
        await TestEntities.EnsureProjectAsync(fx.App.Services.GetRequiredService<IMetadataStore>());
    }

    public Task DisposeAsync()
    {
        foreach (var line in fx.DrainRequestLog()) output.WriteLine(line);
        return Task.CompletedTask;
    }

    private ServiceClient Connect() =>
        new(new Uri(ServiceClientFixture.BaseUrl), _ => Task.FromResult(DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl)), useUniqueInstance: true, new TestOutputLogger(output));

    private Entity Account(string suffix, Guid? id = null)
    {
        var e = new Entity("account") { ["name"] = _tag + suffix };
        if (id is not null) e.Id = id.Value;
        return e;
    }

    private static EntityCollection Targets(params Entity[] targets) => new(targets.ToList()) { EntityName = "account" };

    private static string? NameOf(ServiceClient client, Guid id) =>
        client.Retrieve("account", id, new ColumnSet("name")).GetAttributeValue<string>("name");

    private int CountByTag(ServiceClient client) =>
        client.RetrieveMultiple(new QueryExpression("account")
        {
            ColumnSet = new ColumnSet("accountid"),
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.BeginsWith, _tag) } },
        }).Entities.Count;

    [Fact]
    public void CreateMultiple_then_UpdateMultiple_then_UpsertMultiple()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

        var created = (CreateMultipleResponse)client.Execute(new CreateMultipleRequest
        {
            Targets = Targets(ids.Select((id, i) => Account("-c" + i, id)).ToArray()),
        });
        created.Ids.Should().Equal(ids);
        NameOf(client, ids[1]).Should().Be(_tag + "-c1");

        client.Execute(new UpdateMultipleRequest
        {
            Targets = Targets(ids.Select((id, i) => new Entity("account", id) { ["name"] = _tag + "-u" + i }).ToArray()),
        });
        NameOf(client, ids[2]).Should().Be(_tag + "-u2");

        var fresh = Guid.NewGuid();
        var upserted = (UpsertMultipleResponse)client.Execute(new UpsertMultipleRequest
        {
            Targets = Targets(new Entity("account", ids[0]) { ["name"] = _tag + "-up0" }, Account("-up-new", fresh)),
        });
        upserted.Results.Should().HaveCount(2);
        upserted.Results.Select(r => r.RecordCreated).Should().Equal(false, true);
        NameOf(client, ids[0]).Should().Be(_tag + "-up0");
        NameOf(client, fresh).Should().Be(_tag + "-up-new");
    }

    [Fact]
    public void Upsert_creates_then_updates()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var id = Guid.NewGuid();

        var first = (UpsertResponse)client.Execute(new UpsertRequest { Target = Account("-first", id) });
        first.RecordCreated.Should().BeTrue();
        first.Target.Id.Should().Be(id);

        var second = (UpsertResponse)client.Execute(new UpsertRequest { Target = Account("-second", id) });
        second.RecordCreated.Should().BeFalse();
        NameOf(client, id).Should().Be(_tag + "-second");
    }

    [Fact]
    public void Failing_target_carries_its_fault_and_writes_nothing()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);

        // The same bad target on a single Create gives the reference fault.
        var bad = Account("-bad");
        bad["zz_not_a_column"] = 1;
        var single = () => client.Execute(new CreateRequest { Target = bad });
        var expected = single.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail;

        var act = () => client.Execute(new CreateMultipleRequest { Targets = Targets(Account("-ok0"), Account("-ok1"), bad) });

        var fault = act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail;
        fault.ErrorCode.Should().Be(expected.ErrorCode);
        CountByTag(client).Should().Be(0, "a failing target rolls back every target of a standard-table bulk");
    }

    [Fact]
    public void Detection_query_finds_CreateMultiple_on_account_and_not_UpsertMultiple()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);

        int Rows(string message)
        {
            var query = new QueryExpression("sdkmessagefilter")
            {
                ColumnSet = new ColumnSet("sdkmessagefilterid"),
                Criteria = { Conditions = { new ConditionExpression("primaryobjecttypecode", ConditionOperator.Equal, "account") } },
            };
            var link = query.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid");
            link.LinkCriteria.AddCondition("name", ConditionOperator.Equal, message);
            return client.RetrieveMultiple(query).Entities.Count;
        }

        Rows("CreateMultiple").Should().Be(1);
        Rows("UpdateMultiple").Should().Be(1);
        Rows("UpsertMultiple").Should().Be(0, "UpsertMultiple is not an event on a standard table");
    }
}

using System.ServiceModel;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xunit.Abstractions;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>Plan 3.4 acceptance: elastic tables seen by the real Dataverse <see cref="ServiceClient"/> over SOAP.</summary>
/// <remarks>Binds Kestrel to 127.0.0.1:80; skip with <c>dotnet test --filter Category!=Acceptance</c>.</remarks>
[Collection("serviceclient")]
[Trait("Category", "Acceptance")]
public sealed class ElasticServiceClientAcceptanceTests(ServiceClientFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private const string SessionToken = "x-ms-session-token";
    private static readonly SemaphoreSlim TableLock = new(1, 1);
    private static string? _table;

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

    /// <summary>The elastic table <c>pb_elsc…</c> (<c>pb_name</c>, <c>pb_value</c> Integer), created once through the client.</summary>
    private async Task<string> TableAsync(ServiceClient client)
    {
        await TableLock.WaitAsync();
        try
        {
            if (_table is not null) return _table;
            var name = "pb_elsc" + Guid.NewGuid().ToString("N")[..6];
            var entity = Entity(name, name, "Elastic client");
            entity.TableType = "Elastic";
            client.Execute(new CreateEntityRequest
            {
                Entity = entity,
                PrimaryAttribute = new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200, DisplayName = Label("Name") },
            });
            client.Execute(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new IntegerAttributeMetadata { LogicalName = "pb_value", SchemaName = "pb_Value", DisplayName = Label("Value") },
            });
            return _table = name;
        }
        finally
        {
            TableLock.Release();
        }
    }

    private static Entity Row(string table, string name, int value, string? partition = null, Guid? id = null)
    {
        var e = new Entity(table) { ["pb_name"] = name, ["pb_value"] = value };
        if (id is not null) e.Id = id.Value;
        if (partition is not null) e["partitionid"] = partition;
        return e;
    }

    [Fact]
    public async Task Create_returns_session_token_and_Retrieve_finds_the_row_by_partition()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var table = await TableAsync(client);

        var response = (CreateResponse)client.Execute(new CreateRequest { Target = Row(table, "a", 1, "p1") });
        response.Results[SessionToken].Should().BeOfType<string>().Which.Should().StartWith("0:");
        var id = response.id;

        // partitionId as a request parameter.
        var byParameter = (RetrieveResponse)client.Execute(new RetrieveRequest
        {
            Target = new EntityReference(table, id), ColumnSet = new ColumnSet("pb_name", "partitionid"),
            Parameters = { ["partitionId"] = "p1" },
        });
        byParameter.Entity.GetAttributeValue<string>("pb_name").Should().Be("a");
        byParameter.Entity.GetAttributeValue<string>("partitionid").Should().Be("p1");

        // partitionid in the key attributes.
        var byKey = (RetrieveResponse)client.Execute(new RetrieveRequest
        {
            Target = new EntityReference(table, new KeyAttributeCollection { { table + "id", id }, { "partitionid", "p1" } }),
            ColumnSet = new ColumnSet("pb_name"),
        });
        byKey.Entity.GetAttributeValue<string>("pb_name").Should().Be("a");

        // Another partition does not find the row.
        var act = () => client.Execute(new RetrieveRequest
        {
            Target = new EntityReference(table, id), ColumnSet = new ColumnSet("pb_name"), Parameters = { ["partitionId"] = "other" },
        });
        act.Should().Throw<FaultException<OrganizationServiceFault>>().Which.Detail.ErrorCode.Should().Be(ErrorCodes.ObjectDoesNotExist);

        var update = (UpdateResponse)client.Execute(new UpdateRequest
        {
            Target = new Entity(table, id) { ["pb_value"] = 2 }, Parameters = { ["partitionId"] = "p1" },
        });
        update.Results[SessionToken].Should().BeOfType<string>().Which.Should().StartWith("0:");

        var delete = client.Execute(new DeleteRequest { Target = new EntityReference(table, id), Parameters = { ["partitionId"] = "p1" } });
        delete.Results.Should().NotContainKey(SessionToken);
    }

    [Fact]
    public async Task Bulk_messages_create_update_and_delete_targets()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var table = await TableAsync(client);
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

        var created = (CreateMultipleResponse)client.Execute(new CreateMultipleRequest
        {
            Targets = new EntityCollection(ids.Select((id, i) => Row(table, "bulk" + i, i, "pb", id)).ToList()) { EntityName = table },
        });
        created.Ids.Should().Equal(ids);
        created.Results[SessionToken].Should().BeOfType<string>().Which.Should().StartWith("0:");

        client.Execute(new UpdateMultipleRequest
        {
            Targets = new EntityCollection(ids.Select((id, i) => new Entity(table, id) { ["pb_value"] = 10 + i, ["partitionid"] = "pb" }).ToList()) { EntityName = table },
        });
        var rows = client.RetrieveMultiple(new QueryExpression(table)
        {
            ColumnSet = new ColumnSet("pb_value"),
            Criteria = { Conditions = { new ConditionExpression(table + "id", ConditionOperator.In, ids.Cast<object>().ToArray()) } },
        }).Entities;
        rows.Select(r => r.GetAttributeValue<int>("pb_value")).Order().Should().Equal(10, 11, 12);

        client.Execute(new DeleteMultipleRequest
        {
            Targets = new EntityReferenceCollection(ids.Select(id => new EntityReference(table, id)).ToList()),
            Parameters = { ["partitionId"] = "pb" },
        });
        client.RetrieveMultiple(new QueryExpression(table)
        {
            Criteria = { Conditions = { new ConditionExpression(table + "id", ConditionOperator.In, ids.Cast<object>().ToArray()) } },
        }).Entities.Should().BeEmpty();
    }

    [Fact]
    public async Task Partial_failure_carries_BulkApiErrorDetails_in_the_fault()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        var table = await TableAsync(client);
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var targets = new EntityCollection(new List<Entity>
        {
            Row(table, "ok", 1, "pf", good),
            Row(table, "bad", 2, "pf/slash", bad), // '/' is not allowed in a partition id
        }) { EntityName = table };

        var act = () => client.Execute(new CreateMultipleRequest { Targets = targets });

        var fault = act.Should().Throw<FaultException<OrganizationServiceFault>>().Which;
        fault.Detail.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Detail.ErrorDetails.Should().ContainKey("Plugin.BulkApiErrorDetails");
        var details = JsonSerializer.Deserialize<JsonElement>((string)fault.Detail.ErrorDetails["Plugin.BulkApiErrorDetails"]);
        details.GetArrayLength().Should().Be(1);
        details[0].GetProperty("RequestIndex").GetInt32().Should().Be(1);
        details[0].GetProperty("Id").GetString().Should().Be(bad.ToString());
        details[0].GetProperty("StatusCode").GetInt32().Should().Be(400);

        // The other target was written (failures do not stop the rest).
        client.Execute(new DeleteRequest { Target = new EntityReference(table, good), Parameters = { ["partitionId"] = "pf" } });
    }
}

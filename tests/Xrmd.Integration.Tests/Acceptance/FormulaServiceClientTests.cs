using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xunit.Abstractions;

namespace Xrmd.Integration.Tests.Acceptance;

/// <summary>
/// Plan 2.12c, Task 4: formula columns through the real Dataverse <see cref="ServiceClient"/> (Kestrel on 127.0.0.1:80). The table
/// and its formula columns are made with CreateEntity and CreateAttribute over the client, so the definitions go through the
/// server's Power Fx translator; then Retrieve, RetrieveMultiple with a FetchXML and a QueryExpression filter and order, and
/// RetrieveAttribute.
/// </summary>
/// <remarks>Skip with <c>dotnet test --filter Category!=Acceptance</c>.</remarks>
[Collection("serviceclient")]
[Trait("Category", "Acceptance")]
public sealed class FormulaServiceClientTests(ServiceClientFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        fx.DrainRequestLog();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        foreach (var line in fx.DrainRequestLog()) output.WriteLine(line);
        return Task.CompletedTask;
    }

    private ServiceClient Connect() =>
        new(new Uri(ServiceClientFixture.BaseUrl), _ => Task.FromResult(DevTokenFactory.Create(fx.AdminObjectId, ServiceClientFixture.BaseUrl)),
            useUniqueInstance: true, new TestOutputLogger(output));

    private static Label Text(string text) => new(text, 1033);

    private static void Add(ServiceClient client, string table, AttributeMetadata attribute) =>
        client.Execute(new CreateAttributeRequest { EntityName = table, Attribute = attribute });

    private static T Formula<T>(T attribute, string formula) where T : AttributeMetadata
    {
        attribute.SourceType = ComputedColumnRules.Formula;
        attribute.SchemaName = attribute.LogicalName;
        attribute.DisplayName = Text(attribute.LogicalName);
        ComputedColumnRules.SetFormula(attribute, formula);
        return attribute;
    }

    [Fact]
    public async Task ServiceClient_formula_acceptance()
    {
        using var client = Connect();
        client.IsReady.Should().BeTrue(client.LastError);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);

        var table = "new_fsc" + Guid.NewGuid().ToString("N")[..8];
        client.Execute(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = table, SchemaName = table, DisplayName = Text("Table " + table), DisplayCollectionName = Text("Tables " + table),
                OwnershipType = OwnershipTypes.UserOwned,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Text("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });
        Add(client, table, new StringAttributeMetadata { LogicalName = "new_code", SchemaName = "new_Code", DisplayName = Text("Code"), MaxLength = 100 });
        Add(client, table, new IntegerAttributeMetadata { LogicalName = "new_qty", SchemaName = "new_Qty", DisplayName = Text("Quantity") });
        Add(client, table, new MoneyAttributeMetadata { LogicalName = "new_price", SchemaName = "new_Price", DisplayName = Text("Price"), PrecisionSource = 2 });
        Add(client, table, Formula(new StringAttributeMetadata { LogicalName = "new_fxlabel", MaxLength = 200 }, "Upper(new_code) & \"-\" & Text(new_qty, \"0\")"));
        Add(client, table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxtotal", Precision = 2 }, "Decimal(new_price) * new_qty"));

        var ids = new Dictionary<string, Guid>();
        foreach (var (code, price, qty) in new[] { ("a1", 10m, 3), ("b2", 25m, 4), ("c3", 7.5m, 2) })
            ids[code] = client.Create(new Entity(table)
            {
                ["new_name"] = code, ["new_code"] = code, ["new_price"] = new Money(price), ["new_qty"] = qty, ["new_fxtotal"] = 1m,
            });

        // Retrieve: the computed values (the value sent on Create was ignored).
        var b2 = client.Retrieve(table, ids["b2"], new ColumnSet("new_fxlabel", "new_fxtotal"));
        b2.GetAttributeValue<string>("new_fxlabel").Should().Be("B2-4");
        b2.GetAttributeValue<decimal>("new_fxtotal").Should().Be(100m);

        // RetrieveAttribute: a formula column, read-only, with its definition as sent (the API takes it without '=', Q1 finding 8).
        var label = ((RetrieveAttributeResponse)client.Execute(new RetrieveAttributeRequest { EntityLogicalName = table, LogicalName = "new_fxlabel" }))
            .AttributeMetadata;
        label.SourceType.Should().Be(3);
        label.IsValidForCreate.Should().BeFalse();
        label.IsValidForUpdate.Should().BeFalse();
        ((StringAttributeMetadata)label).FormulaDefinition.Should().Be("Upper(new_code) & \"-\" & Text(new_qty, \"0\")");

        // RetrieveMultiple, FetchXML: total > 20 by total descending.
        var fetch = $"""
            <fetch><entity name="{table}"><attribute name="new_code" /><attribute name="new_fxtotal" />
              <filter><condition attribute="new_fxtotal" operator="gt" value="20" /></filter>
              <order attribute="new_fxtotal" descending="true" /></entity></fetch>
            """;
        client.RetrieveMultiple(new FetchExpression(fetch)).Entities
            .Select(e => (e.GetAttributeValue<string>("new_code"), e.GetAttributeValue<decimal>("new_fxtotal")))
            .Should().Equal(("b2", 100m), ("a1", 30m));

        // RetrieveMultiple, QueryExpression: a filter on the text formula, ordered by it.
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet("new_fxlabel") };
        query.Criteria.AddCondition("new_fxlabel", ConditionOperator.In, "A1-3", "C3-2");
        query.AddOrder("new_fxlabel", OrderType.Descending);
        client.RetrieveMultiple(query).Entities.Select(e => e.GetAttributeValue<string>("new_fxlabel")).Should().Equal("C3-2", "A1-3");
    }
}

using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 3.5 UI-1f, task 4b: aggregate FetchXML results carry a formatted value keyed by the alias, as Dataverse does, so that the
/// charts show labels and names instead of option values and ids: groupby choice/boolean → the label, groupby lookup → the target's
/// primary name (and <see cref="EntityReference.Name"/>), date groupings other than day → the integer, aggregates → the number.
/// </summary>
[Collection("xrmd")]
public sealed class AggregateFormattedValueTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Table = "pb_aggfmt";
    private const string Api = "/api/data/v9.2/";
    private const string FormattedValue = "@OData.Community.Display.V1.FormattedValue";
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private readonly string _tag = "AF" + Guid.NewGuid().ToString("N")[..10];
    private OrganizationService _svc = null!;
    private Guid _adminId;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await EnsureTableAsync(Store);
        _svc = TestServices.NewOrganizationService(fx.App.Services, fx.AdminObjectId);
        await using var conn = await fx.App.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT systemuserid FROM data.systemuser WHERE azureactivedirectoryobjectid = @oid");
        cmd.AddParameter("@oid", DbType.Guid, fx.AdminObjectId);
        _adminId = (Guid)(await cmd.ExecuteScalarAsync())!;

        // kind 1 Alpha ← 1, 3; 2 Beta ← 2, 4 (and a fifth row with no kind); flag true ← 1, 2; count 1..5; on: March, March, April...
        for (var i = 1; i <= 5; i++)
            await _svc.CreateAsync(new Entity(Table)
            {
                ["pb_name"] = $"{_tag}-{i}",
                ["pb_kind"] = i == 5 ? null : new OptionSetValue(i % 2 == 1 ? 1 : 2),
                ["pb_flag"] = i <= 2,
                ["pb_userid"] = i <= 3 ? new EntityReference("systemuser", _adminId) : null,
                ["pb_count"] = i * 1000,
                ["pb_on"] = new DateTime(2026, i <= 2 ? 3 : 4, 10 + i, 12, 0, 0, DateTimeKind.Utc),
            });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Creates <c>pb_aggfmt</c> (user-owned; pb_name String(200), pb_kind Picklist 1 Alpha/2 Beta, pb_flag Boolean Done/Open,
    /// pb_userid Lookup→systemuser, pb_count Integer, pb_on DateTime) unless it exists.
    /// </summary>
    private static async Task EnsureTableAsync(IMetadataStore store)
    {
        await Lock.WaitAsync();
        try
        {
            if (store.GetEntity(Table) is not null) return;
            var entity = new EntityMetadata { LogicalName = Table, SchemaName = "pb_AggFmt", OwnershipType = OwnershipTypes.UserOwned };
            entity.SetAttributes(
            [
                new PicklistAttributeMetadata
                {
                    LogicalName = "pb_kind",
                    SchemaName = "pb_Kind",
                    OptionSet = new OptionSetMetadata(new OptionMetadataCollection
                    {
                        new OptionMetadata(SystemAttributes.CreateLabel("Alpha", 1033), 1),
                        new OptionMetadata(SystemAttributes.CreateLabel("Beta", 1033), 2),
                    }) { IsGlobal = false, OptionSetType = OptionSetType.Picklist },
                },
                new BooleanAttributeMetadata
                {
                    LogicalName = "pb_flag",
                    SchemaName = "pb_Flag",
                    OptionSet = new BooleanOptionSetMetadata(
                        new OptionMetadata(SystemAttributes.CreateLabel("Done", 1033), 1),
                        new OptionMetadata(SystemAttributes.CreateLabel("Open", 1033), 0)),
                },
                new LookupAttributeMetadata { LogicalName = "pb_userid", SchemaName = "pb_UserId", Targets = ["systemuser"] },
                new IntegerAttributeMetadata { LogicalName = "pb_count", SchemaName = "pb_Count" },
                new DateTimeAttributeMetadata { LogicalName = "pb_on", SchemaName = "pb_On" },
            ]);
            await store.CreateEntityAsync(entity, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200 });
        }
        finally
        {
            Lock.Release();
        }
    }

    private string Fetch(string attributes) =>
        $"<fetch aggregate='true'><entity name='{Table}'>{attributes}" +
        $"<filter><condition attribute='pb_name' operator='like' value='{_tag}%' /></filter></entity></fetch>";

    private async Task<List<Entity>> RunAsync(string attributes) =>
        (await _svc.RetrieveMultipleAsync(new FetchExpression(Fetch(attributes)))).Entities.ToList();

    private static object? Value(Entity e, string alias) => e.GetAttributeValue<AliasedValue>(alias)?.Value;

    [Fact]
    public async Task Aggregate_groupby_choice_has_label_formatted_value()
    {
        var rows = await RunAsync("<attribute name='pb_kind' alias='kind' groupby='true' /><attribute name='pb_count' alias='total' aggregate='sum' />");

        rows.Should().HaveCount(3);
        var alpha = rows.Single(e => Value(e, "kind") is OptionSetValue { Value: 1 });
        alpha.FormattedValues["kind"].Should().Be("Alpha");
        alpha.FormattedValues["total"].Should().Be("4,000");
        rows.Single(e => Value(e, "kind") is OptionSetValue { Value: 2 }).FormattedValues["kind"].Should().Be("Beta");
        // The group of the rows without a kind has no value, hence no formatted value.
        rows.Single(e => Value(e, "kind") is null).FormattedValues.Should().NotContainKey("kind");
    }

    [Fact]
    public async Task Aggregate_groupby_lookup_has_name_formatted_value()
    {
        var rows = await RunAsync("<attribute name='pb_userid' alias='user' groupby='true' /><attribute name='pb_count' alias='n' aggregate='count' />");

        rows.Should().HaveCount(2);
        var user = rows.Single(e => Value(e, "user") is not null);
        var reference = (EntityReference)Value(user, "user")!;
        reference.Id.Should().Be(_adminId);
        reference.Name.Should().Be(XrmdFixture.AdminName);
        user.FormattedValues["user"].Should().Be(XrmdFixture.AdminName);
        user.FormattedValues["n"].Should().Be("3");

        // Owner (polymorphic): grouped by id and type, named by the target's primary name.
        var owners = await RunAsync("<attribute name='ownerid' alias='owner' groupby='true' /><attribute name='pb_count' alias='n' aggregate='count' />");
        var owner = owners.Should().ContainSingle().Subject;
        ((EntityReference)Value(owner, "owner")!).Name.Should().Be(XrmdFixture.AdminName);
        owner.FormattedValues["owner"].Should().Be(XrmdFixture.AdminName);
    }

    [Fact]
    public async Task Aggregate_groupby_lookup_on_link_entity_has_name_formatted_value()
    {
        // UI-1f final review M4: the group-by lookup sits on a link-entity, so the name subquery is correlated to the link alias.
        await using var conn = await fx.App.Services.GetRequiredService<IDbConnectionFactory>().OpenAsync();
        await using var cmd = TestSql.Command(conn, "SELECT bu.businessunitid, bu.name FROM data.systemuser u JOIN data.businessunit bu ON bu.businessunitid = u.businessunitid WHERE u.systemuserid = @id");
        cmd.AddParameter("@id", DbType.Guid, _adminId);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        var (unitId, unitName) = (rd.GetGuid(0), rd.GetString(1));

        var rows = await RunAsync("<attribute name='pb_count' alias='n' aggregate='count' />" +
                                  "<link-entity name='systemuser' from='systemuserid' to='pb_userid' alias='u'>" +
                                  "<attribute name='businessunitid' alias='unit' groupby='true' /></link-entity>");

        var row = rows.Should().ContainSingle("the inner link keeps the three rows with a user, all of one business unit").Subject;
        var reference = (EntityReference)Value(row, "unit")!;
        reference.Id.Should().Be(unitId);
        reference.LogicalName.Should().Be("businessunit");
        reference.Name.Should().Be(unitName);
        row.FormattedValues["unit"].Should().Be(unitName);
        row.FormattedValues["n"].Should().Be("3");
    }

    [Fact]
    public async Task Aggregate_groupby_boolean_has_label_formatted_value()
    {
        var rows = await RunAsync("<attribute name='pb_flag' alias='flag' groupby='true' /><attribute name='pb_count' alias='n' aggregate='count' />");

        rows.Should().HaveCount(2);
        rows.Single(e => Value(e, "flag") is true).FormattedValues["flag"].Should().Be("Done");
        rows.Single(e => Value(e, "flag") is false).FormattedValues["flag"].Should().Be("Open");
    }

    [Fact]
    public async Task Aggregate_count_has_formatted_value()
    {
        var row = (await RunAsync("<attribute name='pb_aggfmtid' alias='n' aggregate='count' />" +
                                  "<attribute name='pb_kind' alias='kinds' aggregate='countcolumn' />" +
                                  "<attribute name='pb_count' alias='total' aggregate='sum' />" +
                                  "<attribute name='pb_count' alias='average' aggregate='avg' />")).Should().ContainSingle().Subject;

        row.FormattedValues["n"].Should().Be("5");
        row.FormattedValues["kinds"].Should().Be("4");
        row.FormattedValues["total"].Should().Be("15,000");
        // AVG of an Integer is a decimal (3000.0000000000): formatted as a whole number, as the Integer column is.
        row.FormattedValues["average"].Should().Be("3,000");
    }

    [Fact]
    public async Task Aggregate_groupby_month_formatted_value_is_integer_text()
    {
        var rows = await RunAsync("<attribute name='pb_on' alias='month' groupby='true' dategrouping='month' />" +
                                  "<attribute name='pb_on' alias='day' groupby='true' dategrouping='day' />" +
                                  "<attribute name='pb_count' alias='n' aggregate='count' />");

        rows.Should().HaveCount(5);
        var first = rows.Single(e => Value(e, "day") is DateTime { Day: 11 });
        first.FormattedValues["month"].Should().Be("3");
        first.FormattedValues["day"].Should().Be("3/11/2026");
        rows.Single(e => Value(e, "day") is DateTime { Day: 15 }).FormattedValues["month"].Should().Be("4");
    }

    [Fact]
    public async Task Aggregate_fetchxml_over_web_api_has_formatted_value_annotations()
    {
        var set = Store.GetEntity(Table)!.EntitySetName;
        var fetch = Fetch("<attribute name='pb_kind' alias='kind' groupby='true' /><attribute name='pb_userid' alias='user' groupby='true' />" +
                          "<attribute name='pb_count' alias='n' aggregate='count' /><order alias='kind' />");
        var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}{set}?fetchXml={Uri.EscapeDataString(fetch)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        req.Headers.TryAddWithoutValidation("Prefer", "odata.include-annotations=\"*\"");

        var res = await fx.Client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var rows = JsonNode.Parse(body)!["value"]!.AsArray().Select(r => r!.AsObject()).ToList();
        var alpha = rows.Single(r => r["kind"]?.GetValue<int>() == 1 && r["user"] is not null);
        alpha["kind" + FormattedValue]!.GetValue<string>().Should().Be("Alpha");
        alpha["user"]!.GetValue<string>().Should().Be(_adminId.ToString());
        alpha["user" + FormattedValue]!.GetValue<string>().Should().Be(XrmdFixture.AdminName);
        alpha["n"]!.GetValue<int>().Should().Be(2);
        alpha["n" + FormattedValue]!.GetValue<string>().Should().Be("2");
    }
}

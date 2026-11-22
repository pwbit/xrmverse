using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests;

/// <summary>The seeded system views of the queue tables (plan 4.1a, task 7, ruling R20): they exist, each table has a default, a second seed adds none.</summary>
[Collection("xrmd")]
public sealed class QueueViewSeedTests(XrmdFixture fx)
{
    private async Task<List<(string Name, string Table, bool IsDefault)>> ViewsAsync()
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        var ids = string.Join(", ", QueueSchema.SystemViews.Select(v => $"'{v.Id}'"));
        await using var cmd = TestSql.Command(conn, $"SELECT name, returnedtypecode, isdefault FROM data.savedquery WHERE savedqueryid IN ({ids})");
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string, bool)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1), Convert.ToBoolean(reader.GetValue(2))));
        return rows;
    }

    [Fact]
    public async Task The_queue_views_exist_and_queueitem_has_a_default_view()
    {
        var rows = await ViewsAsync();
        rows.Should().BeEquivalentTo(
        [
            ("Active Queue Items", "queueitem", true),
            ("All Queue Items", "queueitem", false),
            ("Active Queues", "queue", true),
        ]);
    }

    [Fact]
    public async Task A_second_seed_does_not_duplicate_the_views()
    {
        var services = fx.App.Services;
        await new SystemSeed(services.GetRequiredService<IMetadataStore>(), services.GetRequiredService<IDbConnectionFactory>())
            .EnsureAsync(new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), fx.AdminObjectId, "Ada"));
        (await ViewsAsync()).Should().HaveCount(3);
    }
}

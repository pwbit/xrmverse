using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests;

/// <summary>The seeded forms and views of email, queue and queue item (plan 4.1d, task 1).</summary>
[Collection("xrmd")]
public sealed class PlatformLayoutSeedTests(XrmdFixture fx)
{
    private async Task<List<object?[]>> RowsAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            rows.Add(row);
        }
        return rows;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string FormIds => string.Join(", ", PlatformLayouts.Forms.Select(f => $"'{f.Id}'"));

    private Task SeedAgainAsync()
    {
        var services = fx.App.Services;
        return new SystemSeed(services.GetRequiredService<IMetadataStore>(), services.GetRequiredService<IDbConnectionFactory>())
            .EnsureAsync(new SeedOptions(XrmdFixture.OrgName, Guid.NewGuid(), fx.AdminObjectId, "Ada"));
    }

    [Fact]
    public async Task Seeded_forms_exist_as_default_active_main_forms()
    {
        var rows = await RowsAsync($"SELECT name, objecttypecode, type, isdefault, formactivationstate, ismanaged FROM data.systemform WHERE systemformid IN ({FormIds})");
        rows.Select(r => (r[0], r[1], Convert.ToInt32(r[2]), Convert.ToBoolean(r[3]), Convert.ToInt32(r[4]), Convert.ToBoolean(r[5]))).Should().BeEquivalentTo(
        [
            ((object?)"Email", (object?)"email", 2, true, 1, false),
            ("Queue", "queue", 2, true, 1, false),
            ("Queue Item", "queueitem", 2, true, 1, false),
            ("Task", "task", 2, true, 1, false),
            ("Phone Call", "phonecall", 2, true, 1, false),
            ("Appointment", "appointment", 2, true, 1, false),
        ]);
    }

    [Fact]
    public async Task Seeded_email_views_exist_and_open_emails_is_the_default()
    {
        var ids = string.Join(", ", PlatformLayouts.Views.Select(v => $"'{v.Id}'"));
        var rows = await RowsAsync($"SELECT name, returnedtypecode, isdefault FROM data.savedquery WHERE savedqueryid IN ({ids})");
        rows.Select(r => (r[0], r[1], Convert.ToBoolean(r[2]))).Should().BeEquivalentTo(
        [
            ((object?)"Open Emails", (object?)"email", true), ("All Emails", "email", false), ("Sent Emails", "email", false),
            ("Received Emails", "email", false), ("All Queues", "queue", false),
            ("Open Tasks", "task", true), ("All Tasks", "task", false), ("Open Phone Calls", "phonecall", true),
            ("All Phone Calls", "phonecall", false), ("Open Appointments", "appointment", true), ("All Appointments", "appointment", false),
        ]);
    }

    [Fact]
    public async Task A_second_seed_adds_nothing_and_keeps_an_edit()
    {
        var id = PlatformLayouts.EmailFormId;
        try
        {
            await ExecAsync($"UPDATE data.systemform SET name = 'Mine' WHERE systemformid = '{id}'");
            var before = (await RowsAsync("SELECT COUNT(*) FROM data.systemform"))[0][0];
            await SeedAgainAsync();
            (await RowsAsync("SELECT COUNT(*) FROM data.systemform"))[0][0].Should().Be(before);
            (await RowsAsync($"SELECT name FROM data.systemform WHERE systemformid = '{id}'"))[0][0].Should().Be("Mine");
        }
        finally { await ExecAsync($"UPDATE data.systemform SET name = 'Email' WHERE systemformid = '{id}'"); }
    }

    [Fact]
    public async Task A_deleted_seeded_form_comes_back_at_the_next_seed()
    {
        var id = PlatformLayouts.QueueItemFormId;
        await ExecAsync($"DELETE FROM data.systemform WHERE systemformid = '{id}'");
        await SeedAgainAsync();
        (await RowsAsync($"SELECT name FROM data.systemform WHERE systemformid = '{id}'"))[0][0].Should().Be("Queue Item");
    }

    [Fact]
    public async Task A_seeded_form_does_not_take_the_default_from_an_existing_form()
    {
        var seeded = PlatformLayouts.EmailFormId;
        var custom = Guid.NewGuid();
        var yes = TestSql.Dialect.BooleanLiteral(true);
        const string columns = "objecttypecode, formxml, type, formactivationstate, ismanaged, owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode";
        try
        {
            await ExecAsync($"INSERT INTO data.systemform (systemformid, name, isdefault, {columns}) " +
                $"SELECT '{custom}', 'Custom email', {yes}, {columns} FROM data.systemform WHERE systemformid = '{seeded}'");
            await ExecAsync($"DELETE FROM data.systemform WHERE systemformid = '{seeded}'");
            await SeedAgainAsync();
            var rows = await RowsAsync($"SELECT systemformid, isdefault FROM data.systemform WHERE systemformid IN ('{seeded}', '{custom}')");
            rows.Select(r => (Guid.Parse(r[0]!.ToString()!), Convert.ToBoolean(r[1]))).Should().BeEquivalentTo([(seeded, false), (custom, true)]);
            (await RowsAsync($"SELECT COUNT(*) FROM data.systemform WHERE objecttypecode = 'email' AND type = 2 AND isdefault = {yes}"))[0][0]
                .Should().BeEquivalentTo(1);
        }
        finally
        {
            await ExecAsync($"DELETE FROM data.systemform WHERE systemformid IN ('{seeded}', '{custom}')");
            await SeedAgainAsync();
        }
        (await RowsAsync($"SELECT isdefault FROM data.systemform WHERE systemformid = '{seeded}'")).Select(r => Convert.ToBoolean(r[0])).Should().Equal(true);
    }

    [Fact]
    public async Task A_seeded_view_does_not_take_the_default_from_an_existing_view()
    {
        var seeded = PlatformLayouts.Views.Single(v => v.Name == "Open Emails").Id;
        var custom = Guid.NewGuid();
        var yes = TestSql.Dialect.BooleanLiteral(true);
        const string columns = "returnedtypecode, fetchxml, layoutxml, querytype, ismanaged, owningbusinessunit, createdon, createdby, modifiedon, modifiedby, statecode, statuscode";
        try
        {
            await ExecAsync($"INSERT INTO data.savedquery (savedqueryid, name, isdefault, {columns}) " +
                $"SELECT '{custom}', 'Custom emails', {yes}, {columns} FROM data.savedquery WHERE savedqueryid = '{seeded}'");
            await ExecAsync($"DELETE FROM data.savedquery WHERE savedqueryid = '{seeded}'");
            await SeedAgainAsync();
            var rows = await RowsAsync($"SELECT savedqueryid, isdefault FROM data.savedquery WHERE savedqueryid IN ('{seeded}', '{custom}')");
            rows.Select(r => (Guid.Parse(r[0]!.ToString()!), Convert.ToBoolean(r[1]))).Should().BeEquivalentTo([(seeded, false), (custom, true)]);
            (await RowsAsync($"SELECT COUNT(*) FROM data.savedquery WHERE returnedtypecode = 'email' AND querytype = 0 AND isdefault = {yes}"))[0][0]
                .Should().BeEquivalentTo(1);
        }
        finally
        {
            await ExecAsync($"DELETE FROM data.savedquery WHERE savedqueryid IN ('{seeded}', '{custom}')");
            await SeedAgainAsync();
        }
        (await RowsAsync($"SELECT isdefault FROM data.savedquery WHERE savedqueryid = '{seeded}'")).Select(r => Convert.ToBoolean(r[0])).Should().Equal(true);
    }
}

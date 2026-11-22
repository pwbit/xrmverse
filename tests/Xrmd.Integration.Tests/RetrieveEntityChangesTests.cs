using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Plan 2.13, task 3: the RetrieveEntityChanges message on a tracked table: the first sync, the deltas (new or updated rows by
/// version, then the deletions), the token checks (expired, untracked), the Global read demand, paging and field security.
/// </summary>
[Collection("xrmd")]
public sealed class RetrieveEntityChangesTests(XrmdFixture fx) : IAsyncLifetime
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => PluginTestHelper.ClearAsync(fx);

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private async Task<string> NewTableAsync(string label, bool tracked = true, bool securedColumn = false)
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
        if (securedColumn)
            await Admin.ExecuteAsync(new CreateAttributeRequest
            {
                EntityName = name,
                Attribute = new StringAttributeMetadata
                {
                    LogicalName = "new_secret", SchemaName = "new_Secret", MaxLength = 100, DisplayName = Label("Secret"), IsSecured = true,
                },
            });
        if (tracked)
            await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = name, ChangeTrackingEnabled = true } });
        return name;
    }

    private static async Task<BusinessEntityChanges> ChangesAsync(OrganizationService service, string table, string? token = null,
        ColumnSet? columns = null, PagingInfo? paging = null, bool metadataVersion = false)
    {
        var request = new RetrieveEntityChangesRequest
        {
            EntityName = table, Columns = columns ?? new ColumnSet("new_name"), DataVersion = token, GetGlobalMetadataVersion = metadataVersion,
        };
        if (paging is not null) request.PageInfo = paging;
        var response = await service.ExecuteAsync(request);
        return response.Should().BeOfType<RetrieveEntityChangesResponse>().Subject.EntityChanges;
    }

    private Task<BusinessEntityChanges> ChangesAsync(string table, string? token = null, ColumnSet? columns = null, PagingInfo? paging = null,
        bool metadataVersion = false) => ChangesAsync(Admin, table, token, columns, paging, metadataVersion);

    private static List<Guid> Updated(BusinessEntityChanges changes) =>
        changes.Changes.OfType<NewOrUpdatedItem>().Select(i => i.NewOrUpdatedEntity.Id).ToList();

    private static List<Guid> Deleted(BusinessEntityChanges changes) =>
        changes.Changes.OfType<RemovedOrDeletedItem>().Select(i => i.RemovedItem.Id).ToList();

    private static long TokenVersion(string token)
    {
        ChangeToken.TryParse(token, out var version).Should().BeTrue(token);
        return version;
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task<long> RowVersionAsync(string table, Guid id) =>
        (long)(await ScalarAsync($"SELECT CAST(versionnumber AS bigint) FROM data.[{table}] WHERE [{table}id] = @id", ("@id", id)))!;

    private async Task<List<Guid>> CreateAsync(string table, int count, string prefix = "r")
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++) ids.Add(await Admin.CreateAsync(new Entity(table) { ["new_name"] = $"{prefix}{i}" }));
        return ids;
    }

    // ---- tests --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task First_call_returns_every_row_and_a_token()
    {
        var table = await NewTableAsync("ca");
        var ids = await CreateAsync(table, 3);

        var changes = await ChangesAsync(table);

        Updated(changes).Should().Equal(ids); // created in that order: versions ascending
        Deleted(changes).Should().BeEmpty();
        changes.MoreRecords.Should().BeFalse();
        changes.DataToken.Should().MatchRegex(@"^\d+!\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
        TokenVersion(changes.DataToken).Should().BeGreaterThanOrEqualTo(await RowVersionAsync(table, ids[^1]));
        var first = changes.Changes.OfType<NewOrUpdatedItem>().First();
        first.Type.Should().Be(ChangeType.NewOrUpdated);
        first.NewOrUpdatedEntity.LogicalName.Should().Be(table);
        first.NewOrUpdatedEntity.GetAttributeValue<string>("new_name").Should().Be("r0");
        first.NewOrUpdatedEntity.RowVersion.Should().Be((await RowVersionAsync(table, ids[0])).ToString(System.Globalization.CultureInfo.InvariantCulture));
        changes.GlobalMetadataVersion.Should().BeNull();
    }

    [Fact]
    public async Task Next_call_returns_only_created_updated_and_deleted()
    {
        var table = await NewTableAsync("cn");
        var ids = await CreateAsync(table, 3);
        var token = (await ChangesAsync(table)).DataToken;

        await Admin.UpdateAsync(new Entity(table, ids[0]) { ["new_name"] = "changed" });
        var created = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "new" });
        await Admin.DeleteAsync(table, ids[1]);

        var changes = await ChangesAsync(table, token);

        changes.Changes.Should().HaveCount(3);
        changes.Changes[0].Should().BeOfType<NewOrUpdatedItem>().Which.NewOrUpdatedEntity.Id.Should().Be(ids[0]);
        ((NewOrUpdatedItem)changes.Changes[0]).NewOrUpdatedEntity.GetAttributeValue<string>("new_name").Should().Be("changed");
        changes.Changes[1].Should().BeOfType<NewOrUpdatedItem>().Which.NewOrUpdatedEntity.Id.Should().Be(created);
        var removed = changes.Changes[2].Should().BeOfType<RemovedOrDeletedItem>().Subject;
        removed.Type.Should().Be(ChangeType.RemoveOrDeleted);
        removed.RemovedItem.LogicalName.Should().Be(table);
        removed.RemovedItem.Id.Should().Be(ids[1]);
        TokenVersion(changes.DataToken).Should().BeGreaterThan(TokenVersion(token));

        var again = await ChangesAsync(table, changes.DataToken);
        again.Changes.Should().BeEmpty();
        again.MoreRecords.Should().BeFalse();
    }

    [Fact]
    public async Task Created_then_deleted_returns_only_the_deletion()
    {
        var table = await NewTableAsync("cd");
        var token = (await ChangesAsync(table)).DataToken;
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "short-lived" });
        await Admin.DeleteAsync(table, id);

        var changes = await ChangesAsync(table, token);

        Updated(changes).Should().BeEmpty();
        Deleted(changes).Should().Equal(id);
    }

    [Fact]
    public async Task Change_committed_after_the_token_is_returned_next_time()
    {
        var table = await NewTableAsync("cc");
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "before" });
        var token = (await ChangesAsync(table)).DataToken;

        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var tx = conn.BeginTransaction();
        await using (var update = TestSql.Command(conn, tx, $"UPDATE data.[{table}] SET new_name = N'in flight' WHERE [{table}id] = @id"))
        {
            update.Parameters.AddWithValue("@id", id);
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
        }
        long inFlight;
        await using (var read = TestSql.Command(conn, tx, $"SELECT CAST(versionnumber AS bigint) FROM data.[{table}] WHERE [{table}id] = @id"))
        {
            read.Parameters.AddWithValue("@id", id);
            inFlight = (long)(await read.ExecuteScalarAsync())!;
        }

        var during = await ChangesAsync(table, token);

        Updated(during).Should().BeEmpty();
        TokenVersion(during.DataToken).Should().BeLessThan(inFlight);

        await tx.CommitAsync();
        var after = await ChangesAsync(table, during.DataToken);

        Updated(after).Should().Equal(id);
        ((NewOrUpdatedItem)after.Changes[0]).NewOrUpdatedEntity.GetAttributeValue<string>("new_name").Should().Be("in flight");
    }

    [Fact]
    public async Task Token_below_purged_version_is_expired()
    {
        var table = await NewTableAsync("cp");
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "gone" });
        var token = (await ChangesAsync(table)).DataToken;
        await Admin.DeleteAsync(table, id);
        await ScalarAsync("UPDATE meta.deleted_record SET deleted_on = DATEADD(day, -30, SYSUTCDATETIME()) WHERE object_id = @id", ("@id", id));
        await ScalarAsync("UPDATE data.organization SET expirechangetrackingindays = 7");
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            await ChangeTrackingRetention.PurgeAsync(new DataSession(conn, null), DateTime.UtcNow, default);
        }

        var message = await BadRequestAsync(() => ChangesAsync(table, token));

        message.Should().Contain("expired");
        // A sync from scratch works again, and its token is valid.
        var fresh = await ChangesAsync(table);
        (await ChangesAsync(table, fresh.DataToken)).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Token_from_before_tracking_was_enabled_is_expired()
    {
        var tracked = await NewTableAsync("ce");
        var old = (await ChangesAsync(tracked)).DataToken;
        var table = await NewTableAsync("cl", tracked: false);
        await Admin.CreateAsync(new Entity(table) { ["new_name"] = "before tracking" });
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = table, ChangeTrackingEnabled = true } });

        var message = await BadRequestAsync(() => ChangesAsync(table, old));

        message.Should().Contain("expired");
        // The token of the first sync after enabling is valid at once, even with nothing written since.
        var first = await ChangesAsync(table);
        Updated(first).Should().HaveCount(1);
        (await ChangesAsync(table, first.DataToken)).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Token_above_the_current_bound_is_expired()
    {
        // A token of a version the database has not reached (from a database since replaced by an older backup, say).
        var table = await NewTableAsync("cf");
        var fresh = (await ChangesAsync(table)).DataToken;
        var ahead = ChangeToken.Format(TokenVersion(fresh) + 1_000_000, DateTime.UtcNow);

        var message = await BadRequestAsync(() => ChangesAsync(table, ahead));

        message.Should().Contain("expired");
        (await ChangesAsync(table, fresh)).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Token_issued_before_the_last_restore_is_expired()
    {
        var table = await NewTableAsync("cr");
        await CreateAsync(table, 1);
        var before = (await ChangesAsync(table)).DataToken;
        try
        {
            await ScalarAsync("UPDATE meta.change_tracking_state SET restored_at = SYSUTCDATETIME()");

            var message = await BadRequestAsync(() => ChangesAsync(table, before));

            message.Should().Contain("expired");
            // A sync from scratch right after the restore gets a token that is valid at once (not dated before the restore).
            var fresh = await ChangesAsync(table);
            Updated(fresh).Should().HaveCount(1);
            (await ChangesAsync(table, fresh.DataToken)).Changes.Should().BeEmpty();
        }
        finally
        {
            await ScalarAsync("UPDATE meta.change_tracking_state SET restored_at = NULL");
        }
    }

    [Fact]
    public async Task Token_older_than_the_retention_is_expired()
    {
        var table = await NewTableAsync("cg");
        var version = TokenVersion((await ChangesAsync(table)).DataToken);
        await ScalarAsync("UPDATE data.organization SET expirechangetrackingindays = 7");

        var old = ChangeToken.Format(version, DateTime.UtcNow.AddDays(-8));
        (await BadRequestAsync(() => ChangesAsync(table, old))).Should().Contain("expired");
        // Without a time its age is unknown: expired too.
        (await BadRequestAsync(() => ChangesAsync(table, version.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .Should().Contain("expired");
        // Within the retention it is valid.
        var recent = ChangeToken.Format(version, DateTime.UtcNow.AddDays(-6));
        (await ChangesAsync(table, recent)).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Untracked_table_is_refused()
    {
        var table = await NewTableAsync("cu", tracked: false);

        var message = await BadRequestAsync(() => ChangesAsync(table));

        message.Should().Contain(table).And.Contain("change tracking");
    }

    [Fact]
    public async Task Garbage_token_is_refused()
    {
        var table = await NewTableAsync("cg");

        var message = await BadRequestAsync(() => ChangesAsync(table, "not-a-token"));

        message.Should().Contain("not-a-token");
    }

    [Fact]
    public async Task Caller_without_global_read_is_refused()
    {
        var table = await NewTableAsync("cs");
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ($"prvRead{table}", DepthMask.Local));

        var fault = await FaultAsync(() => ChangesAsync(ServiceFor(fx, oid), table));

        fault.HttpStatus.Should().Be(403);
        fault.ErrorCode.Should().Be(ErrorCodes.PrivilegeDenied);

        await GrantAsync(fx, user, team: false, ($"prvRead{table}", DepthMask.Global));
        (await ChangesAsync(ServiceFor(fx, oid), table)).Changes.Should().BeEmpty();
    }

    [Fact]
    public async Task Paging_walks_updates_then_deletions()
    {
        var table = await NewTableAsync("cw");
        var ids = await CreateAsync(table, 7);
        var token = (await ChangesAsync(table)).DataToken;
        foreach (var id in ids.Take(5)) await Admin.UpdateAsync(new Entity(table, id) { ["new_name"] = "u" });
        await Admin.DeleteAsync(table, ids[5]);
        await Admin.DeleteAsync(table, ids[6]);

        var pages = new List<BusinessEntityChanges>();
        var paging = new PagingInfo { Count = 2, PageNumber = 1 };
        while (true)
        {
            var page = await ChangesAsync(table, token, paging: paging);
            pages.Add(page);
            if (!page.MoreRecords || pages.Count > 10) break;
            paging = new PagingInfo { Count = 2, PageNumber = paging.PageNumber + 1, PagingCookie = page.PagingCookie };
        }

        pages.Should().HaveCount(4);
        pages.Select(p => p.MoreRecords).Should().Equal(true, true, true, false);
        pages.Select(p => p.DataToken).Distinct().Should().HaveCount(1);
        pages.SelectMany(Updated).Should().Equal(ids.Take(5));
        pages.SelectMany(Deleted).Should().Equal(ids[5], ids[6]);
        pages[2].Changes.Select(c => c.Type).Should().Equal(ChangeType.NewOrUpdated, ChangeType.RemoveOrDeleted);
    }

    [Fact]
    public async Task Page_after_the_first_needs_the_cookie()
    {
        var table = await NewTableAsync("ck");

        var message = await BadRequestAsync(() => ChangesAsync(table, paging: new PagingInfo { Count = 2, PageNumber = 2 }));

        message.Should().Contain("cookie");
    }

    [Fact]
    public async Task Writes_between_pages_do_not_change_the_page_set()
    {
        var table = await NewTableAsync("cb");
        var ids = await CreateAsync(table, 4);
        var paging = new PagingInfo { Count = 2, PageNumber = 1 };

        var first = await ChangesAsync(table, paging: paging);
        var late = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "late" });
        await Admin.UpdateAsync(new Entity(table, ids[0]) { ["new_name"] = "touched" });
        var second = await ChangesAsync(table, paging: new PagingInfo { Count = 2, PageNumber = 2, PagingCookie = first.PagingCookie });

        Updated(first).Should().Equal(ids[0], ids[1]);
        first.MoreRecords.Should().BeTrue();
        Updated(second).Should().Equal(ids[2], ids[3]);
        second.MoreRecords.Should().BeFalse();
        second.DataToken.Should().Be(first.DataToken);

        var next = await ChangesAsync(table, second.DataToken);
        Updated(next).Should().Equal(late, ids[0]);
    }

    [Fact]
    public async Task Pages_after_the_first_answer_the_issue_time_of_page_one()
    {
        // Final review of 2.15, minor 3: the DataToken of every page carries the issue time page 1 put in the cookie, whatever
        // second the later pages run in. The cookie's last part is rewritten to an hour earlier to make this deterministic.
        var table = await NewTableAsync("ci");
        await CreateAsync(table, 4);
        var first = await ChangesAsync(table, paging: new PagingInfo { Count = 2, PageNumber = 1 });
        var parts = first.PagingCookie.Split('!');
        parts.Should().HaveCount(4);
        var earlier = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[3], CultureInfo.InvariantCulture)).AddHours(-1);
        parts[3] = earlier.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var second = await ChangesAsync(table, paging: new PagingInfo { Count = 2, PageNumber = 2, PagingCookie = string.Join('!', parts) });

        second.DataToken.Should().Be(first.DataToken.Split('!')[0] + "!" + earlier.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Page_size_above_5000_is_refused()
    {
        var table = await NewTableAsync("cz");

        var message = await BadRequestAsync(() => ChangesAsync(table, paging: new PagingInfo { Count = 5001, PageNumber = 1 }));

        message.Should().Contain("5000");
    }

    [Fact]
    public async Task Secured_column_is_masked_in_changes()
    {
        var table = await NewTableAsync("cf", securedColumn: true);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "visible", ["new_secret"] = "hidden" });
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ($"prvRead{table}", DepthMask.Global));

        var changes = await ChangesAsync(ServiceFor(fx, oid), table, columns: new ColumnSet("new_name", "new_secret"));

        var entity = changes.Changes.OfType<NewOrUpdatedItem>().Single().NewOrUpdatedEntity;
        entity.Id.Should().Be(id);
        entity.GetAttributeValue<string>("new_name").Should().Be("visible");
        entity.Attributes.Should().ContainKey("new_secret");
        entity["new_secret"].Should().BeNull();
        (await ChangesAsync(table, columns: new ColumnSet("new_secret"))).Changes.OfType<NewOrUpdatedItem>().Single()
            .NewOrUpdatedEntity.GetAttributeValue<string>("new_secret").Should().Be("hidden");
    }

    [Fact]
    public async Task GetGlobalMetadataVersion_returns_the_version_stamp()
    {
        var table = await NewTableAsync("cm");

        var changes = await ChangesAsync(table, metadataVersion: true);

        var stamp = Convert.ToInt64(await ScalarAsync("SELECT [current] FROM meta.version_stamp"));
        changes.GlobalMetadataVersion.Should().Be(stamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Plugin_step_on_RetrieveEntityChanges_runs()
    {
        var table = await NewTableAsync("cx");
        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, type, "RetrieveEntityChanges", null, stage: 40, config: "invalid:changes seen by a step");

        var message = await BadRequestAsync(() => ChangesAsync(table));

        message.Should().Be("changes seen by a step");
    }
}

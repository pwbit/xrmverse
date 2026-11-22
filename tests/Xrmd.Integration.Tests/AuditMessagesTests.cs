using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Audit;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;
using static Xrmd.Integration.Tests.TestHelpers;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The audit messages (plan 13, task 5; rulings A4, A7, A8) on the SDK and the Web API: RetrieveAuditDetails,
/// RetrieveRecordChangeHistory, RetrieveAttributeChangeHistory (newest first, paging), the history of deleted records, columns and
/// tables, the privileges each message demands, DeleteRecordChangeHistory, DeleteAuditData by closed quarter, the retention purge
/// and the call from a plug-in.
/// </summary>
/// <remarks>Auditing is switched on for the organization at the start of each test and off at its end.</remarks>
[Collection("xrmd")]
public sealed class AuditMessagesTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Account = "account";
    private const string Api = "/api/data/v9.2/";
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);
    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    public async Task InitializeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await SetOrganizationAuditAsync(true);
    }

    public async Task DisposeAsync()
    {
        await PluginTestHelper.ClearAsync(fx);
        await SetOrganizationAuditAsync(false);
        await RevokeAuditPrivilegesAsync(fx);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private async Task<Guid> OrganizationIdAsync() => (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;

    private async Task SetOrganizationAuditAsync(bool enabled) =>
        await Admin.UpdateAsync(new Entity("organization", await OrganizationIdAsync()) { ["isauditenabled"] = enabled });

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>The audit ids of <paramref name="objectId"/>, oldest first.</summary>
    private async Task<List<Guid>> AuditIdsAsync(Guid objectId)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT auditid FROM data.audit WHERE objectid = @id ORDER BY createdon, auditid");
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = objectId;
        var ids = new List<Guid>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) ids.Add(rd.GetGuid(0));
        return ids;
    }

    /// <summary>An audit row written directly, dated <paramref name="createdOn"/> with time to live <paramref name="ttl"/>.</summary>
    private async Task<Guid> InsertRowAsync(DateTime createdOn, int ttl, Guid? objectId = null)
    {
        var id = Guid.NewGuid();
        var user = (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId;
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, """
            INSERT INTO data.audit (auditid, createdon, action, operation, objectid, objectidtype, objecttypecode, userid, changedata,
                transactionid, timetoliveinseconds)
            VALUES (@id, @createdon, 2, 2, @objectid, 1, 'account', @user, '{"v":1,"old":{},"new":{}}', NEWID(), @ttl)
            """);
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        cmd.Parameters.Add("@createdon", SqlDbType.DateTime2).Value = createdOn;
        cmd.Parameters.Add("@objectid", SqlDbType.UniqueIdentifier).Value = objectId ?? Guid.NewGuid();
        cmd.Parameters.Add("@user", SqlDbType.UniqueIdentifier).Value = user;
        cmd.Parameters.Add("@ttl", SqlDbType.Int).Value = ttl;
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<bool> RowExistsAsync(Guid auditId) =>
        Convert.ToInt64(await ScalarAsync("SELECT COUNT(*) FROM data.audit WHERE auditid = @id", ("@id", auditId))) == 1; // bigint on PostgreSQL

    private Task<Guid> NewAccountAsync(string name, params (string Name, object Value)[] values)
    {
        var account = new Entity(Account) { ["name"] = name };
        foreach (var (column, value) in values) account[column] = value;
        return Admin.CreateAsync(account);
    }

    /// <summary>Updates that land in distinct milliseconds, so the order of the history is the order of the calls.</summary>
    private async Task UpdateAsync(Entity entity, OrganizationService? service = null)
    {
        await Task.Delay(15);
        await (service ?? Admin).UpdateAsync(entity);
    }

    private static PagingInfo Page(int number, int count, bool total = false, string? cookie = null) =>
        new() { PageNumber = number, Count = count, ReturnTotalRecordCount = total, PagingCookie = cookie };

    private async Task<AuditDetailCollection> HistoryAsync(Guid id, PagingInfo? paging = null, string entity = Account) =>
        ((RetrieveRecordChangeHistoryResponse)await Admin.ExecuteAsync(
            new RetrieveRecordChangeHistoryRequest { Target = new EntityReference(entity, id), PagingInfo = paging })).AuditDetailCollection;

    private static AttributeAuditDetail Attribute(AuditDetail detail) => detail.Should().BeOfType<AttributeAuditDetail>().Subject;

    private static DateTime QuarterStart(DateTime utc) => new(utc.Year, (utc.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private static async Task<JsonObject> JsonAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    // ---- reading ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retrieve_audit_details_returns_attribute_detail_with_audit_record()
    {
        var id = await NewAccountAsync("Before " + _run);
        await UpdateAsync(new Entity(Account, id) { ["name"] = "After " + _run });
        var update = (await AuditIdsAsync(id))[1];

        var response = (RetrieveAuditDetailsResponse)await Admin.ExecuteAsync(new RetrieveAuditDetailsRequest { AuditId = update });

        var detail = Attribute(response.AuditDetail);
        detail.OldValue.LogicalName.Should().Be(Account);
        detail.OldValue["name"].Should().Be("Before " + _run);
        detail.NewValue["name"].Should().Be("After " + _run);
        detail.DeletedAttributes.Should().BeEmpty();

        var record = detail.AuditRecord;
        (record.LogicalName, record.Id).Should().Be((AuditTable.Name, update));
        record.GetAttributeValue<OptionSetValue>("action").Value.Should().Be(AuditActions.Update);
        record.FormattedValues["action"].Should().Be("Update");
        record.GetAttributeValue<OptionSetValue>("operation").Value.Should().Be(AuditOperations.Update);
        record.FormattedValues["operation"].Should().Be("Update");
        var createdOn = record.GetAttributeValue<DateTime>("createdon");
        createdOn.Kind.Should().Be(DateTimeKind.Utc);
        record.FormattedValues["createdon"].Should().Be(createdOn.ToString("M/d/yyyy h:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US")));
        var user = record.GetAttributeValue<EntityReference>("userid");
        (user.LogicalName, user.Id, user.Name).Should().Be(("systemuser", (await TestServices.CallerAsync(fx.App.Services, fx.AdminObjectId)).UserId,
            XrmdFixture.AdminName));
        record.GetAttributeValue<EntityReference>("objectid").Should().Match<EntityReference>(r => r.Id == id && r.LogicalName == Account);
        record.GetAttributeValue<string>("objecttypecode").Should().Be(Account);

        var unknown = await FaultAsync(() => Admin.ExecuteAsync(new RetrieveAuditDetailsRequest { AuditId = Guid.NewGuid() }));
        (unknown.HttpStatus, unknown.ErrorCode).Should().Be((404, ErrorCodes.ObjectDoesNotExist));
    }

    [Fact]
    public async Task Record_change_history_is_newest_first_and_pages()
    {
        var id = await NewAccountAsync("v0 " + _run);
        for (var i = 1; i <= 3; i++) await UpdateAsync(new Entity(Account, id) { ["name"] = $"v{i} " + _run });

        var first = await HistoryAsync(id, Page(1, 2, total: true));
        first.AuditDetails.Select(d => Attribute(d).NewValue["name"]).Should().Equal("v3 " + _run, "v2 " + _run);
        first.MoreRecords.Should().BeTrue();
        first.TotalRecordCount.Should().Be(4);
        first.PagingCookie.Should().Be("<cookie page=\"1\" />");

        var second = await HistoryAsync(id, Page(2, 2, cookie: first.PagingCookie));
        second.AuditDetails.Select(d => d.AuditRecord.GetAttributeValue<OptionSetValue>("action").Value)
            .Should().Equal(AuditActions.Update, AuditActions.Create);
        Attribute(second.AuditDetails[0]).NewValue["name"].Should().Be("v1 " + _run);
        Attribute(second.AuditDetails[1]).OldValue.Attributes.Should().BeEmpty("a create has no old values");
        second.MoreRecords.Should().BeFalse();
        second.TotalRecordCount.Should().Be(-1, "the count was not asked for");

        var all = await HistoryAsync(id);
        all.AuditDetails.Should().HaveCount(4);
        all.MoreRecords.Should().BeFalse();

        // A cookie xrmverse did not write is refused, not ignored.
        var cookie = await FaultAsync(() => HistoryAsync(id, Page(2, 2, cookie: "<cookie page=\"1\"><cookieExtensions ContinuationToken=\"x\" /></cookie>")));
        (cookie.HttpStatus, cookie.Message).Should().Match<(int, string)>(f => f.Item1 == 400 && f.Item2.Contains("PagingCookie"));
        (await FaultAsync(() => HistoryAsync(id, Page(1, 5001)))).HttpStatus.Should().Be(400);
        (await FaultAsync(() => HistoryAsync(id, Page(1, -1)))).HttpStatus.Should().Be(400, "a negative count is refused");
        (await FaultAsync(() => Admin.ExecuteAsync(new RetrieveRecordChangeHistoryRequest()))).HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Attribute_change_history_returns_only_that_column()
    {
        var id = await NewAccountAsync("n0 " + _run, ("telephone1", "100"));
        await UpdateAsync(new Entity(Account, id) { ["name"] = "n1 " + _run });
        await UpdateAsync(new Entity(Account, id) { ["telephone1"] = "200" });
        await UpdateAsync(new Entity(Account, id) { ["name"] = "n2 " + _run, ["telephone1"] = "300" });

        var response = (RetrieveAttributeChangeHistoryResponse)await Admin.ExecuteAsync(new RetrieveAttributeChangeHistoryRequest
        {
            Target = new EntityReference(Account, id), AttributeLogicalName = "telephone1", PagingInfo = Page(1, 10, total: true),
        });

        var details = response.AuditDetailCollection.AuditDetails.Select(Attribute).ToList();
        details.Should().HaveCount(3);
        response.AuditDetailCollection.TotalRecordCount.Should().Be(3);
        details.Should().OnlyContain(d => d.OldValue.Attributes.Keys.All(k => k == "telephone1") && d.NewValue.Attributes.Keys.All(k => k == "telephone1"));
        details.Select(d => d.NewValue["telephone1"]).Should().Equal("300", "200", "100");
        details[0].OldValue["telephone1"].Should().Be("200");

        var page = (RetrieveAttributeChangeHistoryResponse)await Admin.ExecuteAsync(new RetrieveAttributeChangeHistoryRequest
        {
            Target = new EntityReference(Account, id), AttributeLogicalName = "telephone1", PagingInfo = Page(2, 2),
        });
        page.AuditDetailCollection.AuditDetails.Select(d => Attribute(d).NewValue["telephone1"]).Should().Equal("100");
        page.AuditDetailCollection.MoreRecords.Should().BeFalse();

        // A page far past the end (PageNumber × Count beyond the int range) is empty, not the first page again.
        var beyond = (RetrieveAttributeChangeHistoryResponse)await Admin.ExecuteAsync(new RetrieveAttributeChangeHistoryRequest
        {
            Target = new EntityReference(Account, id), AttributeLogicalName = "telephone1", PagingInfo = Page(int.MaxValue, 5000),
        });
        beyond.AuditDetailCollection.AuditDetails.Should().BeEmpty();
        beyond.AuditDetailCollection.MoreRecords.Should().BeFalse();

        (await FaultAsync(() => Admin.ExecuteAsync(new RetrieveAttributeChangeHistoryRequest { Target = new EntityReference(Account, id) })))
            .HttpStatus.Should().Be(400, "AttributeLogicalName is required");
    }

    [Fact]
    public async Task History_survives_deleted_record_column_and_table()
    {
        var table = $"new_ah{_run}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = table, SchemaName = table, DisplayName = Label("Table " + table), DisplayCollectionName = Label("Tables " + table),
                OwnershipType = OwnershipTypes.UserOwned, IsAuditEnabled = new BooleanManagedProperty(true),
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });
        await Admin.ExecuteAsync(new CreateAttributeRequest
        {
            EntityName = table,
            Attribute = new StringAttributeMetadata { LogicalName = "new_extra", SchemaName = "new_Extra", MaxLength = 100, DisplayName = Label("Extra") },
        });
        await Admin.ExecuteAsync(new CreateOneToManyRequest
        {
            OneToManyRelationship = new OneToManyRelationshipMetadata { SchemaName = $"new_{table}_account", ReferencedEntity = Account, ReferencingEntity = table },
            Lookup = new LookupAttributeMetadata { LogicalName = "new_accountid", SchemaName = "new_AccountId", DisplayName = Label("Account") },
        });
        var parent = await NewAccountAsync("Parent " + _run);
        var id = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "r", ["new_extra"] = "e1", ["new_accountid"] = new EntityReference(Account, parent) });
        await UpdateAsync(new Entity(table, id) { ["new_name"] = "r2", ["new_extra"] = "e2" });
        await Admin.UpdateAsync(new Entity(Account, parent) { ["name"] = "Renamed " + _run });

        // The record, then the column, are deleted: the history answers, the column under DeletedAttributes, the lookup with its stored name.
        await Admin.DeleteAsync(table, id);
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_extra" });

        var history = (await HistoryAsync(id, entity: table)).AuditDetails.Select(Attribute).ToList();
        history.Select(d => d.AuditRecord.GetAttributeValue<OptionSetValue>("action").Value)
            .Should().Equal(AuditActions.Delete, AuditActions.Update, AuditActions.Create);
        var update = history[1];
        update.DeletedAttributes.Values.Should().BeEquivalentTo(["new_extra"]);
        update.NewValue.Attributes.Keys.Should().BeEquivalentTo(["new_name"]);
        update.NewValue["new_name"].Should().Be("r2");
        var lookup = history[2].NewValue["new_accountid"].Should().BeOfType<EntityReference>().Subject;
        (lookup.Id, lookup.Name).Should().Be((parent, "Parent " + _run), "the name stored at write time");
        history[2].NewValue.FormattedValues["new_accountid"].Should().Be("Parent " + _run);
        history[0].OldValue["new_name"].Should().Be("r2");

        // The table is deleted: the entities are named after the stored table and carry no values.
        await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });
        var orphan = (await HistoryAsync(id, entity: table)).AuditDetails.Select(Attribute).ToList();
        orphan.Should().HaveCount(3);
        orphan.Should().OnlyContain(d => d.OldValue.LogicalName == table && d.OldValue.Attributes.Count == 0 && d.NewValue.Attributes.Count == 0);
        orphan[1].DeletedAttributes.Values.Should().Contain(["new_name", "new_extra"]);
    }

    [Fact]
    public async Task Audit_messages_demand_their_privileges()
    {
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ("prvReadAccount", DepthMask.Global));
        var service = ServiceFor(fx, oid);
        var id = await NewAccountAsync("Priv " + _run);
        var auditId = (await AuditIdsAsync(id))[0];
        var target = new EntityReference(Account, id);

        OrganizationRequest[] requests =
        [
            new RetrieveAuditDetailsRequest { AuditId = auditId },
            new RetrieveRecordChangeHistoryRequest { Target = target },
            new RetrieveAttributeChangeHistoryRequest { Target = target, AttributeLogicalName = "name" },
            new DeleteRecordChangeHistoryRequest { Target = new EntityReference(Account, Guid.NewGuid()) },
            new DeleteAuditDataRequest { EndDate = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        ];
        async Task<int> StatusAsync(OrganizationRequest request)
        {
            try
            {
                await service.ExecuteAsync(request);
                return 200;
            }
            catch (XrmdFaultException fault)
            {
                return fault.HttpStatus;
            }
        }
        async Task<int[]> StatusesAsync()
        {
            var statuses = new List<int>();
            foreach (var request in requests) statuses.Add(await StatusAsync(request));
            return [.. statuses];
        }

        (await StatusesAsync()).Should().Equal(403, 403, 403, 403, 403);
        var denied = await FaultAsync(() => service.ExecuteAsync(requests[0]));
        (denied.ErrorCode, denied.Message.Contains(PrivilegeNames.ReadAuditSummary)).Should().Be((ErrorCodes.PrivilegeDenied, true));

        await GrantAsync(fx, user, team: false, (PrivilegeNames.ReadAuditSummary, DepthMask.Global));
        (await StatusesAsync()).Should().Equal(200, 403, 403, 403, 403);
        (await FaultAsync(() => service.ExecuteAsync(requests[1]))).Message.Should().Contain(PrivilegeNames.ReadRecordAuditHistory);

        await GrantAsync(fx, user, team: false, (PrivilegeNames.ReadRecordAuditHistory, DepthMask.Global));
        (await StatusesAsync()).Should().Equal(200, 200, 200, 403, 403);

        await GrantAsync(fx, user, team: false, (PrivilegeNames.DeleteRecordChangeHistory, DepthMask.Global));
        (await StatusesAsync()).Should().Equal(200, 200, 200, 200, 403);

        await GrantAsync(fx, user, team: false, (PrivilegeNames.DeleteAuditPartitions, DepthMask.Global));
        (await StatusesAsync()).Should().Equal(200, 200, 200, 200, 200);

        // The history needs both read privileges: prvReadRecordAuditHistory alone is not enough.
        var other = Guid.NewGuid();
        var historyOnly = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), other);
        await GrantAsync(fx, historyOnly, team: false, (PrivilegeNames.ReadRecordAuditHistory, DepthMask.Global));
        (await FaultAsync(() => ServiceFor(fx, other).ExecuteAsync(requests[1]))).Message.Should().Contain(PrivilegeNames.ReadAuditSummary);
    }

    // ---- deleting ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_record_change_history_returns_the_count()
    {
        var id = await NewAccountAsync("Del " + _run);
        await UpdateAsync(new Entity(Account, id) { ["name"] = "Del2 " + _run });
        var keep = await NewAccountAsync("Keep " + _run);

        var response = (DeleteRecordChangeHistoryResponse)await Admin.ExecuteAsync(
            new DeleteRecordChangeHistoryRequest { Target = new EntityReference(Account, id) });

        response.DeletedEntriesCount.Should().Be(2);
        (await AuditIdsAsync(id)).Should().BeEmpty();
        (await AuditIdsAsync(keep)).Should().HaveCount(1, "another record keeps its history");
        ((DeleteRecordChangeHistoryResponse)await Admin.ExecuteAsync(new DeleteRecordChangeHistoryRequest { Target = new EntityReference(Account, id) }))
            .DeletedEntriesCount.Should().Be(0);
        (await FaultAsync(() => Admin.ExecuteAsync(new DeleteRecordChangeHistoryRequest()))).HttpStatus.Should().Be(400);
    }

    [Fact]
    public async Task Delete_audit_data_deletes_closed_quarters_only()
    {
        var current = QuarterStart(DateTime.UtcNow);
        // Rows of earlier runs in closed quarters are cleared first, so the counts below are this test's.
        await Admin.ExecuteAsync(new DeleteAuditDataRequest { EndDate = current.AddTicks(-1) });

        var q1 = await InsertRowAsync(current.AddMonths(-9).AddDays(3), -1);
        var q2 = await InsertRowAsync(current.AddMonths(-6).AddDays(10), -1);
        var q2b = await InsertRowAsync(current.AddMonths(-3).AddMinutes(-1), -1); // last minute of the quarter before the previous one
        var q3 = await InsertRowAsync(current.AddMonths(-3).AddDays(1), -1);
        var now = await InsertRowAsync(current.AddMinutes(1), -1);

        // EndDate on the last day of the quarter before the previous one: that quarter and the one before.
        var endOfQ2 = current.AddMonths(-3).AddDays(-1).AddHours(12);
        ((DeleteAuditDataResponse)await Admin.ExecuteAsync(new DeleteAuditDataRequest { EndDate = endOfQ2 })).PartitionsDeleted.Should().Be(2);
        (await RowExistsAsync(q1)).Should().BeFalse();
        (await RowExistsAsync(q2)).Should().BeFalse();
        (await RowExistsAsync(q2b)).Should().BeFalse();
        (await RowExistsAsync(q3)).Should().BeTrue();
        (await RowExistsAsync(now)).Should().BeTrue();

        // EndDate in the future: the closed quarters only; the current quarter is kept.
        ((DeleteAuditDataResponse)await Admin.ExecuteAsync(new DeleteAuditDataRequest { EndDate = DateTime.UtcNow.AddYears(1) }))
            .PartitionsDeleted.Should().Be(1);
        (await RowExistsAsync(q3)).Should().BeFalse();
        (await RowExistsAsync(now)).Should().BeTrue();

        await ScalarAsync("DELETE FROM data.audit WHERE auditid = @id", ("@id", now));
    }

    [Fact]
    public async Task Retention_purges_expired_rows_and_keeps_forever_rows()
    {
        var now = DateTime.UtcNow;
        var expired = await InsertRowAsync(now.AddDays(-2), 86_400);
        var forever = await InsertRowAsync(now.AddYears(-5), -1);
        var fresh = await InsertRowAsync(now.AddHours(-1), 86_400);
        var longLived = await InsertRowAsync(now.AddYears(-1), int.MaxValue);

        int purged;
        await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
        {
            purged = await AuditRetention.PurgeAsync(new DataSession(conn, null), now, default);
        }

        purged.Should().BeGreaterThanOrEqualTo(1);
        (await RowExistsAsync(expired)).Should().BeFalse();
        (await RowExistsAsync(forever)).Should().BeTrue("-1 keeps a row forever");
        (await RowExistsAsync(fresh)).Should().BeTrue();
        (await RowExistsAsync(longLived)).Should().BeTrue();

        // The host runs the same purge every hour.
        var service = fx.App.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Xrmd.Host.AuditRetentionService>()
            .Should().ContainSingle().Subject;
        var later = await InsertRowAsync(now.AddHours(-3), 3_600);
        (await service.PurgeOnceAsync(default)).Should().BeGreaterThanOrEqualTo(1);
        (await RowExistsAsync(later)).Should().BeFalse();
        (await RowExistsAsync(forever)).Should().BeTrue();
        foreach (var id in new[] { forever, fresh, longLived }) await ScalarAsync("DELETE FROM data.audit WHERE auditid = @id", ("@id", id));
    }

    // ---- plug-ins ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Plugin_can_call_record_change_history()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = TestEntities.Pipe, IsAuditEnabled = new BooleanManagedProperty(true) } });
        try
        {
            var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
            var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.AuditHistoryPlugin");
            await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", TestEntities.Pipe, 20);

            var id = await Admin.CreateAsync(new Entity(TestEntities.Pipe) { ["pb_name"] = "audited " + _run });
            await UpdateAsync(new Entity(TestEntities.Pipe, id) { ["pb_name"] = "changed " + _run });

            (await ScalarAsync($"SELECT pb_data FROM data.{TestEntities.Pipe} WHERE {TestEntities.Pipe}id = @id", ("@id", id)))
                .Should().Be($"total=1|first=AttributeAuditDetail|name=audited {_run}");
        }
        finally
        {
            await PluginTestHelper.ClearAsync(fx);
            await Admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = TestEntities.Pipe, IsAuditEnabled = new BooleanManagedProperty(false) } });
        }
    }

    // ---- Web API ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_retrieve_record_change_history_with_aliases()
    {
        var parent = await NewAccountAsync("Parent " + _run);
        var id = await NewAccountAsync("Old " + _run);
        await UpdateAsync(new Entity(Account, id) { ["name"] = "New " + _run, ["parentaccountid"] = new EntityReference(Account, parent) });

        var json = await JsonAsync(await SendAsync(HttpMethod.Get,
            "RetrieveRecordChangeHistory(Target=@target,PagingInfo=@paginginfo)?@target=" + Uri.EscapeDataString($"{{ '@odata.id':'accounts({id})'}}")
            + "&@paginginfo=" + Uri.EscapeDataString("""{ "PageNumber": 1, "Count": 1, "ReturnTotalRecordCount": true }""")));

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.RetrieveRecordChangeHistoryResponse");
        var collection = json["AuditDetailCollection"]!;
        collection["MoreRecords"]!.GetValue<bool>().Should().BeTrue();
        collection["TotalRecordCount"]!.GetValue<int>().Should().Be(2);
        collection["PagingCookie"]!.GetValue<string>().Should().Be("<cookie page=\"1\" />");
        var detail = collection["AuditDetails"]![0]!;
        detail["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.AttributeAuditDetail");
        detail["OldValue"]!["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.account");
        detail["OldValue"]!["name"]!.GetValue<string>().Should().Be("Old " + _run);
        detail["OldValue"]!.AsObject().ContainsKey("_parentaccountid_value").Should().BeFalse("the old value was empty");
        detail["NewValue"]!["_parentaccountid_value"]!.GetValue<string>().Should().Be(parent.ToString());
        detail["NewValue"]!["_parentaccountid_value@Microsoft.Dynamics.CRM.lookuplogicalname"]!.GetValue<string>().Should().Be(Account);
        detail.AsObject().ContainsKey("AuditRecord").Should().BeFalse();

        var attribute = await JsonAsync(await SendAsync(HttpMethod.Get,
            $"RetrieveAttributeChangeHistory(Target=@t,AttributeLogicalName=@a)?@t={Uri.EscapeDataString($"{{'@odata.id':'accounts({id})'}}")}&@a='name'"));
        attribute["AuditDetailCollection"]!["AuditDetails"]!.AsArray().Should().HaveCount(2);
        attribute["AuditDetailCollection"]!["TotalRecordCount"]!.GetValue<int>().Should().Be(-1);
    }

    [Fact]
    public async Task Bound_retrieve_audit_details()
    {
        var id = await NewAccountAsync("Bound " + _run);
        var auditId = (await AuditIdsAsync(id))[0];

        var json = await JsonAsync(await SendAsync(HttpMethod.Get, $"audits({auditId})/Microsoft.Dynamics.CRM.RetrieveAuditDetails"));

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.RetrieveAuditDetailsResponse");
        var detail = json["AuditDetail"]!;
        detail["@odata.type"]!.GetValue<string>().Should().Be("#Microsoft.Dynamics.CRM.AttributeAuditDetail");
        detail["NewValue"]!["name"]!.GetValue<string>().Should().Be("Bound " + _run);
        detail["DeletedAttributes"]!["Count"]!.GetValue<int>().Should().Be(0);

        (await SendAsync(HttpMethod.Get, $"audits({Guid.NewGuid()})/Microsoft.Dynamics.CRM.RetrieveAuditDetails")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"accounts({id})/Microsoft.Dynamics.CRM.RetrieveAuditDetails")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the function is bound to audit");
    }

    [Fact]
    public async Task Web_api_audit_function_without_privilege_is_403()
    {
        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ("prvReadAccount", DepthMask.Global));
        var id = await NewAccountAsync("Web403 " + _run);

        using var req = new HttpRequestMessage(HttpMethod.Get, Api + "RetrieveRecordChangeHistory(Target=@t)?@t="
            + Uri.EscapeDataString($"{{'@odata.id':'accounts({id})'}}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(oid, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);

        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain(PrivilegeNames.ReadRecordAuditHistory);
    }

    [Fact]
    public async Task Post_delete_record_change_history()
    {
        var id = await NewAccountAsync("Post " + _run);
        await UpdateAsync(new Entity(Account, id) { ["name"] = "Post2 " + _run });

        var json = await JsonAsync(await SendAsync(HttpMethod.Post, "DeleteRecordChangeHistory",
            $$"""{ "Target": { "@odata.type": "Microsoft.Dynamics.CRM.account", "accountid": "{{id}}" } }"""));

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.DeleteRecordChangeHistoryResponse");
        json["DeletedEntriesCount"]!.GetValue<int>().Should().Be(2);
        (await AuditIdsAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Post_delete_audit_data()
    {
        var current = QuarterStart(DateTime.UtcNow);
        await Admin.ExecuteAsync(new DeleteAuditDataRequest { EndDate = current.AddTicks(-1) });
        var old = await InsertRowAsync(current.AddMonths(-6), -1);

        var json = await JsonAsync(await SendAsync(HttpMethod.Post, "DeleteAuditData",
            $$"""{ "EndDate": "{{current.AddDays(-1):yyyy-MM-dd'T'HH:mm:ss'Z'}}" }"""));

        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#Microsoft.Dynamics.CRM.DeleteAuditDataResponse");
        json["PartitionsDeleted"]!.GetValue<int>().Should().Be(1);
        (await RowExistsAsync(old)).Should().BeFalse();

        (await SendAsync(HttpMethod.Post, "DeleteAuditData", """{ "EndDate": "tomorrow" }""")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

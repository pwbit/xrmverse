using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.Solution;
using Xrmd.TestSupport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// #343.5: <c>overriddencreatedon</c> ("Record Created On") on every table with <c>createdon</c>. A Create that gives it a date T
/// stores T as <c>createdon</c> and the time of the Create as <c>overriddencreatedon</c> (Learn: "Import auditing data"); it needs
/// <c>prvOverrideCreatedOnCreatedBy</c>, which System Administrator and System Customizer hold; an Update or an Upsert of an
/// existing row ignores it (fix round 1).
/// </summary>
[Collection("xrmd")]
public sealed class OverriddenCreatedOnTests(XrmdFixture fx)
{
    private const string Api = "/api/data/v9.2/";
    private static readonly DateTime Original = new(2015, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    private async Task<Entity> CreatedAsync(OrganizationService service, Entity entity)
    {
        var id = await service.CreateAsync(entity);
        return await Admin.RetrieveAsync(entity.LogicalName, id, new ColumnSet("createdon", "overriddencreatedon"));
    }

    private static void ShouldKeepOriginal(Entity row, DateTime before, DateTime after)
    {
        row.GetAttributeValue<DateTime>("createdon").Should().Be(Original);
        row.GetAttributeValue<DateTime?>("overriddencreatedon").Should().NotBeNull()
            .And.BeOnOrAfter(before.AddSeconds(-1)).And.BeOnOrBefore(after.AddSeconds(1));
    }

    [Fact]
    public async Task Account_create_keeps_the_given_date_as_createdon()
    {
        var before = DateTime.UtcNow;
        var row = await CreatedAsync(Admin, new Entity("account") { ["name"] = "Imported " + _run, ["overriddencreatedon"] = Original });
        ShouldKeepOriginal(row, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Create_without_the_column_sets_no_record_created_on()
    {
        var before = DateTime.UtcNow;
        var row = await CreatedAsync(Admin, new Entity("account") { ["name"] = "Plain " + _run });
        row.GetAttributeValue<DateTime>("createdon").Should().BeOnOrAfter(before.AddSeconds(-1));
        row.Contains("overriddencreatedon").Should().BeFalse();
    }

    [Fact]
    public async Task Custom_table_has_the_column_and_keeps_the_given_date()
    {
        var table = $"new_occ{_run}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = TestHelpers.Entity(table, table, "Table " + table),
            PrimaryAttribute = new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = TestHelpers.Label("Name") },
        });
        try
        {
            var column = (DateTimeAttributeMetadata)fx.App.Services.GetRequiredService<IMetadataStore>().GetEntity(table)!
                .Attributes.Single(a => a.LogicalName == "overriddencreatedon");
            (column.SchemaName, column.Format, column.DateTimeBehavior?.Value, column.IsValidForCreate, column.IsValidForUpdate, column.IsValidForRead != false)
                .Should().Be(("OverriddenCreatedOn", DateTimeFormat.DateOnly, DateTimeBehavior.UserLocal.Value, true, false, true));
            column.DisplayName.UserLocalizedLabel.Label.Should().Be("Record Created On");

            var before = DateTime.UtcNow;
            var row = await CreatedAsync(Admin, new Entity(table) { ["new_name"] = "Imported", ["overriddencreatedon"] = Original });
            ShouldKeepOriginal(row, before, DateTime.UtcNow);
        }
        finally
        {
            await Admin.ExecuteAsync(new DeleteEntityRequest { LogicalName = table });
        }
    }

    [Fact]
    public async Task Web_api_create_keeps_the_given_date_as_createdon()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Api + "accounts")
        {
            Content = new StringContent(new JsonObject { ["name"] = "Web " + _run, ["overriddencreatedon"] = "2015-03-04T05:06:07Z" }.ToJsonString(),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        var res = await fx.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());
        var id = Guid.Parse(res.Headers.GetValues("OData-EntityId").Single().Split('(', ')')[1]);

        (await Admin.RetrieveAsync("account", id, new ColumnSet("createdon"))).GetAttributeValue<DateTime>("createdon").Should().Be(Original);
    }

    [Fact]
    public async Task Create_without_the_privilege_is_403_naming_it()
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, team: false, ("prvCreateAccount", DepthMask.Basic), ("prvReadAccount", DepthMask.Basic));
        var service = ServiceFor(fx, objectId);

        var fault = await TestHelpers.FaultAsync(() => service.CreateAsync(
            new Entity("account") { ["name"] = "Denied " + _run, ["overriddencreatedon"] = Original }));
        (fault.HttpStatus, fault.ErrorCode).Should().Be((403, ErrorCodes.PrivilegeDenied));
        fault.Message.Should().Contain(PrivilegeNames.OverrideCreatedOnCreatedBy);
        (await Admin.RetrieveMultipleAsync(new QueryExpression("account")
        {
            Criteria = { Conditions = { new ConditionExpression("name", ConditionOperator.Equal, "Denied " + _run) } },
        })).Entities.Should().BeEmpty();

        // Without the column the same user creates; with the privilege the date is kept.
        await service.CreateAsync(new Entity("account") { ["name"] = "Allowed " + _run });
        await GrantAsync(fx, user, team: false, (PrivilegeNames.OverrideCreatedOnCreatedBy, DepthMask.Global));
        var before = DateTime.UtcNow;
        ShouldKeepOriginal(await CreatedAsync(service, new Entity("account") { ["name"] = "Granted " + _run, ["overriddencreatedon"] = Original }),
            before, DateTime.UtcNow);
    }

    /// <summary>Fix round 1 (ruling): an Update ignores the column, as Dataverse ignores attributes not valid for update.</summary>
    [Fact]
    public async Task Update_ignores_the_column()
    {
        var id = await Admin.CreateAsync(new Entity("account") { ["name"] = "Updated " + _run, ["overriddencreatedon"] = Original });
        var stored = await Admin.RetrieveAsync("account", id, new ColumnSet("createdon", "overriddencreatedon"));

        await Admin.UpdateAsync(new Entity("account", id) { ["name"] = "Changed " + _run, ["overriddencreatedon"] = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc) });

        var row = await Admin.RetrieveAsync("account", id, new ColumnSet("name", "createdon", "overriddencreatedon"));
        row.GetAttributeValue<string>("name").Should().Be("Changed " + _run);
        row.GetAttributeValue<DateTime>("createdon").Should().Be(Original);
        row.GetAttributeValue<DateTime>("overriddencreatedon").Should().Be(stored.GetAttributeValue<DateTime>("overriddencreatedon"));
    }

    [Fact]
    public async Task Web_api_patch_ignores_the_column()
    {
        var id = await Admin.CreateAsync(new Entity("account") { ["name"] = "Patched " + _run });
        var req = new HttpRequestMessage(HttpMethod.Patch, Api + $"accounts({id})")
        {
            Content = new StringContent(new JsonObject { ["name"] = "Patched again " + _run, ["overriddencreatedon"] = "2001-01-01T00:00:00Z" }.ToJsonString(),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        req.Headers.Add("If-Match", "*");
        var res = await fx.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await res.Content.ReadAsStringAsync());

        var row = await Admin.RetrieveAsync("account", id, new ColumnSet("name", "overriddencreatedon"));
        row.GetAttributeValue<string>("name").Should().Be("Patched again " + _run);
        row.Contains("overriddencreatedon").Should().BeFalse();
    }

    /// <summary>An Upsert that reaches an existing row is its Update: the column is ignored, and no privilege is needed for it.</summary>
    [Fact]
    public async Task Upsert_on_an_existing_row_ignores_the_column()
    {
        var objectId = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), objectId);
        await GrantAsync(fx, user, team: false, ("prvCreateAccount", DepthMask.Basic), ("prvReadAccount", DepthMask.Basic), ("prvWriteAccount", DepthMask.Basic));
        var service = ServiceFor(fx, objectId);
        var id = await service.CreateAsync(new Entity("account") { ["name"] = "Upserted " + _run });
        var createdOn = (await Admin.RetrieveAsync("account", id, new ColumnSet("createdon"))).GetAttributeValue<DateTime>("createdon");

        var response = (UpsertResponse)await service.ExecuteAsync(new UpsertRequest
        {
            Target = new Entity("account", id) { ["name"] = "Upserted again " + _run, ["overriddencreatedon"] = Original },
        });

        response.RecordCreated.Should().BeFalse();
        var row = await Admin.RetrieveAsync("account", id, new ColumnSet("name", "createdon", "overriddencreatedon"));
        row.GetAttributeValue<string>("name").Should().Be("Upserted again " + _run);
        row.GetAttributeValue<DateTime>("createdon").Should().Be(createdOn);
        row.Contains("overriddencreatedon").Should().BeFalse();
    }

    [Fact]
    public async Task System_customizer_holds_the_privilege()
    {
        var privilege = PrivilegeNames.IdOf(PrivilegeNames.OverrideCreatedOnCreatedBy);
        (await TestSql.ScalarAsync(fx.ConnectionString, "SELECT name FROM data.privilege WHERE privilegeid = @id", ("@id", privilege)))
            .Should().Be(PrivilegeNames.OverrideCreatedOnCreatedBy);
        (await TestSql.CountAsync(fx.ConnectionString, "SELECT COUNT(*) FROM data.roleprivileges WHERE roleid = @role AND privilegeid = @id",
            ("@role", StandardRoles.SystemCustomizer), ("@id", privilege))).Should().Be(1);
    }

    /// <summary>An organization from before #343.5: System Customizer gets the privilege once, by the top-up.</summary>
    [Fact]
    public async Task Customizer_top_up_reaches_an_existing_organization_once()
    {
        const string step = "override-createdon-role-privilege";
        var grant = $"roleid = '{StandardRoles.SystemCustomizer}' AND privilegeid = '{PrivilegeNames.IdOf(PrivilegeNames.OverrideCreatedOnCreatedBy)}'";
        var bootstrap = fx.App.Services.GetRequiredService<SecurityBootstrap>();
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.roleprivileges WHERE {grant}");
        await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM meta.upgrade_step WHERE name = N'{step}'");
        try
        {
            await bootstrap.EnsureAsync(default);
            (await TestSql.CountAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM data.roleprivileges WHERE {grant} AND privilegedepthmask = 8")).Should().Be(1);
            (await TestSql.CountAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM meta.upgrade_step WHERE name = N'{step}'")).Should().Be(1);

            await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM data.roleprivileges WHERE {grant}");
            await bootstrap.EnsureAsync(default);
            (await TestSql.CountAsync(fx.ConnectionString, $"SELECT COUNT(*) FROM data.roleprivileges WHERE {grant}")).Should().Be(0, "a removed grant stays removed");
        }
        finally
        {
            await TestSql.ExecAsync(fx.ConnectionString, $"DELETE FROM meta.upgrade_step WHERE name = N'{step}'");
            await bootstrap.EnsureAsync(default);
        }
    }
}

/// <summary>
/// #343.5: the tables of an organization made before <c>overriddencreatedon</c> (simulated by dropping the column and its attribute
/// row) get it at the next start (<see cref="OverriddenCreatedOnUpgrade"/>), with their rows kept; a second start changes nothing.
/// </summary>
[Collection("xrmd")]
public sealed class OverriddenCreatedOnUpgradeTests(XrmdFixture fx) : IAsyncLifetime
{
    private static readonly SeedOptions Options = new(XrmdFixture.OrgName, Guid.NewGuid(), Guid.NewGuid(), "Ada");
    private IDbConnectionFactory _factory = null!;
    private string? _database;

    public async Task InitializeAsync()
    {
        var database = _database = "xrmd_occ_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        _factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(_factory).MigrateAsync();
        await new SystemSeed(await NewStoreAsync(), _factory).EnsureAsync(Options);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null) await fx.DropDatabaseAsync(_database);
    }

    private async Task<MetadataStore> NewStoreAsync()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();
        return store;
    }

    private string ConnectionString => TestProvider.WithDatabase(fx.ConnectionString, _database!);

    [Fact]
    public async Task Upgrade_adds_the_column_to_existing_tables_once()
    {
        var store = await NewStoreAsync();
        await store.CreateEntityAsync(new EntityMetadata { LogicalName = "xv_old", SchemaName = "xv_Old", OwnershipType = OwnershipTypes.UserOwned },
            new StringAttributeMetadata("xv_Name") { LogicalName = "xv_name", MaxLength = 100 });
        store.GetEntity("xv_old")!.Attributes.Should().Contain(a => a.LogicalName == "overriddencreatedon", "a new table has the column");
        var row = Guid.NewGuid();
        await TestSql.ExecAsync(ConnectionString, $"""
            INSERT INTO data.xv_old (xv_oldid, xv_name, statecode, statuscode, createdon, modifiedon)
            VALUES ('{row}', N'Kept', 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME())
            """);
        foreach (var table in new[] { "xv_old", "account" })
            await TestSql.ExecAsync(ConnectionString, $"""
                {TestSql.Dialect.DropColumns($"data.{table}", ["overriddencreatedon"])}
                DELETE FROM meta.attribute WHERE entity_logical_name = N'{table}' AND logical_name = N'overriddencreatedon';
                """);
        var old = await NewStoreAsync();
        old.GetEntity("xv_old")!.Attributes.Should().NotContain(a => a.LogicalName == "overriddencreatedon", "the simulated table predates the column");

        await OverriddenCreatedOnUpgrade.EnsureAsync(old);

        foreach (var upgraded in new[] { old, await NewStoreAsync() })
            foreach (var table in new[] { "xv_old", "account", "contact" })
                upgraded.GetEntity(table)!.Attributes.Should().ContainSingle(a => a.LogicalName == "overriddencreatedon")
                    .Which.Should().BeOfType<DateTimeAttributeMetadata>().Which.IsValidForCreate.Should().BeTrue();
        (await TestSql.ScalarAsync(ConnectionString, $"SELECT xv_name FROM data.xv_old WHERE xv_oldid = '{row}'")).Should().Be("Kept");
        (await TestSql.CountAsync(ConnectionString, "SELECT COUNT(*) FROM data.xv_old WHERE overriddencreatedon IS NULL")).Should().Be(1);
        foreach (var table in new[] { "xv_old", "account" })
            (await TestSql.ColumnExistsAsync(ConnectionString, "data", table, "overriddencreatedon")).Should().BeTrue();

        // A second start is a no-op.
        var restarted = await NewStoreAsync();
        var stamp = restarted.VersionStamp;
        await OverriddenCreatedOnUpgrade.EnsureAsync(restarted);
        restarted.VersionStamp.Should().Be(stamp);
    }
}

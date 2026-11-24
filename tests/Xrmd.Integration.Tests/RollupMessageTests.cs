using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using Xrmd.TestSupport;
using Xrmd.Workflows;
using S = Xrmd.Metadata.WorkflowSchema;

namespace Xrmd.Integration.Tests;

/// <summary>
/// <c>CalculateRollupField</c>, the writes and the security of rollup columns (plan 2.12b, Task 5; rulings R10, R11, R12): the SDK
/// request and the Web API function calculate one column of one row as the system and answer it masked for the caller; Write on the
/// row is required (<c>0x8004E027</c>); values sent for a rollup or its companions are dropped (no plug-in Target, no audit); currency
/// stamping leaves a rollup's <c>_base</c> to the calculation; a workflow wait condition cannot read a rollup (<c>0x80060557</c>); the
/// Web API defines a rollup column with <c>SourceType</c> 2 and <c>FormulaDefinition</c>. Each test has a host and an organization
/// database of its own (the rollups on <c>account</c> and their jobs would otherwise reach the other tests), with the table
/// <c>xrmv_calcorder</c> and the rollups of the parity export (tests/fixtures/rollup/real).
/// </summary>
[Collection("xrmd")]
public sealed class RollupMessageTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Account = "account", Order = "xrmv_calcorder";

    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;
    private string _connectionString = "";

    private IServiceProvider Services => _app.Services;
    private MetadataStore Store => (MetadataStore)Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => TestServices.NewOrganizationService(Services, fx.AdminObjectId);
    private SecurityCache Cache => Services.GetRequiredService<SecurityCache>();

    public async Task InitializeAsync()
    {
        _app = fx.WithOwnDatabase(_ => { });
        _client = _app.CreateClient();
        _connectionString = Services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;
        await Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await Store.AddAttributeAsync(Account, Named(new MoneyAttributeMetadata { PrecisionSource = 2 }, "xrmv_budget"));
        await Store.CreateEntityAsync(new EntityMetadata
        {
            LogicalName = Order, SchemaName = Order, OwnershipType = OwnershipTypes.UserOwned, DisplayName = SystemAttributes.CreateLabel(Order, 1033),
        }, new StringAttributeMetadata { LogicalName = "xrmv_name", SchemaName = "xrmv_name", MaxLength = 100 });
        await Store.AddAttributeAsync(Order, Named(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_amount"));
        await Store.AddAttributeAsync(Order, Named(new DateTimeAttributeMetadata(DateTimeFormat.DateAndTime) { DateTimeBehavior = DateTimeBehavior.UserLocal }, "xrmv_startedon"));
        await Store.AddAttributeAsync(Order, Named(new PicklistAttributeMetadata { OptionSet = Options(1, 2, 3) }, "xrmv_status"));
        await TestEntities.InSessionAsync(_connectionString, s => Store.CreateOneToManyAsync(s, new OneToManyRelationshipMetadata
        {
            SchemaName = "xrmv_calcorder_AccountId_account", ReferencedEntity = Account, ReferencingEntity = Order, ReferencedAttribute = "accountid",
        }, Named(new LookupAttributeMetadata(), "xrmv_accountid"), CancellationToken.None));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    // ---- definitions ------------------------------------------------------------------------------------------------------

    private static string Real(string column) =>
        File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "rollup", "real", $"account-{column}.xaml"));

    private static T Named<T>(T a, string name) where T : AttributeMetadata
    {
        a.LogicalName = name;
        a.SchemaName = name;
        a.DisplayName = SystemAttributes.CreateLabel(name, 1033);
        return a;
    }

    private static T Rollup<T>(T a, string name) where T : AttributeMetadata
    {
        Named(a, name);
        a.SourceType = RollupColumns.Rollup;
        ComputedColumnRules.SetFormula(a, Real(name));
        return a;
    }

    private static OptionSetMetadata Options(params int[] values) =>
        new(new OptionMetadataCollection(values.Select(v => new OptionMetadata(SystemAttributes.CreateLabel("Option " + v, 1033), v)).ToList()))
        {
            IsGlobal = false, OptionSetType = OptionSetType.Picklist,
        };

    private static AttributeMetadata OrderTotal(bool secured = false, bool audited = false)
    {
        var a = Rollup(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_ordertotal");
        if (secured) a.IsSecured = true;
        if (audited) a.IsAuditEnabled = new BooleanManagedProperty(true);
        return a;
    }

    private static AttributeMetadata OrderCount() => Rollup(new IntegerAttributeMetadata(), "xrmv_ordercount");
    private static AttributeMetadata TreeBudget() => Rollup(new MoneyAttributeMetadata { PrecisionSource = 2 }, "xrmv_treebudget");

    private async Task AddAsync(params AttributeMetadata[] rollups)
    {
        foreach (var a in rollups) await Store.AddAttributeAsync(Account, a);
        await Store.RollupJobNotifications;
    }

    // ---- rows and calls ---------------------------------------------------------------------------------------------------

    private Task<Guid> AccountAsync(string name, OrganizationService? service = null) =>
        (service ?? Admin).CreateAsync(new Entity(Account) { ["name"] = name });

    private Task<Guid> OrderAsync(Guid account, decimal amount, int status) => Admin.CreateAsync(new Entity(Order)
    {
        ["xrmv_name"] = "order", ["xrmv_accountid"] = new EntityReference(Account, account), ["xrmv_amount"] = amount,
        ["xrmv_status"] = new OptionSetValue(status),
    });

    private static async Task<Entity> CalculateAsync(OrganizationService service, Guid account, string column)
    {
        var response = await service.ExecuteAsync(new CalculateRollupFieldRequest
        {
            Target = new EntityReference(Account, account), FieldName = column,
        });
        response.Should().BeOfType<CalculateRollupFieldResponse>();
        return (Entity)response.Results["Entity"];
    }

    private async Task<Dictionary<string, object?>> RowAsync(Guid account, params string[] columns)
    {
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT {string.Join(", ", columns.Select(c => $"[{c}]"))} FROM [data].[account] WHERE accountid = @id");
        cmd.Parameters.AddWithValue("@id", account);
        await using var rd = await cmd.ExecuteReaderAsync();
        (await rd.ReadAsync()).Should().BeTrue();
        return columns.Select((c, i) => (c, rd.IsDBNull(i) ? null : rd.GetValue(i))).ToDictionary(x => x.c, x => x.Item2);
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static async Task<XrmdFaultException> FaultAsync(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (XrmdFaultException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("The call was expected to fail.");
    }

    private readonly Dictionary<Guid, Guid> _userIds = [];

    /// <summary>A user of the root business unit with <paramref name="privileges"/>; returns its object id.</summary>
    private async Task<Guid> UserAsync(params (string Privilege, DepthMask Depth)[] privileges)
    {
        var oid = Guid.NewGuid();
        var user = await SecurityTestHelper.CreateUserAsync(_connectionString, Cache, await SecurityTestHelper.RootBusinessUnitAsync(_connectionString), oid);
        var role = await SecurityTestHelper.CreateRoleAsync(_connectionString, Cache, "role-" + oid.ToString("N")[..8], privileges);
        await SecurityTestHelper.AssignRoleAsync(_connectionString, Cache, user, role);
        _userIds[oid] = user;
        return oid;
    }

    /// <summary>The systemuser id of the user <see cref="UserAsync"/> made with the object id <paramref name="oid"/>.</summary>
    private Guid UserId(Guid oid) => _userIds[oid];

    /// <summary>Gives the user <paramref name="oid"/> a new field security profile that reads <paramref name="columns"/> of account.</summary>
    private async Task GrantFieldReadAsync(Guid oid, params string[] columns)
    {
        var profile = await Admin.CreateAsync(new Entity(FieldSecurityTables.Profile) { ["name"] = "rollup-" + Guid.NewGuid().ToString("N")[..8] });
        foreach (var column in columns)
            await Admin.CreateAsync(new Entity(FieldSecurityTables.Permission)
            {
                [FieldSecurityTables.ProfileId] = new EntityReference(FieldSecurityTables.Profile, profile),
                [FieldSecurityTables.EntityName] = Account, [FieldSecurityTables.AttributeLogicalName] = column,
                [FieldSecurityTables.CanRead] = new OptionSetValue(4),
            });
        await Admin.ExecuteAsync(new AssociateRequest
        {
            Target = new EntityReference("systemuser", UserId(oid)), Relationship = new Relationship("systemuserprofiles_association"),
            RelatedEntities = [new EntityReference(FieldSecurityTables.Profile, profile)],
        });
    }

    /// <summary>Registers <c>ContextRecorderPlugin</c> on each (message, table, stage); <see cref="RecordedAsync"/> reads what it wrote.</summary>
    private async Task RecordStepsAsync(params (string Message, string? Table, int Stage)[] steps)
    {
        await TestEntities.EnsurePipeAsync(Store);
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        foreach (var (message, table, stage) in steps)
            await PluginTestHelper.RegisterStepAsync(Admin, type, message, table, stage, config: TestEntities.PipeLog);
        Services.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().Invalidate();
    }

    /// <summary>The records <c>ContextRecorderPlugin</c> wrote whose name starts with <paramref name="prefix"/> (message|stage|).</summary>
    private async Task<List<string>> RecordedAsync(string prefix)
    {
        var found = new List<string>();
        await using var conn = await TestProvider.OpenAsync(_connectionString);
        await using var cmd = TestSql.Command(conn, $"SELECT pb_data FROM data.{TestEntities.PipeLog} WHERE pb_name LIKE @p ORDER BY createdon");
        cmd.Parameters.AddWithValue("@p", prefix + "%");
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) found.Add(rd.GetString(0));
        return found;
    }

    private static string Line(string data, string name) =>
        data.Split('\n').Single(l => l.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];

    private OrganizationService ServiceFor(Guid oid) => TestServices.NewOrganizationService(Services, oid);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, _client.BaseAddress!.ToString()));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _client.SendAsync(req);
    }

    // ---- CalculateRollupField (R10) ---------------------------------------------------------------------------------------

    [Fact]
    public async Task CalculateRollupField_returns_value_date_and_state()
    {
        await AddAsync(OrderTotal(), OrderCount());
        var a = await AccountAsync("A");
        await OrderAsync(a, 100m, 2);
        await OrderAsync(a, 25.5m, 2);
        await OrderAsync(a, 7m, 1);
        var before = await RowAsync(a, "modifiedon", "modifiedby");
        var start = DateTime.UtcNow;

        var result = await CalculateAsync(Admin, a, "xrmv_ordertotal");

        (result.LogicalName, result.Id).Should().Be((Account, a));
        result["xrmv_ordertotal"].Should().Be(125.5m, "the orders of status 2 (Won) are summed");
        result["xrmv_ordertotal_state"].Should().Be(1, "Calculated");
        result.GetAttributeValue<DateTime>("xrmv_ordertotal_date").Should().BeCloseTo(start, TimeSpan.FromMinutes(1));
        result.Contains("xrmv_ordercount").Should().BeFalse("only the column asked for is calculated and returned");

        var row = await RowAsync(a, "xrmv_ordertotal", "xrmv_ordertotal_state", "xrmv_ordertotal_date", "xrmv_ordercount",
            "xrmv_ordercount_state", "modifiedon", "modifiedby");
        row["xrmv_ordertotal"].Should().Be(125.5m, "the value is stored");
        row["xrmv_ordertotal_state"].Should().Be(1);
        ((DateTime)row["xrmv_ordertotal_date"]!).Should().BeCloseTo(start, TimeSpan.FromMinutes(1));
        (row["xrmv_ordercount"], row["xrmv_ordercount_state"]).Should().Be((0, 1), "another rollup of the row keeps the value of the Create (Learn: no cascade)");
        (row["modifiedon"], row["modifiedby"]).Should().Be((before["modifiedon"], before["modifiedby"]), "the calculation is the system's, not an update");

        // A step can be registered on the message: it is in the catalog.
        SdkMessageCatalog.IsCatalogMessage("CalculateRollupField").Should().BeTrue();
    }

    [Fact]
    public async Task CalculateRollupField_on_simple_column_is_0x80060554()
    {
        await AddAsync(OrderTotal());
        var a = await AccountAsync("A");

        var fault = await FaultAsync(() => CalculateAsync(Admin, a, "name"));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.RecalculateNotSupportedOnNonRollupField, 400));
        fault.Message.Should().Contain("'account.name'");

        // A companion is not a rollup either.
        fault = await FaultAsync(() => CalculateAsync(Admin, a, "xrmv_ordertotal_state"));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.RecalculateNotSupportedOnNonRollupField, 400));
    }

    [Fact]
    public async Task CalculateRollupField_without_write_access_is_0x8004E027()
    {
        await AddAsync(OrderTotal());
        var a = await AccountAsync("A");
        await OrderAsync(a, 40m, 2);

        var reader = await UserAsync(("prvReadAccount", DepthMask.Global));
        var fault = await FaultAsync(() => CalculateAsync(ServiceFor(reader), a, "xrmv_ordertotal"));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.RollupFieldNoWriteAccess, 403));
        (await RowAsync(a, "xrmv_ordertotal_state"))["xrmv_ordertotal_state"].Should().Be(1, "nothing was calculated: the state of the Create stays");

        // Write on the account's row is what counts (Basic depth does not cover a row the admin owns).
        var basicWriter = await UserAsync(("prvReadAccount", DepthMask.Global), ("prvWriteAccount", DepthMask.Basic));
        fault = await FaultAsync(() => CalculateAsync(ServiceFor(basicWriter), a, "xrmv_ordertotal"));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.RollupFieldNoWriteAccess, 403));

        // Write given by a share of the row (GrantAccess) is Write on the row.
        await Admin.ExecuteAsync(new GrantAccessRequest
        {
            Target = new EntityReference(Account, a),
            PrincipalAccess = new PrincipalAccess { Principal = new EntityReference("systemuser", UserId(basicWriter)), AccessMask = AccessRights.WriteAccess },
        });
        (await CalculateAsync(ServiceFor(basicWriter), a, "xrmv_ordertotal"))["xrmv_ordertotal"].Should().Be(40m, "the share gives Write on the row");

        // With Write on the row, and no privilege at all on the orders (aggregated as the system, R12).
        var writer = await UserAsync(("prvReadAccount", DepthMask.Global), ("prvWriteAccount", DepthMask.Global));
        (await CalculateAsync(ServiceFor(writer), a, "xrmv_ordertotal"))["xrmv_ordertotal"].Should().Be(40m);

        // A row that does not exist is 404, as for any message naming one.
        fault = await FaultAsync(() => CalculateAsync(ServiceFor(writer), Guid.NewGuid(), "xrmv_ordertotal"));
        fault.HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task CalculateRollupField_masks_secured_rollup()
    {
        await AddAsync(OrderTotal(secured: true));
        var a = await AccountAsync("A");
        await OrderAsync(a, 60m, 2);

        var writer = await UserAsync(("prvReadAccount", DepthMask.Global), ("prvWriteAccount", DepthMask.Global));
        var result = await CalculateAsync(ServiceFor(writer), a, "xrmv_ordertotal");
        result["xrmv_ordertotal"].Should().BeNull("the caller cannot read the secured rollup");
        result["xrmv_ordertotal_state"].Should().BeNull("a companion follows its secured column");
        result["xrmv_ordertotal_date"].Should().BeNull();
        (await RowAsync(a, "xrmv_ordertotal"))["xrmv_ordertotal"].Should().Be(60m, "the value is calculated and stored all the same");

        var admin = await CalculateAsync(Admin, a, "xrmv_ordertotal");
        admin["xrmv_ordertotal"].Should().Be(60m);
        admin["xrmv_ordertotal_state"].Should().Be(1);
    }

    [Fact]
    public async Task Rollup_ignores_source_row_security_and_field_security()
    {
        // R12: the aggregate reads the sources as the system: neither the orders' row security nor the secured amount hides a row.
        await Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = Order, Attribute = new DecimalAttributeMetadata { LogicalName = "xrmv_amount", IsSecured = true }, MergeLabels = true,
        });
        await AddAsync(OrderTotal());
        var a = await AccountAsync("A");
        await OrderAsync(a, 30m, 2);
        await OrderAsync(a, 12m, 2);

        var writer = await UserAsync(("prvReadAccount", DepthMask.Global), ("prvWriteAccount", DepthMask.Global));
        var service = ServiceFor(writer);
        var orders = new QueryExpression(Order) { ColumnSet = new ColumnSet("xrmv_amount") };
        (await FaultAsync(() => service.RetrieveMultipleAsync(orders))).HttpStatus.Should().Be(403, "the caller cannot read the orders");

        (await CalculateAsync(service, a, "xrmv_ordertotal"))["xrmv_ordertotal"].Should().Be(42m);

        // Row level: Basic Read on the orders, which the admin owns, finds none of them; the sum still covers all of them.
        var basicReader = await UserAsync(("prvReadAccount", DepthMask.Global), ("prvWriteAccount", DepthMask.Global),
            ("prvRead" + Order, DepthMask.Basic));
        var basicService = ServiceFor(basicReader);
        (await basicService.RetrieveMultipleAsync(orders)).Entities.Should().BeEmpty("the orders are the admin's and Basic Read covers own rows");
        await OrderAsync(a, 8m, 2);
        (await CalculateAsync(basicService, a, "xrmv_ordertotal"))["xrmv_ordertotal"].Should().Be(50m, "every order is summed, as the system");
        (await service.RetrieveAsync(Account, a, new ColumnSet("xrmv_ordertotal")))["xrmv_ordertotal"].Should().Be(50m,
            "the rollup itself is not secured: the caller reads it");
    }

    // ---- writes (R11) -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_and_update_ignore_rollup_and_companion_values()
    {
        await AddAsync(OrderTotal(audited: true), TreeBudget());
        var admin = Admin;
        await TestEntities.EnsurePipeAsync(Store);
        await admin.UpdateAsync(new Entity("organization", (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!)
        {
            ["isauditenabled"] = true,
        });
        await admin.ExecuteAsync(new UpdateEntityRequest { Entity = new EntityMetadata { LogicalName = Account, IsAuditEnabled = new BooleanManagedProperty(true) } });
        await admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = Account,
            Attribute = new StringAttributeMetadata { LogicalName = "telephone1", IsAuditEnabled = new BooleanManagedProperty(true) },
            MergeLabels = true,
        });
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        await PluginTestHelper.RegisterStepAsync(admin, type, "Create", Account, 20, config: TestEntities.PipeLog);
        await PluginTestHelper.RegisterStepAsync(admin, type, "Update", Account, 20, config: TestEntities.PipeLog);
        Services.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().Invalidate();

        var given = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var a = await admin.CreateAsync(new Entity(Account)
        {
            ["name"] = "A", ["telephone1"] = "555-0100", ["xrmv_ordertotal"] = 999m, ["xrmv_ordertotal_state"] = 5, ["xrmv_ordertotal_date"] = given,
            ["xrmv_treebudget"] = new Money(5m), ["xrmv_treebudget_base"] = new Money(5m), ["xrmv_treebudget_state"] = 2,
        });
        await admin.UpdateAsync(new Entity(Account, a)
        {
            ["telephone1"] = "555-0101", ["xrmv_ordertotal"] = 888m, ["xrmv_ordertotal_state"] = 6, ["xrmv_ordertotal_date"] = given,
            ["xrmv_treebudget"] = new Money(7m), ["xrmv_treebudget_state"] = 3,
        });

        var row = await RowAsync(a, "xrmv_ordertotal", "xrmv_ordertotal_state", "xrmv_ordertotal_date", "xrmv_treebudget", "xrmv_treebudget_base",
            "xrmv_treebudget_state", "telephone1");
        row["telephone1"].Should().Be("555-0101");
        (row["xrmv_ordertotal"], row["xrmv_ordertotal_state"]).Should().Be((0m, 1), "the values sent are dropped: the empty-set value of the Create stays");
        row["xrmv_ordertotal_date"].Should().NotBe(given);
        (row["xrmv_treebudget"], row["xrmv_treebudget_base"], row["xrmv_treebudget_state"]).Should().Be((null, null, 0));

        // Never in a plug-in's Target.
        var targets = new List<string>();
        await using (var conn = await TestProvider.OpenAsync(_connectionString))
        {
            await using var cmd = TestSql.Command(conn, $"SELECT pb_name, pb_data FROM data.{TestEntities.PipeLog} WHERE pb_name LIKE N'Create|20|%' OR pb_name LIKE N'Update|20|%'");
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
                targets.Add(rd.GetString(1).Split('\n').Single(l => l.StartsWith("target=", StringComparison.Ordinal))["target=".Length..]);
        }
        targets.Should().HaveCount(2, "the Create and the Update steps ran");
        targets.Should().OnlyContain(t => t.Contains("telephone1"));
        targets.Should().OnlyContain(t => !t.Contains("xrmv_ordertotal") && !t.Contains("xrmv_treebudget"), "rollup values never reach a plug-in (R11)");

        // Nor the audit: neither the values sent nor the initial values the Create stored, nor a CalculateRollupField (ruling candidate).
        await OrderAsync(a, 10m, 2);
        await CalculateAsync(admin, a, "xrmv_ordertotal");
        var changes = new List<string>();
        await using (var conn = await TestProvider.OpenAsync(_connectionString))
        {
            await using var cmd = TestSql.Command(conn, "SELECT changedata FROM data.audit WHERE objectid = @id ORDER BY createdon");
            cmd.Parameters.AddWithValue("@id", a);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) changes.Add(rd.IsDBNull(0) ? "" : rd.GetString(0));
        }
        changes.Should().HaveCount(2, "the Create and the Update are audited, the calculation is not");
        changes.Should().OnlyContain(c => c.Contains("telephone1"));
        changes.Should().OnlyContain(c => !c.Contains("xrmv_ordertotal") && !c.Contains("xrmv_treebudget"), "rollup columns are not audited");
    }

    [Fact]
    public async Task Currency_change_of_target_does_not_restamp_rollup_base()
    {
        await AddAsync(TreeBudget());
        var a = await Admin.CreateAsync(new Entity(Account) { ["name"] = "A", ["xrmv_budget"] = new Money(100m) });
        var calculated = await CalculateAsync(Admin, a, "xrmv_treebudget");
        calculated.GetAttributeValue<Money>("xrmv_treebudget")!.Value.Should().Be(100m);
        calculated.GetAttributeValue<Money>("xrmv_treebudget_base")!.Value.Should().Be(100m, "the base currency's rate is 1");

        var other = await Admin.CreateAsync(new Entity(CurrencySchema.Table)
        {
            ["currencyname"] = "Currency XRB", [CurrencySchema.IsoCode] = "XRB", [CurrencySchema.Symbol] = "X$", [CurrencySchema.Precision] = 2,
            [CurrencySchema.CurrencyType] = new OptionSetValue(1), [CurrencySchema.ExchangeRate] = 2m,
        });
        await Admin.UpdateAsync(new Entity(Account, a) { [CurrencySchema.CurrencyId] = new EntityReference(CurrencySchema.Table, other) });

        var row = await RowAsync(a, "xrmv_budget_base", "xrmv_treebudget", "xrmv_treebudget_base", "xrmv_treebudget_state", "exchangerate");
        row["exchangerate"].Should().Be(2m);
        row["xrmv_budget_base"].Should().Be(50m, "an ordinary money column is restamped");
        (row["xrmv_treebudget"], row["xrmv_treebudget_base"], row["xrmv_treebudget_state"])
            .Should().Be((100m, 100m, 1), "the rollup's base is the calculation's: stamping leaves it until the next calculation");

        var again = await CalculateAsync(Admin, a, "xrmv_treebudget");
        again.GetAttributeValue<Money>("xrmv_treebudget_base")!.Value.Should().Be(50m, "the budget's base is now 50");
        again.GetAttributeValue<Money>("xrmv_treebudget")!.Value.Should().Be(100m, "converted at the row's rate");
    }

    [Fact]
    public async Task CalculateRollupField_masks_secured_currency_rollup_and_honours_profile()
    {
        var budget = TreeBudget();
        budget.IsSecured = true;
        await AddAsync(budget);
        var a = await Admin.CreateAsync(new Entity(Account) { ["name"] = "A", ["xrmv_budget"] = new Money(70m) });

        var writer = await UserAsync(("prvReadAccount", DepthMask.Global), ("prvWriteAccount", DepthMask.Global));
        var hidden = await CalculateAsync(ServiceFor(writer), a, "xrmv_treebudget");
        foreach (var column in new[] { "xrmv_treebudget", "xrmv_treebudget_base", "xrmv_treebudget_state", "xrmv_treebudget_date" })
        {
            hidden.Contains(column).Should().BeTrue($"{column} is part of the answer");
            hidden[column].Should().BeNull($"{column} follows the secured rollup");
        }
        (await RowAsync(a, "xrmv_treebudget_base"))["xrmv_treebudget_base"].Should().Be(70m, "calculated and stored all the same");

        // A profile that reads the rollup shows it, its base and its companions.
        await GrantFieldReadAsync(writer, "xrmv_treebudget");
        var shown = await CalculateAsync(ServiceFor(writer), a, "xrmv_treebudget");
        shown.GetAttributeValue<Money>("xrmv_treebudget")!.Value.Should().Be(70m);
        shown.GetAttributeValue<Money>("xrmv_treebudget_base")!.Value.Should().Be(70m);
        shown["xrmv_treebudget_state"].Should().Be(1);
    }

    [Fact]
    public async Task CalculateRollupField_target_by_key_on_standard_table_is_400()
    {
        await AddAsync(OrderTotal());
        var a = await AccountAsync("A");

        var target = new EntityReference(Account) { KeyAttributes = { ["accountid"] = a } };
        var fault = await FaultAsync(() => Admin.ExecuteAsync(new CalculateRollupFieldRequest { Target = target, FieldName = "xrmv_ordertotal" }));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.InvalidArgument, 400), "a key on a standard table is refused, not a missing row");
        fault.Message.Should().Contain("accountid");

        // The same with the id given too: refused before the security check reads either.
        var both = new EntityReference(Account, a) { KeyAttributes = { ["accountid"] = a } };
        (await FaultAsync(() => Admin.ExecuteAsync(new CalculateRollupFieldRequest { Target = both, FieldName = "xrmv_ordertotal" })))
            .HttpStatus.Should().Be(400);
        (await RowAsync(a, "xrmv_ordertotal_state"))["xrmv_ordertotal_state"].Should().Be(1, "nothing was calculated: the state of the Create stays");
    }

    [Fact]
    public async Task CalculateRollupField_runs_steps_and_rolls_back_with_them()
    {
        await AddAsync(OrderTotal());
        var a = await AccountAsync("A");
        await OrderAsync(a, 9m, 2);
        await RecordStepsAsync(("CalculateRollupField", null, 20), ("CalculateRollupField", null, 40));

        (await CalculateAsync(Admin, a, "xrmv_ordertotal"))["xrmv_ordertotal"].Should().Be(9m);
        var pre = await RecordedAsync("CalculateRollupField|20|");
        var post = await RecordedAsync("CalculateRollupField|40|");
        (pre.Count, post.Count).Should().Be((1, 1), "a step registered on the message fires");
        Line(post[0], "primary").Should().Be(Account, "the primary entity is the row's table");
        Line(post[0], "output").Should().Contain("Entity");

        // A failing post-operation step rolls the calculation back.
        await OrderAsync(a, 1m, 2);
        var assembly = await PluginTestHelper.EnsureAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var thrower = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ThrowPlugin");
        await PluginTestHelper.RegisterStepAsync(Admin, thrower, "CalculateRollupField", null, 40, rank: 2, config: "invalid:refused by the step");
        Services.GetRequiredService<Xrmd.Pipeline.Registration.StepRegistry>().Invalidate();
        var fault = await FaultAsync(() => CalculateAsync(Admin, a, "xrmv_ordertotal"));
        fault.Message.Should().Contain("refused by the step");
        (await RowAsync(a, "xrmv_ordertotal"))["xrmv_ordertotal"].Should().Be(9m, "the new value (10) was rolled back");
    }

    [Fact]
    public async Task Upsert_ignores_rollup_and_companion_values()
    {
        await AddAsync(OrderTotal(), TreeBudget());
        await RecordStepsAsync(("Create", Account, 20), ("Update", Account, 20));
        Entity Sent(Guid id, string name) => new(Account, id)
        {
            ["name"] = name, ["xrmv_ordertotal"] = 999m, ["xrmv_ordertotal_state"] = 5, ["xrmv_treebudget"] = new Money(5m),
            ["xrmv_treebudget_base"] = new Money(5m),
        };

        var a = Guid.NewGuid();
        await Admin.ExecuteAsync(new UpsertRequest { Target = Sent(a, "created") });
        await Admin.ExecuteAsync(new UpsertRequest { Target = Sent(a, "updated") });
        var b = Guid.NewGuid();
        await Admin.ExecuteAsync(new UpsertMultipleRequest { Targets = new EntityCollection([Sent(b, "bulk")]) { EntityName = Account } });

        var targets = (await RecordedAsync("Create|20|")).Concat(await RecordedAsync("Update|20|")).Select(d => Line(d, "target")).ToList();
        targets.Should().HaveCount(3, "the inner Create, Update and the bulk's Create ran their steps");
        targets.Should().OnlyContain(t => t.Contains("name"));
        targets.Should().OnlyContain(t => !t.Contains("xrmv_ordertotal") && !t.Contains("xrmv_treebudget"), "rollup values never reach a plug-in (R11)");
        foreach (var id in new[] { a, b })
        {
            var row = await RowAsync(id, "xrmv_ordertotal", "xrmv_ordertotal_state", "xrmv_treebudget", "xrmv_treebudget_base");
            (row["xrmv_ordertotal"], row["xrmv_ordertotal_state"], row["xrmv_treebudget"], row["xrmv_treebudget_base"])
                .Should().Be((0m, 1, null, null), "the values sent are not stored");
        }
    }

    // ---- workflows (R11) --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("xrmv_ordertotal")]
    [InlineData("xrmv_ordertotal_state")]
    [InlineData("xrmv_ordertotal_date")]
    public async Task Wait_condition_on_rollup_is_refused(string column)
    {
        await AddAsync(OrderTotal());
        var row = WorkflowFixtures.Definition("background-wait", "Wait on " + column);
        row = row with { Xaml = row.Xaml!.Replace("Attribute=\"statuscode\"", $"Attribute=\"{column}\"") };
        row.Xaml.Should().Contain($"Attribute=\"{column}\"");
        await using (var scope = Services.CreateAsyncScope())
        await using (var conn = await TestProvider.OpenAsync(_connectionString))
        {
            await scope.ServiceProvider.GetRequiredService<WorkflowStore>().SaveAsync(new DataSession(conn, null), row, [], default);
        }

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(S.Table, row.Id), State = new OptionSetValue(S.ActivatedState), Status = new OptionSetValue(S.ActivatedStatus),
        }));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.RollupOrCalcNotAllowedInWorkflowWaitCondition, 400));
        fault.Message.Should().Contain($"'account.{column}'");
        (await ScalarAsync("SELECT statecode FROM data.workflow WHERE workflowid = @id", ("@id", row.Id))).Should().Be(0, "it stays Draft");

        // The same process waiting on a simple column activates.
        var simple = WorkflowFixtures.Definition("background-wait", "Wait on status " + column);
        await using (var scope = Services.CreateAsyncScope())
        await using (var conn = await TestProvider.OpenAsync(_connectionString))
        {
            await scope.ServiceProvider.GetRequiredService<WorkflowStore>().SaveAsync(new DataSession(conn, null), simple, [], default);
        }
        await Admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(S.Table, simple.Id), State = new OptionSetValue(S.ActivatedState), Status = new OptionSetValue(S.ActivatedStatus),
        });
    }

    [Theory]
    [InlineData("xrmv_ordertotal")]
    [InlineData("xrmv_ordertotal_date")]
    public async Task Update_trigger_on_rollup_is_refused(string column)
    {
        await AddAsync(OrderTotal());
        var row = WorkflowFixtures.Definition("background-wait", "Trigger on " + column) with { TriggerOnUpdateAttributes = "name," + column };
        await using (var scope = Services.CreateAsyncScope())
        await using (var conn = await TestProvider.OpenAsync(_connectionString))
        {
            await scope.ServiceProvider.GetRequiredService<WorkflowStore>().SaveAsync(new DataSession(conn, null), row, [], default);
        }

        var fault = await FaultAsync(() => Admin.ExecuteAsync(new SetStateRequest
        {
            EntityMoniker = new EntityReference(S.Table, row.Id), State = new OptionSetValue(S.ActivatedState), Status = new OptionSetValue(S.ActivatedStatus),
        }));
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.RollupOrCalcNotAllowedInWorkflowWaitCondition, 400));
        fault.Message.Should().Contain($"'account.{column}'");
        (await ScalarAsync("SELECT statecode FROM data.workflow WHERE workflowid = @id", ("@id", row.Id))).Should().Be(0, "it stays Draft");
    }

    // ---- duplicate detection (final review I1) ------------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_rule_on_rollup_is_refused()
    {
        await AddAsync(OrderTotal());
        var admin = Admin;
        await admin.ExecuteAsync(new UpdateEntityRequest
        {
            Entity = new EntityMetadata { LogicalName = Account, IsDuplicateDetectionEnabled = new BooleanManagedProperty(true) },
        });
        var rule = await admin.CreateAsync(new Entity(DuplicateDetectionSchema.Rule)
        {
            ["name"] = "Rollup rule", ["baseentityname"] = Account, ["matchingentityname"] = Account,
        });

        Task<Guid> Condition(string column) => admin.CreateAsync(new Entity(DuplicateDetectionSchema.Condition)
        {
            ["regardingobjectid"] = new EntityReference(DuplicateDetectionSchema.Rule, rule),
            ["baseattributename"] = column, ["matchingattributename"] = column, ["operatorcode"] = new OptionSetValue(0),
        });

        var rollup = async () => await Condition("xrmv_ordertotal");
        var fault = (await rollup.Should().ThrowAsync<XrmdFaultException>()).Which;
        (fault.ErrorCode, fault.HttpStatus).Should().Be((ErrorCodes.InvalidArgument, 400));
        fault.Message.Should().Be("Duplicate detection rules cannot use the rollup column 'account.xrmv_ordertotal'.");
        // Its companions are refused too, as columns of another column.
        var state = async () => await Condition("xrmv_ordertotal_state");
        (await state.Should().ThrowAsync<XrmdFaultException>()).Which.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        // A simple column of the same table is accepted.
        await Condition("name");
    }

    // ---- Web API (R10, R15) -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Web_api_calculaterollupfield_function()
    {
        await AddAsync(OrderTotal(), OrderCount());
        var a = await AccountAsync("A");
        await OrderAsync(a, 15m, 2);
        await OrderAsync(a, 5m, 2);

        var get = await SendAsync(HttpMethod.Get,
            $"CalculateRollupField(Target=@t,FieldName=@f)?@t={{'@odata.id':'accounts({a})'}}&@f='xrmv_ordertotal'");
        var text = await get.Content.ReadAsStringAsync();
        get.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var json = JsonNode.Parse(text)!;
        json["@odata.context"]!.GetValue<string>().Should().EndWith("$metadata#accounts/$entity");
        json["accountid"]!.GetValue<Guid>().Should().Be(a);
        json["xrmv_ordertotal"]!.GetValue<decimal>().Should().Be(20m);
        json["xrmv_ordertotal_state"]!.GetValue<int>().Should().Be(1);
        DateTimeOffset.Parse(json["xrmv_ordertotal_date"]!.GetValue<string>(), CultureInfo.InvariantCulture).UtcDateTime
            .Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        json.AsObject().ContainsKey("xrmv_ordercount").Should().BeFalse();

        // Inline values, and the refusal of a simple column with its code.
        var inline = await SendAsync(HttpMethod.Get,
            $"CalculateRollupField(Target=@t,FieldName='xrmv_ordercount')?@t={{\"@odata.id\":\"accounts({a})\"}}");
        var inlineText = await inline.Content.ReadAsStringAsync();
        inline.StatusCode.Should().Be(HttpStatusCode.OK, inlineText);
        JsonNode.Parse(inlineText)!["xrmv_ordercount"]!.GetValue<int>().Should().Be(2);

        var simple = await SendAsync(HttpMethod.Get, $"CalculateRollupField(Target=@t,FieldName='name')?@t={{'@odata.id':'accounts({a})'}}");
        var simpleText = await simple.Content.ReadAsStringAsync();
        simple.StatusCode.Should().Be(HttpStatusCode.BadRequest, simpleText);
        simpleText.Should().Contain("0x80060554");
    }

    [Fact]
    public async Task Web_api_creates_rollup_attribute()
    {
        var formula = Real("xrmv_ordertotal");
        var body = new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.DecimalAttributeMetadata",
            ["LogicalName"] = "new_webtotal", ["SchemaName"] = "new_WebTotal", ["Precision"] = 2,
            ["DisplayName"] = JsonNode.Parse("""{ "LocalizedLabels": [ { "Label": "Web total", "LanguageCode": 1033 } ] }"""),
            ["RequiredLevel"] = JsonNode.Parse("""{ "Value": "None" }"""),
            ["SourceType"] = 2, ["FormulaDefinition"] = formula,
        };
        var post = await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{Account}')/Attributes", body.ToJsonString());
        post.StatusCode.Should().Be(HttpStatusCode.NoContent, await post.Content.ReadAsStringAsync());

        var get = await SendAsync(HttpMethod.Get,
            $"EntityDefinitions(LogicalName='{Account}')/Attributes(LogicalName='new_webtotal')/Microsoft.Dynamics.CRM.DecimalAttributeMetadata");
        var text = await get.Content.ReadAsStringAsync();
        get.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var read = JsonNode.Parse(text)!;
        read["SourceType"]!.GetValue<int>().Should().Be(2);
        read["FormulaDefinition"]!.GetValue<string>().Should().Be(formula);
        read["SourceTypeMask"]!.GetValue<int>().Should().Be(0, "a rollup's mask is 0 (R13)");
        read["IsValidForCreate"]!.GetValue<bool>().Should().BeFalse();
        read["IsValidForUpdate"]!.GetValue<bool>().Should().BeFalse();

        // It calculates like the exported one.
        var a = await AccountAsync("A");
        await OrderAsync(a, 3m, 2);
        (await CalculateAsync(Admin, a, "new_webtotal"))["new_webtotal"].Should().Be(3m);
    }
}

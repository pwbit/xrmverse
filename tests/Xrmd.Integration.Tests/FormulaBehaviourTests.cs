using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
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
using Xrmd.Integration.Tests.Tds;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// A table <c>new_fb{label}{run}</c> for the formula column behaviour tests (plan 2.12c, Task 4), made through the metadata messages
/// so that the formulas go through the server's Power Fx translator: <c>new_name</c>, <c>new_code</c> (text), <c>new_qty</c> (whole
/// number), <c>new_price</c> (currency, 2), and the formula columns <c>new_fxlabel</c> = <c>Upper(new_code) &amp; "-" &amp;
/// Text(new_qty, "0")</c> (the parity export's) and <c>new_fxtotal</c> = <c>Decimal(new_price) * new_qty</c> (Decimal, 2).
/// </summary>
internal static class FormulaOrders
{
    public const string Label = "Upper(new_code) & \"-\" & Text(new_qty, \"0\")";
    public const string Total = "Decimal(new_price) * new_qty";

    public static Label Text(string text) => new(text, 1033);

    public static T Formula<T>(T attribute, string formula) where T : AttributeMetadata
    {
        attribute.SourceType = ComputedColumnRules.Formula;
        attribute.SchemaName ??= attribute.LogicalName;
        attribute.DisplayName ??= Text(attribute.LogicalName);
        ComputedColumnRules.SetFormula(attribute, formula);
        return attribute;
    }

    public static async Task<string> TableAsync(OrganizationService admin, string name, bool audit = false, bool securedPrice = false)
    {
        await admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = Text("Table " + name), DisplayCollectionName = Text("Tables " + name),
                OwnershipType = OwnershipTypes.UserOwned, IsAuditEnabled = audit ? new BooleanManagedProperty(true) : null,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Text("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });
        await AddAsync(admin, name, new StringAttributeMetadata { LogicalName = "new_code", SchemaName = "new_Code", DisplayName = Text("Code"), MaxLength = 100 });
        await AddAsync(admin, name, new IntegerAttributeMetadata { LogicalName = "new_qty", SchemaName = "new_Qty", DisplayName = Text("Quantity") });
        await AddAsync(admin, name, new MoneyAttributeMetadata
        {
            LogicalName = "new_price", SchemaName = "new_Price", DisplayName = Text("Price"), PrecisionSource = 2, IsSecured = securedPrice,
        });
        await AddAsync(admin, name, Formula(new StringAttributeMetadata { LogicalName = "new_fxlabel", MaxLength = 200 }, Label));
        await AddAsync(admin, name, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxtotal", Precision = 2 }, Total));
        return name;
    }

    public static Task AddAsync(OrganizationService admin, string table, AttributeMetadata attribute) =>
        admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = attribute });

    /// <summary>
    /// The rows of the query test (code, price, quantity): totals 30, 100, 15, 0 (a blank price is 0 in the product, F5) and 0.
    /// </summary>
    public static readonly (string Code, decimal? Price, int Qty)[] Rows =
        [("A1", 10m, 3), ("B2", 25m, 4), ("C3", 7.5m, 2), ("D4", null, 5), ("E5", 50m, 0)];

    /// <summary>The rows with a total above 20, by total descending: what every query front end must return.</summary>
    public static readonly (string Code, decimal Total)[] AboveTwenty = [("B2", 100m), ("A1", 30m)];

    public static async Task CreateRowsAsync(OrganizationService admin, string table)
    {
        foreach (var (code, price, qty) in Rows)
        {
            var row = new Entity(table) { ["new_name"] = "Order " + code, ["new_code"] = code, ["new_qty"] = qty };
            if (price is { } p) row["new_price"] = new Money(p);
            await admin.CreateAsync(row);
        }
    }

    public static string Fetch(string table) => $"""
        <fetch><entity name="{table}"><attribute name="new_code" /><attribute name="new_fxtotal" />
          <filter><condition attribute="new_fxtotal" operator="gt" value="20" /></filter>
          <order attribute="new_fxtotal" descending="true" /></entity></fetch>
        """;

    public static IEnumerable<(string, decimal)> Pairs(EntityCollection rows) =>
        rows.Entities.Select(e => (e.GetAttributeValue<string>("new_code"), e.GetAttributeValue<decimal>("new_fxtotal")));
}

/// <summary>
/// Formula columns on the read and write sides (plan 2.12c, Task 4; ruling F9, the 2.12a rules K5, K10, K11): filtered and sorted
/// alike by FetchXML, QueryExpression and the Web API (the SQL endpoint in <see cref="FormulaTdsQueryTests"/>; Review Focus 5),
/// values sent for them ignored, the post-image of a plug-in step carrying the computed value, the sources' field security not
/// masking the result, a formula column itself not securable (final review I2), no audit entry, duplicate rules refused, and a
/// 2.12b rollup summing a formula column. Each test builds its own table (<c>new_fb*</c>); the rollup test has a database of its own.
/// </summary>
[Collection("xrmd")]
public sealed class FormulaBehaviourTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync()
    {
        await TestEntities.EnsurePipeAsync(Store);
        await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        await PluginTestHelper.ClearAsync(fx);
    }

    public Task DisposeAsync() => PluginTestHelper.ClearAsync(fx);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private Task<string> TableAsync(string label, bool audit = false, bool securedPrice = false) =>
        FormulaOrders.TableAsync(Admin, $"new_fb{label}{_run}", audit, securedPrice);

    private Task<Guid> OrderAsync(string table, string code, decimal price, int qty, OrganizationService? service = null) =>
        (service ?? Admin).CreateAsync(new Entity(table)
        {
            ["new_name"] = "Order " + _run, ["new_code"] = code, ["new_price"] = new Money(price), ["new_qty"] = qty,
        });

    private async Task<(string? Label, decimal? Total)> FormulasAsync(string table, Guid id)
    {
        var row = await Admin.RetrieveAsync(table, id, new ColumnSet("new_fxlabel", "new_fxtotal"));
        return (row.GetAttributeValue<string>("new_fxlabel"), row.Contains("new_fxtotal") ? row.GetAttributeValue<decimal>("new_fxtotal") : null);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, sql);
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<string> RefusedAsync(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (XrmdValidationException ex)
        {
            return ex.Message;
        }
        catch (XrmdFaultException ex)
        {
            ex.ErrorCode.Should().Be(ErrorCodes.InvalidArgument, ex.Message);
            ex.HttpStatus.Should().Be(400, ex.Message);
            return ex.Message;
        }
        throw new InvalidOperationException("The call was expected to be refused.");
    }

    // ---- queries (K11, Review Focus 5) ----------------------------------------------------------------------------------

    [Fact]
    public async Task Query_filters_and_sorts_on_formula()
    {
        var table = await TableAsync("q");
        await FormulaOrders.CreateRowsAsync(Admin, table);

        // FetchXML: gt and order.
        FormulaOrders.Pairs(await Admin.RetrieveMultipleAsync(new FetchExpression(FormulaOrders.Fetch(table))))
            .Should().Equal(FormulaOrders.AboveTwenty);

        // QueryExpression: the same condition and order.
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet("new_code", "new_fxtotal") };
        query.Criteria.AddCondition("new_fxtotal", ConditionOperator.GreaterThan, 20m);
        query.AddOrder("new_fxtotal", OrderType.Descending);
        FormulaOrders.Pairs(await Admin.RetrieveMultipleAsync(query)).Should().Equal(FormulaOrders.AboveTwenty);

        // A blank price is 0 in the product (F5), and the filter sees that 0: two rows, sorted by the text formula.
        var zero = new QueryExpression(table) { ColumnSet = new ColumnSet("new_code", "new_fxlabel") };
        zero.Criteria.AddCondition("new_fxtotal", ConditionOperator.Equal, 0m);
        zero.AddOrder("new_fxlabel", OrderType.Descending);
        (await Admin.RetrieveMultipleAsync(zero)).Entities.Select(e => e.GetAttributeValue<string>("new_fxlabel"))
            .Should().Equal("E5-0", "D4-5");

        // The Web API: $filter and $orderby.
        var set = Store.GetEntity(table)!.EntitySetName;
        var res = await SendAsync(HttpMethod.Get, $"{set}?$select=new_code,new_fxtotal&$filter=new_fxtotal gt 20&$orderby=new_fxtotal desc");
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonNode.Parse(body)!["value"]!.AsArray()
            .Select(v => (v!["new_code"]!.GetValue<string>(), v["new_fxtotal"]!.GetValue<decimal>()))
            .Should().Equal(FormulaOrders.AboveTwenty);

        // The Web API on the text formula: startswith and an order.
        var label = await SendAsync(HttpMethod.Get, $"{set}?$select=new_fxlabel&$filter=startswith(new_fxlabel,'B') or startswith(new_fxlabel,'C')&$orderby=new_fxlabel asc");
        var labelBody = await label.Content.ReadAsStringAsync();
        label.StatusCode.Should().Be(HttpStatusCode.OK, labelBody);
        JsonNode.Parse(labelBody)!["value"]!.AsArray().Select(v => v!["new_fxlabel"]!.GetValue<string>()).Should().Equal("B2-4", "C3-2");
    }

    // ---- writes (K5) --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_and_update_ignore_formula_values()
    {
        var table = await TableAsync("w");

        var id = await Admin.CreateAsync(new Entity(table)
        {
            ["new_name"] = "Given", ["new_code"] = "ab", ["new_price"] = new Money(10m), ["new_qty"] = 3,
            ["new_fxtotal"] = 999m, ["new_fxlabel"] = "given",
        });
        (await FormulasAsync(table, id)).Should().Be(("AB-3", 30m), "the values sent for the formula columns are ignored");

        await Admin.UpdateAsync(new Entity(table, id) { ["new_fxtotal"] = 1m, ["new_fxlabel"] = "changed" });
        (await FormulasAsync(table, id)).Should().Be(("AB-3", 30m));

        await Admin.UpdateAsync(new Entity(table, id) { ["new_qty"] = 4, ["new_fxtotal"] = 1m });
        (await FormulasAsync(table, id)).Should().Be(("AB-4", 40m), "the sources change the value, the value sent does not");

        // The Web API ignores them the same way.
        var set = Store.GetEntity(table)!.EntitySetName;
        var patch = await SendAsync(HttpMethod.Patch, $"{set}({id})", """{ "new_fxtotal": 5, "new_fxlabel": "x", "new_price": 20 }""");
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent, await patch.Content.ReadAsStringAsync());
        (await FormulasAsync(table, id)).Should().Be(("AB-4", 80m));

        // No column holds them: the row's SQL table has no formula column.
        (await TestSql.ColumnsAsync(fx.ConnectionString, "data", table)).Should().NotContain(c => c.StartsWith("new_fx", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Post_image_at_stage_40_has_formula_value()
    {
        var table = await TableAsync("i");
        var id = await OrderAsync(table, "ab", 10m, 2);
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");

        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        var step = await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", table, 40, config: TestEntities.PipeLog);
        await PluginTestHelper.RegisterImageAsync(Admin, step, 1, "post", "new_fxtotal,new_fxlabel,new_qty");

        await Admin.UpdateAsync(new Entity(table, id) { ["new_qty"] = 5, ["new_fxtotal"] = 1m });

        var data = (string?)await ScalarAsync($"SELECT TOP 1 pb_data FROM data.{TestEntities.PipeLog} WHERE pb_name LIKE N'Update|40|%' ORDER BY createdon DESC");
        data.Should().NotBeNull("the step ran");
        var lines = data!.Split('\n');
        lines.Single(l => l.StartsWith("target=", StringComparison.Ordinal))["target=".Length..].Split(',')
            .Should().NotContain("new_fxtotal", "a formula column is never in a plug-in Target (K5)");
        var post = lines.Single(l => l.StartsWith("postvalues=", StringComparison.Ordinal))["postvalues=post:".Length..].Split(',')
            .Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        post["new_qty"].Should().Be("5");
        decimal.Parse(post["new_fxtotal"], CultureInfo.InvariantCulture).Should().Be(50m, "the post-image carries the value computed after the update");
        post["new_fxlabel"].Should().Be("AB-5");
    }

    // ---- security (K10) -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Formula_ignores_source_field_security()
    {
        var table = await TableAsync("s", securedPrice: true);
        var id = await OrderAsync(table, "ab", 10m, 3);

        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ($"prvRead{table}", DepthMask.Global));
        var reader = ServiceFor(fx, oid);

        var row = await reader.RetrieveAsync(table, id, new ColumnSet("new_price", "new_fxtotal"));
        row.GetAttributeValue<Money>("new_price").Should().BeNull("the secured source is not readable");
        row.GetAttributeValue<decimal>("new_fxtotal").Should().Be(30m, "the formula reads its sources with system rights");

        // A filter on the formula column works for the user.
        var byTotal = new QueryExpression(table) { ColumnSet = new ColumnSet("new_fxtotal") };
        byTotal.Criteria.AddCondition("new_fxtotal", ConditionOperator.Equal, 30m);
        byTotal.Criteria.AddCondition(table + "id", ConditionOperator.Equal, id);
        (await reader.RetrieveMultipleAsync(byTotal)).Entities.Should().ContainSingle();
    }

    [Fact]
    public async Task Formula_column_cannot_be_secured()
    {
        // Learn (field-level-security, "Which columns can be secured?"): formula columns cannot be secured (final review I2).
        var table = await TableAsync("ns");

        var created = await FaultAsync(() => FormulaOrders.AddAsync(Admin, table, FormulaOrders.Formula(
            new DecimalAttributeMetadata { LogicalName = "new_fxsecure", Precision = 2, IsSecured = true }, FormulaOrders.Total)));
        (created.ErrorCode, created.HttpStatus).Should().Be((ErrorCodes.FieldNotSecurable, 400), created.Message);
        created.Message.Should().Contain($"{table}.new_fxsecure");

        var updated = await FaultAsync(() => Admin.ExecuteAsync(new UpdateAttributeRequest
        {
            EntityName = table, Attribute = new DecimalAttributeMetadata { LogicalName = "new_fxtotal", IsSecured = true },
        }));
        (updated.ErrorCode, updated.HttpStatus).Should().Be((ErrorCodes.FieldNotSecurable, 400), updated.Message);

        var read = (RetrieveAttributeResponse)await Admin.ExecuteAsync(new RetrieveAttributeRequest { EntityLogicalName = table, LogicalName = "new_fxtotal" });
        (read.AttributeMetadata.CanBeSecuredForRead, read.AttributeMetadata.CanBeSecuredForCreate, read.AttributeMetadata.CanBeSecuredForUpdate,
            read.AttributeMetadata.IsSecured).Should().Be((false, false, false, false));
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
        throw new InvalidOperationException("The request was not refused.");
    }

    // ---- audit (K5) ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Audit_has_no_entry_for_formula()
    {
        var table = await TableAsync("a", audit: true);
        var organization = (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;
        await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = true });
        try
        {
            var id = await OrderAsync(table, "ab", 10m, 2);
            await Admin.UpdateAsync(new Entity(table, id) { ["new_qty"] = 3, ["new_fxtotal"] = 1m, ["new_fxlabel"] = "x" });

            var changes = new List<string>();
            foreach (var row in await TestSql.RowsAsync(fx.ConnectionString,
                         "SELECT action, changedata FROM data.audit WHERE objectid = @id ORDER BY createdon", ("@id", id)))
                changes.Add(Convert.ToInt32(row[0]) + ":" + row[1]);
            changes.Should().HaveCount(2, "a create and an update are audited");
            changes.Should().OnlyContain(c => c.Contains("new_qty"));
            changes.Should().NotContain(c => c.Contains("new_fx"), "a formula column is not audited");
        }
        finally
        {
            await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = false });
        }
    }

    // ---- duplicate detection ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_rule_on_formula_is_refused()
    {
        var table = await TableAsync("d");
        var kit = new DuplicateDetectionTestKit(fx);
        await kit.InitializeAsync(false, table);
        try
        {
            (await RefusedAsync(() => kit.RuleAsync("new_fxtotal", table)))
                .Should().Be($"Duplicate detection rules cannot use the formula column '{table}.new_fxtotal'.");
            (await RefusedAsync(() => kit.RuleAsync("new_fxlabel", table)))
                .Should().Be($"Duplicate detection rules cannot use the formula column '{table}.new_fxlabel'.");
            // A simple column of the same table is accepted.
            await kit.RuleAsync("new_code", table);
        }
        finally
        {
            await kit.DisposeAsync();
        }
    }

    // ---- rollup (plan 2.12b, R5 extended by F9) -----------------------------------------------------------------------

    [Fact]
    public async Task Rollup_aggregates_simple_formula()
    {
        // A database of its own: a rollup on account and its jobs would reach the other tests (as RollupMessageTests).
        await using var app = fx.WithOwnDatabase(_ => { });
        var services = app.Services;
        var store = (MetadataStore)services.GetRequiredService<IMetadataStore>();
        var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Org")!;
        var admin = TestServices.NewOrganizationService(services, fx.AdminObjectId);
        await services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);

        const string account = "account", order = "xrmv_calcorder";
        static T Named<T>(T a, string name) where T : AttributeMetadata
        {
            a.LogicalName = name;
            a.SchemaName = name;
            a.DisplayName = SystemAttributes.CreateLabel(name, 1033);
            return a;
        }
        await store.CreateEntityAsync(new EntityMetadata
        {
            LogicalName = order, SchemaName = order, OwnershipType = OwnershipTypes.UserOwned, DisplayName = SystemAttributes.CreateLabel(order, 1033),
        }, new StringAttributeMetadata { LogicalName = "xrmv_name", SchemaName = "xrmv_name", MaxLength = 100 });
        await store.AddAttributeAsync(order, Named(new MoneyAttributeMetadata { PrecisionSource = 2 }, "xrmv_price"));
        await store.AddAttributeAsync(order, Named(new IntegerAttributeMetadata(), "xrmv_qty"));
        await store.AddAttributeAsync(order, Named(new PicklistAttributeMetadata
        {
            OptionSet = new OptionSetMetadata(new OptionMetadataCollection(new[] { 1, 2, 3 }
                .Select(v => new OptionMetadata(SystemAttributes.CreateLabel("Option " + v, 1033), v)).ToList()))
            {
                IsGlobal = false, OptionSetType = OptionSetType.Picklist,
            },
        }, "xrmv_status"));
        await TestEntities.InSessionAsync(connectionString, s => store.CreateOneToManyAsync(s, new OneToManyRelationshipMetadata
        {
            SchemaName = "xrmv_calcorder_AccountId_account", ReferencedEntity = account, ReferencingEntity = order, ReferencedAttribute = "accountid",
        }, Named(new LookupAttributeMetadata(), "xrmv_accountid"), CancellationToken.None));

        // The formula column, then the parity export's rollup xrmv_ordertotal (SUM of status-2 orders) aggregating it.
        var total = Named(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_fxtotal");
        total.SourceType = ComputedColumnRules.Formula;
        ComputedColumnRules.SetFormula(total, "Decimal(xrmv_price) * xrmv_qty");
        await store.AddAttributeAsync(order, total);

        var rollup = Named(new DecimalAttributeMetadata { Precision = 2 }, "xrmv_ordertotal");
        rollup.SourceType = RollupColumns.Rollup;
        ComputedColumnRules.SetFormula(rollup, File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "rollup", "real",
            "account-xrmv_ordertotal.xaml")).Replace("Attribute=\"xrmv_amount\"", "Attribute=\"xrmv_fxtotal\""));
        await store.AddAttributeAsync(account, rollup);
        await store.RollupJobNotifications;

        var a = await admin.CreateAsync(new Entity(account) { ["name"] = "Formula rollup " + _run });
        async Task Order(decimal? price, int qty, int status)
        {
            var row = new Entity(order)
            {
                ["xrmv_name"] = "order", ["xrmv_accountid"] = new EntityReference(account, a), ["xrmv_qty"] = qty,
                ["xrmv_status"] = new OptionSetValue(status),
            };
            if (price is { } p) row["xrmv_price"] = new Money(p);
            await admin.CreateAsync(row);
        }
        await Order(10m, 3, 2);    // 30
        await Order(2.5m, 4, 2);   // 10
        await Order(null, 7, 2);   // a blank price is 0 (F5)
        await Order(100m, 1, 1);   // another status: not summed

        var response = await admin.ExecuteAsync(new CalculateRollupFieldRequest
        {
            Target = new EntityReference(account, a), FieldName = "xrmv_ordertotal",
        });
        var calculated = (Entity)response.Results["Entity"];
        calculated.GetAttributeValue<decimal>("xrmv_ordertotal").Should().Be(40m);
        calculated.GetAttributeValue<int>("xrmv_ordertotal_state").Should().Be(1, "Calculated");
    }
}

/// <summary>Formula columns over the SQL endpoint (plan 2.12c, Task 4; Review Focus 5): the same rows as FetchXML.</summary>
[Collection("tds-query")]
public sealed class FormulaTdsQueryTests(TdsQueryFixture fx)
{
    [Fact]
    public async Task Tds_select_where_order_by_formula()
    {
        var service = TestServices.NewOrganizationService(fx.Host.App.Services, fx.AdminObjectId);
        var table = await FormulaOrders.TableAsync(service, "new_fbt" + Guid.NewGuid().ToString("N")[..8]);
        await FormulaOrders.CreateRowsAsync(service, table);

        await using var connection = new SqlConnection(fx.Host.SqlLoginConnectionString().ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT new_code, new_fxtotal, new_fxlabel FROM {table} WHERE new_fxtotal > 20 ORDER BY new_fxtotal DESC", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, decimal)>();
        var labels = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetDecimal(1)));
            labels.Add(reader.GetString(2));
        }

        rows.Should().Equal(FormulaOrders.AboveTwenty);
        labels.Should().Equal("B2-4", "A1-3");
        // The same rows as FetchXML on the same host.
        FormulaOrders.Pairs(await service.RetrieveMultipleAsync(new FetchExpression(FormulaOrders.Fetch(table)))).Should().Equal(rows);
    }
}

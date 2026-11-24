using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Globalization;
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
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Metadata.Sdk;
using Xrmd.Security;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Calculated columns on the write side (plan 2.12a, Task 4): values sent for them are ignored (K5), the post-image of a plug-in
/// step carries the computed value, the sources' field security does not mask the result while the column's own does (K10,
/// Review Focus 3), audit records nothing for them, duplicate rules and auto-numbers refuse them, and the metadata messages
/// (SDK and Web API, K3) create, read and change their definition, refusing a change that breaks a dependent calculated column.
/// Each test builds its own tables (<c>new_cw*</c>) from the fixtures of tests/fixtures/calculated.
/// </summary>
[Collection("xrmd")]
public sealed class CalculatedWriteTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";
    private const string Denied = "0x8004f504";

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

    /// <summary>A fixture's XAML for the table <paramref name="table"/>, with the columns renamed from <c>xrmv_</c> to <c>new_</c>.</summary>
    private static string Xaml(string fixture, string table) =>
        File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "calculated", fixture + ".xaml"))
            .Replace("xrmv_calcorder", table).Replace("xrmv_", "new_");

    /// <summary>chained.xaml as <paramref name="target"/> = <paramref name="source"/> / <paramref name="divisor"/>.</summary>
    /// <summary>
    /// chained.xaml (a Decimal column) for <paramref name="target"/>: <paramref name="source"/> / <paramref name="divisor"/>. For a
    /// Currency column its steps declare the column's type, Money, as the 2.12a parity export writes them.
    /// </summary>
    private static string Chain(string table, string target, string source, int divisor = 2, bool money = false)
    {
        var xaml = Xaml("chained", table).Replace("\"new_total\"", $"\"{source}\"").Replace("new_double", target)
            .Replace("WorkflowPropertyType.Decimal, \"2\"", $"WorkflowPropertyType.Decimal, \"{divisor.ToString(CultureInfo.InvariantCulture)}\"");
        return money ? xaml.Replace("Value=\"x:Decimal\"", "Value=\"mxs:Money\"") : xaml;
    }

    private static Label Label(string text) => new(text, 1033);

    private static T Calculated<T>(T attribute, string xaml) where T : AttributeMetadata
    {
        attribute.SourceType = ComputedColumnRules.Calculated;
        attribute.SchemaName ??= attribute.LogicalName;
        attribute.DisplayName ??= Label(attribute.LogicalName);
        ComputedColumnRules.SetFormula(attribute, xaml);
        return attribute;
    }

    /// <summary>
    /// A table <c>new_cw{label}{run}</c> with <c>new_name</c>, <c>new_amount</c> (money), <c>new_quantity</c> (whole number) and the
    /// calculated <c>new_total</c> = amount × quantity, created through the SDK's CreateAttribute (K3).
    /// </summary>
    private async Task<string> TableAsync(string label, bool audit = false, bool securedAmount = false)
    {
        var name = $"new_cw{label}{_run}";
        await Admin.ExecuteAsync(new CreateEntityRequest
        {
            Entity = new EntityMetadata
            {
                LogicalName = name, SchemaName = name, DisplayName = Label("Table " + name), DisplayCollectionName = Label("Tables " + name),
                OwnershipType = OwnershipTypes.UserOwned, IsAuditEnabled = audit ? new BooleanManagedProperty(true) : null,
            },
            PrimaryAttribute = new StringAttributeMetadata
            {
                LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100, DisplayName = Label("Name"),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            },
        });
        await CreateAttributeAsync(name, new MoneyAttributeMetadata
        {
            LogicalName = "new_amount", SchemaName = "new_Amount", DisplayName = Label("Amount"), PrecisionSource = 2, IsSecured = securedAmount,
        });
        await CreateAttributeAsync(name, new IntegerAttributeMetadata { LogicalName = "new_quantity", SchemaName = "new_Quantity", DisplayName = Label("Quantity") });
        await CreateAttributeAsync(name, Calculated(new MoneyAttributeMetadata { LogicalName = "new_total", PrecisionSource = 0, Precision = 2 },
            Xaml("arithmetic-money", name)));
        return name;
    }

    private Task CreateAttributeAsync(string table, AttributeMetadata attribute) =>
        Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = attribute });

    private Task UpdateAttributeAsync(string table, AttributeMetadata attribute) =>
        Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = table, Attribute = attribute });

    private async Task<AttributeMetadata> RetrieveAttributeAsync(string table, string column) =>
        ((RetrieveAttributeResponse)await Admin.ExecuteAsync(new RetrieveAttributeRequest { EntityLogicalName = table, LogicalName = column }))
        .AttributeMetadata;

    private Task<Guid> OrderAsync(string table, decimal amount, int quantity, OrganizationService? service = null) =>
        (service ?? Admin).CreateAsync(new Entity(table) { ["new_name"] = "Order " + _run, ["new_amount"] = new Money(amount), ["new_quantity"] = quantity });

    private async Task<decimal?> TotalAsync(string table, Guid id, string column = "new_total")
    {
        var row = await Admin.RetrieveAsync(table, id, new ColumnSet(column));
        return row.Contains(column) ? row[column] switch { Money m => m.Value, decimal d => d, _ => null } : null;
    }

    /// <summary>The refusal of <paramref name="act"/>: a 400 InvalidArgument (0x80040203), as a fault or a validation error.</summary>
    private static async Task<string> RefusedAsync(Func<Task> act)
    {
        try
        {
            await act();
        }
        catch (XrmdValidationException ex)
        {
            // In process the store's refusal arrives as the validation error itself; every front end answers it with
            // 400 0x80040203 (ODataErrorMiddleware, the SOAP fault), which the Web API tests assert on the wire.
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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null, Guid? objectId = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(objectId ?? fx.AdminObjectId, fx.BaseUrl));
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

    // ---- writes (K5) --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_and_update_ignore_calculated_values()
    {
        var table = await TableAsync("w");

        var id = await Admin.CreateAsync(new Entity(table)
        {
            ["new_name"] = "Given", ["new_amount"] = new Money(10m), ["new_quantity"] = 3, ["new_total"] = new Money(999m),
        });
        (await TotalAsync(table, id)).Should().Be(30m, "the value sent for the calculated column is ignored");

        await Admin.UpdateAsync(new Entity(table, id) { ["new_total"] = new Money(1m) });
        (await TotalAsync(table, id)).Should().Be(30m);

        await Admin.UpdateAsync(new Entity(table, id) { ["new_quantity"] = 4, ["new_total"] = new Money(1m) });
        (await TotalAsync(table, id)).Should().Be(40m, "the sources change the value, the value sent does not");

        // The Web API ignores it the same way.
        var set = Store.GetEntity(table)!.EntitySetName;
        var patch = await SendAsync(HttpMethod.Patch, $"{set}({id})", """{ "new_total": 5, "new_amount": 20 }""");
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent, await patch.Content.ReadAsStringAsync());
        (await TotalAsync(table, id)).Should().Be(80m);
    }

    [Fact]
    public async Task Post_image_at_stage_40_has_calculated_value()
    {
        var table = await TableAsync("i");
        var id = await OrderAsync(table, 10m, 2);
        await ScalarAsync($"DELETE FROM data.{TestEntities.PipeLog}");

        var assembly = await PluginTestHelper.RegisterAssemblyAsync(Admin, PluginTestHelper.TestPluginBytes);
        var type = await PluginTestHelper.RegisterTypeAsync(Admin, assembly, "Xrmd.TestPlugins.ContextRecorderPlugin");
        var step = await PluginTestHelper.RegisterStepAsync(Admin, type, "Update", table, 40, config: TestEntities.PipeLog);
        await PluginTestHelper.RegisterImageAsync(Admin, step, 1, "post", "new_total,new_quantity");

        await Admin.UpdateAsync(new Entity(table, id) { ["new_quantity"] = 5, ["new_total"] = new Money(1m) });

        var data = (string?)await ScalarAsync($"SELECT TOP 1 pb_data FROM data.{TestEntities.PipeLog} WHERE pb_name LIKE N'Update|40|%' ORDER BY createdon DESC");
        data.Should().NotBeNull("the step ran");
        var lines = data!.Split('\n');
        lines.Single(l => l.StartsWith("target=", StringComparison.Ordinal))["target=".Length..].Split(',')
            .Should().NotContain("new_total", "a calculated column is never in a plug-in Target (K5)");
        var post = lines.Single(l => l.StartsWith("postvalues=", StringComparison.Ordinal))["postvalues=post:".Length..].Split(',')
            .Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        post["new_quantity"].Should().Be("5");
        decimal.Parse(post["new_total"], CultureInfo.InvariantCulture).Should().Be(50m, "the post-image carries the value computed after the update");
    }

    [Fact]
    public async Task Choice_compared_with_empty_gives_the_right_value()
    {
        var table = await TableAsync("e");
        OptionSetMetadata Options(params int[] values) =>
            new(new OptionMetadataCollection(values.Select(v => new OptionMetadata(Label("Option " + v), v)).ToList()))
            {
                IsGlobal = false, OptionSetType = OptionSetType.Picklist,
            };
        await CreateAttributeAsync(table, new PicklistAttributeMetadata
        {
            LogicalName = "new_status", SchemaName = "new_Status", DisplayName = Label("Status"), OptionSet = Options(100000000, 100000001),
        });
        // new_tier: 100000002 when new_status equals empty, else 100000000; new_tierset: the same with NotEqual.
        var empty = Xaml("picklist-branches", table).Replace("[New Object() { ConditionBranchStep2_2 }]", "[New Object() { Nothing }]");
        await CreateAttributeAsync(table, Calculated(
            new PicklistAttributeMetadata { LogicalName = "new_tier", OptionSet = Options(100000000, 100000001, 100000002) }, empty));
        await CreateAttributeAsync(table, Calculated(
            new PicklistAttributeMetadata { LogicalName = "new_tierset", OptionSet = Options(100000000, 100000001, 100000002) },
            empty.Replace("new_tier", "new_tierset").Replace("x:Key=\"ConditionOperator\">Equal<", "x:Key=\"ConditionOperator\">NotEqual<")));

        var none = await OrderAsync(table, 1m, 1);
        var set = await Admin.CreateAsync(new Entity(table) { ["new_name"] = "Set", ["new_status"] = new OptionSetValue(100000001) });

        async Task<(int?, int?)> TiersAsync(Guid id)
        {
            var row = await Admin.RetrieveAsync(table, id, new ColumnSet("new_tier", "new_tierset"));
            return (row.GetAttributeValue<OptionSetValue>("new_tier")?.Value, row.GetAttributeValue<OptionSetValue>("new_tierset")?.Value);
        }
        (await TiersAsync(none)).Should().Be((100000002, 100000000), "an empty choice equals empty");
        (await TiersAsync(set)).Should().Be((100000000, 100000002), "a choice with a value does not equal empty");
    }

    // ---- security (K10, Review Focus 3) -------------------------------------------------------------------------------

    [Fact]
    public async Task Calculated_ignores_source_field_security_but_honours_its_own()
    {
        var table = await TableAsync("s", securedAmount: true);
        await CreateAttributeAsync(table, Calculated(
            new MoneyAttributeMetadata { LogicalName = "new_securetotal", PrecisionSource = 0, Precision = 2, IsSecured = true },
            Xaml("arithmetic-money", table).Replace("new_total", "new_securetotal")));
        var id = await OrderAsync(table, 10m, 3);

        var oid = Guid.NewGuid();
        var user = await CreateUserAsync(fx, await RootBusinessUnitAsync(fx), oid);
        await GrantAsync(fx, user, team: false, ($"prvRead{table}", DepthMask.Global));
        var reader = ServiceFor(fx, oid);

        var row = await reader.RetrieveAsync(table, id, new ColumnSet("new_amount", "new_total", "new_securetotal"));
        row.GetAttributeValue<Money>("new_amount").Should().BeNull("the secured source is not readable");
        row.GetAttributeValue<Money>("new_total")!.Value.Should().Be(30m, "the expression reads its sources with system rights");
        row.GetAttributeValue<Money>("new_securetotal").Should().BeNull("the secured calculated column is not readable");

        // A filter on the readable calculated column works; on the secured one it is refused, never answered empty.
        var byTotal = new QueryExpression(table) { ColumnSet = new ColumnSet("new_total") };
        byTotal.Criteria.AddCondition("new_total", ConditionOperator.Equal, 30m);
        byTotal.Criteria.AddCondition(table + "id", ConditionOperator.Equal, id);
        (await reader.RetrieveMultipleAsync(byTotal)).Entities.Should().ContainSingle();

        var bySecured = new QueryExpression(table) { ColumnSet = new ColumnSet("new_name") };
        bySecured.Criteria.AddCondition("new_securetotal", ConditionOperator.Equal, 30m);
        XrmdFaultException? fault = null;
        try
        {
            await reader.RetrieveMultipleAsync(bySecured);
        }
        catch (XrmdFaultException ex)
        {
            fault = ex;
        }
        fault.Should().NotBeNull();
        fault!.HttpStatus.Should().Be(403);
        fault.ErrorCode.ToString("x8", CultureInfo.InvariantCulture).Should().Be(Denied[2..]);

        // The administrator reads both.
        var all = await Admin.RetrieveAsync(table, id, new ColumnSet("new_total", "new_securetotal"));
        all.GetAttributeValue<Money>("new_securetotal")!.Value.Should().Be(30m);
    }

    // ---- audit (K5) ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Audit_has_no_entry_for_calculated()
    {
        var table = await TableAsync("a", audit: true);
        var organization = (Guid)(await ScalarAsync("SELECT TOP 1 organizationid FROM data.organization"))!;
        await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = true });
        try
        {
            var id = await OrderAsync(table, 10m, 2);
            await Admin.UpdateAsync(new Entity(table, id) { ["new_quantity"] = 3, ["new_total"] = new Money(1m) });

            var changes = new List<string>();
            await using (var conn = await TestProvider.OpenAsync(fx.ConnectionString))
            {
                await using var cmd = TestSql.Command(conn, "SELECT action, changedata FROM data.audit WHERE objectid = @id ORDER BY createdon");
                cmd.Parameters.AddWithValue("@id", id);
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync()) changes.Add(rd.GetInt32(0) + ":" + rd.GetString(1));
            }
            changes.Should().HaveCount(2, "a create and an update are audited");
            changes.Should().OnlyContain(c => c.Contains("new_quantity"));
            changes.Should().NotContain(c => c.Contains("new_total"), "a calculated column is not audited");
        }
        finally
        {
            await Admin.UpdateAsync(new Entity("organization", organization) { ["isauditenabled"] = false });
        }
    }

    // ---- duplicate detection and auto-numbers --------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_rule_on_calculated_is_refused()
    {
        var table = await TableAsync("d");
        var kit = new DuplicateDetectionTestKit(fx);
        await kit.InitializeAsync(false, table);
        try
        {
            (await RefusedAsync(() => kit.RuleAsync("new_total", table)))
                .Should().Be($"Duplicate detection rules cannot use the calculated column '{table}.new_total'.");
            // A simple column of the same table is accepted.
            await kit.RuleAsync("new_name", table);
        }
        finally
        {
            await kit.DisposeAsync();
        }
    }

    [Fact]
    public async Task AutoNumberFormat_on_calculated_is_refused()
    {
        var table = await TableAsync("n");
        var code = Calculated(new StringAttributeMetadata { LogicalName = "new_code", MaxLength = 100, AutoNumberFormat = "C-{SEQNUM:4}" },
            Xaml("trim", table));
        (await RefusedAsync(() => CreateAttributeAsync(table, code))).Should().Contain($"'{table}.new_code'").And.Contain("calculated");

        await CreateAttributeAsync(table, Calculated(new StringAttributeMetadata { LogicalName = "new_code", MaxLength = 100 }, Xaml("trim", table)));
        (await RefusedAsync(() => UpdateAttributeAsync(table, new StringAttributeMetadata { LogicalName = "new_code", AutoNumberFormat = "C-{SEQNUM:4}" })))
            .Should().Contain("calculated");
    }

    // ---- metadata messages (K3, K4, K12) ------------------------------------------------------------------------------

    [Fact]
    public async Task Web_api_creates_and_reads_calculated_attribute()
    {
        var table = await TableAsync("api");
        var formula = Xaml("arithmetic-money", table).Replace("new_total", "new_webtotal");
        var body = new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.MoneyAttributeMetadata",
            ["LogicalName"] = "new_webtotal", ["SchemaName"] = "new_WebTotal", ["PrecisionSource"] = 0, ["Precision"] = 2,
            ["DisplayName"] = JsonNode.Parse("""{ "LocalizedLabels": [ { "Label": "Web total", "LanguageCode": 1033 } ] }"""),
            ["RequiredLevel"] = JsonNode.Parse("""{ "Value": "None" }"""),
            ["SourceType"] = 1, ["FormulaDefinition"] = formula,
            ["SourceTypeMask"] = 16, // server-owned: ignored
        };
        var post = await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{table}')/Attributes", body.ToJsonString());
        post.StatusCode.Should().Be(HttpStatusCode.NoContent, await post.Content.ReadAsStringAsync());

        var get = await SendAsync(HttpMethod.Get,
            $"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='new_webtotal')/Microsoft.Dynamics.CRM.MoneyAttributeMetadata");
        var text = await get.Content.ReadAsStringAsync();
        get.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var read = JsonNode.Parse(text)!;
        read["SourceType"]!.GetValue<int>().Should().Be(1);
        read["FormulaDefinition"]!.GetValue<string>().Should().Be(formula);
        read["SourceTypeMask"]!.GetValue<int>().Should().Be(1, "it reads two simple columns of its own record");
        read["IsValidForCreate"]!.GetValue<bool>().Should().BeFalse();
        read["IsValidForUpdate"]!.GetValue<bool>().Should().BeFalse();

        var id = await OrderAsync(table, 4m, 5);
        (await TotalAsync(table, id, "new_webtotal")).Should().Be(20m);

        // A PUT of the definition read back (with its mask) changes the formula: new_webtotal = new_total / 4.
        read["FormulaDefinition"] = Chain(table, "new_webtotal", "new_total", 4, money: true);
        read["@odata.type"] ??= "Microsoft.Dynamics.CRM.MoneyAttributeMetadata";
        var put = await SendAsync(HttpMethod.Put,
            $"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='new_webtotal')", read.ToJsonString());
        put.StatusCode.Should().Be(HttpStatusCode.NoContent, await put.Content.ReadAsStringAsync());
        (await TotalAsync(table, id, "new_webtotal")).Should().Be(5m);
    }

    [Fact]
    public async Task Update_formula_changes_values()
    {
        var table = await TableAsync("u");
        await CreateAttributeAsync(table, Calculated(new DecimalAttributeMetadata { LogicalName = "new_part", Precision = 2 },
            Xaml("arithmetic-money", table).Replace("new_total", "new_part").Replace("Value=\"mxs:Money\"", "Value=\"x:Decimal\"")));
        var id = await OrderAsync(table, 10m, 3);
        (await TotalAsync(table, id, "new_part")).Should().Be(30m);
        ComputedColumnRules.MaskOf(await RetrieveAttributeAsync(table, "new_part")).Should().Be(1);

        // new_part = new_total / 4: it now reads a calculated column. The cached definition shows the new mask at once (K12).
        var changed = new DecimalAttributeMetadata { LogicalName = "new_part" };
        ComputedColumnRules.SetFormula(changed, Chain(table, "new_part", "new_total", 4));
        await UpdateAttributeAsync(table, changed);

        (await TotalAsync(table, id, "new_part")).Should().Be(7.5m);
        var read = await RetrieveAttributeAsync(table, "new_part");
        ComputedColumnRules.FormulaOf(read).Should().Contain("Divide");
        ComputedColumnRules.MaskOf(read).Should().Be(8, "it reads another calculated column");
        ComputedColumnRules.MaskOf(Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == "new_part")).Should().Be(8);

        // The SourceType cannot change (K4), and an invalid formula is refused with the column named.
        (await RefusedAsync(() => UpdateAttributeAsync(table, new DecimalAttributeMetadata { LogicalName = "new_part", SourceType = 0 })))
            .Should().Contain($"{table}.new_part");
        var broken = new DecimalAttributeMetadata { LogicalName = "new_part" };
        ComputedColumnRules.SetFormula(broken, "<Activity/>");
        (await RefusedAsync(() => UpdateAttributeAsync(table, broken))).Should().Contain($"{table}.new_part");
        (await TotalAsync(table, id, "new_part")).Should().Be(7.5m, "a refused update changes nothing");
    }

    [Fact]
    public async Task Update_that_breaks_a_dependent_chain_is_refused()
    {
        // new_c1 = new_total / 2 ... new_c4 = new_c3 / 2: new_c4 ends a chain of five calculated columns (the limit).
        var table = await TableAsync("c");
        var source = "new_total";
        foreach (var column in new[] { "new_c1", "new_c2", "new_c3", "new_c4" })
        {
            await CreateAttributeAsync(table, Calculated(new DecimalAttributeMetadata { LogicalName = column, Precision = 4 }, Chain(table, column, source)));
            source = column;
        }

        // new_total = new_base / 1, where new_base is calculated: new_c4's chain would be six long.
        await CreateAttributeAsync(table, Calculated(new MoneyAttributeMetadata { LogicalName = "new_base", PrecisionSource = 0, Precision = 2 },
            Xaml("arithmetic-money", table).Replace("new_total", "new_base")));
        var longer = new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.MoneyAttributeMetadata", ["FormulaDefinition"] = Chain(table, "new_total", "new_base", 1, money: true),
        };
        var put = await SendAsync(HttpMethod.Put, $"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='new_total')", longer.ToJsonString());
        var text = await put.Content.ReadAsStringAsync();
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        var error = JsonNode.Parse(text)!["error"]!;
        error["code"]!.GetValue<string>().Should().Be("0x80040203");
        error["message"]!.GetValue<string>().Should()
            .StartWith($"The change of '{table}.new_total' would make the calculated column '{table}.new_c4' invalid: ")
            .And.Contain($"The calculated column '{table}.new_c4' chains more than 5 calculated columns: {table}.new_c4 -> ");
        ComputedColumnRules.FormulaOf(Store.GetEntity(table)!.Attributes.Single(a => a.LogicalName == "new_total"))
            .Should().NotContain("new_base", "a refused update changes nothing");

        var id = await OrderAsync(table, 16m, 1);
        (await TotalAsync(table, id, "new_c4")).Should().Be(1m);
    }

    [Fact]
    public async Task Update_of_a_parent_column_that_breaks_a_child_is_refused()
    {
        // The parent's new_plabel reads its own record; the child's new_clabel reads new_plabel through its lookup.
        var parent = await TableAsync("p");
        await Store.AddAttributeAsync(parent, new LookupAttributeMetadata
        {
            LogicalName = "new_accountid", SchemaName = "new_AccountId", Targets = ["account"], DisplayName = Label("Account"),
        });
        await CreateAttributeAsync(parent, Calculated(new StringAttributeMetadata { LogicalName = "new_plabel", MaxLength = 200 },
            Xaml("trim", parent).Replace("new_code", "new_plabel")));

        var child = await TableAsync("k");
        await Store.AddAttributeAsync(child, new LookupAttributeMetadata
        {
            LogicalName = "new_parentid", SchemaName = "new_ParentId", Targets = [parent], DisplayName = Label("Parent"),
        });
        await CreateAttributeAsync(child, Calculated(new StringAttributeMetadata { LogicalName = "new_clabel", MaxLength = 400 },
            Xaml("concat-related", child).Replace("new_label", "new_clabel").Replace("related_new_accountid#account", $"related_new_parentid#{parent}")
                .Replace("Attribute=\"name\"", "Attribute=\"new_plabel\"").Replace("EntityName=\"account\"", $"EntityName=\"{parent}\"")));

        // new_plabel = name / account name: a parent's calculated column that reads a third table.
        var reaching = new StringAttributeMetadata { LogicalName = "new_plabel" };
        ComputedColumnRules.SetFormula(reaching, Xaml("concat-related", parent).Replace("new_label", "new_plabel"));
        (await RefusedAsync(() => UpdateAttributeAsync(parent, reaching)))
            .Should().StartWith($"The change of '{parent}.new_plabel' would make the calculated column '{child}.new_clabel' invalid: ");

        // Without the dependent, the same update is accepted.
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = child, LogicalName = "new_clabel" });
        await UpdateAttributeAsync(parent, reaching);
        ComputedColumnRules.MaskOf(await RetrieveAttributeAsync(parent, "new_plabel")).Should().Be(3);
    }
}

using Xrmd.Abstractions.Storage;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Formula column definitions (plan 2.12c, Task 2) through the metadata messages of the SDK and the Web API, with the server's Power Fx
/// translator: no SQL column, the stored formula (without its leading <c>=</c>) and <c>SourceTypeMask</c>, the dependency check on
/// delete and the refusal of a formula that would change the column's type. Each test builds its own table (<c>new_fm*</c>; the publisher prefix of the shared server is <c>new</c>).
/// </summary>
[Collection("xrmd")]
public sealed class FormulaMetadataTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Api = "/api/data/v9.2/";

    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();
    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public Task InitializeAsync() => TestEntities.EnsurePipeAsync(Store);

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static Label Label(string text) => new(text, 1033);

    private static T Formula<T>(T attribute, string formula) where T : AttributeMetadata
    {
        attribute.SourceType = ComputedColumnRules.Formula;
        attribute.SchemaName ??= attribute.LogicalName;
        attribute.DisplayName ??= Label(attribute.LogicalName);
        ComputedColumnRules.SetFormula(attribute, formula);
        return attribute;
    }

    /// <summary>
    /// A table <c>new_fm{label}{run}</c> with <c>new_name</c>, <c>new_amount</c> (decimal), <c>new_qty</c> (whole number),
    /// <c>new_price</c> (currency) and the calculated <c>new_total</c> = price × qty (tests/fixtures/calculated/arithmetic-money.xaml).
    /// </summary>
    private async Task<string> TableAsync(string label)
    {
        var name = $"new_fm{label}{_run}";
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
        await CreateAttributeAsync(name, new DecimalAttributeMetadata { LogicalName = "new_amount", SchemaName = "new_Amount", DisplayName = Label("Amount"), Precision = 2 });
        await CreateAttributeAsync(name, new IntegerAttributeMetadata { LogicalName = "new_qty", SchemaName = "new_Qty", DisplayName = Label("Quantity") });
        await CreateAttributeAsync(name, new MoneyAttributeMetadata { LogicalName = "new_price", SchemaName = "new_Price", DisplayName = Label("Price"), PrecisionSource = 2 });
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "calculated", "arithmetic-money.xaml"))
            .Replace("xrmv_calcorder", name).Replace("xrmv_amount", "new_price").Replace("xrmv_quantity", "new_qty").Replace("xrmv_", "new_");
        var total = new MoneyAttributeMetadata
        {
            LogicalName = "new_total", SchemaName = "new_Total", DisplayName = Label("Total"), PrecisionSource = 0, Precision = 2,
            SourceType = ComputedColumnRules.Calculated,
        };
        ComputedColumnRules.SetFormula(total, xaml);
        await CreateAttributeAsync(name, total);
        return name;
    }

    private Task CreateAttributeAsync(string table, AttributeMetadata attribute) =>
        Admin.ExecuteAsync(new CreateAttributeRequest { EntityName = table, Attribute = attribute });

    private Task UpdateAttributeAsync(string table, AttributeMetadata attribute) =>
        Admin.ExecuteAsync(new UpdateAttributeRequest { EntityName = table, Attribute = attribute });

    private async Task<AttributeMetadata> RetrieveAttributeAsync(string table, string column) =>
        ((RetrieveAttributeResponse)await Admin.ExecuteAsync(new RetrieveAttributeRequest { EntityLogicalName = table, LogicalName = column }))
        .AttributeMetadata;

    private async Task<List<string>> SqlColumnsAsync(string table)
    {
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(conn, "SELECT column_name FROM information_schema.columns WHERE table_schema = 'data' AND table_name = CAST(@t AS varchar(300))");
        cmd.Parameters.AddWithValue("@t", table);
        var names = new List<string>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) names.Add(rd.GetString(0));
        return names;
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

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevTokenFactory.Create(fx.AdminObjectId, fx.BaseUrl));
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await fx.Client.SendAsync(req);
    }

    // ---- tests --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAttribute_formula_adds_no_sql_column()
    {
        var table = await TableAsync("sql");
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxtotal", IsValidForCreate = true },
            "new_amount * new_qty"));

        (await SqlColumnsAsync(table)).Should().Contain("new_amount").And.NotContain("new_fxtotal");
        var stored = await RetrieveAttributeAsync(table, "new_fxtotal");
        stored.SourceType.Should().Be(3);
        stored.IsValidForCreate.Should().BeFalse();
        stored.IsValidForUpdate.Should().BeFalse();
        ((DecimalAttributeMetadata)stored).Precision.Should().Be(2, "a formula decimal without a precision gets 2 (Learn)");
    }

    [Fact]
    public async Task RetrieveAttribute_returns_formula_and_mask()
    {
        var table = await TableAsync("mask");
        await CreateAttributeAsync(table, Formula(new StringAttributeMetadata { LogicalName = "new_fxlabel", MaxLength = 4000 },
            "Upper(new_name) & \"-\" & Text(new_qty, \"0\")"));
        await CreateAttributeAsync(table, Formula(new IntegerAttributeMetadata { LogicalName = "new_fxowner" }, "Len(createdby.fullname)"));
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxcalc" }, "Decimal(new_total) + new_qty"));
        await CreateAttributeAsync(table, Formula(new BooleanAttributeMetadata
        {
            LogicalName = "new_fxchain",
            OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(Label("Yes"), 1), new OptionMetadata(Label("No"), 0)),
        }, "IsBlank(new_fxlabel)"));

        var label = await RetrieveAttributeAsync(table, "new_fxlabel");
        ComputedColumnRules.FormulaOf(label).Should().Be("Upper(new_name) & \"-\" & Text(new_qty, \"0\")", "stored as written, without '=' (F2)");
        ComputedColumnRules.MaskOf(label).Should().Be(1);
        ComputedColumnRules.MaskOf(await RetrieveAttributeAsync(table, "new_fxowner")).Should().Be(2);
        ComputedColumnRules.MaskOf(await RetrieveAttributeAsync(table, "new_fxcalc")).Should().Be(9, "it reads a calculated column");
        ComputedColumnRules.MaskOf(await RetrieveAttributeAsync(table, "new_fxchain")).Should().Be(8, "it reads a formula column");
    }

    /// <summary>chained.xaml (a Decimal column) for <paramref name="target"/> of <paramref name="table"/>: <paramref name="source"/> / 2.</summary>
    private static string Chain(string table, string target, string source) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "calculated", "chained.xaml"))
            .Replace("xrmv_calcorder", table).Replace("xrmv_double", target).Replace("\"xrmv_total\"", $"\"{source}\"");

    private static DecimalAttributeMetadata CalculatedDecimal(string name, string xaml)
    {
        var a = new DecimalAttributeMetadata
        {
            LogicalName = name, SchemaName = name, DisplayName = Label(name), Precision = 2, SourceType = ComputedColumnRules.Calculated,
        };
        ComputedColumnRules.SetFormula(a, xaml);
        return a;
    }

    [Fact]
    public async Task Calculated_column_reads_a_formula_column()
    {
        // Controller ruling (Learn advises against it, but allows it): metadata only, the SQL of fx functions is Task 3's.
        var table = await TableAsync("mix");
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxtotal" }, "new_amount * new_qty"));
        await CreateAttributeAsync(table, CalculatedDecimal("new_half", Chain(table, "new_half", "new_fxtotal")));
        var half = await RetrieveAttributeAsync(table, "new_half");
        half.SourceType.Should().Be(1);
        ComputedColumnRules.MaskOf(half).Should().Be(8, "it reads a formula column");

        // Ruling F10: a chain mixing calculated and formula columns may hold 10 columns. new_c1 ... new_c5 (calculated) then
        // new_fxtotal and new_f1 ... new_f4 (formula): 10 columns, accepted; a formula column on top makes 11, refused. (A sixth
        // calculated column on top would meet the calculated-only limit of 5 first.)
        var previous = "new_fxtotal";
        for (var i = 1; i <= 4; i++)
        {
            await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = $"new_f{i}" }, $"{previous} + 1"));
            previous = $"new_f{i}";
        }
        for (var i = 1; i <= 5; i++)
        {
            await CreateAttributeAsync(table, CalculatedDecimal($"new_c{i}", Chain(table, $"new_c{i}", previous)));
            previous = $"new_c{i}";
        }
        (await RetrieveAttributeAsync(table, "new_c5")).SourceType.Should().Be(1);
        (await RefusedAsync(() => CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_top" }, $"{previous} + 1"))))
            .Should().StartWith($"The formula column '{table}.new_top' chains more than 10 formula or calculated columns");
    }

    [Fact]
    public async Task Delete_of_referenced_column_is_refused()
    {
        var table = await TableAsync("del");
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxtotal" }, "new_amount * new_qty"));

        // new_amount is read by the formula column only (new_qty is also read by the calculated new_total).
        var message = await RefusedAsync(() => Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_amount" }));
        message.Should().Be($"The component cannot be deleted because it is referenced by formula column '{table}.new_fxtotal'.");
        (await SqlColumnsAsync(table)).Should().Contain("new_amount", "a refused delete drops nothing");

        // Deleting the formula column frees its sources.
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_fxtotal" });
        await Admin.ExecuteAsync(new DeleteAttributeRequest { EntityLogicalName = table, LogicalName = "new_amount" });
        (await SqlColumnsAsync(table)).Should().NotContain("new_amount");
    }

    [Fact]
    public async Task Type_change_on_update_is_refused()
    {
        var table = await TableAsync("upd");
        await CreateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxtotal" }, "new_amount * new_qty"));

        // Review Focus 4 (Learn): the new formula must give the column's type.
        (await RefusedAsync(() => UpdateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxtotal" }, "new_name & \"x\""))))
            .Should().Be($"The formula of '{table}.new_fxtotal' gives Text, but the column is Decimal.");
        await UpdateAttributeAsync(table, Formula(new DecimalAttributeMetadata { LogicalName = "new_fxtotal" }, "new_amount + new_qty"));
        ComputedColumnRules.FormulaOf(await RetrieveAttributeAsync(table, "new_fxtotal")).Should().Be("new_amount + new_qty");
    }

    [Fact]
    public async Task Web_api_creates_formula_attribute()
    {
        var table = await TableAsync("api");
        var body = new JsonObject
        {
            ["@odata.type"] = "Microsoft.Dynamics.CRM.DecimalAttributeMetadata",
            ["LogicalName"] = "new_webtotal", ["SchemaName"] = "new_WebTotal",
            ["DisplayName"] = JsonNode.Parse("""{ "LocalizedLabels": [ { "Label": "Web total", "LanguageCode": 1033 } ] }"""),
            ["RequiredLevel"] = JsonNode.Parse("""{ "Value": "None" }"""),
            ["SourceType"] = 3, ["FormulaDefinition"] = "new_amount * new_qty",
            ["SourceTypeMask"] = 16, // server-owned: ignored
        };
        var post = await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{table}')/Attributes", body.ToJsonString());
        post.StatusCode.Should().Be(HttpStatusCode.NoContent, await post.Content.ReadAsStringAsync());

        var get = await SendAsync(HttpMethod.Get,
            $"EntityDefinitions(LogicalName='{table}')/Attributes(LogicalName='new_webtotal')/Microsoft.Dynamics.CRM.DecimalAttributeMetadata");
        var text = await get.Content.ReadAsStringAsync();
        get.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var read = JsonNode.Parse(text)!;
        read["SourceType"]!.GetValue<int>().Should().Be(3);
        read["FormulaDefinition"]!.GetValue<string>().Should().Be("new_amount * new_qty");
        read["SourceTypeMask"]!.GetValue<int>().Should().Be(1);
        read["Precision"]!.GetValue<int>().Should().Be(2);
        read["IsValidForCreate"]!.GetValue<bool>().Should().BeFalse();
        read["IsValidForUpdate"]!.GetValue<bool>().Should().BeFalse();

        // A refused formula is a 400 naming the column.
        body["LogicalName"] = "new_webbad";
        body["SchemaName"] = "new_WebBad";
        body["FormulaDefinition"] = "IfError(new_amount, 0)";
        var bad = await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{table}')/Attributes", body.ToJsonString());
        var error = JsonNode.Parse(await bad.Content.ReadAsStringAsync())!["error"]!;
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, error.ToJsonString());
        error["code"]!.GetValue<string>().Should().Be("0x80040203");
        error["message"]!.GetValue<string>().Should()
            .Be($"The formula column '{table}.new_webbad' uses the function 'IfError', which formula columns do not support.");

        // Q1 (Dataverse, 2026-11-19, finding 8): the Web API refuses a FormulaDefinition that starts with '='.
        body["FormulaDefinition"] = "=new_amount * new_qty";
        var equals = await SendAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{table}')/Attributes", body.ToJsonString());
        var equalsError = JsonNode.Parse(await equals.Content.ReadAsStringAsync())!["error"]!;
        equals.StatusCode.Should().Be(HttpStatusCode.BadRequest, equalsError.ToJsonString());
        equalsError["message"]!.GetValue<string>().Should().Be($"The formula column '{table}.new_webbad' has a formula that starts with '=': " +
            "give the formula without it (only a solution's formula file writes it with '=').");
    }
}

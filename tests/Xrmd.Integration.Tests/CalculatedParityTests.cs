using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;
using Xrmd.Solution;
using static Xrmd.Integration.Tests.CalculatedParityExport;
using static Xrmd.Integration.Tests.SecurityTestHelper;

namespace Xrmd.Integration.Tests;

/// <summary>
/// The 2.12a parity export (plan 2.12a, Task 6, ruling K2): Power Builders' own unmanaged export <c>xrmCalcTest_1_0_0_4.zip</c>
/// (publisher xrmv, invented data; tests/fixtures/solutions/RealExports). It holds 27 calculated columns on <c>xrmv_calcorder</c>,
/// rollup columns on <c>account</c> (2.12b) and Power Fx formula columns on <c>xrmv_calcorder</c> (2.12c).
/// </summary>
internal static class CalculatedParityExport
{
    public const string SolutionName = "xrmCalcTest";

    public const string Order = "xrmv_calcorder";

    public static byte[] Zip() =>
        File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "solutions", "RealExports", "xrmCalcTest_1_0_0_4.zip"));

    /// <summary>The <c>SourceType</c> of an <c>attribute</c> element of customizations.xml (0 when it has none).</summary>
    private static int SourceType(XElement attribute) =>
        int.Parse(attribute.Element("SourceType")?.Value ?? "0", CultureInfo.InvariantCulture);

    /// <summary>The formula file of the export's formula columns (plan 2.12c, ruling F3), as an archive path.</summary>
    public const string FormulaFilePath = "Formulas/xrmv_calcorder-FormulaDefinitions.yaml";

    /// <summary>The formula columns of the export (all in <see cref="FormulaFilePath"/>) and their formulas without the leading '='.</summary>
    public static readonly IReadOnlyDictionary<string, string> Formulas = new Dictionary<string, string>
    {
        ["xrmv_fxlabel"] = "Upper(xrmv_code) & \"-\" & Text(xrmv_qty, \"0\")",
        ["xrmv_fxnodue"] = "IsBlank(xrmv_duedate)",
        ["xrmv_fxplus3"] = "DateAdd(xrmv_startedon,3,TimeUnit.Days)",
    };

    /// <summary>A copy of the export whose formula file has no line for <paramref name="column"/>; every other entry byte for byte.</summary>
    public static byte[] WithoutFormulaLine(string column)
    {
        using var source = new ZipArchive(new MemoryStream(Zip()), ZipArchiveMode.Read);
        var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var from = entry.Open();
                using var to = target.CreateEntry(entry.FullName).Open();
                if (entry.FullName != FormulaFilePath)
                {
                    from.CopyTo(to);
                    continue;
                }
                using var reader = new StreamReader(from, new UTF8Encoding(false));
                var lines = reader.ReadToEnd().Split("\r\n").Where(l => !l.StartsWith(column + ":", StringComparison.Ordinal));
                to.Write(new UTF8Encoding(false).GetBytes(string.Join("\r\n", lines)));
            }
        }
        return output.ToArray();
    }

    /// <summary>A copy of the export without its rollup (<c>SourceType</c> 2) and formula (3) columns and the files they name.</summary>
    public static byte[] WithoutRollupAndFormulaColumns() => Without(2, 3);

    /// <summary>
    /// A copy of the export without the columns of <paramref name="sourceTypes"/> and the files they name: every other entry is
    /// copied byte for byte, and customizations.xml loses only those <c>attribute</c> elements (with the whitespace before them).
    /// </summary>
    public static byte[] Without(params int[] sourceTypes)
    {
        using var source = new ZipArchive(new MemoryStream(Zip()), ZipArchiveMode.Read);
        var customizationsEntry = source.GetEntry("customizations.xml")!;
        XDocument customizations;
        using (var stream = customizationsEntry.Open())
            customizations = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var removed = customizations.Descendants("attribute").Where(a => sourceTypes.Contains(SourceType(a))).ToList();
        var removedFiles = removed.Select(a => a.Element("FormulaDefinitionFileName")?.Value.TrimStart('/'))
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in removed)
        {
            if (attribute.PreviousNode is XText whitespace && string.IsNullOrWhiteSpace(whitespace.Value)) whitespace.Remove();
            attribute.Remove();
        }

        var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                if (removedFiles.Contains(entry.FullName)) continue;
                var copy = target.CreateEntry(entry.FullName);
                using var to = copy.Open();
                if (entry.FullName == "customizations.xml")
                {
                    var bytes = new UTF8Encoding(false).GetBytes(customizations.Root!.ToString(SaveOptions.DisableFormatting));
                    to.Write(bytes);
                }
                else
                {
                    using var from = entry.Open();
                    from.CopyTo(to);
                }
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// The values Dataverse recorded after the parity export (<c>tests/fixtures/calculated/real/&lt;file&gt;</c>): one dictionary per
    /// row, column → text ("" when the column had no value). The files hold no quoted field.
    /// </summary>
    public static List<Dictionary<string, string>> Recorded(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(System.AppContext.BaseDirectory, "fixtures", "calculated", "real", file));
        if (lines.Any(l => l.Contains('"'))) throw new InvalidDataException($"{file} holds a quoted field.");
        var header = lines[0].Split(',');
        return lines.Skip(1).Where(l => l.Length > 0).Select(l =>
        {
            var cells = l.Split(',');
            if (cells.Length != header.Length) throw new InvalidDataException($"{file}: a row has {cells.Length} cells, the header {header.Length}.");
            return header.Zip(cells).ToDictionary(p => p.First, p => p.Second);
        }).ToList();
    }

    /// <summary>The calculated columns that read NOW; their recorded values hold at the time of the recording only.</summary>
    internal static readonly string[] NowColumns = ["xrmv_age", "xrmv_hours", "xrmv_minutes", "xrmv_weeks", "xrmv_months", "xrmv_years"];

    /// <summary>An order with the source values of a recorded one; its start moved by <paramref name="shift"/>.</summary>
    internal static Entity NewOrder(Dictionary<string, string> recorded, Dictionary<string, Guid> accountIds, string name, TimeSpan shift)
    {
        var order = new Entity(Order) { ["xrmv_name"] = name, ["xrmv_code"] = recorded["xrmv_code"] };
        if (recorded["xrmv_amount"].Length > 0) order["xrmv_amount"] = Dec(recorded["xrmv_amount"]);
        if (recorded["xrmv_price"].Length > 0) order["xrmv_price"] = new Money(Dec(recorded["xrmv_price"]));
        if (recorded["xrmv_qty"].Length > 0) order["xrmv_qty"] = int.Parse(recorded["xrmv_qty"], CultureInfo.InvariantCulture);
        if (recorded["xrmv_status"].Length > 0) order["xrmv_status"] = new OptionSetValue(int.Parse(recorded["xrmv_status"], CultureInfo.InvariantCulture));
        if (recorded["xrmv_startedon"].Length > 0) order["xrmv_startedon"] = Date(recorded["xrmv_startedon"]) + shift;
        if (recorded["xrmv_duedate"].Length > 0) order["xrmv_duedate"] = Date(recorded["xrmv_duedate"]);
        if (recorded["account"].Length > 0) order["xrmv_accountid"] = new EntityReference("account", accountIds[recorded["account"]]);
        return order;
    }

    internal static decimal Dec(string text) => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

    internal static DateTime Date(string text) =>
        DateTime.SpecifyKind(DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture), DateTimeKind.Utc);

    /// <summary>A column of a row written as the recording writes it ("" when it has no value).</summary>
    internal static string Text(Entity row, string column) => row.Contains(column) ? row[column] switch
    {
        null => "",
        Money money => Number(money.Value),
        decimal d => Number(d),
        double d => Number((decimal)d),
        int i => i.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        OptionSetValue o => o.Value.ToString(CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        string s => s,
        var other => throw new InvalidOperationException($"{column}: {other.GetType().Name}"),
    } : "";

    /// <summary>A number without trailing zeros, as the recording writes it.</summary>
    internal static string Number(decimal d) => (d / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Plan 2.7a, Task 7 (ruling (a)): the tables and columns of the export made through the metadata API instead of a solution
    /// import (ported in 2.7b on PostgreSQL), from the same customizations.xml read by the importer's reader: <c>xrmv_calcorder</c>
    /// with its plain columns, its lookup to <c>account</c>, its calculated columns (each once the columns it reads exist) and its
    /// formula columns; <c>account</c>'s budget and its four rollups. The server adds the companions (<c>_base</c>, <c>_date</c>,
    /// <c>_state</c>) as an import does. Waits for the rollup jobs to be told.
    /// </summary>
    public static async Task CreateThroughMetadataAsync(MetadataStore store, string connectionString)
    {
        var (solution, customizations, files) = SolutionZip.Read(Zip());
        var package = CustomizationsReader.Read(solution, customizations, files);
        var account = package.Entities.Single(e => e.LogicalName == "account");
        var order = package.Entities.Single(e => e.LogicalName == Order);
        var orderColumns = order.Attributes.Where(Own).ToList();
        var accountColumns = account.Attributes.Where(Own).ToList();

        var primary = orderColumns.Single(a => a.LogicalName == order.PrimaryNameAttribute);
        order.SetAttributes([]);
        await store.CreateEntityAsync(order, primary);
        foreach (var plain in orderColumns.Where(a => a != primary && (a.SourceType ?? 0) == 0 && a is not LookupAttributeMetadata))
            await store.AddAttributeAsync(Order, plain);
        var relationship = package.Relationships.OfType<OneToManyRelationshipMetadata>()
            .Single(r => r.ReferencingEntity == Order && r.ReferencedEntity == "account");
        var lookup = package.Lookups.Single(l => l.LogicalName == relationship.ReferencingAttribute);
        await TestEntities.InSessionAsync(connectionString, s => store.CreateOneToManyAsync(s, relationship, lookup, CancellationToken.None));
        foreach (var plain in accountColumns.Where(a => (a.SourceType ?? 0) == 0))
            await store.AddAttributeAsync("account", plain);

        // A calculated column may read another (xrmv_net2 reads xrmv_net): each round adds those whose columns exist.
        var pending = orderColumns.Where(a => a.SourceType == ComputedColumnRules.Calculated).ToList();
        while (pending.Count > 0)
        {
            var added = new List<AttributeMetadata>();
            Exception? last = null;
            foreach (var calculated in pending)
            {
                try
                {
                    await store.AddAttributeAsync(Order, calculated);
                    added.Add(calculated);
                }
                catch (XrmdValidationException e)
                {
                    last = e;
                }
            }
            if (added.Count == 0) throw new InvalidOperationException("The calculated columns of the export cannot be added.", last);
            pending.RemoveAll(added.Contains);
        }
        foreach (var formula in orderColumns.Where(a => a.SourceType == ComputedColumnRules.Formula))
            await store.AddAttributeAsync(Order, formula);
        foreach (var rollup in accountColumns.Where(a => a.SourceType == RollupColumns.Rollup))
            await store.AddAttributeAsync("account", rollup);
        await store.RollupJobNotifications;

        // The columns of the export a server adds itself: the companions of Currency, calculated and rollup columns.
        static bool Own(AttributeMetadata a) =>
            a.LogicalName.StartsWith("xrmv_", StringComparison.Ordinal) && a.AttributeOf is null
            && a.IsPrimaryId != true && !(a is MoneyAttributeMetadata { IsBaseCurrency: true });
    }

    /// <summary>The calculated columns of the export: logical name → its formula file (archive path, no leading slash) and bytes.</summary>
    public static Dictionary<string, (string Path, byte[] Bytes)> CalculatedFiles() => FormulaFiles(1);

    /// <summary>The rollup columns of the export (plan 2.12b): logical name → its formula file (archive path, no leading slash) and bytes.</summary>
    public static Dictionary<string, (string Path, byte[] Bytes)> RollupFiles() => FormulaFiles(2);

    private static Dictionary<string, (string Path, byte[] Bytes)> FormulaFiles(int sourceType)
    {
        var (_, customizations, files) = SolutionZip.Read(Zip());
        return customizations.Descendants("attribute")
            .Where(a => SourceType(a) == sourceType && a.Element("FormulaDefinitionFileName") is not null)
            .ToDictionary(a => a.Element("LogicalName")!.Value, a =>
            {
                var path = a.Element("FormulaDefinitionFileName")!.Value.TrimStart('/');
                return (path, files[path]);
            });
    }
}

/// <summary>
/// Plan 2.12a, Task 6: the parity export imports (with its rollup columns since plan 2.12b, Task 6, and its formula columns since
/// plan 2.12c, Task 5), every
/// calculated column computes the value its definition gives on invented records, and an export writes the definitions back byte for
/// byte. The expected values are derived by hand from the definitions (plan 2.12a, Q1) and the rules K7/K8; the values Dataverse
/// recorded are compared in part B (<c>Parity_values_match_recorded_dataverse_values</c>).
/// </summary>
[Collection("real-export")]
public sealed class CalculatedParityTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string Order = CalculatedParityExport.Order;

    private IMetadataStore Store => fx.App.Services.GetRequiredService<IMetadataStore>();

    private OrganizationService Admin => ServiceFor(fx, fx.AdminObjectId);

    public async Task InitializeAsync() => await fx.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<OrganizationResponse> ImportAsync(byte[] zip) =>
        Admin.ExecuteAsync(new ImportSolutionRequest { CustomizationFile = zip, ImportJobId = Guid.NewGuid() });

    private static readonly SemaphoreSlim ImportGate = new(1, 1);

    private static bool _imported;

    /// <summary>
    /// The refusal of a copy whose formula file lacks a column, then the import of the export as it is: once for the class, by
    /// whichever of its tests runs first (both need the table, and the refusals need a database where it does not exist yet).
    /// </summary>
    private async Task ImportOnceAsync()
    {
        await ImportGate.WaitAsync();
        try
        {
            if (_imported) return;
            await RefusalsThenImportAsync();
            _imported = true;
        }
        finally
        {
            ImportGate.Release();
        }
    }

    /// <summary>
    /// The export's tables for the tests of values (plan 2.7a, Task 7, ruling (a)): on SQL Server the import (with its refusals,
    /// so the tests of the import find the database they expect), on PostgreSQL, where the solution import is ported in 2.7b,
    /// the same tables and columns through the metadata API (<see cref="CalculatedParityExport.CreateThroughMetadataAsync"/>).
    /// </summary>
    private async Task TablesOnceAsync()
    {
        if (!TestProvider.IsPostgres)
        {
            await ImportOnceAsync();
            return;
        }
        await ImportGate.WaitAsync();
        try
        {
            if (_imported) return;
            await CalculatedParityExport.CreateThroughMetadataAsync((MetadataStore)Store, fx.ConnectionString);
            _imported = true;
        }
        finally
        {
            ImportGate.Release();
        }
    }

    private async Task RefusalsThenImportAsync()
    {
        // The export with a formula column missing from its formula file (plan 2.12c, ruling F3): refused, naming the column and the
        // file, before anything is written.
        var fault = (await FluentActions.Awaiting(() => ImportAsync(CalculatedParityExport.WithoutFormulaLine("xrmv_fxnodue")))
            .Should().ThrowAsync<XrmdFaultException>()).Which;
        fault.HttpStatus.Should().Be(400);
        fault.ErrorCode.Should().Be(ErrorCodes.InvalidArgument);
        fault.Message.Should().Be(
            "The formula file '/Formulas/xrmv_calcorder-FormulaDefinitions.yaml' has no formula for the formula column 'xrmv_calcorder.xrmv_fxnodue'.");
        Store.GetEntity(Order).Should().BeNull("nothing of a refused import is written");
        Store.GetEntity("account")!.Attributes.Should().NotContain(a => a.LogicalName == "xrmv_budget" || a.LogicalName == "xrmv_ordertotal");

        // The export as it is imports whole: calculated, rollup (plan 2.12b) and formula (plan 2.12c) columns.
        _importedAt = DateTime.UtcNow;
        await ImportAsync(CalculatedParityExport.Zip());
        await ((MetadataStore)Store).RollupJobNotifications;
    }

    private static DateTime _importedAt;

    [Fact]
    public async Task Parity_export_imports_with_rollups()
    {
        await ImportOnceAsync();

        // Plan 2.12b, Task 6: the four rollups of account, read-only, with their definitions kept verbatim, and the base of the
        // Currency one; the server generated the companions, which the export does not carry.
        var files = CalculatedParityExport.RollupFiles();
        files.Keys.Should().BeEquivalentTo(["xrmv_lastorder", "xrmv_ordercount", "xrmv_ordertotal", "xrmv_treebudget"]);
        var account = Store.GetEntity("account")!;
        foreach (var (column, (_, bytes)) in files)
        {
            var attribute = account.Attributes.Single(a => a.LogicalName == column);
            attribute.SourceType.Should().Be(RollupColumns.Rollup, column);
            attribute.IsValidForCreate.Should().BeFalse(column);
            ComputedColumnRules.FormulaOf(attribute).Should().Be(Encoding.UTF8.GetString(bytes), column);
            account.Attributes.Should().Contain(a => a.LogicalName == column + RollupColumns.DateSuffix, column);
            account.Attributes.Should().Contain(a => a.LogicalName == column + RollupColumns.StateSuffix, column);
        }
        account.Attributes.OfType<Microsoft.Xrm.Sdk.Metadata.MoneyAttributeMetadata>().Single(a => a.LogicalName == "xrmv_treebudget_base")
            .IsBaseCurrency.Should().BeTrue("the _base of a rollup Currency column is its base-currency companion");

        // R8, R14: each imported rollup's Mass Calculate Rollup Field job is due at once.
        await using var conn = await TestProvider.OpenAsync(fx.ConnectionString);
        await using var cmd = TestSql.Command(
            "SELECT data, postponeuntil FROM data.asyncoperation WHERE operationtype = 58 AND primaryentitytype = 'account'", conn);
        var due = new Dictionary<string, DateTime>();
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
            {
                using var data = System.Text.Json.JsonDocument.Parse(rd.GetString(0));
                due[data.RootElement.GetProperty("attribute").GetString()!] = rd.GetDateTime(1);
            }
        }
        due.Keys.Should().BeEquivalentTo(files.Keys);
        due.Values.Should().OnlyContain(d => d < _importedAt.AddMinutes(5), "due at once, not after the 12-hour delay");

        // An export writes them back as the export has them, the XAML byte for byte, and no companion.
        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest
        {
            SolutionName = CalculatedParityExport.SolutionName, Managed = false,
        })).ExportSolutionFile;
        var (_, customizations, written) = SolutionZip.Read(exported);
        foreach (var (column, (path, bytes)) in files)
        {
            var element = customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == column);
            element.Element("SourceType")!.Value.Should().Be("2", column);
            element.Element("FormulaDefinitionFileName")!.Value.Should().Be("/" + path, column);
            written[path].Should().Equal(bytes, "the XAML of {0} is written back byte for byte", column);
        }
        var treeBase = customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == "xrmv_treebudget_base");
        treeBase.Element("SourceType")!.Value.Should().Be("2");
        treeBase.Element("CalculationOf")!.Value.Should().Be("xrmv_TreeBudget");
        treeBase.Element("FormulaDefinitionFileName").Should().BeNull();
        customizations.Descendants("attribute").Select(a => a.Element("LogicalName")?.Value)
            .Should().NotContain(n => n != null && (n.EndsWith(RollupColumns.DateSuffix, StringComparison.Ordinal)
                                                   || n.EndsWith(RollupColumns.StateSuffix, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Parity_export_imports_whole()
    {
        await ImportOnceAsync();

        // Plan 2.12c, ruling F3: the three formula columns of the export, read-only, with their formulas from the table's formula file
        // stored without the leading '=' (F2).
        var order = Store.GetEntity(Order)!;
        foreach (var (column, formula) in CalculatedParityExport.Formulas)
        {
            var attribute = order.Attributes.Single(a => a.LogicalName == column);
            attribute.SourceType.Should().Be(ComputedColumnRules.Formula, column);
            attribute.IsValidForCreate.Should().BeFalse(column);
            attribute.IsValidForUpdate.Should().BeFalse(column);
            ComputedColumnRules.FormulaOf(attribute).Should().Be(formula, column);
            ComputedColumnRules.MaskOf(attribute).Should().Be(1, "{0} reads columns of its own record", column);
        }


        // An export writes the formula file back byte for byte, and every formula column names it.
        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest
        {
            SolutionName = CalculatedParityExport.SolutionName, Managed = false,
        })).ExportSolutionFile;
        var (_, customizations, written) = SolutionZip.Read(exported);
        var (_, _, original) = SolutionZip.Read(CalculatedParityExport.Zip());
        written[CalculatedParityExport.FormulaFilePath].Should().Equal(original[CalculatedParityExport.FormulaFilePath],
            "the formula file is written back byte for byte");
        foreach (var column in CalculatedParityExport.Formulas.Keys)
        {
            var element = customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == column);
            element.Element("SourceType")!.Value.Should().Be("3", column);
            element.Element("FormulaDefinitionFileName")!.Value.Should().Be("/" + CalculatedParityExport.FormulaFilePath, column);
        }
    }

    [Fact]
    public async Task Formula_values_on_an_invented_order()
    {
        await TablesOnceAsync();

        // The formula columns' values on an invented order: AB, qty 2, no due date, started 2026-11-15 23:00 UTC.
        var id = await Admin.CreateAsync(new Entity(Order)
        {
            ["xrmv_name"] = "Formula A", ["xrmv_code"] = "AB", ["xrmv_qty"] = 2,
            ["xrmv_startedon"] = new DateTime(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc),
        });
        var row = await Admin.RetrieveAsync(Order, id, new ColumnSet([.. CalculatedParityExport.Formulas.Keys]));
        row.GetAttributeValue<string>("xrmv_fxlabel").Should().Be("AB-2");
        row.GetAttributeValue<bool>("xrmv_fxnodue").Should().BeTrue();
        row.GetAttributeValue<DateTime>("xrmv_fxplus3").Should().Be(new DateTime(2026, 11, 18, 23, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Calculated_values_follow_their_definitions()
    {
        await TablesOnceAsync();
        await ValuesAsync();
    }

    [Fact]
    public async Task Parity_export_imports()
    {
        await ImportOnceAsync();

        // 27 calculated columns, read-only, with their definitions kept verbatim.
        var files = CalculatedParityExport.CalculatedFiles();
        files.Should().HaveCount(27);
        var order = Store.GetEntity(Order)!;
        foreach (var (column, (_, bytes)) in files)
        {
            var attribute = order.Attributes.Single(a => a.LogicalName == column);
            attribute.SourceType.Should().Be(1, column);
            attribute.IsValidForCreate.Should().BeFalse(column);
            ComputedColumnRules.FormulaOf(attribute).Should().Be(Encoding.UTF8.GetString(bytes), column);
        }
        order.Attributes.OfType<Microsoft.Xrm.Sdk.Metadata.MoneyAttributeMetadata>().Single(a => a.LogicalName == "xrmv_total_base")
            .IsBaseCurrency.Should().BeTrue("the _base of a calculated Currency column is its base-currency companion");
        ComputedColumnRules.MaskOf(order.Attributes.Single(a => a.LogicalName == "xrmv_label")).Should().Be(3);
        ComputedColumnRules.MaskOf(order.Attributes.Single(a => a.LogicalName == "xrmv_net2")).Should().Be(8);

        await ExportRoundTripsAsync(files);
    }

    // ---- values ------------------------------------------------------------------------------------------------------

    private static readonly DateTime Started = new(2026, 1, 31, 10, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Due = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Two invented orders: one with an account, a month-end start and quantity 3; one without account, start or due date, with
    /// quantity 0 (division by zero) and a two-character code (TRIMLEFT/TRIMRIGHT of all of it).
    /// </summary>
    private async Task ValuesAsync()
    {
        var account = await Admin.CreateAsync(new Entity("account") { ["name"] = "Northwind Example", ["xrmv_budget"] = new Money(2500m) });
        var full = await Admin.CreateAsync(new Entity(Order)
        {
            ["xrmv_name"] = "Parity A", ["xrmv_code"] = "ORD-001", ["xrmv_amount"] = 1500m, ["xrmv_qty"] = 3, ["xrmv_price"] = new Money(19.99m),
            ["xrmv_status"] = new OptionSetValue(1), ["xrmv_startedon"] = Started, ["xrmv_duedate"] = Due,
            ["xrmv_accountid"] = new EntityReference("account", account),
        });
        var empty = await Admin.CreateAsync(new Entity(Order)
        {
            ["xrmv_name"] = "Parity B", ["xrmv_code"] = "AB", ["xrmv_amount"] = 200m, ["xrmv_qty"] = 0, ["xrmv_price"] = new Money(10m),
            ["xrmv_status"] = new OptionSetValue(2),
        });

        var before = DateTime.UtcNow;
        var a = await Admin.RetrieveAsync(Order, full, new ColumnSet(true));
        var b = await Admin.RetrieveAsync(Order, empty, new ColumnSet(true));
        var after = DateTime.UtcNow;

        // total = price × qty (Currency), its _base at the organization's rate 1; accountbudget = the account's budget.
        a.GetAttributeValue<Money>("xrmv_total").Value.Should().Be(59.97m);
        a.GetAttributeValue<Money>("xrmv_total_base").Value.Should().Be(59.97m);
        a.GetAttributeValue<Money>("xrmv_accountbudget").Value.Should().Be(2500m);
        // fxtotal = amount × qty; net = amount − amount / 10; net2 = net × 2; unitprice = amount / qty.
        a.GetAttributeValue<decimal>("xrmv_fxtotal").Should().Be(4500m);
        a.GetAttributeValue<decimal>("xrmv_net").Should().Be(1350m);
        a.GetAttributeValue<decimal>("xrmv_net2").Should().Be(2700m);
        a.GetAttributeValue<decimal>("xrmv_unitprice").Should().Be(500m);
        // label = CONCAT(code, "-", account name); short = TRIMLEFT(code, 2); tail = TRIMRIGHT(code, 2).
        a.GetAttributeValue<string>("xrmv_label").Should().Be("ORD-001-Northwind Example");
        a.GetAttributeValue<string>("xrmv_short").Should().Be("D-001");
        a.GetAttributeValue<string>("xrmv_tail").Should().Be("ORD-0");
        // isbig: amount > 1000 and qty ≥ 2; tier: amount > 500 or status Won → High (128830001).
        a.GetAttributeValue<bool>("xrmv_isbig").Should().BeTrue();
        a.GetAttributeValue<OptionSetValue>("xrmv_tier").Value.Should().Be(128830001);
        // followup: status Open → ADDDAYS(7, startedon); the date shifts of startedon (31 January, a month end).
        a.GetAttributeValue<DateTime>("xrmv_followup").Should().Be(new DateTime(2026, 2, 7, 10, 0, 0, DateTimeKind.Utc));
        a.GetAttributeValue<DateTime>("xrmv_plushours").Should().Be(new DateTime(2026, 1, 31, 15, 0, 0, DateTimeKind.Utc));
        a.GetAttributeValue<DateTime>("xrmv_plusweeks").Should().Be(new DateTime(2026, 2, 14, 10, 0, 0, DateTimeKind.Utc));
        a.GetAttributeValue<DateTime>("xrmv_plusyears").Should().Be(new DateTime(2027, 1, 31, 10, 0, 0, DateTimeKind.Utc));
        a.GetAttributeValue<DateTime>("xrmv_minusdays").Should().Be(new DateTime(2026, 1, 28, 10, 0, 0, DateTimeKind.Utc));
        a.GetAttributeValue<DateTime>("xrmv_minushours").Should().Be(new DateTime(2026, 1, 31, 5, 0, 0, DateTimeKind.Utc));
        a.GetAttributeValue<DateTime>("xrmv_minusmonths").Should().Be(new DateTime(2025, 12, 31, 10, 0, 0, DateTimeKind.Utc));
        a.GetAttributeValue<DateTime>("xrmv_minusweeks").Should().Be(new DateTime(2026, 1, 17, 10, 0, 0, DateTimeKind.Utc));
        a.GetAttributeValue<DateTime>("xrmv_minusyears").Should().Be(new DateTime(2025, 1, 31, 10, 0, 0, DateTimeKind.Utc));
        // dueplus = ADDDAYS(10, duedate), Date only.
        a.GetAttributeValue<DateTime>("xrmv_dueplus").Date.Should().Be(new DateTime(2026, 2, 20));
        // age = DIFFINDAYS(createdon, now): created just now. The DIFFIN… of startedon count boundaries up to now (K7).
        a.GetAttributeValue<int>("xrmv_age").Should().Be(0);
        a.GetAttributeValue<int>("xrmv_years").Should().BeInRange(Boundaries(Started, before, Years), Boundaries(Started, after, Years));
        a.GetAttributeValue<int>("xrmv_months").Should().BeInRange(Boundaries(Started, before, Months), Boundaries(Started, after, Months));
        a.GetAttributeValue<int>("xrmv_weeks").Should().BeInRange(Boundaries(Started, before, Weeks), Boundaries(Started, after, Weeks));
        a.GetAttributeValue<int>("xrmv_hours").Should().BeInRange(Boundaries(Started, before, Hours), Boundaries(Started, after, Hours));
        a.GetAttributeValue<int>("xrmv_minutes").Should().BeInRange(Boundaries(Started, before, Minutes), Boundaries(Started, after, Minutes));

        // The second order: qty 0 makes unitprice null (division by zero, K7); no account: label ends with "-" (CONCAT reads the
        // missing name as empty) and accountbudget is null; no start date: every date column is null; code "AB": TRIMLEFT and
        // TRIMRIGHT of 2 leave it empty; status Won: followup reads ADDMONTHS(1, null), null, and tier is High.
        b.GetAttributeValue<Money>("xrmv_total").Value.Should().Be(0m);
        b.Contains("xrmv_unitprice").Should().BeFalse("amount / 0 is null");
        b.GetAttributeValue<decimal>("xrmv_fxtotal").Should().Be(0m);
        b.GetAttributeValue<decimal>("xrmv_net").Should().Be(180m);
        b.GetAttributeValue<decimal>("xrmv_net2").Should().Be(360m);
        b.GetAttributeValue<string>("xrmv_label").Should().Be("AB-");
        b.Contains("xrmv_accountbudget").Should().BeFalse();
        (b.GetAttributeValue<string>("xrmv_short") ?? "").Should().BeEmpty();
        (b.GetAttributeValue<string>("xrmv_tail") ?? "").Should().BeEmpty();
        b.GetAttributeValue<bool>("xrmv_isbig").Should().BeFalse();
        b.GetAttributeValue<OptionSetValue>("xrmv_tier").Value.Should().Be(128830001);
        foreach (var column in new[] { "xrmv_followup", "xrmv_plushours", "xrmv_plusweeks", "xrmv_plusyears", "xrmv_minusdays",
                     "xrmv_minushours", "xrmv_minusmonths", "xrmv_minusweeks", "xrmv_minusyears", "xrmv_dueplus", "xrmv_years",
                     "xrmv_months", "xrmv_weeks", "xrmv_hours", "xrmv_minutes" })
            b.Contains(column).Should().BeFalse("{0} reads an empty date", column);
        b.GetAttributeValue<int>("xrmv_age").Should().Be(0);

        // A query filtered and sorted on calculated columns.
        var fetch = $"""
            <fetch><entity name="{Order}"><attribute name="xrmv_name" /><attribute name="xrmv_total" /><order attribute="xrmv_net" descending="true" />
              <filter><condition attribute="xrmv_name" operator="like" value="Parity %" /><condition attribute="xrmv_total" operator="ge" value="0" /></filter>
            </entity></fetch>
            """;
        (await Admin.RetrieveMultipleAsync(new FetchExpression(fetch))).Entities.Select(e => e.GetAttributeValue<string>("xrmv_name"))
            .Should().Equal("Parity A", "Parity B");
    }

    private static int Years(DateTime d) => d.Year;

    private static int Months(DateTime d) => d.Year * 12 + d.Month;

    /// <summary>Sunday-to-Saturday weeks, as SQL Server counts week boundaries.</summary>
    private static int Weeks(DateTime d) => (int)((d.Date.AddDays(-(int)d.DayOfWeek) - DateTime.MinValue).TotalDays / 7);

    private static int Hours(DateTime d) => (int)((d - DateTime.MinValue).Ticks / TimeSpan.TicksPerHour);

    private static int Minutes(DateTime d) => (int)((d - DateTime.MinValue).Ticks / TimeSpan.TicksPerMinute);

    private static int Days(DateTime d) => (int)((d.Date - DateTime.MinValue).TotalDays);

    /// <summary>The number of <paramref name="unit"/> boundaries between two times.</summary>
    private static int Boundaries(DateTime start, DateTime end, Func<DateTime, int> unit) => unit(end) - unit(start);

    // ---- values Dataverse recorded -----------------------------------------------------------------------------------

    /// <summary>
    /// When the recorded values were read on Dataverse (tests/fixtures/calculated/real/README.md): the columns that read NOW were
    /// computed at this time. Every recorded date is UTC.
    /// </summary>
    private static readonly DateTime RecordedRead = new(2026, 11, 16, 15, 45, 0, DateTimeKind.Utc);


    /// <summary>
    /// Plan 2.12a, Task 6 part B: the three accounts and four orders recorded on Dataverse after the parity export
    /// (tests/fixtures/calculated/real/*.csv) are created again with the same source values, and every calculated column reads the
    /// value Dataverse recorded. The columns that read NOW are checked on copies of the orders whose start is moved by the time
    /// since the recording (whole hours for <c>xrmv_hours</c>, whole minutes for <c>xrmv_minutes</c>), so the same boundaries are
    /// crossed whatever the wall clock; weeks, months and years depend on the calendar, so their count is the one the boundary
    /// rule gives now, and the same rule gives the recorded count at the recording time.
    /// </summary>
    [Fact]
    public async Task Parity_values_match_recorded_dataverse_values()
    {
        await TablesOnceAsync();
        var accounts = CalculatedParityExport.Recorded("accounts.csv");
        var orders = CalculatedParityExport.Recorded("xrmv_calcorders.csv");
        accounts.Should().HaveCount(3);
        orders.Should().HaveCount(4);

        // Parents first: an account names its parent.
        var accountIds = new Dictionary<string, Guid>();
        foreach (var account in accounts.OrderBy(a => a["parentaccount"].Length))
        {
            var entity = new Entity("account") { ["name"] = account["name"] };
            if (account["xrmv_budget"].Length > 0) entity["xrmv_budget"] = new Money(Dec(account["xrmv_budget"]));
            if (account["parentaccount"].Length > 0) entity["parentaccountid"] = new EntityReference("account", accountIds[account["parentaccount"]]);
            accountIds[account["name"]] = await Admin.CreateAsync(entity);
        }

        // Every calculated column of the table is in the recording, with the _base of the calculated Currency columns.
        var calculated = Store.GetEntity(Order)!.Attributes.Where(a => a.SourceType == 1).Select(a => a.LogicalName).ToList();
        calculated.Should().HaveCount(27);
        orders[0].Keys.Should().Contain(calculated);
        // Plan 2.12c: the formula columns of the export too.
        var formulas = Store.GetEntity(Order)!.Attributes.Where(a => a.SourceType == ComputedColumnRules.Formula).Select(a => a.LogicalName).ToList();
        formulas.Should().BeEquivalentTo(CalculatedParityExport.Formulas.Keys);
        orders[0].Keys.Should().Contain(formulas);
        var checkedColumns = calculated.Except(NowColumns).Concat(formulas).Concat(["xrmv_total_base", "xrmv_accountbudget_base"])
            .Order(StringComparer.Ordinal).ToList();

        foreach (var recorded in orders)
        {
            var code = recorded["xrmv_code"];
            var id = await Admin.CreateAsync(NewOrder(recorded, accountIds, "recorded " + code, TimeSpan.Zero));
            var row = await Admin.RetrieveAsync(Order, id, new ColumnSet(true));
            foreach (var column in checkedColumns)
                Text(row, column).Should().Be(recorded[column], "Dataverse recorded {0}.{1} = '{2}'", code, column, recorded[column]);
        }

        await NowColumnsAsync(orders, accountIds);
    }

    /// <summary>
    /// The columns that read NOW, on copies of the recorded orders moved by the time since the recording: by whole hours (the hours
    /// cross the same hour boundaries) and by whole minutes (the minutes cross the same minute boundaries). A copy is read in the
    /// minute it is written, or written again.
    /// </summary>
    private async Task NowColumnsAsync(List<Dictionary<string, string>> orders, Dictionary<string, Guid> accountIds)
    {
        for (var attempt = 1; ; attempt++)
        {
            var before = await DatabaseNowAsync();
            var hourShift = Floor(before, TimeSpan.TicksPerHour) - Floor(RecordedRead, TimeSpan.TicksPerHour);
            var minuteShift = Floor(before, TimeSpan.TicksPerMinute) - RecordedRead;
            var rows = new List<(Dictionary<string, string> Recorded, Entity ByHours, Entity ByMinutes)>();
            foreach (var recorded in orders)
            {
                var code = recorded["xrmv_code"];
                var byHours = await Admin.CreateAsync(NewOrder(recorded, accountIds, "now/h " + code, hourShift));
                var byMinutes = await Admin.CreateAsync(NewOrder(recorded, accountIds, "now/m " + code, minuteShift));
                rows.Add((recorded, await Admin.RetrieveAsync(Order, byHours, new ColumnSet(true)),
                    await Admin.RetrieveAsync(Order, byMinutes, new ColumnSet(true))));
            }
            var after = await DatabaseNowAsync();
            if (Floor(before, TimeSpan.TicksPerMinute) != Floor(after, TimeSpan.TicksPerMinute))
            {
                attempt.Should().BeLessThan(3, "the copies are read within a minute");
                continue;
            }

            foreach (var (recorded, byHours, byMinutes) in rows)
            {
                var code = recorded["xrmv_code"];
                var started = Date(recorded["xrmv_startedon"]);
                byHours.GetAttributeValue<int>("xrmv_hours").ToString(CultureInfo.InvariantCulture)
                    .Should().Be(recorded["xrmv_hours"], "Dataverse recorded {0}.xrmv_hours", code);
                byMinutes.GetAttributeValue<int>("xrmv_minutes").ToString(CultureInfo.InvariantCulture)
                    .Should().Be(recorded["xrmv_minutes"], "Dataverse recorded {0}.xrmv_minutes", code);
                foreach (var (column, unit) in new (string, Func<DateTime, int>)[] { ("xrmv_weeks", Weeks), ("xrmv_months", Months), ("xrmv_years", Years) })
                {
                    Boundaries(started, RecordedRead, unit).ToString(CultureInfo.InvariantCulture)
                        .Should().Be(recorded[column], "the boundary rule gives the count Dataverse recorded for {0}.{1}", code, column);
                    byHours.GetAttributeValue<int>(column).Should().Be(Boundaries(started + hourShift, before, unit), "{0}.{1} now", code, column);
                }
                // age = DIFFINDAYS(createdon, NOW): the recorded orders had just been created (0), as the copies are.
                byHours.GetAttributeValue<int>("xrmv_age").Should()
                    .Be(Boundaries(byHours.GetAttributeValue<DateTime>("createdon"), before, Days), "{0}.xrmv_age", code);
                byHours.GetAttributeValue<int>("xrmv_age").ToString(CultureInfo.InvariantCulture).Should().Be(recorded["xrmv_age"], "{0}.xrmv_age", code);
            }
            return;
        }
    }

    private static DateTime Floor(DateTime d, long unitTicks) => new(d.Ticks - d.Ticks % unitTicks, DateTimeKind.Utc);

    private async Task<DateTime> DatabaseNowAsync()
    {
        var now = await TestSql.ScalarAsync(fx.ConnectionString, "SELECT " + TestSql.Dialect.UtcNow);
        return DateTime.SpecifyKind(now is DateTimeOffset o ? o.UtcDateTime : (DateTime)now!, DateTimeKind.Utc);
    }

    // ---- export round trip -------------------------------------------------------------------------------------------

    private async Task ExportRoundTripsAsync(Dictionary<string, (string Path, byte[] Bytes)> expected)
    {
        var exported = ((ExportSolutionResponse)await Admin.ExecuteAsync(new ExportSolutionRequest
        {
            SolutionName = CalculatedParityExport.SolutionName, Managed = false,
        })).ExportSolutionFile;

        var (_, customizations, files) = SolutionZip.Read(exported);
        foreach (var (column, (path, bytes)) in expected)
        {
            var element = customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == column);
            element.Element("SourceType")!.Value.Should().Be("1", column);
            element.Element("FormulaDefinitionFileName")!.Value.Should().Be("/" + path, column);
            files.Should().ContainKey(path);
            files[path].Should().Equal(bytes, "the XAML of {0} is written back byte for byte", column);
        }
        // The _base of a calculated Currency column is written as the export has it: SourceType 1, no formula file.
        var totalBase = customizations.Descendants("attribute").Single(a => a.Element("LogicalName")?.Value == "xrmv_total_base");
        totalBase.Element("SourceType")!.Value.Should().Be("1");
        totalBase.Element("FormulaDefinitionFileName").Should().BeNull();
        files.Keys.Where(k => k.StartsWith("Formulas/", StringComparison.OrdinalIgnoreCase)).Should()
            .BeEquivalentTo(expected.Values.Concat(CalculatedParityExport.RollupFiles().Values).Select(v => v.Path)
                    .Append(CalculatedParityExport.FormulaFilePath),
                "only the calculated, rollup and formula columns have formula files");
    }
}

/// <summary>
/// The SQL endpoint part of the calculated column acceptance (plan 2.12a, Task 6): the 2.12a parity export (without its rollup and
/// formula columns) imported into a database of its own (the shared one holds the provisional <c>xrmv_calcorder</c> of
/// <see cref="CalculatedTdsQueryTests"/>), read with Microsoft.Data.SqlClient: <c>SELECT … WHERE xrmv_total &gt; … ORDER BY</c>.
/// </summary>
[Collection("tds-query")]
[Trait("Category", "Acceptance")]
public sealed class CalculatedTdsAcceptanceTests(Tds.TdsQueryFixture fx)
{
    [Fact]
    public async Task Tds_calculated_acceptance()
    {
        await using var host = await Tds.TdsTestHost.StartAsync(org: new Tds.TdsTestOrg(await fx.NewDatabaseAsync(), fx.AdminObjectId));
        await host.App.Services.GetRequiredService<SdkMessageCatalog>().EnsureAsync(default);
        var service = TestServices.NewOrganizationService(host.App.Services, fx.AdminObjectId);
        await service.ExecuteAsync(new ImportSolutionRequest
        {
            CustomizationFile = CalculatedParityExport.WithoutRollupAndFormulaColumns(), ImportJobId = Guid.NewGuid(),
        });
        var account = await service.CreateAsync(new Entity("account") { ["name"] = "Tailspin Example" });
        foreach (var (code, price, qty) in new[] { ("A1", 10m, 3), ("B2", 25m, 4), ("C3", 7.5m, 2) })
            await service.CreateAsync(new Entity(CalculatedParityExport.Order)
            {
                ["xrmv_name"] = "tds " + code, ["xrmv_code"] = code, ["xrmv_price"] = new Money(price), ["xrmv_qty"] = qty,
                ["xrmv_amount"] = price * qty, ["xrmv_accountid"] = new EntityReference("account", account),
            });

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(host.SqlLoginConnectionString().ConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT xrmv_code, xrmv_total, xrmv_label, xrmv_net2 FROM xrmv_calcorder WHERE xrmv_total > 20 ORDER BY xrmv_total DESC", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(string, decimal, string, decimal)>();
        while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetDecimal(1), reader.GetString(2), reader.GetDecimal(3)));

        // total = price × qty; label = CONCAT(code, "-", account name); net2 = (amount − amount / 10) × 2.
        rows.Should().Equal(("B2", 100m, "B2-Tailspin Example", 180m), ("A1", 30m, "A1-Tailspin Example", 54m));
    }
}

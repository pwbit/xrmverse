using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Npgsql;
using Xrmd.Abstractions.Storage;
using Xrmd.Model.Metadata;
using Xrmd.Model.Records;
using Xrmd.Storage.PostgreSql;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Core.Tests;

/// <summary>
/// S.2e3b, Task 1: golden dumps of the parameters <see cref="EntityRowMapper.ToParameters"/> builds for a row, captured before the
/// CRUD handlers move onto the model. One row holds a value of every column type the mapper writes (the date in each
/// <see cref="DateTimeKind"/>, a polymorphic lookup and an EntityName column included), one row holds a null of each. Each parameter
/// is written as the mapper built it (name, <see cref="DbType"/>, size, precision, scale, value), then as the provider's own parameter
/// that <see cref="DbCommandExtensions.AddParameters"/> copies onto a command of that provider (with the dialect's adjustments). One
/// file per provider dialect in <c>tests/fixtures/row-parameters-golden</c>; a missing file is written once and the test fails, an
/// existing file is never rewritten.
/// </summary>
public sealed class EntityRowMapperGoldenTests
{
    private const string Table = "new_rowtypes";

    private static readonly DateTime When = new(2015, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc);

    private static readonly Guid AccountId = new("a0a0a0a0-0000-4000-8000-000000000001");
    private static readonly Guid ContactId = new("a0a0a0a0-0000-4000-8000-000000000002");
    private static readonly Guid FileId = new("a0a0a0a0-0000-4000-8000-000000000003");
    private static readonly Guid UniqueId = new("a0a0a0a0-0000-4000-8000-000000000004");
    private static readonly Guid RowId = new("a0a0a0a0-0000-4000-8000-000000000005");

    /// <summary>The columns of the row, in the order the row holds them (the order the parameters come in).</summary>
    private static readonly AttributeMetadata[] Columns =
    [
        new UniqueIdentifierAttributeMetadata { LogicalName = "new_rowtypesid" },
        new StringAttributeMetadata { LogicalName = "new_name", MaxLength = 100 },
        new MemoAttributeMetadata { LogicalName = "new_memo", MaxLength = 2000 },
        new IntegerAttributeMetadata { LogicalName = "new_int" },
        new BigIntAttributeMetadata { LogicalName = "new_big" },
        new DecimalAttributeMetadata { LogicalName = "new_decimal", Precision = 4 },
        new DoubleAttributeMetadata { LogicalName = "new_double", Precision = 3 },
        new MoneyAttributeMetadata { LogicalName = "new_amount", Precision = 2 },
        new BooleanAttributeMetadata { LogicalName = "new_flag" },
        new DateTimeAttributeMetadata { LogicalName = "new_whenutc" },
        new DateTimeAttributeMetadata { LogicalName = "new_whenlocal" },
        new DateTimeAttributeMetadata { LogicalName = "new_whenunspecified" },
        new PicklistAttributeMetadata { LogicalName = "new_choice" },
        new StateAttributeMetadata { LogicalName = "statecode" },
        new StatusAttributeMetadata { LogicalName = "statuscode" },
        new MultiSelectPicklistAttributeMetadata { LogicalName = "new_choices" },
        new LookupAttributeMetadata { LogicalName = "new_accountid", Targets = ["account"] },
        new LookupAttributeMetadata { LogicalName = "new_customerid", Targets = ["account", "contact"] },
        new EntityNameAttributeMetadata { LogicalName = "new_tablename" },
        new FileAttributeMetadata { LogicalName = "new_doc" },
        new ImageAttributeMetadata { LogicalName = "new_pic" },
    ];

    private static readonly IReadOnlyDictionary<string, AttributeMetadata> Attrs = Columns.ToDictionary(a => a.LogicalName, StringComparer.Ordinal);

    /// <summary>A value of every column type.</summary>
    private static Entity Values() => new(Table)
    {
        ["new_rowtypesid"] = RowId,
        ["new_name"] = "row golden",
        ["new_memo"] = "line one\nline two",
        ["new_int"] = 42,
        ["new_big"] = 9_000_000_000L,
        ["new_decimal"] = 12.3456m,
        ["new_double"] = 0.125d,
        ["new_amount"] = new Money(1234.5m),
        ["new_flag"] = true,
        ["new_whenutc"] = When,
        // A fixed offset of the local zone would make the file depend on the machine: the local value is the UTC one converted.
        ["new_whenlocal"] = When.ToLocalTime(),
        ["new_whenunspecified"] = DateTime.SpecifyKind(When, DateTimeKind.Unspecified),
        ["new_choice"] = new OptionSetValue(100000001),
        ["statecode"] = new OptionSetValue(1),
        ["statuscode"] = new OptionSetValue(2),
        ["new_choices"] = new OptionSetValueCollection([new OptionSetValue(3), new OptionSetValue(1)]),
        ["new_accountid"] = new EntityReference("account", AccountId),
        ["new_customerid"] = new EntityReference("contact", ContactId),
        ["new_tablename"] = "contact",
        ["new_doc"] = FileId,
        ["new_pic"] = Enumerable.Range(0, 80).Select(i => (byte)i).ToArray(),
    };

    /// <summary>A null in every column (the EntityName column excepted: a null names no table and is not written by its case).</summary>
    private static Entity Nulls()
    {
        var e = new Entity(Table);
        foreach (var a in Columns) e[a.LogicalName] = null;
        e["new_rowtypesid"] = UniqueId;
        e.Attributes.Remove("new_tablename");
        return e;
    }

    /// <summary>S.2e3b Task 4: the columns of the row as model definitions, the same as <see cref="Columns"/>.</summary>
    private static readonly ColumnDefinition[] ModelColumns =
    [
        new UniqueIdentifierColumn { LogicalName = "new_rowtypesid" },
        new TextColumn { LogicalName = "new_name", MaxLength = 100 },
        new MemoColumn { LogicalName = "new_memo", MaxLength = 2000 },
        new IntegerColumn { LogicalName = "new_int" },
        new BigIntColumn { LogicalName = "new_big" },
        new DecimalColumn { LogicalName = "new_decimal", Precision = 4 },
        new DoubleColumn { LogicalName = "new_double", Precision = 3 },
        new MoneyColumn { LogicalName = "new_amount", Precision = 2 },
        new BooleanColumn { LogicalName = "new_flag" },
        new DateTimeColumn { LogicalName = "new_whenutc" },
        new DateTimeColumn { LogicalName = "new_whenlocal" },
        new DateTimeColumn { LogicalName = "new_whenunspecified" },
        new ChoiceColumn { LogicalName = "new_choice" },
        new StateColumn { LogicalName = "statecode" },
        new StatusColumn { LogicalName = "statuscode" },
        new MultiChoiceColumn { LogicalName = "new_choices" },
        new LookupColumn { LogicalName = "new_accountid", Targets = ["account"] },
        new LookupColumn { LogicalName = "new_customerid", Targets = ["account", "contact"] },
        new EntityNameColumn { LogicalName = "new_tablename" },
        new FileColumn { LogicalName = "new_doc" },
        new ImageColumn { LogicalName = "new_pic" },
    ];

    private static readonly IReadOnlyDictionary<string, ColumnDefinition> ModelAttrs =
        ModelColumns.ToDictionary(a => a.LogicalName!, StringComparer.Ordinal);

    /// <summary>The model form of <see cref="Values"/>, built with the model value types.</summary>
    private static Record ModelValues() => new(Table)
    {
        ["new_rowtypesid"] = RowId,
        ["new_name"] = "row golden",
        ["new_memo"] = "line one\nline two",
        ["new_int"] = 42,
        ["new_big"] = 9_000_000_000L,
        ["new_decimal"] = 12.3456m,
        ["new_double"] = 0.125d,
        ["new_amount"] = new MoneyValue(1234.5m),
        ["new_flag"] = true,
        ["new_whenutc"] = When,
        ["new_whenlocal"] = When.ToLocalTime(),
        ["new_whenunspecified"] = DateTime.SpecifyKind(When, DateTimeKind.Unspecified),
        ["new_choice"] = new ChoiceValue(100000001),
        ["statecode"] = new ChoiceValue(1),
        ["statuscode"] = new ChoiceValue(2),
        ["new_choices"] = new ChoiceValues { new(3), new(1) },
        ["new_accountid"] = new RecordReference { LogicalName = "account", Id = AccountId },
        ["new_customerid"] = new RecordReference { LogicalName = "contact", Id = ContactId },
        ["new_tablename"] = "contact",
        ["new_doc"] = FileId,
        ["new_pic"] = Enumerable.Range(0, 80).Select(i => (byte)i).ToArray(),
    };

    /// <summary>The model form of <see cref="Nulls"/>.</summary>
    private static Record ModelNulls()
    {
        var e = new Record(Table);
        foreach (var a in ModelColumns) e[a.LogicalName!] = null;
        e["new_rowtypesid"] = UniqueId;
        e.Attributes.Remove("new_tablename");
        return e;
    }

    private static TableDefinition? TableOf(string name)
    {
        var code = name switch { "account" => 1, "contact" => 2, _ => (int?)null };
        return code is null ? null : new TableDefinition { LogicalName = name, ObjectTypeCode = code };
    }

    private static EntityMetadata? EntityOf(string name)
    {
        var code = name switch { "account" => 1, "contact" => 2, _ => (int?)null };
        if (code is null) return null;
        var e = new EntityMetadata { LogicalName = name };
        typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.ObjectTypeCode))!.GetSetMethod(true)!.Invoke(e, [code]);
        return e;
    }

    [Fact]
    public void Sql_server_parameters_are_what_the_golden_file_says()
    {
        using var connection = new SqlConnection();
        AssertGolden("sqlserver.txt", Dump(() => connection.CreateCommand()));
    }

    [Fact]
    public void Postgresql_parameters_are_what_the_golden_file_says()
    {
        PostgresStorage.Register();
        using var connection = new NpgsqlConnection();
        AssertGolden("postgresql.txt", Dump(() => connection.CreateCommand()));
    }

    /// <summary>
    /// S.2e3b Task 4: <see cref="EntityRowMapper.ToParameters(Record, IReadOnlyDictionary{string, ColumnDefinition}, Func{string, TableDefinition?})"/>
    /// on the model form of the same rows dumps byte-identical to the same golden files, for both providers.
    /// </summary>
    [Fact]
    public void Model_parameters_equal_the_golden()
    {
        using (var connection = new SqlConnection())
            AssertGolden("sqlserver.txt", Dump(() => connection.CreateCommand(), model: true));
        PostgresStorage.Register();
        using (var connection = new NpgsqlConnection())
            AssertGolden("postgresql.txt", Dump(() => connection.CreateCommand(), model: true));
    }

    private static string Dump(Func<DbCommand> command, bool model = false)
    {
        var text = new StringBuilder();
        foreach (var (label, row, modelRow) in new[] { ("values", Values(), ModelValues()), ("nulls", Nulls(), ModelNulls()) })
        {
            var parameters = model
                ? EntityRowMapper.ToParameters(modelRow, ModelAttrs, TableOf)
                : EntityRowMapper.ToParameters(row, Attrs, EntityOf);
            text.Append("=== ").Append(label).Append(" ===\n--- EntityRowMapper.ToParameters ---\n");
            foreach (var p in parameters) text.Append(Line(p)).Append('\n');
            using var cmd = command();
            cmd.AddParameters(parameters);
            text.Append("--- ").Append(cmd.GetType().Name).Append(".Parameters ---\n");
            foreach (DbParameter p in cmd.Parameters)
            {
                text.Append(Line(p));
                switch (p)
                {
                    case SqlParameter sql:
                        text.Append(" SqlDbType=").Append(sql.SqlDbType);
                        break;
                    case NpgsqlParameter npgsql:
                        text.Append(" NpgsqlDbType=").Append(npgsql.NpgsqlDbType).Append(" DataTypeName=").Append(npgsql.DataTypeName ?? "null");
                        break;
                }
                text.Append('\n');
            }
        }
        return text.ToString();
    }

    private static string Line(DbParameter p) => string.Create(CultureInfo.InvariantCulture,
        $"{p.ParameterName} DbType={p.DbType} Size={p.Size} Precision={p.Precision} Scale={p.Scale} Direction={p.Direction} Value={Value(p.Value)}");

    private static string Value(object? v) => v switch
    {
        null => "null",
        DBNull => "DBNull",
        string s => "String:\"" + s.Replace("\\", "\\\\").Replace("\n", "\\n") + "\"",
        DateTime d => string.Create(CultureInfo.InvariantCulture, $"DateTime:{d:O} {d.Kind}"),
        byte[] b => b.Length <= 64 ? "Byte[]:" + Convert.ToHexString(b) : string.Create(CultureInfo.InvariantCulture, $"Byte[]:[{b.Length}] sha256:{Convert.ToHexString(SHA256.HashData(b))}"),
        Guid g => "Guid:" + g.ToString("D"),
        IFormattable f => v.GetType().Name + ":" + f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.GetType().Name + ":" + v,
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        dir.Should().NotBeNull("the repository root holds xrmverse.sln");
        return dir!.FullName;
    }

    /// <summary>Compares <paramref name="text"/> with <c>tests/fixtures/row-parameters-golden/{name}</c>, written once when missing.</summary>
    private static void AssertGolden(string name, string text)
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "fixtures", "row-parameters-golden", name);
        var actual = Encoding.UTF8.GetBytes(text);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(actual);
            Assert.Fail($"golden file {name} written, review and commit");
        }

        Encoding.UTF8.GetString(File.ReadAllBytes(path)).Should().Be(text);
        File.ReadAllBytes(path).Should().Equal(actual);
    }
}

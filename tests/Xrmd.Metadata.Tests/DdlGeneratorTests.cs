using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using VerifyXunit;
using Xrmd.Abstractions.Storage;
using Xrmd.Abstractions.Storage.SqlServer;
using Xrmd.Metadata;

namespace Xrmd.Metadata.Tests;

public class DdlGeneratorTests
{
    private static readonly ISqlDialect D = SqlServerDialect.Instance;

    private static EntityMetadata Project() => new()
    {
        LogicalName = "pb_project",
        SchemaName = "pb_Project",
        OwnershipType = OwnershipTypes.UserOwned,
    };

    private static AttributeMetadata[] ProjectAttributes() =>
    [
        SystemAttributes.PrimaryId("pb_project"),
        new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 200 },
        new MoneyAttributeMetadata { LogicalName = "pb_budget" },
        SystemAttributes.Owner(),
    ];

    [Fact]
    public Task CreateTable_pb_project() =>
        Verifier.Verify(DdlGenerator.CreateTable(Project(), ProjectAttributes(), D));

    [Fact]
    public void CreateTable_does_not_duplicate_caller_supplied_system_columns()
    {
        var sql = DdlGenerator.CreateTable(Project(), ProjectAttributes(), D);

        foreach (var col in new[] { "[ownerid]", "[owneridtype]", "[createdon]", "[versionnumber]", "[pb_budget_base]" })
            sql.Split(col).Length.Should().Be(2, $"{col} must appear exactly once");
    }

    [Fact]
    public void CreateTable_skips_base_attribute_metadata_already_expanded_from_money()
    {
        var attrs = SystemAttributes.Complete(Project(), ProjectAttributes());

        attrs.Select(a => a.LogicalName).Should().Contain("pb_budget_base");
        DdlGenerator.CreateTable(Project(), attrs, D).Should().Be(DdlGenerator.CreateTable(Project(), ProjectAttributes(), D));
    }

    [Fact]
    public void AddColumn_money_adds_value_and_base_columns() =>
        DdlGenerator.AddColumn("pb_project", new MoneyAttributeMetadata { LogicalName = "pb_cost" }, D)
            .Should().Be("ALTER TABLE [data].[pb_project] ADD [pb_cost] decimal(19,4) NULL, [pb_cost_base] decimal(19,4) NULL;");

    [Fact]
    public void AddColumn_string() =>
        DdlGenerator.AddColumn("pb_project", new StringAttributeMetadata { LogicalName = "pb_code", MaxLength = 20 }, D)
            .Should().Be("ALTER TABLE [data].[pb_project] ADD [pb_code] nvarchar(20) NULL;");

    [Fact]
    public void DropColumn_drops_the_base_and_type_companions() =>
        (DdlGenerator.DropColumn("pb_project", new MoneyAttributeMetadata { LogicalName = "pb_cost" }, D),
            DdlGenerator.DropColumn("pb_project", SystemAttributes.Owner(), D))
        .Should().Be(("ALTER TABLE [data].[pb_project] DROP COLUMN [pb_cost], [pb_cost_base];",
            "ALTER TABLE [data].[pb_project] DROP COLUMN [ownerid], [owneridtype];"));

    [Fact]
    public void AlterColumn_and_DropTable() =>
        (DdlGenerator.AlterColumn("pb_project", new StringAttributeMetadata { LogicalName = "pb_code", MaxLength = 300 }, D),
            DdlGenerator.DropTable("pb_project", D))
        .Should().Be(("ALTER TABLE [data].[pb_project] ALTER COLUMN [pb_code] nvarchar(300) NULL;", "DROP TABLE [data].[pb_project];"));

    /// <summary>Plan 2.7a, Task 4: the DDL helpers moved to the dialect write the SQL Server text they wrote before.</summary>
    [Fact]
    public void The_dialect_ddl_keeps_the_sql_server_text()
    {
        var a = new StringAttributeMetadata { LogicalName = "pb_number", MetadataId = Guid.Parse("11111111-2222-3333-4444-555555555555") };
        const string seq = "[autonumber].[11111111-2222-3333-4444-555555555555]";
        AutoNumberSequences.EnsureSql(a, D).Should().Be(
            "IF SCHEMA_ID(N'autonumber') IS NULL EXEC(N'CREATE SCHEMA [autonumber]');\n" +
            $"IF OBJECT_ID(N'{seq}', N'SO') IS NULL EXEC(N'CREATE SEQUENCE {seq} AS bigint START WITH 1000 INCREMENT BY 1 MINVALUE 0 NO CACHE');");
        AutoNumberSequences.ResetSql(a, 42, D).Should().Be(
            $"IF OBJECT_ID(N'{seq}', N'SO') IS NOT NULL DROP SEQUENCE {seq};\nCREATE SEQUENCE {seq} AS bigint START WITH 42 INCREMENT BY 1 MINVALUE 0 NO CACHE;");
        AutoNumberSequences.ReadSql(a, D).Should().EndWith($"FROM sys.sequences WHERE object_id = OBJECT_ID(N'{seq}', N'SO');");
        ChangeTrackingSchema.EnsureIndexSql("account", D).Should().Be(
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_account_versionnumber' AND object_id = OBJECT_ID(N'[data].[account]'))\n" +
            "    CREATE INDEX [IX_account_versionnumber] ON [data].[account] ([versionnumber]);");
        D.AddColumnIfMissing("data", "account", "parentidtype", "int").Should().Be(
            "IF COL_LENGTH(N'[data].[account]', N'parentidtype') IS NULL ALTER TABLE [data].[account] ADD [parentidtype] int NULL;");
        D.RenameColumn("data", "account", "oldid", "accountid").Should().Be("EXEC sp_rename N'data.account.oldid', N'accountid', N'COLUMN';");
        D.When(D.ColumnExists("data", "team", "teamtemplateid"), "CREATE INDEX [i] ON [data].[team] ([c]) WHERE [s] <> 'x'").Should().Be(
            "IF COL_LENGTH(N'[data].[team]', N'teamtemplateid') IS NOT NULL EXEC(N'CREATE INDEX [i] ON [data].[team] ([c]) WHERE [s] <> ''x''');");
        D.RowVersionTrigger("data", "account").Should().BeEmpty();
    }

    [Fact]
    public void Invalid_names_are_rejected()
    {
        FluentActions.Invoking(() => DdlGenerator.DropTable("pb_x]; DROP TABLE y", D))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => DdlGenerator.DropColumn("pb_project", new StringAttributeMetadata { LogicalName = "Bad]Name" }, D))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => DdlGenerator.AddColumn("pb_project", new StringAttributeMetadata { LogicalName = "Bad]Name" }, D))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => DdlGenerator.CreateTable(new EntityMetadata { LogicalName = "PB_X" }, [], D))
            .Should().Throw<ArgumentException>();
    }
}

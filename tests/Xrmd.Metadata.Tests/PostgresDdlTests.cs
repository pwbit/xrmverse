using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Abstractions.Storage.SqlServer;
using Xrmd.Metadata;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Metadata.Tests;

/// <summary>
/// Plan 2.7a, Task 4: the DDL of the <c>data</c> schema on PostgreSQL (rulings P4 and P5) and the catalog checks of the
/// existing DDL helpers in the PostgreSQL dialect. The SQL Server text is pinned by <see cref="DdlGeneratorTests"/>.
/// </summary>
public class PostgresDdlTests
{
    private static readonly ISqlDialect D = PostgresDialect.Instance;

    private static EntityMetadata Account() => new()
    {
        LogicalName = "account",
        SchemaName = "Account",
        OwnershipType = OwnershipTypes.UserOwned,
    };

    private static AttributeMetadata[] AccountAttributes() =>
    [
        SystemAttributes.PrimaryId("account"),
        new StringAttributeMetadata { LogicalName = "name", MaxLength = 160 },
        new MoneyAttributeMetadata { LogicalName = "revenue" },
        new BooleanAttributeMetadata { LogicalName = "donotemail" },
        SystemAttributes.Owner(),
    ];

    [Fact]
    public void CreateTable_writes_postgresql_types_and_the_versionnumber_trigger()
    {
        var sql = DdlGenerator.CreateTable(Account(), AccountAttributes(), D);

        sql.Should().StartWith("CREATE TABLE \"data\".\"account\" (")
            .And.Contain("\"accountid\" uuid NOT NULL CONSTRAINT \"PK_account\" PRIMARY KEY")
            .And.Contain("\"name\" citext NULL")
            .And.Contain("\"revenue\" numeric(19,4) NULL")
            .And.Contain("\"donotemail\" boolean NULL")
            .And.Contain("\"createdon\" timestamptz NULL")
            .And.Contain("\"versionnumber\" bigint NOT NULL")
            .And.Contain("CREATE TRIGGER \"trg_account_versionnumber\" BEFORE INSERT OR UPDATE ON \"data\".\"account\" " +
                         "FOR EACH ROW EXECUTE FUNCTION \"data\".\"set_versionnumber\"();");
        sql.Should().NotContain("[").And.NotContain("rowversion").And.NotContain("nvarchar");
        sql.Split("\"versionnumber\"").Length.Should().Be(2, "the column is defined once; the trigger names it in its function");
    }

    [Fact]
    public void CreateIntersectTable_keeps_the_pair_index_name()
    {
        var e = new EntityMetadata { LogicalName = "new_a_b", SchemaName = "new_a_b", OwnershipType = OwnershipTypes.None };
        var sql = DdlGenerator.CreateIntersectTable(e,
            [SystemAttributes.PrimaryId("new_a_b"), new UniqueIdentifierAttributeMetadata { LogicalName = "aid" }, new UniqueIdentifierAttributeMetadata { LogicalName = "bid" }],
            "aid", "bid", D);

        sql.Should().Contain("CREATE UNIQUE INDEX \"UQ_new_a_b_pair\" ON \"data\".\"new_a_b\" (\"aid\", \"bid\");");
    }

    [Fact]
    public void A_long_object_name_is_cut_to_63_bytes_with_a_hash_and_is_stable()
    {
        var name = "IX_" + new string('a', 60) + "_versionnumber"; // 77 characters
        var cut = D.ObjectName(name);

        Encoding.UTF8.GetByteCount(cut).Should().Be(63);
        cut.Should().Be(name[..54] + "_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8]);
        D.ObjectName(name).Should().Be(cut, "the same name always gives the same identifier");
        D.ObjectName(name + "x").Should().NotBe(cut, "two names with the same first 54 bytes stay apart");

        var seventy = new string('i', 70);
        Encoding.UTF8.GetByteCount(D.ObjectName(seventy)).Should().Be(63);
        D.ObjectName(new string('k', 63)).Should().Be(new string('k', 63), "a name of 63 bytes fits");
        SqlServerDialect.Instance.ObjectName(seventy).Should().Be(seventy, "SQL Server takes 128 characters");
    }

    [Fact]
    public void The_constraint_and_trigger_names_of_a_long_table_are_cut()
    {
        var table = "a" + new string('b', 60); // 61 characters: its primary key column (62) is valid, PK_<table> has 64
        D.ObjectName("PK_" + table).Should().NotBe("PK_" + table);
        var e = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.OrganizationOwned };
        var sql = DdlGenerator.CreateTable(e, [SystemAttributes.PrimaryId(table)], D);

        sql.Should().Contain($"CONSTRAINT \"{D.ObjectName("PK_" + table)}\" PRIMARY KEY")
            .And.Contain($"CREATE TRIGGER \"{D.ObjectName("trg_" + table + "_versionnumber")}\"");
    }

    [Fact]
    public void Table_and_column_names_postgresql_would_cut_are_refused()
    {
        var table = "a" + new string('b', 63); // 64 characters: a valid logical name, one byte over PostgreSQL's identifiers
        var e = new EntityMetadata { LogicalName = table, SchemaName = table, OwnershipType = OwnershipTypes.OrganizationOwned };
        FluentActions.Invoking(() => DdlGenerator.CreateTable(e, [SystemAttributes.PrimaryId("x")], D))
            .Should().Throw<XrmdValidationException>()
            .WithMessage($"The table name '{table}' is longer than the 63 bytes PostgreSQL allows for a table or column name.");

        var money = new MoneyAttributeMetadata { LogicalName = "m" + new string('n', 59) }; // 60: its _base column has 65
        FluentActions.Invoking(() => DdlGenerator.AddColumn("account", money, D))
            .Should().Throw<XrmdValidationException>().WithMessage($"The column name '{money.LogicalName}_base' is longer than the 63 bytes*");
        var account = new EntityMetadata { LogicalName = "account", SchemaName = "Account", OwnershipType = OwnershipTypes.UserOwned };
        FluentActions.Invoking(() => DdlGenerator.CreateTable(account, [SystemAttributes.PrimaryId("account"), money], D))
            .Should().Throw<XrmdValidationException>().WithMessage("*_base' is longer than the 63 bytes*");

        var lookup = new LookupAttributeMetadata { LogicalName = "l" + new string('k', 59), Targets = ["account", "contact"] }; // 60 + "type"
        FluentActions.Invoking(() => DdlGenerator.AddColumn("account", lookup, D))
            .Should().Throw<XrmdValidationException>().WithMessage($"The column name '{lookup.LogicalName}type' is longer than the 63 bytes*");
        var text = new StringAttributeMetadata { LogicalName = "s" + new string('t', 63) };
        FluentActions.Invoking(() => DdlGenerator.AddColumn("account", text, D))
            .Should().Throw<XrmdValidationException>().WithMessage($"The column name '{text.LogicalName}' is longer than the 63 bytes*");

        // SQL Server (128 characters) keeps taking them.
        FluentActions.Invoking(() => SqlServerDialect.Instance.EnsureIdentifier(table, "table")).Should().NotThrow();
        DdlGenerator.AddColumn("account", money, SqlServerDialect.Instance).Should().Contain($"[{money.LogicalName}_base]");
        DdlGenerator.AddColumn("account", lookup, SqlServerDialect.Instance).Should().Contain($"[{lookup.LogicalName}type]");
    }

    [Fact]
    public void Column_changes_use_one_clause_per_column()
    {
        DdlGenerator.AddColumn("account", new MoneyAttributeMetadata { LogicalName = "pb_cost" }, D)
            .Should().Be("ALTER TABLE \"data\".\"account\" ADD COLUMN \"pb_cost\" numeric(19,4) NULL, ADD COLUMN \"pb_cost_base\" numeric(19,4) NULL;");
        DdlGenerator.DropColumn("account", SystemAttributes.Owner(), D)
            .Should().Be("ALTER TABLE \"data\".\"account\" DROP COLUMN \"ownerid\", DROP COLUMN \"owneridtype\";");
        DdlGenerator.AlterColumn("account", new StringAttributeMetadata { LogicalName = "pb_code", MaxLength = 300 }, D)
            .Should().Be("ALTER TABLE \"data\".\"account\" ALTER COLUMN \"pb_code\" TYPE citext;");
        DdlGenerator.DropTable("account", D).Should().Be("DROP TABLE \"data\".\"account\";");
    }

    [Fact]
    public void The_versionnumber_index_of_change_tracking()
    {
        ChangeTrackingSchema.EnsureIndexSql("account", D)
            .Should().Be("CREATE INDEX IF NOT EXISTS \"IX_account_versionnumber\" ON \"data\".\"account\" (\"versionnumber\");");
        ChangeTrackingSchema.EnsureIndexSql("account", SqlServerDialect.Instance).Should().Contain(
            "CREATE INDEX [IX_account_versionnumber] ON [data].[account] ([versionnumber])");
    }

    [Fact]
    public void Autonumber_sequences()
    {
        var a = new StringAttributeMetadata { LogicalName = "pb_number", MetadataId = Guid.Parse("11111111-2222-3333-4444-555555555555") };
        const string seq = "\"autonumber\".\"11111111-2222-3333-4444-555555555555\"";

        AutoNumberSequences.EnsureSql(a, D).Should().Be(
            "CREATE SCHEMA IF NOT EXISTS \"autonumber\";\n" +
            $"CREATE SEQUENCE IF NOT EXISTS {seq} AS bigint START WITH 1000 INCREMENT BY 1 MINVALUE 0 CACHE 1;");
        AutoNumberSequences.DropSql(a, D).Should().Be($"DROP SEQUENCE IF EXISTS {seq};");
        AutoNumberSequences.ResetSql(a, 42, D).Should().Be(
            $"DROP SEQUENCE IF EXISTS {seq};\nCREATE SEQUENCE {seq} AS bigint START WITH 42 INCREMENT BY 1 MINVALUE 0 CACHE 1;");
        AutoNumberSequences.NextValueSql(a, D).Should().Be($"SELECT nextval('{seq}');");
        AutoNumberSequences.ReadSql(a, D).Should().Contain("FROM pg_catalog.pg_sequences")
            .And.Contain("schemaname = 'autonumber' AND sequencename = '11111111-2222-3333-4444-555555555555'");
    }

    [Fact]
    public void Catalog_checks_and_guarded_statements()
    {
        D.CreateSchemaIfMissing("data").Should().Be("CREATE SCHEMA IF NOT EXISTS \"data\";");
        D.TableExists("data", "audit").Should().Be(
            "EXISTS (SELECT 1 FROM pg_catalog.pg_tables WHERE schemaname = 'data' AND tablename = 'audit')");
        D.ColumnExists("data", "team", "regardingobjectid").Should().Be(
            "EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'data' AND table_name = 'team' AND column_name = 'regardingobjectid')");
        D.IndexExists("data", "team", "UX_team_record_template").Should().Be(
            "EXISTS (SELECT 1 FROM pg_catalog.pg_indexes WHERE schemaname = 'data' AND tablename = 'team' AND indexname = 'UX_team_record_template')");
        D.CreateIndexIfMissing("data", "audit", "IX_audit_objectid", "\"objectid\", \"objecttypecode\", \"createdon\" DESC")
            .Should().Be("CREATE INDEX IF NOT EXISTS \"IX_audit_objectid\" ON \"data\".\"audit\" (\"objectid\", \"objecttypecode\", \"createdon\" DESC);");
        D.CreateIndexIfMissing("data", "team", "UX_team_record_template", "\"regardingobjectid\"", unique: true, filter: "\"teamtemplateid\" IS NOT NULL")
            .Should().Be("CREATE UNIQUE INDEX IF NOT EXISTS \"UX_team_record_template\" ON \"data\".\"team\" (\"regardingobjectid\") WHERE \"teamtemplateid\" IS NOT NULL;");
        D.DropIndexIfExists("data", "principalobjectaccess", "UX_principalobjectaccess_object_principal")
            .Should().Be("DROP INDEX IF EXISTS \"data\".\"UX_principalobjectaccess_object_principal\";");
        D.When("1 = 1", "CREATE INDEX \"i\" ON \"data\".\"t\" (\"c\") WHERE \"s\" <> 'x'").Should().Be(
            "DO $xrmd$ BEGIN IF 1 = 1 THEN EXECUTE 'CREATE INDEX \"i\" ON \"data\".\"t\" (\"c\") WHERE \"s\" <> ''x'''; END IF; END $xrmd$;");
        D.AddColumnIfMissing("data", "account", "parentidtype", "integer").Should().Be(
            "ALTER TABLE \"data\".\"account\" ADD COLUMN IF NOT EXISTS \"parentidtype\" integer NULL;");
        D.RenameColumn("data", "account", "oldid", "accountid").Should().Be(
            "ALTER TABLE \"data\".\"account\" RENAME COLUMN \"oldid\" TO \"accountid\";");
    }

    [Fact]
    public void Postgresql_before_16_is_refused()
    {
        FluentActions.Invoking(() => PostgresDialect.EnsureSupportedVersion(150008, "15.8"))
            .Should().Throw<InvalidOperationException>().WithMessage("xrmverse needs PostgreSQL 16 or later (found 15.8).");
        FluentActions.Invoking(() => PostgresDialect.EnsureSupportedVersion(160004, "16.4")).Should().NotThrow();
        FluentActions.Invoking(() => PostgresDialect.EnsureSupportedVersion(170000, "17.0 (Debian 17.0-1)")).Should().NotThrow();
    }
}

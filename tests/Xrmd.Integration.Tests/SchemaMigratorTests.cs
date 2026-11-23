using Xrmd.Abstractions;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;

namespace Xrmd.Integration.Tests;

[Collection("xrmd")]
public class SchemaMigratorTests(XrmdFixture fx)
{
    private static readonly string[] ExpectedTables =
        ["entity", "attribute", "relationship", "optionset", "version_stamp", "schema_version", "filesession", "filesecret", "fileorphan", "hierarchy_security_exclusion", "upgrade_step", "component_layer", "solution_rank", "matchcode_table", "serviceendpoint_secret", "workflow_wait", "customcontrol_file", "migration_package", "migration_run", "migration_chunk", "migration_idmap", "migration_deferred", "migration_issue", "migration_phase", "migration_table", "migration_deactivated", "mail_secret"];

    private static async Task<long> ScalarAsync(DbConnection conn, string sql)
    {
        await using var cmd = TestSql.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task MigrateAsync_twice_creates_tables_once()
    {
        // Fresh database: the shared one is already migrated, seeded and stamped by the host at startup.
        var database = "xrmd_migrate_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);

        var factory = TestProvider.Factory(
            TestProvider.WithDatabase(fx.ConnectionString, database));
        var migrator = new SchemaMigrator(factory);

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        await using var conn = await factory.OpenAsync();
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM meta.schema_version")).Should().Be(19, "each migration is applied once");
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM meta.filesecret WHERE DATALENGTH(secret) = 32")).Should().Be(1, "0004 writes one secret");
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM meta.version_stamp")).Should().Be(1);
        (await ScalarAsync(conn, "SELECT [current] FROM meta.version_stamp")).Should().Be(0);

        var tables = new List<string>();
        await using (var cmd = TestSql.Command(conn, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'meta'"))
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync()) tables.Add(rd.GetString(0));
        }
        tables.Should().Contain(ExpectedTables);

        // 0003 adds the unmodelled XML of each meta table (the settings columns of a superseded draft are gone with it).
        (await ScalarAsync(conn, """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'meta' AND COLUMN_NAME = 'unmodelled_xml'
              AND TABLE_NAME IN ('entity', 'attribute', 'relationship', 'optionset')
            """)).Should().Be(4);
        (await ScalarAsync(conn, """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'meta' AND COLUMN_NAME IN ('solution_settings_xml', 'display_mask')
            """)).Should().Be(0);
    }

    [SqlServerFact("the draft 0003 is SQL Server migration history; PostgreSQL's schema is one baseline")]
    public async Task Migrator_upgrades_a_database_with_the_pre_folding_0003()
    {
        // The draft 0003 (before it was folded into the unmodelled XML migration) added entity.solution_settings_xml and
        // attribute.display_mask and was recorded as version 3; the later draft 0004 dropped them and added unmodelled_xml.
        var database = "xrmd_migrate_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);

        var factory = TestProvider.Factory(
            TestProvider.WithDatabase(fx.ConnectionString, database));
        var migrator = new SchemaMigrator(factory);
        await migrator.MigrateAsync();

        await using (var conn = await factory.OpenAsync())
        {
            foreach (var table in new[] { "entity", "attribute", "relationship", "optionset" })
            {
                await using var drop = TestSql.Command(conn, $"ALTER TABLE meta.{table} DROP COLUMN unmodelled_xml");
                await drop.ExecuteNonQueryAsync();
            }
            await using var old = TestSql.Command(conn, """
                ALTER TABLE meta.entity ADD solution_settings_xml nvarchar(max) NULL;
                ALTER TABLE meta.attribute ADD display_mask nvarchar(400) NULL;
                """);
            await old.ExecuteNonQueryAsync();
        }

        await migrator.MigrateAsync();

        await using var check = await factory.OpenAsync();
        (await ScalarAsync(check, """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'meta' AND COLUMN_NAME = 'unmodelled_xml'
              AND TABLE_NAME IN ('entity', 'attribute', 'relationship', 'optionset')
            """)).Should().Be(4);
        (await ScalarAsync(check, """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'meta' AND COLUMN_NAME IN ('solution_settings_xml', 'display_mask')
            """)).Should().Be(0);
        (await ScalarAsync(check, "SELECT COUNT(*) FROM meta.schema_version")).Should().Be(19);
        await migrator.MigrateAsync(); // idempotent once upgraded
    }

    [SqlServerFact("migration 0011 is SQL Server migration history; PostgreSQL's schema is one baseline")]
    public async Task Migration_0011_makes_the_share_index_per_table_on_existing_shares()
    {
        // Final review C2: a database from before 0011 has shares and the unique index on (objectid, principalid). The migration
        // replaces it with (objectid, objecttypecode, principalid), keeps every share, and then two tables' records with one id
        // can each be shared with the same principal.
        var database = "xrmd_migrate_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        var factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        var migrator = new SchemaMigrator(factory);
        await migrator.MigrateAsync();
        var record = Guid.NewGuid();
        var principal = Guid.NewGuid();
        await using (var conn = await factory.OpenAsync())
        {
            // The state of an organization before 0011: the share table with the old index and a share in it; 0011 not applied.
            await using var setup = TestSql.Command(conn, """
                IF SCHEMA_ID('data') IS NULL EXEC('CREATE SCHEMA data');
                CREATE TABLE data.principalobjectaccess (principalobjectaccessid uniqueidentifier NOT NULL PRIMARY KEY,
                    objectid uniqueidentifier NULL, objecttypecode int NULL, principalid uniqueidentifier NULL, accessrightsmask int NULL);
                CREATE UNIQUE INDEX UX_principalobjectaccess_object_principal ON data.principalobjectaccess (objectid, principalid);
                INSERT INTO data.principalobjectaccess VALUES (NEWID(), @record, 1, @principal, 1);
                INSERT INTO data.principalobjectaccess VALUES (NEWID(), NEWID(), 2, @principal, 3);
                DELETE FROM meta.schema_version WHERE version = 11;
                """);
            setup.AddParameter("@record", record);
            setup.AddParameter("@principal", principal);
            await setup.ExecuteNonQueryAsync();
        }

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        await using var check = await factory.OpenAsync();
        (await ScalarAsync(check, "SELECT COUNT(*) FROM meta.schema_version")).Should().Be(19);
        (await ScalarAsync(check, "SELECT COUNT(*) FROM data.principalobjectaccess")).Should().Be(2, "every share is kept");
        (await ScalarAsync(check, """
            SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('data.principalobjectaccess')
              AND name = 'UX_principalobjectaccess_object_principal'
            """)).Should().Be(0);
        var columns = new List<string>();
        await using (var cmd = TestSql.Command(check, """
            SELECT c.name FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('data.principalobjectaccess') AND i.name = 'UX_principalobjectaccess_object_type_principal'
              AND i.is_unique = 1
            ORDER BY ic.key_ordinal
            """))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) columns.Add(rd.GetString(0));
        columns.Should().Equal("objectid", "objecttypecode", "principalid");

        await using (var other = TestSql.Command(check, "INSERT INTO data.principalobjectaccess VALUES (NEWID(), @record, 4212, @principal, 3)"))
        {
            other.AddParameter("@record", record);
            other.AddParameter("@principal", principal);
            await other.ExecuteNonQueryAsync();
        }
        await using (var same = TestSql.Command(check, "INSERT INTO data.principalobjectaccess VALUES (NEWID(), @record, 1, @principal, 2)"))
        {
            same.AddParameter("@record", record);
            same.AddParameter("@principal", principal);
            var duplicate = async () => await same.ExecuteNonQueryAsync();
            (await duplicate.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(2601, "one share per record, table and principal");
        }
    }

    [Fact]
    public async Task New_database_has_the_same_share_index_as_a_migrated_one()
    {
        // SecurityBootstrap builds the index of a new database (its share table comes with the seed, after the migrations).
        var index = await TestSql.IndexAsync(fx.ConnectionString, "data", "principalobjectaccess", "UX_principalobjectaccess_object_type_principal");
        index.Should().NotBeNull();
        index!.Unique.Should().BeTrue();
        index.Keys.Should().Equal("objectid", "objecttypecode", "principalid");
        (await TestSql.IndexExistsAsync(fx.ConnectionString, "data", "principalobjectaccess", "UX_principalobjectaccess_object_principal")).Should().BeFalse();
    }
}

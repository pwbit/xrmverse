using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk.Metadata;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core;
using Xrmd.Metadata;
using Xrmd.Storage.PostgreSql;

namespace Xrmd.Integration.Tests.Postgres;

/// <summary>
/// A PostgreSQL 16 and a SQL Server container of their own (plan 2.7a, ruling R1: the shared fixture switches in Task 5), each
/// handing out fresh databases.
/// </summary>
public sealed class MetaSchemaContainers : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
        .Build();

    public Task InitializeAsync() => Task.WhenAll(_postgres.StartAsync(), _sql.StartAsync());

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _sql.DisposeAsync();
    }

    /// <summary>A new, empty PostgreSQL database created as the provisioning will (ruling P5), and its connection factory.</summary>
    public async Task<NpgsqlConnectionFactory> NewPostgresDatabaseAsync()
    {
        var database = "xrmd_meta_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $"CREATE DATABASE \"{database}\" TEMPLATE template0 ENCODING 'UTF8' LOCALE_PROVIDER icu ICU_LOCALE 'und'", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionFactory(new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString);
    }

    /// <summary>A new, empty SQL Server database and its connection factory.</summary>
    public async Task<SqlConnectionFactory> NewSqlServerDatabaseAsync()
    {
        var database = "xrmd_meta_" + Guid.NewGuid().ToString("N");
        await using (var master = new SqlConnection(_sql.GetConnectionString()))
        {
            await master.OpenAsync();
            await using var cmd = master.Command($"CREATE DATABASE [{database}]");
            await cmd.ExecuteNonQueryAsync();
        }
        return new SqlConnectionFactory(new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = database }.ConnectionString);
    }
}

/// <summary>
/// Plan 2.7a, Task 4 (ruling P6): a fresh database migrated on PostgreSQL has the meta and auth schemas SQL Server has after
/// migration 0018, and the PostgreSQL bootstrap gives the data tables their <c>versionnumber</c> (ruling P4).
/// </summary>
[Trait("Category", "Postgres")]
public sealed class MetaSchemaParityTests(MetaSchemaContainers fx) : IClassFixture<MetaSchemaContainers>
{
    private const string ColumnsSql = """
        SELECT table_schema, table_name, column_name FROM information_schema.columns
        WHERE table_schema IN ('meta', 'auth')
        """;

    [Fact]
    public async Task Both_providers_migrate_to_the_same_meta_and_auth_columns_and_versions()
    {
        var postgres = await fx.NewPostgresDatabaseAsync();
        var sqlServer = await fx.NewSqlServerDatabaseAsync();
        await new SchemaMigrator(postgres, postgres.Dialect).MigrateAsync();
        await new SchemaMigrator(sqlServer, sqlServer.Dialect).MigrateAsync();

        var pgColumns = await ColumnsAsync(postgres);
        var sqlColumns = await ColumnsAsync(sqlServer);
        sqlColumns.Should().HaveCountGreaterThan(190);
        pgColumns.Should().BeEquivalentTo(sqlColumns);

        (await VersionsAsync(postgres)).Should().Equal(Enumerable.Range(1, 18));
        (await VersionsAsync(sqlServer)).Should().Equal(Enumerable.Range(1, 18));
    }

    [Fact]
    public async Task The_postgresql_migration_is_idempotent_and_reports_its_status()
    {
        var factory = await fx.NewPostgresDatabaseAsync();
        var migrator = new SchemaMigrator(factory, factory.Dialect);

        (await migrator.GetStatusAsync()).Should().Be(new SchemaStatus(0, 1), "an empty database has the baseline pending");
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        (await migrator.GetStatusAsync()).Should().Be(new SchemaStatus(18, 0));

        await using var conn = await factory.OpenAsync();
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM meta.schema_version")).Should().Be(18);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM meta.version_stamp")).Should().Be(1);
        (await ScalarAsync(conn, "SELECT \"current\" FROM meta.version_stamp")).Should().Be(0);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM meta.filesecret WHERE octet_length(secret) = 32")).Should().Be(1);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM meta.change_tracking_state")).Should().Be(1);
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM pg_extension WHERE extname = 'citext'")).Should().Be(1);
        // The status read opened a pooled connection before citext existed: the bootstrap reloaded the data source's types, so
        // a string parameter (sent as citext) works on the connections handed out after it.
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM meta.upgrade_step WHERE name = @n", ("@n", DbType.String, "none"))).Should().Be(0);
    }

    [Fact]
    public async Task Logical_names_in_meta_compare_ignoring_case_and_definitions_are_text()
    {
        var factory = await fx.NewPostgresDatabaseAsync();
        await new SchemaMigrator(factory, factory.Dialect).MigrateAsync();

        await using var conn = await factory.OpenAsync();
        await ExecuteAsync(conn, "INSERT INTO meta.entity (metadata_id, logical_name, definition, version_stamp) VALUES (@id, @name, @def, 1)",
            ("@id", DbType.Guid, Guid.NewGuid()), ("@name", DbType.String, "account"), ("@def", DbType.String, "{}"));
        (await ScalarAsync(conn, "SELECT COUNT(*) FROM meta.entity WHERE logical_name = @name", ("@name", DbType.String, "ACCOUNT")))
            .Should().Be(1, "a string parameter (citext) matches the citext column ignoring case, as SQL Server's _CI_AS does");
        var duplicate = () => ExecuteAsync(conn,
            "INSERT INTO meta.entity (metadata_id, logical_name, definition, version_stamp) VALUES (@id, @name, @def, 1)",
            ("@id", DbType.Guid, Guid.NewGuid()), ("@name", DbType.String, "Account"), ("@def", DbType.String, "{}"));
        var thrown = await duplicate.Should().ThrowAsync<DbException>();
        factory.Dialect.Classify(thrown.Which).Should().Be(DbErrorKind.UniqueViolation);

        var types = await TypesAsync(conn, "meta", "entity");
        types["definition"].Should().Be("text");
        types["logical_name"].Should().Be("citext");
        types["metadata_id"].Should().Be("uuid");
    }

    [Fact]
    public async Task A_data_table_gets_a_new_versionnumber_on_every_write()
    {
        var factory = await fx.NewPostgresDatabaseAsync();
        await new SchemaMigrator(factory, factory.Dialect).MigrateAsync();
        var d = factory.Dialect;
        var entity = new EntityMetadata { LogicalName = "pb_project", SchemaName = "pb_Project", OwnershipType = OwnershipTypes.UserOwned };
        AttributeMetadata[] attributes =
        [
            SystemAttributes.PrimaryId("pb_project"),
            new StringAttributeMetadata { LogicalName = "pb_name", MaxLength = 100 },
            SystemAttributes.Owner(),
        ];

        await using var conn = await factory.OpenAsync();
        await ExecuteAsync(conn, d.CreateSchemaIfMissing(DdlGenerator.DataSchema));
        await ExecuteAsync(conn, DdlGenerator.CreateTable(entity, attributes, d));
        await ExecuteAsync(conn, ChangeTrackingSchema.EnsureIndexSql("pb_project", d));
        await ExecuteAsync(conn, ChangeTrackingSchema.EnsureIndexSql("pb_project", d)); // guarded: runs again

        var id = Guid.NewGuid();
        await ExecuteAsync(conn, "INSERT INTO data.pb_project (pb_projectid, pb_name) VALUES (@id, @name)",
            ("@id", DbType.Guid, id), ("@name", DbType.String, "Alpha"));
        var first = await ScalarAsync(conn, "SELECT versionnumber FROM data.pb_project WHERE pb_projectid = @id", ("@id", DbType.Guid, id));
        await ExecuteAsync(conn, "UPDATE data.pb_project SET pb_name = @name WHERE pb_projectid = @id",
            ("@id", DbType.Guid, id), ("@name", DbType.String, "Beta"));
        var second = await ScalarAsync(conn, "SELECT versionnumber FROM data.pb_project WHERE pb_projectid = @id", ("@id", DbType.Guid, id));
        second.Should().BeGreaterThan(first);

        // The deletion log draws from the same organization sequence (ruling P4), so its versions and the rows' compare.
        await ExecuteAsync(conn, "INSERT INTO meta.deleted_record (entity_logical_name, object_id, deleted_on) VALUES (@e, @id, statement_timestamp())",
            ("@e", DbType.String, "pb_project"), ("@id", DbType.Guid, id));
        (await ScalarAsync(conn, "SELECT version FROM meta.deleted_record WHERE object_id = @id", ("@id", DbType.Guid, id)))
            .Should().BeGreaterThan(second);

        (await ScalarAsync(conn, "SELECT COUNT(*) FROM data.pb_project WHERE pb_name = @name", ("@name", DbType.String, "BETA"))).Should().Be(1);
        (await ScalarAsync(conn, $"SELECT CASE WHEN {d.IndexExists("data", "pb_project", "IX_pb_project_versionnumber")} THEN 1 ELSE 0 END")).Should().Be(1);
    }

    [Fact]
    public async Task Column_changes_guards_and_autonumber_sequences_run_on_postgresql()
    {
        var factory = await fx.NewPostgresDatabaseAsync();
        await new SchemaMigrator(factory, factory.Dialect).MigrateAsync();
        var d = factory.Dialect;
        var entity = new EntityMetadata { LogicalName = "pb_item", SchemaName = "pb_Item", OwnershipType = OwnershipTypes.OrganizationOwned };
        await using var conn = await factory.OpenAsync();
        await ExecuteAsync(conn, DdlGenerator.CreateTable(entity, [SystemAttributes.PrimaryId("pb_item")], d));

        var money = new MoneyAttributeMetadata { LogicalName = "pb_cost" };
        await ExecuteAsync(conn, DdlGenerator.AddColumn("pb_item", money, d));
        await ExecuteAsync(conn, DdlGenerator.AddColumn("pb_item", new StringAttributeMetadata { LogicalName = "pb_code", MaxLength = 10 }, d));
        await ExecuteAsync(conn, DdlGenerator.AlterColumn("pb_item", new StringAttributeMetadata { LogicalName = "pb_code", MaxLength = 300 }, d));
        await ExecuteAsync(conn, d.AddColumnIfMissing("data", "pb_item", "pb_parenttype", d.ColumnType(SqlColumnKind.Int32)));
        await ExecuteAsync(conn, d.AddColumnIfMissing("data", "pb_item", "pb_parenttype", d.ColumnType(SqlColumnKind.Int32)));
        await ExecuteAsync(conn, d.RenameColumn("data", "pb_item", "pb_parenttype", "pb_ownertype"));
        await ExecuteAsync(conn, d.When(d.ColumnExists("data", "pb_item", "pb_ownertype") + " AND NOT " + d.IndexExists("data", "pb_item", "IX_pb_item_ownertype"),
            "CREATE INDEX \"IX_pb_item_ownertype\" ON \"data\".\"pb_item\" (\"pb_ownertype\") WHERE \"pb_code\" <> 'x'"));
        await ExecuteAsync(conn, d.CreateIndexIfMissing("data", "pb_item", "IX_pb_item_ownertype", "\"pb_ownertype\""));
        await ExecuteAsync(conn, DdlGenerator.DropColumn("pb_item", money, d));

        var types = await TypesAsync(conn, "data", "pb_item");
        types.Keys.Should().Contain(["pb_itemid", "pb_code", "pb_ownertype", "versionnumber"])
            .And.NotContain(["pb_cost", "pb_cost_base", "pb_parenttype"]);
        types["pb_ownertype"].Should().Be("integer");
        (await ScalarAsync(conn, $"SELECT CASE WHEN {d.IndexExists("data", "pb_item", "IX_pb_item_ownertype")} THEN 1 ELSE 0 END")).Should().Be(1);
        (await ScalarAsync(conn, $"SELECT CASE WHEN {d.TableExists("data", "pb_item")} THEN 1 ELSE 0 END")).Should().Be(1);

        var number = new StringAttributeMetadata { LogicalName = "pb_number", MetadataId = Guid.NewGuid() };
        await ExecuteAsync(conn, AutoNumberSequences.EnsureSql(number, d));
        await ExecuteAsync(conn, AutoNumberSequences.EnsureSql(number, d));
        (await ReadSequenceAsync(conn, AutoNumberSequences.ReadSql(number, d))).Should().Be((1000L, 1000L));
        (await ScalarAsync(conn, AutoNumberSequences.NextValueSql(number, d))).Should().Be(1000);
        (await ReadSequenceAsync(conn, AutoNumberSequences.ReadSql(number, d))).Should().Be((1000L, 1001L));
        await ExecuteAsync(conn, AutoNumberSequences.ResetSql(number, 5, d));
        (await ReadSequenceAsync(conn, AutoNumberSequences.ReadSql(number, d))).Should().Be((5L, 5L));
        await ExecuteAsync(conn, AutoNumberSequences.DropSql(number, d));
        await ExecuteAsync(conn, AutoNumberSequences.DropSql(number, d));
        (await ReadSequenceAsync(conn, AutoNumberSequences.ReadSql(number, d))).Should().BeNull();
    }

    private static async Task<HashSet<string>> ColumnsAsync(IDbConnectionFactory factory)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.Command(ColumnsSql);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) set.Add($"{rd.GetString(0)}.{rd.GetString(1)}.{rd.GetString(2)}".ToLowerInvariant());
        return set;
    }

    private static async Task<List<int>> VersionsAsync(IDbConnectionFactory factory)
    {
        var list = new List<int>();
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.Command("SELECT version FROM meta.schema_version ORDER BY version");
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) list.Add(rd.GetInt32(0));
        return list;
    }

    private static async Task<Dictionary<string, string>> TypesAsync(DbConnection conn, string schema, string table)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = conn.Command(
            "SELECT column_name::text, udt_name::text FROM information_schema.columns WHERE table_schema = @s AND table_name = @t");
        cmd.AddParameter("@s", DbType.String, schema);
        cmd.AddParameter("@t", DbType.String, table);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) types[rd.GetString(0)] = rd.GetString(1) is "int4" ? "integer" : rd.GetString(1);
        return types;
    }

    private static async Task<(long Seed, long Next)?> ReadSequenceAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.Command(sql);
        await using var rd = await cmd.ExecuteReaderAsync();
        return await rd.ReadAsync() ? (rd.GetInt64(0), rd.GetInt64(1)) : null;
    }

    private static async Task<long> ScalarAsync(DbConnection conn, string sql, params (string Name, DbType Type, object Value)[] ps)
    {
        await using var cmd = conn.Command(sql);
        foreach (var (name, type, value) in ps) cmd.AddParameter(name, type, value);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(DbConnection conn, string sql, params (string Name, DbType Type, object Value)[] ps)
    {
        await using var cmd = conn.Command(sql);
        foreach (var (name, type, value) in ps) cmd.AddParameter(name, type, value);
        await cmd.ExecuteNonQueryAsync();
    }
}

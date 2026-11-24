using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Integration.Tests.Metadata;

/// <summary>
/// S.2b2, Task 3: the store's writes take the model, and the SDK overloads convert to it; the <c>meta</c> rows must still be
/// written byte for byte as before (spec §2: stored data unchanged, no migration). The golden files under
/// <c>tests/fixtures/metadata-golden/stored/</c> were written by this test on the code before the change (S.2b1 merge,
/// 245cd7e0). Set <c>XRMD_UPDATE_STORED_GOLDEN=1</c> to rewrite them (only when the stored format is meant to change).
/// </summary>
/// <remarks>
/// To re-capture the golden on the base code: <c>git worktree add &lt;dir&gt; 245cd7e0</c>, copy this file and the
/// <c>fixtures/metadata-golden/stored</c> line of the test project's csproj there, remove the <c>using Xrmd.Metadata.Sdk;</c>
/// line (the namespace does not exist on the base; the SDK writes are instance members there), run this test with
/// <c>XRMD_UPDATE_STORED_GOLDEN=1</c>, copy <c>tests/fixtures/metadata-golden/stored/</c> back and run it here without the
/// variable. Ids: a caller-given MetadataId (<see cref="FixedIds"/>) and a deterministic one (a global choice's
/// <c>MetadataStore.OptionSetId</c>) are kept as they are, so a write that drops or regenerates one fails; only the ids the
/// store generates are replaced by placeholders.
/// </remarks>
[Collection("xrmd")]
public sealed partial class StoredMetadataJsonTests(XrmdFixture fx) : IAsyncLifetime
{
    private const string UpdateVariable = "XRMD_UPDATE_STORED_GOLDEN";

    // Caller-given MetadataIds: the store must keep them (a write that drops or regenerates one fails the golden).
    private static readonly Guid OneToManyId = new("5b2b2003-0000-4000-8000-000000000001");
    private static readonly Guid LookupId = new("5b2b2003-0000-4000-8000-000000000002");
    private static readonly Guid ManyToManyId = new("5b2b2003-0000-4000-8000-000000000003");

    /// <summary>The ids kept verbatim in the golden: the caller-given ones and the global choice's deterministic id.</summary>
    private static IReadOnlySet<Guid> FixedIds { get; } = new HashSet<Guid>
    {
        OneToManyId, LookupId, ManyToManyId, MetadataStore.OptionSetId("pb_priority"),
    };

    // Every meta table with a definition, and the key that names a row.
    private static readonly (string Table, string Key)[] MetaTables =
    [
        ("entity", "logical_name"),
        ("attribute", "entity_logical_name, logical_name"),
        ("relationship", "schema_name"),
        ("optionset", "name"),
    ];

    private IDbConnectionFactory _factory = null!;
    private string? _database;

    public async Task InitializeAsync()
    {
        var database = _database = "xrmd_stored_" + Guid.NewGuid().ToString("N");
        await TestProvider.CreateDatabaseAsync(fx.ConnectionString, database);
        _factory = TestProvider.Factory(TestProvider.WithDatabase(fx.ConnectionString, database));
        await new SchemaMigrator(_factory).MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null) await fx.DropDatabaseAsync(_database);
    }

    [Fact]
    public async Task Stored_json_is_unchanged()
    {
        var store = new MetadataStore(_factory);
        await store.LoadAsync();

        // A bare store's stand-in for the seeded transactioncurrency table, which a money column needs for its currency pair.
        var currency = new EntityMetadata { LogicalName = CurrencySchema.Table, SchemaName = "TransactionCurrency", OwnershipType = OwnershipTypes.OrganizationOwned };
        SetServerOwned(currency, nameof(EntityMetadata.ObjectTypeCode), 9105);
        await store.CreateEntityAsync(currency, new StringAttributeMetadata { LogicalName = "currencyname", MaxLength = 100 });

        await store.CreateEntityAsync(
            new EntityMetadata
            {
                LogicalName = "pb_parent", SchemaName = "pb_Parent", EntitySetName = "pb_parents",
                OwnershipType = OwnershipTypes.UserOwned, DisplayName = SystemAttributes.CreateLabel("Parent", 1033),
            },
            new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 100 });

        // A text, a choice (local option set) and a money column, created with the table.
        var project = new EntityMetadata
        {
            LogicalName = "pb_project", SchemaName = "pb_Project", EntitySetName = "pb_projects",
            OwnershipType = OwnershipTypes.UserOwned, DisplayName = SystemAttributes.CreateLabel("Project", 1033),
            DisplayCollectionName = SystemAttributes.CreateLabel("Projects", 1033),
            Description = SystemAttributes.CreateLabel("A project", 1033),
        };
        project.SetAttributes(
        [
            new StringAttributeMetadata
            {
                LogicalName = "pb_code", SchemaName = "pb_Code", MaxLength = 20,
                DisplayName = SystemAttributes.CreateLabel("Code", 1033),
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.ApplicationRequired),
            },
            new PicklistAttributeMetadata
            {
                LogicalName = "pb_stage", SchemaName = "pb_Stage", DisplayName = SystemAttributes.CreateLabel("Stage", 1033),
                OptionSet = new OptionSetMetadata(new OptionMetadataCollection(
                [
                    new OptionMetadata(SystemAttributes.CreateLabel("Draft", 1033), 100000000),
                    new OptionMetadata(SystemAttributes.CreateLabel("Active", 1033), 100000001),
                ])) { IsGlobal = false, OptionSetType = OptionSetType.Picklist },
            },
            new MoneyAttributeMetadata { LogicalName = "pb_budget", SchemaName = "pb_Budget", Precision = 2 },
        ]);
        await store.CreateEntityAsync(project, new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200 });

        // A lookup, with its relationship.
        await InSessionAsync(s => store.CreateOneToManyAsync(s,
            new OneToManyRelationshipMetadata
            {
                SchemaName = "pb_parent_pb_project", ReferencedEntity = "pb_parent", ReferencingEntity = "pb_project", MetadataId = OneToManyId,
            },
            new LookupAttributeMetadata
            {
                LogicalName = "pb_parentid", SchemaName = "pb_ParentId", DisplayName = SystemAttributes.CreateLabel("Parent", 1033), MetadataId = LookupId,
            },
            default));

        // The other write members: a global choice and a many-to-many relationship, then an update of each kind.
        await InSessionAsync(async s =>
        {
            await store.CreateOptionSetAsync(s, new OptionSetMetadata(new OptionMetadataCollection(
            [
                new OptionMetadata(SystemAttributes.CreateLabel("Low", 1033), 100000000),
                new OptionMetadata(SystemAttributes.CreateLabel("High", 1033), 100000001),
            ]))
            {
                Name = "pb_priority", DisplayName = SystemAttributes.CreateLabel("Priority", 1033), OptionSetType = OptionSetType.Picklist,
            }, default);
            await store.CreateManyToManyAsync(s, new ManyToManyRelationshipMetadata
            {
                SchemaName = "pb_parent_pb_project_links", Entity1LogicalName = "pb_parent", Entity2LogicalName = "pb_project",
                MetadataId = ManyToManyId,
            }, default);
        });
        await InSessionAsync(async s =>
        {
            await store.UpdateEntityAsync(s,
                new EntityMetadata { LogicalName = "pb_parent", Description = SystemAttributes.CreateLabel("The parent", 1033) }, default);
            await store.UpdateAttributeAsync(s, "pb_project",
                new StringAttributeMetadata { LogicalName = "pb_code", DisplayName = SystemAttributes.CreateLabel("Project code", 1033) }, default);
            await store.UpdateOptionSetAsync(s, new OptionSetMetadata
            {
                Name = "pb_priority", Description = SystemAttributes.CreateLabel("How urgent", 1033),
            }, default);
        });

        var rows = await StoredRowsAsync();
        foreach (var id in FixedIds)
            rows.Should().Contain(r => r.Json.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase), $"the id {id} is stored as given");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            var directory = SourceDirectory;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            foreach (var (name, json) in rows)
            {
                var path = Path.Combine(directory, name + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, json);
            }
            Assert.Fail("stored golden files written: review and commit them");
        }

        var golden = Directory.EnumerateFiles(GoldenDirectory, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(GoldenDirectory, path).Replace('\\', '/')[..^".json".Length])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        rows.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).Should().Equal(golden, "the same meta rows are written");
        foreach (var (name, json) in rows)
        {
            json.Should().Be(await File.ReadAllTextAsync(Path.Combine(GoldenDirectory, name + ".json")), name);
            // The stored text is MetadataSerializer output, so it reads back to the same text.
            Reserialized(name, json).Should().Be(json, name);
        }
    }

    /// <summary>
    /// Every meta row, named <c>table/key</c>, with its definition as stored. Ids the store generates (new GUIDs) are replaced,
    /// in order of first appearance over the ordered rows, by <c>00000000-0000-0000-0000-{n:D12}</c>, so equal ids stay equal;
    /// the <see cref="FixedIds"/> stay as they are.
    /// </summary>
    private async Task<List<(string Name, string Json)>> StoredRowsAsync()
    {
        var raw = new List<(string Name, string Json)>();
        await using var conn = await _factory.OpenAsync();
        foreach (var (table, key) in MetaTables)
        {
            var keyColumns = key.Split(", ");
            await using var cmd = TestSql.Command(conn,
                $"SELECT {key}, definition FROM meta.{table} ORDER BY {key}");
            await using var reader = await cmd.ExecuteReaderAsync();
            var batch = new List<(string, string)>();
            while (await reader.ReadAsync())
            {
                var name = string.Join(".", Enumerable.Range(0, keyColumns.Length).Select(reader.GetString));
                batch.Add(($"{table}/{name}", reader.GetString(keyColumns.Length)));
            }
            raw.AddRange(batch.OrderBy(r => r.Item1, StringComparer.Ordinal));
        }

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return raw.Select(r => (r.Name, GuidPattern().Replace(r.Json, m =>
        {
            if (FixedIds.Contains(Guid.Parse(m.Value))) return m.Value;
            if (!ids.TryGetValue(m.Value, out var id))
                ids[m.Value] = id = $"00000000-0000-0000-0000-{ids.Count + 1:D12}";
            return id;
        }))).ToList();
    }

    private static string Reserialized(string name, string json) => name[..name.IndexOf('/')] switch
    {
        "entity" => MetadataSerializer.Serialize(MetadataSerializer.Deserialize(json)),
        "attribute" => MetadataSerializer.SerializeAttribute(MetadataSerializer.DeserializeAttribute(json)),
        "relationship" => MetadataSerializer.SerializeRelationship(MetadataSerializer.DeserializeRelationship(json)),
        "optionset" => MetadataSerializer.SerializeOptionSet(MetadataSerializer.DeserializeOptionSet(json)),
        _ => throw new InvalidOperationException($"Unknown meta table of {name}."),
    };

    private async Task InSessionAsync(Func<DataSession, Task> write)
    {
        await using var conn = await _factory.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var session = new DataSession(conn, tx);
        try
        {
            await write(session);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            session.RolledBack();
            throw;
        }
        session.Committed();
    }

    private static void SetServerOwned(object target, string property, object? value) =>
        target.GetType().GetProperty(property)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);

    private static string GoldenDirectory => Path.Combine(AppContext.BaseDirectory, "fixtures", "metadata-golden", "stored");

    private static string SourceDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("xrmverse.sln not found."),
                "tests", "fixtures", "metadata-golden", "stored");
        }
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();
}

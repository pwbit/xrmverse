using FluentAssertions;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;
using Xrmd.Query.Model;
using Xrmd.Query.Sql;

namespace Xrmd.Query.Tests.Sdk;

/// <summary>
/// S.2c Task 3: the compilers read the model metadata per call (Review Focus 3), and the callbacks implemented elsewhere get the cached
/// SDK view of the model table, the instance <c>store.GetEntity(name)</c> returns (Review Focus 4).
/// </summary>
public sealed class QueryMetadataTests
{
    private const string Table = "xrmv_fresh";
    private static readonly QueryContext Context = new(Guid.NewGuid(), Guid.NewGuid(), new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc));

    private static QueryModel Select(params string[] columns)
    {
        var root = new EntityNode { EntityName = Table };
        root.Columns.AddRange(columns.Select(c => new ColumnNode(c)));
        return new QueryModel { Root = root };
    }

    [Fact]
    public void A_column_added_to_the_store_is_selectable_by_the_next_compilation()
    {
        var store = new ModelStore(NewTable());
        var compiler = new SqlQueryCompiler(store, TestMetadata.Dialect);
        compiler.Compile(Select("xrmv_name"), Context);

        store.AddColumn(Table, new TextColumn { LogicalName = "xrmv_extra", SchemaName = "xrmv_Extra", MaxLength = 100, EntityLogicalName = Table });
        var c = compiler.Compile(Select("xrmv_extra"), Context);

        c.Sql.Should().Contain("t0.[xrmv_extra] AS c1");
        var table = store.GetTable(Table)!;
        c.Table.Should().BeSameAs(table);
        c.Columns.Single(x => x.ResultName == "xrmv_extra").Column.Should().BeSameAs(table.Attributes!.Single(a => a.LogicalName == "xrmv_extra"));
    }

    [Fact]
    public void The_security_callback_gets_the_view_the_store_gives_for_every_compilation()
    {
        var store = new ModelStore(NewTable());
        var security = new RecordingSecurity();
        var compiler = new SqlQueryCompiler(store, TestMetadata.Dialect);

        var first = compiler.Compile(Select("xrmv_name"), Context with { Security = security });
        var second = compiler.Compile(Select("xrmv_name"), Context with { Security = security });

        var view = store.GetEntity(Table)!;
        security.Filtered.Should().HaveCount(2).And.OnlyContain(e => ReferenceEquals(e, view));
        security.Unreadable.Should().HaveCount(2).And.OnlyContain(e => ReferenceEquals(e, view));
        first.Root.Should().BeSameAs(view);
        second.Root.Should().BeSameAs(view);
        view.ToModel().Should().BeSameAs(store.GetTable(Table));
        // The SDK attribute of a result column is the column of that same view.
        second.Columns.Single(x => x.ResultName == "xrmv_name").Attribute.Should()
            .BeSameAs(view.Attributes.Single(a => a.LogicalName == "xrmv_name"));
    }

    [Fact]
    public void All_columns_of_a_wide_table_have_the_columns_of_the_view_the_store_gives()
    {
        var table = NewTable(Enumerable.Range(0, 400).Select(i => new TextColumn
        {
            LogicalName = $"xrmv_c{i}", SchemaName = $"xrmv_C{i}", MaxLength = 100, EntityLogicalName = Table,
        }));
        var store = new ModelStore(table);
        var compiled = new SqlQueryCompiler(store, TestMetadata.Dialect)
            .Compile(new QueryModel { Root = new EntityNode { EntityName = Table, AllColumns = true } }, Context);

        var view = store.GetEntity(Table)!;
        var byName = view.Attributes.ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        var columns = compiled.Columns.Where(c => c.Column is not null).ToList();
        columns.Should().HaveCountGreaterThan(400);
        columns.Should().OnlyContain(c => ReferenceEquals(c.Attribute, byName[c.Column!.LogicalName!]));
    }

    [Fact]
    public void ToModel_looks_a_view_up_and_converts_any_other_sdk_object_afresh()
    {
        var store = new ModelStore(NewTable());
        var view = store.GetEntity(Table)!;
        var model = store.GetTable(Table)!;

        view.ToModel().Should().BeSameAs(model);
        view.Attributes.Single(a => a.LogicalName == "xrmv_name").ToModel().Should()
            .BeSameAs(model.Attributes!.Single(a => a.LogicalName == "xrmv_name"));

        var entity = new EntityMetadata { LogicalName = "xrmv_other" };
        var attribute = new StringAttributeMetadata { LogicalName = "xrmv_text", MaxLength = 10 };
        entity.ToModel().Should().NotBeSameAs(entity.ToModel(), "an object that is not a view may change: it is never cached");
        entity.ToModel().LogicalName.Should().Be("xrmv_other");
        attribute.ToModel().Should().BeOfType<TextColumn>().Which.MaxLength.Should().Be(10);
    }

    private static TableDefinition NewTable(IEnumerable<ColumnDefinition>? more = null)
    {
        var table = new TableDefinition
        {
            LogicalName = Table, SchemaName = Table, EntitySetName = Table + "s", ObjectTypeCode = 10900,
            OwnershipType = TableOwnership.UserOwned, PrimaryIdAttribute = Table + "id", PrimaryNameAttribute = "xrmv_name",
        };
        table.SetAttributes(SystemAttributes.Complete(table,
            [new TextColumn { LogicalName = "xrmv_name", SchemaName = "xrmv_Name", MaxLength = 100, EntityLogicalName = Table }, .. more ?? []]));
        return table;
    }

    /// <summary>Records the SDK instances the compiler passes; no restriction.</summary>
    private sealed class RecordingSecurity : IQuerySecurity
    {
        public List<EntityMetadata> Filtered { get; } = [];

        public List<EntityMetadata> Unreadable { get; } = [];

        public FilterNode? ReadFilter(EntityMetadata entity)
        {
            Filtered.Add(entity);
            return null;
        }

        public IReadOnlySet<string> UnreadableColumns(EntityMetadata entity)
        {
            Unreadable.Add(entity);
            return new HashSet<string>();
        }
    }

    /// <summary>
    /// A store on model tables that, like the real one, replaces a table by a new instance when it changes (a definition handed
    /// out is never changed).
    /// </summary>
    private sealed class ModelStore(params TableDefinition[] tables) : IMetadataStore
    {
        private readonly Dictionary<string, TableDefinition> _tables = tables.ToDictionary(t => t.LogicalName!, StringComparer.Ordinal);

        public void AddColumn(string table, ColumnDefinition column)
        {
            var changed = MetadataJson.Clone(_tables[table]);
            changed.Attributes!.Add(column);
            _tables[table] = changed;
        }

        public long VersionStamp => 1;
        public TableDefinition? GetTable(string logicalName) => _tables.GetValueOrDefault(logicalName);
        public TableDefinition? GetTableBySetName(string entitySetName) => _tables.Values.FirstOrDefault(t => t.EntitySetName == entitySetName);
        public TableDefinition? GetTableByObjectTypeCode(int objectTypeCode) => _tables.Values.FirstOrDefault(t => t.ObjectTypeCode == objectTypeCode);
        public IReadOnlyList<TableDefinition> GetAllTables() => [.. _tables.Values];
        public ChoiceSetBase? GetChoiceSet(string name) => null;
        public IReadOnlyList<ChoiceSetBase> GetAllChoiceSets() => [];
        public RelationshipDefinition? GetRelationshipDefinition(string schemaName) => null;
        public IReadOnlyList<ManyToManyRelationship> GetManyToManyDefinitions() => [];
        public IReadOnlyList<OneToManyRelationship> GetOneToManyDefinitions() => [];
        public Task LoadAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateEntityAsync(TableDefinition entity, ColumnDefinition primaryName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAttributeAsync(string entityLogicalName, ColumnDefinition attribute, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task CreateEntityAsync(DataSession session, TableDefinition entity, ColumnDefinition primaryName, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task AddAttributeAsync(DataSession session, string entityLogicalName, ColumnDefinition attribute, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task UpdateEntityAsync(DataSession session, TableDefinition changes, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateAttributeAsync(DataSession session, string entityLogicalName, ColumnDefinition changes, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAttributeAsync(DataSession session, string entityLogicalName, string attributeLogicalName, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteEntityAsync(DataSession session, string entityLogicalName, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateOptionSetAsync(DataSession session, ChoiceSetBase optionSet, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateOptionSetAsync(DataSession session, ChoiceSetBase changes, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteOptionSetAsync(DataSession session, string name, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateOneToManyAsync(DataSession session, OneToManyRelationship relationship, LookupColumn lookup, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task CreateCustomerRelationshipsAsync(DataSession session, LookupColumn lookup,
            IReadOnlyList<OneToManyRelationship> relationships, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateManyToManyAsync(DataSession session, ManyToManyRelationship relationship, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteRelationshipAsync(DataSession session, string schemaName, CancellationToken ct) => throw new NotSupportedException();
    }
}

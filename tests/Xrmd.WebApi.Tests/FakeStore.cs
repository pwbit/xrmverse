using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.WebApi.Tests;

/// <summary>
/// In-memory <see cref="IMetadataStore"/> over a mutable entity list (read members only). Each SDK fixture is converted to the
/// model once, on its first read (<c>SdkMetadataView.ModelOf</c>): a fixture mutated after its first read is not seen (adding or
/// removing a fixture is).
/// </summary>
internal sealed class FakeStore(params EntityMetadata[] entities) : IMetadataStore
{
    public List<EntityMetadata> Entities { get; } = [.. entities];
    public long VersionStamp { get; set; } = 1;
    // The model of each SDK fixture (converted once per instance); the SDK view of it is the fixture itself.
    private static TableDefinition? Model(EntityMetadata? e) => e is null ? null : SdkMetadataView.ModelOf(e);
    public TableDefinition? GetTable(string logicalName) => Model(Entities.FirstOrDefault(e => e.LogicalName == logicalName));
    public TableDefinition? GetTableBySetName(string entitySetName) => Model(Entities.FirstOrDefault(e => e.EntitySetName == entitySetName));
    public TableDefinition? GetTableByObjectTypeCode(int objectTypeCode) => Model(Entities.FirstOrDefault(e => e.ObjectTypeCode == objectTypeCode));
    public IReadOnlyList<TableDefinition> GetAllTables() => Entities.Select(e => Model(e)!).ToList();
    public Task LoadAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task CreateEntityAsync(TableDefinition entity, ColumnDefinition primaryName, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task AddAttributeAsync(string entityLogicalName, ColumnDefinition attribute, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task CreateEntityAsync(DataSession session, TableDefinition entity, ColumnDefinition primaryName, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task AddAttributeAsync(DataSession session, string entityLogicalName, ColumnDefinition attribute, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task UpdateEntityAsync(DataSession session, TableDefinition changes, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task UpdateAttributeAsync(DataSession session, string entityLogicalName, ColumnDefinition changes, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task DeleteAttributeAsync(DataSession session, string entityLogicalName, string attributeLogicalName, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task DeleteEntityAsync(DataSession session, string entityLogicalName, CancellationToken ct) =>
        throw new NotSupportedException();
    public ChoiceSetBase? GetChoiceSet(string name) => null;
    public IReadOnlyList<ChoiceSetBase> GetAllChoiceSets() => [];
    public Task CreateOptionSetAsync(DataSession session, ChoiceSetBase optionSet, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task UpdateOptionSetAsync(DataSession session, ChoiceSetBase changes, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task DeleteOptionSetAsync(DataSession session, string name, CancellationToken ct) =>
        throw new NotSupportedException();
    public RelationshipDefinition? GetRelationshipDefinition(string schemaName) => null;
    public IReadOnlyList<ManyToManyRelationship> GetManyToManyDefinitions() => [];
    public List<OneToManyRelationshipMetadata> OneToMany { get; } = [];
    public IReadOnlyList<OneToManyRelationship> GetOneToManyDefinitions() =>
        OneToMany.Select(r => (OneToManyRelationship)SdkMetadataView.ModelOf(r)).ToList();
    public Task CreateOneToManyAsync(DataSession session, OneToManyRelationship relationship, LookupColumn lookup,
        CancellationToken ct) => throw new NotSupportedException();
    public Task CreateCustomerRelationshipsAsync(DataSession session, LookupColumn lookup,
        IReadOnlyList<OneToManyRelationship> relationships, CancellationToken ct) => throw new NotSupportedException();
    public Task CreateManyToManyAsync(DataSession session, ManyToManyRelationship relationship, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task DeleteRelationshipAsync(DataSession session, string schemaName, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>A custom API registry that knows no API, for tests that build the model without custom APIs.</summary>
internal static class TestCustomApis
{
    public static Xrmd.Pipeline.CustomApis.CustomApiRegistry None() =>
        new(_ => Task.FromResult<IReadOnlyList<Xrmd.Pipeline.CustomApis.CustomApiDefinition>>([]));
}

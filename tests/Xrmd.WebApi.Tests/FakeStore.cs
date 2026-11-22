using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;

namespace Xrmd.WebApi.Tests;

/// <summary>In-memory <see cref="IMetadataStore"/> over a mutable entity list (read members only).</summary>
internal sealed class FakeStore(params EntityMetadata[] entities) : IMetadataStore
{
    public List<EntityMetadata> Entities { get; } = [.. entities];
    public long VersionStamp { get; set; } = 1;
    public EntityMetadata? GetEntity(string logicalName) => Entities.FirstOrDefault(e => e.LogicalName == logicalName);
    public EntityMetadata? GetEntityBySetName(string entitySetName) => Entities.FirstOrDefault(e => e.EntitySetName == entitySetName);
    public EntityMetadata? GetEntityByObjectTypeCode(int objectTypeCode) => Entities.FirstOrDefault(e => e.ObjectTypeCode == objectTypeCode);
    public IReadOnlyList<EntityMetadata> GetAllEntities() => Entities;
    public Task LoadAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task CreateEntityAsync(EntityMetadata entity, AttributeMetadata primaryName, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task AddAttributeAsync(string entityLogicalName, AttributeMetadata attribute, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task CreateEntityAsync(DataSession session, EntityMetadata entity, AttributeMetadata primaryName, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task AddAttributeAsync(DataSession session, string entityLogicalName, AttributeMetadata attribute, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task UpdateEntityAsync(DataSession session, EntityMetadata changes, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task UpdateAttributeAsync(DataSession session, string entityLogicalName, AttributeMetadata changes, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task DeleteAttributeAsync(DataSession session, string entityLogicalName, string attributeLogicalName, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task DeleteEntityAsync(DataSession session, string entityLogicalName, CancellationToken ct) =>
        throw new NotSupportedException();
    public OptionSetMetadataBase? GetOptionSet(string name) => null;
    public IReadOnlyList<OptionSetMetadataBase> GetAllOptionSets() => [];
    public Task CreateOptionSetAsync(DataSession session, OptionSetMetadataBase optionSet, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task UpdateOptionSetAsync(DataSession session, OptionSetMetadataBase changes, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task DeleteOptionSetAsync(DataSession session, string name, CancellationToken ct) =>
        throw new NotSupportedException();
    public RelationshipMetadataBase? GetRelationship(string schemaName) => null;
    public IReadOnlyList<ManyToManyRelationshipMetadata> GetManyToManyRelationships() => [];
    public List<OneToManyRelationshipMetadata> OneToMany { get; } = [];
    public IReadOnlyList<OneToManyRelationshipMetadata> GetOneToManyRelationships() => OneToMany;
    public Task CreateOneToManyAsync(DataSession session, OneToManyRelationshipMetadata relationship, LookupAttributeMetadata lookup,
        CancellationToken ct) => throw new NotSupportedException();
    public Task CreateCustomerRelationshipsAsync(DataSession session, LookupAttributeMetadata lookup,
        IReadOnlyList<OneToManyRelationshipMetadata> relationships, CancellationToken ct) => throw new NotSupportedException();
    public Task CreateManyToManyAsync(DataSession session, ManyToManyRelationshipMetadata relationship, CancellationToken ct) =>
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

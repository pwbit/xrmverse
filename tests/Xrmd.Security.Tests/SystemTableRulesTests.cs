using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;

namespace Xrmd.Security.Tests;

/// <summary>The checks of <see cref="SystemTableRules"/> that need no database.</summary>
public sealed class SystemTableRulesTests
{
    private static readonly Guid SystemUserId = Guid.NewGuid();

    /// <summary>A factory that must not be used: these requests are decided without reading.</summary>
    internal sealed class NoDatabase : IDbConnectionFactory
    {
        public Xrmd.Abstractions.Storage.ISqlDialect Dialect => Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance;

        public Type ConnectionType => Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.ConnectionType;

        public Task<DbConnection> OpenAsync(CancellationToken ct = default) => throw new InvalidOperationException("No database here.");
    }

    /// <summary>A store with no entity (an Update reads none).</summary>
    internal sealed class NoMetadata : IMetadataStore
    {
        public long VersionStamp => 1;
        public EntityMetadata? GetEntity(string logicalName) => null;
        public EntityMetadata? GetEntityBySetName(string entitySetName) => null;
        public EntityMetadata? GetEntityByObjectTypeCode(int objectTypeCode) => null;
        public IReadOnlyList<EntityMetadata> GetAllEntities() => [];
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
        public IReadOnlyList<OneToManyRelationshipMetadata> GetOneToManyRelationships() => [];
        public Task CreateOneToManyAsync(DataSession session, OneToManyRelationshipMetadata relationship, LookupAttributeMetadata lookup,
            CancellationToken ct) => throw new NotSupportedException();
        public Task CreateCustomerRelationshipsAsync(DataSession session, LookupAttributeMetadata lookup,
            IReadOnlyList<OneToManyRelationshipMetadata> relationships, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateManyToManyAsync(DataSession session, ManyToManyRelationshipMetadata relationship, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteRelationshipAsync(DataSession session, string schemaName, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Theory]
    [InlineData("systemuser")]
    [InlineData("asyncoperation")]
    [InlineData("sdkmessageprocessingstep")]
    public async Task Malformed_primary_key_is_left_to_the_core(string entity)
    {
        var target = new Entity(entity) { [entity + "id"] = "not-a-guid", ["name"] = "x" };
        if (entity == "asyncoperation")
        {
            target.Attributes.Remove("name");
            target["statecode"] = new OptionSetValue(3);
            target["statuscode"] = new OptionSetValue(32);
        }
        await using var conn = new SqlConnection();
        var act = () => SystemTableRules.EnforceAsync(new UpdateRequest { Target = target }, new Caller(SystemUserId, Guid.Empty),
            SystemUserId, new SecurityCache(new NoDatabase()), new NoMetadata(), new DataSession(conn, null), default);

        await act.Should().NotThrowAsync();
    }
}

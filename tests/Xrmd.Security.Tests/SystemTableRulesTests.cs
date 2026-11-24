using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Model.Metadata;

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

    /// <summary>
    /// A store with no entity (an Update reads none). Like the other test stores it would not see a fixture mutated after its
    /// first read (the store hands out model instances converted once).
    /// </summary>
    internal sealed class NoMetadata : IMetadataStore
    {
        public long VersionStamp => 1;
        public TableDefinition? GetTable(string logicalName) => null;
        public TableDefinition? GetTableBySetName(string entitySetName) => null;
        public TableDefinition? GetTableByObjectTypeCode(int objectTypeCode) => null;
        public IReadOnlyList<TableDefinition> GetAllTables() => [];
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
        public IReadOnlyList<OneToManyRelationship> GetOneToManyDefinitions() => [];
        public Task CreateOneToManyAsync(DataSession session, OneToManyRelationship relationship, LookupColumn lookup,
            CancellationToken ct) => throw new NotSupportedException();
        public Task CreateCustomerRelationshipsAsync(DataSession session, LookupColumn lookup,
            IReadOnlyList<OneToManyRelationship> relationships, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateManyToManyAsync(DataSession session, ManyToManyRelationship relationship, CancellationToken ct) =>
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

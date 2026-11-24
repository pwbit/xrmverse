using System.Reflection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Tds.Tests;

/// <summary>
/// In-memory <see cref="IMetadataStore"/> with <c>account</c>, <c>contact</c> and <c>systemuser</c>, completed with
/// <see cref="SystemAttributes.Complete"/>. <c>account</c> has a column of each type the endpoint exposes, and also has a file, an image and a multiselect column and the file and image
/// companions, which the SQL endpoint does not expose (ruling T7). Each SDK fixture is converted to the model once, on its first
/// read (<c>SdkMetadataView.ModelOf</c>): a fixture mutated after its first read is not seen.
/// </summary>
internal sealed class TestMetadata : IMetadataStore
{
    public static TestMetadata Instance { get; } = new();

    private readonly List<EntityMetadata> _entities;

    private TestMetadata()
    {
        _entities =
        [
            Entity("account", 1, OwnershipTypes.UserOwned, "name",
            [
                new StringAttributeMetadata { LogicalName = "name", SchemaName = "Name", MaxLength = 160 },
                new StringAttributeMetadata { LogicalName = "accountnumber", SchemaName = "AccountNumber", MaxLength = 20 },
                new LookupAttributeMetadata { LogicalName = "primarycontactid", SchemaName = "PrimaryContactId", Targets = ["contact"] },
                new LookupAttributeMetadata { LogicalName = "parentaccountid", SchemaName = "ParentAccountId", Targets = ["account"] },
                new MoneyAttributeMetadata { LogicalName = "revenue", SchemaName = "Revenue" },
                new IntegerAttributeMetadata { LogicalName = "numberofemployees", SchemaName = "NumberOfEmployees" },
                new PicklistAttributeMetadata { LogicalName = "industrycode", SchemaName = "IndustryCode" },
                new MemoAttributeMetadata { LogicalName = "description", SchemaName = "Description", MaxLength = 2000 },
                new BigIntAttributeMetadata { LogicalName = "pb_visits", SchemaName = "pb_Visits" },
                new DecimalAttributeMetadata { LogicalName = "pb_rating", SchemaName = "pb_Rating", Precision = 2 },
                new DoubleAttributeMetadata { LogicalName = "pb_score", SchemaName = "pb_Score", Precision = 2 },
                new BooleanAttributeMetadata { LogicalName = "pb_active", SchemaName = "pb_Active" },
                new FileAttributeMetadata { LogicalName = "pb_document", SchemaName = "pb_Document" },
                new ImageAttributeMetadata { LogicalName = "pb_logo", SchemaName = "pb_Logo" },
                new MultiSelectPicklistAttributeMetadata { LogicalName = "pb_regions", SchemaName = "pb_Regions" },
                // The companions of the file and image columns, as the server makes them (FileColumns.Companions).
                Companion(new StringAttributeMetadata { LogicalName = "pb_document_name", SchemaName = "pb_Document_Name", MaxLength = 255 }, "pb_document"),
                Companion(new UniqueIdentifierAttributeMetadata { LogicalName = "pb_logoid", SchemaName = "pb_LogoId" }, "pb_logo"),
                Companion(new BigIntAttributeMetadata { LogicalName = "pb_logo_timestamp", SchemaName = "pb_Logo_Timestamp" }, "pb_logo"),
                Companion(new StringAttributeMetadata { LogicalName = "pb_logo_url", SchemaName = "pb_Logo_URL", MaxLength = 200 }, "pb_logo"),
            ]),
            Entity("contact", 2, OwnershipTypes.UserOwned, "fullname",
            [
                new StringAttributeMetadata { LogicalName = "fullname", SchemaName = "FullName", MaxLength = 160 },
                new StringAttributeMetadata { LogicalName = "emailaddress1", SchemaName = "EMailAddress1", MaxLength = 100 },
                new LookupAttributeMetadata { LogicalName = "parentcustomerid", SchemaName = "ParentCustomerId", Targets = ["account"] },
            ]),
            Entity("systemuser", 8, OwnershipTypes.BusinessOwned, "fullname",
            [
                new StringAttributeMetadata { LogicalName = "fullname", SchemaName = "FullName", MaxLength = 200 },
            ]),
        ];
    }

    private static EntityMetadata Entity(string name, int otc, OwnershipTypes ownership, string primaryName, AttributeMetadata[] attrs)
    {
        var e = new EntityMetadata { LogicalName = name, SchemaName = name, EntitySetName = name + "s", OwnershipType = ownership };
        Set(e, nameof(EntityMetadata.PrimaryIdAttribute), name + "id");
        Set(e, nameof(EntityMetadata.PrimaryNameAttribute), primaryName);
        Set(e, nameof(EntityMetadata.ObjectTypeCode), (int?)otc);
        foreach (var a in attrs) Set(a, nameof(AttributeMetadata.EntityLogicalName), name);
        e.SetAttributes(SystemAttributes.Complete(e, attrs));
        return e;
    }

    private static AttributeMetadata Companion(AttributeMetadata companion, string main)
    {
        Set(companion, nameof(AttributeMetadata.AttributeOf), main);
        Set(companion, nameof(AttributeMetadata.LinkedAttributeId), (Guid?)Guid.NewGuid());
        return companion;
    }

    private static void Set(object target, string property, object? value)
    {
        var p = target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!;
        var setter = p.GetSetMethod(nonPublic: true)
                     ?? p.DeclaringType!.GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.GetSetMethod(nonPublic: true)!;
        setter.Invoke(target, [value]);
    }

    public long VersionStamp => 1;
    // The model of each SDK fixture (converted once per instance); the SDK view of it is the fixture itself.
    private static TableDefinition? Model(EntityMetadata? e) => e is null ? null : SdkMetadataView.ModelOf(e);
    public TableDefinition? GetTable(string logicalName) => Model(_entities.FirstOrDefault(e => e.LogicalName == logicalName));
    public TableDefinition? GetTableBySetName(string entitySetName) => Model(_entities.FirstOrDefault(e => e.EntitySetName == entitySetName));
    public TableDefinition? GetTableByObjectTypeCode(int objectTypeCode) => Model(_entities.FirstOrDefault(e => e.ObjectTypeCode == objectTypeCode));
    public IReadOnlyList<TableDefinition> GetAllTables() => _entities.Select(e => Model(e)!).ToList();
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

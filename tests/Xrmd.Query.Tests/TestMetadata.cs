using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Query.Tests;

/// <summary>
/// In-memory <see cref="IMetadataStore"/> with <c>pb_project</c> (as <c>TestEntities</c> of the integration tests) and
/// <c>systemuser</c> (as the seed), both completed with <see cref="SystemAttributes.Complete"/>. Each SDK fixture is converted to
/// the model once, on its first read (<c>SdkMetadataView.ModelOf</c>): a fixture mutated after its first read is not seen.
/// </summary>
internal sealed class TestMetadata : IMetadataStore
{
    public static TestMetadata Instance { get; } = new();

    /// <summary>The SQL Server dialect the compiler tests write (plan 2.7a: their expected SQL is SQL Server's).</summary>
    public static Xrmd.Abstractions.Storage.ISqlDialect Dialect => Xrmd.Abstractions.Storage.SqlServer.SqlServerDialect.Instance;

    private readonly List<EntityMetadata> _entities;

    private TestMetadata()
    {
        _entities =
        [
            Entity("systemuser", 8, OwnershipTypes.BusinessOwned, "fullname",
            [
                new StringAttributeMetadata { LogicalName = "fullname", SchemaName = "FullName", MaxLength = 200 },
                new StringAttributeMetadata { LogicalName = "domainname", SchemaName = "DomainName", MaxLength = 1024 },
                new LookupAttributeMetadata { LogicalName = "businessunitid", SchemaName = "BusinessUnitId", Targets = ["businessunit"] },
            ]),
            Entity("account", 1, OwnershipTypes.UserOwned, "name",
            [
                new StringAttributeMetadata { LogicalName = "name", SchemaName = "Name", MaxLength = 160 },
                new LookupAttributeMetadata { LogicalName = "parentaccountid", SchemaName = "ParentAccountId", Targets = ["account"] },
                // A multi-target lookup whose first target is hierarchical: the tree operators must refuse it, not walk account.
                new LookupAttributeMetadata { LogicalName = "pb_regardingid", SchemaName = "pb_RegardingId", Targets = ["account", "systemuser"] },
            ]),
            Entity("new_sensordata", 10010, OwnershipTypes.UserOwned, "new_name",
            [
                new StringAttributeMetadata { LogicalName = "new_name", SchemaName = "new_Name", MaxLength = 100 },
                new StringAttributeMetadata
                {
                    LogicalName = "new_payload", SchemaName = "new_Payload", MaxLength = 4000, FormatName = StringFormatName.Json,
                },
            ], elastic: true),
            Entity("pb_reading", 10020, OwnershipTypes.UserOwned, "pb_name",
            [
                new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200 },
                new IntegerAttributeMetadata { LogicalName = "pb_value", SchemaName = "pb_Value" },
                new MemoAttributeMetadata { LogicalName = "pb_notes", SchemaName = "pb_Notes", MaxLength = 4000 },
                new StringAttributeMetadata
                {
                    LogicalName = "pb_payload", SchemaName = "pb_Payload", MaxLength = 4000, FormatName = StringFormatName.Json,
                },
                new StringAttributeMetadata
                {
                    LogicalName = "pb_secret", SchemaName = "pb_Secret", MaxLength = 4000, FormatName = StringFormatName.Json, IsSecured = true,
                },
            ], elastic: true),
            Entity("pb_project", 10000, OwnershipTypes.UserOwned, "pb_name",
            [
                new StringAttributeMetadata { LogicalName = "pb_name", SchemaName = "pb_Name", MaxLength = 200 },
                new MoneyAttributeMetadata { LogicalName = "pb_budget", SchemaName = "pb_Budget" },
                new LookupAttributeMetadata { LogicalName = "pb_customerid", SchemaName = "pb_CustomerId", Targets = ["systemuser"] },
                new DateTimeAttributeMetadata { LogicalName = "pb_startdate", SchemaName = "pb_StartDate" },
                new IntegerAttributeMetadata { LogicalName = "pb_count", SchemaName = "pb_Count" },
                new DoubleAttributeMetadata { LogicalName = "pb_ratio", SchemaName = "pb_Ratio" },
                new PicklistAttributeMetadata { LogicalName = "pb_priority", SchemaName = "pb_Priority" },
                // Plan 3.5 UI-1a final review: a choice with labels (ordered by label) and a Customer and a party list lookup.
                Stage(),
                Flag(),
                TypedLookup("pb_partnerid", AttributeTypeCode.Customer, "account", "systemuser"),
                TypedLookup("pb_parties", AttributeTypeCode.PartyList, "account", "systemuser"),
            ]),
            // Plan 2.11: a note's objectid (managed polymorphic lookup) and its EntityName companion objecttypecode.
            Entity("annotation", 5, OwnershipTypes.UserOwned, "subject",
            [
                new StringAttributeMetadata { LogicalName = "subject", SchemaName = "Subject", MaxLength = 500 },
                new LookupAttributeMetadata { LogicalName = "objectid", SchemaName = "ObjectId", Targets = [] },
                ObjectTypeCode(),
            ]),
            // Plan 2.12a: calculated columns (SourceType 1, no column of their own). The definitions come from the test's
            // IComputedColumns; these are the columns' types only.
            Entity("xrmv_calcparent", 10030, OwnershipTypes.UserOwned, "xrmv_pname",
            [
                new StringAttributeMetadata { LogicalName = "xrmv_pname", SchemaName = "xrmv_PName", MaxLength = 100 },
                new MoneyAttributeMetadata { LogicalName = "xrmv_credit", SchemaName = "xrmv_Credit" },
                Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_pcalc", SchemaName = "xrmv_PCalc", PrecisionSource = 0, Precision = 2 }),
            ]),
            Entity("xrmv_calcorder", 10031, OwnershipTypes.UserOwned, "xrmv_name",
            [
                new StringAttributeMetadata { LogicalName = "xrmv_name", SchemaName = "xrmv_Name", MaxLength = 100 },
                new MoneyAttributeMetadata { LogicalName = "xrmv_amount", SchemaName = "xrmv_Amount" },
                new IntegerAttributeMetadata { LogicalName = "xrmv_quantity", SchemaName = "xrmv_Quantity" },
                new DateTimeAttributeMetadata { LogicalName = "xrmv_orderdate", SchemaName = "xrmv_OrderDate" },
                new BooleanAttributeMetadata { LogicalName = "xrmv_rush", SchemaName = "xrmv_Rush" },
                new PicklistAttributeMetadata { LogicalName = "xrmv_status", SchemaName = "xrmv_Status" },
                new LookupAttributeMetadata { LogicalName = "xrmv_accountid", SchemaName = "xrmv_AccountId", Targets = ["account"] },
                new LookupAttributeMetadata { LogicalName = "xrmv_parentid", SchemaName = "xrmv_ParentId", Targets = ["xrmv_calcparent"] },
                Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_total", SchemaName = "xrmv_Total", PrecisionSource = 0, Precision = 2 }),
                Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_totalcur", SchemaName = "xrmv_TotalCur", PrecisionSource = 2 }),
                Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_totalpricing", SchemaName = "xrmv_TotalPricing", PrecisionSource = 1 }),
                Calculated(new MoneyAttributeMetadata { LogicalName = "xrmv_fromparent", SchemaName = "xrmv_FromParent", PrecisionSource = 0, Precision = 2 }),
                Calculated(new StringAttributeMetadata { LogicalName = "xrmv_label", SchemaName = "xrmv_Label", MaxLength = 200 }),
                Calculated(new StringAttributeMetadata { LogicalName = "xrmv_code", SchemaName = "xrmv_Code", MaxLength = 100 }),
                Calculated(new DateTimeAttributeMetadata { LogicalName = "xrmv_followup", SchemaName = "xrmv_Followup" }),
                Calculated(new DateTimeAttributeMetadata
                {
                    LogicalName = "xrmv_shipday", SchemaName = "xrmv_ShipDay", DateTimeBehavior = DateTimeBehavior.DateOnly,
                }),
                Calculated(new IntegerAttributeMetadata { LogicalName = "xrmv_age", SchemaName = "xrmv_Age" }),
                Calculated(new BooleanAttributeMetadata { LogicalName = "xrmv_isbig", SchemaName = "xrmv_IsBig" }),
                Calculated(new PicklistAttributeMetadata { LogicalName = "xrmv_tier", SchemaName = "xrmv_Tier" }),
                Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_double", SchemaName = "xrmv_Double", Precision = 2 }),
                Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_ratio", SchemaName = "xrmv_Ratio", Precision = 10 }),
            ]),
            // Plan 2.12b: rollup columns (SourceType 2, stored) of xrmv_rollacct over its hierarchy and over xrmv_rollorder. The
            // definitions are given to RollupSql by the tests; these are the columns' types only.
            Entity("xrmv_rollacct", 10033, OwnershipTypes.UserOwned, "xrmv_name",
            [
                new StringAttributeMetadata { LogicalName = "xrmv_name", SchemaName = "xrmv_Name", MaxLength = 100 },
                new LookupAttributeMetadata { LogicalName = "xrmv_parentid", SchemaName = "xrmv_ParentId", Targets = ["xrmv_rollacct"] },
                new MoneyAttributeMetadata { LogicalName = "xrmv_budget", SchemaName = "xrmv_Budget" },
                new StringAttributeMetadata { LogicalName = "xrmv_region", SchemaName = "xrmv_Region", MaxLength = 100 },
                Rollup(new DecimalAttributeMetadata { LogicalName = "xrmv_total", SchemaName = "xrmv_Total", Precision = 2 }),
                Rollup(new IntegerAttributeMetadata { LogicalName = "xrmv_count", SchemaName = "xrmv_Count" }),
                Rollup(new MoneyAttributeMetadata { LogicalName = "xrmv_pricetotal", SchemaName = "xrmv_PriceTotal", PrecisionSource = 2 }),
                Rollup(new MoneyAttributeMetadata { LogicalName = "xrmv_tree", SchemaName = "xrmv_Tree", PrecisionSource = 0, Precision = 2 }),
                Rollup(new DateTimeAttributeMetadata { LogicalName = "xrmv_last", SchemaName = "xrmv_Last" }),
            ]),
            Entity("xrmv_rollorder", 10034, OwnershipTypes.UserOwned, "xrmv_code",
            [
                new StringAttributeMetadata { LogicalName = "xrmv_code", SchemaName = "xrmv_Code", MaxLength = 100 },
                new DecimalAttributeMetadata { LogicalName = "xrmv_amount", SchemaName = "xrmv_Amount", Precision = 4 },
                new IntegerAttributeMetadata { LogicalName = "xrmv_qty", SchemaName = "xrmv_Qty" },
                new MoneyAttributeMetadata { LogicalName = "xrmv_price", SchemaName = "xrmv_Price" },
                new PicklistAttributeMetadata { LogicalName = "xrmv_status", SchemaName = "xrmv_Status" },
                new LookupAttributeMetadata { LogicalName = "xrmv_acctid", SchemaName = "xrmv_AcctId", Targets = ["xrmv_rollacct"] },
                new DateTimeAttributeMetadata { LogicalName = "xrmv_on", SchemaName = "xrmv_On" },
                Calculated(new DecimalAttributeMetadata { LogicalName = "xrmv_basic", SchemaName = "xrmv_Basic", Precision = 2 }),
            ]),
            // Plan 2.12c: formula columns (SourceType 3) of xrmv_fxorder, one per result type; the definitions are given by the tests.
            Entity("xrmv_fxorder", 10035, OwnershipTypes.UserOwned, "xrmv_code",
            [
                new StringAttributeMetadata { LogicalName = "xrmv_code", SchemaName = "xrmv_Code", MaxLength = 100 },
                new IntegerAttributeMetadata { LogicalName = "xrmv_qty", SchemaName = "xrmv_Qty" },
                new DecimalAttributeMetadata { LogicalName = "xrmv_amount", SchemaName = "xrmv_Amount", Precision = 2 },
                new DoubleAttributeMetadata { LogicalName = "xrmv_ratio", SchemaName = "xrmv_Ratio" },
                new DateTimeAttributeMetadata
                {
                    LogicalName = "xrmv_duedate", SchemaName = "xrmv_DueDate", DateTimeBehavior = DateTimeBehavior.TimeZoneIndependent,
                },
                new DateTimeAttributeMetadata { LogicalName = "xrmv_startedon", SchemaName = "xrmv_StartedOn" },
                Formula(new StringAttributeMetadata { LogicalName = "xrmv_fxtext", SchemaName = "xrmv_FxText", MaxLength = 4000 }),
                Formula(new IntegerAttributeMetadata { LogicalName = "xrmv_fxint", SchemaName = "xrmv_FxInt" }),
                Formula(new DecimalAttributeMetadata { LogicalName = "xrmv_fxdec", SchemaName = "xrmv_FxDec", Precision = 2 }),
                Formula(new DoubleAttributeMetadata { LogicalName = "xrmv_fxfloat", SchemaName = "xrmv_FxFloat" }),
                Formula(new BooleanAttributeMetadata { LogicalName = "xrmv_fxbool", SchemaName = "xrmv_FxBool" }),
                Formula(new DateTimeAttributeMetadata { LogicalName = "xrmv_fxdate", SchemaName = "xrmv_FxDate" }),
                .. Enumerable.Range(1, 11).Select(i =>
                    (AttributeMetadata)Formula(new DecimalAttributeMetadata { LogicalName = $"xrmv_fxchain{i}", SchemaName = $"xrmv_FxChain{i}" })),
            ]),
            Entity("xrmv_many", 10032, OwnershipTypes.UserOwned, "xrmv_name",
            [
                new StringAttributeMetadata { LogicalName = "xrmv_name", SchemaName = "xrmv_Name", MaxLength = 100 },
                .. Enumerable.Range(1, 51).Select(i =>
                    (AttributeMetadata)Calculated(new IntegerAttributeMetadata { LogicalName = $"xrmv_m{i}", SchemaName = $"xrmv_M{i}" })),
            ]),
        ];
    }

    private static T Calculated<T>(T attribute) where T : AttributeMetadata
    {
        attribute.SourceType = 1;
        return attribute;
    }

    private static T Formula<T>(T attribute) where T : AttributeMetadata
    {
        attribute.SourceType = 3;
        return attribute;
    }

    private static T Rollup<T>(T attribute) where T : AttributeMetadata
    {
        attribute.SourceType = 2;
        return attribute;
    }

    private static Label En(string text) => new(new LocalizedLabel(text, 1033), new[] { new LocalizedLabel(text, 1033) });

    /// <summary><c>pb_stage</c>: values 1 Zulu, 2 Alpha, 3 Mike (label order differs from value order).</summary>
    private static PicklistAttributeMetadata Stage() => new()
    {
        LogicalName = "pb_stage", SchemaName = "pb_Stage",
        OptionSet = new OptionSetMetadata([new OptionMetadata(En("Zulu"), 1), new OptionMetadata(En("Alpha"), 2), new OptionMetadata(En("Mike"), 3)]),
    };

    /// <summary><c>pb_flag</c>: true "Active", false "Paused" (the true label sorts first).</summary>
    private static BooleanAttributeMetadata Flag() => new()
    {
        LogicalName = "pb_flag", SchemaName = "pb_Flag",
        OptionSet = new BooleanOptionSetMetadata(new OptionMetadata(En("Active"), 1), new OptionMetadata(En("Paused"), 0)),
    };

    private static LookupAttributeMetadata TypedLookup(string name, AttributeTypeCode type, params string[] targets)
    {
        var a = new LookupAttributeMetadata { LogicalName = name, SchemaName = name, Targets = targets };
        Set(a, nameof(AttributeMetadata.AttributeType), (AttributeTypeCode?)type);
        return a;
    }

    private static EntityNameAttributeMetadata ObjectTypeCode()
    {
        var a = new EntityNameAttributeMetadata("ObjectTypeCode") { LogicalName = "objecttypecode" };
        Set(a, nameof(AttributeMetadata.AttributeOf), "objectid");
        return a;
    }

    public static AttributeMetadata Attribute(string entity, string attribute) =>
        Instance.GetEntity(entity)!.Attributes.Single(a => a.LogicalName == attribute);

    private static EntityMetadata Entity(string name, int otc, OwnershipTypes ownership, string primaryName, AttributeMetadata[] attrs, bool elastic = false)
    {
        var e = new EntityMetadata { LogicalName = name, SchemaName = name, EntitySetName = name + "s", OwnershipType = ownership };
        if (elastic) e.TableType = "Elastic";
        Set(e, nameof(EntityMetadata.PrimaryIdAttribute), name + "id");
        Set(e, nameof(EntityMetadata.PrimaryNameAttribute), primaryName);
        Set(e, nameof(EntityMetadata.ObjectTypeCode), (int?)otc);
        foreach (var a in attrs) Set(a, nameof(AttributeMetadata.EntityLogicalName), name);
        e.SetAttributes(SystemAttributes.Complete(e, attrs));
        return e;
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
    public IReadOnlyList<OneToManyRelationship> GetOneToManyDefinitions() => OneToManyFixtures().Select(r => (OneToManyRelationship)SdkMetadataView.ModelOf(r)).ToList();

    private static IReadOnlyList<OneToManyRelationshipMetadata> OneToManyFixtures() =>
    [
        new OneToManyRelationshipMetadata
        {
            SchemaName = "account_parent_account", ReferencingEntity = "account", ReferencedEntity = "account",
            ReferencingAttribute = "parentaccountid", ReferencedAttribute = "accountid", IsHierarchical = true,
        },
        new OneToManyRelationshipMetadata
        {
            SchemaName = "xrmv_rollacct_parent", ReferencingEntity = "xrmv_rollacct", ReferencedEntity = "xrmv_rollacct",
            ReferencingAttribute = "xrmv_parentid", ReferencedAttribute = "xrmv_rollacctid", IsHierarchical = true,
        },
        new OneToManyRelationshipMetadata
        {
            SchemaName = "xrmv_rollorder_acct", ReferencingEntity = "xrmv_rollorder", ReferencedEntity = "xrmv_rollacct",
            ReferencingAttribute = "xrmv_acctid", ReferencedAttribute = "xrmv_rollacctid",
        },
    ];
    public Task CreateOneToManyAsync(DataSession session, OneToManyRelationship relationship, LookupColumn lookup,
        CancellationToken ct) => throw new NotSupportedException();
    public Task CreateCustomerRelationshipsAsync(DataSession session, LookupColumn lookup,
        IReadOnlyList<OneToManyRelationship> relationships, CancellationToken ct) => throw new NotSupportedException();
    public Task CreateManyToManyAsync(DataSession session, ManyToManyRelationship relationship, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task DeleteRelationshipAsync(DataSession session, string schemaName, CancellationToken ct) =>
        throw new NotSupportedException();
}

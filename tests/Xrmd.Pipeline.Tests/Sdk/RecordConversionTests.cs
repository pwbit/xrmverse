using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Query;
using Xrmd.Model.Records;

namespace Xrmd.Pipeline.Tests.Sdk;

public class RecordConversionTests
{
    private static void AssertParity<TSdk, TModel>() where TSdk : struct, Enum where TModel : struct, Enum
    {
        Assert.Equal(Enum.GetNames<TSdk>().OrderBy(n => n, StringComparer.Ordinal), Enum.GetNames<TModel>().OrderBy(n => n, StringComparer.Ordinal));
        foreach (var name in Enum.GetNames<TSdk>())
            Assert.Equal(Convert.ToInt32(Enum.Parse<TSdk>(name)), Convert.ToInt32(Enum.Parse<TModel>(name)));
    }

    [Fact] public void RecordRole_matches_EntityRole() => AssertParity<EntityRole, RecordRole>();
    [Fact] public void RecordState_matches_EntityState() => AssertParity<EntityState, RecordState>();

    internal static Entity KitchenSink(BooleanManagedProperty unknown)
    {
        var inner = new Entity("contact", Guid.NewGuid()) { ["fullname"] = "Inner", ["balance"] = new Money(1.5m) };
        var e = new Entity("account", Guid.NewGuid())
        {
            RowVersion = "12345",
            EntityState = EntityState.Changed,
        };
        e["name"] = "Contoso";
        e["count"] = 7;
        e["when"] = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        e["choice"] = new OptionSetValue(3);
        e["choices"] = new OptionSetValueCollection { new OptionSetValue(1), new OptionSetValue(2) };
        e["revenue"] = new Money(99.95m);
        e["owner"] = new EntityReference("systemuser", Guid.NewGuid()) { Name = "Me", RowVersion = "9" };
        e["nested"] = inner;
        e["page"] = new EntityCollection(new List<Entity> { inner }) { EntityName = "contact", MoreRecords = true };
        e["parties"] = new EntityReferenceCollection { new EntityReference("contact", Guid.NewGuid()), new EntityReference("account", Guid.NewGuid()) };
        e["aliased"] = new AliasedValue("contact", "parent", new EntityCollection(new List<Entity> { inner }));
        e["aliasedmoney"] = new AliasedValue("c", "m", new Money(2m));
        e["refs"] = new[] { new EntityReference("a", Guid.NewGuid()), new EntityReference("b", Guid.NewGuid()) };
        e["entities"] = new[] { inner };
        e["mixed"] = new object[] { 1, "x", new OptionSetValue(5), new Money(3m) };
        e["bytes"] = new byte[] { 1, 2, 3 };
        e["nothing"] = null;
        e["managed"] = unknown;
        e.FormattedValues["choice"] = "Three";
        e.KeyAttributes["accountnumber"] = "A-1";
        e.KeyAttributes["code"] = 42;
        e.RelatedEntities[new Relationship("contact_customer_accounts") { PrimaryEntityRole = EntityRole.Referenced }] =
            new EntityCollection(new List<Entity> { inner }) { EntityName = "contact" };
        e.RelatedEntities[new Relationship("other")] = new EntityCollection();
        return e;
    }

    [Fact]
    public void KitchenSink_round_trips_deep_equal_and_unknown_value_keeps_its_instance()
    {
        var unknown = new BooleanManagedProperty(true) { CanBeChanged = false };
        var sdk = KitchenSink(unknown);

        var model = sdk.ToModel();
        Assert.Same(unknown, model["managed"]);
        Assert.IsType<ChoiceValue>(model["choice"]);
        Assert.IsType<ChoiceValues>(model["choices"]);
        Assert.Equal(new MoneyValue(99.95m), model["revenue"]);
        Assert.IsType<RecordReference>(model["owner"]);
        Assert.IsType<Xrmd.Model.Records.Record>(model["nested"]);
        Assert.IsType<RecordCollection>(model["page"]);
        Assert.IsType<RecordReferenceCollection>(model["parties"]);
        Assert.IsType<AliasedColumnValue>(model["aliased"]);
        Assert.IsType<RecordReference[]>(model["refs"]);
        Assert.IsType<Xrmd.Model.Records.Record[]>(model["entities"]);
        Assert.IsType<object[]>(model["mixed"]);
        Assert.Equal(RecordState.Changed, model.State);
        Assert.Equal("12345", model.RowVersion);
        Assert.Equal("Three", model.FormattedValues["choice"]);
        Assert.Equal(42, model.KeyAttributes["code"]);
        Assert.Equal(2, model.RelatedRecords.Count);
        Assert.Contains(new RelationshipName("contact_customer_accounts", RecordRole.Referenced), model.RelatedRecords.Keys);

        var back = model.ToSdk();
        Assert.Same(unknown, back["managed"]);
        DeepEqual.Assert(sdk, back);
    }

    [Fact]
    public void Model_and_sdk_objects_share_no_mutable_collections()
    {
        var sdk = new Entity("account", Guid.NewGuid()) { ["name"] = "a" };
        var model = sdk.ToModel();
        model["name"] = "changed";
        Assert.Equal("a", sdk["name"]);
        var back = model.ToSdk();
        back["name"] = "again";
        Assert.Equal("changed", model["name"]);
    }

    [Fact]
    public void Paged_EntityCollection_round_trips()
    {
        var sdk = new EntityCollection(new List<Entity> { new Entity("account", Guid.NewGuid()) { ["name"] = "x" } })
        {
            EntityName = "account",
            MoreRecords = true,
            PagingCookie = "<cookie page=\"2\" />",
            TotalRecordCount = 5000,
            TotalRecordCountLimitExceeded = true,
            MinActiveRowVersion = "777",
        };
        var model = sdk.ToModel();
        Assert.Equal("<cookie page=\"2\" />", model.PagingCookie);
        Assert.Equal(5000, model.TotalRecordCount);
        Assert.True(model.TotalRecordCountLimitExceeded);
        Assert.Equal("777", model.MinActiveRowVersion);
        DeepEqual.Assert(sdk, model.ToSdk());
    }

    [Fact]
    public void ColumnSet_and_Relationship_round_trip()
    {
        var cs = new ColumnSet("name", "revenue");
        DeepEqual.Assert(cs, cs.ToModel().ToSdk());
        var all = new ColumnSet(true);
        DeepEqual.Assert(all, all.ToModel().ToSdk());
        var rel = new Relationship("a_b") { PrimaryEntityRole = EntityRole.Referencing };
        DeepEqual.Assert(rel, rel.ToModel().ToSdk());
        var rel2 = new Relationship("a_b");
        Assert.Null(rel2.ToModel().PrimaryEntityRole);
        DeepEqual.Assert(rel2, rel2.ToModel().ToSdk());
    }

    [Fact]
    public void EntityReference_with_keys_round_trips()
    {
        var r = new EntityReference("account", new KeyAttributeCollection { { "accountnumber", "A-1" } }) { Name = "n", RowVersion = "3" };
        DeepEqual.Assert(r, r.ToModel().ToSdk());
    }

    [Fact]
    public void Other_arrays_pass_through_as_the_same_instance()
    {
        var strings = new[] { "a", "b" };
        var unknown = new[] { new BooleanManagedProperty(true) };
        var meta = new object[0];
        foreach (var v in new object[] { strings, unknown })
        {
            Assert.Same(v, SdkRecords.ValueToModel(v));
            Assert.Same(v, SdkRecords.ValueToSdk(v));
        }
        var mixed = (object[])SdkRecords.ValueToModel(new object[] { new OptionSetValue(5), "x" })!;
        Assert.Equal(new ChoiceValue(5), mixed[0]);
        Assert.Equal(typeof(object[]), SdkRecords.ValueToSdk(meta)!.GetType());
    }

    [Fact]
    public void ColumnSet_with_AttributeExpressions_round_trips()
    {
        var plain = new ColumnSet("name");
        Assert.IsType<ColumnSelection>(SdkRecords.ValueToModel(plain));
        var withExpr = new ColumnSet("name");
        withExpr.AttributeExpressions.Add(new XrmAttributeExpression("revenue", XrmAggregateType.Sum, "total"));
        var model = Assert.IsType<ColumnSelection>(SdkRecords.ValueToModel(withExpr));
        var expression = Assert.Single(model.Expressions);
        Assert.Equal("revenue", expression.AttributeName);
        Assert.Equal(ColumnAggregate.Sum, expression.Aggregate);
        var back = Assert.IsType<ColumnSet>(SdkRecords.ValueToSdk(model));
        Assert.NotSame(withExpr, back);
        DeepEqual.Assert(withExpr, back);
    }
}

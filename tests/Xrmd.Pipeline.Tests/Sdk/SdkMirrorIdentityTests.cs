using System.Runtime.CompilerServices;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>Identity cases of the mirror: instance sharing on the SDK side, pass-through parity, lifetime, nulls and graph identity.</summary>
public class SdkMirrorIdentityTests
{
    [Fact]
    public void One_SDK_value_in_two_attributes_stays_one_object_and_follows_in_place_changes()
    {
        var mirror = new SdkMirror();
        var os = new OptionSetValue(1);
        var e = new Entity("account", Guid.NewGuid()) { ["a"] = os, ["b"] = os };

        var record = (Record)mirror.In(e)!;
        Assert.Same(record["a"], record["b"]);
        mirror.Out(record);
        Assert.Same(os, e["a"]);
        Assert.Same(os, e["b"]);

        os.Value = 9; // a plug-in changes the shared object in place
        mirror.In(e);
        Assert.Equal(new ChoiceValue(9), record["a"]);
        Assert.Equal(new ChoiceValue(9), record["b"]);
        mirror.Out(record);
        Assert.Same(os, e["a"]);
        Assert.Same(os, e["b"]);
        Assert.Equal(9, os.Value);
    }

    [Fact]
    public void One_Money_under_two_entities_of_a_bag_and_a_condition_value_stay_shared()
    {
        var mirror = new SdkMirror();
        var money = new Money(5m);
        var os = new OptionSetValue(3);
        var query = new QueryExpression("account");
        query.Criteria.AddCondition("statuscode", ConditionOperator.Equal, os);
        var bag = new ParameterCollection
        {
            ["A"] = new Entity("account", Guid.NewGuid()) { ["m"] = money, ["s"] = os },
            ["B"] = new Entity("account", Guid.NewGuid()) { ["m"] = money },
            ["Query"] = query,
        };

        var model = mirror.In(bag)!;
        mirror.Out(model);
        Assert.Same(money, ((Entity)bag["A"])["m"]);
        Assert.Same(money, ((Entity)bag["B"])["m"]);
        Assert.Same(os, ((Entity)bag["A"])["s"]);
        Assert.Same(os, query.Criteria.Conditions[0].Values[0]);

        money.Value = 7m;
        os.Value = 4;
        mirror.In(bag);
        mirror.Out(model);
        Assert.Equal(7m, money.Value);
        Assert.Same(os, query.Criteria.Conditions[0].Values[0]);
        Assert.Equal(4, os.Value);
    }

    [Fact]
    public void CreateMultiple_targets_sharing_one_option_value_keep_sharing()
    {
        var mirror = new SdkMirror();
        var active = new OptionSetValue(1);
        var targets = new EntityCollection(Enumerable.Range(0, 5).Select(i => new Entity("account") { ["name"] = "n" + i, ["statuscode"] = active }).ToList());
        var request = new CreateMultipleRequest { Targets = targets };

        var model = mirror.In(request)!;
        mirror.Out(model);
        active.Value = 2; // a plug-in changes Targets[0] in place; today all rows change
        mirror.In(request);
        var records = ((RecordCollection)model["Targets"]!).Records;
        Assert.All(records, r => Assert.Equal(new ChoiceValue(2), r["statuscode"]));
        Assert.Single(records.Select(r => r["statuscode"]).Distinct(ReferenceEqualityComparer.Instance));

        mirror.Out(model);
        Assert.All(targets.Entities, t => Assert.Same(active, t["statuscode"]));
        Assert.Equal(2, active.Value);
    }

    [Fact]
    public void Sharing_the_model_creates_gets_a_fresh_SDK_value_also_across_calls()
    {
        var mirror = new SdkMirror();
        var target = new Entity("account", Guid.NewGuid()) { ["a"] = new OptionSetValue(1), ["b"] = new OptionSetValue(1) };
        var record = (Record)mirror.In(target)!;
        var original = (OptionSetValue)target["a"];
        Assert.NotSame(target["a"], target["b"]);

        record["b"] = record["a"]; // the model shares one value that the SDK side did not share
        mirror.Out(record);
        Assert.Same(original, target["a"]);
        Assert.NotSame(target["a"], target["b"]);

        // an image built from the same model value and lent in a separate Out is a copy
        var image = new Record("account", record.Id) { ["a"] = record["a"] };
        var sdkImage = (Entity)mirror.Out(image)!;
        Assert.NotSame(original, sdkImage["a"]);
        ((OptionSetValue)sdkImage["a"]).Value = 8;
        Assert.Equal(1, original.Value);
    }

    [Fact]
    public void Values_the_converters_pass_through_are_their_own_twin()
    {
        var mirror = new SdkMirror();
        var inner = new CreateRequest { Target = new Entity("account") };
        var request = new OrganizationRequest("Mixed")
        {
            ["Async"] = inner,
            ["Paging"] = new PagingInfo { Count = 5 },
            ["PageInfo"] = new PagingInfo { PageNumber = 2 },
            ["Response"] = new OrganizationResponse { Results = { ["x"] = 1 } },
            ["Bag"] = new ParameterCollection { ["k"] = "v" },
            ["Filter"] = new FilterExpression(),
            ["Condition"] = new ConditionExpression("a", ConditionOperator.Equal, 1),
            ["Link"] = new LinkEntity(),
            ["Order"] = new OrderExpression("a", OrderType.Ascending),
            ["Expr"] = new XrmAttributeExpression("a"),
            ["Known"] = new Entity("account"),
            ["Choice"] = new OptionSetValue(1),
            ["Managed"] = new BooleanManagedProperty(true),
            ["Reference"] = new EntityReference("account", Guid.NewGuid()),
            ["Query"] = new QueryExpression("account"),
            ["Fetch"] = new FetchExpression("<fetch/>"),
        };
        var expected = request.ToModel();
        var model = mirror.In(request)!;

        Assert.Equal(expected.Parameters.Keys, model.Parameters.Keys);
        foreach (var key in expected.Parameters.Keys)
            Assert.Equal(expected.Parameters[key]?.GetType(), model.Parameters[key]?.GetType());
        Assert.Same(inner, model.Parameters["Async"]);
        Assert.IsType<PagingInfo>(model.Parameters["Paging"]);

        var back = mirror.Out(model)!;
        Assert.Same(request, back);
        Assert.Same(inner, back["Async"]);
        Assert.Same(request["Paging"], back["Paging"]);
        Assert.Same(request["Filter"], back["Filter"]);

        var entity = new Entity("account") { ["f"] = new FilterExpression(), ["p"] = new PagingInfo() };
        var record = (Record)mirror.In(entity)!;
        Assert.IsType<FilterExpression>(record["f"]);
        mirror.Out(record);
        Assert.IsType<FilterExpression>(entity["f"]);
        _ = record.ToSdkViaConverter(); // model objects never leak into an SDK container through the converter either
    }

    [Fact]
    public void Query_parts_still_twin_as_children_and_at_the_top_level()
    {
        var mirror = new SdkMirror();
        var filter = new FilterExpression();
        Assert.IsType<QueryFilter>(mirror.In(filter));
        Assert.Same(filter, mirror.Out(mirror.In(filter)));
        var paging = new PagingInfo { Count = 3 };
        Assert.IsType<QueryPaging>(mirror.In(paging));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference, WeakReference, WeakReference, WeakReference) CrossNested(SdkMirror mirror)
    {
        var request = new CreateRequest { Target = new Entity("account", Guid.NewGuid()) { ["name"] = "n" } };
        var response = new CreateResponse { Results = { ["id"] = Guid.NewGuid(), ["Entity"] = new Entity("account") } };
        var modelRequest = mirror.In(request)!;
        var modelResponse = mirror.In((OrganizationResponse)response);
        mirror.Out(modelRequest);
        mirror.Out(modelResponse);
        return (new WeakReference(request), new WeakReference(response), new WeakReference(modelRequest), new WeakReference(modelResponse));
    }

    [Fact]
    public void Objects_of_a_finished_nested_call_can_be_collected_while_the_mirror_lives()
    {
        var mirror = new SdkMirror();
        var (request, response, modelRequest, modelResponse) = CrossNested(mirror);
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(request.IsAlive);
        Assert.False(response.IsAlive);
        Assert.False(modelRequest.IsAlive);
        Assert.False(modelResponse.IsAlive);
        GC.KeepAlive(mirror);
    }

    [Fact]
    public void Null_elements_throw_in_the_converters_as_before()
    {
        Assert.Equal("entity", Assert.Throws<ArgumentNullException>(() => new EntityCollection { Entities = { null! } }.ToModel()).ParamName);
        Assert.Equal("reference", Assert.Throws<ArgumentNullException>(() => new EntityReferenceCollection { null! }.ToModel()).ParamName);
        var withNullRelated = new Entity("a");
        withNullRelated.RelatedEntities[new Relationship("r")] = null!;
        Assert.Equal("collection", Assert.Throws<ArgumentNullException>(() => withNullRelated.ToModel()).ParamName);
        Assert.Equal("record", Assert.Throws<ArgumentNullException>(() => new RecordCollection { Records = { null! } }.ToSdk()).ParamName);
        Assert.Equal("reference", Assert.Throws<ArgumentNullException>(() => new RecordReferenceCollection { null! }.ToSdk()).ParamName);
    }

    [Fact]
    public void Nullable_typed_overloads_return_null_for_null()
    {
        var mirror = new SdkMirror();
        Assert.Null(mirror.Out((ParameterBag?)null));
        Assert.Null(mirror.In((ParameterCollection?)null));
        Assert.Null(mirror.Out((Request?)null));
        Assert.Null(mirror.In((OrganizationRequest?)null));
        Assert.Null(mirror.Out((Response?)null));
        Assert.Null(mirror.In((OrganizationResponse?)null));
    }

    [Fact]
    public void Reference_graph_identity_and_metadata_round_trip()
    {
        var mirror = new SdkMirror();
        var reference = new EntityReference("account", new KeyAttributeCollection { { "number", "A1" } }) { Name = "n", RowVersion = "rv" };
        var related = new EntityCollection(new List<Entity> { new("contact", Guid.NewGuid()) }) { EntityName = "contact", MoreRecords = true, PagingCookie = "c", TotalRecordCount = 4, TotalRecordCountLimitExceeded = true, MinActiveRowVersion = "m", RecordCountLessThanMarv = 2 };
        var key = new Relationship("rel") { PrimaryEntityRole = EntityRole.Referenced };
        var entity = new Entity("account", Guid.NewGuid()) { RowVersion = "ev", EntityState = EntityState.Changed, ["ref"] = reference };
        entity.FormattedValues["f"] = "formatted";
        entity.KeyAttributes["number"] = "A1";
        entity.RelatedEntities[key] = related;

        var record = (Record)mirror.In(entity)!;
        Assert.Equal("rv", ((RecordReference)record["ref"]!).RowVersion);
        Assert.Equal("A1", ((RecordReference)record["ref"]!).KeyAttributes["number"]);
        Assert.Equal(RecordState.Changed, record.State);
        mirror.Out(record);

        Assert.Same(reference, entity["ref"]);
        Assert.Equal("n", reference.Name);
        Assert.Equal("rv", reference.RowVersion);
        Assert.Equal("A1", reference.KeyAttributes["number"]);
        Assert.Equal("formatted", entity.FormattedValues["f"]);
        Assert.Equal("A1", entity.KeyAttributes["number"]);
        Assert.Equal("ev", entity.RowVersion);
        Assert.Equal(EntityState.Changed, entity.EntityState);
        Assert.Same(related, entity.RelatedEntities[key]);
        Assert.Same(key, entity.RelatedEntities.Keys.Single());
        Assert.Equal(["contact", "c", "m"], new[] { related.EntityName, related.PagingCookie, related.MinActiveRowVersion });
        Assert.True(related.MoreRecords);
        Assert.True(related.TotalRecordCountLimitExceeded);
        Assert.Equal(4, related.TotalRecordCount);
        Assert.Equal(2, related.RecordCountLessThanMarv);
    }

    [Fact]
    public void Query_parts_fetch_and_typed_request_properties_keep_identity()
    {
        var mirror = new SdkMirror();
        var link = new LinkEntity("account", "contact", "pid", "pid", JoinOperator.Inner) { Columns = new ColumnSet("x"), EntityAlias = "c" };
        var query = new QueryExpression("account") { ColumnSet = new ColumnSet("name"), PageInfo = new PagingInfo { Count = 2 } };
        query.LinkEntities.Add(link);
        query.Orders.Add(new OrderExpression("name", OrderType.Descending));
        query.Criteria.AddCondition("a", ConditionOperator.Equal, 1);
        var columns = query.ColumnSet;
        var criteria = query.Criteria;
        var condition = criteria.Conditions[0];
        var order = query.Orders[0];
        var page = query.PageInfo;
        var fetch = new FetchExpression("<fetch a='1'/>");
        var request = new RetrieveMultipleRequest { Query = query };

        var model = mirror.In(request)!;
        ((RecordQuery)model.Parameters["Query"]!).Criteria!.Conditions[0].Values[0] = 2;
        mirror.Out(model);
        Assert.Same(query, request.Query);
        Assert.Same(columns, query.ColumnSet);
        Assert.Same(criteria, query.Criteria);
        Assert.Same(condition, criteria.Conditions[0]);
        Assert.Equal(2, condition.Values[0]);
        Assert.Same(order, query.Orders[0]);
        Assert.Same(page, query.PageInfo);
        Assert.Same(link, query.LinkEntities[0]);
        Assert.Equal("c", link.EntityAlias);

        var fetchModel = (FetchQuery)mirror.In(fetch)!;
        fetch.Query = "<fetch b='2'/>";
        mirror.In(fetch);
        Assert.Equal("<fetch b='2'/>", fetchModel.Text);
        fetchModel.Text = "<fetch c='3'/>";
        Assert.Same(fetch, mirror.Out(fetchModel));
        Assert.Equal("<fetch c='3'/>", fetch.Query);
    }

    [Fact]
    public void Removed_parameter_disappears_and_key_order_follows_the_source()
    {
        var mirror = new SdkMirror();
        var request = new UpdateRequest { Target = new Entity("account", Guid.NewGuid()) { ["a"] = 1, ["b"] = 2, ["c"] = 3 } };
        request["Extra"] = 1;
        var model = mirror.In(request)!;

        request.Parameters.Remove("Extra");
        var target = request.Target;
        target.Attributes.Remove("a");
        target["a"] = 10; // remove then add: the SDK's own enumeration order decides
        var order = target.Attributes.Select(x => x.Key).ToArray();
        mirror.In(request);
        Assert.False(model.Parameters.ContainsKey("Extra"));
        mirror.Out(model);
        Assert.False(request.Parameters.ContainsKey("Extra"));
        Assert.Same(target, request.Target);
        Assert.Equal(order, target.Attributes.Select(x => x.Key).ToArray());
        Assert.Equal(10, target["a"]);
    }
}

internal static class RecordTestExtensions
{
    public static Entity ToSdkViaConverter(this Record record) => record.ToSdk();
}

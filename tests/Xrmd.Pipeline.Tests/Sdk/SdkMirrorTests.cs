using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using Record = Xrmd.Model.Records.Record;

namespace Xrmd.Pipeline.Tests.Sdk;

public class SdkMirrorTests
{
    /// <summary>Stands in for an early-bound entity class: a typed Entity subclass that keeps its CLR type.</summary>
    private sealed class EarlyBoundAccount : Entity
    {
        public EarlyBoundAccount() : base("account") { }
    }

    /// <summary>Stands in for a typed response subclass.</summary>
    private sealed class TypedResponse : OrganizationResponse;

    /// <summary>Shaped like the core's RetrieveMultiple request that carries a front-end query beside a placeholder.</summary>
    private sealed class PlaceholderRequest : OrganizationRequest
    {
        public PlaceholderRequest()
        {
            RequestName = "RetrieveMultiple";
            Placeholder = new QueryExpression("account") { ColumnSet = new ColumnSet(true) };
            Parameters["Query"] = Placeholder;
        }

        public QueryExpression Placeholder { get; }

        public bool UsesModel => Parameters.TryGetValue("Query", out var q) && ReferenceEquals(q, Placeholder);
    }

    [Fact]
    public void Round_trip_returns_the_same_instance()
    {
        var mirror = new SdkMirror();
        object[] samples =
        [
            new Entity("account", Guid.NewGuid()) { ["name"] = "x" },
            new EntityReference("account", Guid.NewGuid()) { Name = "n" },
            new EntityCollection(new List<Entity> { new("account", Guid.NewGuid()) }) { EntityName = "account", MoreRecords = true },
            new ColumnSet("a", "b"),
            new OptionSetValue(3),
            new Money(1.5m),
            new QueryExpression("account") { ColumnSet = new ColumnSet("name"), Criteria = { Conditions = { new ConditionExpression("a", ConditionOperator.Equal, 1) } } },
            new CreateRequest { Target = new Entity("account") { ["name"] = "t" } },
            new RetrieveResponse { Results = { ["Entity"] = new Entity("account", Guid.NewGuid()) } },
        ];
        foreach (var sdk in samples)
        {
            var model = mirror.In(sdk)!;
            Assert.NotNull(model);
            Assert.NotSame(sdk, model);
            Assert.Same(sdk, mirror.Out(model));
            Assert.Same(model, mirror.In(sdk));
            Assert.Equal(sdk.GetType(), mirror.Out(model)!.GetType());
        }
    }

    [Fact]
    public void In_writes_into_the_existing_twin()
    {
        var mirror = new SdkMirror();
        var choice = new OptionSetValue(1);
        var query = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
        var entity = new Entity("account", Guid.NewGuid()) { ["name"] = "a", ["gone"] = 1, ["choice"] = choice, ["query"] = query };

        var record = (Record)mirror.In(entity)!;
        Assert.Equal(new ChoiceValue(1), record["choice"]);

        entity["added"] = "new";
        entity.Attributes.Remove("gone");
        choice.Value = 7;
        query.Criteria.Conditions.Add(new ConditionExpression("x", ConditionOperator.Equal, "y"));

        Assert.Same(record, mirror.In(entity));
        Assert.Equal("new", record["added"]);
        Assert.False(record.Contains("gone"));
        Assert.Equal(new ChoiceValue(7), record["choice"]);
        var modelQuery = Assert.IsType<RecordQuery>(record["query"]);
        Assert.Single(modelQuery.Criteria!.Conditions);

        // the model side changes; Out writes into the very same SDK instances
        record["name"] = "b";
        record.Attributes.Remove("added");
        modelQuery.Criteria.Conditions.Clear();

        Assert.Same(entity, mirror.Out(record));
        Assert.Equal("b", entity["name"]);
        Assert.False(entity.Contains("added"));
        Assert.Same(choice, entity["choice"]); // unchanged model value: the original SDK instance, with its current state
        Assert.Equal(7, choice.Value);
        Assert.Same(query, entity["query"]);
        Assert.Empty(query.Criteria.Conditions);

        // a replaced model value is a replaced attribute, as when a step assigns a new option value today
        record["choice"] = new ChoiceValue(9);
        mirror.Out(record);
        Assert.NotSame(choice, entity["choice"]);
        Assert.Equal(9, ((OptionSetValue)entity["choice"]).Value);
    }

    [Fact]
    public void Out_keeps_containers_and_refills_in_source_order()
    {
        var mirror = new SdkMirror();
        var entity = new Entity("account", Guid.NewGuid()) { ["z"] = 1, ["a"] = 2 };
        var attributes = entity.Attributes;
        var record = (Record)mirror.In(entity)!;

        record.Attributes.Clear();
        record["m"] = 1;
        record["b"] = 2;
        record["k"] = 3;
        mirror.Out(record);

        Assert.Same(attributes, entity.Attributes);
        Assert.Equal(["m", "b", "k"], entity.Attributes.Select(a => a.Key).ToArray());
    }

    [Fact]
    public void Same_value_twice_maps_to_one_twin()
    {
        var mirror = new SdkMirror();
        var shared = new Entity("contact", Guid.NewGuid()) { ["fullname"] = "c" };
        var bag = new ParameterCollection { ["A"] = shared, ["B"] = shared, ["C"] = new EntityCollection(new List<Entity> { shared }) };

        var model = mirror.In(bag)!;
        var a = (Record)model["A"]!;
        Assert.Same(a, model["B"]);
        Assert.Same(a, ((RecordCollection)model["C"]!).Records.Single());

        var back = mirror.Out(model)!;
        Assert.Same(bag, back);
        Assert.Same(shared, back["A"]);
        Assert.Same(shared, back["B"]);
        Assert.Same(shared, ((EntityCollection)back["C"]).Entities.Single());

        // a model object seen for the first time gets one plain twin, also when shared
        var fresh = new Record("account", Guid.NewGuid());
        var newBag = new ParameterBag { ["X"] = fresh, ["Y"] = fresh };
        var sdk = mirror.Out(newBag)!;
        Assert.Same(sdk["X"], sdk["Y"]);
        Assert.Equal(typeof(Entity), sdk["X"].GetType());
    }

    [Fact]
    public void Pass_through_keeps_the_instance()
    {
        var mirror = new SdkMirror();
        var managed = new BooleanManagedProperty(true);
        var ints = new[] { 1, 2, 3 };
        var bytes = new byte[] { 1 };
        var early = new EarlyBoundAccount { Id = Guid.NewGuid() };
        early["name"] = "typed";
        var request = new OrganizationRequest("Custom") { ["Target"] = early, ["Ints"] = ints, ["Bytes"] = bytes, ["Managed"] = managed };

        var model = mirror.In(request)!;
        Assert.Same(ints, model.Parameters["Ints"]);
        Assert.Same(managed, model.Parameters["Managed"]);
        Assert.Same(bytes, model.Parameters["Bytes"]);
        var record = Assert.IsType<Record>(model.Parameters["Target"]);

        record["name"] = "changed";
        var back = mirror.Out(model)!;
        Assert.Same(request, back);
        Assert.Same(early, back["Target"]);
        Assert.IsType<EarlyBoundAccount>(back["Target"]);
        Assert.Equal("changed", early["name"]);
        Assert.Same(ints, back["Ints"]);
        Assert.Same(managed, back["Managed"]);
        Assert.Same(bytes, back["Bytes"]);
    }

    [Fact]
    public void Request_subclass_and_marks_survive()
    {
        var mirror = new SdkMirror();
        var request = new UpdateRequest { Target = new Entity("account", Guid.NewGuid()) { ["name"] = "n" } };
        AuditActionHint.Mark(request, 42);
        CascadeHint.Mark(request);

        var model = mirror.In(request)!;
        model.Parameters["Extra"] = 1;
        var back = mirror.Out(model)!;

        Assert.Same(request, back);
        Assert.IsType<UpdateRequest>(back);
        Assert.Equal(42, AuditActionHint.Of(back));
        Assert.True(CascadeHint.IsMarked(back));
        Assert.Equal(1, back["Extra"]);

        var typed = new TypedResponse { Results = { ["Name"] = "r" } };
        var typedModel = mirror.In(typed)!;
        typedModel.Results["Name"] = "s";
        Assert.Same(typed, mirror.Out(typedModel));
        Assert.Equal("s", typed["Name"]);
    }

    [Fact]
    public void Placeholder_query_keeps_its_identity_until_replaced()
    {
        var mirror = new SdkMirror();
        var request = new PlaceholderRequest();
        var model = mirror.In(request)!;

        mirror.Out(model);
        Assert.True(request.UsesModel);

        model.Parameters["Query"] = new RecordQuery { EntityName = "contact" };
        mirror.Out(model);
        Assert.False(request.UsesModel);
        Assert.Equal("contact", ((QueryExpression)request["Query"]).EntityName);
    }

    [Fact]
    public void Cycle_maps_to_a_cycle()
    {
        var mirror = new SdkMirror();
        var entity = new Entity("account", Guid.NewGuid());
        var collection = new EntityCollection(new List<Entity> { entity });
        entity["children"] = collection;

        var record = (Record)mirror.In(entity)!;
        var modelCollection = (RecordCollection)record["children"]!;
        Assert.Same(record, modelCollection.Records.Single());

        Assert.Same(entity, mirror.Out(record));
        Assert.Same(collection, entity["children"]);
        Assert.Same(entity, collection.Entities.Single());

        var model = new Record("contact", Guid.NewGuid());
        var modelBag = new RecordCollection { Records = { model } };
        model["me"] = modelBag;
        var sdk = (Entity)mirror.Out(model)!;
        Assert.Same(sdk, ((EntityCollection)sdk["me"]).Entities.Single());
    }

    [Fact]
    public void Model_values_replaced_in_place_do_not_alias_in_the_SDK()
    {
        var mirror = new SdkMirror();
        var entity = new Entity("account", Guid.NewGuid()) { ["a"] = new OptionSetValue(1) };
        var record = (Record)mirror.In(entity)!;
        var original = (OptionSetValue)entity["a"];

        var same = (ChoiceValue)record["a"]!;
        record["b"] = same; // the immutable model value is shared by two attributes
        mirror.Out(record);

        Assert.Same(original, entity["a"]);
        Assert.NotSame(entity["a"], entity["b"]);
        ((OptionSetValue)entity["b"]).Value = 5;
        Assert.Equal(1, original.Value);
    }

    [Fact]
    public void SyncCount_counts_written_objects()
    {
        var mirror = new SdkMirror();
        var plain = new Entity("account", Guid.NewGuid()) { ["name"] = "a", ["count"] = 3, ["flag"] = true };
        var record = (Record)mirror.In(plain)!;
        Assert.Equal(1, mirror.SyncCount);
        mirror.Out(record);
        Assert.Equal(2, mirror.SyncCount);
        mirror.In(plain);
        Assert.Equal(3, mirror.SyncCount);

        var rich = new SdkMirror();
        var target = new Entity("account", Guid.NewGuid()) { ["name"] = "a", ["choice"] = new OptionSetValue(1), ["money"] = new Money(2m) };
        var model = (Record)rich.In(target)!; // the entity, the option value, the money
        Assert.Equal(3, rich.SyncCount);
        rich.Out(model);
        Assert.Equal(6, rich.SyncCount);

        // an object reachable twice in one crossing is written once
        var shared = new Entity("contact", Guid.NewGuid());
        var twice = new SdkMirror();
        twice.In(new ParameterCollection { ["A"] = shared, ["B"] = shared });
        Assert.Equal(2, twice.SyncCount);
    }

    [Fact]
    public void Arrays_keep_identity_and_known_element_types_convert()
    {
        var mirror = new SdkMirror();
        var entities = new[] { new Entity("a", Guid.NewGuid()), new Entity("b", Guid.NewGuid()) };
        var objects = new object[] { "x", new OptionSetValue(2), entities[0] };
        var strings = new[] { "s" };
        var request = new OrganizationRequest("R") { ["E"] = entities, ["O"] = objects, ["S"] = strings };

        var model = mirror.In(request)!;
        var modelEntities = Assert.IsType<Record[]>(model.Parameters["E"]);
        Assert.Equal(["a", "b"], modelEntities.Select(r => r.LogicalName).ToArray());
        Assert.Same(strings, model.Parameters["S"]);
        var modelObjects = Assert.IsType<object[]>(model.Parameters["O"]);
        Assert.Equal(new ChoiceValue(2), modelObjects[1]);
        Assert.Same(modelEntities[0], modelObjects[2]);

        modelEntities[0].LogicalName = "changed";
        mirror.Out(model);
        Assert.Same(entities, request["E"]);
        Assert.Same(objects, request["O"]);
        Assert.Equal("changed", entities[0].LogicalName);
        Assert.Same(entities[0], objects[2]);
    }

    [Fact]
    public void Concurrent_use_of_one_mirror_is_serialized()
    {
        var mirror = new SdkMirror();
        var entity = new Entity("account", Guid.NewGuid()) { ["n"] = 0 };
        var record = (Record)mirror.In(entity)!;
        Parallel.For(0, 200, i =>
        {
            if (i % 2 == 0) mirror.In(entity);
            else mirror.Out(record);
        });
        Assert.Same(entity, mirror.Out(record));
        Assert.Equal(200 + 1 + 1, mirror.SyncCount);
    }

    [Fact]
    public void Pairs_can_be_registered_and_queried()
    {
        var mirror = new SdkMirror();
        var record = new Record("account", Guid.NewGuid());
        var entity = new Entity("account");
        mirror.Pair(record, entity);

        Assert.True(mirror.TryGetSdk(record, out var sdk));
        Assert.Same(entity, sdk);
        Assert.True(mirror.TryGetModel(entity, out var model));
        Assert.Same(record, model);
        Assert.Same(entity, mirror.Out(record));
        Assert.Equal(record.Id, entity.Id);
        Assert.False(mirror.TryGetSdk(new Record(), out _));
    }
}

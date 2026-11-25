using System.Runtime.Serialization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;

namespace Xrmd.Pipeline.Tests.Sdk;

public class RemoteContextConversionTests
{
    private sealed class EarlyBound : Entity
    {
        public EarlyBound() : base("account") { }
    }

    [Fact]
    public void Golden_context_round_trips_deep_equal()
    {
        var context = RemoteContextGoldenTests.Build();
        var model = context.ToModel();
        var back = model.ToSdk();
        DeepEqual.Assert(context, back);
        Assert.NotSame(context, back);
        Assert.NotNull(model.ParentContext);
        Assert.Equal(context.MessageName, model.MessageName);
        Assert.Equal(["pre"], model.PreEntityImages.Keys.ToArray());
    }

    [Fact]
    public void Model_context_converts_a_value_to_its_model_form()
    {
        var context = new RemoteExecutionContext();
        context.InputParameters["m"] = new Money(2m);
        var model = context.ToModel();
        Assert.IsType<Xrmd.Model.Records.MoneyValue>(model.InputParameters["m"]);
    }

    [Fact]
    public void Golden_context_can_be_carried() => Assert.True(SdkRemoteContext.ModelCanCarry(RemoteContextGoldenTests.Build()));

    [Fact]
    public void Plain_context_can_be_carried() => Assert.True(SdkRemoteContext.ModelCanCarry(new RemoteExecutionContext()));

    [Fact]
    public void Managed_property_shared_variable_is_outside()
    {
        var c = RemoteContextGoldenTests.Build();
        c.SharedVariables["m"] = new BooleanManagedProperty(true);
        Assert.False(SdkRemoteContext.ModelCanCarry(c));
    }

    [Fact]
    public void Object_array_attribute_is_outside()
    {
        var c = RemoteContextGoldenTests.Build();
        c.InputParameters["Target"] = new Entity("account") { ["a"] = new object[] { 1, "x" } };
        Assert.False(SdkRemoteContext.ModelCanCarry(c));
    }

    [Fact]
    public void Early_bound_entity_image_is_outside()
    {
        var c = RemoteContextGoldenTests.Build();
        c.PreEntityImages["x"] = new EarlyBound();
        Assert.False(SdkRemoteContext.ModelCanCarry(c));
    }

    [Fact]
    public void Int_array_input_parameter_is_outside()
    {
        var c = RemoteContextGoldenTests.Build();
        c.InputParameters["ints"] = new[] { 1, 2 };
        Assert.False(SdkRemoteContext.ModelCanCarry(c));
    }

    [Fact]
    public void Outside_value_in_the_parent_chain_is_outside()
    {
        var c = RemoteContextGoldenTests.Build();
        c.ParentContext.SharedVariables["m"] = new BooleanManagedProperty(true);
        Assert.False(SdkRemoteContext.ModelCanCarry(c));
    }

    [Fact]
    public void Outside_value_nested_in_an_aliased_value_is_outside()
    {
        var c = RemoteContextGoldenTests.Build();
        c.InputParameters["a"] = new AliasedValue("x", "y", new BooleanManagedProperty(true));
        Assert.False(SdkRemoteContext.ModelCanCarry(c));
    }

    [Fact]
    public void Query_with_a_foreign_condition_value_is_outside()
    {
        var q = new QueryExpression("account");
        q.Criteria.AddCondition("a", ConditionOperator.Equal, new BooleanManagedProperty(true));
        Assert.False(SdkRemoteContext.ModelCanCarry(q));
        var link = new LinkEntity("account", "contact", "a", "b", JoinOperator.Inner);
        link.LinkCriteria.AddCondition("a", ConditionOperator.Equal, new BooleanManagedProperty(true));
        var q2 = new QueryExpression("account");
        q2.LinkEntities.Add(link);
        Assert.False(SdkRemoteContext.ModelCanCarry(q2));
        var byAttribute = new QueryByAttribute("account");
        byAttribute.Attributes.Add("a");
        byAttribute.Values.Add(new[] { 1 });
        Assert.False(SdkRemoteContext.ModelCanCarry(byAttribute));
    }

    [Fact]
    public void Query_with_model_values_can_be_carried()
    {
        var q = new QueryExpression("account");
        q.Criteria.AddCondition("a", ConditionOperator.Equal, new EntityReference("x", Guid.NewGuid()));
        q.Criteria.AddCondition("b", ConditionOperator.Equal, "s");
        Assert.True(SdkRemoteContext.ModelCanCarry(q));
        Assert.True(SdkRemoteContext.ModelCanCarry(new FetchExpression("<fetch/>")));
    }

    private static bool CarryParameter(object? value)
    {
        var c = new RemoteExecutionContext();
        c.InputParameters["p"] = value;
        return SdkRemoteContext.ModelCanCarry(c);
    }

    private static readonly Guid G = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Top_level_null_can_be_carried() => Assert.True(CarryParameter(null));

    [Fact]
    public void Null_element_in_a_typed_array_is_outside()
    {
        Assert.False(CarryParameter(new Entity?[] { null }));
        Assert.False(CarryParameter(new EntityReference?[] { null }));
        Assert.False(CarryParameter(new OptionSetValue?[] { null }));
        Assert.False(CarryParameter(new Money?[] { null }));
    }

    [Fact]
    public void Null_element_in_a_collection_is_outside()
    {
        var entities = new EntityCollection();
        entities.Entities.Add(null!);
        Assert.False(CarryParameter(entities));
        var references = new EntityReferenceCollection { null! };
        Assert.False(CarryParameter(references));
        var choices = new OptionSetValueCollection { null! };
        Assert.False(CarryParameter(choices));
    }

    [Fact]
    public void Null_image_is_outside()
    {
        var c = new RemoteExecutionContext();
        c.PreEntityImages["x"] = null!;
        Assert.False(SdkRemoteContext.ModelCanCarry(c));
        var d = new RemoteExecutionContext();
        d.PostEntityImages["x"] = null!;
        Assert.False(SdkRemoteContext.ModelCanCarry(d));
    }

    [Fact]
    public void Null_related_entities_value_is_outside()
    {
        var e = new Entity("account");
        e.RelatedEntities[new Relationship("r")] = null!;
        Assert.False(CarryParameter(e));
    }

    [Fact]
    public void Outside_value_in_key_attributes_is_outside()
    {
        var reference = new EntityReference("account", new KeyAttributeCollection { { "k", new[] { 1 } } });
        Assert.False(CarryParameter(reference));
        var e = new Entity("account");
        e.KeyAttributes["k"] = new[] { 1 };
        Assert.False(CarryParameter(e));
    }

    [Fact]
    public void Outside_value_in_related_entities_is_outside()
    {
        var related = new Entity("contact") { ["a"] = new BooleanManagedProperty(true) };
        var e = new Entity("account");
        e.RelatedEntities[new Relationship("r")] = new EntityCollection(new List<Entity> { related });
        Assert.False(CarryParameter(e));
    }

    [Fact]
    public void Early_bound_entity_in_a_collection_is_outside()
    {
        Assert.False(CarryParameter(new EntityCollection(new List<Entity> { new EarlyBound() })));
        Assert.False(CarryParameter(new[] { new EarlyBound() }.Select(x => (Entity)x).ToArray()));
    }

    [Fact]
    public void Outside_value_in_the_any_all_link_entity_is_outside()
    {
        var anyAll = new LinkEntity("account", "task", "a", "b", JoinOperator.Any);
        anyAll.LinkCriteria.AddCondition("a", ConditionOperator.Equal, new BooleanManagedProperty(true));
        var q = new QueryExpression("account");
        q.Criteria.AnyAllFilterLinkEntity = anyAll;
        Assert.False(SdkRemoteContext.ModelCanCarry(q));
    }

    [Fact]
    public void Outside_value_in_the_subquery_is_outside()
    {
        var sub = new QueryExpression("contact");
        sub.Criteria.AddCondition("a", ConditionOperator.Equal, new BooleanManagedProperty(true));
        var q = new QueryExpression("account") { SubQueryExpression = sub };
        Assert.False(SdkRemoteContext.ModelCanCarry(q));
    }

    [Fact]
    public void Outside_value_in_the_owning_extension_is_outside()
    {
        var c = new RemoteExecutionContext
        {
            OwningExtension = new EntityReference("x", new KeyAttributeCollection { { "k", new[] { 1 } } }),
        };
        Assert.False(SdkRemoteContext.ModelCanCarry(c));
    }

    // Today's query serializer throws ("Invalid name character in 'Money[]'") on a typed array of a model type directly as a
    // condition or QueryByAttribute value, so such a query stays on the SDK path (where it keeps throwing).
    [Theory]
    [InlineData("entities")]
    [InlineData("references")]
    [InlineData("choices")]
    [InlineData("moneys")]
    public void Typed_array_query_value_is_outside(string kind)
    {
        object value = kind switch
        {
            "entities" => new[] { new Entity("a") },
            "references" => new[] { new EntityReference("a", Guid.NewGuid()) },
            "choices" => new[] { new OptionSetValue(1) },
            _ => new[] { new Money(1m) },
        };
        var expression = new QueryExpression("a");
        expression.Criteria.Conditions.Add(new ConditionExpression("x", ConditionOperator.Equal, new[] { value }));
        Assert.False(SdkRemoteContext.ModelCanCarry(expression));

        var byAttribute = new QueryByAttribute("a");
        byAttribute.Values.Add(value);
        Assert.False(SdkRemoteContext.ModelCanCarry(byAttribute));

        var link = new QueryExpression("a");
        var l = new LinkEntity("a", "b", "x", "y", JoinOperator.Inner);
        l.LinkCriteria.Conditions.Add(new ConditionExpression("x", ConditionOperator.Equal, new[] { value }));
        link.LinkEntities.Add(l);
        Assert.False(SdkRemoteContext.ModelCanCarry(link));
    }

    [Fact]
    public void Typed_array_inside_an_entity_query_value_is_carried()
    {
        var expression = new QueryExpression("a");
        expression.Criteria.Conditions.Add(new ConditionExpression("x", ConditionOperator.Equal,
            new[] { new Entity("a") { ["m"] = new[] { new Money(1m) } } }));
        Assert.True(SdkRemoteContext.ModelCanCarry(expression));
    }

    private static IEnumerable<(string Name, object Array)> TypedArrays()
    {
        yield return ("entities", new[] { new Entity("a") });
        yield return ("entities-empty", Array.Empty<Entity>());
        yield return ("references", new[] { new EntityReference("a", Guid.NewGuid()) });
        yield return ("references-empty", Array.Empty<EntityReference>());
        yield return ("choices", new[] { new OptionSetValue(1) });
        yield return ("choices-empty", Array.Empty<OptionSetValue>());
        yield return ("moneys", new[] { new Money(1m) });
        yield return ("moneys-empty", Array.Empty<Money>());
    }

    private static QueryExpression Cond(object value)
    {
        var q = new QueryExpression("a");
        q.Criteria.Conditions.Add(new ConditionExpression("x", ConditionOperator.Equal, new[] { value }));
        return q;
    }

    private static EntityReference Keyed(object value) => new("a", new KeyAttributeCollection { { "k", value } });

    private static Entity EntityKeyed(object value)
    {
        var e = new Entity("a", Guid.NewGuid());
        e.KeyAttributes["k"] = value;
        return e;
    }

    // The positions where today's query serializer (query style) throws on a typed array of a model type.
    private static IEnumerable<(string Name, QueryBase Query)> ThrowingShapes()
    {
        foreach (var (name, array) in TypedArrays())
        {
            yield return ($"reference-key/{name}", Cond(Keyed(array)));
            yield return ($"entity-key/{name}", Cond(EntityKeyed(array)));
            yield return ($"collection-entity-key/{name}", Cond(new EntityCollection(new List<Entity> { EntityKeyed(array) })));
            yield return ($"reference-in-reference-key/{name}", Cond(Keyed(Keyed(array))));
            if (name.StartsWith("entities")) yield return ($"reference-key-in-aliased/{name}", Cond(new AliasedValue("e", "a", Keyed(array))));
            var byAttribute = new QueryByAttribute("a");
            byAttribute.Values.Add(Keyed(array));
            yield return ($"by-attribute-reference-key/{name}", byAttribute);
            var link = new QueryExpression("a");
            var l = new LinkEntity("a", "b", "x", "y", JoinOperator.Inner);
            l.LinkCriteria.Conditions.Add(new ConditionExpression("x", ConditionOperator.Equal, new[] { (object)Keyed(array) }));
            link.LinkEntities.Add(l);
            yield return ($"link-reference-key/{name}", link);
        }
        yield return ("aliased-entities", Cond(new AliasedValue("e", "a", new[] { new Entity("a") })));
        yield return ("aliased-entities-empty", Cond(new AliasedValue("e", "a", Array.Empty<Entity>())));
        yield return ("aliased-in-aliased-entities", Cond(new AliasedValue("e", "a", new AliasedValue("e", "b", new[] { new Entity("a") }))));
    }

    [Fact]
    public void Typed_array_in_a_query_style_position_is_outside()
    {
        foreach (var (name, query) in ThrowingShapes())
        {
            Assert.True(Record.Exception(() => SdkReferenceSerializers.SerializeQuery(query)) is not null, $"{name}: the SDK no longer throws");
            Assert.False(SdkRemoteContext.ModelCanCarry(query), name);
        }
    }

    // Typed arrays that serialize today stay carried: in the attributes of an entity, in an aliased value (other than Entity[]),
    // and in a query nested in a context (the context serializer knows them).
    [Fact]
    public void Typed_array_in_a_context_style_position_is_carried()
    {
        foreach (var (name, array) in TypedArrays())
        {
            var inAttribute = Cond(new Entity("a") { ["m"] = array });
            Assert.True(SdkRemoteContext.ModelCanCarry(inAttribute), name);
            Assert.Null(Record.Exception(() => SdkReferenceSerializers.SerializeQuery(inAttribute)));

            var context = new RemoteExecutionContext();
            context.InputParameters["q"] = Cond(array);
            context.InputParameters["k"] = Keyed(array);
            Assert.True(SdkRemoteContext.ModelCanCarry(context), name);
            Assert.Null(Record.Exception(() => SdkReferenceSerializers.SerializeContext(context)));

            if (!name.StartsWith("entities"))
            {
                var aliased = Cond(new AliasedValue("e", "a", array));
                Assert.True(SdkRemoteContext.ModelCanCarry(aliased), name);
                Assert.Null(Record.Exception(() => SdkReferenceSerializers.SerializeQuery(aliased)));
                var aliasedKey = Cond(new AliasedValue("e", "a", Keyed(array)));
                Assert.True(SdkRemoteContext.ModelCanCarry(aliasedKey), name);
                Assert.Null(Record.Exception(() => SdkReferenceSerializers.SerializeQuery(aliasedKey)));
            }
        }
    }

    [Fact]
    public void Record_count_less_than_marv_round_trips()
    {
        var c = new RemoteExecutionContext();
        c.OutputParameters["page"] = new EntityCollection { RecordCountLessThanMarv = 5 };
        Assert.True(SdkRemoteContext.ModelCanCarry(c));
        DeepEqual.Assert(c, c.ToModel().ToSdk());
    }

    // ExtensionData is not carried by the model: a graph holding any stays on the SDK path.
    [Fact]
    public void Extension_data_is_outside()
    {
        var context = RemoteContextGoldenTests.Build();
        Assert.True(SdkRemoteContext.ModelCanCarry(context));
        var xml = SdkReferenceSerializers.SerializeContext(context);
        foreach (var tag in new[] { "</RemoteExecutionContext>", "</ParentContext>", "</OwningExtension>", "<EntityState i:nil=\"true\" />", "</AliasedValue>" })
        {
            if (!xml.Contains(tag)) continue;
            var read = SdkReferenceSerializers.DeserializeContext(xml.Replace(tag, "<UnknownMember>1</UnknownMember>" + tag));
            Assert.False(SdkRemoteContext.ModelCanCarry(read), tag);
        }

        // Elements the XML does not end with a closing tag of their own (entities, references, money, choice values).
        var insertions = new (string Anchor, string Description)[]
        {
            ("<LogicalName>account</LogicalName>", "entity or reference"),
            ("<Value>99.95</Value>", "money"),
            ("<Value>3</Value>", "option set value"),
            ("<TotalRecordCount>17</TotalRecordCount>", "collection"),
            ("<SchemaName>contact_customer_accounts</SchemaName>", "relationship"),
            ("<AllColumns>false</AllColumns>", "column set"),
            ("<NeedFormatting>false</NeedFormatting>", "aliased value"),
        };
        foreach (var (anchor, description) in insertions)
        {
            Assert.Contains(anchor, xml);
            var read = SdkReferenceSerializers.DeserializeContext(xml.Replace(anchor, "<UnknownMember>1</UnknownMember>" + anchor));
            Assert.False(SdkRemoteContext.ModelCanCarry(read), description);
        }
    }

    [Fact]
    public void Extension_data_in_a_query_is_outside()
    {
        var query = RemoteContextGoldenTests.Query();
        Assert.True(SdkRemoteContext.ModelCanCarry(query));
        var xml = SdkReferenceSerializers.SerializeQuery(query);
        var anchors = new[]
        {
            "<AllColumns>false</AllColumns>",               // column set
            "<AggregateType>Sum</AggregateType>",           // attribute expression
            "<CompareColumns>false</CompareColumns>",       // condition
            "<FilterOperator>Or</FilterOperator>",          // filter
            "<EntityAlias>pc</EntityAlias>",                // link entity
            "<OrderType>Ascending</OrderType>",             // order
            "<PageNumber>2</PageNumber>",                   // paging
            "<Distinct>true</Distinct>",                    // the query itself
            "<Value>5</Value>",                             // money value
        };
        foreach (var anchor in anchors)
        {
            Assert.Contains(anchor, xml);
            var read = SdkReferenceSerializers.DeserializeQuery(xml.Replace(anchor, "<UnknownMember>1</UnknownMember>" + anchor));
            Assert.False(SdkRemoteContext.ModelCanCarry(read), anchor);
        }
        var byAttribute = SdkReferenceSerializers.SerializeQuery(RemoteContextGoldenTests.ByAttribute());
        Assert.False(SdkRemoteContext.ModelCanCarry(SdkReferenceSerializers.DeserializeQuery(
            byAttribute.Replace("<TopCount>3</TopCount>", "<UnknownMember>1</UnknownMember><TopCount>3</TopCount>"))));
        var fetch = SdkReferenceSerializers.SerializeQuery(new FetchExpression("<fetch />"));
        Assert.False(SdkRemoteContext.ModelCanCarry(SdkReferenceSerializers.DeserializeQuery(
            fetch.Replace("<Query>", "<UnknownMember>1</UnknownMember><Query>"))));
    }

    // Gaps of the first ExtensionData gate: members of a QueryByAttribute and the expressions of a ColumnSet passed as a value.
    // The deserializer marks many objects of the graph for one unknown member, so each test clears every ExtensionData except
    // the one of the member under test: the gate must find it there.
    private static void Clear(object value) => ((IExtensibleDataObject)value).ExtensionData = null!;

    [Fact]
    public void Extension_data_in_a_query_by_attribute_member_is_outside()
    {
        var xml = SdkReferenceSerializers.SerializeQuery(RemoteContextGoldenTests.ByAttribute());
        var anchors = new (string Anchor, Func<QueryByAttribute, object> Member)[]
        {
            ("<AllColumns>false</AllColumns>", q => q.ColumnSet),
            ("<Count>10</Count>", q => q.PageInfo),
            ("<OrderType>Ascending</OrderType>", q => q.Orders[0]),
        };
        foreach (var (anchor, member) in anchors)
        {
            Assert.Contains(anchor, xml);
            var read = (QueryByAttribute)SdkReferenceSerializers.DeserializeQuery(xml.Replace(anchor, "<UnknownMember>1</UnknownMember>" + anchor));
            var target = member(read);
            Assert.NotNull(((IExtensibleDataObject)target).ExtensionData);
            foreach (var other in new object[] { read, read.ColumnSet, read.PageInfo, read.Orders[0], read.Values[0]! })
                if (!ReferenceEquals(other, target)) Clear(other);
            Assert.False(SdkRemoteContext.ModelCanCarry(read), anchor);
        }
    }

    [Fact]
    public void Extension_data_in_an_attribute_expression_of_a_column_set_value_is_outside()
    {
        var columns = new ColumnSet("name");
        columns.AttributeExpressions.Add(new XrmAttributeExpression("revenue", XrmAggregateType.Sum, "total"));
        var context = new RemoteExecutionContext();
        context.InputParameters["columns"] = columns;
        Assert.True(SdkRemoteContext.ModelCanCarry(context));
        var xml = SdkReferenceSerializers.SerializeContext(context);
        const string anchor = "<AggregateType>Sum</AggregateType>";
        Assert.Contains(anchor, xml);
        var read = SdkReferenceSerializers.DeserializeContext(xml.Replace(anchor, "<UnknownMember>1</UnknownMember>" + anchor));
        var readColumns = (ColumnSet)read.InputParameters["columns"];
        Assert.NotNull(((IExtensibleDataObject)readColumns.AttributeExpressions[0]).ExtensionData);
        Clear(readColumns);
        Clear(read);
        Assert.False(SdkRemoteContext.ModelCanCarry(read));
    }
}

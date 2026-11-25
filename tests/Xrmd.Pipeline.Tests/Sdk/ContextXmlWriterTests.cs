using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Serialization;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// The own XML writer of the remote context and of queries against the SDK serializer: the golden files and a corpus (every
/// value of the model set in every place a context carries one, the Review Focus values, the query kitchen sinks) must come out
/// byte for byte as <see cref="SdkReferenceSerializers"/> writes them.
/// </summary>
public class ContextXmlWriterTests
{
    private static readonly string[] ContextNames = ContextCorpus.ContextNames;
    private static readonly string[] QueryNames = ContextCorpus.QueryNames;

    public static IEnumerable<object[]> Contexts() => ContextNames.Select(n => new object[] { n });

    public static IEnumerable<object[]> Queries() => QueryNames.Select(n => new object[] { n });

    private static string GoldenDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tests", "fixtures", "remote-context-golden");
    }

    private static string Golden(string file) => new UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(GoldenDirectory(), file)));

    private static void AssertSameBytes(string expected, string actual)
    {
        var utf8 = new UTF8Encoding(false);
        if (utf8.GetBytes(expected).AsSpan().SequenceEqual(utf8.GetBytes(actual))) return;
        var i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i]) i++;
        var from = Math.Max(0, i - 80);
        Assert.Fail($"differs at char {i} (expected length {expected.Length}, actual {actual.Length}){Environment.NewLine}" +
                    $"expected: ...{expected.Substring(from, Math.Min(200, expected.Length - from))}{Environment.NewLine}" +
                    $"actual:   ...{actual.Substring(from, Math.Min(200, actual.Length - from))}");
    }

    // Corpus entries must not share leaf values: a test that mutates one entry cannot change another.
    [Fact]
    public void Corpus_entries_do_not_share_value_instances()
    {
        foreach (var name in new[] { "entity", "reference", "reference-keys", "entity-formatted" })
        {
            var input = ContextCorpus.Context("input/" + name).InputParameters["p"];
            var shared = ContextCorpus.Context("shared/" + name).SharedVariables["p"];
            var output = ContextCorpus.Context("output/" + name).OutputParameters["p"];
            Assert.NotSame(input, shared);
            Assert.NotSame(input, output);
            Assert.NotSame(shared, output);
        }
        var images = ContextCorpus.Context("pre-image/entity").PreEntityImages["pre"];
        var postImages = ContextCorpus.Context("post-image/entity").PostEntityImages["post"];
        Assert.NotSame(images, postImages);
    }

    [Fact]
    public void Golden_context_matches() =>
        AssertSameBytes(Golden("async-context.xml"), ContextXml.Write(RemoteContextGoldenTests.Build().ToModel()));

    [Fact]
    public void Golden_queries_match()
    {
        AssertSameBytes(Golden("query-expression.xml"), QueryXml.Write(RemoteContextGoldenTests.Query().ToModel()));
        AssertSameBytes(Golden("query-by-attribute.xml"), QueryXml.Write(RemoteContextGoldenTests.ByAttribute().ToModel()));
        AssertSameBytes(Golden("fetch.xml"),
            QueryXml.Write(new FetchExpression("<fetch top=\"5\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>").ToModel()));
    }

    [Fact]
    public void Golden_query_can_be_carried() => Assert.True(SdkRemoteContext.ModelCanCarry(RemoteContextGoldenTests.Query()));

    [Fact]
    public void Corpus_names_are_unique()
    {
        Assert.Equal(ContextNames.Length, ContextNames.Distinct().Count());
        Assert.Equal(QueryNames.Length, QueryNames.Distinct().Count());
        Assert.True(ContextNames.Length > 300);
        Assert.True(QueryNames.Length > 150);
    }

    [Theory, MemberData(nameof(Contexts))]
    public void Corpus_matches_the_sdk_serializer(string name)
    {
        var context = ContextCorpus.Context(name);
        Assert.True(SdkRemoteContext.ModelCanCarry(context), "the corpus entry is outside the model set");
        AssertSameBytes(SdkReferenceSerializers.SerializeContext(context), ContextXml.Write(context.ToModel()));
    }

    [Theory, MemberData(nameof(Queries))]
    public void Queries_match_the_sdk_serializer(string name)
    {
        var query = ContextCorpus.Query(name);
        Assert.True(SdkRemoteContext.ModelCanCarry(query), "the corpus entry is outside the model set");
        AssertSameBytes(SdkReferenceSerializers.SerializeQuery(query), QueryXml.Write(query.ToModel()));
    }

    // The bar of the model-set decision: no query that ModelCanCarry accepts may be one today's serializer throws on. Every value
    // of the corpus is put into every position a query can hold one (directly, in key attributes, in aliased values, in entities).
    [Fact]
    public void A_query_the_model_can_carry_never_throws_in_the_sdk()
    {
        static QueryExpression Cond(object? value)
        {
            var q = new QueryExpression("a");
            q.Criteria.Conditions.Add(new ConditionExpression("x", ConditionOperator.Equal, new[] { value }));
            return q;
        }
        static EntityReference Keyed(object? v) => new("a", new KeyAttributeCollection { { "k", v } });
        static Entity EntityKeyed(object? v) { var e = new Entity("a", Guid.NewGuid()); e.KeyAttributes["k"] = v; return e; }

        var wrappers = new List<(string Name, Func<object?, QueryBase> Build)>
        {
            ("direct", v => Cond(v)),
            ("reference-key", v => Cond(Keyed(v))),
            ("entity-key", v => Cond(EntityKeyed(v))),
            ("entity-attribute", v => Cond(new Entity("a") { ["m"] = v })),
            ("collection-entity-key", v => Cond(new EntityCollection(new List<Entity> { EntityKeyed(v) }))),
            ("reference-in-reference-key", v => Cond(Keyed(Keyed(v)))),
            ("aliased", v => Cond(new AliasedValue("e", "a", v))),
            ("aliased-reference-key", v => Cond(new AliasedValue("e", "a", Keyed(v)))),
            ("aliased-in-aliased", v => Cond(new AliasedValue("e", "a", new AliasedValue("e", "b", v)))),
            ("entity-attribute-aliased", v => Cond(new Entity("a") { ["m"] = new AliasedValue("e", "a", v) })),
            ("nested-query", v => Cond(Cond(v))),
            ("by-attribute", v => { var q = new QueryByAttribute("a"); q.Values.Add(v!); return q; }),
            ("by-attribute-reference-key", v => { var q = new QueryByAttribute("a"); q.Values.Add(Keyed(v)); return q; }),
            ("link", v => { var q = new QueryExpression("a"); var l = new LinkEntity("a", "b", "x", "y", JoinOperator.Inner); l.LinkCriteria.AddCondition("x", ConditionOperator.Equal, v!); q.LinkEntities.Add(l); return q; }),
        };
        var problems = new List<string>();
        foreach (var (valueName, value) in ContextCorpus.Values().ToList())
        foreach (var (wrapperName, build) in wrappers)
        {
            var query = build(value);
            if (!SdkRemoteContext.ModelCanCarry(query)) continue;
            try { SdkReferenceSerializers.SerializeQuery(query); }
            catch (Exception ex) { problems.Add($"{wrapperName}/{valueName}: {ex.GetType().Name}"); }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Writer_is_deterministic()
    {
        var model = RemoteContextGoldenTests.Build().ToModel();
        Assert.Equal(ContextXml.Write(model), ContextXml.Write(model));
    }

    [Theory]
    [InlineData("object")]
    [InlineData("float")]
    [InlineData("int-array")]
    [InlineData("uri")]
    public void Outside_the_model_set_throws_and_names_the_type(string kind)
    {
        object value = kind switch { "object" => new object(), "float" => 1.5f, "int-array" => new[] { 1, 2 }, _ => new Uri("http://x/") };
        var context = new RemoteContext();
        context.SharedVariables["v"] = value;
        var ex = Assert.Throws<NotSupportedException>(() => ContextXml.Write(context));
        Assert.Contains(value.GetType().FullName!, ex.Message);

        var query = new RecordQuery();
        query.Criteria!.Conditions.Add(new QueryCondition { AttributeName = "a", Values = { value } });
        var qex = Assert.Throws<NotSupportedException>(() => QueryXml.Write(query));
        Assert.Contains(value.GetType().FullName!, qex.Message);
    }

    // Like the SDK serializer, an enum value without a name is a SerializationException (not a value outside the model set).
    [Theory]
    [InlineData("operator")]
    [InlineData("join")]
    [InlineData("logic")]
    [InlineData("order")]
    [InlineData("aggregate")]
    [InlineData("grouping")]
    public void An_undefined_query_enum_throws_like_the_sdk(string kind)
    {
        var q = new QueryExpression("a");
        switch (kind)
        {
            case "operator": q.Criteria.Conditions.Add(new ConditionExpression("x", (ConditionOperator)999, 1)); break;
            case "join": q.LinkEntities.Add(new LinkEntity("a", "b", "x", "y", (JoinOperator)99)); break;
            case "logic": q.Criteria = new FilterExpression((LogicalOperator)5); break;
            case "order": q.Orders.Add(new OrderExpression("n", (OrderType)5)); break;
            case "aggregate": q.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression("n", (XrmAggregateType)50, "a")); break;
            default: q.ColumnSet.AttributeExpressions.Add(new XrmAttributeExpression("n", XrmAggregateType.None, "a") { DateTimeGrouping = (XrmDateTimeGrouping)50 }); break;
        }
        Assert.Throws<System.Runtime.Serialization.SerializationException>(() => SdkReferenceSerializers.SerializeQuery(q));
        Assert.Throws<System.Runtime.Serialization.SerializationException>(() => QueryXml.Write(q.ToModel()));
    }

    [Theory]
    [InlineData("state")]
    [InlineData("role")]
    public void An_undefined_record_enum_throws_like_the_sdk(string kind)
    {
        var context = new RemoteExecutionContext();
        context.InputParameters["v"] = kind == "state"
            ? new Entity("a") { EntityState = (EntityState)9 }
            : new Relationship("r") { PrimaryEntityRole = (EntityRole)7 };
        Assert.Throws<System.Runtime.Serialization.SerializationException>(() => SdkReferenceSerializers.SerializeContext(context));
        Assert.Throws<System.Runtime.Serialization.SerializationException>(() => ContextXml.Write(context.ToModel()));
    }

    // Characters XML cannot carry fail with the BCL writer's ArgumentException, in the SDK serializer and in the own writer.
    [Theory]
    [InlineData("control")]
    [InlineData("surrogate")]
    public void Characters_xml_cannot_carry_throw_like_the_sdk(string kind)
    {
        var text = kind == "control" ? "a\u0001b" : "a\ud800b";
        var context = new RemoteExecutionContext();
        context.SharedVariables["v"] = text;
        var sdk = Assert.Throws<ArgumentException>(() => SdkReferenceSerializers.SerializeContext(context));
        var own = Assert.Throws<ArgumentException>(() => ContextXml.Write(context.ToModel()));
        Assert.Equal(sdk.Message, own.Message);
    }

    [Fact]
    public void An_unknown_query_definition_throws()
    {
        Assert.Throws<NotSupportedException>(() => QueryXml.Write(new UnknownQuery()));
    }

    private sealed class UnknownQuery : QueryDefinition;
}

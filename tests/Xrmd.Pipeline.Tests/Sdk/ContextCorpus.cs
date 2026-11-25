using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// The differential corpus of S.2e2b (reused by the JSON writer, the reader and the twin tests): contexts and queries whose
/// values are all inside the model set (<c>SdkRemoteContext.ModelCanCarry</c> is asserted by the tests). Every value of the
/// model set appears alone as an input parameter, an output parameter, a shared variable, an attribute of an image, an attribute
/// of a record in a collection and wrapped in an aliased value; plus the Review Focus values (DateTime kinds, floating-point and
/// decimal edges, strings that need escaping, empty and null members), a parent chain, every member of a context at a non-default
/// value and the query kitchen sinks.
/// </summary>
internal static class ContextCorpus
{
    private static readonly Guid G1 = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid G2 = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid G3 = new("aaaaaaaa-0000-0000-0000-000000000003");

    private static Entity Inner() => new("contact", G2) { ["fullname"] = "Inner", ["balance"] = new Money(1.5m) };

    /// <summary>Every value of the model set, one entry each (a fresh instance per call).</summary>
    public static IEnumerable<(string Name, object? Value)> Values()
    {
        yield return ("null", null);
        yield return ("string", "x");
        yield return ("string-empty", "");
        yield return ("string-spaces", "  padded  ");
        yield return ("string-markup", "<a href=\"x\">&amp; 'q' / \\ ]]></a>");
        yield return ("string-lines", "line1\r\nline2\nline3\rline4\ttab");
        yield return ("string-unicode", "caf\u00e9 \u4e2d\u6587 \ud83d\ude00 \u00a0\u2028");
        yield return ("string-prefixlike", "d2p1:key i:type=\"x\"");
        yield return ("bool-true", true);
        yield return ("bool-false", false);
        yield return ("int", 7);
        yield return ("int-min", int.MinValue);
        yield return ("int-max", int.MaxValue);
        yield return ("int-zero", 0);
        yield return ("long", 9007199254740993L);
        yield return ("long-min", long.MinValue);
        yield return ("long-max", long.MaxValue);
        yield return ("double", 0.25d);
        yield return ("double-third", 1d / 3d);
        yield return ("double-nan", double.NaN);
        yield return ("double-pinf", double.PositiveInfinity);
        yield return ("double-ninf", double.NegativeInfinity);
        yield return ("double-negzero", -0.0d);
        yield return ("double-zero", 0.0d);
        yield return ("double-max", double.MaxValue);
        yield return ("double-min", double.MinValue);
        yield return ("double-epsilon", double.Epsilon);
        yield return ("double-large", 1e21d);
        yield return ("double-small", 1.5e-7d);
        yield return ("decimal", 12.5m);
        yield return ("decimal-trailing-zeros", 1.50m);
        yield return ("decimal-zero-scale", 0.000m);
        yield return ("decimal-max", decimal.MaxValue);
        yield return ("decimal-min", decimal.MinValue);
        yield return ("decimal-small", 0.0000000000000000000000000001m);
        yield return ("guid", G3);
        yield return ("guid-empty", Guid.Empty);
        yield return ("datetime-utc", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        yield return ("datetime-utc-ticks", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567));
        yield return ("datetime-utc-millis", new DateTime(2026, 1, 2, 3, 4, 5, 120, DateTimeKind.Utc));
        yield return ("datetime-unspecified", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified));
        yield return ("datetime-unspecified-ticks", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(5));
        yield return ("datetime-local", new DateTime(2026, 7, 2, 3, 4, 5, DateTimeKind.Local));
        yield return ("datetime-local-winter", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local));
        yield return ("datetime-min", DateTime.MinValue);
        yield return ("datetime-max", DateTime.MaxValue);
        yield return ("datetime-min-utc", new DateTime(0, DateTimeKind.Utc));
        yield return ("bytes", new byte[] { 1, 2, 3 });
        yield return ("bytes-empty", Array.Empty<byte>());
        yield return ("bytes-large", Enumerable.Range(0, 300).Select(i => (byte)i).ToArray());
        yield return ("strings", new[] { "a", "b" });
        yield return ("strings-empty", Array.Empty<string>());
        yield return ("strings-escaped", new[] { "<&>", "", " x " });
        yield return ("choice", new OptionSetValue(3));
        yield return ("choice-negative", new OptionSetValue(-1));
        yield return ("choices", new OptionSetValueCollection { new OptionSetValue(1), new OptionSetValue(2) });
        yield return ("choices-empty", new OptionSetValueCollection());
        yield return ("money", new Money(99.95m));
        yield return ("money-trailing-zeros", new Money(2.50m));
        yield return ("reference", new EntityReference("systemuser", G1) { Name = "Me" });
        yield return ("reference-bare", new EntityReference());
        yield return ("reference-keys", new EntityReference("account", new KeyAttributeCollection { { "accountnumber", "A-1" }, { "code", 42 }, { "when", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) } }) { Name = "Keyed", RowVersion = "77" });
        yield return ("reference-empty-name", new EntityReference("a", G1) { Name = "" });
        yield return ("entity", Inner());
        yield return ("entity-empty", new Entity());
        yield return ("entity-states", new Entity("account", G1) { EntityState = EntityState.Created, RowVersion = "9" });
        yield return ("entity-unchanged", new Entity("account", G1) { EntityState = EntityState.Unchanged });
        yield return ("entity-formatted", EntityWithFormatted());
        yield return ("entity-keys", EntityWithKeys());
        yield return ("entity-related", EntityWithRelated());
        yield return ("collection", new EntityCollection(new List<Entity> { Inner(), new Entity("contact", G3) }) { EntityName = "contact", MoreRecords = true, PagingCookie = "<cookie page=\"1\" />", TotalRecordCount = 17, TotalRecordCountLimitExceeded = true, MinActiveRowVersion = "123" });
        yield return ("collection-empty", new EntityCollection());
        yield return ("collection-marv", new EntityCollection(new List<Entity> { Inner() }) { RecordCountLessThanMarv = 5, EntityName = "contact" });
        yield return ("reference-collection", new EntityReferenceCollection { new EntityReference("contact", G2), new EntityReference("account", G1) { Name = "n" } });
        yield return ("reference-collection-empty", new EntityReferenceCollection());
        yield return ("columns", new ColumnSet("name", "revenue"));
        yield return ("columns-all", new ColumnSet(true));
        yield return ("columns-expressions", ColumnsWithExpressions());
        yield return ("relationship", new Relationship("contact_customer_accounts") { PrimaryEntityRole = EntityRole.Referenced });
        yield return ("relationship-referencing", new Relationship("r") { PrimaryEntityRole = EntityRole.Referencing });
        yield return ("relationship-norole", new Relationship("r"));
        yield return ("query", new QueryExpression("account") { ColumnSet = new ColumnSet("name"), TopCount = 5 });
        yield return ("query-by-attribute", ByAttribute());
        yield return ("fetch", new FetchExpression("<fetch top=\"5\"><entity name=\"account\" /></fetch>"));
        yield return ("entities", new[] { Inner(), new Entity("a", G1) });
        yield return ("entities-empty", Array.Empty<Entity>());
        yield return ("references", new[] { new EntityReference("a", G1), new EntityReference("b", G2) { Name = "n" } });
        yield return ("references-empty", Array.Empty<EntityReference>());
        yield return ("choice-array", new[] { new OptionSetValue(4), new OptionSetValue(5) });
        yield return ("choice-array-empty", Array.Empty<OptionSetValue>());
        yield return ("money-array", new[] { new Money(1m), new Money(2.25m) });
        yield return ("money-array-empty", Array.Empty<Money>());
        yield return ("query-with-collections", QueryWithCollections());
        yield return ("query-array-condition", QueryWithArrayCondition());
    }

    // A query as a context value with a typed array as a condition value: the context serializer writes it (a query on its own does not).
    private static QueryExpression QueryWithArrayCondition()
    {
        var q = new QueryExpression("a");
        q.Criteria.Conditions.Add(new ConditionExpression("m", ConditionOperator.In, new object[] { new[] { new Money(1m) } }));
        return q;
    }

    // A query as a context value: the context serializer knows the collection types, so they are written as in a context.
    private static QueryExpression QueryWithCollections()
    {
        var q = new QueryExpression("a");
        q.Criteria.AddCondition("c", ConditionOperator.In, new OptionSetValueCollection { new OptionSetValue(1) });
        q.Criteria.AddCondition("r", ConditionOperator.In, new EntityReferenceCollection { new EntityReference("a", G1) });
        return q;
    }

    private static Entity EntityRelatedCollections()
    {
        var inner = new Entity("b", G2) { ["c"] = new OptionSetValueCollection { new OptionSetValue(3) } };
        var e = new Entity("a", G1);
        e.RelatedEntities[new Relationship("r")] = new EntityCollection(new List<Entity> { inner });
        return e;
    }

    private static QueryByAttribute ByAttributeCollections()
    {
        var q = new QueryByAttribute("a");
        q.Attributes.AddRange(new[] { "c", "r" });
        q.Values.AddRange(new object[] { new OptionSetValueCollection { new OptionSetValue(1) }, new EntityReferenceCollection { new EntityReference("a", G1) } });
        return q;
    }

    private static Entity EntityWithFormatted()
    {
        var e = new Entity("account", G1);
        e["choice"] = new OptionSetValue(3);
        e.FormattedValues["choice"] = "Three";
        e.FormattedValues["other"] = "<&>";
        return e;
    }

    private static Entity EntityWithKeys()
    {
        var e = new Entity("account", G1);
        e.KeyAttributes["accountnumber"] = "A-1";
        e.KeyAttributes["code"] = 42;
        return e;
    }

    private static Entity EntityWithRelated()
    {
        var e = new Entity("account", G1);
        e.RelatedEntities[new Relationship("rel") { PrimaryEntityRole = EntityRole.Referenced }] =
            new EntityCollection(new List<Entity> { Inner() }) { EntityName = "contact" };
        e.RelatedEntities[new Relationship("other")] = new EntityCollection();
        return e;
    }

    private static ColumnSet ColumnsWithExpressions()
    {
        var columns = new ColumnSet("name");
        columns.AttributeExpressions.Add(new XrmAttributeExpression("revenue", XrmAggregateType.Sum, "total"));
        columns.AttributeExpressions.Add(new XrmAttributeExpression("createdon", XrmAggregateType.None, "m") { HasGroupBy = true, DateTimeGrouping = XrmDateTimeGrouping.Month });
        return columns;
    }

    private static QueryByAttribute ByAttribute()
    {
        var q = new QueryByAttribute("contact") { ColumnSet = new ColumnSet("fullname"), TopCount = 3, PageInfo = new PagingInfo { PageNumber = 1, Count = 10 } };
        q.Attributes.Add("parentcustomerid");
        q.Values.Add(new EntityReference("account", G1) { Name = "Contoso" });
        q.Orders.Add(new OrderExpression("fullname", OrderType.Ascending));
        return q;
    }

    private static Entity ImageWith(object? value)
    {
        var e = new Entity("account", G1);
        e["v"] = value;
        return e;
    }

    private static readonly Lazy<(string Name, RemoteExecutionContext Context)[]> ContextEntries = new(() => Build().ToArray());
    private static readonly Lazy<(string Name, QueryBase Query)[]> QueryEntries = new(() => BuildQueries().ToArray());

    /// <summary>The contexts, built once (the serializers and converters never change their input).</summary>
    public static IEnumerable<(string Name, RemoteExecutionContext Context)> All() => ContextEntries.Value;

    public static string[] ContextNames => ContextEntries.Value.Select(e => e.Name).ToArray();

    public static RemoteExecutionContext Context(string name) => ContextEntries.Value.First(e => e.Name == name).Context;

    /// <summary>The queries, built once.</summary>
    public static IEnumerable<(string Name, QueryBase Query)> Queries() => QueryEntries.Value;

    public static string[] QueryNames => QueryEntries.Value.Select(e => e.Name).ToArray();

    public static QueryBase Query(string name) => QueryEntries.Value.First(e => e.Name == name).Query;

    private static IEnumerable<(string Name, RemoteExecutionContext Context)> Build()
    {
        yield return ("golden", RemoteContextGoldenTests.Build());
        yield return ("empty", new RemoteExecutionContext());
        yield return ("owning-extension-null", new RemoteExecutionContext { OwningExtension = null! });
        yield return ("parent-null-explicit", new RemoteExecutionContext { ParentContext = null! });
        yield return ("request-id-null", new RemoteExecutionContext { RequestId = null });
        yield return ("strings-null", new RemoteExecutionContext { MessageName = null!, PrimaryEntityName = null!, SecondaryEntityName = null!, OrganizationName = null!, InitiatingUserAgent = null });
        yield return ("strings-empty", new RemoteExecutionContext { MessageName = "", PrimaryEntityName = "", SecondaryEntityName = "", OrganizationName = "", InitiatingUserAgent = "" });
        yield return ("every-member", EveryMember());
        yield return ("parent-chain", ParentChain());
        yield return ("local-times", LocalTimes());
        yield return ("image-null-rowversion", ImageNullRowVersion());

        foreach (var (name, _) in Values())
        {
            // A fresh instance per placement: contexts and values are never shared between entries.
            yield return ($"input/{name}", With(name, c => c.InputParameters["p"] = Value(name)));
            yield return ($"output/{name}", With(name, c => c.OutputParameters["p"] = Value(name)));
            yield return ($"shared/{name}", With(name, c => c.SharedVariables["p"] = Value(name)));
            yield return ($"pre-image/{name}", With(name, c => c.PreEntityImages["pre"] = ImageWith(Value(name))));
            yield return ($"post-image/{name}", With(name, c => c.PostEntityImages["post"] = ImageWith(Value(name))));
            yield return ($"in-collection/{name}", With(name, c => c.InputParameters["page"] = new EntityCollection(new List<Entity> { ImageWith(Value(name)) })));
            if (name != "null") yield return ($"aliased/{name}", With(name, c => c.InputParameters["a"] = new AliasedValue("contact", "attr", Value(name))));
        }
    }

    // A fresh instance on every call: Values() builds new objects each time, so no two placements share a leaf value.
    private static object? Value(string name) => Values().First(v => v.Name == name).Value;

    private static RemoteExecutionContext With(string name, Action<RemoteExecutionContext> configure)
    {
        var context = new RemoteExecutionContext();
        configure(context);
        return context;
    }

    private static RemoteExecutionContext EveryMember()
    {
        var c = new RemoteExecutionContext
        {
            BusinessUnitId = G1, CorrelationId = G2, Depth = 3, InitiatingUserAgent = "agent/1.0", InitiatingUserAzureActiveDirectoryObjectId = G3,
            InitiatingUserId = G1, IsExecutingOffline = true, IsInTransaction = true, IsOfflinePlayback = true, IsolationMode = 1,
            MessageName = "Update", Mode = 1, OperationCreatedOn = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc), OperationId = G2,
            OrganizationId = G3, OrganizationName = "contoso", OwningExtension = new EntityReference("sdkmessageprocessingstep", G1) { Name = "step", RowVersion = "5" },
            PrimaryEntityId = G2, PrimaryEntityName = "account", RequestId = G3, SecondaryEntityName = "contact", Stage = 40,
            UserAzureActiveDirectoryObjectId = G2, UserId = G3,
        };
        c.InputParameters["Target"] = Inner();
        c.OutputParameters["id"] = G1;
        c.SharedVariables["n"] = 5;
        c.PreEntityImages["pre"] = Inner();
        c.PostEntityImages["post"] = Inner();
        return c;
    }

    private static RemoteExecutionContext ParentChain()
    {
        var grand = new RemoteExecutionContext { MessageName = "Create", Depth = 1, Stage = 20 };
        grand.SharedVariables["g"] = "grand";
        var parent = new RemoteExecutionContext { MessageName = "Update", Depth = 2, Stage = 30, ParentContext = grand };
        parent.InputParameters["Target"] = Inner();
        parent.PreEntityImages["pre"] = Inner();
        var child = new RemoteExecutionContext { MessageName = "Delete", Depth = 3, Stage = 40, ParentContext = parent };
        child.OutputParameters["id"] = G1;
        var root = new RemoteExecutionContext { MessageName = "Assign", Depth = 4, Stage = 40, ParentContext = child };
        root.InputParameters["Target"] = new EntityReference("account", G1);
        return root;
    }

    private static RemoteExecutionContext LocalTimes()
    {
        var c = new RemoteExecutionContext { OperationCreatedOn = new DateTime(2026, 7, 2, 3, 4, 5, DateTimeKind.Local) };
        c.InputParameters["u"] = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
        return c;
    }

    private static RemoteExecutionContext ImageNullRowVersion()
    {
        var c = new RemoteExecutionContext();
        c.PreEntityImages["pre"] = new Entity("account", G1) { RowVersion = null };
        return c;
    }

    // ---- queries --------------------------------------------------------------------------------------------------------

    private static IEnumerable<(string Name, QueryBase Query)> BuildQueries()
    {
        yield return ("golden-expression", RemoteContextGoldenTests.Query());
        yield return ("golden-by-attribute", RemoteContextGoldenTests.ByAttribute());
        yield return ("golden-fetch", new FetchExpression("<fetch top=\"5\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>"));
        yield return ("expression-empty", new QueryExpression());
        yield return ("expression-null-members", new QueryExpression("account") { ColumnSet = null!, Criteria = null!, PageInfo = null! });
        yield return ("expression-top-zero", new QueryExpression("account") { TopCount = 0 });
        yield return ("expression-all-columns", new QueryExpression("account") { ColumnSet = new ColumnSet(true) });
        yield return ("by-attribute-empty", new QueryByAttribute());
        yield return ("by-attribute-null-members", new QueryByAttribute("a") { ColumnSet = null!, PageInfo = null! });
        yield return ("by-attribute-top-zero", new QueryByAttribute("a") { TopCount = 0 });
        yield return ("by-attribute-values", ByAttributeValues());
        yield return ("fetch-empty", new FetchExpression(""));
        yield return ("fetch-escaped", new FetchExpression("<fetch><entity name=\"a\"><filter><condition attribute=\"n\" operator=\"eq\" value=\"a&amp;b &lt; 'c'\" /></filter></entity></fetch>\r\n"));
        yield return ("fetch-null", new FetchExpression((string)null!));
        yield return ("link-null-members", NullLinkMembers());
        yield return ("sub-query-deep", new QueryExpression("a") { SubQueryExpression = new QueryExpression("b") { SubQueryExpression = new QueryExpression("c") } });
        yield return ("link-chain", LinkChain());
        yield return ("filter-chain", FilterChain());
        yield return ("value-entity", ConditionWith(Inner()));
        yield return ("value-collection", ConditionWith(new EntityCollection(new List<Entity> { Inner() })));
        yield return ("value-aliased", ConditionWith(new AliasedValue("c", "a", 5)));
        yield return ("value-columns", ConditionWith(new ColumnSet("a")));
        yield return ("value-relationship", ConditionWith(new Relationship("r")));
        yield return ("value-choices", ConditionWith(new OptionSetValueCollection { new OptionSetValue(1) }));
        yield return ("value-reference-keys", ConditionWith(new EntityReference("a", new KeyAttributeCollection { { "k", 1 } })));
        yield return ("value-query", ConditionWith(new QueryExpression("a")));
        yield return ("value-entity-with-arrays", ConditionWith(new Entity("a", G1) { ["refs"] = new[] { new EntityReference("a", G1) }, ["m"] = new[] { new Money(1m) }, ["s"] = new[] { "x" }, ["b"] = new byte[] { 1 } }));
        yield return ("value-aliased-array", ConditionWith(new AliasedValue("c", "a", new[] { new Money(1m) })));
        yield return ("value-collection-with-arrays", ConditionWith(new EntityCollection(new List<Entity> { new Entity("a") { ["o"] = new[] { new OptionSetValue(1) } } })));
        yield return ("value-reference-keys-array", ConditionWith(new EntityReference("a", new KeyAttributeCollection { { "k", new[] { "x" } } })));
        yield return ("value-entity-with-collections", ConditionWith(new Entity("a", G1) { ["c"] = new OptionSetValueCollection { new OptionSetValue(1) }, ["r"] = new EntityReferenceCollection { new EntityReference("a", G1) } }));
        yield return ("value-entity-key-collections", ConditionWith(new Entity("a", G1) { KeyAttributes = { ["k"] = new OptionSetValueCollection { new OptionSetValue(1) } } }));
        yield return ("value-entity-related-collections", ConditionWith(EntityRelatedCollections()));
        yield return ("value-entity-aliased-collections", ConditionWith(new Entity("a", G1) { ["al"] = new AliasedValue("c", "a", new EntityReferenceCollection { new EntityReference("a", G1) }) }));
        yield return ("value-reference-key-collections", ConditionWith(new EntityReference("a", new KeyAttributeCollection { { "k", new OptionSetValueCollection { new OptionSetValue(1) } } })));
        yield return ("value-aliased-collections", ConditionWith(new AliasedValue("c", "a", new OptionSetValueCollection { new OptionSetValue(1), new OptionSetValue(2) })));
        yield return ("value-nested-query-collections", ConditionWith(QueryWithCollections()));
        yield return ("by-attribute-collections", ByAttributeCollections());
        yield return ("value-strings-empty", ConditionWith(Array.Empty<string>()));
        yield return ("value-bytes-empty", ConditionWith(Array.Empty<byte>()));

        foreach (var (name, _) in Values())
        {
            if (name is "null" or "query-array-condition") continue;
            // Typed arrays of model types directly as a condition value are refused by today's serializer (outside the model set).
            if (name is "entities" or "entities-empty" or "references" or "references-empty" or "choice-array" or "choice-array-empty"
                or "money-array" or "money-array-empty") continue;
            yield return ($"condition/{name}", ConditionWith(Value(name)));
        }

        foreach (var op in Enum.GetValues<ConditionOperator>())
            yield return ($"operator/{op}", new QueryExpression("a") { Criteria = { Conditions = { new ConditionExpression("x", op, 1) } } });
        foreach (var join in Enum.GetValues<JoinOperator>())
            yield return ($"join/{join}", new QueryExpression("a") { LinkEntities = { new LinkEntity("a", "b", "x", "y", join) } });
        foreach (var logic in Enum.GetValues<LogicalOperator>())
            yield return ($"logic/{logic}", new QueryExpression("a") { Criteria = new FilterExpression(logic) });
        foreach (var aggregate in Enum.GetValues<XrmAggregateType>())
            foreach (var grouping in Enum.GetValues<XrmDateTimeGrouping>())
                yield return ($"aggregate/{aggregate}/{grouping}", new QueryExpression("a") { ColumnSet = { AttributeExpressions = { new XrmAttributeExpression("n", aggregate, "al") { DateTimeGrouping = grouping } } } });
        foreach (var type in Enum.GetValues<OrderType>())
            yield return ($"order/{type}", new QueryExpression("a") { Orders = { new OrderExpression("n", type) } });
    }

    private static QueryExpression ConditionWith(object? value)
    {
        var q = new QueryExpression("account");
        q.Criteria.Conditions.Add(new ConditionExpression("x", ConditionOperator.Equal, new[] { value }));
        return q;
    }

    private static QueryByAttribute ByAttributeValues()
    {
        var q = new QueryByAttribute("a");
        q.Attributes.AddRange(new[] { "s", "i", "g", "d", "m", "e" });
        q.Values.AddRange(new object[] { "x", 3, G1, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), 1.50m, new EntityReference("b", G2) });
        return q;
    }

    private static QueryExpression NullLinkMembers()
    {
        var link = new LinkEntity("a", "b", "x", "y", JoinOperator.Inner) { Columns = null!, LinkCriteria = null! };
        return new QueryExpression("a") { LinkEntities = { link } };
    }

    private static QueryExpression LinkChain()
    {
        var q = new QueryExpression("a");
        var l1 = new LinkEntity("a", "b", "x", "y", JoinOperator.Inner) { EntityAlias = "b1", Columns = new ColumnSet(true) };
        var l2 = new LinkEntity("b", "c", "x", "y", JoinOperator.LeftOuter) { EntityAlias = "c1" };
        l2.LinkCriteria.AddCondition("z", ConditionOperator.Null);
        l2.Orders.Add(new OrderExpression("z", OrderType.Descending));
        var l3 = new LinkEntity("c", "d", "x", "y", JoinOperator.Natural);
        l2.LinkEntities.Add(l3);
        l1.LinkEntities.Add(l2);
        q.LinkEntities.Add(l1);
        q.LinkEntities.Add(new LinkEntity("a", "e", "x", "y", JoinOperator.Exists) { ForceSeek = "ix" });
        return q;
    }

    private static QueryExpression FilterChain()
    {
        var inner = new FilterExpression(LogicalOperator.Or) { FilterHint = "h" };
        inner.AddCondition("a", ConditionOperator.In, 1, 2, 3);
        var mid = new FilterExpression(LogicalOperator.And) { IsQuickFindFilter = true };
        mid.Filters.Add(inner);
        mid.AddCondition("b", ConditionOperator.Between, 1, 5);
        var q = new QueryExpression("a");
        q.Criteria.Filters.Add(mid);
        q.Criteria.Filters.Add(new FilterExpression());
        return q;
    }
}

using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Pipeline.Async;
using Xrmd.Pipeline.Webhooks;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// The switched entry points (S.2e2b): <see cref="AsyncContextSerializer"/> and <see cref="WebhookPayload"/> use the own
/// serializers when the model set carries the value and today's SDK serializers otherwise. Every outcome (bytes, or exception
/// type and message) equals the fixed SDK reference handles of <see cref="SdkReferenceSerializers"/>.
/// </summary>
public class SerializerSwitchTests
{
    public static IEnumerable<object[]> Contexts() => ContextCorpus.ContextNames.Select(n => new object[] { n });

    public static IEnumerable<object[]> Queries() => ContextCorpus.QueryNames.Select(n => new object[] { n });

    private static (T? Value, Exception? Error) Run<T>(Func<T> action)
    {
        try { return (action(), null); }
        catch (Exception e) { return (default, e); }
    }

    // The own reader's messages do not name SDK types, so broken payloads compare by exception type and inner type only.
    private static void AssertSameErrorType(Exception? expected, Exception? actual, string what)
    {
        Assert.NotNull(expected);
        Assert.True(expected.GetType() == actual?.GetType(), $"{what}: expected {expected.GetType().Name}, got {actual?.GetType().Name}");
        Assert.Equal(expected.InnerException?.GetType(), actual?.InnerException?.GetType());
    }

    private static void AssertSameError(Exception? expected, Exception? actual, string what)
    {
        Assert.True(expected?.GetType() == actual?.GetType() && expected?.Message == actual?.Message,
            $"{what}: expected {expected?.GetType().Name} '{expected?.Message}', got {actual?.GetType().Name} '{actual?.Message}'");
        Assert.Equal(expected?.InnerException?.GetType(), actual?.InnerException?.GetType());
    }

    // ---- XML: context -------------------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Contexts))]
    public void Serialize_context_equals_the_sdk_reference(string name)
    {
        var context = ContextCorpus.Context(name);
        var (expected, expectedError) = Run(() => SdkReferenceSerializers.SerializeContext(context));
        var (actual, actualError) = Run(() => AsyncContextSerializer.Serialize(context));
        AssertSameError(expectedError, actualError, name);
        Assert.Equal(expected, actual);
    }

    [Theory, MemberData(nameof(Contexts))]
    public void Deserialize_context_equals_the_sdk_reference(string name)
    {
        var xml = SdkReferenceSerializers.SerializeContext(ContextCorpus.Context(name));
        var expected = SdkReferenceSerializers.DeserializeContext(xml);
        var actual = AsyncContextSerializer.Deserialize(xml);
        DeepEqual.AssertStrict(expected, actual);
        Assert.Equal(xml, SdkReferenceSerializers.SerializeContext(actual));
    }

    // ---- XML: query ---------------------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Queries))]
    public void Serialize_query_equals_the_sdk_reference(string name)
    {
        var query = ContextCorpus.Query(name);
        var (expected, expectedError) = Run(() => SdkReferenceSerializers.SerializeQuery(query));
        var (actual, actualError) = Run(() => AsyncContextSerializer.SerializeQuery(query));
        AssertSameError(expectedError, actualError, name);
        Assert.Equal(expected, actual);
    }

    [Theory, MemberData(nameof(Queries))]
    public void Deserialize_query_equals_the_sdk_reference_or_reads_what_the_sdk_cannot(string name)
    {
        var original = ContextCorpus.Query(name);
        var xml = SdkReferenceSerializers.SerializeQuery(original);
        var actual = AsyncContextSerializer.DeserializeQuery(xml);
        var (expected, expectedError) = Run(() => SdkReferenceSerializers.DeserializeQuery(xml));
        if (expectedError is null) DeepEqual.AssertStrict(expected, actual);
        else
        {
            // The SDK reader does not know the collection type hints of its own query writer; the switched reader does.
            Assert.IsType<SerializationException>(expectedError);
            Assert.Equal(xml, SdkReferenceSerializers.SerializeQuery(actual));
        }
    }

    [Fact]
    public void Deserialize_query_reads_option_set_and_reference_collections_the_sdk_reader_refuses()
    {
        var query = new QueryExpression("account");
        query.Criteria.AddCondition("a", ConditionOperator.In,
            new OptionSetValueCollection { new OptionSetValue(1), new OptionSetValue(2) });
        var xml = SdkReferenceSerializers.SerializeQuery(query);
        Assert.Throws<SerializationException>(() => SdkReferenceSerializers.DeserializeQuery(xml));
        var read = (QueryExpression)AsyncContextSerializer.DeserializeQuery(xml);
        var values = Assert.IsType<OptionSetValueCollection>(read.Criteria.Conditions[0].Values[0]);
        Assert.Equal([1, 2], values.Select(v => v.Value));
    }

    [Fact]
    public void Deserialize_query_without_a_type_hint_throws_as_the_sdk_reader_does()
    {
        const string xml = "<QueryBase xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"/>";
        var (_, expected) = Run(() => SdkReferenceSerializers.DeserializeQuery(xml));
        var (_, actual) = Run(() => AsyncContextSerializer.DeserializeQuery(xml));
        Assert.NotNull(expected);
        AssertSameError(expected, actual, "no type hint");
    }

    // ---- broken payloads: the own reader's exceptions are not caught -----------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("<RemoteExecutionContext")]
    [InlineData("<Other xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\"/>")]
    [InlineData("<!DOCTYPE a [<!ENTITY x \"y\">]><RemoteExecutionContext xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\"/>")]
    public void Deserialize_of_a_broken_payload_throws_like_the_sdk_reader(string xml)
    {
        var (_, expected) = Run(() => SdkReferenceSerializers.DeserializeContext(xml));
        var (_, actual) = Run(() => AsyncContextSerializer.Deserialize(xml));
        Assert.NotNull(expected);
        AssertSameErrorType(expected, actual, xml);
        Assert.IsType<SerializationException>(actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<RemoteExecutionContext")]
    public void Deserialize_of_malformed_xml_throws_serialization_exception_with_xml_exception_inside(string xml)
    {
        var actual = Assert.Throws<SerializationException>(() => AsyncContextSerializer.Deserialize(xml));
        Assert.IsType<System.Xml.XmlException>(actual.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<QueryBase")]
    [InlineData("<!DOCTYPE a [<!ENTITY x \"y\">]><QueryBase xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\"/>")]
    public void DeserializeQuery_of_a_broken_payload_throws_like_the_sdk_reader(string xml)
    {
        var (_, expected) = Run(() => SdkReferenceSerializers.DeserializeQuery(xml));
        var (_, actual) = Run(() => AsyncContextSerializer.DeserializeQuery(xml));
        Assert.NotNull(expected);
        AssertSameErrorType(expected, actual, xml);
    }

    // ---- values outside the model set: the SDK fallback -------------------------------------------------------------------

    private static RemoteExecutionContext WithManagedProperty()
    {
        var context = new RemoteExecutionContext { MessageName = "Create", OperationCreatedOn = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        context.SharedVariables["flag"] = new BooleanManagedProperty(true);
        return context;
    }

    [Fact]
    public void Managed_property_shared_variable_serializes_and_deserializes_as_before()
    {
        var context = WithManagedProperty();
        Assert.False(SdkRemoteContext.ModelCanCarry(context));
        var xml = AsyncContextSerializer.Serialize(context);
        Assert.Equal(SdkReferenceSerializers.SerializeContext(context), xml);
        var read = AsyncContextSerializer.Deserialize(xml);
        DeepEqual.AssertStrict(SdkReferenceSerializers.DeserializeContext(xml), read);
        Assert.True(((BooleanManagedProperty)read.SharedVariables["flag"]).Value);
    }

    [Fact]
    public void A_stored_payload_with_a_managed_property_deserializes_as_before()
    {
        var xml = SdkReferenceSerializers.SerializeContext(WithManagedProperty());
        Assert.False(Xrmd.Model.Serialization.ContextXml.TryRead(xml, out _));
        var read = AsyncContextSerializer.Deserialize(xml);
        Assert.IsType<BooleanManagedProperty>(read.SharedVariables["flag"]);
    }

    [Fact]
    public void Managed_property_in_a_query_value_goes_through_the_fallback()
    {
        var query = new QueryExpression("account");
        query.Criteria.AddCondition("a", ConditionOperator.Equal, new BooleanManagedProperty(true));
        var (expected, expectedError) = Run(() => SdkReferenceSerializers.SerializeQuery(query));
        var (actual, actualError) = Run(() => AsyncContextSerializer.SerializeQuery(query));
        AssertSameError(expectedError, actualError, "query");
        Assert.Equal(expected, actual);
    }

    private static Entity EarlyBound()
    {
        var assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.EarlyBound.dll"));
        var entity = (Entity)Activator.CreateInstance(assembly.GetType("Xrmd.TestPlugins.EarlyBound.PipeLog")!)!;
        entity.Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        entity["pb_name"] = "n";
        return entity;
    }

    [Fact]
    public void Early_bound_entity_payload_is_written_and_read_by_the_fallback()
    {
        var context = new RemoteExecutionContext();
        context.InputParameters["Target"] = EarlyBound();
        context.PostEntityImages["post"] = EarlyBound();
        Assert.False(SdkRemoteContext.ModelCanCarry(context));
        var xml = AsyncContextSerializer.Serialize(context);
        Assert.Equal(SdkReferenceSerializers.SerializeContext(context), xml);
        Assert.False(Xrmd.Model.Serialization.ContextXml.TryRead(xml, out _));
        // Today's reader does not know the early-bound type either (its assembly is not a known type of the reader): same outcome.
        var (expected, expectedError) = Run(() => SdkReferenceSerializers.DeserializeContext(xml));
        var (actual, actualError) = Run(() => AsyncContextSerializer.Deserialize(xml));
        AssertSameError(expectedError, actualError, "early bound");
        if (expectedError is null) DeepEqual.AssertStrict(expected, actual);
    }

    [Fact]
    public void A_type_outside_the_allowlist_is_still_refused()
    {
        const string xml =
            "<RemoteExecutionContext xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            "<InputParameters xmlns:a=\"http://schemas.datacontract.org/2004/07/System.Collections.Generic\" xmlns:s=\"http://schemas.datacontract.org/2004/07/System.Data\">" +
            "<a:KeyValuePairOfstringanyType><a:key>x</a:key><a:value i:type=\"s:DataSet\"/></a:KeyValuePairOfstringanyType></InputParameters>" +
            "</RemoteExecutionContext>";
        var (_, expected) = Run(() => SdkReferenceSerializers.DeserializeContext(xml));
        var (_, actual) = Run(() => AsyncContextSerializer.Deserialize(xml));
        Assert.NotNull(expected);
        AssertSameError(expected, actual, "DataSet");
        Assert.IsType<SerializationException>(actual);
    }

    // ---- JSON: webhook -------------------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Contexts))]
    public void Webhook_serialize_equals_the_sdk_reference(string name)
    {
        var context = ContextCorpus.Context(name);
        var (expected, expectedError) = Run(() => SdkReferenceSerializers.SerializeContextJson(context));
        var (actual, actualError) = Run(() => WebhookPayload.Serialize(context));
        AssertSameError(expectedError, actualError, name);
        Assert.Equal(expected, actual);
    }

    private static readonly PropertyInfo[] Settable = typeof(RemoteExecutionContext)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0
                    && p.Name is not (nameof(RemoteExecutionContext.ParentContext) or nameof(RemoteExecutionContext.InputParameters)
                        or nameof(RemoteExecutionContext.PreEntityImages) or nameof(RemoteExecutionContext.PostEntityImages)))
        .ToArray();

    // Today's rule, spelled out with the reference serializer: whole body when it fits, else the copy without the four members.
    private static (byte[]? Body, bool Trimmed, Exception? Error) ReferenceBuild(RemoteExecutionContext context, int limit)
    {
        try
        {
            var body = SdkReferenceSerializers.SerializeContextJson(context);
            if (body.Length <= limit) return (body, false, null);
            var copy = new RemoteExecutionContext();
            foreach (var member in Settable) member.SetValue(copy, member.GetValue(context));
            foreach (var (key, value) in context.OutputParameters) copy.OutputParameters[key] = value;
            foreach (var (key, value) in context.SharedVariables) copy.SharedVariables[key] = value;
            return (SdkReferenceSerializers.SerializeContextJson(copy), true, null);
        }
        catch (Exception e) { return (null, false, e); }
    }

    [Theory, MemberData(nameof(Contexts))]
    public void Webhook_build_equals_the_sdk_reference_for_several_limits(string name)
    {
        var context = ContextCorpus.Context(name);
        var whole = Run(() => SdkReferenceSerializers.SerializeContextJson(context)).Value;
        var limits = new List<int> { 1, 1000, WebhookPayload.SizeLimit, int.MaxValue };
        if (whole is not null)
        {
            limits.Add(whole.Length);
            limits.Add(whole.Length - 1);
            var trimmed = ReferenceBuild(context, 1).Body;
            if (trimmed is not null && trimmed.Length < whole.Length) limits.Add((trimmed.Length + whole.Length) / 2);
        }
        foreach (var limit in limits.Where(l => l > 0))
        {
            var expected = ReferenceBuild(context, limit);
            var (actual, actualError) = Run(() => WebhookPayload.Build(context, limit));
            AssertSameError(expected.Error, actualError, $"{name} limit {limit}");
            if (expected.Error is not null) continue;
            Assert.Equal(expected.Trimmed, actual.Trimmed);
            Assert.Equal(expected.Body, actual.Body);
        }
    }

    [Fact]
    public void Webhook_build_does_not_change_its_input()
    {
        var context = ContextCorpus.Context("every-member");
        var before = SdkReferenceSerializers.SerializeContext(context);
        Assert.True(WebhookPayload.Build(context, 1).Trimmed);
        Assert.Equal(before, SdkReferenceSerializers.SerializeContext(context));
    }

    [Fact]
    public void Webhook_with_a_managed_property_goes_through_the_fallback_whole_and_trimmed()
    {
        var context = WithManagedProperty();
        context.InputParameters["a"] = "x";
        Assert.False(SdkRemoteContext.ModelCanCarry(context));
        foreach (var limit in new[] { 1, WebhookPayload.SizeLimit })
        {
            var expected = ReferenceBuild(context, limit);
            var actual = WebhookPayload.Build(context, limit);
            Assert.Null(expected.Error);
            Assert.Equal(expected.Trimmed, actual.Trimmed);
            Assert.Equal(expected.Body, actual.Body);
        }
    }

    [Fact]
    public void Webhook_with_an_early_bound_entity_goes_through_the_fallback()
    {
        var context = new RemoteExecutionContext();
        context.InputParameters["Target"] = EarlyBound();
        var expected = ReferenceBuild(context, WebhookPayload.SizeLimit);
        var (actual, actualError) = Run(() => WebhookPayload.Build(context));
        AssertSameError(expected.Error, actualError, "early bound");
        if (expected.Error is null) Assert.Equal(expected.Body, actual.Body);
    }

    [Theory]
    [InlineData("Depth", "abc")]
    [InlineData("RequestId", "not-a-guid")]
    [InlineData("IsInTransaction", "maybe")]
    public void Deserialize_of_a_value_format_error_has_an_xml_exception_inside_like_the_sdk_reader(string member, string text)
    {
        var xml = System.Text.RegularExpressions.Regex.Replace(
            SdkReferenceSerializers.SerializeContext(new RemoteExecutionContext { RequestId = Guid.NewGuid() }),
            $@"<{member}>[^<]*</{member}>|<{member}\s[^>]*/>", $"<{member}>{text}</{member}>");
        var (_, expected) = Run(() => SdkReferenceSerializers.DeserializeContext(xml));
        var (_, actual) = Run(() => AsyncContextSerializer.Deserialize(xml));
        AssertSameErrorType(expected, actual, member);
        Assert.IsType<System.Xml.XmlException>(actual!.InnerException);
    }

    [Fact]
    public void The_fallback_counter_counts_per_thread_so_parallel_tests_cannot_disturb_it()
    {
        var before = AsyncContextSerializer.SdkFallbackCount;
        var other = new Thread(AsyncContextSerializer.WarmUpSdk);
        other.Start();
        other.Join();
        Assert.Equal(before, AsyncContextSerializer.SdkFallbackCount);
    }

    [Fact]
    public void Warm_up_exercises_the_sdk_fallback_and_a_carried_round_trip_does_not()
    {
        var context = new RemoteExecutionContext { MessageName = "Create" };
        var before = AsyncContextSerializer.SdkFallbackCount;
        AsyncContextSerializer.Deserialize(AsyncContextSerializer.Serialize(context));
        Assert.Equal(before, AsyncContextSerializer.SdkFallbackCount);
        AsyncContextSerializer.WarmUpSdk();
        Assert.True(AsyncContextSerializer.SdkFallbackCount >= before + 2, "write and read both go through the SDK serializer");
    }

    [Fact]
    public void Serialize_null_throws_argument_null()
    {
        Assert.Throws<ArgumentNullException>(() => AsyncContextSerializer.Serialize((RemoteExecutionContext)null!));
    }

    // ---- nesting: over the reader's bound the SDK serializers are used, as on main ---------------------------------------

    private static Entity NestedEntity(int levels)
    {
        var e = new Entity("account", Guid.NewGuid()) { ["name"] = "leaf" };
        for (var i = 0; i < levels; i++) e = new Entity("account", Guid.NewGuid()) { ["child"] = e };
        return e;
    }

    private static RemoteExecutionContext NestedContext(int levels)
    {
        var context = new RemoteExecutionContext { MessageName = "Create" };
        context.SharedVariables["tree"] = NestedEntity(levels);
        return context;
    }

    private static RemoteExecutionContext ParentChain(int length)
    {
        var context = new RemoteExecutionContext { MessageName = "Create", Depth = 1 };
        for (var i = 0; i < length; i++) context = new RemoteExecutionContext { MessageName = "Update", Depth = i + 2, ParentContext = context };
        return context;
    }

    private static QueryExpression NestedFilters(int levels)
    {
        var query = new QueryExpression("account");
        var filter = query.Criteria;
        for (var i = 0; i < levels; i++) filter = filter.AddFilter(LogicalOperator.And);
        filter.AddCondition("name", ConditionOperator.Equal, "x");
        return query;
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(300)]
    public void A_context_with_nested_entities_round_trips_like_the_sdk_path(int levels)
    {
        var context = NestedContext(levels);
        var expectedXml = SdkReferenceSerializers.SerializeContext(context);
        var xml = AsyncContextSerializer.Serialize(context);
        Assert.Equal(expectedXml, xml);
        var expected = SdkReferenceSerializers.DeserializeContext(xml);
        DeepEqual.AssertStrict(expected, AsyncContextSerializer.Deserialize(xml));
        Assert.Equal(xml, AsyncContextSerializer.Serialize(AsyncContextSerializer.Deserialize(xml)));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(300)]
    public void A_parent_chain_round_trips_like_the_sdk_path(int length)
    {
        var context = ParentChain(length);
        var xml = AsyncContextSerializer.Serialize(context);
        Assert.Equal(SdkReferenceSerializers.SerializeContext(context), xml);
        DeepEqual.AssertStrict(SdkReferenceSerializers.DeserializeContext(xml), AsyncContextSerializer.Deserialize(xml));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(300)]
    public void A_query_with_nested_filters_round_trips_like_the_sdk_path(int levels)
    {
        var query = NestedFilters(levels);
        var xml = AsyncContextSerializer.SerializeQuery(query);
        Assert.Equal(SdkReferenceSerializers.SerializeQuery(query), xml);
        DeepEqual.AssertStrict(SdkReferenceSerializers.DeserializeQuery(xml), AsyncContextSerializer.DeserializeQuery(xml));
    }

    // What the own writer carries, the own reader reads: no payload is written by the own code that it then refuses.
    [Fact]
    public void The_model_carries_only_what_the_own_reader_reads_at_every_nesting_depth()
    {
        var carriedEntities = 0;
        var carriedChain = 0;
        var carriedFilters = 0;
        for (var n = 0; n <= 130; n++)
        {
            var context = NestedContext(n);
            if (SdkRemoteContext.ModelCanCarry(context))
            {
                carriedEntities = n;
                Assert.True(Xrmd.Model.Serialization.ContextXml.TryRead(AsyncContextSerializer.Serialize(context), out _), $"entities {n}");
            }
            var chain = ParentChain(n);
            if (SdkRemoteContext.ModelCanCarry(chain))
            {
                carriedChain = n;
                Assert.True(Xrmd.Model.Serialization.ContextXml.TryRead(AsyncContextSerializer.Serialize(chain), out _), $"parents {n}");
            }
            var query = NestedFilters(n);
            if (SdkRemoteContext.ModelCanCarry(query))
            {
                carriedFilters = n;
                Assert.True(Xrmd.Model.Serialization.QueryXml.TryRead(AsyncContextSerializer.SerializeQuery(query), out _), $"filters {n}");
            }
        }
        // Real contexts stay far below the bound; a few dozen levels still take the own path.
        Assert.True(carriedEntities >= 20, $"entities {carriedEntities}");
        Assert.True(carriedChain >= 20, $"parents {carriedChain}");
        Assert.True(carriedFilters >= 20, $"filters {carriedFilters}");
        Assert.True(carriedEntities < 130 && carriedChain < 130 && carriedFilters < 130, "the bound exists");
    }

    [Fact]
    public void The_own_reader_reports_false_over_the_bound_on_a_small_stack_without_overflow()
    {
        var xml = SdkReferenceSerializers.SerializeContext(NestedContext(100));
        var deepChain = SdkReferenceSerializers.SerializeContext(ParentChain(300));
        var results = new List<bool>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                results.Add(Xrmd.Model.Serialization.ContextXml.TryRead(xml, out _));
                results.Add(Xrmd.Model.Serialization.ContextXml.TryRead(deepChain, out _));
            }
            catch (Exception e) { failure = e; }
        }, 512 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.Equal([false, false], results);
    }
}

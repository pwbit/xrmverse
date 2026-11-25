using System.Text;
using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Serialization;
using Xrmd.Pipeline.Webhooks;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// The own JSON writer of the webhook payload against today's <c>DataContractJsonSerializer</c> path (the golden file, the corpus,
/// the Review Focus edges, the trimmed body): the bytes must be identical.
/// </summary>
public class ContextJsonWriterTests
{
    public static IEnumerable<object[]> Contexts() => ContextCorpus.ContextNames.Select(n => new object[] { n });

    private static string GoldenPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "xrmverse.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tests", "fixtures", "remote-context-golden", "webhook.json");
    }

    private static void AssertSameBytes(byte[] expected, byte[] actual)
    {
        if (expected.AsSpan().SequenceEqual(actual)) return;
        var i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i]) i++;
        var utf8 = new UTF8Encoding(false);
        var from = Math.Max(0, i - 80);
        string Slice(byte[] b) => utf8.GetString(b, from, Math.Min(240, Math.Max(0, b.Length - from)));
        Assert.Fail($"differs at byte {i} (expected length {expected.Length}, actual {actual.Length}){Environment.NewLine}" +
                    $"expected: ...{Slice(expected)}{Environment.NewLine}actual:   ...{Slice(actual)}");
    }

    private static void AssertMatchesSdk(RemoteExecutionContext context)
    {
        Assert.True(SdkRemoteContext.ModelCanCarry(context), "the context is outside the model set");
        var own = ContextJson.Write(context.ToModel());
        AssertSameBytes(SdkReferenceSerializers.SerializeContextJson(context), own);
        AssertSameBytes(WebhookPayload.Serialize(context), own);
    }

    // Same outcome as the SDK path: the same bytes, or the same exception type and message.
    private static void AssertSameOutcome(RemoteExecutionContext context, string what)
    {
        byte[]? expected = null, viaWebhook = null, actual = null;
        Exception? sdkError = null, webhookError = null, ownError = null;
        try { expected = SdkReferenceSerializers.SerializeContextJson(context); } catch (Exception e) { sdkError = e; }
        try { viaWebhook = WebhookPayload.Serialize(context); } catch (Exception e) { webhookError = e; }
        try { actual = ContextJson.Write(context.ToModel()); } catch (Exception e) { ownError = e; }
        Assert.True(sdkError?.GetType() == ownError?.GetType() && sdkError?.Message == ownError?.Message,
            $"{what}: SDK {sdkError?.GetType().Name} '{sdkError?.Message}' vs own {ownError?.GetType().Name} '{ownError?.Message}'");
        Assert.Equal(sdkError?.GetType(), webhookError?.GetType());
        Assert.Equal(sdkError?.Message, webhookError?.Message);
        Assert.Equal(sdkError?.InnerException?.GetType(), ownError?.InnerException?.GetType());
        if (expected is not null)
        {
            AssertSameBytes(expected, actual!);
            AssertSameBytes(expected, viaWebhook!);
        }
    }

    [Fact]
    public void Golden_matches() =>
        AssertSameBytes(File.ReadAllBytes(GoldenPath()), ContextJson.Write(RemoteContextGoldenTests.Build().ToModel()));

    [Theory, MemberData(nameof(Contexts))]
    public void Corpus_matches_the_sdk_serializer(string name)
    {
        var context = ContextCorpus.Context(name);
        Assert.True(SdkRemoteContext.ModelCanCarry(context), "the corpus entry is outside the model set");
        AssertSameOutcome(context, name);
    }

    [Theory, MemberData(nameof(Contexts))]
    public void Trimmed_matches(string name)
    {
        var context = ContextCorpus.Context(name);
        var model = context.ToModel();
        byte[]? body = null;
        Exception? sdkError = null, ownError = null;
        try { body = WebhookPayload.Build(context, limit: 1).Body; } catch (Exception e) { sdkError = e; }
        byte[]? own = null, before = null;
        try { before = ContextJson.Write(model); own = ContextJson.Write(ContextJson.Trim(model)); } catch (Exception e) { ownError = e; }
        Assert.Equal(sdkError?.GetType(), ownError?.GetType());
        Assert.Equal(sdkError?.Message, ownError?.Message);
        if (body is null) return;
        Assert.True(WebhookPayload.Build(context, limit: 1).Trimmed);
        AssertSameBytes(body, own!);
        // The input is not changed.
        AssertSameBytes(before!, ContextJson.Write(model));
    }

    [Fact]
    public void Trim_leaves_the_other_members_and_empties_the_four()
    {
        var model = ContextCorpus.Context("every-member").ToModel();
        var trimmed = ContextJson.Trim(model);
        Assert.NotSame(model, trimmed);
        Assert.Null(trimmed.ParentContext);
        Assert.Empty(trimmed.InputParameters);
        Assert.Empty(trimmed.PreEntityImages);
        Assert.Empty(trimmed.PostEntityImages);
        Assert.Equal(model.MessageName, trimmed.MessageName);
        Assert.Equal(model.OutputParameters.Count, trimmed.OutputParameters.Count);
        Assert.Equal(model.SharedVariables.Count, trimmed.SharedVariables.Count);
        Assert.Equal(model.OwningExtension?.Id, trimmed.OwningExtension?.Id);
        Assert.NotEmpty(model.InputParameters);
    }

    // Every UTF-16 code unit outside the surrogate range, in one string per 2048 units, and surrogate pairs.
    [Fact]
    public void Every_character_is_escaped_like_the_sdk()
    {
        for (var start = 0; start < 0x10000; start += 2048)
        {
            var sb = new StringBuilder();
            for (var c = start; c < start + 2048; c++)
                if (c is < 0xD800 or > 0xDFFF) sb.Append((char)c);
            var context = new RemoteExecutionContext { OperationCreatedOn = DateTime.UnixEpoch, MessageName = sb.ToString() };
            context.SharedVariables["s"] = sb.ToString();
            AssertMatchesSdk(context);
        }
        var pairs = new RemoteExecutionContext { OperationCreatedOn = DateTime.UnixEpoch, MessageName = "\U0001F600\U00010000\U0010FFFF a\u2028b\u2029c / \\ \" '" };
        pairs.InputParameters["k/\"\\\u0001"] = "\U0001F600";
        AssertMatchesSdk(pairs);
    }

    [Fact]
    public void Invalid_surrogates_fail_like_the_sdk()
    {
        var context = new RemoteExecutionContext { OperationCreatedOn = DateTime.UnixEpoch, MessageName = "a\ud800b" };
        Exception? sdk = null;
        try { SdkReferenceSerializers.SerializeContextJson(context); } catch (Exception e) { sdk = e; }
        Exception? own = null;
        try { ContextJson.Write(context.ToModel()); } catch (Exception e) { own = e; }
        Assert.Equal(sdk?.GetType(), own?.GetType());
        Assert.Equal(sdk?.Message, own?.Message);
        Assert.True(sdk is not null || own is null);
        if (sdk is null) AssertSameBytes(SdkReferenceSerializers.SerializeContextJson(context), ContextJson.Write(context.ToModel()));
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void DateTime_kinds_match_around_the_clock_and_at_the_limits(DateTimeKind kind)
    {
        var instants = new List<DateTime>
        {
            DateTime.MinValue, DateTime.MaxValue, new(1, 1, 2, 0, 0, 0), new(1969, 12, 31, 23, 59, 59, 999), new(1970, 1, 1),
            new(1970, 1, 1, 0, 0, 0, 1), new(1900, 6, 15, 12, 0, 0), new(2026, 1, 1, 0, 0, 0), new(2026, 3, 29, 1, 30, 0),
            new(2026, 10, 25, 2, 30, 0), new(2026, 7, 1, 12, 0, 0), new(9999, 12, 31, 23, 59, 59, 999), new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(9999),
        };
        foreach (var instant in instants)
        {
            var value = DateTime.SpecifyKind(instant, kind);
            var context = new RemoteExecutionContext { OperationCreatedOn = value };
            context.InputParameters["d"] = value;
            context.PreEntityImages["i"] = new Entity("a") { ["d"] = value };
            AssertSameOutcome(context, $"{kind} {instant:o}");
        }
    }

    // Every quarter hour of the days around the transitions of the zone the tests run in (set TZ to try another): the offset
    // suffix and the UTC conversion of local and unspecified values, one context per kind and period.
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void DateTime_grid_matches_in_every_period(DateTimeKind kind)
    {
        var periods = new (DateTime From, int Days)[]
        {
            (new DateTime(2026, 1, 1), 366), (new DateTime(1969, 12, 30), 4), (new DateTime(1972, 1, 5), 5), (new DateTime(1900, 1, 1), 3),
            (new DateTime(1883, 11, 17), 3), (new DateTime(1945, 9, 29), 3), (new DateTime(1986, 3, 28), 3), (new DateTime(2038, 1, 18), 3),
            (new DateTime(1, 1, 1), 2), (new DateTime(9999, 12, 30), 2),
        };
        foreach (var (from, days) in periods)
        {
            var context = new RemoteExecutionContext();
            for (var i = 0; i < days * 96; i++)
            {
                var ticks = from.Ticks + i * TimeSpan.TicksPerMinute * 15;
                if (ticks > DateTime.MaxValue.Ticks) break;
                context.InputParameters["t" + i] = new DateTime(ticks, kind);
            }
            AssertSameOutcome(context, $"{kind} from {from:o}");
        }
    }

    // Local values made by ToLocalTime carry the BCL's ambiguous-daylight flag in the fall-back hour; the writer must not rebuild
    // them (SpecifyKind would drop the flag). Every quarter hour of a year, converted from UTC (does nothing without DST).
    [Fact]
    public void Local_values_converted_from_utc_match_including_the_ambiguous_hour()
    {
        var context = new RemoteExecutionContext();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 366 * 96; i++) context.InputParameters["t" + i] = start.AddMinutes(15 * i).ToLocalTime();
        context.OperationCreatedOn = new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc).ToLocalTime();
        AssertSameOutcome(context, "ToLocalTime values");
    }

    // The chain stored XML -> read -> JSON on both paths: a local date read from an offset text keeps the flag end to end.
    [Theory]
    [InlineData("2026-10-25T02:30:00+02:00")]
    [InlineData("2026-10-25T02:30:00.0000000+02:00")]
    [InlineData("2026-10-25T02:30:00+01:00")]
    [InlineData("2026-10-25T01:30:00+02:00")]
    [InlineData("2026-11-01T01:30:00-04:00")]
    [InlineData("2026-11-01T01:30:00-05:00")]
    [InlineData("2026-03-29T03:30:00+02:00")]
    [InlineData("2026-10-25T00:30:00Z")]
    [InlineData("2026-10-25T02:30:00")]
    public void Dates_read_from_stored_xml_give_the_same_json_on_both_paths(string text)
    {
        var template = new RemoteExecutionContext { OperationCreatedOn = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        template.InputParameters["d"] = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        template.PreEntityImages["i"] = new Entity("a") { ["d"] = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        var xml = SdkReferenceSerializers.SerializeContext(template).Replace("2000-01-02T03:04:05Z", text);
        Assert.Contains(text, xml);
        var sdkJson = WebhookPayload.Serialize(SdkReferenceSerializers.DeserializeContext(xml));
        Assert.True(ContextXml.TryRead(xml, out var model));
        AssertSameBytes(sdkJson, ContextJson.Write(model!));
        // And through the converters.
        AssertSameBytes(sdkJson, ContextJson.Write(SdkReferenceSerializers.DeserializeContext(xml).ToModel()));
        AssertSameBytes(sdkJson, ContextJson.Write(model!.ToSdk().ToModel()));
    }

    [Fact]
    public void Sub_millisecond_and_pre_epoch_ticks_truncate_like_the_sdk()
    {
        foreach (var kind in new[] { DateTimeKind.Utc, DateTimeKind.Local, DateTimeKind.Unspecified })
        {
            var context = new RemoteExecutionContext();
            for (var k = -25; k <= 25; k++)
                context.InputParameters["t" + k] = new DateTime(DateTime.UnixEpoch.Ticks + k * 4999L, kind == DateTimeKind.Utc ? DateTimeKind.Utc : kind);
            AssertSameOutcome(context, kind.ToString());
        }
    }

    [Fact]
    public void Doubles_and_decimals_match()
    {
        var doubles = new[]
        {
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0, 0.0, double.MaxValue, double.MinValue, double.Epsilon,
            1e21, 1e-7, 123456789012345678d, 0.1, 1d / 3, 100, 1e15, 1e16, 1e17, -1.5e300, 4.9406564584124654E-324, 5e-324, 1.7976931348623157E+308,
        };
        var decimals = new[]
        {
            0m, 1.50m, 0.000m, decimal.MaxValue, decimal.MinValue, 0.0000000000000000000000000001m, -1.10m, 79228162514264337593543950335m,
            1234567890.1234567890123456789m, 100m, 1e10m,
        };
        var context = new RemoteExecutionContext { OperationCreatedOn = DateTime.UnixEpoch };
        foreach (var d in doubles) context.InputParameters["d" + d.ToString("R")] = d;
        foreach (var m in decimals) context.OutputParameters["m" + m] = m;
        foreach (var m in decimals) context.SharedVariables["money" + m] = new Money(m);
        AssertMatchesSdk(context);
        foreach (var d in doubles)
        {
            var single = new RemoteExecutionContext { OperationCreatedOn = DateTime.UnixEpoch };
            single.InputParameters["d"] = d;
            AssertMatchesSdk(single);
        }
    }

    [Fact]
    public void Integers_bytes_and_arrays_match()
    {
        var context = new RemoteExecutionContext { OperationCreatedOn = DateTime.UnixEpoch, Depth = int.MinValue, Mode = int.MaxValue, Stage = -1, IsolationMode = 0 };
        context.InputParameters["i"] = int.MinValue;
        context.InputParameters["l"] = long.MinValue;
        context.InputParameters["l2"] = long.MaxValue;
        context.InputParameters["b0"] = Array.Empty<byte>();
        context.InputParameters["b1"] = new byte[] { 0, 255, 1 };
        context.InputParameters["b2"] = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        context.InputParameters["s0"] = Array.Empty<string>();
        context.InputParameters["s1"] = new[] { "a", "", "\"" };
        context.InputParameters["n"] = null;
        AssertMatchesSdk(context);
    }

    [Fact]
    public void Undefined_enum_numbers_match()
    {
        var entity = new Entity("a") { EntityState = (EntityState)7 };
        var related = new Entity("b");
        related.RelatedEntities[new Relationship("r") { PrimaryEntityRole = (EntityRole)9 }] = new EntityCollection();
        var context = new RemoteExecutionContext { OperationCreatedOn = DateTime.UnixEpoch };
        context.PostEntityImages["x"] = entity;
        context.PostEntityImages["y"] = related;
        Exception? sdk = null;
        byte[]? expected = null;
        try { expected = SdkReferenceSerializers.SerializeContextJson(context); } catch (Exception e) { sdk = e; }
        Exception? own = null;
        byte[]? actual = null;
        try { actual = ContextJson.Write(context.ToModel()); } catch (Exception e) { own = e; }
        Assert.Equal(sdk?.GetType(), own?.GetType());
        if (expected is not null) AssertSameBytes(expected, actual!);
    }

    [Fact]
    public void Every_enum_value_of_a_query_is_written_as_the_sdk_number()
    {
        var q = new Microsoft.Xrm.Sdk.Query.QueryExpression("a");
        foreach (var op in Enum.GetValues<Microsoft.Xrm.Sdk.Query.ConditionOperator>())
            q.Criteria.Conditions.Add(new Microsoft.Xrm.Sdk.Query.ConditionExpression("x", op, 1));
        foreach (var join in Enum.GetValues<Microsoft.Xrm.Sdk.Query.JoinOperator>())
            q.LinkEntities.Add(new Microsoft.Xrm.Sdk.Query.LinkEntity("a", "b", "x", "y", join));
        foreach (var logic in Enum.GetValues<Microsoft.Xrm.Sdk.Query.LogicalOperator>())
            q.Criteria.AddFilter(logic);
        foreach (var order in Enum.GetValues<Microsoft.Xrm.Sdk.Query.OrderType>())
            q.Orders.Add(new Microsoft.Xrm.Sdk.Query.OrderExpression("x", order));
        var context = new RemoteExecutionContext { OperationCreatedOn = DateTime.UnixEpoch };
        context.InputParameters["q"] = q;
        var entity = new Entity("a");
        context.InputParameters["roles"] = new EntityCollection(Enum.GetValues<EntityState>().Select(st => new Entity("a") { EntityState = st }).ToList());
        foreach (var role in Enum.GetValues<EntityRole>())
            entity.RelatedEntities[new Relationship("r" + role) { PrimaryEntityRole = role }] = new EntityCollection();
        context.PreEntityImages["i"] = entity;
        AssertMatchesSdk(context);
    }

    [Fact]
    public void A_value_outside_the_model_set_is_refused()
    {
        var model = new RemoteContext { OperationCreatedOn = DateTime.UnixEpoch };
        model.InputParameters["x"] = new object();
        Assert.Throws<NotSupportedException>(() => ContextJson.Write(model));
    }

    [Fact]
    public void Output_has_no_byte_order_mark_and_is_utf8()
    {
        var bytes = ContextJson.Write(new RemoteContext { OperationCreatedOn = DateTime.UnixEpoch, MessageName = "café" });
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Contains("café", new UTF8Encoding(false).GetString(bytes));
    }
}

using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Abstractions.Sdk;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Serialization;

namespace Xrmd.Pipeline.Tests.Sdk;

/// <summary>
/// The own XML reader of the remote context and of queries against the SDK reader: every corpus payload written by the SDK
/// reads back deep-equal (DateTime kinds, double bits and decimal scale included) to the original and to what the SDK reads;
/// payloads outside the model set report false; broken payloads fail with the same exception types as the SDK path.
/// </summary>
public class ContextXmlReaderTests
{
    public static IEnumerable<object[]> Contexts() => ContextCorpus.ContextNames.Select(n => new object[] { n });

    public static IEnumerable<object[]> Queries() => ContextCorpus.QueryNames.Select(n => new object[] { n });

    private const string Namespaces =
        "xmlns:g=\"http://schemas.datacontract.org/2004/07/System.Collections.Generic\" xmlns:x=\"http://www.w3.org/2001/XMLSchema\" " +
        "xmlns:g2=\"http://schemas.microsoft.com/2003/10/Serialization/\"";

    // A complete payload as the SDK writes it for an empty context; tests replace single members of it, so every other member is present.
    private static readonly string Template = SdkReferenceSerializers.SerializeContext(new RemoteExecutionContext());

    /// <summary>The template with the element <paramref name="name"/> replaced by <paramref name="element"/>.</summary>
    private static string Replace(string xml, string name, string element)
    {
        var pattern = $@"<{name}(\s[^>]*)?/>|<{name}(\s[^>]*)?>.*?</{name}>";
        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.Matches(regex, xml);
        return regex.Replace(xml, _ => element, 1);
    }

    private static string Member(string name, string inner) => Replace(Template, name, $"<{name}>{inner}</{name}>");

    private static string Entry(string key, string typeHint, string value) =>
        $"<KeyValuePairOfstringanyType><g:key>{key}</g:key><g:value i:type=\"{typeHint}\">{value}</g:value></KeyValuePairOfstringanyType>";

    private static string Parameters(string name, string entries) => Replace(Template, name, $"<{name} {Namespaces}>{entries}</{name}>");

    private static string Input(string key, string typeHint, string value) => Parameters("InputParameters", Entry(key, typeHint, value));

    private static T? Outcome<T>(Func<T> read, out Type? failure) where T : class
    {
        try
        {
            failure = null;
            return read();
        }
        catch (Exception e)
        {
            failure = e.GetType();
            return null;
        }
    }

    /// <summary>Reads with both readers: the own reader must read what the SDK reads (equal), and fail where the SDK fails.</summary>
    private static string AssertSameAsSdk(string xml, string because)
    {
        var sdk = Outcome(() => SdkReferenceSerializers.DeserializeContext(xml), out var sdkFailure);
        var own = Outcome<RemoteContext>(() => ContextXml.TryRead(xml, out var c) ? c! : throw new NotSupportedException("unknown"), out var ownFailure);
        if (ownFailure == typeof(NotSupportedException)) return "false"; // reported false: the twin uses the SDK reader
        if (sdk is null)
        {
            Assert.True(ownFailure is not null, $"{because}: the SDK refuses the payload ({sdkFailure}), the own reader read it");
            Assert.True(ownFailure == sdkFailure, $"{because}: SDK {sdkFailure}, own {ownFailure}");
            return "both throw";
        }
        Assert.True(own is not null, $"{because}: the SDK reads the payload, the own reader threw {ownFailure}");
        DeepEqual.AssertStrict(sdk, own!.ToSdk());
        return "equal";
    }

    // ---- corpus -------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Corpus_context_written_by_the_sdk_reads_equal(string name)
    {
        var original = ContextCorpus.Context(name);
        var xml = SdkReferenceSerializers.SerializeContext(original);
        Assert.True(ContextXml.TryRead(xml, out var read), name);
        if (!LossyInTheSdkWriter(name)) DeepEqual.AssertStrict(original, read!.ToSdk());
        DeepEqual.AssertStrict(SdkReferenceSerializers.DeserializeContext(xml), read!.ToSdk());
    }

    // The SDK's writer turns "\r\n" and a lone "\r" of a text into "\n" (the XmlWriter's newline handling), so the SDK itself does not
    // read those texts back equal to the original: they are compared with what the SDK reads.
    private static bool LossyInTheSdkWriter(string name) => name.Contains("string-lines") || name.Contains("fetch-escaped");

    // The SDK's reader does not know the collection and relationship type hints its query writer produces (it throws "The deserializer has
    // no knowledge of any type that maps to this name"). The own reader accepts them; the comparison with the SDK reader is made when the SDK
    // reads the payload.
    private static QueryBase? SdkReadsQuery(string xml)
    {
        try { return SdkReferenceSerializers.DeserializeQuery(xml); }
        catch (SerializationException) { return null; }
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Corpus_context_round_trips_through_the_own_writer(string name)
    {
        var original = ContextCorpus.Context(name);
        var written = ContextXml.Write(original.ToModel());
        Assert.True(ContextXml.TryRead(written, out var read), name);
        Assert.Equal(written, ContextXml.Write(read!));
        if (!LossyInTheSdkWriter(name)) DeepEqual.AssertStrict(original, read!.ToSdk());
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Corpus_query_written_by_the_sdk_reads_equal(string name)
    {
        var original = ContextCorpus.Query(name);
        var xml = SdkReferenceSerializers.SerializeQuery(original);
        Assert.True(QueryXml.TryRead(xml, out var read), name);
        if (!LossyInTheSdkWriter(name)) DeepEqual.AssertStrict(original, read!.ToSdk());
        if (SdkReadsQuery(xml) is { } sdk) DeepEqual.AssertStrict(sdk, read!.ToSdk());
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Corpus_query_round_trips_through_the_own_writer(string name)
    {
        var original = ContextCorpus.Query(name);
        var written = QueryXml.Write(original.ToModel());
        Assert.True(QueryXml.TryRead(written, out var read), name);
        Assert.Equal(written, QueryXml.Write(read!));
        if (!LossyInTheSdkWriter(name)) DeepEqual.AssertStrict(original, read!.ToSdk());
    }

    [Theory]
    [MemberData(nameof(Contexts))]
    public void Corpus_context_reads_equal_with_other_prefixes(string name)
    {
        var original = ContextCorpus.Context(name);
        var xml = Reprefix(SdkReferenceSerializers.SerializeContext(original));
        Assert.DoesNotContain("xmlns:i=", xml);
        Assert.True(ContextXml.TryRead(xml, out var read), name);
        if (!LossyInTheSdkWriter(name)) DeepEqual.AssertStrict(original, read!.ToSdk());
        else DeepEqual.AssertStrict(SdkReferenceSerializers.DeserializeContext(SdkReferenceSerializers.SerializeContext(original)), read!.ToSdk());
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Corpus_query_reads_equal_with_other_prefixes(string name)
    {
        var original = ContextCorpus.Query(name);
        var xml = Reprefix(SdkReferenceSerializers.SerializeQuery(original));
        Assert.True(QueryXml.TryRead(xml, out var read), name);
        if (!LossyInTheSdkWriter(name)) DeepEqual.AssertStrict(original, read!.ToSdk());
        else DeepEqual.AssertStrict(QueryXml.TryRead(SdkReferenceSerializers.SerializeQuery(original), out var plain) ? plain!.ToSdk() : null, read!.ToSdk());
    }

    /// <summary>Rewrites the payload with a different prefix for every namespace (the default namespace becomes a prefix too).</summary>
    private static string Reprefix(string xml)
    {
        var text = new StringBuilder();
        var prefixes = new Dictionary<string, string>();
        string PrefixOf(string ns) => prefixes.TryGetValue(ns, out var p) ? p : prefixes[ns] = "zz" + prefixes.Count;
        using (var reader = XmlReader.Create(new StringReader(xml)))
        using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var empty = reader.IsEmptyElement;
                        writer.WriteStartElement(PrefixOf(reader.NamespaceURI), reader.LocalName, reader.NamespaceURI);
                        var attributes = new List<(string Ns, string Local, string Value)>();
                        if (reader.HasAttributes)
                        {
                            while (reader.MoveToNextAttribute())
                            {
                                if (reader.Prefix == "xmlns" || reader.Name == "xmlns") continue;
                                var value = reader.Value;
                                if (reader.NamespaceURI == "http://www.w3.org/2001/XMLSchema-instance" && reader.LocalName == "type")
                                {
                                    var colon = value.IndexOf(':');
                                    var ns = reader.LookupNamespace(colon < 0 ? "" : value[..colon]) ?? "";
                                    var local = colon < 0 ? value : value[(colon + 1)..];
                                    writer.WriteStartAttribute(PrefixOf(reader.NamespaceURI), reader.LocalName, reader.NamespaceURI);
                                    writer.WriteQualifiedName(local, ns);
                                    writer.WriteEndAttribute();
                                }
                                else writer.WriteAttributeString(PrefixOf(reader.NamespaceURI), reader.LocalName, reader.NamespaceURI, value);
                            }
                            reader.MoveToElement();
                        }
                        if (empty) writer.WriteEndElement();
                        break;
                    case XmlNodeType.EndElement:
                        writer.WriteEndElement();
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.CDATA:
                        writer.WriteString(reader.Value);
                        break;
                }
            }
        }
        return text.ToString();
    }

    // ---- outside the model set ----------------------------------------------------------------------------------------

    [Fact]
    public void A_shared_variable_outside_the_model_set_reports_false()
    {
        var context = new RemoteExecutionContext();
        context.SharedVariables["flag"] = new BooleanManagedProperty(true);
        var xml = SdkReferenceSerializers.SerializeContext(context);
        Assert.False(ContextXml.TryRead(xml, out var read));
        Assert.Null(read);
        // The SDK reader (the fallback) still reads it.
        Assert.IsType<BooleanManagedProperty>(SdkReferenceSerializers.DeserializeContext(xml).SharedVariables["flag"]);
    }

    [Theory]
    [InlineData("x:float", "1.5")]
    [InlineData("x:short", "1")]
    [InlineData("x:byte", "1")]
    [InlineData("x:duration", "PT1S")]
    [InlineData("x:anyURI", "urn:a")]
    [InlineData("Nope", "")]
    [InlineData("Entity2", "")]
    public void A_type_hint_outside_the_model_set_reports_false(string hint, string value)
    {
        Assert.False(ContextXml.TryRead(Input("a", hint, value), out var read));
        Assert.Null(read);
    }

    [Fact]
    public void An_untyped_empty_value_reports_false()
    {
        var xml = Parameters("InputParameters", "<KeyValuePairOfstringanyType><g:key>a</g:key><g:value /></KeyValuePairOfstringanyType>");
        Assert.False(ContextXml.TryRead(xml, out _));
    }

    [Fact]
    public void A_type_name_in_a_foreign_namespace_is_not_instantiated()
    {
        var xml = Parameters("InputParameters", Entry("a", "x:string", "text")).Replace("xmlns:x=\"http://www.w3.org/2001/XMLSchema\"", "xmlns:x=\"urn:other\"");
        Assert.False(ContextXml.TryRead(xml, out _));
    }

    [Fact]
    public void An_unknown_member_reports_false_because_the_sdk_keeps_it_as_extension_data()
    {
        var xml = SdkReferenceSerializers.SerializeContext(RemoteContextGoldenTests.Build());
        foreach (var anchor in new[] { "<Depth>2</Depth>", "<LogicalName>account</LogicalName>", "<AllColumns>false</AllColumns>" })
        {
            if (!xml.Contains(anchor)) continue;
            Assert.False(ContextXml.TryRead(xml.Replace(anchor, "<UnknownMember>1</UnknownMember>" + anchor), out _), anchor);
        }
        Assert.False(ContextXml.TryRead(xml.Replace("<Depth>2</Depth>", "<Depth z=\"1\">2</Depth>"), out _));
        var query = SdkReferenceSerializers.SerializeQuery(RemoteContextGoldenTests.Query());
        Assert.False(QueryXml.TryRead(query.Replace("<Distinct>true</Distinct>", "<UnknownMember>1</UnknownMember><Distinct>true</Distinct>"), out _));
    }

    [Fact]
    public void A_query_root_with_an_unknown_type_reports_false()
    {
        const string xml = "<QueryBase xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" i:type=\"Other\" xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\" />";
        Assert.False(QueryXml.TryRead(xml, out var read));
        Assert.Null(read);
    }

    // ---- type hints on declared members and items ----------------------------------------------------------------------

    private static Entity EarlyBound()
    {
        var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Xrmd.TestPlugins.EarlyBound.dll"));
        var entity = (Entity)Activator.CreateInstance(assembly.GetType("Xrmd.TestPlugins.EarlyBound.PipeLog")!)!;
        entity.Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        entity["pb_name"] = "n";
        return entity;
    }

    [Fact]
    public void An_early_bound_entity_in_a_nested_place_reports_false()
    {
        var placements = new (string Name, Action<RemoteExecutionContext> Fill)[]
        {
            ("image", c => c.PreEntityImages["x"] = EarlyBound()),
            ("post image", c => c.PostEntityImages["x"] = EarlyBound()),
            ("collection item", c => c.InputParameters["c"] = new EntityCollection(new List<Entity> { EarlyBound() })),
            ("array item", c => c.InputParameters["a"] = new Entity[] { EarlyBound() }),
            ("related entity", c =>
            {
                var owner = new Entity("a");
                owner.RelatedEntities[new Relationship("r")] = new EntityCollection(new List<Entity> { EarlyBound() });
                c.InputParameters["rel"] = owner;
            }),
            ("aliased", c => c.InputParameters["al"] = new AliasedValue("a", "b", EarlyBound())),
            ("parameter", c => c.InputParameters["t"] = EarlyBound()),
        };
        foreach (var (name, fill) in placements)
        {
            var context = new RemoteExecutionContext();
            fill(context);
            Assert.False(SdkRemoteContext.ModelCanCarry(context), name);
            var xml = SdkReferenceSerializers.SerializeContext(context);
            Assert.Contains("PipeLog", xml);
            Assert.False(ContextXml.TryRead(xml, out var read), name);
            Assert.Null(read);
        }
    }

    [Theory]
    [InlineData("ParentContext", "d:DataSet")]
    [InlineData("OwningExtension", "Entity")]
    [InlineData("InputParameters", "Nope")]
    [InlineData("PreEntityImages", "Nope")]
    [InlineData("ParentContext", "RemoteExecutionContext")]
    public void A_type_hint_on_a_declared_member_reports_false(string member, string hint)
    {
        var element = $"<{member} xmlns:d=\"http://schemas.datacontract.org/2004/07/System.Data\" i:type=\"{hint}\" />";
        var xml = Replace(Template, member, element);
        Assert.False(ContextXml.TryRead(xml, out _));
    }

    [Fact]
    public void A_type_hint_in_a_query_part_or_an_item_reports_false()
    {
        var query = SdkReferenceSerializers.SerializeQuery(RemoteContextGoldenTests.Query());
        foreach (var (anchor, replacement) in new[]
        {
            ("<Criteria>", "<Criteria i:type=\"Nope\">"),
            ("<ColumnSet>", "<ColumnSet i:type=\"Nope\">"),
            ("<ConditionExpression>", "<ConditionExpression i:type=\"Nope\">"),
            ("<OrderExpression>", "<OrderExpression i:type=\"Nope\">"),
            ("<LinkEntity>", "<LinkEntity i:type=\"Nope\">"),
            ("<PageInfo>", "<PageInfo i:type=\"Nope\">"),
        })
        {
            Assert.Contains(anchor, query);
            Assert.False(QueryXml.TryRead(query.Replace(anchor, replacement), out _), anchor);
        }
        var context = SdkReferenceSerializers.SerializeContext(RemoteContextGoldenTests.Build());
        Assert.False(ContextXml.TryRead(context.Replace("<KeyValuePairOfstringanyType>", "<KeyValuePairOfstringanyType i:type=\"Nope\">"), out _));
        Assert.False(ContextXml.TryRead(context.Replace("<Entities>", "<Entities i:type=\"Nope\">"), out _));
    }

    // ---- strictness like the SDK ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(" Equal ")]
    [InlineData("Equal ")]
    public void Enum_text_is_not_trimmed(string text)
    {
        var query = SdkReferenceSerializers.SerializeQuery(RemoteContextGoldenTests.Query());
        var changed = query.Replace("<Operator>Equal</Operator>", $"<Operator>{text}</Operator>");
        Assert.NotEqual(query, changed);
        Assert.Throws<SerializationException>(() => SdkReferenceSerializers.DeserializeQuery(changed));
        Assert.Throws<SerializationException>(() => QueryXml.TryRead(changed, out _));
    }

    [Fact]
    public void A_padded_type_hint_is_not_trimmed()
    {
        Assert.False(ContextXml.TryRead(Input("a", " x:int ", "1"), out _));
        var query = SdkReferenceSerializers.SerializeQuery(new FetchExpression("<fetch />"));
        var padded = query.Replace("i:type=\"FetchExpression\"", "i:type=\" FetchExpression \"");
        Assert.NotEqual(query, padded);
        Assert.Throws<SerializationException>(() => SdkReferenceSerializers.DeserializeQuery(padded));
        Assert.False(QueryXml.TryRead(padded, out _));
    }

    [Theory]
    [InlineData("&#160;")]
    [InlineData("<![CDATA[ ]]>")]
    [InlineData("<![CDATA[]]>")]
    [InlineData("x")]
    [InlineData(" \t\r\n ")]
    [InlineData("<!-- c -->")]
    public void Content_between_members_behaves_like_the_sdk(string filler)
    {
        var xml = Replace(Template, "InputParameters", $"<InputParameters {Namespaces}>{filler}</InputParameters>");
        var sdk = Record.Exception(() => SdkReferenceSerializers.DeserializeContext(xml));
        var reported = false;
        var own = Record.Exception(() => reported = ContextXml.TryRead(xml, out _));
        if (sdk is null) Assert.True(own is null, filler + ": SDK reads, own threw " + own?.GetType().Name);
        else if (own is null) Assert.False(reported, filler + ": SDK refuses, own read");
        else Assert.Equal(sdk.GetType(), own.GetType());
        var inEntries = Parameters("InputParameters", filler + Entry("a", "x:int", "1") + filler);
        var sdk2 = Record.Exception(() => SdkReferenceSerializers.DeserializeContext(inEntries));
        var reported2 = false;
        var own2 = Record.Exception(() => reported2 = ContextXml.TryRead(inEntries, out _));
        if (sdk2 is null) Assert.True(own2 is null, filler + ": SDK reads, own threw " + own2?.GetType().Name);
        else if (own2 is null) Assert.False(reported2, filler + ": SDK refuses, own read");
        else Assert.Equal(sdk2.GetType(), own2.GetType());
    }

    // ---- stack ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_payload_at_the_nesting_cap_reads_on_a_small_stack_and_one_more_level_reports_false()
    {
        // 254 parents: the deepest element is the 256th level (root = 1, then 254 ParentContext, then a member of the last one).
        var atCap = Chain(254);
        var overCap = Chain(255);
        Exception? failure = null;
        var read = false;
        var thread = new Thread(() =>
        {
            try
            {
                read = ContextXml.TryRead(atCap, out _);
                Assert.False(ContextXml.TryRead(overCap, out var none) || none is not null);
            }
            catch (Exception e)
            {
                failure = e;
            }
        }, 512 * 1024);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.True(read);
    }

    // ---- failures -----------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Broken()
    {
        var xml = SdkReferenceSerializers.SerializeContext(RemoteContextGoldenTests.Build());
        yield return new object[] { "empty", "" };
        yield return new object[] { "whitespace", "   " };
        yield return new object[] { "text", "not xml" };
        yield return new object[] { "truncated", xml[..(xml.Length / 2)] };
        yield return new object[] { "dtd", "<!DOCTYPE a [<!ENTITY x \"y\">]>" + xml };
        yield return new object[] { "dtd-entity", "<!DOCTYPE RemoteExecutionContext [<!ENTITY x \"y\">]><RemoteExecutionContext xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\">&x;</RemoteExecutionContext>" };
        yield return new object[] { "two-roots", xml + "<x />" };
        yield return new object[] { "wrong-root", xml.Replace("RemoteExecutionContext", "Other") };
        yield return new object[] { "wrong-namespace", xml.Replace("http://schemas.microsoft.com/xrm/2011/Contracts", "urn:x") };
        yield return new object[] { "query-payload", SdkReferenceSerializers.SerializeQuery(RemoteContextGoldenTests.Query()) };
        yield return new object[] { "bad-int", xml.Replace("<Depth>2</Depth>", "<Depth>abc</Depth>") };
        yield return new object[] { "empty-int", xml.Replace("<Depth>2</Depth>", "<Depth></Depth>") };
        yield return new object[] { "int-overflow", xml.Replace("<Depth>2</Depth>", "<Depth>99999999999</Depth>") };
        yield return new object[] { "bad-guid", xml.Replace("<BusinessUnitId>33333333-3333-3333-3333-333333333333</BusinessUnitId>", "<BusinessUnitId>zz</BusinessUnitId>") };
        yield return new object[] { "bad-bool", xml.Replace("<IsInTransaction>false</IsInTransaction>", "<IsInTransaction>maybe</IsInTransaction>") };
        yield return new object[] { "capital-bool", xml.Replace("<IsInTransaction>false</IsInTransaction>", "<IsInTransaction>False</IsInTransaction>") };
        yield return new object[] { "bad-date", xml.Replace("<OperationCreatedOn>", "<OperationCreatedOn>nope") };
        yield return new object[] { "nil-int", Replace(Template, "Depth", "<Depth i:nil=\"true\" />") };
        yield return new object[] { "nil-guid", Replace(Template, "BusinessUnitId", "<BusinessUnitId i:nil=\"true\" />") };
        yield return new object[] { "nil-bool", Replace(Template, "IsInTransaction", "<IsInTransaction i:nil=\"true\" />") };
        yield return new object[] { "nil-datetime", Replace(Template, "OperationCreatedOn", "<OperationCreatedOn i:nil=\"true\" />") };
        yield return new object[] { "bad-base64", Input("a", "x:base64Binary", "@@@") };
        yield return new object[] { "bad-long", Input("a", "x:long", "1.5") };
        yield return new object[] { "bad-double", Input("a", "x:double", "one") };
        yield return new object[] { "bad-decimal", Input("a", "x:decimal", "1e5x") };
        yield return new object[] { "decimal-exponent", Input("a", "x:decimal", "1E3") };
        yield return new object[] { "bad-guid-value", Input("a", "g2:guid", "nope") };
        yield return new object[] { "bad-enum", Parameters("InputParameters", Entry("a", "Entity", "<EntityState>Bogus</EntityState>")) };
        yield return new object[] { "numeric-enum", Parameters("InputParameters", Entry("a", "Entity", "<EntityState>1</EntityState>")) };
        yield return new object[] { "dup-parameter", Parameters("InputParameters", Entry("a", "x:int", "1") + Entry("a", "x:int", "2")) };
        yield return new object[] { "dup-attribute", Parameters("InputParameters", Entry("a", "Entity", "<Attributes>" + Entry("k", "x:int", "1") + Entry("k", "x:int", "2") + "</Attributes>")) };
        yield return new object[] { "nil-key", Parameters("InputParameters", "<KeyValuePairOfstringanyType><g:key i:nil=\"true\" /><g:value i:nil=\"true\" /></KeyValuePairOfstringanyType>") };
        yield return new object[] { "missing-key", Parameters("InputParameters", "<KeyValuePairOfstringanyType><g:value i:nil=\"true\" /></KeyValuePairOfstringanyType>") };
        yield return new object[] { "untyped-with-content", Parameters("InputParameters", "<KeyValuePairOfstringanyType><g:key>a</g:key><g:value>text</g:value></KeyValuePairOfstringanyType>") };
        yield return new object[] { "text-in-collection", Parameters("InputParameters", "text") };
        yield return new object[] { "text-in-entry", Parameters("InputParameters", "<KeyValuePairOfstringanyType>text</KeyValuePairOfstringanyType>") };
        yield return new object[] { "wrong-entry-name", Parameters("InputParameters", "<Foo><g:key>a</g:key><g:value i:nil=\"true\" /></Foo>") };
    }

    // A broken payload either throws the SDK path's exception type, or the own reader reports false and the twin's fallback to the SDK
    // reader throws it (the own reader never reads a payload the SDK refuses).
    [Theory]
    [MemberData(nameof(Broken))]
    public void A_broken_payload_fails_like_the_sdk_path(string name, string xml)
    {
        var sdk = Record.Exception(() => SdkReferenceSerializers.DeserializeContext(xml));
        Assert.NotNull(sdk);
        var reported = false;
        var own = Record.Exception(() => reported = ContextXml.TryRead(xml, out _));
        if (own is null)
        {
            Assert.False(reported, $"{name}: the SDK refuses the payload ({sdk!.GetType().Name}), the own reader read it");
            return;
        }
        Assert.Equal(sdk!.GetType(), own.GetType());
    }

    [Fact]
    public void A_nil_root_is_not_an_execution_context()
    {
        const string xml = "<RemoteExecutionContext xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" i:nil=\"true\" />";
        Assert.Throws<SerializationException>(() => Xrmd.Pipeline.Async.AsyncContextSerializer.Deserialize(xml));
        Assert.Throws<SerializationException>(() => ContextXml.TryRead(xml, out _));
        Assert.Throws<SerializationException>(() => QueryXml.TryRead(xml.Replace("RemoteExecutionContext", "QueryBase"), out _));
    }

    [Fact]
    public void Malformed_xml_and_a_dtd_surface_as_serialization_exceptions_with_the_xml_exception_inside()
    {
        foreach (var xml in new[] { "<a", "<!DOCTYPE a [<!ENTITY x \"y\">]><a />", "" })
        {
            var e = Assert.Throws<SerializationException>(() => ContextXml.TryRead(xml, out _));
            Assert.IsType<XmlException>(e.InnerException);
            Assert.Throws<SerializationException>(() => QueryXml.TryRead(xml, out _));
        }
    }

    [Fact]
    public void A_context_payload_is_not_a_query_and_the_other_way_round()
    {
        Assert.Throws<SerializationException>(() => QueryXml.TryRead(SdkReferenceSerializers.SerializeContext(RemoteContextGoldenTests.Build()), out _));
        Assert.Throws<SerializationException>(() => ContextXml.TryRead(SdkReferenceSerializers.SerializeQuery(RemoteContextGoldenTests.Query()), out _));
        // Without a type hint the SDK reader fails with another exception type; the own reader reports false so the twin falls back.
        Assert.False(QueryXml.TryRead("<QueryBase xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\" />", out var query));
        Assert.Null(query);
    }

    [Fact]
    public void Null_payloads_throw_argument_null()
    {
        Assert.Throws<ArgumentNullException>(() => ContextXml.TryRead(null!, out _));
        Assert.Throws<ArgumentNullException>(() => QueryXml.TryRead(null!, out _));
    }

    // ---- depth --------------------------------------------------------------------------------------------------------

    // A chain of complete contexts (every member present, as the SDK writes them).
    private static string Chain(int depth)
    {
        var body = Template["<RemoteExecutionContext".Length..];
        var open = body[..(body.IndexOf('>') + 1)];
        var inner = body[open.Length..^"</RemoteExecutionContext>".Length];
        const string nilParent = "<ParentContext i:nil=\"true\" />";
        Assert.Contains(nilParent, inner);
        var chain = inner;
        for (var i = 0; i < depth; i++) chain = inner.Replace(nilParent, "<ParentContext>" + chain + "</ParentContext>");
        return "<RemoteExecutionContext" + open + chain + "</RemoteExecutionContext>";
    }

    // A lean chain: only the nesting counts for the depth bound.
    private static string LeanChain(int depth)
    {
        var text = new StringBuilder("<RemoteExecutionContext xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">");
        for (var i = 0; i < depth; i++) text.Append("<ParentContext>");
        for (var i = 0; i < depth; i++) text.Append("</ParentContext>");
        return text.Append("</RemoteExecutionContext>").ToString();
    }

    [Fact]
    public void A_parent_chain_within_the_bound_reads()
    {
        Assert.True(ContextXml.TryRead(Chain(150), out var read));
        var length = 0;
        for (var c = read; c is not null; c = c.ParentContext) length++;
        Assert.Equal(151, length);
    }

    [Theory]
    [InlineData(2000)]
    [InlineData(100000)]
    public void A_payload_nested_too_deep_reports_false_and_no_stack_overflow(int depth)
    {
        Assert.False(ContextXml.TryRead(LeanChain(depth), out _));
        var nested = new StringBuilder("<QueryBase xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" i:type=\"QueryExpression\">");
        for (var i = 0; i < depth; i++) nested.Append("<SubQueryExpression>");
        for (var i = 0; i < depth; i++) nested.Append("</SubQueryExpression>");
        nested.Append("</QueryBase>");
        Assert.False(QueryXml.TryRead(nested.ToString(), out _));
    }

    // ---- hand-written payloads: the same as the SDK reads ---------------------------------------------------------------

    // The SDK writes every member except a few it omits when null (below); a payload with another member absent is not one the SDK wrote,
    // so the own reader leaves it to the SDK reader (false) rather than guess the defaults of an uninitialized SDK object.
    [Fact]
    public void An_absent_member_reports_false_except_the_ones_the_sdk_omits()
    {
        foreach (var xml in new[]
        {
            "<RemoteExecutionContext xmlns=\"http://schemas.microsoft.com/xrm/2011/Contracts\" />",
            Replace(Template, "OwningExtension", "<OwningExtension />"),
            Replace(Template, "InputParameters", ""),
            Input("e", "Entity", ""), Input("r", "EntityReference", ""), Input("c", "EntityCollection", ""), Input("m", "Money", ""),
            Input("o", "OptionSetValue", ""), Input("s", "ColumnSet", ""), Input("r", "Relationship", ""), Input("q", "QueryExpression", ""),
            Input("q", "QueryByAttribute", ""), Input("q", "FetchExpression", ""), Input("a", "AliasedValue", ""),
        })
        {
            Assert.False(ContextXml.TryRead(xml, out _), xml);
            AssertSameAsSdk(xml, xml);
        }
    }

    [Theory]
    [InlineData("x:int", " 5 ")]
    [InlineData("x:int", "+5")]
    [InlineData("x:int", "007")]
    [InlineData("x:boolean", "1")]
    [InlineData("x:boolean", "0")]
    [InlineData("x:boolean", " true ")]
    [InlineData("x:double", "INF")]
    [InlineData("x:double", "-INF")]
    [InlineData("x:double", "NaN")]
    [InlineData("x:double", "-0")]
    [InlineData("x:double", "1E3")]
    [InlineData("x:double", "0.1")]
    [InlineData("x:decimal", "1.50")]
    [InlineData("x:decimal", "-0.000")]
    [InlineData("x:long", "-9223372036854775808")]
    [InlineData("x:dateTime", "2026-01-02T03:04:05Z")]
    [InlineData("x:dateTime", "2026-01-02T03:04:05")]
    [InlineData("x:dateTime", "2026-01-02T03:04:05.1234567Z")]
    [InlineData("x:dateTime", "2026-01-02T03:04:05+02:00")]
    [InlineData("x:dateTime", "2026-01-02T03:04:05-05:30")]
    [InlineData("x:dateTime", "0001-01-01T00:00:00")]
    [InlineData("x:dateTime", "9999-12-31T23:59:59.9999999")]
    [InlineData("x:dateTime", "2026-01-02")]
    [InlineData("x:base64Binary", "AQID")]
    [InlineData("x:base64Binary", "AQ ID\r\n")]
    [InlineData("x:base64Binary", "")]
    [InlineData("x:string", "")]
    [InlineData("x:string", "  padded  ")]
    [InlineData("x:string", "&#65;&#x42;&amp;&lt;")]
    [InlineData("x:string", "<![CDATA[a<b]]>")]
    [InlineData("x:string", "line1\r\nline2\rline3")]
    [InlineData("x:string", "a<!-- comment -->b")]
    [InlineData("g2:guid", "33333333-3333-3333-3333-333333333333")]
    [InlineData("g2:guid", "{33333333-3333-3333-3333-333333333333}")]
    [InlineData("g2:guid", "33333333333333333333333333333333")]
    [InlineData("g2:guid", " 33333333-3333-3333-3333-333333333333 ")]
    public void A_hand_written_value_reads_like_the_sdk(string hint, string value)
    {
        var xml = Input("a", hint, value);
        // Neither reader may refuse it and the own reader must read it (not report false), so the comparison is not vacuous.
        _ = SdkReferenceSerializers.DeserializeContext(xml);
        Assert.Equal("equal", AssertSameAsSdk(xml, hint + " " + value));
    }

    [Fact]
    public void A_context_member_in_other_notations_reads_like_the_sdk()
    {
        foreach (var (member, value) in new[]
        {
            ("Depth", " 7 "), ("IsInTransaction", "1"), ("OperationCreatedOn", "2026-05-06T07:08:09+02:00"),
            ("OperationCreatedOn", "2026-05-06T07:08:09"), ("OperationCreatedOn", "2026-05-06T07:08:09.5Z"),
            ("MessageName", "&#x41;ccount"), ("MessageName", "<![CDATA[<x>]]>"), ("RequestId", "33333333-3333-3333-3333-333333333333"),
        })
            Assert.Equal("equal", AssertSameAsSdk(Member(member, value), member + " " + value));
    }

    // ---- mutations: a payload the SDK refuses must not be read, one it reads must read equal ------------------------------

    [Fact]
    public void Mutated_payloads_never_read_differently_from_the_sdk()
    {
        var sources = new[]
        {
            SdkReferenceSerializers.SerializeContext(RemoteContextGoldenTests.Build()),
            SdkReferenceSerializers.SerializeContext(ContextCorpus.Context("every-member")),
            SdkReferenceSerializers.SerializeContext(ContextCorpus.Context("parent-chain")),
        };
        var outcomes = new Dictionary<string, int>();
        foreach (var source in sources)
        {
            var count = new System.Xml.Linq.XDocument(System.Xml.Linq.XDocument.Parse(source)).Descendants().Count();
            for (var index = 0; index < count; index++)
            {
                foreach (var kind in new[] { "remove", "duplicate", "swap", "text", "nil", "empty" })
                {
                    var doc = System.Xml.Linq.XDocument.Parse(source);
                    var element = doc.Descendants().ElementAt(index);
                    var changed = Mutate(element, kind);
                    if (!changed) continue;
                    var outcome = AssertSameAsSdk(doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting), $"{kind} #{index} <{element.Name.LocalName}>");
                    outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                }
            }
        }
        // The test is not vacuous: many mutated payloads are read (and equal), many are refused by both, the rest go to the fallback.
        Assert.True(outcomes.GetValueOrDefault("equal") > 100, string.Join(", ", outcomes));
        Assert.True(outcomes.GetValueOrDefault("both throw") > 100, string.Join(", ", outcomes));
        Assert.True(outcomes.GetValueOrDefault("false") > 100, string.Join(", ", outcomes));
    }

    private static string AssertSameQueryAsSdk(string xml, string because)
    {
        var sdk = Outcome(() => SdkReferenceSerializers.DeserializeQuery(xml), out var sdkFailure);
        var own = Outcome<QueryDefinition>(() => QueryXml.TryRead(xml, out var q) ? q! : throw new NotSupportedException("unknown"), out var ownFailure);
        if (ownFailure == typeof(NotSupportedException)) return "false";
        if (sdk is null)
        {
            Assert.True(ownFailure is not null, $"{because}: the SDK refuses the payload ({sdkFailure}), the own reader read it");
            Assert.True(ownFailure == sdkFailure, $"{because}: SDK {sdkFailure}, own {ownFailure}");
            return "both throw";
        }
        Assert.True(own is not null, $"{because}: the SDK reads the payload, the own reader threw {ownFailure}");
        DeepEqual.AssertStrict(sdk, own!.ToSdk());
        return "equal";
    }

    [Fact]
    public void Mutated_query_payloads_never_read_differently_from_the_sdk()
    {
        var sources = new[]
        {
            SdkReferenceSerializers.SerializeQuery(RemoteContextGoldenTests.Query()),
            SdkReferenceSerializers.SerializeQuery(RemoteContextGoldenTests.ByAttribute()),
            SdkReferenceSerializers.SerializeQuery(new FetchExpression("<fetch />")),
        };
        var outcomes = new Dictionary<string, int>();
        foreach (var source in sources)
        {
            var count = System.Xml.Linq.XDocument.Parse(source).Descendants().Count();
            for (var index = 0; index < count; index++)
            {
                foreach (var kind in new[] { "remove", "duplicate", "swap", "text", "nil", "empty" })
                {
                    var doc = System.Xml.Linq.XDocument.Parse(source);
                    var element = doc.Descendants().ElementAt(index);
                    if (!Mutate(element, kind)) continue;
                    var outcome = AssertSameQueryAsSdk(doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting), $"{kind} #{index} <{element.Name.LocalName}>");
                    outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                }
            }
        }
        Assert.True(outcomes.GetValueOrDefault("equal") > 50, string.Join(", ", outcomes));
        Assert.True(outcomes.GetValueOrDefault("both throw") > 50, string.Join(", ", outcomes));
        Assert.True(outcomes.GetValueOrDefault("false") > 50, string.Join(", ", outcomes));
    }

    private static bool Mutate(System.Xml.Linq.XElement element, string kind)
    {
        if (element.Parent is null) return false;
        var nil = System.Xml.Linq.XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance");
        switch (kind)
        {
            case "remove":
                element.Remove();
                return true;
            case "duplicate":
                element.AddAfterSelf(new System.Xml.Linq.XElement(element));
                return true;
            case "swap":
                if (element.NextNode is not System.Xml.Linq.XElement next) return false;
                next.AddAfterSelf(new System.Xml.Linq.XElement(element));
                element.Remove();
                return true;
            case "text":
                if (element.HasElements) return false;
                element.Value = "x";
                return true;
            case "nil":
                element.RemoveNodes();
                element.SetAttributeValue(nil, "true");
                return true;
            case "empty":
                if (element.HasElements) return false;
                element.Value = "";
                return true;
            default:
                return false;
        }
    }
}

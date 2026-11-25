using System.Runtime.Serialization;
using System.Xml;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;
using static Xrmd.Model.Serialization.ContractNames;

namespace Xrmd.Model.Serialization;

/// <summary>
/// Reads the contract XML of the remote context and of queries into model types. The payload is parsed with the same
/// <see cref="XmlReaderSettings"/> as the SDK path (DTD prohibited, no resolver) into a small tree, and the tree is read by
/// element name and namespace, never by prefix or position of the declaration, so a payload from another writer with other
/// prefixes reads the same.
/// <para>
/// The reader reads exactly what the model can carry. When the payload holds anything else, it reports "unknown" (the caller gets
/// <c>false</c> from <c>TryRead</c> and uses the SDK reader): a type hint outside the model set (never instantiated here), a
/// member the contract does not list or lists elsewhere in the order (the SDK keeps those as extension data), an attribute
/// other than <c>xmlns</c>, <c>i:type</c> and <c>i:nil</c>, a null item in a collection, an aliased value with its formatting
/// members set. Malformed XML, a prohibited DTD, a wrong root and a value that cannot be converted throw
/// <see cref="SerializationException"/>, as the SDK path does (it wraps XML errors in that type). The element nesting is
/// bounded by <see cref="MaxDepth"/> (the stack guard of this reader): a deeper payload is "unknown" too, so the caller reads it
/// with the SDK reader, as before, and a hostile payload cannot exhaust the stack here.
/// </para>
/// </summary>
internal static class ContractXmlReader
{
    /// <summary>The deepest element nesting accepted (Dataverse limits the context depth to 8; nested entities and queries stay far below). Stack use per level is about 1 KB. A deeper payload reports "unknown" (the SDK reader reads it).</summary>
    public const int MaxDepth = 256;

    /// <summary>Raised inside the reader when the payload is outside what the model carries; <c>TryRead</c> turns it into false.</summary>
    private sealed class UnknownException : Exception;

    // A new instance per throw: exceptions are not shared between threads.
    private static UnknownException Unknown => new();

    // ---- entry points -------------------------------------------------------------------------------------------------

    public static bool TryReadContext(string xml, out RemoteContext? context)
    {
        ArgumentNullException.ThrowIfNull(xml);
        try
        {
            var root = Parse(xml, "RemoteExecutionContext");
            if (root.Name != "RemoteExecutionContext" || root.Ns != Contracts)
                throw Fail($"The payload is not a remote execution context: its root element is '{root.Name}' in namespace '{root.Ns}'.");
            if (root.Nil) throw Fail("The asyncoperation data is not an execution context.");
            if (root.TypeName is not null) throw Unknown;
            context = ReadContext(root, 1);
            return true;
        }
        catch (UnknownException)
        {
            context = null;
            return false;
        }
    }

    public static bool TryReadQuery(string xml, out QueryDefinition? query)
    {
        ArgumentNullException.ThrowIfNull(xml);
        try
        {
            var root = Parse(xml, "QueryBase");
            if (root.Name != "QueryBase" || root.Ns != Contracts)
                throw Fail($"The payload is not a query: its root element is '{root.Name}' in namespace '{root.Ns}'.");
            if (root.Nil) throw Fail("The asyncoperation data is not a query.");
            // Without a type hint the SDK reader fails on the abstract base type (not with a SerializationException): report
            // "unknown" so the caller's fallback reproduces today's exception.
            if (root.TypeName is null) throw Unknown;
            var hint = root.TypeName;
            var hintNs = root.TypeNs;
            root.TypeName = null;
            root.TypeNs = null;
            query = hintNs == Contracts
                ? hint switch
                {
                    QueryExpressionType => ReadRecordQuery(root, 1),
                    QueryByAttributeType => ReadAttributeQuery(root, 1),
                    FetchExpressionType => ReadFetchQuery(root, 1),
                    _ => throw Unknown,
                }
                : throw Unknown;
            return true;
        }
        catch (UnknownException)
        {
            query = null;
            return false;
        }
    }

    // ---- the tree -----------------------------------------------------------------------------------------------------

    private sealed class Node(string name, string ns)
    {
        public readonly string Name = name;
        public readonly string Ns = ns;
        public readonly List<Node> Children = new();
        public string Text = "";
        public bool Nil;
        public string? TypeNs;
        public string? TypeName;
        public bool OtherAttributes;
        public bool HasNonWhitespaceText;
        public bool HasCData;
    }

    private static SerializationException Fail(string message, Exception? inner = null) => new(message, inner);

    private static SerializationException Wrap(string rootName, string message, Exception? inner = null) =>
        new($"There was an error deserializing the {(rootName == "QueryBase" ? "query" : "remote execution context")}. {message}", inner);

    private static Node Parse(string xml, string rootName)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            Node? root = null;
            var stack = new Stack<Node>();
            var text = new Stack<System.Text.StringBuilder>();
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (stack.Count + 1 > MaxDepth) throw Unknown;
                        var node = new Node(reader.LocalName, reader.NamespaceURI);
                        var empty = reader.IsEmptyElement;
                        if (reader.HasAttributes)
                        {
                            while (reader.MoveToNextAttribute())
                            {
                                if (reader.Prefix == "xmlns" || (reader.Prefix == "" && reader.LocalName == "xmlns")) continue;
                                if (reader.NamespaceURI == XmlSchemaInstance && reader.LocalName == "nil")
                                    node.Nil = ParseBool(reader.Value);
                                else if (reader.NamespaceURI == XmlSchemaInstance && reader.LocalName == "type")
                                    (node.TypeNs, node.TypeName) = ResolveType(reader, reader.Value);
                                else node.OtherAttributes = true;
                            }
                            reader.MoveToElement();
                        }
                        if (root is null) root = node;
                        else stack.Peek().Children.Add(node);
                        if (!empty)
                        {
                            stack.Push(node);
                            text.Push(new System.Text.StringBuilder());
                        }
                        break;
                    case XmlNodeType.EndElement:
                        var done = stack.Pop();
                        var content = text.Pop().ToString();
                        done.Text = content;
                        done.HasNonWhitespaceText = content.AsSpan().Trim(" \t\r\n").Length > 0;
                        break;
                    case XmlNodeType.CDATA:
                        stack.Peek().HasCData = true;
                        text.Peek().Append(reader.Value);
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        if (text.Count > 0) text.Peek().Append(reader.Value);
                        break;
                }
            }
            return root ?? throw Wrap(rootName, "Root element is missing.");
        }
        catch (XmlException e)
        {
            throw Wrap(rootName, e.Message, e);
        }
    }

    // The value of i:type is a qualified name: its prefix is resolved at the element carrying it, by namespace.
    private static (string? Ns, string? Name) ResolveType(XmlReader reader, string value)
    {
        var text = value;
        var colon = text.IndexOf(':');
        var prefix = colon < 0 ? "" : text[..colon];
        var local = colon < 0 ? text : text[(colon + 1)..];
        return (reader.LookupNamespace(prefix) ?? "", local);
    }

    private static bool ParseBool(string text)
    {
        try { return XmlConvert.ToBoolean(text); }
        catch (FormatException e) { throw Fail($"Content cannot be converted to the type Boolean. {e.Message}", new XmlException(e.Message, e)); }
    }

    // ---- members ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Walks the child elements of a node in contract order: <see cref="Take"/> returns the next child when it is the named
    /// member and null when the member is absent. <see cref="End"/> reports "unknown" when children are left over (an unknown
    /// member, or one out of order: the SDK keeps those as extension data).
    /// </summary>
    private ref struct Cursor
    {
        private readonly Node _node;
        private int _next;

        public Cursor(Node node)
        {
            if (node.HasNonWhitespaceText || node.HasCData) throw Fail($"'Text' is an invalid XmlNodeType. Element '{node.Name}' has no text content.");
            // A type hint is consumed by ReadValue (and the query root) when it names a model type; one left on a node read here
            // names a type the member does not declare (an early-bound subclass, a foreign type): outside the model set.
            if (node.OtherAttributes || node.TypeName is not null) throw Unknown;
            _node = node;
        }

        /// <summary>The next child, which must be the named member: the SDK writes every member, so an absent one is unknown.</summary>
        public Node Take(string name, string ns = Contracts) => Optional(name, ns) ?? throw Unknown;

        /// <summary>The next child when it is the named member, else null: for the members the SDK omits when null.</summary>
        public Node? Optional(string name, string ns = Contracts)
        {
            if (_next < _node.Children.Count && _node.Children[_next] is { } child && child.Name == name && child.Ns == ns)
            {
                _next++;
                return child;
            }
            return null;
        }

        /// <summary>Takes the next child while it is the named member (the items of a list).</summary>
        public Node? Item(string name, string ns = Contracts) => Optional(name, ns);

        public readonly void End()
        {
            if (_next != _node.Children.Count) throw Unknown;
        }
    }

    private static void RequireDepth(int depth)
    {
        if (depth > MaxDepth) throw Unknown;
    }

    // ---- leaf values --------------------------------------------------------------------------------------------------

    private static string Leaf(Node n)
    {
        if (n.Children.Count > 0 || n.OtherAttributes) throw n.OtherAttributes ? Unknown : Fail($"Element '{n.Name}' cannot have child elements.");
        return n.Text;
    }

    private static string? Str(Node? n, string? whenAbsent = null)
    {
        if (n is null) return whenAbsent;
        if (n.Nil) return null;
        return Leaf(n);
    }

    private static T Value<T>(Node? n, Func<string, T> parse, T whenAbsent, string typeName)
    {
        if (n is null) return whenAbsent;
        if (n.Nil) throw Fail($"A {typeName} value is null, but the type does not allow null.");
        try { return parse(Leaf(n)); }
        catch (FormatException e) { throw Fail($"Content cannot be converted to the type {typeName}. {e.Message}", new XmlException(e.Message, e)); }
    }

    private static T? NullableOf<T>(Node? n, Func<string, T> parse, string typeName) where T : struct
    {
        if (n is null || n.Nil) return null;
        return Value(n, parse, default(T), typeName);
    }

    // xs:decimal has no exponent; the scale of the text is kept (1.50 stays 1.50).
    private static decimal ParseDecimal(string text) =>
        decimal.Parse(text, System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite
            | System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture);

    private static int Int(Node? n) => Value(n, XmlConvert.ToInt32, 0, "Int32");

    private static bool Bool(Node? n) => Value(n, ParseBool, false, "Boolean");

    private static Guid GuidOf(Node? n) => Value(n, s => Guid.Parse(s.Trim()), Guid.Empty, "Guid");

    private static DateTime DateTimeOf(Node? n) =>
        Value(n, s => XmlConvert.ToDateTime(s, XmlDateTimeSerializationMode.RoundtripKind), default(DateTime), "DateTime");

    private static T EnumOf<T>(Node? n) where T : struct, Enum
    {
        if (n is null) return default;
        if (n.Nil) throw Fail($"A {typeof(T).Name} value is null, but the type does not allow null.");
        var text = Leaf(n);
        foreach (var name in Enum.GetNames<T>())
            if (name == text) return Enum.Parse<T>(name);
        throw Fail($"'{text}' is not a member of {typeof(T).Name}.");
    }

    private static T? NullableEnum<T>(Node? n) where T : struct, Enum => n is null || n.Nil ? null : EnumOf<T>(n);

    // ---- context ------------------------------------------------------------------------------------------------------

    private static RemoteContext ReadContext(Node n, int depth)
    {
        RequireDepth(depth);
        var c = new Cursor(n);
        var result = new RemoteContext
        {
            BusinessUnitId = GuidOf(c.Take("BusinessUnitId")),
            CorrelationId = GuidOf(c.Take("CorrelationId")),
            Depth = Int(c.Take("Depth")),
            InitiatingUserAgent = Str(c.Take("InitiatingUserAgent")),
            InitiatingUserAzureActiveDirectoryObjectId = GuidOf(c.Take("InitiatingUserAzureActiveDirectoryObjectId")),
            InitiatingUserId = GuidOf(c.Take("InitiatingUserId")),
        };
        ReadParameters(c.Take("InputParameters"), result.InputParameters, depth + 1);
        result.IsExecutingOffline = Bool(c.Take("IsExecutingOffline"));
        result.IsInTransaction = Bool(c.Take("IsInTransaction"));
        result.IsOfflinePlayback = Bool(c.Take("IsOfflinePlayback"));
        result.IsolationMode = Int(c.Take("IsolationMode"));
        result.MessageName = Str(c.Take("MessageName"));
        result.Mode = Int(c.Take("Mode"));
        result.OperationCreatedOn = DateTimeOf(c.Take("OperationCreatedOn"));
        result.OperationId = GuidOf(c.Take("OperationId"));
        result.OrganizationId = GuidOf(c.Take("OrganizationId"));
        result.OrganizationName = Str(c.Take("OrganizationName"));
        ReadParameters(c.Take("OutputParameters"), result.OutputParameters, depth + 1);
        if (c.Take("OwningExtension") is { Nil: false } owner) result.OwningExtension = ReadReference(owner, depth + 1);
        if (c.Take("ParentContext") is { Nil: false } parent) result.ParentContext = ReadContext(parent, depth + 1);
        ReadImages(c.Take("PostEntityImages"), result.PostEntityImages, depth + 1);
        ReadImages(c.Take("PreEntityImages"), result.PreEntityImages, depth + 1);
        result.PrimaryEntityId = GuidOf(c.Take("PrimaryEntityId"));
        result.PrimaryEntityName = Str(c.Take("PrimaryEntityName"));
        result.RequestId = NullableOf(c.Take("RequestId"), s => Guid.Parse(s.Trim()), "Guid");
        result.SecondaryEntityName = Str(c.Take("SecondaryEntityName"));
        ReadParameters(c.Take("SharedVariables"), result.SharedVariables, depth + 1);
        result.Stage = Int(c.Take("Stage"));
        result.UserAzureActiveDirectoryObjectId = GuidOf(c.Take("UserAzureActiveDirectoryObjectId"));
        result.UserId = GuidOf(c.Take("UserId"));
        c.End();
        return result;
    }

    // A null collection member is read by the SDK as a null collection, which the model cannot carry.
    private static Node Collection(Node? n)
    {
        if (n is null) throw Unknown;
        if (n.Nil) throw Unknown;
        return n;
    }

    private static void ReadParameters(Node? n, IDictionary<string, object?> into, int depth)
    {
        if (n is null) return;
        var c = new Cursor(Collection(n));
        while (c.Item(ParameterEntry) is { } entry)
        {
            var (key, value) = ReadEntry(entry, depth + 1);
            into.Add(key, value);
        }
        c.End();
    }

    private static (string Key, object? Value) ReadEntry(Node entry, int depth)
    {
        RequireDepth(depth);
        var c = new Cursor(entry);
        var key = c.Take("key", Generic) ?? throw Unknown;
        var value = c.Take("value", Generic) ?? throw Unknown;
        c.End();
        return (Str(key) ?? throw new ArgumentNullException("key"), ReadValue(value, depth + 1));
    }

    private static void ReadImages(Node? n, IDictionary<string, Record> into, int depth)
    {
        if (n is null) return;
        var c = new Cursor(Collection(n));
        while (c.Item(ImageEntry) is { } entry)
        {
            RequireDepth(depth + 1);
            var e = new Cursor(entry);
            var key = e.Take("key", Generic) ?? throw Unknown;
            var value = e.Take("value", Generic) ?? throw Unknown;
            e.End();
            if (value.Nil) throw Unknown;
            into.Add(Str(key) ?? throw new ArgumentNullException("key"), ReadRecord(value, depth + 2));
        }
        c.End();
    }

    // ---- values -------------------------------------------------------------------------------------------------------

    private static object? ReadValue(Node n, int depth)
    {
        RequireDepth(depth);
        if (n.Nil)
        {
            if (n.OtherAttributes) throw Unknown;
            return null;
        }
        if (n.TypeName is null)
        {
            // An untyped value is a plain object: with content the SDK refuses it; empty, it is outside the model set.
            if (n.Children.Count > 0 || n.HasNonWhitespaceText)
                throw Fail($"Element '{n.Name}' in namespace '{n.Ns}' holds a value where an object is expected.");
            throw Unknown;
        }
        var name = n.TypeName;
        var typeNs = n.TypeNs;
        n.TypeName = null;
        n.TypeNs = null;
        switch (typeNs)
        {
            case XmlSchema:
                return name switch
                {
                    "string" => Leaf(n),
                    "boolean" => Bool(n),
                    "int" => Int(n),
                    "long" => Value(n, XmlConvert.ToInt64, 0L, "Int64"),
                    "double" => Value(n, XmlConvert.ToDouble, 0d, "Double"),
                    "decimal" => Value(n, ParseDecimal, 0m, "Decimal"),
                    "dateTime" => DateTimeOf(n),
                    "base64Binary" => Base64(n),
                    _ => throw Unknown,
                };
            case DataContractSerialization when name == "guid":
                return GuidOf(n);
            case Arrays when name == "ArrayOfstring":
                return ReadStrings(n);
            case Contracts:
                return name switch
                {
                    EntityType => ReadRecord(n, depth),
                    EntityReferenceType => ReadReference(n, depth),
                    EntityCollectionType => ReadCollection(n, depth),
                    EntityReferenceCollectionType => ReadReferenceCollection(n, depth),
                    OptionSetValueType => ReadChoice(n),
                    MoneyType => ReadMoney(n),
                    AliasedValueType => ReadAliased(n, depth),
                    ColumnSetType => ReadColumns(n),
                    RelationshipType => ReadRelationship(n),
                    QueryExpressionType => ReadRecordQuery(n, depth),
                    QueryByAttributeType => ReadAttributeQuery(n, depth),
                    FetchExpressionType => ReadFetchQuery(n, depth),
                    ArrayOfEntityType => ReadArray(n, "Entity", Contracts, x => ReadRecord(x, depth + 1)),
                    ArrayOfEntityReferenceType => ReadArray(n, "EntityReference", Contracts, x => ReadReference(x, depth + 1)),
                    ArrayOfOptionSetValueType => ReadArray(n, "OptionSetValue", Contracts, ReadChoice),
                    ArrayOfMoneyType => ReadArray(n, "Money", Contracts, ReadMoney),
                    _ => throw Unknown,
                };
            case Choices when name == "OptionSetValueCollection":
            case ClrNamespace when name == "OptionSetValueCollection":
                return ReadChoices(n);
            case ClrNamespace when name == EntityReferenceCollectionType:
                return ReadReferenceCollection(n, depth);
            default:
                throw Unknown;
        }
    }

    private static byte[] Base64(Node n)
    {
        var text = Leaf(n);
        try { return Convert.FromBase64String(text); }
        catch (FormatException e) { throw Fail($"The value '{text}' cannot be parsed as the type 'byte[]'.", new XmlException(e.Message, e)); }
    }

    private static string?[] ReadStrings(Node n)
    {
        var c = new Cursor(n);
        var items = new List<string?>();
        while (c.Item("string", Arrays) is { } item) items.Add(Str(item));
        c.End();
        return items.ToArray();
    }

    private static T[] ReadArray<T>(Node n, string itemName, string ns, Func<Node, T> read)
    {
        var c = new Cursor(n);
        var items = new List<T>();
        while (c.Item(itemName, ns) is { } item)
        {
            if (item.Nil) throw Unknown;
            items.Add(read(item));
        }
        c.End();
        return items.ToArray();
    }

    private static ChoiceValue ReadChoice(Node n)
    {
        var c = new Cursor(n);
        var value = Int(c.Take("Value"));
        c.End();
        return new ChoiceValue(value);
    }

    private static MoneyValue ReadMoney(Node n)
    {
        var c = new Cursor(n);
        var value = Value(c.Take("Value"), ParseDecimal, 0m, "Decimal");
        c.End();
        return new MoneyValue(value);
    }

    private static ChoiceValues ReadChoices(Node n)
    {
        var c = new Cursor(n);
        var result = new ChoiceValues();
        while (c.Item("OptionSetValue", Choices) is { } item)
        {
            if (item.Nil) throw Unknown;
            result.Add(ReadChoice(item));
        }
        c.End();
        return result;
    }

    private static AliasedColumnValue ReadAliased(Node n, int depth)
    {
        var c = new Cursor(n);
        var attribute = Str(c.Take("AttributeLogicalName"));
        var entity = Str(c.Take("EntityLogicalName"));
        var needFormatting = Bool(c.Take("NeedFormatting"));
        var returnType = Int(c.Take("ReturnType"));
        var value = c.Take("Value");
        c.End();
        // The model carries neither the formatting flag nor the return type.
        if (needFormatting || returnType != 0) throw Unknown;
        return new AliasedColumnValue(entity, attribute, value is null ? null : ReadValue(value, depth + 1));
    }

    // ---- records ------------------------------------------------------------------------------------------------------

    private static Record ReadRecord(Node n, int depth)
    {
        RequireDepth(depth);
        var c = new Cursor(n);
        var record = new Record();
        if (c.Take("Attributes") is { } attributes)
        {
            var a = new Cursor(Collection(attributes));
            while (a.Item(ParameterEntry) is { } entry)
            {
                var (key, value) = ReadEntry(entry, depth + 2);
                record.Attributes.Add(key, value);
            }
            a.End();
        }
        record.State = NullableEnum<RecordState>(c.Take("EntityState"));
        if (c.Take("FormattedValues") is { } formatted)
        {
            var f = new Cursor(Collection(formatted));
            while (f.Item(FormattedValueEntry) is { } entry)
            {
                var e = new Cursor(entry);
                var key = e.Take("key", Generic) ?? throw Unknown;
                var value = e.Take("value", Generic) ?? throw Unknown;
                e.End();
                // A null formatted value cannot be carried by the model's string dictionary.
                if (value.Nil) throw Unknown;
                record.FormattedValues.Add(Str(key) ?? throw new ArgumentNullException("key"), Leaf(value));
            }
            f.End();
        }
        record.Id = GuidOf(c.Take("Id"));
        ReadKeyAttributes(c.Take("KeyAttributes"), record.KeyAttributes, depth + 1);
        record.LogicalName = Str(c.Take("LogicalName"))!;
        if (c.Take("RelatedEntities") is { } related)
        {
            var r = new Cursor(Collection(related));
            while (r.Item(RelatedEntitiesEntry) is { } entry)
            {
                RequireDepth(depth + 2);
                var e = new Cursor(entry);
                var key = e.Take("key", Generic) ?? throw Unknown;
                var value = e.Take("value", Generic) ?? throw Unknown;
                e.End();
                if (key.Nil || value.Nil) throw Unknown;
                record.RelatedRecords.Add(ReadRelationship(key), ReadCollection(value, depth + 3));
            }
            r.End();
        }
        record.RowVersion = Str(c.Take("RowVersion"));
        c.End();
        return record;
    }

    private static void ReadKeyAttributes(Node? n, IDictionary<string, object?> into, int depth)
    {
        if (n is null) return;
        var c = new Cursor(Collection(n));
        while (c.Item(ParameterEntry, KeyAttributes) is { } entry)
        {
            var (key, value) = ReadEntry(entry, depth + 1);
            into.Add(key, value);
        }
        c.End();
    }

    private static RecordReference ReadReference(Node n, int depth)
    {
        RequireDepth(depth);
        var c = new Cursor(n);
        var result = new RecordReference { Id = GuidOf(c.Take("Id")) };
        ReadKeyAttributes(c.Take("KeyAttributes"), result.KeyAttributes, depth + 1);
        result.LogicalName = Str(c.Take("LogicalName"))!;
        result.Name = Str(c.Take("Name"));
        result.RowVersion = Str(c.Take("RowVersion"));
        c.End();
        return result;
    }

    private static RecordCollection ReadCollection(Node n, int depth)
    {
        RequireDepth(depth);
        var c = new Cursor(n);
        var result = new RecordCollection();
        if (c.Take("Entities") is { } entities)
        {
            var e = new Cursor(Collection(entities));
            while (e.Item("Entity") is { } item)
            {
                if (item.Nil) throw Unknown;
                result.Records.Add(ReadRecord(item, depth + 2));
            }
            e.End();
        }
        result.EntityName = Str(c.Take("EntityName"));
        result.MinActiveRowVersion = Str(c.Take("MinActiveRowVersion"));
        result.MoreRecords = Bool(c.Take("MoreRecords"));
        result.PagingCookie = Str(c.Take("PagingCookie"));
        result.RecordCountLessThanMarv = Int(c.Take("RecordCountLessThanMarv"));
        result.TotalRecordCount = Int(c.Take("TotalRecordCount"));
        result.TotalRecordCountLimitExceeded = Bool(c.Take("TotalRecordCountLimitExceeded"));
        c.End();
        return result;
    }

    private static RecordReferenceCollection ReadReferenceCollection(Node n, int depth)
    {
        var c = new Cursor(n);
        var result = new RecordReferenceCollection();
        while (c.Item("EntityReference") is { } item)
        {
            if (item.Nil) throw Unknown;
            result.Add(ReadReference(item, depth + 1));
        }
        c.End();
        return result;
    }

    private static RelationshipName ReadRelationship(Node n)
    {
        var c = new Cursor(n);
        var role = NullableEnum<RecordRole>(c.Take("PrimaryEntityRole"));
        var schema = Str(c.Take("SchemaName"));
        c.End();
        // The model's relationship name has no null schema name.
        return new RelationshipName(schema ?? throw Unknown, role);
    }

    // ---- queries ------------------------------------------------------------------------------------------------------

    private static FetchQuery ReadFetchQuery(Node n, int depth)
    {
        RequireDepth(depth);
        var c = new Cursor(n);
        var text = Str(c.Take("Query"));
        c.End();
        return new FetchQuery { Text = text };
    }

    private static ColumnSelection ReadColumns(Node n)
    {
        var c = new Cursor(n);
        var result = new ColumnSelection { AllColumns = Bool(c.Take("AllColumns")) };
        if (c.Take("AttributeExpressions") is { } expressions)
        {
            var e = new Cursor(Collection(expressions));
            while (e.Item("AttributeExpression") is { } item)
            {
                if (item.Nil) throw Unknown;
                var x = new Cursor(item);
                var expression = new ColumnExpression
                {
                    Aggregate = EnumOf<ColumnAggregate>(x.Take("AggregateType")),
                    Alias = Str(x.Take("Alias")),
                    AttributeName = Str(x.Take("AttributeName")),
                    DateGrouping = EnumOf<ColumnDateGrouping>(x.Take("DateTimeGrouping")),
                    GroupBy = Bool(x.Take("HasGroupBy")),
                };
                x.End();
                result.Expressions.Add(expression);
            }
            e.End();
        }
        if (c.Take("Columns") is { } columns)
        {
            var e = new Cursor(Collection(columns));
            while (e.Item("string", Arrays) is { } item)
                result.Columns.Add(Str(item) ?? throw Unknown);
            e.End();
        }
        c.End();
        return result;
    }

    private static ColumnSelection? OptionalColumns(Node? n, ColumnSelection? whenAbsent) =>
        n is null ? whenAbsent : n.Nil ? null : ReadColumns(n);

    private static QueryFilter? OptionalFilter(Node? n, QueryFilter? whenAbsent, int depth) =>
        n is null ? whenAbsent : n.Nil ? null : ReadFilter(n, depth);

    private static QueryPaging? OptionalPaging(Node? n, QueryPaging? whenAbsent)
    {
        if (n is null) return whenAbsent;
        if (n.Nil) return null;
        var c = new Cursor(n);
        var result = new QueryPaging
        {
            Count = Int(c.Take("Count")),
            PageNumber = Int(c.Take("PageNumber")),
            PagingCookie = Str(c.Take("PagingCookie")),
            ReturnRecordCountLessThanMarv = Bool(c.Take("ReturnRecordCountLessThanMarv")),
            ReturnTotalRecordCount = Bool(c.Take("ReturnTotalRecordCount")),
        };
        c.End();
        return result;
    }

    private static List<QueryOrder> ReadOrders(Node? n)
    {
        var result = new List<QueryOrder>();
        if (n is null) return result;
        var c = new Cursor(Collection(n));
        while (c.Item("OrderExpression") is { } item)
        {
            if (item.Nil) throw Unknown;
            var o = new Cursor(item);
            var order = new QueryOrder
            {
                Alias = Str(o.Take("Alias")),
                AttributeName = Str(o.Take("AttributeName")),
                EntityName = Str(o.Take("EntityName")),
                Direction = EnumOf<SortDirection>(o.Take("OrderType")),
            };
            o.End();
            result.Add(order);
        }
        c.End();
        return result;
    }

    private static List<object?> ReadValues(Node? n, int depth)
    {
        var result = new List<object?>();
        if (n is null) return result;
        var c = new Cursor(Collection(n));
        while (c.Item("anyType", Arrays) is { } item) result.Add(ReadValue(item, depth + 1));
        c.End();
        return result;
    }

    private static QueryFilter ReadFilter(Node n, int depth)
    {
        RequireDepth(depth);
        var c = new Cursor(n);
        var result = new QueryFilter();
        if (c.Take("AnyAllFilterLinkEntity") is { Nil: false } link) result.AnyAllLink = ReadLink(link, depth + 1);
        if (c.Take("Conditions") is { } conditions)
        {
            var e = new Cursor(Collection(conditions));
            while (e.Item("ConditionExpression") is { } item)
            {
                if (item.Nil) throw Unknown;
                RequireDepth(depth + 2);
                var x = new Cursor(item);
                var condition = new QueryCondition
                {
                    AttributeName = Str(x.Take("AttributeName")),
                    CompareColumns = Bool(x.Take("CompareColumns")),
                    Operator = EnumOf<FilterOperator>(x.Take("Operator")),
                };
                condition.Values = ReadValues(x.Take("Values"), depth + 3);
                condition.EntityName = Str(x.Take("EntityName"));
                x.End();
                result.Conditions.Add(condition);
            }
            e.End();
        }
        result.Logic = EnumOf<FilterLogic>(c.Take("FilterOperator"));
        if (c.Take("Filters") is { } filters)
        {
            var e = new Cursor(Collection(filters));
            while (e.Item("FilterExpression") is { } item)
            {
                if (item.Nil) throw Unknown;
                result.Filters.Add(ReadFilter(item, depth + 2));
            }
            e.End();
        }
        result.IsQuickFindFilter = Bool(c.Optional("IsQuickFindFilter"));
        result.FilterHint = Str(c.Optional("FilterHint"));
        c.End();
        return result;
    }

    private static QueryLink ReadLink(Node n, int depth)
    {
        RequireDepth(depth);
        var c = new Cursor(n);
        var result = new QueryLink
        {
            Columns = OptionalColumns(c.Take("Columns"), new ColumnSelection()),
            Alias = Str(c.Take("EntityAlias")),
            ForceSeek = Str(c.Optional("ForceSeek")),
            Join = EnumOf<JoinKind>(c.Take("JoinOperator")),
            Criteria = OptionalFilter(c.Take("LinkCriteria"), new QueryFilter(), depth + 1),
        };
        if (c.Take("LinkEntities") is { } links)
        {
            var e = new Cursor(Collection(links));
            while (e.Item("LinkEntity") is { } item)
            {
                if (item.Nil) throw Unknown;
                result.Links.Add(ReadLink(item, depth + 2));
            }
            e.End();
        }
        result.FromAttributeName = Str(c.Take("LinkFromAttributeName"));
        result.FromEntityName = Str(c.Take("LinkFromEntityName"));
        result.ToAttributeName = Str(c.Take("LinkToAttributeName"));
        result.ToEntityName = Str(c.Take("LinkToEntityName"));
        result.Orders = ReadOrders(c.Take("Orders"));
        c.End();
        return result;
    }

    private static RecordQuery ReadRecordQuery(Node n, int depth)
    {
        RequireDepth(depth);
        var c = new Cursor(n);
        var result = new RecordQuery
        {
            Columns = OptionalColumns(c.Take("ColumnSet"), new ColumnSelection()),
            Criteria = OptionalFilter(c.Take("Criteria"), new QueryFilter(), depth + 1),
            Distinct = Bool(c.Take("Distinct")),
            EntityName = Str(c.Take("EntityName")),
            ForceSeek = Str(c.Optional("ForceSeek")),
        };
        if (c.Take("LinkEntities") is { } links)
        {
            var e = new Cursor(Collection(links));
            while (e.Item("LinkEntity") is { } item)
            {
                if (item.Nil) throw Unknown;
                result.Links.Add(ReadLink(item, depth + 2));
            }
            e.End();
        }
        result.Orders = ReadOrders(c.Take("Orders"));
        result.Paging = OptionalPaging(c.Take("PageInfo"), new QueryPaging());
        if (c.Optional("SubQueryExpression") is { Nil: false } sub) result.SubQuery = ReadRecordQuery(sub, depth + 1);
        result.NoLock = Bool(c.Take("NoLock"));
        result.TopCount = NullableOf(c.Optional("TopCount"), XmlConvert.ToInt32, "Int32");
        result.QueryHints = Str(c.Optional("QueryHints"));
        result.DataSource = Str(c.Optional("DataSource"));
        c.End();
        return result;
    }

    private static AttributeQuery ReadAttributeQuery(Node n, int depth)
    {
        RequireDepth(depth);
        var c = new Cursor(n);
        var result = new AttributeQuery();
        if (c.Take("Attributes") is { } attributes)
        {
            var e = new Cursor(Collection(attributes));
            while (e.Item("string", Arrays) is { } item) result.Attributes.Add(Str(item) ?? throw Unknown);
            e.End();
        }
        result.Columns = OptionalColumns(c.Take("ColumnSet"), new ColumnSelection());
        result.EntityName = Str(c.Take("EntityName"));
        result.Orders = ReadOrders(c.Take("Orders"));
        result.Paging = OptionalPaging(c.Take("PageInfo"), new QueryPaging());
        result.Values = ReadValues(c.Take("Values"), depth + 1);
        result.TopCount = NullableOf(c.Take("TopCount"), XmlConvert.ToInt32, "Int32");
        c.End();
        return result;
    }
}

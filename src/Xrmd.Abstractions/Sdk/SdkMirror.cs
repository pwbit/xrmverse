using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xrmd.Model.Messages;
using Xrmd.Model.Query;
using Xrmd.Model.Records;

namespace Xrmd.Abstractions.Sdk;

/// <summary>
/// The identity-preserving two-way map between SDK objects and their model twins, one per execution chain. Where
/// <see cref="SdkRecords"/> and <see cref="SdkMessages"/> copy into new instances, the mirror gives every SDK object exactly one
/// model twin (and back) and writes state into the existing twin at each crossing: <see cref="Out(object?)"/> writes the whole
/// state of a model object into its SDK twin (creating and pairing a plain one when none exists), <see cref="In(object?)"/> the
/// reverse. Object identity, CLR subtypes (typed requests and responses, early-bound entities, a request carrying a
/// placeholder query) and the marks keyed on a request instance therefore survive any number of crossings.
/// <para>
/// A twin is paired before its contents are written, so cycles map to cycles, and an object reachable twice in one crossing is
/// written once. Containers (attribute and parameter collections, entity lists, query parts) are cleared and refilled in source
/// order; an SDK container that already holds the same keys in the same order only has its values set, and an SDK list that already
/// holds the same items is left as it is, so an enumeration a plug-in keeps open across a service call (around which its view is
/// read back and written again) stays valid. The immutable model values (<see cref="ChoiceValue"/>, <see cref="MoneyValue"/>, <see cref="AliasedColumnValue"/>,
/// <see cref="RelationshipName"/>) are paired with their mutable SDK counterparts by instance: an SDK value changed in place
/// yields a new model value that takes over the pairing, a model value that is unchanged writes into its original SDK instance, and
/// a model value seen under a second slot of one crossing gets a fresh SDK value, so two attributes never share one. Arrays of the
/// exact known types are twinned by instance and refilled in place while their length stays. Attribute, parameter and condition values
/// convert exactly like <see cref="SdkRecords.ValueToModel"/>: values of any other type (<c>BooleanManagedProperty</c>, <c>byte[]</c>,
/// <c>string[]</c>, subtype arrays, primitives, but also a <c>PagingInfo</c> parameter, an inner request or a filter held as a
/// value) are their own twin, the same instance both ways. The parts of a query, record or message are twinned wherever they are
/// members, and a part or message handed to <see cref="In(object?)"/> directly is twinned.
/// </para>
/// <para>
/// Instance sharing survives as it exists on the SDK side: one SDK value held in several slots (the same <c>OptionSetValue</c> in
/// two attributes, or in every row of a CreateMultiple) is one model value, and writing that model value back into each slot that
/// still holds the paired SDK instance keeps them one object. Only sharing that the model itself creates (a model value placed in a
/// slot that did not hold its SDK twin, for example in an image record) gets a fresh SDK value, so the model never aliases
/// objects that are separate on the SDK side. The pairing is chain-wide, held in weak tables, so objects of a finished nested call
/// can be collected while the mirror lives.
/// </para>
/// <para>
/// Identity follows the slot: a model object that was moved (for example into another attribute) or replaced on the model side keeps
/// its values correct on the SDK side, but the original SDK instance is lost to that slot, because only a slot that still holds
/// the paired SDK instance writes into it; any other slot receives a fresh SDK object.
/// </para>
/// <para>
/// Always <see cref="In(object?)"/> before <see cref="Out(object?)"/>: an <c>Out</c> is authoritative and overwrites an SDK change
/// (including an in-place <c>OptionSetValue.Value</c>) that was not read back first.
/// </para>
/// <para>
/// A model request can carry its chain mirror (<see cref="Register"/>, <see cref="For"/>), so the core adapter converts with it,
/// and a typed SDK response the core returns crosses as an unread <see cref="Shell"/> that is converted only when read in.
/// </para>
/// <para>
/// One lock per mirror serializes every crossing. <see cref="SyncCount"/> counts the objects written so far (an object
/// written twice in two crossings counts twice) and backs the plug-in view's overhead test.
/// </para>
/// </summary>
public sealed class SdkMirror
{
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<object, object> _toSdk = new();
    private readonly ConditionalWeakTable<object, object> _toModel = new();
    private readonly ConditionalWeakTable<Response, object> _unread = new();
    private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);
    private readonly IFillMap _sdkMap;
    private readonly IFillMap _modelMap;
    private int _depth;
    private long _syncCount;

    /// <summary>The chain mirror of each registered model request (see <see cref="Register"/>); weak, and locked on itself.</summary>
    private static readonly ConditionalWeakTable<Request, SdkMirror> Registry = new();

    /// <summary>The mark of an unread shell: the name it was made with, which a model write must not have changed.</summary>
    private sealed class UnreadMark(string? name)
    {
        public string? Name { get; } = name;
    }

    public SdkMirror()
    {
        _sdkMap = new DirectionMap(this, true);
        _modelMap = new DirectionMap(this, false);
    }

    /// <summary>The number of objects written so far.</summary>
    public long SyncCount
    {
        get
        {
            lock (_gate) return _syncCount;
        }
    }

    /// <summary>Writes the state of a model object into its SDK twin and returns the twin; see the class remarks.</summary>
    public object? Out(object? model) => Cross(model, true);

    /// <summary>Writes the state of an SDK object into its model twin and returns the twin; see the class remarks.</summary>
    public object? In(object? sdk) => Cross(sdk, false);

    public ParameterCollection? Out(ParameterBag? model) => (ParameterCollection?)Cross(model, true);

    public ParameterBag? In(ParameterCollection? sdk) => (ParameterBag?)Cross(sdk, false);

    public OrganizationRequest? Out(Request? model) => (OrganizationRequest?)Cross(model, true);

    public Request? In(OrganizationRequest? sdk) => (Request?)Cross(sdk, false);

    public OrganizationResponse? Out(Response? model) => (OrganizationResponse?)Cross(model, true);

    public Response? In(OrganizationResponse? sdk) => (Response?)Cross(sdk, false);

    /// <summary>
    /// Registers <paramref name="mirror"/> as the chain mirror of a model request, so that the core adapter converts with the
    /// mirror of the chain the request belongs to and twins, typed requests and typed responses keep their identity across the
    /// core. The entry is weak: it lives as long as the request. Registering the same mirror again does nothing. A model request
    /// belongs to one chain: a nested call of the chain registers the same mirror, and a retry or a call in another chain converts
    /// the SDK request again, which gives a new model request, so one model request is never run in two chains.
    /// </summary>
    /// <exception cref="InvalidOperationException">The request is already registered with another mirror.</exception>
    public static void Register(Request request, SdkMirror mirror)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mirror);
        lock (Registry)
        {
            if (Registry.TryGetValue(request, out var existing))
            {
                if (!ReferenceEquals(existing, mirror))
                    throw new InvalidOperationException($"The request '{request.Name}' is already registered with another mirror.");
                return;
            }
            Registry.Add(request, mirror);
        }
    }

    /// <summary>The mirror registered for a model request with <see cref="Register"/>, or null.</summary>
    public static SdkMirror? For(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (Registry) return Registry.TryGetValue(request, out var mirror) ? mirror : null;
    }

    /// <summary>
    /// The model response of a typed SDK response, without reading its results. When <paramref name="sdk"/> already has a model
    /// twin, that twin is returned. Otherwise a new <see cref="Response"/> carrying only the name is paired with it and marked
    /// unread (a <em>shell</em>), and nothing is counted in <see cref="SyncCount"/>.
    /// <para>
    /// While a shell is unread, its <see cref="Response.Results"/> are empty and <see cref="Out(Response?)"/> returns its twin
    /// unchanged, without writing it or counting it, so a response nobody observes is never converted. A model write to an unread
    /// shell (a result added, the results replaced by a non-empty bag, or the name changed from the one the shell was made with)
    /// cannot reach the twin, so <see cref="Out(Response?)"/> then throws <see cref="InvalidOperationException"/> instead of
    /// dropping it. A read cannot be detected: an unread shell reads as empty. <see cref="In(OrganizationResponse?)"/> of the twin
    /// fills the shell and clears the mark, after which it is an ordinary twin. Every model read or write of a core response therefore calls
    /// <c>mirror.In(twin)</c> first.
    /// </para>
    /// </summary>
    public Response Shell(OrganizationResponse sdk)
    {
        ArgumentNullException.ThrowIfNull(sdk);
        lock (_gate)
        {
            if (_toModel.TryGetValue(sdk, out var existing)) return (Response)existing;
            var shell = new Response { Name = sdk.ResponseName };
            _toSdk.AddOrUpdate(shell, sdk);
            _toModel.AddOrUpdate(sdk, shell);
            _unread.AddOrUpdate(shell, new UnreadMark(shell.Name));
            return shell;
        }
    }

    /// <summary>True while <paramref name="response"/> is a shell (see <see cref="Shell"/>) whose twin has not been read in.</summary>
    public bool IsUnread(Response response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate) return _unread.TryGetValue(response, out _);
    }

    /// <summary>
    /// Registers an existing pair (the adapter's context objects), replacing any pairing of either object. Registering a pair that
    /// already exists changes nothing; when the pairing changes, a shell either object was paired with stops being unread.
    /// </summary>
    public void Pair(object model, object sdk)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sdk);
        lock (_gate)
        {
            if (_toSdk.TryGetValue(model, out var oldSdk))
            {
                if (ReferenceEquals(oldSdk, sdk)) return; // the same pair: nothing changes, an unread shell stays unread
                _toModel.Remove(oldSdk);
            }
            if (_toModel.TryGetValue(sdk, out var oldModel))
            {
                _toSdk.Remove(oldModel);
                if (oldModel is Response oldResponse) _unread.Remove(oldResponse);
            }
            if (model is Response response) _unread.Remove(response);
            _toSdk.AddOrUpdate(model, sdk);
            _toModel.AddOrUpdate(sdk, model);
        }
    }

    public bool TryGetSdk(object model, out object? sdk)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate) return _toSdk.TryGetValue(model, out sdk);
    }

    public bool TryGetModel(object sdk, out object? model)
    {
        ArgumentNullException.ThrowIfNull(sdk);
        lock (_gate) return _toModel.TryGetValue(sdk, out model);
    }

    /// <summary>
    /// True when <paramref name="model"/> has no SDK twin, or its twin is of exactly the plain SDK type the mirror itself creates for
    /// it (an <c>Entity</c> for a <see cref="Record"/>, a <c>FilterExpression</c> for a <see cref="QueryFilter"/>, ...) and carries no
    /// extension data. False for a twin of another type (an early-bound <c>Entity</c> subclass, a subclass of any other known SDK
    /// type) or one deserialized with members its contract does not list: the SDK view of such an object is written differently from
    /// its model value, so a carrier of the model hands it to the SDK serializers instead (see <c>ModelSet.CanCarry</c>).
    /// </summary>
    public bool IsPlainTwin(object model)
    {
        ArgumentNullException.ThrowIfNull(model);
        object? sdk;
        lock (_gate)
        {
            if (!_toSdk.TryGetValue(model, out sdk)) return true;
        }
        return PlainSdkTypes.TryGetValue(model.GetType(), out var plain)
               && sdk.GetType() == plain
               && sdk is not System.Runtime.Serialization.IExtensibleDataObject { ExtensionData: not null };
    }

    /// <summary>The SDK type the mirror creates as the twin of each model type it twins.</summary>
    private static readonly Dictionary<Type, Type> PlainSdkTypes = new()
    {
        [typeof(Record)] = typeof(Entity),
        [typeof(RecordReference)] = typeof(EntityReference),
        [typeof(RecordCollection)] = typeof(EntityCollection),
        [typeof(RecordReferenceCollection)] = typeof(EntityReferenceCollection),
        [typeof(ChoiceValues)] = typeof(OptionSetValueCollection),
        [typeof(ColumnSelection)] = typeof(ColumnSet),
        [typeof(RecordQuery)] = typeof(QueryExpression),
        [typeof(AttributeQuery)] = typeof(QueryByAttribute),
        [typeof(FetchQuery)] = typeof(FetchExpression),
        [typeof(ChoiceValue)] = typeof(OptionSetValue),
        [typeof(MoneyValue)] = typeof(Money),
        [typeof(RelationshipName)] = typeof(Relationship),
        [typeof(AliasedColumnValue)] = typeof(AliasedValue),
        [typeof(ColumnExpression)] = typeof(XrmAttributeExpression),
        [typeof(QueryFilter)] = typeof(FilterExpression),
        [typeof(QueryCondition)] = typeof(ConditionExpression),
        [typeof(QueryLink)] = typeof(LinkEntity),
        [typeof(QueryOrder)] = typeof(OrderExpression),
        [typeof(QueryPaging)] = typeof(PagingInfo),
        [typeof(Record[])] = typeof(Entity[]),
        [typeof(RecordReference[])] = typeof(EntityReference[]),
        [typeof(ChoiceValue[])] = typeof(OptionSetValue[]),
        [typeof(MoneyValue[])] = typeof(Money[]),
    };

    private object? Cross(object? value, bool toSdk)
    {
        lock (_gate)
        {
            _depth++;
            try
            {
                return toSdk ? ToSdkTop(value) : ToModelTop(value);
            }
            finally
            {
                if (--_depth == 0) _visited.Clear();
            }
        }
    }

    private sealed class DirectionMap(SdkMirror mirror, bool toSdk) : IFillMap
    {
        public object? Child(object? value) => toSdk ? mirror.ToSdkChild(value) : mirror.ToModelChild(value);

        public object? Value(object? value, object? current) => toSdk ? mirror.ToSdkValue(value, current, true) : mirror.ToModelValue(value);

        public bool AllowNullElements => true;
    }

    // Top level: messages and bags (never twinned as values), then everything a member can be.
    private object? ToSdkTop(object? value) => value switch
    {
        ParameterBag b => Twin(b, _toSdk, _toModel, () => new ParameterCollection(), SdkMessages.Fill, _sdkMap),
        Request r => Twin(r, _toSdk, _toModel, () => new OrganizationRequest(), SdkMessages.Fill, _sdkMap),
        Response r when _unread.TryGetValue(r, out var mark) => UnreadTwin(r, (UnreadMark)mark),
        Response r => Twin(r, _toSdk, _toModel, () => new OrganizationResponse(), SdkMessages.Fill, _sdkMap),
        _ => ToSdkChild(value),
    };

    private object? ToModelTop(object? value) => value switch
    {
        ParameterCollection b => Twin(b, _toModel, _toSdk, () => new ParameterBag(), SdkMessages.Fill, _modelMap),
        OrganizationRequest r => Twin(r, _toModel, _toSdk, () => new Request(), SdkMessages.Fill, _modelMap),
        OrganizationResponse r => ReadResponse(Twin(r, _toModel, _toSdk, () => new Response(), SdkMessages.Fill, _modelMap)),
        _ => ToModelChild(value),
    };

    /// <summary>
    /// The twin of an unread shell, returned as it is: nothing is written and nothing is counted. A shell that was written on the
    /// model side (results added or replaced, or the name changed from the one it was made with) is refused, so the write is never
    /// dropped silently. The name is compared with the one recorded by <see cref="Shell"/>, so SDK code that renames the twin
    /// afterwards does not make an untouched shell look written.
    /// </summary>
    private object UnreadTwin(Response shell, UnreadMark mark)
    {
        if (!_toSdk.TryGetValue(shell, out var twin)) throw new InvalidOperationException("An unread shell has no SDK twin.");
        if (shell.Results is not { Count: 0 } || shell.Name != mark.Name)
            throw new InvalidOperationException(
                $"The response '{shell.Name}' was written on the model side while unread: a core response must be read in "
                + "(mirror.In(twin)) before it is written.");
        return twin;
    }

    /// <summary>A response that has been read in is no longer an unread shell.</summary>
    private Response ReadResponse(Response response)
    {
        _unread.Remove(response);
        return response;
    }

    // Members of a record, query or message: the query parts first, then the value types (no slot).
    private object? ToSdkChild(object? value) => value switch
    {
        ColumnExpression e => Twin(e, _toSdk, _toModel, () => new XrmAttributeExpression(), (f, t, _) => SdkQueries.Fill(f, t), _sdkMap),
        QueryFilter f => Twin(f, _toSdk, _toModel, () => new FilterExpression(), SdkQueries.Fill, _sdkMap),
        QueryCondition c => Twin(c, _toSdk, _toModel, () => new ConditionExpression(), SdkQueries.Fill, _sdkMap),
        QueryLink l => Twin(l, _toSdk, _toModel, () => new LinkEntity(), SdkQueries.Fill, _sdkMap),
        QueryOrder o => Twin(o, _toSdk, _toModel, () => new OrderExpression(), (f, t, _) => SdkQueries.Fill(f, t), _sdkMap),
        QueryPaging p => Twin(p, _toSdk, _toModel, () => new PagingInfo(), (f, t, _) => SdkQueries.Fill(f, t), _sdkMap),
        _ => ToSdkValue(value, null, false),
    };

    private object? ToModelChild(object? value) => value switch
    {
        XrmAttributeExpression e => Twin(e, _toModel, _toSdk, () => new ColumnExpression(), (f, t, _) => SdkQueries.Fill(f, t), _modelMap),
        FilterExpression f => Twin(f, _toModel, _toSdk, () => new QueryFilter(), SdkQueries.Fill, _modelMap),
        ConditionExpression c => Twin(c, _toModel, _toSdk, () => new QueryCondition(), SdkQueries.Fill, _modelMap),
        LinkEntity l => Twin(l, _toModel, _toSdk, () => new QueryLink(), SdkQueries.Fill, _modelMap),
        OrderExpression o => Twin(o, _toModel, _toSdk, () => new QueryOrder(), (f, t, _) => SdkQueries.Fill(f, t), _modelMap),
        PagingInfo p => Twin(p, _toModel, _toSdk, () => new QueryPaging(), (f, t, _) => SdkQueries.Fill(f, t), _modelMap),
        _ => ToModelValue(value),
    };

    // The known value types, exactly the set of SdkRecords.ValueToModel / ValueToSdk; anything else is its own twin.
    private object? ToSdkValue(object? value, object? current, bool hasSlot) => value switch
    {
        null => null,
        Record r => Twin(r, _toSdk, _toModel, () => new Entity(), SdkRecords.Fill, _sdkMap),
        RecordReference r => Twin(r, _toSdk, _toModel, () => new EntityReference(), SdkRecords.Fill, _sdkMap),
        RecordCollection c => Twin(c, _toSdk, _toModel, () => new EntityCollection(), SdkRecords.Fill, _sdkMap),
        RecordReferenceCollection c => Twin(c, _toSdk, _toModel, () => new EntityReferenceCollection(), SdkRecords.Fill, _sdkMap),
        ChoiceValues c => Twin(c, _toSdk, _toModel, () => new OptionSetValueCollection(), SdkRecords.Fill, _sdkMap),
        ColumnSelection c => Twin(c, _toSdk, _toModel, () => new ColumnSet(), SdkRecords.Fill, _sdkMap),
        RecordQuery q => Twin(q, _toSdk, _toModel, () => new QueryExpression(), SdkQueries.Fill, _sdkMap),
        AttributeQuery q => Twin(q, _toSdk, _toModel, () => new QueryByAttribute(), SdkQueries.Fill, _sdkMap),
        FetchQuery q => Twin(q, _toSdk, _toModel, () => new FetchExpression(string.Empty), SdkQueries.Fill, _sdkMap),
        ChoiceValue o => ValueOut(o, current, hasSlot, () => new OptionSetValue(), (s, m) => s.Value = m.Value),
        MoneyValue m => ValueOut(m, current, hasSlot, () => new Money(), (s, v) => s.Value = v.Value),
        RelationshipName r => ValueOut(r, current, hasSlot, () => new Relationship(), (s, m) => SdkRecords.Fill(m, s)),
        AliasedColumnValue a => AliasedOut(a, current, hasSlot),
        Record[] a when a.GetType() == typeof(Record[]) => ArrayTwin<Record, Entity>(a, _toSdk, _toModel, (v, _) => ToSdkChild(v)),
        RecordReference[] a when a.GetType() == typeof(RecordReference[]) => ArrayTwin<RecordReference, EntityReference>(a, _toSdk, _toModel, (v, _) => ToSdkChild(v)),
        ChoiceValue[] a when a.GetType() == typeof(ChoiceValue[]) => ArrayTwin<ChoiceValue, OptionSetValue>(a, _toSdk, _toModel, (v, c) => ToSdkValue(v, c, true)),
        MoneyValue[] a when a.GetType() == typeof(MoneyValue[]) => ArrayTwin<MoneyValue, Money>(a, _toSdk, _toModel, (v, c) => ToSdkValue(v, c, true)),
        object[] a when a.GetType() == typeof(object[]) => ArrayTwin<object, object>(a, _toSdk, _toModel, (v, c) => ToSdkValue(v, c, true)),
        _ => value,
    };

    private object? ToModelValue(object? value) => value switch
    {
        null => null,
        Entity e => Twin(e, _toModel, _toSdk, () => new Record(), SdkRecords.Fill, _modelMap),
        EntityReference r => Twin(r, _toModel, _toSdk, () => new RecordReference(), SdkRecords.Fill, _modelMap),
        EntityCollection c => Twin(c, _toModel, _toSdk, () => new RecordCollection(), SdkRecords.Fill, _modelMap),
        EntityReferenceCollection c => Twin(c, _toModel, _toSdk, () => new RecordReferenceCollection(), SdkRecords.Fill, _modelMap),
        OptionSetValueCollection c => Twin(c, _toModel, _toSdk, () => new ChoiceValues(), SdkRecords.Fill, _modelMap),
        ColumnSet c => Twin(c, _toModel, _toSdk, () => new ColumnSelection(), SdkRecords.Fill, _modelMap),
        QueryExpression q => Twin(q, _toModel, _toSdk, () => new RecordQuery(), SdkQueries.Fill, _modelMap),
        QueryByAttribute q => Twin(q, _toModel, _toSdk, () => new AttributeQuery(), SdkQueries.Fill, _modelMap),
        FetchExpression q => Twin(q, _toModel, _toSdk, () => new FetchQuery(), SdkQueries.Fill, _modelMap),
        OptionSetValue o => ValueIn(o, () => new ChoiceValue(o.Value)),
        Money m => ValueIn(m, () => new MoneyValue(m.Value)),
        Relationship r => ValueIn(r, () => r.ToModel()),
        AliasedValue a => ValueIn(a, () => new AliasedColumnValue(a.EntityLogicalName, a.AttributeLogicalName, ToModelValue(a.Value))),
        Entity[] a when a.GetType() == typeof(Entity[]) => ArrayTwin<Entity, Record>(a, _toModel, _toSdk, (v, _) => ToModelChild(v)),
        EntityReference[] a when a.GetType() == typeof(EntityReference[]) => ArrayTwin<EntityReference, RecordReference>(a, _toModel, _toSdk, (v, _) => ToModelChild(v)),
        OptionSetValue[] a when a.GetType() == typeof(OptionSetValue[]) => ArrayTwin<OptionSetValue, ChoiceValue>(a, _toModel, _toSdk, (v, _) => ToModelValue(v)),
        Money[] a when a.GetType() == typeof(Money[]) => ArrayTwin<Money, MoneyValue>(a, _toModel, _toSdk, (v, _) => ToModelValue(v)),
        object[] a when a.GetType() == typeof(object[]) => ArrayTwin<object, object>(a, _toModel, _toSdk, (v, _) => ToModelValue(v)),
        _ => value,
    };

    /// <summary>The twin of a mutable object: found or created and paired first, then (once per crossing) written.</summary>
    private TTo Twin<TFrom, TTo>(TFrom from, ConditionalWeakTable<object, object> forward, ConditionalWeakTable<object, object> reverse,
        Func<TTo> create, Action<TFrom, TTo, IFillMap> fill, IFillMap map)
        where TFrom : class where TTo : class
    {
        if (!forward.TryGetValue(from, out var existing))
        {
            existing = create();
            forward.AddOrUpdate(from, existing);
            reverse.AddOrUpdate(existing, from);
        }
        var to = (TTo)existing;
        if (_visited.Add(from))
        {
            fill(from, to, map);
            _syncCount++;
        }
        return to;
    }

    private TElementTo[] ArrayTwin<TElementFrom, TElementTo>(TElementFrom[] from, ConditionalWeakTable<object, object> forward,
        ConditionalWeakTable<object, object> reverse, Func<object?, object?, object?> element)
    {
        var reused = forward.TryGetValue(from, out var existing) && existing is TElementTo[] same && same.Length == from.Length;
        var target = reused ? (TElementTo[])existing! : new TElementTo[from.Length];
        if (!reused)
        {
            if (existing is not null) reverse.Remove(existing);
            forward.AddOrUpdate(from, target);
            reverse.AddOrUpdate(target, from);
        }
        if (_visited.Add(from))
        {
            for (var i = 0; i < from.Length; i++) target[i] = (TElementTo)element(from[i], reused ? target[i] : null)!;
            _syncCount++;
        }
        return target;
    }

    /// <summary>The model value of a mutable SDK value: the paired one while it still equals the SDK value, else a new one that takes over.</summary>
    private TModel ValueIn<TSdk, TModel>(TSdk sdk, Func<TModel> current) where TSdk : class where TModel : class
    {
        var candidate = current();
        if (_visited.Add(sdk)) _syncCount++;
        if (_toModel.TryGetValue(sdk, out var existing))
        {
            if (candidate.Equals(existing)) return (TModel)existing;
            _toSdk.Remove(existing);
        }
        _toModel.AddOrUpdate(sdk, candidate);
        _toSdk.AddOrUpdate(candidate, sdk);
        return candidate;
    }

    /// <summary>
    /// The SDK value of an immutable model value: the paired instance (written) when the slot still holds it, or when there is no
    /// slot; otherwise (the model placed the value somewhere that did not hold its twin) a fresh instance, the pairing staying.
    /// </summary>
    private TSdk ValueOut<TModel, TSdk>(TModel model, object? current, bool hasSlot, Func<TSdk> create, Action<TSdk, TModel> write)
        where TModel : class where TSdk : class
    {
        TSdk sdk;
        if (_toSdk.TryGetValue(model, out var paired))
            sdk = !hasSlot || ReferenceEquals(paired, current) ? (TSdk)paired : create();
        else
        {
            sdk = create();
            _toSdk.AddOrUpdate(model, sdk);
            _toModel.AddOrUpdate(sdk, model);
        }
        write(sdk, model);
        _syncCount++;
        return sdk;
    }

    /// <summary>
    /// An SDK <c>AliasedValue</c> has no setters, so the paired one is reused only while its names and its (twinned) value are
    /// unchanged and the slot still holds it; otherwise a new one is made (and takes over the pairing when the old one is not
    /// in the slot either).
    /// </summary>
    private AliasedValue AliasedOut(AliasedColumnValue model, object? current, bool hasSlot)
    {
        var inner = ToSdkValue(model.Value, (current as AliasedValue)?.Value, true);
        _syncCount++;
        if (_toSdk.TryGetValue(model, out var existing))
        {
            var paired = (AliasedValue)existing;
            var same = paired.EntityLogicalName == model.EntityLogicalName && paired.AttributeLogicalName == model.AttributeLogicalName
                && Equals(paired.Value, inner);
            if (same && (!hasSlot || ReferenceEquals(paired, current))) return paired;
            var fresh = new AliasedValue(model.EntityLogicalName, model.AttributeLogicalName, inner);
            if (!same)
            {
                _toModel.Remove(paired);
                _toSdk.AddOrUpdate(model, fresh);
                _toModel.AddOrUpdate(fresh, model);
            }
            return fresh;
        }
        var created = new AliasedValue(model.EntityLogicalName, model.AttributeLogicalName, inner);
        _toSdk.AddOrUpdate(model, created);
        _toModel.AddOrUpdate(created, model);
        return created;
    }
}

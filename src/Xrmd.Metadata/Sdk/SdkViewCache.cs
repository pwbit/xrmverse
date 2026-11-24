using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Model.Metadata;

namespace Xrmd.Metadata.Sdk;

/// <summary>
/// A cached SDK view of model metadata. The metadata store holds the own model types; the modules that still read SDK
/// types get this view. A view is built once per model instance (<see cref="ConditionalWeakTable{TKey,TValue}"/>, so it
/// lives exactly as long as the model object and concurrent first reads store a single value) and every later call
/// returns the same instance. A list is cached per list instance: the store hands out shared snapshot lists that are
/// rebuilt only when metadata changes, and the view elements are the same instances as <c>Of(element)</c>. A cached list
/// is read-only (callers share it).
/// </summary>
/// <remarks>
/// Elements of a converted table (its <c>Attributes</c>, relationships) are produced by converting the whole table.
/// <c>Of(column)</c> for a column taken from a model table is therefore a different instance from the column inside
/// <c>Of(table)</c>; the store never hands out the same attribute both ways.
/// <para>
/// For test fakes of <c>IMetadataStore</c> that keep SDK fixtures (S.2b2): <c>ModelOf</c> gives the model of an SDK
/// instance, cached per instance the same way, and records that SDK instance as the view of that model, so
/// <c>Of(ModelOf(x))</c> is <c>x</c> itself (same identity, no second conversion). The model is converted on the first call:
/// a fixture changed after that is not seen. The store itself keeps the model and never calls it.
/// </para>
/// </remarks>
internal static class SdkViewCache
{
    private static readonly ConditionalWeakTable<TableDefinition, EntityMetadata> Tables = new();
    private static readonly ConditionalWeakTable<ColumnDefinition, AttributeMetadata> Columns = new();
    private static readonly ConditionalWeakTable<ChoiceSetBase, OptionSetMetadataBase> ChoiceSets = new();
    private static readonly ConditionalWeakTable<RelationshipDefinition, RelationshipMetadataBase> Relationships = new();

    // Per model list instance: the converted list. A list instance is converted to one element type only.
    private static readonly ConditionalWeakTable<object, object> Lists = new();

    // The reverse direction: SDK view (or paired SDK instance) -> its model, element-wise for the columns and relationships of a
    // table. Twins of the model helpers (Sdk/Sdk*.cs) look a view up here instead of converting it again on every call.
    private static readonly ConditionalWeakTable<object, object> ModelsOfViews = new();

    private static readonly ConditionalWeakTable<TableDefinition, EntityMetadata>.CreateValueCallback TableFactory =
        static table => Registered(SdkMetadataBridge.ToSdk(table), table);
    private static readonly ConditionalWeakTable<ColumnDefinition, AttributeMetadata>.CreateValueCallback ColumnFactory =
        static column => Registered(SdkMetadataBridge.ToSdk(column), column);
    private static readonly ConditionalWeakTable<ChoiceSetBase, OptionSetMetadataBase>.CreateValueCallback ChoiceSetFactory =
        static choiceSet => Registered(SdkMetadataBridge.ToSdk(choiceSet), choiceSet);
    private static readonly ConditionalWeakTable<RelationshipDefinition, RelationshipMetadataBase>.CreateValueCallback RelationshipFactory =
        static relationship => Registered(SdkMetadataBridge.ToSdk(relationship), relationship);

    // SDK instance -> model, for the test fakes (see remarks).
    private static readonly ConditionalWeakTable<EntityMetadata, TableDefinition> TableModels = new();
    private static readonly ConditionalWeakTable<OptionSetMetadataBase, ChoiceSetBase> ChoiceSetModels = new();
    private static readonly ConditionalWeakTable<RelationshipMetadataBase, RelationshipDefinition> RelationshipModels = new();

    // Each factory records the SDK instance as the view of the new model (a model that loses a concurrent first read is
    // never handed out and goes with its entry).
    private static readonly ConditionalWeakTable<EntityMetadata, TableDefinition>.CreateValueCallback TableModelFactory =
        static entity => Paired(SdkMetadataBridge.ToModel(entity), entity, Tables);
    private static readonly ConditionalWeakTable<OptionSetMetadataBase, ChoiceSetBase>.CreateValueCallback ChoiceSetModelFactory =
        static optionSet => Paired(SdkMetadataBridge.ToModel(optionSet), optionSet, ChoiceSets);
    private static readonly ConditionalWeakTable<RelationshipMetadataBase, RelationshipDefinition>.CreateValueCallback RelationshipModelFactory =
        static relationship => Paired(SdkMetadataBridge.ToModel(relationship), relationship, Relationships);

    public static EntityMetadata Of(TableDefinition table) => Tables.GetValue(table, TableFactory);

    public static AttributeMetadata Of(ColumnDefinition column) => Columns.GetValue(column, ColumnFactory);

    public static OptionSetMetadataBase Of(ChoiceSetBase choiceSet) => ChoiceSets.GetValue(choiceSet, ChoiceSetFactory);

    public static RelationshipMetadataBase Of(RelationshipDefinition relationship) => Relationships.GetValue(relationship, RelationshipFactory);

    /// <summary>
    /// The view of <paramref name="column"/> of <paramref name="table"/>: the matching column of the table's view (so a reader sees
    /// one consistent table), else the column's own view.
    /// </summary>
    public static AttributeMetadata Of(TableDefinition table, ColumnDefinition column)
    {
        var index = table.Attributes?.IndexOf(column) ?? -1;
        return index >= 0 && Of(table).Attributes is { } columns && index < columns.Length ? columns[index] : Of(column);
    }

    /// <summary>The read-only SDK view of a model list, cached per list instance; elements are the same instances as <c>Of(element)</c>.</summary>
    /// <exception cref="InvalidOperationException">The list instance was viewed before with another SDK element type.</exception>
    public static IReadOnlyList<TSdk> Of<TModel, TSdk>(IReadOnlyList<TModel> models)
        where TModel : class
        where TSdk : class =>
        CachedList<TSdk>(models, static list => Convert<TModel, TSdk>((IReadOnlyList<TModel>)list, ElementOf));

    /// <summary>The model of a store SDK table, cached per SDK instance, whose view is that instance (test fakes, see remarks).</summary>
    public static TableDefinition ModelOf(EntityMetadata entity) => TableModels.GetValue(entity, TableModelFactory);

    /// <summary>The model of a store SDK option set, cached per SDK instance, whose view is that instance (test fakes, see remarks).</summary>
    public static ChoiceSetBase ModelOf(OptionSetMetadataBase optionSet) => ChoiceSetModels.GetValue(optionSet, ChoiceSetModelFactory);

    /// <summary>The model of a store SDK relationship, cached per SDK instance, whose view is that instance (test fakes, see remarks).</summary>
    public static RelationshipDefinition ModelOf(RelationshipMetadataBase relationship) =>
        RelationshipModels.GetValue(relationship, RelationshipModelFactory);

    /// <summary>
    /// The model of <paramref name="view"/> when it is a view this cache built (or a paired store instance, or a column or
    /// relationship of one); null for any other SDK object. One <see cref="ConditionalWeakTable{TKey,TValue}"/> lookup.
    /// </summary>
    public static object? ModelOfView(object view) => ModelsOfViews.TryGetValue(view, out var model) ? model : null;

    private static TModel Paired<TModel, TSdk>(TModel model, TSdk sdk, ConditionalWeakTable<TModel, TSdk> views)
        where TModel : class
        where TSdk : class
    {
        views.AddOrUpdate(model, sdk);
        Register(sdk, model);
        return model;
    }

    private static TSdk Registered<TSdk>(TSdk sdk, object model) where TSdk : class
    {
        Register(sdk, model);
        return sdk;
    }

    // Records view -> model, and for a table its columns and relationships element-wise (the view is built from the model's JSON, so
    // the lists have the same order and length; a mismatch is not paired and those twins convert instead).
    private static void Register(object sdk, object model)
    {
        ModelsOfViews.AddOrUpdate(sdk, model);
        if (sdk is EntityMetadata e && model is TableDefinition t)
        {
            Pair(e.Attributes, t.Attributes);
            Pair(e.OneToManyRelationships, t.OneToManyRelationships);
            Pair(e.ManyToOneRelationships, t.ManyToOneRelationships);
            Pair(e.ManyToManyRelationships, t.ManyToManyRelationships);
        }
    }

    private static void Pair<TSdk, TModel>(TSdk[]? views, List<TModel>? models)
        where TSdk : class
        where TModel : class
    {
        if (views is null || models is null || views.Length != models.Count) return;
        for (var i = 0; i < views.Length; i++)
            if (views[i] is { } view && models[i] is { } model) ModelsOfViews.AddOrUpdate(view, model);
    }

    private static IReadOnlyList<TResult> CachedList<TResult>(object list, ConditionalWeakTable<object, object>.CreateValueCallback convert)
        where TResult : class
    {
        var cached = Lists.GetValue(list, convert);
        return cached as IReadOnlyList<TResult>
               ?? throw new InvalidOperationException(
                   $"The list instance ({list.GetType()}) was converted before to {cached.GetType()}; it cannot also give IReadOnlyList<{typeof(TResult).Name}>.");
    }

    private static ReadOnlyCollection<TResult> Convert<TSource, TResult>(IReadOnlyList<TSource> items, Func<object, object> element)
        where TSource : class
        where TResult : class
    {
        var result = new TResult[items.Count];
        for (var i = 0; i < result.Length; i++) result[i] = (TResult)element(items[i]);
        return Array.AsReadOnly(result);
    }

    private static object ElementOf(object model) => model switch
    {
        TableDefinition t => Of(t),
        ColumnDefinition c => Of(c),
        ChoiceSetBase o => Of(o),
        RelationshipDefinition r => Of(r),
        _ => throw new NotSupportedException($"No SDK view for {model.GetType().FullName}."),
    };
}

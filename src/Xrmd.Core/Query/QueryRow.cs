using Xrmd.Model.Records;

namespace Xrmd.Core.Query;

/// <summary>
/// S.2e3b Decision 4 (the dual row sink, ruling 8b): one row of a query result as the read path shapes it. The read path
/// (<see cref="QueryExecutor"/>'s row shaper and its post-processors: field security, party lists, note bodies, the user's date format)
/// computes every value and formatted value once, in the model form, and writes it through this row; the row stores it in its own form.
/// A <see cref="RowSink.Model"/> row is a <see cref="Record"/> that keeps the model value as it is. The SDK sink
/// (<c>QueryExecutorSdk</c>) keeps an SDK <c>Entity</c> and converts each value once as it is written, with the converter that
/// <c>Record.ToSdk</c> uses, so an SDK reader gets the same entity without a model record being built and copied per row.
/// <para>
/// A value read back (<see cref="TryGetValue"/>) is in the model form. The post-processors read only the values they then replace, so
/// a read never needs to stay the same instance as the stored value.
/// </para>
/// </summary>
internal abstract class QueryRow
{
    public abstract string LogicalName { get; }

    public abstract Guid Id { get; set; }

    public abstract string? RowVersion { set; }

    /// <summary>Writes the model value <paramref name="value"/> under <paramref name="key"/> (added, or replaced in place).</summary>
    public abstract object? this[string key] { set; }

    /// <summary>The value under <paramref name="key"/>, in the model form.</summary>
    public abstract bool TryGetValue(string key, out object? value);

    public abstract bool Remove(string key);

    /// <summary>The attribute keys, in order.</summary>
    public abstract IEnumerable<string> Keys { get; }

    public abstract void SetFormatted(string key, string text);

    public abstract bool RemoveFormatted(string key);

    public abstract bool HasFormatted(string key);

    /// <summary>The model value under <paramref name="key"/> as a <typeparamref name="T"/> (as <see cref="Record.GetValue{T}"/>).</summary>
    public T? GetValue<T>(string key) => TryGetValue(key, out var value) && value is not null ? (T)value : default;
}

/// <summary>Makes the rows of a query result: model records (<see cref="Model"/>) or SDK entities (<c>QueryExecutorSdk</c>).</summary>
internal abstract class RowSink
{
    /// <summary>The model sink: each row is a <see cref="Record"/> (<see cref="ModelRow"/>).</summary>
    public static readonly RowSink Model = new ModelSink();

    public abstract QueryRow New(string logicalName);

    private sealed class ModelSink : RowSink
    {
        public override QueryRow New(string logicalName) => new ModelRow(new Record(logicalName));
    }
}

/// <summary>A row kept as a model <see cref="Record"/>.</summary>
internal sealed class ModelRow(Record record) : QueryRow
{
    public Record Record { get; } = record;

    public override string LogicalName => Record.LogicalName;

    public override Guid Id
    {
        get => Record.Id;
        set => Record.Id = value;
    }

    public override string? RowVersion
    {
        set => Record.RowVersion = value;
    }

    public override object? this[string key]
    {
        set => Record.Attributes[key] = value;
    }

    public override bool TryGetValue(string key, out object? value) => Record.Attributes.TryGetValue(key, out value);

    public override bool Remove(string key) => Record.Attributes.Remove(key);

    public override IEnumerable<string> Keys => Record.Attributes.Keys;

    public override void SetFormatted(string key, string text) => Record.FormattedValues[key] = text;

    public override bool RemoveFormatted(string key) => Record.FormattedValues.Remove(key);

    public override bool HasFormatted(string key) => Record.FormattedValues.ContainsKey(key);
}

/// <summary>
/// The rows of a query result and its paging state, before they become a <see cref="RecordCollection"/> (<see cref="ToRecords"/>) or
/// the SDK collection of the SDK sink.
/// </summary>
internal sealed class QueryPage
{
    public required List<QueryRow> Rows { get; init; }

    public string? EntityName { get; init; }

    public bool MoreRecords { get; init; }

    public string? PagingCookie { get; set; }

    public int TotalRecordCount { get; set; }

    public bool TotalRecordCountLimitExceeded { get; set; }

    /// <summary>The page as model records; its rows were made by <see cref="RowSink.Model"/>.</summary>
    public RecordCollection ToRecords() => new()
    {
        Records = Rows.ConvertAll(r => ((ModelRow)r).Record),
        EntityName = EntityName,
        MoreRecords = MoreRecords,
        PagingCookie = PagingCookie,
        TotalRecordCount = TotalRecordCount,
        TotalRecordCountLimitExceeded = TotalRecordCountLimitExceeded,
    };
}

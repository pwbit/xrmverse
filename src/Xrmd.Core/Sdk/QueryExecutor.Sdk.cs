using Microsoft.Xrm.Sdk;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Query.Model;

namespace Xrmd.Core.Query;

/// <summary>
/// S.2e3b: the SDK twin of <see cref="QueryExecutor"/>, whose read path shapes model records
/// (<see cref="QueryExecutor.ExecuteRecordsAsync(QueryModel, Caller, DataSession, CancellationToken)"/>). The twin keeps the old
/// name and signature, so the callers still on the SDK (MCP, Host, Web API helpers, Admin, the TDS endpoint) are not edited; it differs
/// from the model member only in its return type, so it is an extension class.
/// <para>
/// Decision 4 (the dual row sink, ruling 8b): the twin runs the same read path with the SDK row sink (<see cref="Rows"/>), so each
/// <c>Entity</c> is shaped straight from the reader. The values and formatted values are still computed once, in the model form, by
/// the read path; the sink converts each value as it is written, with the converter <c>Record.ToSdk</c> uses
/// (<see cref="SdkRecords.ValueToSdk"/>), so the entity is the one the model record would write out, without that record being built
/// and copied. Transitional: removed when its callers move to the model (S.2f, S.2h).
/// </para>
/// </summary>
public static class QueryExecutorSdk
{
    /// <summary>Test seam: called with each entity the SDK row sink makes, so a test can see that a read shaped it from the reader.</summary>
    internal static readonly AsyncLocal<Action<Entity>?> RowShaped = new();

    /// <summary>The SDK row sink: each row is an <see cref="Entity"/>.</summary>
    internal static readonly RowSink Rows = new SdkSink();

    /// <summary>The SDK twin of <see cref="QueryExecutor.ExecuteRecordsAsync(QueryModel, Caller, CancellationToken)"/>.</summary>
    public static async Task<EntityCollection> ExecuteAsync(this QueryExecutor executor, QueryModel query, Caller caller, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return ToSdk(await executor.ExecutePageAsync(query, caller, Rows, ct));
    }

    /// <summary>The SDK twin of <see cref="QueryExecutor.ExecuteRecordsAsync(QueryModel, Caller, DataSession, CancellationToken)"/>.</summary>
    public static async Task<EntityCollection> ExecuteAsync(this QueryExecutor executor, QueryModel query, Caller caller, DataSession session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return ToSdk(await executor.ExecutePageAsync(query, caller, session, Rows, ct));
    }

    /// <summary>A page made by <see cref="Rows"/>, as the SDK collection (the members <c>RecordCollection.ToSdk</c> writes).</summary>
    internal static EntityCollection ToSdk(QueryPage page)
    {
        var result = new EntityCollection
        {
            EntityName = page.EntityName,
            MoreRecords = page.MoreRecords,
            PagingCookie = page.PagingCookie,
            TotalRecordCount = page.TotalRecordCount,
            TotalRecordCountLimitExceeded = page.TotalRecordCountLimitExceeded,
            MinActiveRowVersion = null,
            RecordCountLessThanMarv = 0,
        };
        foreach (var row in page.Rows) result.Entities.Add(((SdkRow)row).Entity);
        return result;
    }

    /// <summary>The entity of a row made by <see cref="Rows"/>.</summary>
    internal static Entity ToSdk(QueryRow row) => ((SdkRow)row).Entity;

    private sealed class SdkSink : RowSink
    {
        public override QueryRow New(string logicalName)
        {
            var entity = new Entity { LogicalName = logicalName };
            RowShaped.Value?.Invoke(entity);
            return new SdkRow(entity);
        }
    }

    /// <summary>A row kept as an SDK <see cref="Entity"/>: model values converted on write, read back in the model form.</summary>
    private sealed class SdkRow(Entity entity) : QueryRow
    {
        public Entity Entity { get; } = entity;

        public override string LogicalName => Entity.LogicalName;

        public override Guid Id
        {
            get => Entity.Id;
            set => Entity.Id = value;
        }

        public override string? RowVersion
        {
            set => Entity.RowVersion = value;
        }

        public override object? this[string key]
        {
            set => Entity.Attributes[key] = SdkRecords.ValueToSdk(value);
        }

        public override bool TryGetValue(string key, out object? value)
        {
            if (!Entity.Attributes.TryGetValue(key, out var sdk))
            {
                value = null;
                return false;
            }
            value = SdkRecords.ValueToModel(sdk);
            return true;
        }

        public override bool Remove(string key) => Entity.Attributes.Remove(key);

        public override IEnumerable<string> Keys => Entity.Attributes.Keys;

        public override void SetFormatted(string key, string text) => Entity.FormattedValues[key] = text;

        public override bool RemoveFormatted(string key) => Entity.FormattedValues.Remove(key);

        public override bool HasFormatted(string key) => Entity.FormattedValues.ContainsKey(key);
    }
}

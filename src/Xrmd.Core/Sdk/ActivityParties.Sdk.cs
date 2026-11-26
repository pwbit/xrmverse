using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Sdk;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata.Sdk;
using Xrmd.Model.Metadata;

namespace Xrmd.Core;

/// <summary>
/// S.2e3b: the SDK twin of the read of <see cref="ActivityParties"/>, whose body is on the model, for the readers still on the SDK (the
/// core's images, the mail messages). Transitional: removed with their callers' moves.
/// </summary>
public sealed partial class ActivityParties
{
    /// <summary>
    /// The SDK twin of <see cref="ReadAsync(DataSession, TableDefinition, IReadOnlyCollection{Guid}, IReadOnlyCollection{string}, CancellationToken)"/>:
    /// each list written out once.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, EntityCollection>>> ReadAsync(DataSession s, EntityMetadata activity,
        IReadOnlyCollection<Guid> ids, IReadOnlyCollection<string> attributes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var lists = await ReadAsync(s, activity.ToModel(), ids, attributes, ct);
        return lists.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyDictionary<string, EntityCollection>)kv.Value.ToDictionary(c => c.Key, c => c.Value.ToSdk(), StringComparer.Ordinal));
    }
}

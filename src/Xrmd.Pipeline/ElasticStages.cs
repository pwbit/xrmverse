using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Pipeline;

/// <summary>
/// How the pipeline commits a write on an elastic table (plan 3.4, rulings E3 and E4): an execution that owns the
/// transaction commits after each of stages 20, 30 and 40, each stage in a transaction of its own; a write nested in
/// another message's transaction joins that transaction.
/// </summary>
internal static class ElasticStages
{
    /// <summary>The single-record writes that commit per stage on an elastic table.</summary>
    private static readonly HashSet<string> Writes = new(StringComparer.OrdinalIgnoreCase) { "Create", "Update", "Delete", "Upsert", "Assign" };

    /// <summary>
    /// True when <paramref name="message"/> is Create, Update, Delete, Upsert or Assign on an elastic
    /// <paramref name="entity"/> and the execution <paramref name="owned"/> the transaction (a first-level write).
    /// </summary>
    public static bool CommitPerStage(string message, EntityMetadata? entity, bool owned) =>
        owned && entity is not null && ElasticTables.IsElastic(entity) && Writes.Contains(message);
}

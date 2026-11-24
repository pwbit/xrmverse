using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Metadata.Sdk;

namespace Xrmd.Metadata;

/// <summary>
/// The SDK twins and the handlers of <see cref="SdkMessageCatalog"/> (S.2b2, ruling R4): <see cref="IEntityCreatedHandler"/> and
/// <see cref="IEntityDeletedHandler"/> take the SDK view of the table, which the catalog reads as its model. Transitional: the
/// interfaces take the model with the SDK view gone (S.2i).
/// </summary>
public sealed partial class SdkMessageCatalog
{
    /// <summary>The SDK twin of <see cref="EntityMessagesOf(Xrmd.Model.Metadata.TableDefinition)"/>.</summary>
    public static IReadOnlyList<string> EntityMessagesOf(EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return EntityMessagesOf(SdkTwin.Model(entity));
    }

    /// <summary>The SDK twin of <see cref="IsEventOn(string, Xrmd.Model.Metadata.TableDefinition)"/>.</summary>
    public static bool IsEventOn(string message, EntityMetadata entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return IsEventOn(message, SdkTwin.Model(entity));
    }

    /// <summary>Creates, in the creating session, the filters of the new entity (and the message rows if still missing).</summary>
    public Task OnEntityCreatedAsync(EntityMetadata entity, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return OnTableCreatedAsync(SdkTwin.Model(entity), session, ct);
    }

    /// <summary>
    /// Deletes, in the deleting session, the entity's filters and the steps (with their images) registered on them, after the
    /// custom API check (see <see cref="OnTableDeletingAsync"/>).
    /// </summary>
    /// <exception cref="Xrmd.Core.Errors.XrmdFaultException">400 when a custom API is bound to the table.</exception>
    public Task OnEntityDeletingAsync(EntityMetadata entity, DataSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return OnTableDeletingAsync(SdkTwin.Model(entity), session, ct);
    }
}

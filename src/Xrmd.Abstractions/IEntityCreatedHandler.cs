using Microsoft.Xrm.Sdk.Metadata;

namespace Xrmd.Abstractions;

/// <summary>
/// Notified by the metadata store when an entity is created, inside the session that creates it and before its commit:
/// the handler writes through <c>session</c>, so its rows are committed or rolled back with the entity. An exception
/// thrown by a handler fails the creation (the session's owner rolls it back).
/// </summary>
public interface IEntityCreatedHandler
{
    /// <summary>
    /// Called once the entity's metadata rows and table exist in <paramref name="session"/>'s transaction. The entity is
    /// not yet in the published metadata cache; work that must wait for the commit registers with
    /// <see cref="DataSession.OnCommitted"/>.
    /// </summary>
    Task OnEntityCreatedAsync(EntityMetadata entity, DataSession session, CancellationToken ct);
}

/// <summary>
/// Notified by the metadata store when an entity is deleted, inside the session that deletes it and before its table is
/// dropped: the handler removes, through <c>session</c>, the rows that describe the entity elsewhere (catalogs,
/// registrations) and registers with <see cref="DataSession.OnCommitted"/> what must follow the commit (cache invalidation).
/// </summary>
public interface IEntityDeletedHandler
{
    /// <summary>Called once the entity's <c>meta.*</c> rows are deleted in <paramref name="session"/>'s transaction.</summary>
    Task OnEntityDeletingAsync(EntityMetadata entity, DataSession session, CancellationToken ct);
}

/// <summary>
/// Notified by the metadata store when an attribute is deleted, inside the session that deletes it, once its <c>meta.*</c> rows
/// are deleted and before its columns are dropped: the handler removes, through <c>session</c>, the rows that hang from the
/// attribute elsewhere (e.g. the files of a file column) and registers with <see cref="DataSession.OnCommitted"/> what must
/// follow the commit.
/// </summary>
public interface IAttributeDeletedHandler
{
    /// <summary>Called for the deleted attribute <paramref name="attribute"/> of <paramref name="entity"/>.</summary>
    Task OnAttributeDeletingAsync(EntityMetadata entity, AttributeMetadata attribute, DataSession session, CancellationToken ct);
}

/// <summary>
/// Notified by the metadata store after the commit of a change of a column's <c>IsSecured</c> (an update that turns it on
/// or off, a column created secured, or a secured column deleted): the handler drops what it derived from the old value
/// (cached field permissions).
/// It runs outside the transaction and must not write; an exception it throws does not undo the committed change.
/// </summary>
public interface IAttributeSecurityChangedHandler
{
    /// <summary>Called once for each changed column <paramref name="attribute"/> of <paramref name="entity"/> (logical names).</summary>
    void OnAttributeSecurityChanged(string entity, string attribute);
}

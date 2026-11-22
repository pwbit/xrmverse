using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;

namespace Xrmd.Metadata;

/// <summary>
/// File and image columns of <see cref="MetadataStore"/> (rulings F1 and F5). The first file or image column of a table links
/// it to <c>fileattachment</c>: the table is added to the targets of <c>fileattachment.objectid</c> and the saved one-to-many
/// relationship <c>{table}_FileAttachments</c> is written, in the same session as the CreateAttribute. Dropping the last such
/// column (or the table) undoes both. Only these paths change the targets of <c>objectid</c>; the public messages refuse it.
/// </summary>
public sealed partial class MetadataStore
{
    /// <summary>
    /// The first image column of a table is the primary image. A later one is not, even when it asks for it: Learn ("Update
    /// image columns") says IsPrimaryImage true "is ignored if set to true when creating a new image column" when another image
    /// column exists, and the column is made primary afterwards with an update (UpdateAttribute moves the flag). Sets
    /// <c>IsPrimaryImage</c> of <paramref name="image"/> and records its creation ordinal.
    /// </summary>
    private static void ClaimPrimaryImage(string entityLogicalName, ImageAttributeMetadata image,
        IEnumerable<AttributeMetadata> existing, Dictionary<(string, string), int> imageOrder)
    {
        var others = existing.OfType<ImageAttributeMetadata>().ToList();
        // The creation order of the image columns, persisted with the stored definition (not in the metadata): the oldest remaining one becomes primary when the primary is deleted.
        imageOrder[(entityLogicalName, image.LogicalName)] =
            others.Select(o => OrdinalOf(imageOrder, entityLogicalName, o) ?? 0).DefaultIfEmpty(0).Max() + 1;
        if (others.Count == 0)
        {
            if (image.IsPrimaryImage == false)
                throw new XrmdValidationException(
                    $"Attribute '{image.LogicalName}' is the first image column of '{entityLogicalName}', so it is the primary image: IsPrimaryImage cannot be false.");
            image.IsPrimaryImage = true;
            return;
        }
        image.IsPrimaryImage = false; // true is ignored on create when another image column exists (Learn)
    }

    private static AttributeMetadata Demote(ImageAttributeMetadata image)
    {
        var copy = MetadataSerializer.Clone(image);
        copy.IsPrimaryImage = false;
        return copy;
    }

    /// <inheritdoc />
    public int? GetImageOrdinal(string entityLogicalName, string attributeLogicalName) =>
        _cache.ImageOrder.TryGetValue((entityLogicalName, attributeLogicalName), out var ordinal) ? ordinal : null;

    private static int? OrdinalOf(IReadOnlyDictionary<(string, string), int> imageOrder, string entityLogicalName, AttributeMetadata a) =>
        a is ImageAttributeMetadata && imageOrder.TryGetValue((entityLogicalName, a.LogicalName), out var ordinal) ? ordinal : null;

    private static Task StoreAttributeAsync(DataSession session, string entityLogicalName, AttributeMetadata a, long stamp, CancellationToken ct,
        IReadOnlyDictionary<(string, string), int>? imageOrder = null) =>
        ExecuteAsync(session,
            "UPDATE meta.attribute SET definition = @definition, version_stamp = @stamp WHERE entity_logical_name = @entity AND logical_name = @name", ct,
            ("@definition", StoredAttributeJson(a, imageOrder is null ? null : OrdinalOf(imageOrder, entityLogicalName, a))), ("@stamp", stamp),
            ("@entity", entityLogicalName), ("@name", a.LogicalName));

    /// <summary>The fileattachment table exists and no relationship already has the name the link would give.</summary>
    private static void EnsureAttachmentLinkable(CacheState cache, string entityLogicalName)
    {
        if (!cache.ByLogicalName.ContainsKey(FileColumns.AttachmentEntity))
            throw new XrmdValidationException(
                $"File and image columns need the system table '{FileColumns.AttachmentEntity}', which this organization does not have yet.");
        var name = FileColumns.RelationshipName(entityLogicalName);
        if (cache.ManyToMany.ContainsKey(name) || cache.SavedOneToMany.ContainsKey(name)
            || EntityMetadataExtensions.FindLookupRelationship(cache.ByLogicalName.Values, name, cache.SavedOneToMany.Values) is not null)
            throw new MetadataComponentExistsException(
                $"Relationship '{name}' already exists: it is the name the file and image columns of '{entityLogicalName}' give their relationship to '{FileColumns.AttachmentEntity}'.");
    }

    /// <summary>Adds <paramref name="entityLogicalName"/> to the targets of <c>fileattachment.objectid</c> and saves its relationship.</summary>
    private async Task LinkFileAttachmentsAsync(DataSession session, PendingChanges pending, string entityLogicalName, long stamp, CancellationToken ct)
    {
        var cache = pending.Working;
        await SetAttachmentTargetsAsync(session, cache, targets => targets.Contains(entityLogicalName) ? targets : [.. targets, entityLogicalName], stamp, ct);

        var r = new OneToManyRelationshipMetadata
        {
            SchemaName = FileColumns.RelationshipName(entityLogicalName),
            MetadataId = Guid.NewGuid(),
            ReferencedEntity = entityLogicalName,
            ReferencedAttribute = SystemAttributes.PrimaryIdName(cache.ByLogicalName[entityLogicalName]),
            ReferencingEntity = FileColumns.AttachmentEntity,
            ReferencingAttribute = FileColumns.AttachmentLookup,
            ReferencingEntityNavigationPropertyName = $"{FileColumns.AttachmentLookup}_{entityLogicalName}",
            ReferencedEntityNavigationPropertyName = FileColumns.RelationshipName(entityLogicalName),
            IsCustomRelationship = false,
        };
        await ExecuteAsync(session,
            "INSERT INTO meta.relationship (metadata_id, schema_name, definition, version_stamp) VALUES (@id, @name, @definition, @stamp)", ct,
            ("@id", r.MetadataId.Value), ("@name", r.SchemaName), ("@definition", MetadataSerializer.SerializeRelationship(r)), ("@stamp", stamp));
        cache.SavedOneToMany[r.SchemaName] = r;
    }

    /// <summary>Removes <paramref name="entityLogicalName"/> from the targets of <c>fileattachment.objectid</c> and deletes its relationship.</summary>
    private async Task UnlinkFileAttachmentsAsync(DataSession session, PendingChanges pending, string entityLogicalName, long stamp, CancellationToken ct)
    {
        var cache = pending.Working;
        if (!cache.ByLogicalName.ContainsKey(FileColumns.AttachmentEntity)) return;
        // A permanent target (the note bodies, plan 2.11 A16) stays when its last file or image column goes.
        if (FileColumns.IsPermanentTarget(entityLogicalName) && cache.ByLogicalName.ContainsKey(entityLogicalName)) return;
        await SetAttachmentTargetsAsync(session, cache, targets => targets.Where(t => t != entityLogicalName).ToArray(), stamp, ct);
        await DeleteSavedOneToManyAsync(session, cache,
            r => r.ReferencingEntity == FileColumns.AttachmentEntity && r.ReferencedEntity == entityLogicalName, ct);
    }

    /// <summary>
    /// Links each permanent target of <c>fileattachment.objectid</c> (<see cref="FileColumns.PermanentTargets"/>) that exists and is
    /// not linked yet: its target and its <c>{table}_FileAttachments</c> relationship, as its first file column would. Part of the
    /// activity model's links (<see cref="SyncActivityLinksAsync"/>), so it runs at startup and writes nothing once done.
    /// </summary>
    private async Task EnsurePermanentAttachmentTargetsAsync(DataSession session, PendingChanges pending, CancellationToken ct)
    {
        var cache = pending.Working;
        if (cache.ByLogicalName.GetValueOrDefault(FileColumns.AttachmentEntity) is not { } attachment) return;
        var lookup = (attachment.Attributes ?? []).OfType<LookupAttributeMetadata>().FirstOrDefault(FileColumns.IsAttachmentObjectId);
        if (lookup is null) return;
        foreach (var name in FileColumns.PermanentTargets.Order(StringComparer.Ordinal))
        {
            if (!cache.ByLogicalName.ContainsKey(name)) continue;
            var targeted = (lookup.Targets ?? []).Contains(name, StringComparer.Ordinal);
            var related = cache.SavedOneToMany.ContainsKey(FileColumns.RelationshipName(name));
            if (targeted && related) continue;
            var stamp = pending.Stamp ?? await BumpVersionStampAsync(session, pending, ct);
            if (!related) EnsureAttachmentLinkable(cache, name);
            if (related) await SetAttachmentTargetsAsync(session, cache, targets => targets.Contains(name) ? targets : [.. targets, name], stamp, ct);
            else await LinkFileAttachmentsAsync(session, pending, name, stamp, ct);
        }
    }

    private static async Task SetAttachmentTargetsAsync(DataSession session, CacheState cache, Func<string[], string[]> change, long stamp, CancellationToken ct)
    {
        var attachment = cache.ByLogicalName[FileColumns.AttachmentEntity];
        var current = (LookupAttributeMetadata)(attachment.Attributes ?? []).Single(a => a.LogicalName == FileColumns.AttachmentLookup);
        var targets = change(current.Targets ?? []);
        if (targets.SequenceEqual(current.Targets ?? [])) return;

        var updated = MetadataSerializer.Clone(current);
        updated.Targets = targets;
        await StoreAttributeAsync(session, attachment.LogicalName, updated, stamp, ct);
        var copy = MetadataSerializer.Clone(attachment);
        copy.SetAttributes((attachment.Attributes ?? []).Select(a => a.LogicalName == updated.LogicalName ? updated : a));
        cache.Put(copy, previous: attachment);
    }

    /// <summary>The public messages never write the server-maintained <c>fileattachment.objectid</c> or its relationships.</summary>
    private static XrmdValidationException AttachmentLookupIsManaged(string what) => new(FileColumns.ManagedMessage(what));
}

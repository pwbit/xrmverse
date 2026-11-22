using System.Reflection;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;
using Xrmd.Solution;

namespace Xrmd.Schema.Tests;

/// <summary>
/// An org held in memory: applies a plan's requests the way the metadata store does (system attributes, money base,
/// global option set bindings, intersect tables), after checking each one with the server's own request rules
/// (<see cref="MetadataRequestRules"/>), so a plan this org accepts is one MetadataOperations accepts.
/// </summary>
internal sealed class InMemoryOrg
{
    public List<EntityMetadata> Entities { get; } = [];

    public List<OptionSetMetadataBase> OptionSets { get; } = [];

    /// <summary>The <c>SolutionUniqueName</c> of every request applied.</summary>
    public List<string?> Solutions { get; } = [];

    public EntityMetadata Entity(string name) => Entities.Single(e => e.LogicalName == name);

    public AttributeMetadata Attribute(string entity, string name) => Entity(entity).Attributes.Single(a => a.LogicalName == name);

    public void Apply(IEnumerable<OrganizationRequest> requests)
    {
        foreach (var request in requests) Apply(request);
    }

    public void Apply(OrganizationRequest request)
    {
        Solutions.Add(request.Parameters.TryGetValue("SolutionUniqueName", out var solution) ? solution as string : null);
        switch (request)
        {
            case CreateOptionSetRequest r:
                OptionSets.Should().NotContain(o => o.Name == r.OptionSet.Name);
                var set = MetadataSerializer.Clone((OptionSetMetadata)r.OptionSet);
                set.IsGlobal = true;
                OptionSets.Add(set);
                break;
            case CreateEntityRequest r:
                var entity = MetadataSerializer.Clone(r.Entity);
                var primary = MetadataSerializer.Clone((StringAttributeMetadata)r.PrimaryAttribute);
                MetadataRequestRules.DemandSupportedEntity(entity, isNew: true);
                MetadataRequestRules.DemandOnlyModelledSettings(entity, primary.LogicalName);
                MetadataRequestRules.DemandOnlyModelledAttributeSettings(primary, entity.LogicalName);
                entity.SchemaName.Should().NotBeNull();
                primary.SchemaName.Should().NotBeNull();
                Entities.Should().NotContain(e => e.LogicalName == entity.LogicalName);
                // A custom activity (plan 2.11 A4): primary id activityid, primary column Subject, always with notes.
                if (entity.IsActivity == true) primary.SchemaName.Should().Be("Subject");
                Set(entity, nameof(EntityMetadata.PrimaryIdAttribute), entity.IsActivity == true ? "activityid" : entity.LogicalName + "id");
                if (r.HasNotes || entity.IsActivity == true) entity.HasNotes = true;
                if (r.HasActivities) entity.HasActivities = true;
                Set(entity, nameof(EntityMetadata.PrimaryNameAttribute), primary.LogicalName);
                primary.MaxLength ??= TypeMapping.DefaultStringLength;
                entity.SetAttributes(SystemAttributes.Complete(entity, [primary]));
                Set(entity, nameof(EntityMetadata.ManyToManyRelationships), Array.Empty<ManyToManyRelationshipMetadata>());
                Entities.Add(entity);
                break;
            case CreateAttributeRequest r:
                var attribute = MetadataSerializer.Clone(r.Attribute);
                MetadataRequestRules.DemandOnlyModelledAttributeSettings(attribute, r.EntityName);
                attribute.SchemaName.Should().NotBeNull();
                if (attribute is StringAttributeMetadata s) s.MaxLength ??= TypeMapping.DefaultStringLength;
                AddAttribute(r.EntityName, attribute);
                break;
            case CreateOneToManyRequest r:
                var lookup = (LookupAttributeMetadata)MetadataSerializer.Clone(r.Lookup);
                MetadataRequestRules.DemandOnlyModelledAttributeSettings(lookup, r.OneToManyRelationship.ReferencingEntity);
                r.OneToManyRelationship.SchemaName.Should().NotBeNullOrEmpty();
                Entities.Should().Contain(e => e.LogicalName == r.OneToManyRelationship.ReferencedEntity);
                lookup.Targets = [r.OneToManyRelationship.ReferencedEntity];
                AddAttribute(r.OneToManyRelationship.ReferencingEntity, lookup);
                break;
            case CreateCustomerRelationshipsRequest r:
                var customer = (LookupAttributeMetadata)MetadataSerializer.Clone(r.Lookup);
                var referencing = r.OneToManyRelationships.Select(x => x.ReferencingEntity).Distinct().Should().ContainSingle().Subject;
                MetadataRequestRules.DemandOnlyModelledAttributeSettings(customer, referencing);
                r.OneToManyRelationships.Select(x => x.ReferencedEntity).Should().BeEquivalentTo(["account", "contact"]);
                r.OneToManyRelationships.Should().OnlyContain(x => !string.IsNullOrEmpty(x.SchemaName));
                customer.Targets = ["account", "contact"];
                Set(customer, nameof(AttributeMetadata.AttributeType), AttributeTypeCode.Customer);
                AddAttribute(referencing, customer);
                break;
            case CreateManyToManyRequest r:
                var relationship = MetadataSerializer.Clone(r.ManyToManyRelationship);
                relationship.SchemaName.Should().NotBeNullOrEmpty();
                relationship.IntersectEntityName ??= r.IntersectEntitySchemaName.ToLowerInvariant();
                var intersect = new EntityMetadata { LogicalName = relationship.IntersectEntityName, SchemaName = r.IntersectEntitySchemaName };
                Set(intersect, nameof(EntityMetadata.IsIntersect), true);
                intersect.SetAttributes([]);
                Entities.Add(intersect);
                foreach (var side in new[] { relationship.Entity1LogicalName, relationship.Entity2LogicalName }.Distinct())
                {
                    var e = Entity(side);
                    Set(e, nameof(EntityMetadata.ManyToManyRelationships), (e.ManyToManyRelationships ?? []).Append(relationship).ToArray());
                }
                break;
            case UpdateEntityRequest r:
                var current = Entity(r.Entity.LogicalName);
                MetadataRequestRules.UnappliedEntityChanges(current, r.Entity).Should().BeEmpty();
                if (r.Entity.DisplayName is { } d) current.DisplayName = d;
                if (r.Entity.DisplayCollectionName is { } c) current.DisplayCollectionName = c;
                if (r.Entity.Description is { } desc) current.Description = desc;
                // HasNotes and HasActivities are one-way (plan 2.11 A5).
                r.Entity.HasNotes.Should().NotBe(false);
                r.Entity.HasActivities.Should().NotBe(false);
                if (r.Entity.HasNotes == true) current.HasNotes = true;
                if (r.Entity.HasActivities == true) current.HasActivities = true;
                break;
            case UpdateAttributeRequest r:
                var existing = Attribute(r.EntityName, r.Attribute.LogicalName);
                r.Attribute.GetType().Should().Be(existing.GetType());
                MetadataRequestRules.UnappliedAttributeChanges(existing, r.Attribute, labelsOnly: false).Should().BeEmpty();
                if (r.Attribute.DisplayName is { } label) existing.DisplayName = label;
                if (r.Attribute.Description is { } description) existing.Description = description;
                // A calculated column's definition (plan 2.12a): the store re-validates the formula; the SourceType cannot change.
                if (r.Attribute.SourceType is { } sourceType) sourceType.Should().Be(existing.SourceType ?? 0);
                if (Xrmd.Metadata.Computed.ComputedColumnRules.FormulaOf(r.Attribute) is { } formula)
                    Xrmd.Metadata.Computed.ComputedColumnRules.SetFormula(existing, formula);
                switch (r.Attribute)
                {
                    case StringAttributeMetadata text:
                        if (text.MaxLength is { } textLength) ((StringAttributeMetadata)existing).MaxLength = textLength;
                        if (text.AutoNumberFormat is { } format) existing.AutoNumberFormat = format;
                        break;
                    case MemoAttributeMetadata { MaxLength: { } length }: ((MemoAttributeMetadata)existing).MaxLength = length; break;
                    case FileAttributeMetadata { MaxSizeInKB: { } fileSize }: ((FileAttributeMetadata)existing).MaxSizeInKB = fileSize; break;
                    case ImageAttributeMetadata image:
                        var stored = (ImageAttributeMetadata)existing;
                        if (image.MaxSizeInKB is { } imageSize) stored.MaxSizeInKB = imageSize;
                        if (image.CanStoreFullImage is { } full) stored.CanStoreFullImage = full;
                        image.IsPrimaryImage.Should().NotBe(false, "the primary image flag is never set to false");
                        if (image.IsPrimaryImage == true) ClaimPrimaryImage(r.EntityName, stored);
                        break;
                }
                if (r.Attribute is EnumAttributeMetadata { OptionSet.Options: { Count: > 0 } relabelled })
                {
                    var options = ((EnumAttributeMetadata)existing).OptionSet;
                    options.IsGlobal.Should().NotBe(true);
                    foreach (var option in relabelled) options.Options.Single(o => o.Value == option.Value).Label = option.Label;
                }
                break;
            case InsertOptionValueRequest r:
                var target = r.OptionSetName is { Length: > 0 } name
                    ? (OptionSetMetadata)OptionSets.Single(o => o.Name == name)
                    : ((EnumAttributeMetadata)Attribute(r.EntityLogicalName, r.AttributeLogicalName)).OptionSet;
                target.Options.Should().NotContain(o => o.Value == r.Value);
                target.Options.Add(new OptionMetadata(r.Label, r.Value));
                RefreshBindings();
                break;
            case UpdateOptionSetRequest r:
                var global = (OptionSetMetadata)OptionSets.Single(o => o.Name == r.OptionSet.Name);
                MetadataRequestRules.UnappliedOptionSetChanges(global, (OptionSetMetadata)r.OptionSet).Should().BeEmpty();
                if (r.OptionSet.DisplayName is { } setLabel) global.DisplayName = setLabel;
                if (r.OptionSet.Description is { } setDescription) global.Description = setDescription;
                foreach (var option in ((OptionSetMetadata)r.OptionSet).Options)
                    global.Options.Single(o => o.Value == option.Value).Label = option.Label;
                RefreshBindings();
                break;
            default:
                throw new InvalidOperationException($"Unexpected request {request.GetType().Name}.");
        }
    }

    private void AddAttribute(string entityName, AttributeMetadata attribute)
    {
        var entity = Entity(entityName);
        entity.Attributes.Should().NotContain(a => a.LogicalName == attribute.LogicalName);
        if (attribute is EnumAttributeMetadata { OptionSet: { IsGlobal: true, Name: { } bound } } enumAttribute)
            enumAttribute.OptionSet = MetadataSerializer.Clone((OptionSetMetadata)OptionSets.Single(o => o.Name == bound));
        List<AttributeMetadata> added = [attribute];
        if (attribute is MoneyAttributeMetadata money)
        {
            added.Add(SystemAttributes.MoneyBase(money));
            // The first money column brings the currency pair (plan 2.10, X4).
            if (!CurrencySchema.HasCurrencyColumns(entity)) added.AddRange([SystemAttributes.CurrencyLookup(), SystemAttributes.ExchangeRate()]);
        }
        // File and image columns as the metadata store makes them: the defaults, their companions, the primary image (F1, F5).
        switch (attribute)
        {
            case FileAttributeMetadata file:
                file.MaxSizeInKB ??= FileColumns.DefaultFileMaxSizeInKB;
                break;
            case ImageAttributeMetadata image:
                image.MaxSizeInKB ??= FileColumns.DefaultImageMaxSizeInKB;
                image.CanStoreFullImage ??= false;
                image.IsPrimaryImage.Should().NotBe(false, "the primary image flag is never set to false");
                // The first image column is primary; true on a later one is ignored on create (only an update moves it).
                image.IsPrimaryImage = !entity.Attributes.OfType<ImageAttributeMetadata>().Any();
                break;
        }
        if (FileColumns.IsFileOrImage(attribute))
        {
            attribute.MetadataId ??= Guid.NewGuid();
            added.AddRange(FileColumns.Companions(attribute));
        }
        entity.SetAttributes(entity.Attributes.Concat(added));
    }

    private void ClaimPrimaryImage(string entityName, ImageAttributeMetadata image)
    {
        foreach (var other in Entity(entityName).Attributes.OfType<ImageAttributeMetadata>()) other.IsPrimaryImage = false;
        image.IsPrimaryImage = true;
    }

    /// <summary>Attributes bound to a global option set hold a copy of it (as the store's cache does).</summary>
    private void RefreshBindings()
    {
        foreach (var attribute in Entities.SelectMany(e => e.Attributes).OfType<EnumAttributeMetadata>())
            if (attribute.OptionSet is { IsGlobal: true, Name: { } bound } && OptionSets.SingleOrDefault(o => o.Name == bound) is OptionSetMetadata set)
                attribute.OptionSet = MetadataSerializer.Clone(set);
    }

    public static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.GetSetMethod(nonPublic: true)!.Invoke(target, [value]);
}

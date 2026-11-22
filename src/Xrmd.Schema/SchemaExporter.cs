using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;

namespace Xrmd.Schema;

/// <summary>
/// The <see cref="SchemaDocument"/> of an org's custom metadata: the global choices, the tables (not intersect tables), their
/// columns and the many-to-many relationships they are the first side of, all named with the publisher prefix, sorted by
/// name. Applying the exported document to the same org plans nothing (<see cref="SchemaDiff"/>).
/// </summary>
/// <remarks>
/// What the schema YAML cannot describe is not left out silently: a prefixed column of another type (double, bigint,
/// a polymorphic lookup other than Customer, ...), a prefixed global option set that is not a choice or a table with another ownership is a
/// <see cref="NotSupportedException"/> naming it. A table without a label is exported with its logical name as label.
/// </remarks>
public static class SchemaExporter
{
    /// <param name="publisherPrefix">The prefix, with or without its trailing <c>_</c>.</param>
    /// <exception cref="NotSupportedException">A prefixed component the schema YAML cannot describe.</exception>
    public static SchemaDocument Export(IReadOnlyList<EntityMetadata> entities, IReadOnlyList<OptionSetMetadataBase> optionSets,
        string publisherPrefix)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(optionSets);
        ArgumentException.ThrowIfNullOrEmpty(publisherPrefix);
        var publisher = publisherPrefix.TrimEnd('_');
        var prefix = publisher + "_";
        var document = new SchemaDocument { Publisher = publisher };

        foreach (var set in optionSets.Where(o => o.Name?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                     .OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            if (set is not OptionSetMetadata choice)
                throw new NotSupportedException($"Global option set '{set.Name}' is a {set.GetType().Name}, which the schema YAML cannot describe.");
            document.Choices.Add(new SchemaChoice
            {
                Name = choice.Name, Label = SchemaColumnTypes.Text(choice.DisplayName), Description = SchemaColumnTypes.Text(choice.Description),
                Options = SchemaColumnTypes.Options(choice),
            });
        }

        foreach (var entity in entities.Where(e => e.LogicalName?.StartsWith(prefix, StringComparison.Ordinal) == true && e.IsIntersect != true)
                     .OrderBy(e => e.LogicalName, StringComparer.Ordinal))
            document.Tables.Add(Table(entity, prefix));
        return document;
    }

    /// <summary>The SchemaName the YAML implies for the primary column <paramref name="logicalName"/> (<c>Subject</c> for an activity's).</summary>
    private static string DefaultPrimarySchemaName(SchemaTable table, string logicalName) =>
        new SchemaTable { IsActivity = table.IsActivity, PrimaryName = new SchemaPrimaryName { Name = logicalName } }.PrimarySchemaName;

    private static SchemaTable Table(EntityMetadata entity, string prefix)
    {
        var name = entity.LogicalName;
        var table = new SchemaTable
        {
            Name = name,
            SchemaName = entity.SchemaName is { } schemaName && schemaName != name ? schemaName : null,
            Label = SchemaColumnTypes.Text(entity.DisplayName) ?? name,
            PluralLabel = SchemaColumnTypes.Text(entity.DisplayCollectionName),
            Description = SchemaColumnTypes.Text(entity.Description),
            TableType = ElasticTables.IsElastic(entity) ? ElasticTables.TableTypeElastic : ElasticTables.TableTypeStandard,
            Ownership = entity.OwnershipType switch
            {
                null or OwnershipTypes.UserOwned => SchemaOwnership.User,
                OwnershipTypes.OrganizationOwned => SchemaOwnership.Organization,
                var other => throw new NotSupportedException($"Table '{name}' has the ownership {other}, which the schema YAML cannot describe."),
            },
            // Plan 2.11: the flags are written when true (false is a new table's default, and they are one-way).
            IsActivity = entity.IsActivity == true,
            HasNotes = entity.HasNotes == true ? true : null,
            HasActivities = entity.HasActivities == true ? true : null,
            // Plan 4.1a, one-way too: written when true, and false for an activity, which is queue-enabled when the key is absent.
            IsValidForQueue = entity.IsValidForQueue?.Value == true ? true : entity.IsActivity == true ? false : null,
        };

        var primary = (entity.Attributes ?? []).FirstOrDefault(a => a.LogicalName == entity.PrimaryNameAttribute) as StringAttributeMetadata
                      ?? throw new NotSupportedException($"Table '{name}' has no string primary name column, which the schema YAML requires.");
        table.PrimaryName = new SchemaPrimaryName
        {
            Name = primary.LogicalName,
            SchemaName = primary.SchemaName is { } primarySchemaName && primarySchemaName != DefaultPrimarySchemaName(table, primary.LogicalName) ? primarySchemaName : null,
            Label = SchemaColumnTypes.Text(primary.DisplayName),
            Description = SchemaColumnTypes.Text(primary.Description),
            MaxLength = primary.MaxLength,
            AutoNumberFormat = string.IsNullOrEmpty(primary.AutoNumberFormat) ? null : primary.AutoNumberFormat,
        };

        foreach (var attribute in SchemaDiff.CustomAttributes(entity, prefix))
        {
            table.Columns.Add(SchemaColumnTypes.ToColumn(attribute)
                              ?? throw new NotSupportedException(
                                  $"Column '{name}.{attribute.LogicalName}' is a {attribute.AttributeTypeName?.Value ?? attribute.GetType().Name} column, which the schema YAML cannot describe."));
        }

        foreach (var relationship in (entity.ManyToManyRelationships ?? [])
                     .Where(r => r.Entity1LogicalName == name && r.SchemaName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                     .OrderBy(r => r.SchemaName, StringComparer.Ordinal))
        {
            // The YAML names are logical names (lowercase); the diff matches relationship names ignoring case.
            table.ManyToMany.Add(new SchemaManyToMany { Name = relationship.SchemaName.ToLowerInvariant(), With = relationship.Entity2LogicalName });
        }
        return table;
    }
}

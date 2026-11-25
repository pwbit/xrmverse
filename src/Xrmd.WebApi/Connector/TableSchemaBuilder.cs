using System.Text.Json.Nodes;
using Xrmd.Abstractions;
using Xrmd.Metadata;
using Xrmd.Model.Metadata;
using Xrmd.Security;

namespace Xrmd.WebApi.Connector;

/// <summary>What a table's JSON schema describes: the record a read returns, or the body of a create or an update.</summary>
public enum SchemaUse { Read, Create, Update }

/// <summary>
/// X.12a: the JSON Schema of a table for the custom connector's designer (the fields the flow designer offers). Read lists the
/// readable columns (a lookup as <c>_name_value</c>), Create and Update the writable ones (a lookup as one
/// <c>navigation@odata.bind</c> per target). Field security drops the columns the caller may not read, create or update.
/// </summary>
public static class TableSchemaBuilder
{
    // Columns the server fills on create: never required of the caller.
    private static readonly HashSet<string> ServerFilled = new(["ownerid", "statecode", "statuscode"], StringComparer.OrdinalIgnoreCase);

    public static JsonObject Build(TableDefinition table, SchemaUse use, IReadOnlyList<OneToManyRelationship> relationships,
        IMetadataStore store, FieldPermissionSet fields, int languageCode)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(relationships);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fields);

        var properties = new JsonObject();
        var required = new JsonArray();
        if (use == SchemaUse.Read) properties["@odata.etag"] = new JsonObject { ["type"] = "string" };

        foreach (var column in table.Attributes ?? [])
        {
            if (!IsCandidate(table.LogicalName, column, use, fields) || column.LogicalName is not { } name) continue;
            var summary = FormattedValues.LabelText(column.DisplayName, languageCode) ?? name;
            var description = FormattedValues.LabelText(column.Description, languageCode);
            string? firstProperty = null;
            var bindCount = 0;

            if (column is LookupColumn lookup)
            {
                if (lookup.AttributeType == ColumnTypeCode.PartyList) continue;
                if (use == SchemaUse.Read)
                {
                    firstProperty = EntityValueName(name);
                    properties[firstProperty] = Property(summary, description, "string", "uuid");
                    continue;
                }
                foreach (var (navigation, target) in Navigations(table, lookup, relationships))
                {
                    var bind = navigation + "@odata.bind";
                    firstProperty ??= bind;
                    bindCount++;
                    var targetTable = target is null ? null : store.GetTable(target);
                    var targetLabel = target is null ? null : FormattedValues.TableName(target, targetTable, languageCode);
                    var prop = Property(targetLabel is null ? summary : $"{summary} ({targetLabel})", null, "string", null);
                    prop["description"] = $"/{targetTable?.EntitySetName ?? target}(<id>)";
                    properties[bind] = prop;
                }
            }
            else
            {
                var prop = ScalarProperty(column, use, summary, description, languageCode);
                if (prop is null) continue;
                properties[name] = prop;
                firstProperty = name;
            }

            // A lookup with several bind properties (Customer, multi-target) is never listed: JSON Schema cannot say "one of",
            // the server enforces the requirement.
            if (use == SchemaUse.Create && firstProperty is not null && bindCount <= 1 && IsRequired(table, column))
                required.Add(firstProperty);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (use == SchemaUse.Create) schema["required"] = required;
        return schema;
    }

    private static string EntityValueName(string lookupLogicalName) => $"_{lookupLogicalName}_value";

    private static bool IsCandidate(string? tableName, ColumnDefinition column, SchemaUse use, FieldPermissionSet fields)
    {
        if (column.AttributeOf is not null || column.LogicalName is not { } name) return false;
        if (WebApiHiddenAttributes.IsHidden(tableName, name)) return false;
        return use switch
        {
            SchemaUse.Read => column.IsValidForRead != false && fields.CanRead(name),
            SchemaUse.Create => column.IsValidForCreate != false && fields.CanCreate(name),
            _ => column.IsValidForUpdate != false && fields.CanUpdate(name),
        };
    }

    private static bool IsRequired(TableDefinition table, ColumnDefinition column) =>
        column.RequiredLevel?.Value is RequirementLevel.SystemRequired or RequirementLevel.ApplicationRequired
        && column.IsPrimaryId != true && column.LogicalName != table.PrimaryIdAttribute && !ServerFilled.Contains(column.LogicalName!);

    /// <summary>The bind navigation properties of a lookup, as the Web API names them (one per target for a Customer column).</summary>
    private static IEnumerable<(string Navigation, string? Target)> Navigations(TableDefinition table, LookupColumn lookup,
        IReadOnlyList<OneToManyRelationship> relationships)
    {
        var targets = lookup.Targets ?? [];
        if (CustomerColumns.IsCustomer(lookup) || ActivitySchema.IsManagedLookup(table.LogicalName, lookup.LogicalName))
            return EntityMetadataExtensions.CustomerNavigations(table, lookup, relationships).Select(n => (n.Name, (string?)n.Target));
        if (targets.Count > 1)
        {
            var perTarget = EntityMetadataExtensions.CustomerNavigations(table, lookup, relationships).ToList();
            var own = relationships.Where(r => r.ReferencingEntity == table.LogicalName && r.ReferencingAttribute == lookup.LogicalName)
                .Select(r => r.ReferencingEntityNavigationPropertyName).ToHashSet(StringComparer.Ordinal);
            if (perTarget.All(n => own.Contains(n.Name)) && perTarget.Select(n => n.Name).Distinct(StringComparer.Ordinal).Count() == perTarget.Count)
                return perTarget.Select(n => (n.Name, (string?)n.Target));
        }
        return [(EntityMetadataExtensions.ReferencingNavigationName(table, lookup, relationships), targets.FirstOrDefault())];
    }

    private static JsonObject Property(string summary, string? description, string type, string? format)
    {
        var p = new JsonObject { ["type"] = type };
        if (format is not null) p["format"] = format;
        p["x-ms-summary"] = summary;
        if (description is not null) p["description"] = description;
        return p;
    }

    private static JsonObject? ScalarProperty(ColumnDefinition column, SchemaUse use, string summary, string? description, int language)
    {
        var write = use != SchemaUse.Read;
        switch (column)
        {
            case TextColumn or MemoColumn: return Property(summary, description, "string", null);
            case IntegerColumn: return Property(summary, description, "integer", "int32");
            case BigIntColumn: return Property(summary, description, "integer", "int64");
            case DecimalColumn or DoubleColumn or MoneyColumn: return Property(summary, description, "number", null);
            case BooleanColumn: return Property(summary, description, "boolean", null);
            case DateTimeColumn dt:
                return Property(summary, description, "string", dt.DateTimeBehavior?.Value == "DateOnly" ? "date" : "date-time");
            case UniqueIdentifierColumn: return write ? null : Property(summary, description, "string", "uuid");
            case MultiChoiceColumn multi:
            {
                var labels = string.Join(", ", Options(multi).Select(o => $"{o.Value}={Label(o, language)}"));
                var text = description is null ? labels : $"{description} ({labels})";
                return Property(summary, labels.Length == 0 ? description : text, "string", null);
            }
            case StateColumn or StatusColumn:
                return use == SchemaUse.Create ? null : Choice((ChoiceColumnBase)column, summary, description, language);
            case ChoiceColumn choice: return Choice(choice, summary, description, language);
            default: return null; // File, Image, EntityName, ManagedProperty, Virtual, ...
        }
    }

    private static IEnumerable<ChoiceOption> Options(ChoiceColumnBase column) =>
        (column.OptionSet?.Options ?? []).Where(o => o.Value is not null);

    private static string Label(ChoiceOption option, int language) =>
        FormattedValues.LabelText(option.Label, language) ?? option.Value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject Choice(ChoiceColumnBase column, string summary, string? description, int language)
    {
        var p = Property(summary, description, "integer", null);
        var values = new JsonArray();
        var named = new JsonArray();
        foreach (var option in Options(column))
        {
            values.Add(option.Value!.Value);
            named.Add(new JsonObject { ["value"] = option.Value.Value, ["displayName"] = Label(option, language) });
        }
        if (values.Count == 0) return p;
        p["enum"] = values;
        p["x-ms-enum-values"] = named;
        return p;
    }
}

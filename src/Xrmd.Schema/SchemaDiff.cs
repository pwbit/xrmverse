using System.Globalization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Metadata;
using Xrmd.Metadata.Computed;

namespace Xrmd.Schema;

/// <summary>
/// The additive plan that brings an org to a <see cref="SchemaDocument"/>: it creates what is missing (global choices,
/// tables with their primary name, columns, lookups, many-to-many relationships, options) and updates only labels,
/// descriptions, a larger <c>MaxLength</c>, a new <c>AutoNumberFormat</c>, option labels and <c>hasNotes</c>/<c>hasActivities</c>/<c>isValidForQueue</c>
/// switched on (one-way: switching one off, or changing <c>isActivity</c>, is a rejection). It never deletes: the org's components with the publisher
/// prefix that the document does not mention are <see cref="SchemaPlan.Extras"/>. Every other difference (a type change,
/// a smaller <c>MaxLength</c>, another lookup target, another precision, ownership or primary name, a choice bound
/// differently, a reference to a table or choice that exists nowhere) is a rejection naming the component.
/// </summary>
/// <remarks>
/// The requests are the SDK messages the server's metadata operations take: <see cref="CreateOptionSetRequest"/>,
/// <see cref="CreateEntityRequest"/> (with its primary name attribute), <see cref="CreateAttributeRequest"/>,
/// <see cref="CreateCustomerRelationshipsRequest"/> for a customer column (relationships <c>{table}_{column}_account</c> and <c>_contact</c>),
/// <see cref="CreateOneToManyRequest"/> for a lookup (relationship <c>{table}_{column}_{target}</c>, the name the server
/// derives for a lookup), <see cref="CreateManyToManyRequest"/> (intersect table named like the relationship),
/// <see cref="UpdateOptionSetRequest"/>, <see cref="UpdateEntityRequest"/>, <see cref="UpdateAttributeRequest"/> and
/// <see cref="InsertOptionValueRequest"/>, each with <c>SolutionUniqueName</c> when the document names a solution. Labels
/// are written and compared in <see cref="SchemaColumnTypes.LanguageCode"/>.
/// </remarks>
public static class SchemaDiff
{
    /// <summary>Server default of a string's MaxLength (and of a decimal's precision) when created without one.</summary>
    private const int DefaultStringLength = 100, DefaultPrecision = 2;

    /// <summary>Longest relationship SchemaName the server takes.</summary>
    private const int MaxRelationshipName = 100;

    public static SchemaPlan Compute(SchemaDocument desired, IReadOnlyList<EntityMetadata> entities,
        IReadOnlyList<OptionSetMetadataBase> globalOptionSets)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(globalOptionSets);
        return new Differ(desired, entities, globalOptionSets).Run();
    }

    private sealed class Differ(SchemaDocument desired, IReadOnlyList<EntityMetadata> entities, IReadOnlyList<OptionSetMetadataBase> optionSets)
    {
        private readonly string _prefix = desired.Publisher + "_";
        private readonly List<OrganizationRequest> _choices = [], _tables = [], _columns = [], _lookups = [], _manyToMany = [], _computed = [];
        private readonly List<string> _extras = [], _rejections = [];

        public SchemaPlan Run()
        {
            foreach (var choice in desired.Choices) Choice(choice);
            ExtraChoices();
            foreach (var table in desired.Tables) Table(table);
            ExtraTables();
            return new SchemaPlan
            {
                // The calculated (plan 2.12a) and rollup (plan 2.12b) columns last, in the document's order: their definitions may read
                // any column, lookup or relationship the plan creates, and one another in the order the document gives them.
                Changes = [.. _choices, .. _tables, .. _columns, .. _lookups, .. _manyToMany, .. _computed],
                Extras = _extras,
                Rejections = _rejections,
            };
        }

        // ---- choices ------------------------------------------------------------------------------------------------

        private void Choice(SchemaChoice choice)
        {
            var existing = OptionSet(choice.Name);
            if (existing is null)
            {
                var created = new OptionSetMetadata(new OptionMetadataCollection(choice.Options.Select(NewOption).ToList()))
                {
                    Name = choice.Name, IsGlobal = true, OptionSetType = OptionSetType.Picklist,
                };
                if (choice.Label is { } label) created.DisplayName = SchemaColumnTypes.Label(label);
                if (choice.Description is { } description) created.Description = SchemaColumnTypes.Label(description);
                _choices.Add(Solution(new CreateOptionSetRequest { OptionSet = created }));
                return;
            }
            if (existing is not OptionSetMetadata current)
            {
                _rejections.Add($"Choice '{choice.Name}' is a {existing.GetType().Name} in the org, not a choice.");
                return;
            }

            var update = new OptionSetMetadata { Name = current.Name };
            var changed = false;
            if (Differs(choice.Label, current.DisplayName))
            {
                update.DisplayName = SchemaColumnTypes.Label(choice.Label!);
                changed = true;
            }
            if (Differs(choice.Description, current.Description))
            {
                update.Description = SchemaColumnTypes.Label(choice.Description!);
                changed = true;
            }
            var (added, relabelled) = CompareOptions(choice.Options, current, $"option {choice.Name}");
            foreach (var option in relabelled) update.Options.Add(option);
            if (changed || relabelled.Count > 0) _choices.Add(Solution(new UpdateOptionSetRequest { OptionSet = update }));
            foreach (var option in added)
                _choices.Add(Solution(new InsertOptionValueRequest { OptionSetName = current.Name, Value = option.Value, Label = SchemaColumnTypes.Label(option.Label) }));
        }

        private void ExtraChoices()
        {
            var named = desired.Choices.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var set in optionSets.Where(o => HasPrefix(o.Name) && !named.Contains(o.Name)).OrderBy(o => o.Name, StringComparer.Ordinal))
                _extras.Add($"choice {set.Name}");
        }

        // ---- tables -------------------------------------------------------------------------------------------------

        private void Table(SchemaTable table)
        {
            var entity = Entity(table.Name);
            if (entity is null)
            {
                var created = new EntityMetadata
                {
                    LogicalName = table.Name,
                    SchemaName = table.SchemaName ?? table.Name,
                    DisplayName = SchemaColumnTypes.Label(table.Label),
                    OwnershipType = Ownership(table.Ownership),
                };
                if (ElasticTables.NormalizeTableType(table.TableType) == ElasticTables.TableTypeElastic) created.TableType = ElasticTables.TableTypeElastic;
                if (table.PluralLabel is { } plural) created.DisplayCollectionName = SchemaColumnTypes.Label(plural);
                if (table.Description is { } description) created.Description = SchemaColumnTypes.Label(description);
                // Plan 2.11: a custom activity, and the two one-way flags (on the entity and as the request's parameters).
                if (table.IsActivity) created.IsActivity = true;
                if (table.HasNotes == true) created.HasNotes = true;
                if (table.HasActivities == true) created.HasActivities = true;
                // Plan 4.1a: stated only when given (an activity is queue-enabled when absent).
                if (table.IsValidForQueue is { } queues) created.IsValidForQueue = new BooleanManagedProperty(queues);
                _tables.Add(Solution(new CreateEntityRequest
                {
                    Entity = created, PrimaryAttribute = SchemaColumnTypes.ToPrimaryAttribute(table),
                    HasNotes = table.HasNotes == true, HasActivities = table.HasActivities == true,
                }));
                foreach (var column in table.Columns) NewColumn(table, column);
            }
            else
            {
                ExistingTable(table, entity);
                foreach (var column in table.Columns)
                {
                    var attribute = entity.Attributes?.FirstOrDefault(a => a.LogicalName == column.Name);
                    if (attribute is null) NewColumn(table, column);
                    else ExistingColumn(table.Name, column, attribute, column.Type is SchemaColumnTypes.Lookup or SchemaColumnTypes.Customer ? _lookups : _columns);
                }
                ExtraColumns(table, entity);
            }
            foreach (var relationship in table.ManyToMany) ManyToMany(table, relationship);
        }

        private void ExistingTable(SchemaTable table, EntityMetadata entity)
        {
            if (entity.IsIntersect == true)
            {
                _rejections.Add($"Table '{table.Name}' is the intersect table of a many-to-many relationship in the org.");
                return;
            }
            var wanted = ElasticTables.NormalizeTableType(table.TableType);
            var actual = ElasticTables.IsElastic(entity) ? ElasticTables.TableTypeElastic : ElasticTables.TableTypeStandard;
            if (wanted != actual) _rejections.Add($"Table '{table.Name}': the table type cannot change from {actual} to {wanted}.");
            var ownership = Ownership(table.Ownership);
            if ((entity.OwnershipType ?? OwnershipTypes.UserOwned) != ownership)
                _rejections.Add($"Table '{table.Name}': the ownership cannot change from {Describe(entity.OwnershipType)} to {Describe(ownership)}.");
            if (table.SchemaName is { } schemaName && schemaName != (entity.SchemaName ?? entity.LogicalName))
                _rejections.Add($"Table '{table.Name}': the SchemaName cannot change from '{entity.SchemaName}' to '{schemaName}'.");

            var update = new EntityMetadata { LogicalName = entity.LogicalName };
            var changed = false;
            if (Differs(table.Label, entity.DisplayName))
            {
                update.DisplayName = SchemaColumnTypes.Label(table.Label);
                changed = true;
            }
            if (Differs(table.PluralLabel, entity.DisplayCollectionName))
            {
                update.DisplayCollectionName = SchemaColumnTypes.Label(table.PluralLabel!);
                changed = true;
            }
            if (Differs(table.Description, entity.Description))
            {
                update.Description = SchemaColumnTypes.Label(table.Description!);
                changed = true;
            }
            // Plan 2.11: IsActivity is set at creation only (A4); HasNotes and HasActivities switch from false to true only (A5).
            if (table.IsActivity != (entity.IsActivity == true))
                _rejections.Add($"Table '{table.Name}': isActivity cannot change from {Flag(entity.IsActivity == true)} to {Flag(table.IsActivity)} (it is set when the table is created).");
            if (Switch(table, "hasNotes", table.HasNotes, entity.HasNotes == true))
            {
                update.HasNotes = true;
                changed = true;
            }
            if (Switch(table, "hasActivities", table.HasActivities, entity.HasActivities == true))
            {
                update.HasActivities = true;
                changed = true;
            }
            // Plan 4.1a: IsValidForQueue is one-way as well.
            if (Switch(table, "isValidForQueue", table.IsValidForQueue, entity.IsValidForQueue?.Value == true))
            {
                update.IsValidForQueue = new BooleanManagedProperty(true);
                changed = true;
            }
            if (changed) _tables.Add(Solution(new UpdateEntityRequest { Entity = update }));

            var primaryName = table.PrimaryName;
            var primary = entity.Attributes?.FirstOrDefault(a => a.LogicalName == entity.PrimaryNameAttribute);
            if (entity.PrimaryNameAttribute != primaryName.Name || primary is null)
            {
                _rejections.Add($"Table '{table.Name}': the primary name column is '{entity.PrimaryNameAttribute}', it cannot change to '{primaryName.Name}'.");
                return;
            }
            ExistingColumn(table.Name, new SchemaColumn
            {
                Name = primaryName.Name, SchemaName = primaryName.SchemaName, Type = SchemaColumnTypes.String, Label = primaryName.Label,
                Description = primaryName.Description, MaxLength = primaryName.MaxLength, AutoNumberFormat = primaryName.AutoNumberFormat,
            }, primary, _tables);
        }

        /// <summary>
        /// True when the one-way flag <paramref name="key"/> must be switched on; a desired false on a table that has it is a
        /// rejection; null (not stated) changes nothing.
        /// </summary>
        private bool Switch(SchemaTable table, string key, bool? desired, bool current)
        {
            if (desired == false && current)
                _rejections.Add($"Table '{table.Name}': {key} cannot be turned off (it is true in the org; once on, it stays on).");
            return desired == true && !current;
        }

        private static string Flag(bool value) => value ? "true" : "false";

        private void ExtraTables()
        {
            var named = desired.Tables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var entity in entities.Where(e => HasPrefix(e.LogicalName) && e.IsIntersect != true && !named.Contains(e.LogicalName))
                         .OrderBy(e => e.LogicalName, StringComparer.Ordinal))
                _extras.Add($"table {entity.LogicalName}");
        }

        // ---- columns ------------------------------------------------------------------------------------------------

        private void NewColumn(SchemaTable table, SchemaColumn column)
        {
            var where = $"{table.Name}.{column.Name}";
            if (column.Type == SchemaColumnTypes.Customer)
            {
                var names = CustomerColumns.Targets.Select(t => SchemaWebApiBodies.CustomerRelationshipName(table.Name, column.Name, t)).ToList();
                if (names.FirstOrDefault(n => n.Length > MaxRelationshipName) is { } tooLong)
                {
                    _rejections.Add($"Column '{where}': the relationship name '{tooLong}' is longer than {MaxRelationshipName} characters.");
                    return;
                }
                _lookups.Add(Solution(new CreateCustomerRelationshipsRequest
                {
                    Lookup = (LookupAttributeMetadata)SchemaColumnTypes.ToAttribute(column),
                    OneToManyRelationships = CustomerColumns.Targets.Select((target, i) => new OneToManyRelationshipMetadata
                    {
                        SchemaName = names[i], ReferencingEntity = table.Name, ReferencedEntity = target,
                        ReferencedAttribute = Entity(target)?.PrimaryIdAttribute ?? target + "id",
                    }).ToArray(),
                }));
                return;
            }
            if (column.Type == SchemaColumnTypes.Lookup)
            {
                var target = column.Target!;
                if (!TableExists(target))
                {
                    _rejections.Add($"Column '{where}': the lookup target '{target}' is not a table of the schema or of the org.");
                    return;
                }
                var name = $"{table.Name}_{column.Name}_{target}";
                if (name.Length > MaxRelationshipName)
                {
                    _rejections.Add($"Column '{where}': the relationship name '{name}' is longer than {MaxRelationshipName} characters.");
                    return;
                }
                _lookups.Add(Solution(new CreateOneToManyRequest
                {
                    OneToManyRelationship = new OneToManyRelationshipMetadata
                    {
                        SchemaName = name, ReferencingEntity = table.Name, ReferencedEntity = target,
                        ReferencedAttribute = Entity(target)?.PrimaryIdAttribute ?? target + "id",
                    },
                    Lookup = (LookupAttributeMetadata)SchemaColumnTypes.ToAttribute(column),
                }));
                return;
            }
            if (column.Choice is { } choice && !ChoiceExists(choice))
            {
                _rejections.Add($"Column '{where}': the choice '{choice}' is not a choice of the schema or of the org.");
                return;
            }
            var attribute = SchemaColumnTypes.ToAttribute(column);
            if (column.Calculated is not null || column.Rollup is not null || column.Formula is not null)
            {
                _computed.Add(Solution(new CreateAttributeRequest { EntityName = table.Name, Attribute = attribute }));
                return;
            }
            if (attribute is ImageAttributeMetadata { IsPrimaryImage: true } image)
            {
                // IsPrimaryImage true is ignored on create when the table has another image column (Learn, "Update image
                // columns"): the column is made primary by an update right after its create.
                image.IsPrimaryImage = null;
                _columns.Add(Solution(new CreateAttributeRequest { EntityName = table.Name, Attribute = attribute }));
                _columns.Add(Solution(new UpdateAttributeRequest
                {
                    EntityName = table.Name, Attribute = new ImageAttributeMetadata { LogicalName = column.Name, IsPrimaryImage = true },
                }));
                return;
            }
            _columns.Add(Solution(new CreateAttributeRequest { EntityName = table.Name, Attribute = attribute }));
        }

        /// <summary>Compares an existing attribute with its column: updates go to <paramref name="phase"/>, the rest are rejections.</summary>
        private void ExistingColumn(string table, SchemaColumn column, AttributeMetadata attribute, List<OrganizationRequest> phase)
        {
            var where = $"{table}.{column.Name}";
            var type = SchemaColumnTypes.TypeNameOf(attribute);
            if (type != column.Type)
            {
                var actual = type ?? attribute.AttributeTypeName?.Value ?? attribute.AttributeType?.ToString() ?? attribute.GetType().Name;
                _rejections.Add($"Column '{where}' is a {actual} column: its type cannot change to {column.Type}.");
                return;
            }
            if (column.SchemaName is { } schemaName && schemaName != (attribute.SchemaName ?? attribute.LogicalName))
                _rejections.Add($"Column '{where}': the SchemaName cannot change from '{attribute.SchemaName}' to '{schemaName}'.");
            // A column is simple, calculated, rollup or formula from its creation (plan 2.12a ruling K4, plan 2.12b ruling R13, plan
            // 2.12c); a computed one's updates come last.
            var kind = KindOf(attribute);
            var wanted = column.Calculated is not null ? "calculated" : column.Rollup is not null ? "rollup"
                : column.Formula is not null ? "formula" : "simple";
            if (kind != wanted)
            {
                _rejections.Add($"Column '{where}' is a {kind} column: it cannot become a {wanted} column (the SourceType of a column cannot change).");
                return;
            }
            if (kind != "simple") phase = _computed;

            var update = (AttributeMetadata)Activator.CreateInstance(attribute.GetType())!;
            update.LogicalName = attribute.LogicalName;
            var changed = false;
            if (Differs(column.Label, attribute.DisplayName))
            {
                update.DisplayName = SchemaColumnTypes.Label(column.Label!);
                changed = true;
            }
            if (Differs(column.Description, attribute.Description))
            {
                update.Description = SchemaColumnTypes.Label(column.Description!);
                changed = true;
            }

            // A formula is compared as the server stores it, without a leading '=' (plan 2.12c, ruling F2).
            if ((column.Calculated ?? column.Rollup ?? column.Formula) is { } written
                && (column.Formula is null ? written : FormulaColumnRules.Normalize(written)) is var formula
                && formula != ComputedColumnRules.FormulaOf(attribute))
            {
                update.SourceType = attribute.SourceType;
                ComputedColumnRules.SetFormula(update, formula);
                changed = true;
            }

            List<SchemaOption> added = [];
            switch (attribute)
            {
                case StringAttributeMetadata s:
                    if (column.MaxLength is { } stringLength && MaxLength(where, s.MaxLength ?? DefaultStringLength, stringLength))
                        (((StringAttributeMetadata)update).MaxLength, changed) = (stringLength, true);
                    if (column.AutoNumberFormat is { } format && format != s.AutoNumberFormat)
                        (((StringAttributeMetadata)update).AutoNumberFormat, changed) = (format, true);
                    break;
                case MemoAttributeMetadata m when column.MaxLength is { } length:
                    if (m.MaxLength is not { } current || MaxLength(where, current, length)) (((MemoAttributeMetadata)update).MaxLength, changed) = (length, true);
                    break;
                case DecimalAttributeMetadata d when column.Precision is { } precision && precision != (d.Precision ?? DefaultPrecision):
                    _rejections.Add($"Column '{where}': the precision cannot change from {d.Precision ?? DefaultPrecision} to {precision}.");
                    break;
                case FileAttributeMetadata f when column.MaxSizeInKB is { } size && size != (f.MaxSizeInKB ?? FileColumns.DefaultFileMaxSizeInKB):
                    (((FileAttributeMetadata)update).MaxSizeInKB, changed) = (size, true);
                    break;
                case ImageAttributeMetadata i:
                    var image = (ImageAttributeMetadata)update;
                    if (column.MaxSizeInKB is { } imageSize && imageSize != (i.MaxSizeInKB ?? FileColumns.DefaultImageMaxSizeInKB))
                        (image.MaxSizeInKB, changed) = (imageSize, true);
                    if (column.CanStoreFullImage is { } full && full != (i.CanStoreFullImage ?? false)) (image.CanStoreFullImage, changed) = (full, true);
                    // Only true moves the flag; a column without primary keeps it until another image column takes it.
                    if (column.Primary == true && i.IsPrimaryImage != true) (image.IsPrimaryImage, changed) = (true, true);
                    break;
                case LookupAttributeMetadata l when column.Type == SchemaColumnTypes.Lookup && l.Targets?.FirstOrDefault() != column.Target:
                    _rejections.Add($"Column '{where}': the lookup target cannot change from '{l.Targets?.FirstOrDefault() ?? "(none)"}' to '{column.Target}'.");
                    break;
                case EnumAttributeMetadata e:
                    var global = e.OptionSet is { IsGlobal: true } bound ? bound.Name : null;
                    if (!string.Equals(global, column.Choice, StringComparison.OrdinalIgnoreCase))
                    {
                        _rejections.Add($"Column '{where}' uses {(global is null ? "its own options" : $"the choice '{global}'")}: "
                                        + $"it cannot change to {(column.Choice is null ? "its own options" : $"the choice '{column.Choice}'")}.");
                        break;
                    }
                    if (column.Options is null) break; // a global choice's options are the choice's
                    var (newOptions, relabelled) = CompareOptions(column.Options, e.OptionSet, $"option {where}");
                    added = newOptions;
                    if (relabelled.Count > 0)
                    {
                        ((EnumAttributeMetadata)update).OptionSet = new OptionSetMetadata(new OptionMetadataCollection(relabelled));
                        changed = true;
                    }
                    break;
            }
            if (changed) phase.Add(Solution(new UpdateAttributeRequest { EntityName = table, Attribute = update }));
            foreach (var option in added)
            {
                phase.Add(Solution(new InsertOptionValueRequest
                {
                    EntityLogicalName = table, AttributeLogicalName = attribute.LogicalName, Value = option.Value,
                    Label = SchemaColumnTypes.Label(option.Label),
                }));
            }
        }

        /// <summary>True when <paramref name="requested"/> is larger; a smaller one is a rejection.</summary>
        private bool MaxLength(string where, int current, int requested)
        {
            if (requested < current)
                _rejections.Add($"Column '{where}': MaxLength cannot decrease (it is {current}, {requested} was requested).");
            return requested > current;
        }

        private void ExtraColumns(SchemaTable table, EntityMetadata entity)
        {
            var named = table.Columns.Select(c => c.Name).Append(table.PrimaryName.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var attribute in CustomAttributes(entity, _prefix).Where(a => !named.Contains(a.LogicalName)))
                _extras.Add($"column {table.Name}.{attribute.LogicalName}");
            var relationships = desired.Tables.SelectMany(t => t.ManyToMany).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var relationship in (entity.ManyToManyRelationships ?? [])
                         .Where(r => r.Entity1LogicalName == entity.LogicalName && HasPrefix(r.SchemaName) && !relationships.Contains(r.SchemaName))
                         .OrderBy(r => r.SchemaName, StringComparer.Ordinal))
                _extras.Add($"relationship {relationship.SchemaName}");
        }

        // ---- many-to-many -------------------------------------------------------------------------------------------

        private void ManyToMany(SchemaTable table, SchemaManyToMany relationship)
        {
            var where = $"Relationship '{relationship.Name}' of table '{table.Name}'";
            var existing = entities.SelectMany(e => e.ManyToManyRelationships ?? [])
                .FirstOrDefault(r => string.Equals(r.SchemaName, relationship.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.Entity1LogicalName != table.Name || existing.Entity2LogicalName != relationship.With)
                    _rejections.Add($"{where}: it relates '{existing.Entity1LogicalName}' and '{existing.Entity2LogicalName}' in the org, "
                                    + $"it cannot change to '{table.Name}' and '{relationship.With}'.");
                return;
            }
            if (Entity(relationship.Name.ToLowerInvariant()) is { } taken)
            {
                if (taken.IsIntersect != true) _rejections.Add($"{where}: the table '{taken.LogicalName}' already exists in the org.");
                return; // the intersect table of a relationship the org lists elsewhere
            }
            if (!TableExists(relationship.With))
            {
                _rejections.Add($"{where}: '{relationship.With}' is not a table of the schema or of the org.");
                return;
            }
            _manyToMany.Add(Solution(new CreateManyToManyRequest
            {
                ManyToManyRelationship = new ManyToManyRelationshipMetadata
                {
                    SchemaName = relationship.Name, Entity1LogicalName = table.Name, Entity2LogicalName = relationship.With,
                },
                IntersectEntitySchemaName = relationship.Name,
            }));
        }

        // ---- helpers ------------------------------------------------------------------------------------------------

        /// <summary>The options of <paramref name="desiredOptions"/> that <paramref name="current"/> lacks and those it labels otherwise; the current ones not desired are extras.</summary>
        private (List<SchemaOption> Added, List<OptionMetadata> Relabelled) CompareOptions(IReadOnlyList<SchemaOption> desiredOptions,
            OptionSetMetadata? current, string extra)
        {
            var existing = current?.Options ?? [];
            var added = new List<SchemaOption>();
            var relabelled = new List<OptionMetadata>();
            foreach (var option in desiredOptions)
            {
                var match = existing.FirstOrDefault(o => o.Value == option.Value);
                if (match is null) added.Add(option);
                else if (Differs(option.Label, match.Label)) relabelled.Add(NewOption(option));
            }
            foreach (var option in existing.Where(o => o.Value is { } v && desiredOptions.All(d => d.Value != v)).OrderBy(o => o.Value))
                _extras.Add($"{extra}.{option.Value!.Value.ToString(CultureInfo.InvariantCulture)}");
            return (added, relabelled);
        }

        private static OptionMetadata NewOption(SchemaOption option) => new(SchemaColumnTypes.Label(option.Label), option.Value);

        /// <summary>
        /// "calculated" (<c>SourceType</c> 1), "rollup" (2), "formula" (3) or "simple", for the comparison and the texts of the
        /// rejections.
        /// </summary>
        private static string KindOf(AttributeMetadata attribute) =>
            attribute.SourceType == ComputedColumnRules.Calculated ? "calculated" : RollupColumns.IsRollup(attribute) ? "rollup"
            : attribute.SourceType == ComputedColumnRules.Formula ? "formula" : "simple";

        /// <summary>A desired label that differs from the current one in <see cref="SchemaColumnTypes.LanguageCode"/> (null: not compared).</summary>
        private static bool Differs(string? desiredText, Label? current) =>
            desiredText is not null && !string.Equals(desiredText, SchemaColumnTypes.Text(current), StringComparison.Ordinal);

        private T Solution<T>(T request) where T : OrganizationRequest
        {
            if (desired.Solution is { Length: > 0 } solution) request.Parameters["SolutionUniqueName"] = solution;
            return request;
        }

        private EntityMetadata? Entity(string name) => entities.FirstOrDefault(e => e.LogicalName == name);

        private OptionSetMetadataBase? OptionSet(string name) =>
            optionSets.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

        private bool TableExists(string name) => desired.Tables.Any(t => t.Name == name) || Entity(name) is not null;

        private bool ChoiceExists(string name) =>
            desired.Choices.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) || OptionSet(name) is OptionSetMetadata;

        private bool HasPrefix(string? name) => name is not null && name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);

        private static OwnershipTypes Ownership(SchemaOwnership ownership) =>
            ownership == SchemaOwnership.Organization ? OwnershipTypes.OrganizationOwned : OwnershipTypes.UserOwned;

        private static string Describe(OwnershipTypes? ownership) => ownership switch
        {
            null or OwnershipTypes.UserOwned => "user",
            OwnershipTypes.OrganizationOwned => "organization",
            var other => other.Value.ToString(),
        };
    }

    /// <summary>
    /// The attributes of <paramref name="entity"/> a schema describes: named with <paramref name="prefix"/>, other than the
    /// primary key and name and the attributes the server generates (a money's base, an attribute of another, the currency pair:
    /// <see cref="SchemaColumnTypes.IsGenerated"/>), by name.
    /// </summary>
    internal static IEnumerable<AttributeMetadata> CustomAttributes(EntityMetadata entity, string prefix)
    {
        var primaryId = entity.PrimaryIdAttribute ?? entity.LogicalName + "id";
        return (entity.Attributes ?? [])
            .Where(a => a.LogicalName is { } name && name.StartsWith(prefix, StringComparison.Ordinal)
                        && name != primaryId && name != entity.PrimaryNameAttribute
                        && !SchemaColumnTypes.IsGenerated(entity, a))
            .OrderBy(a => a.LogicalName, StringComparer.Ordinal);
    }
}

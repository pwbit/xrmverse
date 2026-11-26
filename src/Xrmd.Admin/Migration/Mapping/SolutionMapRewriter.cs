using System.Xml.Linq;
using Xrmd.Core.Errors;
using Xrmd.Migration.Mapping;
using Xrmd.Migration.Source;
using Xrmd.Solution;

namespace Xrmd.Admin.Migration.Mapping;

/// <summary>
/// A component taken out of a solution by <see cref="SolutionMapRewriter"/>.
/// </summary>
/// <param name="Component">What was removed, as listed in <see cref="SolutionRewrite.Removed"/> (<c>form lead/Information</c>).</param>
/// <param name="Table">The source table the component belongs to (<c>lead</c>; <c>account</c> for its lookup to a missing table).</param>
/// <param name="Column">The column of an attribute; null for any other component.</param>
/// <param name="Cause">The mapped or missing source table that made it go (<c>lead</c>, <c>quote</c>).</param>
/// <param name="Target">The target of <paramref name="Cause"/> when it is mapped; null when it is missing.</param>
public sealed record SolutionRemoval(string Component, string Table, string? Column, string Cause, string? Target)
{
    /// <summary>True when the cause is a mapped table (R11), false when it is a table the organization lacks (R11b).</summary>
    public bool Mapped => Target is not null;
}

/// <summary>
/// The result of <see cref="SolutionMapRewriter.Rewrite"/>: the solution file to import (the original bytes when nothing
/// changed), whether it is managed, and every component taken out (<see cref="Removals"/> says why for each).
/// </summary>
public sealed record SolutionRewrite(byte[] Zip, bool Changed, bool Managed, IReadOnlyList<string> Removed)
{
    /// <summary>The removed components with their table and cause, in the order of <see cref="Removed"/>.</summary>
    public IReadOnlyList<SolutionRemoval> Removals { get; init; } = [];

    /// <summary>The relationships pointed to a target table (R5), as <c>relationship new_lead_account_leadid → xv_lead</c>.</summary>
    public IReadOnlyList<string> Retargeted { get; init; } = [];
}

/// <summary>
/// R11 and R11b of the migration table mapping (spec §8.3): before a customer solution is imported, the components of a mapped
/// source table (renamed entries only; a same-table entry leaves the solution alone) and of a table the organization lacks are
/// taken out of its <c>customizations.xml</c> and <c>solution.xml</c>: the entity node with its custom attributes, forms, views,
/// charts and ribbon, its root component, its relationships and entity maps. A lookup of another table to a mapped table is
/// retargeted to the target table (its relationship's <c>ReferencedEntityName</c>), and so is either end of an N:N; a lookup to a
/// missing table is removed with its relationship. Every removed component is listed.
/// </summary>
public static class SolutionMapRewriter
{
    /// <summary>
    /// A table a customer made (<c>new_progetto</c>): a name with a customization prefix that is not a Microsoft first-party one.
    /// Dataverse names every custom table <c>&lt;prefix&gt;_&lt;name&gt;</c>; its own tables have no prefix (<c>quote</c>) or a
    /// first-party one (<c>msdyn_</c>).
    /// </summary>
    public static bool IsCustomTableName(string logicalName)
    {
        ArgumentNullException.ThrowIfNull(logicalName);
        return logicalName.IndexOf('_', StringComparison.Ordinal) > 0 && !ExclusionCatalog.IsFirstParty(logicalName.ToLowerInvariant());
    }

    /// <summary>
    /// Takes the components of the mapped tables of <paramref name="maps"/> and of the tables <paramref name="isMissingTable"/>
    /// names out of <paramref name="solutionZip"/>. A file that is not a readable solution is returned unchanged (the import then
    /// says what is wrong with it).
    /// </summary>
    /// <param name="isMissingTable">For a source table (lower-case logical name): true when it is not custom in the solution,
    /// not in the organization and not mapped.</param>
    public static SolutionRewrite Rewrite(byte[] solutionZip, MapSet maps, Func<string, bool> isMissingTable)
    {
        ArgumentNullException.ThrowIfNull(solutionZip);
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(isMissingTable);

        XDocument solution, customizations;
        IReadOnlyDictionary<string, byte[]> files;
        try
        {
            (solution, customizations, files) = SolutionZip.Read(solutionZip);
        }
        catch (XrmdFaultException)
        {
            return new SolutionRewrite(solutionZip, false, false, []);
        }

        var manifest = solution.Root?.Element("SolutionManifest");
        var managed = (manifest?.Element("Managed")?.Value.Trim() ?? "") is "1" or "true" or "True";
        var work = new Work(maps, isMissingTable);
        work.Run(solution, customizations);
        if (!work.Changed) return new SolutionRewrite(solutionZip, false, managed, []);

        var others = files.Where(f => !f.Key.Equals(SolutionZip.ContentTypesEntry, StringComparison.OrdinalIgnoreCase) && !work.DroppedFiles.Contains(f.Key))
            .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var zip = SolutionZip.Write(solution, customizations, others);
        return new SolutionRewrite(zip, true, managed, [.. work.Removals.Select(r => r.Component)]) 
        {
            Removals = work.Removals,
            Retargeted = work.Retargeted,
        };
    }

    /// <summary>The rewrite of one solution.</summary>
    private sealed class Work(MapSet maps, Func<string, bool> isMissingTable)
    {
        private readonly Dictionary<string, (bool Strip, string? Target)> _verdicts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removedEntities = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removedRelationships = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Guid> _removedIds = [];
        private readonly HashSet<string> _removedAttributes = new(StringComparer.Ordinal);
        private readonly List<(string? Table, string? Column, string Cause)> _lookupsToMissing = [];

        public List<SolutionRemoval> Removals { get; } = [];

        public List<string> Retargeted { get; } = [];

        /// <summary>The archive paths of removed processes' files (no leading <c>/</c>), left out of the rewritten file.</summary>
        public HashSet<string> DroppedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Changed { get; private set; }

        /// <summary>Whether the components of <paramref name="table"/> go, and its target when it is mapped.</summary>
        private (bool Strip, string? Target) Verdict(string table)
        {
            table = table.ToLowerInvariant();
            // A process or step of no table (an action, a global message) names "none".
            if (table is "none") return (false, null);
            if (_verdicts.TryGetValue(table, out var verdict)) return verdict;
            verdict = maps.TargetOf(table) is { } target ? (true, (string?)target)
                : maps.BySource(table) is null && isMissingTable(table) ? (true, (string?)null)
                : (false, (string?)null);
            return _verdicts[table] = verdict;
        }

        private void Remove(string component, string table, string? column, string cause)
        {
            Removals.Add(new SolutionRemoval(component, table, column, cause, Verdict(cause).Target));
            Changed = true;
        }

        public void Run(XDocument solution, XDocument customizations)
        {
            var root = customizations.Root;
            if (root is not null)
            {
                var entities = root.Element("Entities")?.Elements("Entity").ToList() ?? [];
                foreach (var entity in entities) StripEntity(entity);
                var relationships = root.Element("EntityRelationships")?.Elements("EntityRelationship").ToList() ?? [];
                foreach (var relationship in relationships) Relationship(relationship);
                RemoveLookupsToMissingTables(relationships, entities);
                foreach (var map in root.Element("EntityMaps")?.Elements("EntityMap").ToList() ?? [])
                    EntityMap(map);
                foreach (var process in root.Element("Workflows")?.Elements("Workflow").ToList() ?? [])
                    Process(process);
                foreach (var step in root.Element("SdkMessageProcessingSteps")?.Elements("SdkMessageProcessingStep").ToList() ?? [])
                    Step(step);
            }
            RootComponents(solution.Root?.Element("SolutionManifest")?.Element("RootComponents"), customizations.Root);
        }

        private static string? EntityName(XElement entity) =>
            NonEmpty((string?)entity.Element("EntityInfo")?.Element("entity")?.Attribute("Name")) ?? NonEmpty(entity.Element("Name")?.Value);

        private void StripEntity(XElement entity)
        {
            if (EntityName(entity) is not { } schemaName) return;
            var table = schemaName.ToLowerInvariant();
            if (!Verdict(table).Strip) return;

            Remove($"entity {table}", table, null, table);
            _removedEntities.Add(table);
            foreach (var attribute in entity.Element("EntityInfo")?.Element("entity")?.Element("attributes")?.Elements("attribute") ?? [])
            {
                // A custom column of a mapped table goes through R7 (the schema delta) instead; listed either way.
                if (!IsCustom(attribute) || AttributeName(attribute) is not { } column) continue;
                Remove($"attribute {table}.{column}", table, column, table);
                _removedAttributes.Add(column);
            }
            foreach (var form in entity.Element("FormXml")?.Elements("forms").SelectMany(f => f.Elements("systemform")) ?? [])
                Component("form", table, form, "formid");
            foreach (var view in entity.Element("SavedQueries")?.Element("savedqueries")?.Elements("savedquery") ?? [])
                Component("view", table, view, "savedqueryid");
            foreach (var chart in entity.Element("Visualizations")?.Elements("visualization") ?? [])
                Component("chart", table, chart, "savedqueryvisualizationid");
            if (entity.Element("RibbonDiffXml") is { } ribbon && CustomizationsReader.HasContent(ribbon))
                Remove($"ribbon {table}", table, null, table);
            entity.Remove();
        }

        private void Component(string kind, string table, XElement element, string idElement)
        {
            var id = NonEmpty(element.Element(idElement)?.Value);
            if (id is not null && Guid.TryParse(id, out var guid)) _removedIds.Add(guid);
            var name = NonEmpty((string?)element.Element("LocalizedNames")?.Elements("LocalizedName").FirstOrDefault()?.Attribute("description"))
                       ?? NonEmpty(element.Element("name")?.Value) ?? id ?? "?";
            Remove($"{kind} {table}/{name}", table, null, table);
        }

        private void Relationship(XElement relationship)
        {
            var name = NonEmpty((string?)relationship.Attribute("Name")) ?? "?";
            if (NonEmpty(relationship.Element("EntityRelationshipType")?.Value) == "ManyToMany")
            {
                var first = Lower(relationship.Element("FirstEntityName"));
                var second = Lower(relationship.Element("SecondEntityName"));
                // A missing end takes the relationship with it; a mapped end is retargeted.
                if (new[] { first, second }.FirstOrDefault(e => e is not null && Verdict(e) is { Strip: true, Target: null }) is { } missing)
                {
                    RemoveRelationship(relationship, name, missing, missing);
                    return;
                }
                var targets = new[] { Retarget(relationship.Element("FirstEntityName"), first), Retarget(relationship.Element("SecondEntityName"), second) }
                    .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                if (targets.Count > 0) Retargeted.Add($"relationship {name} → {string.Join(", ", targets)}");
                return;
            }

            var referencing = Lower(relationship.Element("ReferencingEntityName"));
            var referenced = Lower(relationship.Element("ReferencedEntityName"));
            if (referencing is not null && Verdict(referencing).Strip)
            {
                // The lookup is a column of the mapped or missing table: it went with its entity (or goes through R7).
                RemoveRelationship(relationship, name, referencing, referencing);
                return;
            }
            if (referenced is null || Verdict(referenced) is not { Strip: true } verdict) return;
            if (verdict.Target is null)
            {
                // A lookup to a table the organization lacks has no target to point to: the relationship goes, and its lookup with
                // it unless another relationship of the file still uses it (RemoveLookupsToMissingTables).
                _lookupsToMissing.Add((referencing, Lower(relationship.Element("ReferencingAttributeName")), referenced));
                RemoveRelationship(relationship, name, referencing ?? referenced, referenced);
                return;
            }
            // A lookup to a mapped table points to its target (R5).
            Retarget(relationship.Element("ReferencedEntityName"), referenced);
            if (relationship.Element("ReferencedAttributeName") is { } key && string.Equals(key.Value.Trim(), referenced + "id", StringComparison.OrdinalIgnoreCase))
                key.Value = verdict.Target + "id";
            Retargeted.Add($"relationship {name} → {verdict.Target}");
        }

        /// <summary>
        /// The lookups whose relationship to a missing table was removed: a lookup that no kept 1:N of the file still uses (a
        /// multi-table lookup keeps its other relationships) is removed from its entity node and listed.
        /// </summary>
        private void RemoveLookupsToMissingTables(List<XElement> relationships, List<XElement> entities)
        {
            foreach (var (table, column, cause) in _lookupsToMissing.Distinct())
            {
                if (table is null || column is null) continue;
                var stillUsed = relationships.Any(r => r.Parent is not null && NonEmpty(r.Element("EntityRelationshipType")?.Value) != "ManyToMany"
                                                       && Lower(r.Element("ReferencingEntityName")) == table && Lower(r.Element("ReferencingAttributeName")) == column);
                if (stillUsed) continue;
                var owner = entities.FirstOrDefault(e => e.Parent is not null && EntityName(e)?.ToLowerInvariant() == table);
                var attribute = owner?.Element("EntityInfo")?.Element("entity")?.Element("attributes")?.Elements("attribute")
                    .FirstOrDefault(a => AttributeName(a) == column);
                if (attribute is null) continue;
                Remove($"attribute {table}.{column}", table, column, cause);
                _removedAttributes.Add(column);
                attribute.Remove();
            }
        }

        /// <summary>A process (<c>Workflow</c>) on a mapped or missing table, with its XAML or JSON file.</summary>
        private void Process(XElement process)
        {
            if (Lower(process.Element("PrimaryEntity")) is not { } table || !Verdict(table).Strip) return;
            var id = Guid.TryParse((string?)process.Attribute("WorkflowId"), out var parsed) ? parsed : (Guid?)null;
            if (id is { } removed) _removedIds.Add(removed);
            foreach (var file in new[] { "XamlFileName", "JsonFileName" })
                if (NonEmpty(process.Element(file)?.Value) is { } path) DroppedFiles.Add(path.Replace('\\', '/').TrimStart('/'));
            Remove($"process {table}/{NonEmpty((string?)process.Attribute("Name")) ?? id?.ToString("D") ?? "?"}", table, null, table);
            process.Remove();
        }

        /// <summary>A plug-in step on a mapped or missing table.</summary>
        private void Step(XElement step)
        {
            if (Lower(step.Element("PrimaryEntity")) is not { } table || !Verdict(table).Strip) return;
            var id = Guid.TryParse((string?)step.Attribute("SdkMessageProcessingStepId"), out var parsed) ? parsed : (Guid?)null;
            if (id is { } removed) _removedIds.Add(removed);
            Remove($"step {table}/{NonEmpty((string?)step.Attribute("Name")) ?? id?.ToString("D") ?? "?"}", table, null, table);
            step.Remove();
        }

        private void RemoveRelationship(XElement relationship, string name, string table, string cause)
        {
            Remove($"relationship {name}", table, null, cause);
            _removedRelationships.Add(name);
            relationship.Remove();
        }

        /// <summary>Points <paramref name="element"/> to the target of <paramref name="table"/> when it is mapped; returns that target.</summary>
        private string? Retarget(XElement? element, string? table)
        {
            if (element is null || table is null || Verdict(table).Target is not { } target) return null;
            element.Value = target;
            Changed = true;
            return target;
        }

        private void EntityMap(XElement map)
        {
            var source = Lower(map.Element("EntitySource"));
            var target = Lower(map.Element("EntityTarget"));
            if (new[] { source, target }.FirstOrDefault(e => e is not null && Verdict(e).Strip) is not { } cause) return;
            Remove($"entity map {source}/{target}", source ?? cause, null, cause);
            map.Remove();
        }

        private void RootComponents(XElement? roots, XElement? customizations)
        {
            if (roots is null) return;
            foreach (var root in roots.Elements("RootComponent").ToList())
            {
                var type = (string?)root.Attribute("type");
                var schemaName = NonEmpty((string?)root.Attribute("schemaName"));
                var remove = false;
                if (type == "1" && schemaName is not null)
                {
                    var table = schemaName.ToLowerInvariant();
                    if (Verdict(table).Strip)
                    {
                        // An entity named by the manifest alone (no node in customizations.xml) is listed here.
                        if (_removedEntities.Add(table)) Remove($"entity {table}", table, null, table);
                        remove = true;
                    }
                }
                else if (type == "10" && schemaName is not null) remove = _removedRelationships.Contains(schemaName);
                else if (type == "2" && schemaName is not null) remove = IsRemovedAttribute(schemaName.ToLowerInvariant(), customizations);
                if (!remove && Guid.TryParse((string?)root.Attribute("id"), out var id)) remove = _removedIds.Contains(id);
                if (!remove) continue;
                root.Remove();
                Changed = true;
            }
        }

        /// <summary>
        /// Whether an attribute root component (by <c>schemaName</c>, or <c>table.column</c>) names a removed column. A bare name is
        /// matched only when no kept table of the file has a column of that name. A root by id alone cannot be matched (the file's
        /// columns carry no id); the import then only warns that it is not in the file.
        /// </summary>
        private bool IsRemovedAttribute(string key, XElement? customizations)
        {
            var dot = key.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0) return Removals.Any(r => r.Component == $"attribute {key}");
            if (!_removedAttributes.Contains(key)) return false;
            return !(customizations?.Element("Entities")?.Elements("Entity")
                .SelectMany(e => e.Element("EntityInfo")?.Element("entity")?.Element("attributes")?.Elements("attribute") ?? [])
                .Any(a => AttributeName(a) == key) ?? false);
        }

        /// <summary>Whether a column of the file is custom: <c>IsCustomField</c> 1, or absent (the reader's rule).</summary>
        private static bool IsCustom(XElement attribute) =>
            (NonEmpty(attribute.Element("IsCustomField")?.Value) ?? "1") is "1" or "true" or "True";

        private static string? AttributeName(XElement attribute) =>
            (NonEmpty(attribute.Element("LogicalName")?.Value) ?? NonEmpty(attribute.Element("Name")?.Value)
                ?? NonEmpty((string?)attribute.Attribute("PhysicalName")))?.ToLowerInvariant();

        private static string? Lower(XElement? element) => NonEmpty(element?.Value)?.ToLowerInvariant();

        private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

using Xrmd.Core.Errors;
using System.Xml.Linq;

namespace Xrmd.Solution.SourceLayout.Writers;

/// <summary>
/// Tables: <c>Entities/&lt;name&gt;/Entity.xml</c> keeps the table definition, with each form, view, chart and the ribbon
/// changes in a file of its own beside it (<c>FormXml/&lt;type&gt;/&lt;id&gt;.xml</c>, <c>SavedQueries/&lt;id&gt;.xml</c>,
/// <c>Visualizations/&lt;id&gt;.xml</c>, <c>RibbonDiff.xml</c>). The split parts stay in <c>Entity.xml</c> as empty elements.
/// </summary>
internal static class EntityWriter
{
    /// <summary>The children of an <c>Entity</c> element the reference folders show; any other one is refused.</summary>
    private static readonly HashSet<string> KnownParts =
        ["Name", "EntityInfo", "FormXml", "SavedQueries", "Visualizations", "RibbonDiffXml"];

    public static void Write(UnpackContext context)
    {
        var entities = context.Collection("Entities");
        if (entities is null) return;
        foreach (var entity in entities.Elements())
        {
            if (entity.Name != "Entity") throw UnpackRules.Unsupported(entity);
            var name = UnpackRules.Required(entity.Element("Name"), "Entity/Name").Value;
            var folder = "Entities/" + UnpackRules.SafeSegment(name, "table");
            foreach (var part in entity.Elements())
            {
                if (!KnownParts.Contains(part.Name.LocalName))
                    throw UnpackRules.Unsupported($"table part {part.Name.LocalName}", name);
            }

            WriteForms(context, name, folder, entity.Element("FormXml"));
            WriteViews(context, name, folder, entity.Element("SavedQueries"));
            WriteCharts(context, folder, entity.Element("Visualizations"));
            var ribbon = entity.Element("RibbonDiffXml");
            if (ribbon is not null)
            {
                context.Write(folder + "/RibbonDiff.xml", LayoutXml.RootFrom("RibbonDiffXml", ribbon));
                ribbon.RemoveNodes();
            }

            context.Write(folder + "/Entity.xml", LayoutXml.RootFrom("Entity", entity));
        }
        entities.RemoveNodes();
    }

    private static void WriteForms(UnpackContext context, string table, string folder, XElement? formXml)
    {
        if (formXml is null) return;
        foreach (var forms in formXml.Elements())
        {
            // Pack rebuilds each group with its type only: any other attribute would be lost, so it is refused.
            RefuseAttributes(forms, table, "type");
            var type = UnpackRules.SafeSegment((string?)forms.Attribute("type") ?? "", "form type");
            foreach (var form in forms.Elements())
            {
                var id = UnpackRules.Required(form.Element("formid"), "systemform/formid").Value;
                context.Write($"{folder}/FormXml/{type}/{UnpackRules.SafeSegment(id, "form id")}.xml", LayoutXml.Root("forms", form));
            }
        }
        formXml.RemoveNodes();
    }

    private static void WriteViews(UnpackContext context, string table, string folder, XElement? savedQueries)
    {
        if (savedQueries is null) return;
        // Pack rebuilds the group without attributes: any attribute would be lost, so it is refused.
        foreach (var group in savedQueries.Elements()) RefuseAttributes(group, table);
        foreach (var view in savedQueries.Elements().Elements())
        {
            var id = UnpackRules.Required(view.Element("savedqueryid"), "savedquery/savedqueryid").Value;
            context.Write($"{folder}/SavedQueries/{UnpackRules.SafeSegment(id, "view id")}.xml", LayoutXml.Root("savedqueries", view));
        }
        savedQueries.RemoveNodes();
    }

    /// <summary>A 400 <see cref="ErrorCodes.UnsupportedComponent"/> fault naming the table when <paramref name="group"/> has an attribute other than <paramref name="kept"/>.</summary>
    private static void RefuseAttributes(XElement group, string table, params string[] kept)
    {
        var extra = group.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && !kept.Contains(a.Name.ToString()));
        if (extra is not null)
            throw UnpackRules.Unsupported($"attribute {extra.Name.LocalName} on the {group.Name.LocalName} of table", table);
    }

    private static void WriteCharts(UnpackContext context, string folder, XElement? visualizations)
    {
        if (visualizations is null) return;
        foreach (var chart in visualizations.Elements())
        {
            var id = UnpackRules.Required(chart.Element("savedqueryvisualizationid"), "visualization/savedqueryvisualizationid").Value;
            context.Write($"{folder}/Visualizations/{UnpackRules.SafeSegment(id, "chart id")}.xml", LayoutXml.RootFrom("visualization", chart));
        }
        visualizations.RemoveNodes();
    }

    /// <summary>Pack: rebuilds each <c>Entity</c> from its folder, the split parts put back into their empty elements.</summary>
    public static void Read(PackContext context)
    {
        // Only a name followed by '/' is a table folder; a stray file directly in Entities/ is left for the unexpected-file check.
        var folders = context.Unclaimed("Entities/").Select(k => PackContext.Rest(k, "Entities/"))
            .Where(rest => rest.Length >= 2).Select(rest => rest[0])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (folders.Count == 0) return;
        var container = context.Container("Entities");
        foreach (var name in folders)
        {
            var folder = $"Entities/{name}/";
            var entityPath = folder + "Entity.xml";
            var first = context.Unclaimed(folder).First();
            var entity = PackContext.FromRoot("Entity", context.Load(entityPath, "Entity", $"the table folder (it holds {first})"));
            if (entity.Element("Name")?.Value != name)
                throw XrmdFaultException.InvalidArgument($"The file {entityPath} must name the table '{name}', as its folder does.");

            var forms = context.Unclaimed(folder + "FormXml/").Where(k => PackContext.Rest(k, folder + "FormXml/").Length == 2).ToList();
            if (forms.Count > 0)
            {
                var formXml = Placeholder(entity, "FormXml", entityPath, forms[0]);
                foreach (var byType in forms.GroupBy(k => PackContext.Rest(k, folder + "FormXml/")[0]))
                {
                    formXml.Add(new XElement("forms", new XAttribute("type", byType.Key),
                        byType.Select(k => context.Load(k, "forms", entityPath).Elements())));
                }
            }

            var views = Direct(context, folder + "SavedQueries/");
            if (views.Count > 0)
            {
                Placeholder(entity, "SavedQueries", entityPath, views[0])
                    .Add(new XElement("savedqueries", views.Select(k => context.Load(k, "savedqueries", entityPath).Elements())));
            }

            var charts = Direct(context, folder + "Visualizations/");
            if (charts.Count > 0)
            {
                Placeholder(entity, "Visualizations", entityPath, charts[0])
                    .Add(charts.Select(k => PackContext.FromRoot("visualization", context.Load(k, "visualization", entityPath))));
            }

            var ribbonPath = folder + "RibbonDiff.xml";
            if (context.Has(ribbonPath))
                PackContext.Fill(Placeholder(entity, "RibbonDiffXml", entityPath, ribbonPath), context.Load(ribbonPath, "RibbonDiffXml", entityPath));

            container.Add(entity);
        }
    }

    /// <summary>The unclaimed <c>.xml</c> files directly in <paramref name="folder"/>.</summary>
    private static List<string> Direct(PackContext context, string folder) =>
        context.Unclaimed(folder).Where(k => !k[folder.Length..].Contains('/') && k.EndsWith(".xml", StringComparison.Ordinal)).ToList();

    /// <summary>The empty element <paramref name="name"/> of the entity that receives the part files, or a 400 fault.</summary>
    private static XElement Placeholder(XElement entity, string name, string entityPath, string partPath)
    {
        var placeholder = entity.Element(name);
        if (placeholder is null || placeholder.Nodes().Any() || placeholder.HasAttributes)
            throw XrmdFaultException.InvalidArgument(
                $"The file {entityPath} must hold an empty {name} element to receive {partPath}.");
        return placeholder;
    }
}

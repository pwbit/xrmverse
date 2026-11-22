using System.Globalization;
using System.Xml.Linq;
using Xrmd.Core.Errors;

namespace Xrmd.Solution;

/// <summary>An <c>AppModuleComponent</c> of an app module: a type, and the component by id or by schema name.</summary>
/// <param name="Type">The component type (1 entity, 26 view, 29 business process flow, 59 chart, 60 form, 62 site map, any other as is).</param>
/// <param name="Id">The component's id, when the file names it by id (forms, views, charts).</param>
/// <param name="SchemaName">The component's schema name, when the file names it that way (entities, site maps).</param>
/// <param name="Behavior">The root component behavior (0 include subcomponents, 1 do not include subcomponents, 2 include as shell only).</param>
public sealed record AppModuleComponentRef(int Type, Guid? Id, string? SchemaName, int Behavior);

/// <summary>An <c>appsetting</c> of an app module (#129): the definition, by unique name, and the value as the file writes it.</summary>
/// <param name="DefinitionUniqueName">The <c>settingdefinitionid.uniquename</c> attribute: the setting's definition.</param>
/// <param name="Value">The <c>value</c> child (a string, such as <c>true</c> or <c>1</c>), or null when the file gives none.</param>
public sealed record AppSettingRef(string DefinitionUniqueName, string? Value);

/// <summary>What an <c>AppModule</c> element of <c>customizations.xml</c> gives the <c>appmodule</c> row and its links.</summary>
/// <param name="Columns">The columns of the row the file sets, by logical name (integers, booleans, GUIDs and strings as plain values).</param>
/// <param name="Components">The <c>AppModuleComponents</c>, in file order.</param>
/// <param name="RoleIds">The <c>AppModuleRoleMaps</c> role ids, in file order, without duplicates.</param>
/// <param name="Settings">The <c>appsettings</c> of the app, in file order (#129).</param>
public sealed record AppModuleDefinition(
    IReadOnlyDictionary<string, object?> Columns, IReadOnlyList<AppModuleComponentRef> Components, IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<AppSettingRef> Settings);

/// <summary>The parse of an <c>AppModule</c> element of a solution file (plan 3.5 UI-1a, task 2).</summary>
public static class AppModuleXml
{
    /// <summary>The columns read from the element's children, by the child's name: (logical name, kind).</summary>
    private static readonly (string Element, string Column, Kind Kind)[] ColumnElements =
    [
        ("ClientType", "clienttype", Kind.Integer), ("FormFactor", "formfactor", Kind.Integer),
        ("NavigationType", "navigationtype", Kind.Integer), ("statecode", "statecode", Kind.Integer),
        ("statuscode", "statuscode", Kind.Integer), ("WebResourceId", "webresourceid", Kind.Guid),
        ("WelcomePageId", "welcomepageid", Kind.Guid), ("Url", "url", Kind.Text), ("AppModuleVersion", "appmoduleversion", Kind.Text),
        ("IsDefault", "isdefault", Kind.Boolean), ("IsFeatured", "isfeatured", Kind.Boolean),
    ];

    private enum Kind { Integer, Guid, Text, Boolean }

    /// <summary>The attribute of an <c>appsetting</c> element that names its definition.</summary>
    private const string SettingDefinitionAttribute = "settingdefinitionid.uniquename";

    /// <summary>Parses <paramref name="appModule"/>. A child that is absent or empty sets no column.</summary>
    public static AppModuleDefinition Parse(XElement appModule)
    {
        var columns = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (element, column, kind) in ColumnElements)
        {
            var text = appModule.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, element, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            columns[column] = kind switch
            {
                Kind.Integer => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : Bad(element, text),
                Kind.Guid => Guid.TryParse(text, out var guid) ? guid : Bad(element, text),
                Kind.Boolean => text is "1" or "true" or "True" ? true : text is "0" or "false" or "False" ? false : Bad(element, text),
                _ => text,
            };
        }

        var components = new List<AppModuleComponentRef>();
        foreach (var component in appModule.Element("AppModuleComponents")?.Elements("AppModuleComponent") ?? [])
        {
            if (!int.TryParse((string?)component.Attribute("type"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
                throw XrmdFaultException.InvalidArgument("An AppModuleComponent has no numeric 'type'.");
            var id = Guid.TryParse((string?)component.Attribute("id"), out var parsed) ? parsed : (Guid?)null;
            var schemaName = (string?)component.Attribute("schemaName");
            var behavior = int.TryParse((string?)component.Attribute("behavior"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0;
            components.Add(new(type, id, string.IsNullOrEmpty(schemaName) ? null : schemaName, behavior));
        }

        var roles = new List<Guid>();
        foreach (var role in appModule.Element("AppModuleRoleMaps")?.Elements("Role") ?? [])
        {
            var text = (string?)role.Attribute("id");
            if (!Guid.TryParse(text, out var roleId))
                throw XrmdFaultException.InvalidArgument($"A role of the AppModuleRoleMaps of an AppModule has the id '{text}', which is not valid.");
            if (!roles.Contains(roleId)) roles.Add(roleId);
        }

        // The app settings (#129): an export names each definition by its unique name, without ids. A setting without its
        // definition, or one given twice, is refused rather than guessed.
        var settings = new List<AppSettingRef>();
        foreach (var setting in appModule.Element("appsettings")?.Elements("appsetting") ?? [])
        {
            var definition = ((string?)setting.Attribute(SettingDefinitionAttribute))?.Trim();
            if (string.IsNullOrEmpty(definition))
                throw XrmdFaultException.InvalidArgument($"An appsetting of an AppModule has no '{SettingDefinitionAttribute}' attribute.");
            if (settings.Any(x => string.Equals(x.DefinitionUniqueName, definition, StringComparison.OrdinalIgnoreCase)))
                throw XrmdFaultException.InvalidArgument($"The app setting '{definition}' of an AppModule is given more than once.");
            settings.Add(new(definition, setting.Element("value")?.Value));
        }

        return new(columns, components, roles, settings);
    }

    private static object Bad(string element, string text) =>
        throw XrmdFaultException.InvalidArgument($"The element '{element}' of an AppModule has the value '{text}', which is not valid.");
}

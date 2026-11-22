using System.Data;
using System.Xml.Linq;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using Xrmd.Metadata;

namespace Xrmd.Solution.Layers;

/// <summary>
/// The managed properties of a component (plan 2.6, ruling M13), as its top Managed layer delivered them. Each is true
/// (allowed) unless the layer's element says 0 or false.
/// </summary>
/// <param name="IsCustomizable">Whether unmanaged writes may change the component (and, for a table, add columns or
/// relationships to it).</param>
/// <param name="IsRenameable">Whether its display names may change (tables, columns).</param>
/// <param name="CanBeDeleted">Whether it may be deleted (a managed component is never deleted directly anyway, ruling M14).</param>
/// <param name="CanModifyRequirementLevel">Whether the required level of a column may change
/// (<c>CanModifyRequirementLevelSettings</c>).</param>
/// <param name="CanCreateAttributes">Whether unmanaged columns and relationships may be added to the table (a table's flag).</param>
/// <param name="CanCreateForms">Whether unmanaged forms (<c>systemform</c>) may be created for the table (a table's flag).</param>
/// <param name="CanCreateViews">Whether unmanaged views (<c>savedquery</c>) may be created for the table (a table's flag).</param>
/// <param name="CanCreateCharts">Whether unmanaged system charts (<c>savedqueryvisualization</c>, plan 3.5 UI-1f) may be created
/// for the table (a table's flag).</param>
public sealed record ManagedFlags(bool IsCustomizable, bool IsRenameable, bool CanBeDeleted, bool CanModifyRequirementLevel,
    bool CanCreateAttributes = true, bool CanCreateForms = true, bool CanCreateViews = true, bool CanCreateCharts = true)
{
    /// <summary>Everything allowed: a layer whose element names no property.</summary>
    public static ManagedFlags Open { get; } = new(true, true, true, true);
}

/// <summary>
/// The managed state of a component (rulings M13 and M14): the flags of its top Managed layer and the managed solutions that
/// hold a layer on it, top first.
/// </summary>
/// <param name="Flags">The managed properties of the top Managed layer.</param>
/// <param name="Solutions">The unique names of the managed solutions layering the component, top first (never empty).</param>
public sealed record ManagedState(ManagedFlags Flags, IReadOnlyList<string> Solutions)
{
    /// <summary>The managed solution whose layer is on top (the one whose flags apply).</summary>
    public string Solution => Solutions[0];
}

/// <summary>
/// Reads the managed properties of a component from its layers (plan 2.6, ruling M13) and words the refusals of rulings M13
/// and M14. "Managed" means the component has a Managed layer (a solution's, base or holding); an unmanaged component's
/// flags are informative only, so it has none here. The flags come from the payload of the top Managed layer, which is the
/// file content that wins (top wins, ruling M3) even when an Active marker kept the file from being applied.
/// <para>
/// The layer operations bypass every rule (ruling R2): <see cref="Caller.Internal"/> (restore, uninstall, promote) and the
/// writes a managed import makes as its user, inside <see cref="LayerOperation"/>. <see cref="Bypassed"/> tells them apart.
/// </para>
/// </summary>
public static class ManagedProperties
{
    /// <summary>The layer table (plan 2.7a, Task 6: quoted by the session's dialect; every update of a component reads it).</summary>
    private static string Layers(ISqlDialect d) => $"{d.Quote("meta")}.{d.Quote("component_layer")}";

    /// <summary>The names of the elements, as a solution file writes them.</summary>
    private const string IsCustomizableElement = "IsCustomizable", IsRenameableElement = "IsRenameable",
        CanBeDeletedElement = "CanBeDeleted", CanModifyRequirementLevelElement = "CanModifyRequirementLevelSettings",
        CanCreateAttributesElement = "CanCreateAttributes", CanCreateFormsElement = "CanCreateForms",
        CanCreateViewsElement = "CanCreateViews", CanCreateChartsElement = "CanCreateCharts";

    /// <summary>
    /// Marks the writes of the current asynchronous flow as a layer operation until the result is disposed (a managed import
    /// writing its components and its <c>solution</c> row as the importing user): <see cref="Bypassed"/> is then true for
    /// every caller. A flag of the flow only: nothing a request carries can set it.
    /// </summary>
    public static IDisposable LayerOperation() => LayerOperationScope.Enter();

    /// <summary>True when the rules of M13 and M14 do not apply: <see cref="Caller.Internal"/> or inside <see cref="LayerOperation"/>.</summary>
    public static bool Bypassed(Caller caller) => caller == Caller.Internal || LayerOperationScope.IsActive;

    /// <summary>
    /// The managed properties of the component <paramref name="id"/> of type <paramref name="type"/>, from its top Managed
    /// layer, on <paramref name="s"/>; null when it has no Managed layer (an unmanaged component, or one with only a System
    /// layer or an Active marker).
    /// </summary>
    public static async Task<ManagedFlags?> ReadAsync(DataSession s, int type, Guid id, CancellationToken ct) =>
        (await StateAsync(s, type, id, ct))?.Flags;

    /// <summary><see cref="ReadAsync"/> with the managed solutions layering the component, top first.</summary>
    public static async Task<ManagedState?> StateAsync(DataSession s, int type, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var d = s.Dialect;
        await using var cmd = s.Command($"""
            SELECT l.payload, COALESCE(sol.uniquename, CAST(l.solution_id AS {d.ColumnType(SqlColumnKind.String, 36)}))
            FROM {Layers(d)} AS l LEFT JOIN {s.DataTable("solution")} AS sol ON sol.solutionid = l.solution_id
            WHERE l.component_type = @type AND l.object_id = @object AND l.solution_id NOT IN (@active, @system)
            ORDER BY l.layer_order DESC, l.solution_id
            """);
        cmd.AddParameter("@type", DbType.Int32, ComponentType.Normalize(type));
        cmd.AddParameter("@object", DbType.Guid, id);
        cmd.AddParameter("@active", DbType.Guid, SystemSeed.ActiveSolutionId);
        cmd.AddParameter("@system", DbType.Guid, ComponentLayerStore.SystemLayerSolutionId);
        string? payload = null;
        var solutions = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (solutions.Count == 0 && !reader.IsDBNull(0)) payload = reader.GetString(0);
                solutions.Add(reader.GetString(1));
            }
        }
        return solutions.Count == 0 ? null : new ManagedState(Parse(payload, type), solutions);
    }

    /// <summary>
    /// The managed properties a layer payload gives the component of type <paramref name="type"/>, read from the direct
    /// children of the component's own element only: a table's <c>entity</c>, a column's <c>attribute</c>, an option set's
    /// <c>optionset</c>, a relationship's <c>EntityRelationship</c>, a row component's element (<c>Role</c>, <c>savedquery</c>,
    /// <c>systemform</c>, <c>WebResource</c>, ...; the first of its name in the fragment). Everything is allowed when the
    /// layer has no payload (a component the file carried without one), when the element names no property, and for a type
    /// whose element carries none (a custom API).
    /// </summary>
    /// <exception cref="InvalidOperationException">A payload that cannot be read (not JSON, no fragment, not XML): a corrupt
    /// layer is reported, never taken as "everything allowed".</exception>
    public static ManagedFlags Parse(string? payload, int type)
    {
        if (payload is null) return ManagedFlags.Open;
        XElement fragment;
        try
        {
            fragment = LayerPayload.Parse(payload, type).Fragment;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"The managed layer of a component of type {type} cannot be read: {ex.Message}", ex);
        }
        if (ElementOf(type) is not { } own) return ManagedFlags.Open;
        // Exact names: a table's "entity" sits inside an "Entity". A system dashboard (plan 3.5 UI-1f) is a form whose element is
        // a root "Dashboard".
        var element = fragment.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == own
                                                                        || (own == "systemform" && e.Name.LocalName == "Dashboard"));
        if (element is null) return ManagedFlags.Open;
        return new ManagedFlags(Flag(element, IsCustomizableElement), Flag(element, IsRenameableElement), Flag(element, CanBeDeletedElement),
            Flag(element, CanModifyRequirementLevelElement), Flag(element, CanCreateAttributesElement), Flag(element, CanCreateFormsElement),
            Flag(element, CanCreateViewsElement), Flag(element, CanCreateChartsElement));
    }

    /// <summary>The name of the component's own element in a payload fragment; null for a type with none.</summary>
    private static string? ElementOf(int type) => ComponentType.Normalize(type) switch
    {
        ComponentType.Entity => "entity",
        ComponentType.Attribute => "attribute",
        ComponentType.OptionSet => "optionset",
        ComponentType.EntityRelationship => "EntityRelationship",
        ComponentType.Role => "Role",
        ComponentType.SavedQuery => "savedquery",
        ComponentType.SavedQueryVisualization => "visualization",
        ComponentType.SystemForm => "systemform",
        ComponentType.WebResource => "WebResource",
        ComponentType.SiteMap => "AppModuleSiteMap",
        ComponentType.AppModule => "AppModule",
        ComponentType.FieldSecurityProfile => "FieldSecurityProfile",
        ComponentType.PluginAssembly => "PluginAssembly",
        ComponentType.PluginType => "PluginType",
        ComponentType.SdkMessageProcessingStep => "SdkMessageProcessingStep",
        ComponentType.SdkMessageProcessingStepImage => "SdkMessageProcessingStepImage",
        _ => null,
    };

    /// <summary>
    /// True when <paramref name="given"/> holds a label whose text differs from <paramref name="current"/>'s in its language
    /// (a new display name, ruling M13 <c>IsRenameable</c>); a label not given is no change.
    /// </summary>
    public static bool Renamed(Microsoft.Xrm.Sdk.Label? given, Microsoft.Xrm.Sdk.Label? current)
    {
        if (given is null) return false;
        var labels = (given.LocalizedLabels ?? []).Append(given.UserLocalizedLabel).OfType<Microsoft.Xrm.Sdk.LocalizedLabel>();
        return labels.Any(l => !string.Equals(l.Label ?? "",
            (current?.LocalizedLabels ?? []).FirstOrDefault(c => c.LanguageCode == l.LanguageCode)?.Label ?? "", StringComparison.Ordinal));
    }

    private static bool Flag(XElement element, string name)
    {
        var child = element.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        // A managed property may be written as an element with a Value child (<IsCustomizable><Value>0</Value>...).
        var text = (child?.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value ?? child?.Value)?.Trim();
        return text is null or "" || !(text == "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase));
    }

    // ---- refusals (400 InvalidArgument, Global Constraints) ---------------------------------------------------------

    /// <summary>The refusal of an unmanaged change to a component whose <c>IsCustomizable</c> is false (M13).</summary>
    /// <param name="what">The component, e.g. <c>attribute 'xm_item.xm_code'</c>.</param>
    /// <param name="state">Its managed state.</param>
    public static XrmdFaultException NotCustomizable(string what, ManagedState state) =>
        XrmdFaultException.InvalidArgument(
            $"The {what} cannot be customized: the managed solution '{state.Solution}' delivers it with IsCustomizable set to false.");

    /// <summary>The refusal of a new column or relationship on a table whose <c>IsCustomizable</c> is false (M13).</summary>
    /// <param name="what">The new component, e.g. <c>attribute 'xm_new'</c>.</param>
    /// <param name="table">The table's logical name.</param>
    /// <param name="state">The table's managed state.</param>
    public static XrmdFaultException TableNotCustomizable(string what, string table, ManagedState state) =>
        XrmdFaultException.InvalidArgument(
            $"The {what} cannot be added to the table '{table}': the managed solution '{state.Solution}' delivers the table with IsCustomizable set to false.");

    /// <summary>
    /// The refusal of a new component on a table whose <paramref name="flag"/> (<c>CanCreateAttributes</c>,
    /// <c>CanCreateForms</c> or <c>CanCreateViews</c>) is false (M13).
    /// </summary>
    /// <param name="what">The new component, e.g. <c>attribute 'xm_new'</c> or <c>form 'Main'</c>.</param>
    /// <param name="table">The table's logical name.</param>
    /// <param name="flag">The element name of the flag.</param>
    /// <param name="state">The table's managed state.</param>
    public static XrmdFaultException TableFlagFalse(string what, string table, string flag, ManagedState state) =>
        XrmdFaultException.InvalidArgument(
            $"The {what} cannot be added to the table '{table}': the managed solution '{state.Solution}' delivers the table with {flag} set to false.");

    /// <summary>The refusal of a new display name for a component whose <c>IsRenameable</c> is false (M13).</summary>
    public static XrmdFaultException NotRenameable(string what, ManagedState state) =>
        XrmdFaultException.InvalidArgument(
            $"The {what} cannot be renamed: the managed solution '{state.Solution}' delivers it with IsRenameable set to false.");

    /// <summary>The refusal of a new required level for a column whose <c>CanModifyRequirementLevelSettings</c> is false (M13).</summary>
    public static XrmdFaultException RequirementLevelFixed(string what, ManagedState state) =>
        XrmdFaultException.InvalidArgument(
            $"The required level of the {what} cannot be changed: the managed solution '{state.Solution}' delivers it with CanModifyRequirementLevelSettings set to false.");

    /// <summary>
    /// The refusal of the direct delete of a managed component (M13 <c>CanBeDeleted</c> false, otherwise M14): a managed
    /// component goes with the uninstall of its solutions.
    /// </summary>
    public static XrmdFaultException NotDeletable(string what, ManagedState state)
    {
        var solutions = string.Join(", ", state.Solutions.Select(n => $"'{n}'"));
        return XrmdFaultException.InvalidArgument(state.Flags.CanBeDeleted
            ? $"The {what} is managed (solution {solutions}): it cannot be deleted directly; uninstall the managed solution instead."
            : $"The {what} cannot be deleted: the managed solution '{state.Solution}' delivers it with CanBeDeleted set to false; uninstall the managed solution instead.");
    }

}

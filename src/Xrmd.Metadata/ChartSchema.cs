namespace Xrmd.Metadata;

/// <summary>
/// The seeded table <c>savedqueryvisualization</c> (plan 3.5 UI-1f, ruling F2): a system chart of a table, defined in
/// <c>fixtures/seed</c>. Names, column types and lengths follow the Learn table reference. The rows come from the
/// <c>Visualizations</c> of an entity in a solution file (component type 59); the export writes them back under the entity.
/// </summary>
/// <remarks>
/// <c>primaryentitytypecode</c> is an EntityName column on Learn. xrmverse keeps the table's logical name as text, as it does for
/// <c>savedquery.returnedtypecode</c> and <c>systemform.objecttypecode</c>: its EntityName columns hold the record's own table
/// (<c>activitytypecode</c>) or a lookup's target, not a free table name.
/// </remarks>
public static class ChartSchema
{
    /// <summary>The table of system charts (organization-owned).</summary>
    public const string Table = "savedqueryvisualization";

    /// <summary>The entity set of <see cref="Table"/>.</summary>
    public const string EntitySet = "savedqueryvisualizations";

    /// <summary>
    /// The object type code of <see cref="Table"/>. Assumed to be Dataverse's code (1111), not checked on a real environment yet:
    /// a known limit until the trial environment confirms it.
    /// </summary>
    public const int TypeCode = 1111;

    /// <summary>The solution component type of a system chart ("Chart" in the Learn <c>solutioncomponent</c> choices).</summary>
    public const int ComponentType = 59;

    /// <summary>The primary key and the primary name.</summary>
    public const string Id = "savedqueryvisualizationid", Name = "name";

    /// <summary>The other columns.</summary>
    public const string Description = "description", PrimaryEntityTypeCode = "primaryentitytypecode", DataDescription = "datadescription",
        PresentationDescription = "presentationdescription", IsDefault = "isdefault", ChartType = "charttype", Type = "type",
        WebResourceId = "webresourceid", IntroducedVersion = "introducedversion";
}

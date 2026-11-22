namespace Xrmd.Metadata;

/// <summary>
/// The seeded tables of environment variables (#167): <c>environmentvariabledefinition</c> (a named setting a solution ships, with an
/// optional default) and <c>environmentvariablevalue</c> (the value this organization gives it), defined in <c>fixtures/seed</c>.
/// Names, column types and lengths, choices and the relationship's cascades follow the Learn table references; the object type codes
/// are the solution component types of the two tables (380 and 381), assumed equal to the type codes as for <c>customapi</c>.
/// </summary>
/// <remarks>
/// Both tables are user-owned and written by clients (ruling E2): a definition's <c>schemaname</c> is unique (case-insensitive) and
/// fixed once created, and a definition has at most one value row (see <c>Xrmd.Security.EnvironmentVariableRules</c>).
/// </remarks>
public static class EnvironmentVariableSchema
{
    /// <summary>The table of definitions (user-owned).</summary>
    public const string Definition = "environmentvariabledefinition";

    /// <summary>The table of values (user-owned).</summary>
    public const string Value = "environmentvariablevalue";

    /// <summary>The object type code of <see cref="Definition"/> (also its solution component type).</summary>
    public const int DefinitionTypeCode = 380;

    /// <summary>The object type code of <see cref="Value"/>.</summary>
    public const int ValueTypeCode = 381;

    /// <summary>The relationship from <c>environmentvariablevalue.environmentvariabledefinitionid</c> to the definition (Delete Cascade).</summary>
    public const string Relationship = "environmentvariabledefinition_environmentvariablevalue";

    /// <summary>The primary name of both tables.</summary>
    public const string SchemaName = "schemaname";

    /// <summary>The other columns of <see cref="Definition"/>.</summary>
    public const string DisplayName = "displayname", Description = "description", DefaultValue = "defaultvalue", Type = "type",
        IsRequired = "isrequired", SecretStore = "secretstore", ValueSchema = "valueschema", IntroducedVersion = "introducedversion",
        Hint = "hint";

    /// <summary>The other columns of <see cref="Value"/>: the text and the lookup to its definition.</summary>
    public const string ValueColumn = "value", DefinitionLookup = "environmentvariabledefinitionid";

    /// <summary>True for <see cref="Definition"/> and <see cref="Value"/>.</summary>
    public static bool IsEnvironmentVariableTable(string? entity) => entity is Definition or Value;
}

/// <summary>The choices of <c>environmentvariabledefinition.type</c>.</summary>
public enum VariableType
{
    String = 100000000,
    Number = 100000001,
    Boolean = 100000002,
    Json = 100000003,
    DataSource = 100000004,
    Secret = 100000005,
}

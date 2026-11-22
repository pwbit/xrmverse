namespace Xrmd.Metadata;

/// <summary>
/// The seeded table <c>customcontrol</c> (plan 3.5 UI-1d): a code component (PCF control) a solution ships, defined in
/// <c>fixtures/seed</c>. Names, column types and lengths follow the Learn table reference; the object type code is the solution
/// component type "Custom Control" (66), assumed equal to the type code as for <c>customapi</c> and the environment variables.
/// </summary>
/// <remarks>
/// The rows come from solution import only (ruling R1): a request by any caller but the internal one cannot create, update or
/// delete them (<see cref="ImportOnly"/>). The files of each control (bundle, styles, resource strings, images) are kept in the
/// internal table <c>meta.customcontrol_file</c> (migration 0014), read and written through <see cref="ICustomControlFiles"/>; they
/// go with the row.
/// </remarks>
public static class CustomControlSchema
{
    /// <summary>The table of custom controls (organization-owned).</summary>
    public const string Table = "customcontrol";

    /// <summary>The object type code of <see cref="Table"/> (also its solution component type).</summary>
    public const int TypeCode = 66;

    /// <summary>The primary name: the control's full name (<c>namespace.constructor</c>).</summary>
    public const string Name = "name";

    /// <summary>The other columns.</summary>
    public const string Manifest = "manifest", Version = "version", CompatibleDataTypes = "compatibledatatypes", ClientJson = "clientjson",
        AuthoringManifest = "authoringmanifest", IntroducedVersion = "introducedversion";

    /// <summary>The refusal (400) of a write of the table by a request: the solution import writes it.</summary>
    public const string ImportOnly = "Custom controls are created by solution import in this version.";
}

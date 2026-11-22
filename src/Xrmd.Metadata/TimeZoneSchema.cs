using System.Security.Cryptography;
using System.Text;

namespace Xrmd.Metadata;

/// <summary>
/// #131: the seeded <c>timezonedefinition</c> table (ruling Z3) and the time zone and format columns of <c>usersettings</c> with
/// their defaults (ruling Z2).
/// </summary>
public static class TimeZoneSchema
{
    /// <summary>The time zone table (ObjectTypeCode 4810, OwnershipType None): one row per <see cref="TimeZoneCatalog"/> code.</summary>
    public const string Table = "timezonedefinition";

    /// <summary>The time zone code, on <see cref="Table"/> and on <c>usersettings</c>.</summary>
    public const string Code = "timezonecode";

    /// <summary>The longest <c>userinterfacename</c>.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The <c>meta.upgrade_step</c> name of the one-time backfill of <see cref="UserSettingsDefaults"/> (never renamed).</summary>
    public const string DefaultsStep = "usersettings-time-zone-defaults";

    /// <summary>
    /// The values a new <c>usersettings</c> row gets, as observed on a fresh trial environment (2026-11-15): UTC and the en-US
    /// date and time formats.
    /// </summary>
    public static IReadOnlyList<(string Column, object Value)> UserSettingsDefaults { get; } =
    [
        (Code, TimeZoneCatalog.Utc),
        ("dateformatstring", "M/d/yyyy"),
        ("timeformatstring", "h:mm tt"),
        ("dateseparator", "/"),
        ("timeseparator", ":"),
        ("amdesignator", "AM"),
        ("pmdesignator", "PM"),
    ];

    /// <summary>The stable id of the <see cref="Table"/> row of <paramref name="code"/>.</summary>
    public static Guid RowId(int code) => new(MD5.HashData(Encoding.UTF8.GetBytes("xrmd:timezonedefinition:" + code)));
}

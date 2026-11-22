using System.Text.RegularExpressions;

namespace Xrmd.Abstractions;

/// <summary>The names an organization database may have; also what keeps the name safe to quote into T-SQL. Shared by the server and the CLI.</summary>
public static class OrganizationName
{
    public static readonly Regex Pattern = new("^[A-Za-z][A-Za-z0-9_]{0,99}\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsValid(string? name) => name is not null && Pattern.IsMatch(name);
}

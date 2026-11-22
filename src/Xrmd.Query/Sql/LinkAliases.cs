using System.Globalization;

namespace Xrmd.Query.Sql;

/// <summary>
/// The result alias of each link-entity of one query, as the SQL compiler gives them: a link's own alias, or for an unaliased
/// link <c>{entity}1</c>, <c>{entity}2</c>... in depth-first order, skipping every alias the query names explicitly. The one rule
/// for the compiler and for every reader that must find a link's columns in the result (<c>NoteBodyRead</c>; final review M4):
/// ask it for the links in the compiler's order, depth-first, a link before its own links.
/// </summary>
public sealed class LinkAliases(IEnumerable<string> explicitAliases)
{
    private readonly HashSet<string> _taken = new(explicitAliases, StringComparer.Ordinal);
    private readonly Dictionary<string, int> _generated = new(StringComparer.Ordinal);

    /// <summary>The alias of the next link: <paramref name="alias"/> when given, else the next generated one for its entity.</summary>
    public string For(string entityName, string? alias) => alias ?? Generate(entityName);

    private string Generate(string entityName)
    {
        var n = _generated.GetValueOrDefault(entityName);
        string alias;
        do alias = entityName + (++n).ToString(CultureInfo.InvariantCulture);
        while (_taken.Contains(alias));
        _generated[entityName] = n;
        _taken.Add(alias);
        return alias;
    }
}

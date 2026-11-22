namespace Xrmd.Host.Idp;

/// <summary>
/// The scopes the local identity provider accepts (plan 2.8, L9): <c>openid</c>, <c>profile</c>, <c>offline_access</c> and the
/// environment's resource scopes <c>&lt;audience&gt;.default</c> and <c>&lt;audience&gt;user_impersonation</c>, with or without a
/// doubled slash before them (ServiceClient sends <c>https://host//.default</c> in some versions). Anything else is refused.
/// </summary>
public static class IdpScopes
{
    private static readonly string[] Identity = ["openid", "profile", "offline_access"];
    private static readonly string[] ResourceSuffixes = ["/.default", "//.default", "/user_impersonation", "//user_impersonation"];

    /// <summary>
    /// True when every requested scope is accepted; <paramref name="granted"/> then holds them, in order and without duplicates.
    /// False (and nothing granted) when any is not. No scope at all is accepted (a refresh request may omit them).
    /// </summary>
    public static bool TryResolve(IEnumerable<string> requested, string audience, out IReadOnlyList<string> granted)
    {
        var root = audience.TrimEnd('/');
        var accepted = new List<string>();
        foreach (var scope in requested)
        {
            if (!Identity.Contains(scope, StringComparer.Ordinal) && !IsResourceScope(scope, root))
            {
                granted = [];
                return false;
            }
            if (!accepted.Contains(scope, StringComparer.Ordinal)) accepted.Add(scope);
        }
        granted = accepted;
        return true;
    }

    // Scheme and host compare case-insensitively, as URLs do; the suffix is exact.
    private static bool IsResourceScope(string scope, string root) =>
        scope.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && ResourceSuffixes.Contains(scope[root.Length..], StringComparer.Ordinal);
}

namespace Xrmd.Solution;

/// <summary>Recognizes the publishers of Microsoft's own first-party solutions, which xrmverse does not support.</summary>
public static class MicrosoftPublisher
{
    private static readonly string[] Prefixes = ["msdyn", "msdynce", "msdynmkt", "mspp", "adx"];

    /// <summary>
    /// True when <paramref name="customizationPrefix"/> is, case-insensitively, one of <c>msdyn</c>, <c>msdynce</c>,
    /// <c>msdynmkt</c>, <c>mspp</c>, <c>adx</c>, or <paramref name="uniqueName"/> starts with <c>microsoft</c> or equals
    /// <c>dynamics365</c> (case-insensitive).
    /// </summary>
    public static bool Matches(string uniqueName, string customizationPrefix) =>
        Prefixes.Contains(customizationPrefix ?? "", StringComparer.OrdinalIgnoreCase)
        || (uniqueName ?? "").StartsWith("microsoft", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uniqueName, "dynamics365", StringComparison.OrdinalIgnoreCase);
}

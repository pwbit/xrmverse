using System.Text.Json;

namespace Xrmd.Licenses;

public enum LicenseClass
{
    Allowed,
    Review,
    Forbidden,
}

public sealed record ReviewedException(string Id, string Version, int Issue, string Reason);

public sealed record DeniedPackage(string Id, string MinVersion, string Reason);

/// <summary>
/// Corrects what a package's metadata states (for example a nuspec without <c>&lt;copyright&gt;</c>, whose authors are not a
/// copyright notice) for one exact version. <paramref name="Source"/> records where the values were verified.
/// </summary>
public sealed record PackageOverride(string Id, string Version, string? Copyright, string? ProjectUrl, string Source);

/// <summary>Findings of a license check: violations fail the check, warnings are only reported.</summary>
public sealed record CheckFindings(IReadOnlyList<string> Violations, IReadOnlyList<string> Warnings);

/// <summary>
/// The license lists of the guardrails (spec §4), the known license URLs (ruling P4) and the reviewed exceptions (ruling P3).
/// </summary>
public sealed class LicensePolicy
{
    public const string Unknown = "LicenseRef-unknown";

    private readonly IReadOnlyList<string> _allowed;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _allowedFor;
    private readonly IReadOnlyList<string> _forbidden;
    private readonly IReadOnlyList<string> _forbiddenInExpression;
    private readonly IReadOnlyDictionary<string, string> _licenseUrls;

    private LicensePolicy(
        IReadOnlyList<string> allowed,
        IReadOnlyDictionary<string, IReadOnlyList<string>> allowedFor,
        IReadOnlyList<string> forbidden,
        IReadOnlyList<string> forbiddenInExpression,
        IReadOnlyList<DeniedPackage> deniedPackages,
        IReadOnlyDictionary<string, string> licenseUrls,
        IReadOnlyList<ReviewedException> exceptions,
        IReadOnlyList<PackageOverride> overrides,
        string textsDirectory)
    {
        Overrides = overrides;
        _allowed = allowed;
        _allowedFor = allowedFor;
        _forbidden = forbidden;
        _forbiddenInExpression = forbiddenInExpression;
        DeniedPackages = deniedPackages;
        _licenseUrls = licenseUrls;
        Exceptions = exceptions;
        TextsDirectory = textsDirectory;
    }

    public IReadOnlyList<DeniedPackage> DeniedPackages { get; }

    public IReadOnlyList<ReviewedException> Exceptions { get; }

    /// <summary>The <c>packageOverrides</c> of the policy: copyright and project URL of one exact package version.</summary>
    public IReadOnlyList<PackageOverride> Overrides { get; }

    /// <summary>The override of <paramref name="id"/> (case-insensitive) at exactly <paramref name="version"/>; null when none.</summary>
    public PackageOverride? Override(string id, string version) =>
        Overrides.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(o.Version, version, StringComparison.OrdinalIgnoreCase));

    /// <summary>Directory with the standard license texts, one <c>&lt;SPDX id&gt;.txt</c> per allowed license.</summary>
    public string TextsDirectory { get; }

    /// <summary>Loads <paramref name="policyPath"/> (texts next to it under <c>texts/</c>) and the reviewed exceptions.</summary>
    public static LicensePolicy Load(string policyPath, string exceptionsPath)
    {
        using var policy = JsonDocument.Parse(InputFiles.Read(policyPath));
        using var exceptions = JsonDocument.Parse(InputFiles.Read(exceptionsPath));
        var root = policy.RootElement;

        IReadOnlyList<string> Strings(string name) =>
            root.TryGetProperty(name, out var array) ? array.EnumerateArray().Select(e => e.GetString()!).ToArray() : Array.Empty<string>();

        var denied = root.TryGetProperty("deniedPackages", out var d)
            ? d.EnumerateArray().Select(e => new DeniedPackage(
                e.GetProperty("id").GetString()!, e.GetProperty("minVersion").GetString()!, e.GetProperty("reason").GetString()!)).ToArray()
            : Array.Empty<DeniedPackage>();
        var urls = root.TryGetProperty("licenseUrls", out var u)
            ? u.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>();
        var allowedFor = root.TryGetProperty("allowedFor", out var af)
            ? af.EnumerateObject().ToDictionary(
                p => p.Name,
                p => (IReadOnlyList<string>)p.Value.EnumerateArray().Select(e => e.GetString()!).ToArray(),
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var overrides = root.TryGetProperty("packageOverrides", out var o)
            ? o.EnumerateArray().Select(e => new PackageOverride(
                e.GetProperty("id").GetString()!, e.GetProperty("version").GetString()!,
                e.TryGetProperty("copyright", out var c) ? c.GetString() : null,
                e.TryGetProperty("projectUrl", out var url) ? url.GetString() : null,
                e.GetProperty("source").GetString()!)).ToArray()
            : Array.Empty<PackageOverride>();
        var reviewed = exceptions.RootElement.GetProperty("exceptions").EnumerateArray()
            .Select(e => new ReviewedException(
                e.GetProperty("id").GetString()!, e.GetProperty("version").GetString()!,
                e.GetProperty("issue").GetInt32(), e.GetProperty("reason").GetString()!))
            .ToArray();

        var texts = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(policyPath))!, "texts");
        return new LicensePolicy(Strings("allowed"), allowedFor, Strings("forbidden"), Strings("forbiddenInExpression"), denied, urls, reviewed, overrides, texts);
    }

    /// <summary>
    /// Classifies an SPDX expression: OR takes the best branch, AND the worst, WITH an exception needs review
    /// (unless the license itself is forbidden). An invalid expression needs review. A license of <c>allowedFor</c> is
    /// allowed only for a <paramref name="packageId"/> that matches one of its patterns (OFL-1.1 for fonts, guardrails §4).
    /// </summary>
    public LicenseClass Classify(string? expression, string? packageId = null)
    {
        if (expression is not null && _forbiddenInExpression.Any(s => expression.Contains(s, StringComparison.OrdinalIgnoreCase)))
        {
            return LicenseClass.Forbidden;
        }

        var parsed = SpdxExpression.TryParse(expression);
        return parsed is null ? LicenseClass.Review : Classify(parsed, packageId);
    }

    private LicenseClass Classify(SpdxExpression expression, string? packageId) => expression switch
    {
        SpdxExpression.Or or => (LicenseClass)Math.Min((int)Classify(or.Left, packageId), (int)Classify(or.Right, packageId)),
        SpdxExpression.And and => (LicenseClass)Math.Max((int)Classify(and.Left, packageId), (int)Classify(and.Right, packageId)),
        SpdxExpression.License leaf => ClassifyLeaf(leaf, packageId),
        _ => LicenseClass.Review,
    };

    private LicenseClass ClassifyLeaf(SpdxExpression.License leaf, string? packageId)
    {
        if (_forbidden.Any(p => Matches(p, leaf.Id)))
        {
            return LicenseClass.Forbidden;
        }

        if (leaf.Exception is null && _allowed.Any(p => Matches(p, leaf.Id)))
        {
            return LicenseClass.Allowed;
        }

        if (leaf.Exception is null && packageId is not null
            && _allowedFor.TryGetValue(leaf.Id, out var packages) && packages.Any(p => Matches(p, packageId)))
        {
            return LicenseClass.Allowed;
        }

        return LicenseClass.Review;
    }

    private static bool Matches(string pattern, string id) =>
        pattern.EndsWith('*')
            ? id.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, id, StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps a nuspec <c>licenseUrl</c> to a license (ruling P4); anything not listed is <c>LicenseRef-unknown</c>.</summary>
    public string MapLicenseUrl(string url) => _licenseUrls.TryGetValue(url.Trim(), out var license) ? license : Unknown;

    /// <summary>
    /// Classifies a license file by its heading only (ruling P4): MIT, Apache-2.0, BSD-2/3-Clause; anything else is unknown.
    /// </summary>
    public static string ClassifyLicenseFile(string text)
    {
        var words = string.Join(' ', text.TrimStart('﻿').Split((char[]?)null, 12, StringSplitOptions.RemoveEmptyEntries).Take(11));
        if (words.StartsWith("MIT License", StringComparison.OrdinalIgnoreCase)
            || words.StartsWith("The MIT License", StringComparison.OrdinalIgnoreCase))
        {
            return "MIT";
        }

        if (words.StartsWith("Apache License Version 2.0", StringComparison.OrdinalIgnoreCase)
            || words.StartsWith("Apache License, Version 2.0", StringComparison.OrdinalIgnoreCase))
        {
            return "Apache-2.0";
        }

        if (words.StartsWith("BSD 2-Clause License", StringComparison.OrdinalIgnoreCase))
        {
            return "BSD-2-Clause";
        }

        if (words.StartsWith("BSD 3-Clause License", StringComparison.OrdinalIgnoreCase))
        {
            return "BSD-3-Clause";
        }

        return Unknown;
    }

    /// <summary>The standard text of an SPDX license id, or null when the repository has none for it.</summary>
    public string? StandardText(string id)
    {
        var allowed = AllowedIds.FirstOrDefault(a => !a.EndsWith('*') && string.Equals(a, id, StringComparison.OrdinalIgnoreCase));
        if (allowed is null)
        {
            return null;
        }

        var path = Path.Combine(TextsDirectory, allowed + ".txt");
        return File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : null;
    }

    /// <summary>The canonical spelling of an allowed id (<c>mit</c> → <c>MIT</c>), or the id itself.</summary>
    public string CanonicalId(string id) =>
        AllowedIds.FirstOrDefault(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase)) ?? id;

    /// <summary>The ids of <c>allowed</c> and of <c>allowedFor</c>, whose standard texts the notices reproduce.</summary>
    private IEnumerable<string> AllowedIds => _allowed.Concat(_allowedFor.Keys);

    /// <summary>
    /// Ruling P2: forbidden licenses and denied versions fail for every package; "needs review" fails only for shipped
    /// packages without a reviewed exception for that exact version, and is a warning otherwise.
    /// </summary>
    public CheckFindings Check(IReadOnlyList<Package> packages)
    {
        var violations = new List<string>();
        var warnings = new List<string>();
        foreach (var unused in Overrides.Where(o => !packages.Any(p => string.Equals(p.Id, o.Id, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(p.Version, o.Version, StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add($"packageOverrides names {unused.Id} {unused.Version}, which is not in the inventory; update or remove it in eng/licenses/policy.json");
        }

        foreach (var p in packages)
        {
            var name = $"{p.Ecosystem} package {p.Id} {p.Version}";
            var where = p.Shipped ? "shipped" : "not shipped";
            foreach (var denied in DeniedPackages.Where(d =>
                         string.Equals(d.Id, p.Id, StringComparison.OrdinalIgnoreCase) && VersionAtLeast(p.Version, d.MinVersion)))
            {
                violations.Add($"{name} ({where}) is denied from {denied.MinVersion}: {denied.Reason}");
            }

            if (p.Shipped && p.Copyright is null && p.LicenseText is null)
            {
                warnings.Add($"{name} is shipped but states no copyright and has no license file; its notice names no holder");
            }

            switch (Classify(p.License, p.Id))
            {
                case LicenseClass.Forbidden:
                    violations.Add($"{name} ({where}) has a forbidden license: {p.License}");
                    break;
                case LicenseClass.Review when !p.Shipped:
                    warnings.Add($"{name} (build only, not shipped) has a license that needs review: {p.License}");
                    break;
                case LicenseClass.Review:
                    var sameId = Exceptions.Where(e => string.Equals(e.Id, p.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var exact = sameId.FirstOrDefault(e => e.Version == p.Version);
                    if (exact is not null)
                    {
                        warnings.Add($"{name} ships under {p.License} as a reviewed exception while license review issue #{exact.Issue} is open");
                    }
                    else if (sameId.Length > 0)
                    {
                        var other = string.Join(", ", sameId.Select(e => $"{e.Version} (license review issue #{e.Issue})"));
                        violations.Add(
                            $"{name} is shipped and its license needs review: {p.License}. The reviewed exception covers only version {other}; "
                            + "re-confirm it on the issue and update eng/licenses/reviewed-exceptions.json");
                    }
                    else
                    {
                        violations.Add(
                            $"{name} is shipped and its license needs review: {p.License}. Open a license review issue; do not ship it until it is decided");
                    }

                    break;
            }
        }

        foreach (var e in Exceptions.Where(e => !packages.Any(p =>
                     p.Shipped && p.Version == e.Version && string.Equals(p.Id, e.Id, StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add($"reviewed exception {e.Id} {e.Version} (license review issue #{e.Issue}) matches no shipped package");
        }

        return new CheckFindings(violations, warnings);
    }

    /// <summary>Compares the numeric parts of two versions; a pre-release of a denied version counts as that version.</summary>
    public static bool VersionAtLeast(string version, string minimum)
    {
        static int[] Parts(string v) =>
            v.Split('-', '+')[0].Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();

        var a = Parts(version);
        var b = Parts(minimum);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x > y;
            }
        }

        return true;
    }
}

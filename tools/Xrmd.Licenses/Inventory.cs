using System.Text.Json;
using System.Xml.Linq;

namespace Xrmd.Licenses;

/// <summary>A third-party package of the build.</summary>
/// <param name="Ecosystem"><c>nuget</c>, <c>npm</c>, or <c>generic</c> (software built into the server image from its source).</param>
/// <param name="License">An SPDX expression or a <c>LicenseRef-*</c>.</param>
/// <param name="Shipped">Whether its assets reach a user (Global Constraints, "Shipped").</param>
/// <param name="Copyright">The copyright notice; several notices are separated by line feeds.</param>
/// <param name="LicenseText">
/// The package's own license file: always for a nuspec <c>license type="file"</c>, otherwise when it was read to find the copyright.
/// </param>
/// <param name="LicenseUrl">The nuspec license URL, when the license comes from it.</param>
/// <param name="NoticeText">The package's NOTICE file (Apache-2.0 section 4(d)), reproduced verbatim.</param>
/// <param name="EmbedLicenseText">
/// The license file states no copyright line, so the file itself is reproduced to keep the attribution.
/// </param>
/// <param name="Description">What a <c>generic</c> component consists of (e.g. which programs of it are shipped).</param>
public sealed record Package(
    string Ecosystem,
    string Id,
    string Version,
    string License,
    string? Copyright,
    string? ProjectUrl,
    bool Shipped,
    string? LicenseText,
    string? LicenseUrl = null,
    string? NoticeText = null,
    bool EmbedLicenseText = false,
    string? Description = null);

/// <summary>The input files of the inventory.</summary>
/// <param name="DepsFiles">Release <c>deps.json</c> files: their packages with assets are shipped.</param>
/// <param name="NugetPackages">The NuGet global packages folder, for the <c>.nuspec</c> files.</param>
/// <param name="PnpmLicenses"><c>pnpm licenses list --json --prod</c>: shipped npm packages.</param>
/// <param name="AllPackages">Optional <c>dotnet list package --include-transitive --format json</c>: every NuGet package.</param>
/// <param name="PnpmLicensesAll">Optional <c>pnpm licenses list --json</c>: every npm package, dev included.</param>
/// <param name="ImagePackages">
/// Optional <c>eng/licenses/image-packages.json</c>: the software the server image builds from source and adds to its base image
/// (plan 2.7b, ruling Q9: the PostgreSQL client tools), stated by hand because no package manager file lists it. All of it is shipped.
/// </param>
public sealed record InventoryInputs(
    IReadOnlyList<string> DepsFiles,
    string NugetPackages,
    string PnpmLicenses,
    string? AllPackages,
    string? PnpmLicensesAll,
    string? ImagePackages = null);

/// <summary>An input file is missing or malformed (exit code 2).</summary>
public sealed class InputException(string message) : Exception(message);

internal static class InputFiles
{
    public static string Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new InputException($"input file not found: {path}");
        }

        return File.ReadAllText(path);
    }

    public static JsonDocument ReadJson(string path)
    {
        try
        {
            return JsonDocument.Parse(Read(path));
        }
        catch (JsonException e)
        {
            throw new InputException($"{path} is not valid JSON: {e.Message}");
        }
    }
}

/// <summary>Builds the package inventory from deps.json, the NuGet cache and the pnpm license lists.</summary>
public static class Inventory
{
    public static IReadOnlyList<Package> Load(InventoryInputs inputs, LicensePolicy policy)
    {
        // (id, version) → shipped; ids compare case-insensitively, as NuGet does.
        var nuget = new Dictionary<(string Id, string Version), bool>(new NugetKeyComparer());
        foreach (var deps in inputs.DepsFiles)
        {
            foreach (var (id, version, shipped) in ReadDeps(deps))
            {
                nuget[(id, version)] = shipped || (nuget.TryGetValue((id, version), out var s) && s);
            }
        }

        if (inputs.AllPackages is not null)
        {
            foreach (var (id, version) in ReadDotnetList(inputs.AllPackages))
            {
                nuget.TryAdd((id, version), false);
            }
        }

        var packages = new List<Package>();
        foreach (var ((id, version), shipped) in nuget)
        {
            packages.Add(ReadNuspec(inputs.NugetPackages, id, version, shipped, policy));
        }

        var npm = new Dictionary<(string Id, string Version), Package>();
        foreach (var p in ReadPnpm(inputs.PnpmLicenses, shipped: true, policy))
        {
            npm[(p.Id, p.Version)] = p;
        }

        if (inputs.PnpmLicensesAll is not null)
        {
            foreach (var p in ReadPnpm(inputs.PnpmLicensesAll, shipped: false, policy))
            {
                npm.TryAdd((p.Id, p.Version), p);
            }
        }

        packages.AddRange(npm.Values);
        if (inputs.ImagePackages is not null)
        {
            packages.AddRange(ReadImagePackages(inputs.ImagePackages));
        }

        return Sort(packages);
    }

    /// <summary>
    /// <c>{"packages": [{"ecosystem": "generic", "id", "version", "description", "license", "copyright", "projectUrl"}]}</c>; every
    /// field is required.
    /// </summary>
    private static IEnumerable<Package> ReadImagePackages(string path)
    {
        using var doc = InputFiles.ReadJson(path);
        if (!doc.RootElement.TryGetProperty("packages", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new InputException($"{path} has no packages array");
        }

        var result = new List<Package>();
        foreach (var entry in list.EnumerateArray())
        {
            string Required(string name) =>
                entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                    ? v.GetString()!
                    : throw new InputException($"{path}: a package has no {name}");

            var ecosystem = Required("ecosystem");
            if (ecosystem != "generic")
            {
                throw new InputException($"{path}: ecosystem '{ecosystem}' is not supported (generic)");
            }

            result.Add(new Package(ecosystem, Required("id"), Required("version"), Required("license"), Required("copyright"),
                Required("projectUrl"), Shipped: true, LicenseText: null, Description: Required("description")));
        }

        return result;
    }

    /// <summary>Sorted by ecosystem, then id, then version, independent of culture.</summary>
    public static IReadOnlyList<Package> Sort(IEnumerable<Package> packages) =>
        packages
            .OrderBy(p => p.Ecosystem, StringComparer.Ordinal)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ThenBy(p => p.Version, StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<(string Id, string Version, bool Shipped)> ReadDeps(string path)
    {
        using var doc = InputFiles.ReadJson(path);
        var root = doc.RootElement;
        if (!root.TryGetProperty("targets", out var targets) || !root.TryGetProperty("libraries", out var libraries))
        {
            throw new InputException($"{path} is not a deps.json file (no targets or libraries)");
        }

        var result = new List<(string, string, bool)>();
        foreach (var library in libraries.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "package")
            {
                continue; // projects are first-party
            }

            var slash = library.Name.IndexOf('/');
            if (slash <= 0)
            {
                throw new InputException($"{path}: unexpected library key '{library.Name}'");
            }

            var shipped = false;
            foreach (var target in targets.EnumerateObject())
            {
                if (target.Value.TryGetProperty(library.Name, out var assets)
                    && (HasAssets(assets, "runtime") || HasAssets(assets, "native") || HasAssets(assets, "runtimeTargets")))
                {
                    shipped = true;
                }
            }

            result.Add((library.Name[..slash], library.Name[(slash + 1)..], shipped));
        }

        return result;
    }

    private static bool HasAssets(JsonElement library, string kind) =>
        library.TryGetProperty(kind, out var assets) && assets.ValueKind == JsonValueKind.Object && assets.EnumerateObject().Any();

    private static IEnumerable<(string Id, string Version)> ReadDotnetList(string path)
    {
        using var doc = InputFiles.ReadJson(path);
        if (!doc.RootElement.TryGetProperty("projects", out var projects))
        {
            throw new InputException($"{path} is not 'dotnet list package --format json' output (no projects)");
        }

        var result = new List<(string, string)>();
        foreach (var project in projects.EnumerateArray())
        {
            if (!project.TryGetProperty("frameworks", out var frameworks))
            {
                continue;
            }

            foreach (var framework in frameworks.EnumerateArray())
            {
                foreach (var list in new[] { "topLevelPackages", "transitivePackages" })
                {
                    if (!framework.TryGetProperty(list, out var items))
                    {
                        continue;
                    }

                    foreach (var item in items.EnumerateArray())
                    {
                        result.Add((item.GetProperty("id").GetString()!, item.GetProperty("resolvedVersion").GetString()!));
                    }
                }
            }
        }

        return result;
    }

    private static Package ReadNuspec(string cache, string id, string version, bool shipped, LicensePolicy policy)
    {
        var directory = Path.Combine(cache, id.ToLowerInvariant(), version.ToLowerInvariant());
        var nuspecPath = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.nuspec").FirstOrDefault() : null;
        if (nuspecPath is null)
        {
            throw new InputException($"no .nuspec for {id} {version} in {cache} (restore the solution first)");
        }

        XElement metadata;
        try
        {
            metadata = XDocument.Load(nuspecPath).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")
                       ?? throw new InputException($"{nuspecPath} has no <metadata>");
        }
        catch (System.Xml.XmlException e)
        {
            throw new InputException($"{nuspecPath} is not valid XML: {e.Message}");
        }

        string? Value(string name) =>
            metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        var licenseElement = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
        var licenseUrl = Value("licenseUrl");
        string license;
        string? licenseText = null;
        string? sourceUrl = null;
        switch (licenseElement?.Attribute("type")?.Value)
        {
            case "expression":
                license = licenseElement.Value.Trim();
                break;
            case "file":
                var file = Path.Combine(directory, licenseElement.Value.Trim().Replace('\\', '/'));
                if (!File.Exists(file))
                {
                    throw new InputException($"{id} {version}: license file '{licenseElement.Value}' not found in {directory}");
                }

                licenseText = File.ReadAllText(file).TrimStart('﻿').Replace("\r\n", "\n");
                license = LicensePolicy.ClassifyLicenseFile(licenseText);
                break;
            default:
                if (licenseUrl is not null)
                {
                    license = policy.MapLicenseUrl(licenseUrl);
                    sourceUrl = licenseUrl;
                }
                else
                {
                    license = LicensePolicy.Unknown;
                }

                break;
        }

        // nuspec <copyright> first; otherwise the copyright lines of the license file; otherwise <authors>.
        var files = PackageFiles.Read(directory);
        var copyright = Value("copyright");
        var embed = false;
        if (copyright is null)
        {
            copyright = licenseText is null ? files.Copyright : PackageFiles.CopyrightLines(new[] { licenseText }) ?? files.Copyright;
            licenseText ??= files.LicenseText;
            embed = copyright is null && licenseText is not null;
            copyright ??= Value("authors");
        }

        // A reviewed override (eng/licenses/policy.json, packageOverrides) corrects what the metadata states.
        var projectUrl = Value("projectUrl");
        if (policy.Override(id, version) is { } fix)
        {
            if (fix.Copyright is not null)
            {
                copyright = fix.Copyright;
                embed = false;
            }

            projectUrl = fix.ProjectUrl ?? projectUrl;
        }

        return new Package(
            "nuget", Value("id") ?? id, version, license, copyright, projectUrl, shipped, licenseText, sourceUrl,
            files.NoticeText, embed);
    }

    private static IEnumerable<Package> ReadPnpm(string path, bool shipped, LicensePolicy policy)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        using var doc = InputFiles.ReadJson(path);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InputException($"{path} is not 'pnpm licenses list --json' output");
        }

        var result = new List<Package>();
        foreach (var group in doc.RootElement.EnumerateObject())
        {
            foreach (var entry in group.Value.EnumerateArray())
            {
                var license = String(entry, "license") ?? group.Name;
                if (SpdxExpression.TryParse(license) is null || string.Equals(license, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    license = LicensePolicy.Unknown;
                }

                // npm "author" first; otherwise the copyright lines of the license file (relative paths are
                // resolved against the JSON file's folder). Machine paths are only read, never written out.
                var versions = entry.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToArray();
                var paths = entry.TryGetProperty("paths", out var p)
                    ? p.EnumerateArray().Select(e => Path.Combine(baseDirectory, e.GetString()!)).ToArray()
                    : Array.Empty<string>();
                for (var i = 0; i < versions.Length; i++)
                {
                    var version = versions[i];
                    var files = PackageFiles.Read(paths.Length == versions.Length ? paths[i] : paths.FirstOrDefault());
                    var copyright = String(entry, "author") ?? files.Copyright;
                    var embed = copyright is null && files.LicenseText is not null;
                    var name = entry.GetProperty("name").GetString()!;
                    var projectUrl = String(entry, "homepage");
                    if (policy.Override(name, version) is { } fix)
                    {
                        if (fix.Copyright is not null)
                        {
                            copyright = fix.Copyright;
                            embed = false;
                        }

                        projectUrl = fix.ProjectUrl ?? projectUrl;
                    }

                    result.Add(new Package(
                        "npm", name, version, license,
                        copyright, projectUrl, shipped, files.LicenseText, null, files.NoticeText, embed));
                }
            }
        }

        return result;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s.Trim()
            : null;

    private sealed class NugetKeyComparer : IEqualityComparer<(string Id, string Version)>
    {
        public bool Equals((string Id, string Version) x, (string Id, string Version) y) =>
            string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Version, y.Version, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Id, string Version) obj) =>
            HashCode.Combine(obj.Id.ToUpperInvariant(), obj.Version.ToUpperInvariant());
    }
}

/// <summary>License, COPYING and NOTICE files at the top of a package folder.</summary>
internal sealed record PackageFiles(string? LicenseText, string? Copyright, string? NoticeText)
{
    private static readonly System.Text.RegularExpressions.Regex LicenseName =
        new(@"^(LICEN[CS]E|COPYING)([.\-_].*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex NoticeName =
        new(@"^NOTICE(\.txt|\.md)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A copyright line: "Copyright …", "(c) …" or "© …", without template placeholders or license prose.</summary>
    private static readonly System.Text.RegularExpressions.Regex CopyrightLine = new(
        @"^(Copyright\b(?!\s+(notice|notices|holder|holders|owner|owners|license|licen[cs]e|and|law|laws|statement|protection|to|of|in)\b)|\(c\)\s*(\d|©|\(c\))|©)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex Placeholder = new(
        @"\[yyyy\]|\[year\]|<year>|\{yyyy\}|\[name of copyright owner\]|<copyright holders>|YEAR by AUTHOR|\{name of copyright owner\}",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly PackageFiles None = new(null, null, null);

    public static PackageFiles Read(string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return None;
        }

        var names = Directory.GetFiles(directory).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var licenses = names.Where(n => LicenseName.IsMatch(n)).Select(n => ReadText(Path.Combine(directory, n))).ToArray();
        var notice = names.Where(n => NoticeName.IsMatch(n)).Select(n => ReadText(Path.Combine(directory, n))).FirstOrDefault();
        var copyright = CopyrightLines(licenses);
        return new PackageFiles(licenses.FirstOrDefault(), copyright, string.IsNullOrWhiteSpace(notice) ? null : notice);
    }

    /// <summary>Every distinct copyright line of the texts, in order, separated by line feeds; null when there is none.</summary>
    public static string? CopyrightLines(IEnumerable<string> texts)
    {
        var lines = texts
            .SelectMany(t => t.Split('\n'))
            .Select(l => l.Trim())
            .Where(l => CopyrightLine.IsMatch(l) && !Placeholder.IsMatch(l))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return lines.Length == 0 ? null : string.Join('\n', lines);
    }

    private static string ReadText(string path) => File.ReadAllText(path).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
}

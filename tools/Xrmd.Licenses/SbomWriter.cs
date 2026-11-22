using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xrmd.Licenses;

/// <summary>Writes a CycloneDX 1.5 JSON SBOM of the shipped packages.</summary>
public static class SbomWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="name">The name of the xrmverse component the SBOM describes (no license: spec §11).</param>
    public static string Write(IReadOnlyList<Package> packages, string name, string version, DateTimeOffset timestamp, Guid serial)
    {
        var components = new JsonArray();
        foreach (var p in Inventory.Sort(packages.Where(p => p.Shipped)))
        {
            var purl = Purl(p);
            var component = new JsonObject
            {
                ["type"] = "library",
                ["bom-ref"] = purl,
                ["name"] = p.Id,
                ["version"] = p.Version,
                ["purl"] = purl,
                ["licenses"] = new JsonArray(License(p.License)),
            };
            if (p.Copyright is not null)
            {
                component["copyright"] = p.Copyright;
            }

            if (p.ProjectUrl is not null)
            {
                component["externalReferences"] = new JsonArray(new JsonObject { ["type"] = "website", ["url"] = p.ProjectUrl });
            }

            components.Add(component);
        }

        var bom = new JsonObject
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = "1.5",
            ["serialNumber"] = "urn:uuid:" + serial.ToString("D"),
            ["version"] = 1,
            ["metadata"] = new JsonObject
            {
                ["timestamp"] = timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["tools"] = new JsonObject
                {
                    ["components"] = new JsonArray(new JsonObject { ["type"] = "application", ["name"] = "Xrmd.Licenses" }),
                },
                ["component"] = new JsonObject
                {
                    ["type"] = "application",
                    ["bom-ref"] = name + "@" + version,
                    ["name"] = name,
                    ["version"] = version,
                },
            },
            ["components"] = components,
        };

        return bom.ToJsonString(Options).Replace("\r\n", "\n") + "\n";
    }

    /// <summary>A single SPDX id → <c>license.id</c>; a LicenseRef → <c>license.name</c>; a compound expression → <c>expression</c>.</summary>
    private static JsonObject License(string license)
    {
        var parsed = SpdxExpression.TryParse(license);
        if (parsed is SpdxExpression.License { Exception: null } leaf)
        {
            return leaf.Id.StartsWith("LicenseRef-", StringComparison.Ordinal) || leaf.Id.Contains(':')
                ? new JsonObject { ["license"] = new JsonObject { ["name"] = leaf.Id } }
                : new JsonObject { ["license"] = new JsonObject { ["id"] = leaf.Id } };
        }

        return parsed is null
            ? new JsonObject { ["license"] = new JsonObject { ["name"] = license } }
            : new JsonObject { ["expression"] = license };
    }

    /// <summary>
    /// Package URL (purl spec): <c>pkg:nuget/Id@version</c>, <c>pkg:npm/%40scope/name@version</c>, <c>pkg:generic/name@version</c>.
    /// </summary>
    public static string Purl(Package p)
    {
        var name = p.Id;
        string? ns = null;
        if (p.Ecosystem == "npm" && name.StartsWith('@') && name.Contains('/'))
        {
            ns = name[..name.IndexOf('/')];
            name = name[(name.IndexOf('/') + 1)..];
        }

        var sb = new StringBuilder("pkg:").Append(p.Ecosystem).Append('/');
        if (ns is not null)
        {
            sb.Append(Encode(ns)).Append('/');
        }

        return sb.Append(Encode(name)).Append('@').Append(Encode(p.Version)).ToString();
    }

    private static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '~')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }
}

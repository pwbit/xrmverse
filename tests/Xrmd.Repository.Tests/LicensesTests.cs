using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xrmd.Licenses;

namespace Xrmd.Repository.Tests;

/// <summary>
/// Plan 10, Task 1: inventory, third-party notices, license check and SBOM (tools/Xrmd.Licenses).
/// The data under Data/ is a fake NuGet cache, two deps.json files and pnpm license lists.
/// </summary>
public sealed class LicensesTests : IDisposable
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "Data");
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PolicyPath = Path.Combine(RepoRoot, "eng", "licenses", "policy.json");

    private readonly string _temp = Directory.CreateTempSubdirectory("xrmd-licenses-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Shipped_packages_are_those_with_assets_in_deps_json()
    {
        var packages = LoadInventory();

        Find(packages, "nuget", "System.Runtime", "4.3.0").Shipped.Should().BeFalse("a facade without assets ships nothing");
        Find(packages, "nuget", "Acme.Native", "1.0.0").Shipped.Should().BeTrue("native assets reach the user");
        Find(packages, "nuget", "Acme.Proprietary", "1.0.0").Shipped.Should().BeTrue("runtimeTargets assets reach the user");
        Find(packages, "nuget", "Newtonsoft.Json", "13.0.1").Shipped.Should().BeTrue();
        Find(packages, "nuget", "FluentAssertions", "6.12.2").Shipped.Should().BeFalse("only test projects reference it");
        Find(packages, "npm", "@fluentui/react-components", "9.75.1").Shipped.Should().BeTrue();
        Find(packages, "npm", "vite", "7.1.0").Shipped.Should().BeFalse("dev dependency");
        packages.Should().NotContain(p => p.Id == "xrmd" || p.Id == "Xrmd.Host", "projects are first-party");
        packages.Where(p => p.Id == "Newtonsoft.Json").Should().ContainSingle("the package is in both deps files once");
    }

    [Theory]
    [InlineData("MIT", LicenseClass.Allowed)]
    [InlineData("mit", LicenseClass.Allowed)]
    [InlineData("(Apache-2.0)", LicenseClass.Allowed)]
    [InlineData("MIT OR GPL-3.0-only", LicenseClass.Allowed)]
    [InlineData("MIT AND GPL-3.0-only", LicenseClass.Forbidden)]
    [InlineData("Apache-2.0 WITH LLVM-exception", LicenseClass.Review)]
    [InlineData("(MIT OR Apache-2.0) AND BSD-3-Clause", LicenseClass.Allowed)]
    [InlineData("MIT AND MPL-2.0", LicenseClass.Review)]
    [InlineData("PostgreSQL", LicenseClass.Allowed)]
    [InlineData("postgresql", LicenseClass.Allowed)]
    [InlineData("MPL-2.0", LicenseClass.Review)]
    [InlineData("LGPL-2.1-only", LicenseClass.Review)]
    [InlineData("EPL-2.0", LicenseClass.Review)]
    [InlineData("GPL-2.0-or-later", LicenseClass.Forbidden)]
    [InlineData("AGPL-3.0-only", LicenseClass.Forbidden)]
    [InlineData("SSPL-1.0", LicenseClass.Forbidden)]
    [InlineData("BUSL-1.1", LicenseClass.Forbidden)]
    [InlineData("MIT OR Apache-2.0 WITH Commons-Clause", LicenseClass.Forbidden)]
    [InlineData("LicenseRef-MS-NET-Library", LicenseClass.Review)]
    [InlineData("LicenseRef-unknown", LicenseClass.Review)]
    [InlineData("SEE LICENSE IN LICENSE.txt", LicenseClass.Review)]
    [InlineData("MIT OR", LicenseClass.Review)]
    [InlineData("", LicenseClass.Review)]
    public void Spdx_expressions_are_classified(string expression, LicenseClass expected)
    {
        Policy().Classify(expression).Should().Be(expected);
    }

    [Theory]
    [InlineData("OFL-1.1", "@fontsource-variable/sora", LicenseClass.Allowed)]
    [InlineData("OFL-1.1", "@fontsource/inter", LicenseClass.Allowed)]
    [InlineData("OFL-1.1", "some-icon-font", LicenseClass.Review)]
    [InlineData("OFL-1.1", null, LicenseClass.Review)]
    [InlineData("MIT OR OFL-1.1", "left-pad", LicenseClass.Allowed)]
    [InlineData("OFL-1.1 WITH LLVM-exception", "@fontsource/inter", LicenseClass.Review)]
    public void Ofl_is_allowed_for_font_packages_only(string expression, string? packageId, LicenseClass expected)
    {
        // #154: the user allowed OFL-1.1 for fonts only (guardrails section 4); any other package needs a legal review.
        Policy().Classify(expression, packageId).Should().Be(expected);
    }

    [Fact]
    public void A_shipped_package_that_is_not_a_font_fails_under_ofl()
    {
        Package Ofl(string id) => new("npm", id, "1.0.0", "OFL-1.1", "Copyright 2026 Someone", null, true, null, null);

        var findings = Policy().Check(new[] { Ofl("@fontsource-variable/sora"), Ofl("some-icon-font") });

        findings.Violations.Should().ContainSingle().Which.Should().Contain("some-icon-font").And.Contain("needs review");
        Policy().StandardText("OFL-1.1").Should().Contain("SIL OPEN FONT LICENSE", "the notices still reproduce the OFL text");
    }

    [Fact]
    public void The_postgresql_license_is_allowed_with_its_text()
    {
        // Plan 2.7a, ruling P13 (#279, decided by the user): Npgsql's PostgreSQL License joins the allow list of guardrails §4.
        Package Npgsql() => new("nuget", "Npgsql", "10.0.3", "PostgreSQL", "Copyright 2025 © The Npgsql Development Team", null, true, null, null);

        Policy().Check(new[] { Npgsql() }).Violations.Should().BeEmpty();
        Policy().StandardText("PostgreSQL").Should().Contain("Permission to use, copy, modify, and distribute this software");
    }

    [Fact]
    public void Known_license_urls_are_mapped()
    {
        var policy = Policy();
        policy.MapLicenseUrl("https://github.com/dotnet/corefx/blob/master/LICENSE.TXT").Should().Be("MIT");
        policy.MapLicenseUrl("http://go.microsoft.com/fwlink/?LinkId=329770").Should().Be("LicenseRef-MS-NET-Library");
        policy.MapLicenseUrl("https://go.microsoft.com/fwlink/?linkid=2108407").Should().Be("LicenseRef-unknown");

        var packages = LoadInventory();
        Find(packages, "nuget", "Microsoft.VisualBasic", "10.3.0").License.Should().Be("MIT");
        Find(packages, "nuget", "System.Runtime", "4.3.0").License.Should().Be("LicenseRef-MS-NET-Library");
        Find(packages, "nuget", "Acme.FileLicensed", "1.0.0").License.Should().Be("MIT", "the license file starts with the MIT heading");
        var proprietary = Find(packages, "nuget", "Acme.Proprietary", "1.0.0");
        proprietary.License.Should().Be("LicenseRef-unknown");
        proprietary.LicenseText.Should().StartWith("ACME SOFTWARE LICENSE TERMS");
        var webUrl = Find(packages, "nuget", "Acme.WebUrl", "1.2.27");
        webUrl.License.Should().Be("LicenseRef-unknown");
        webUrl.LicenseUrl.Should().Be("https://example.com/acme/eula");
    }

    [Fact]
    public void Unknown_license_of_a_shipped_package_fails_unless_excepted()
    {
        var notices = Path.Combine(_temp, "NOTICES.md");
        Run(Args("notices", Deps()).Concat(new[] { "--out", notices })).Code.Should().Be(0);
        var ok = Run(Args("check", Deps()).Concat(new[] { "--notices", notices }));
        ok.Code.Should().Be(0, ok.Output);

        var bumped = Bump(Path.Combine(DataDir, "deps", "Xrmd.Host.deps.json"), "Acme.WebUrl/1.2.27", "Acme.WebUrl/1.2.28");
        Run(Args("notices", Deps(cli: null, host: bumped)).Concat(new[] { "--out", notices })).Code.Should().Be(0);
        var failed = Run(Args("check", Deps(cli: null, host: bumped)).Concat(new[] { "--notices", notices }));

        failed.Code.Should().Be(1);
        failed.Output.Should().Contain("Acme.WebUrl 1.2.28").And.Contain("LicenseRef-unknown")
            .And.Contain("1.2.27").And.Contain("#102");
    }

    [Fact]
    public void Denied_test_package_fails_the_check()
    {
        var notices = Path.Combine(_temp, "NOTICES.md");
        var allPackages = Path.Combine(DataDir, "dotnet-packages-denied.json");
        Run(Args("notices", Deps(), allPackages).Concat(new[] { "--out", notices })).Code.Should().Be(0);

        var result = Run(Args("check", Deps(), allPackages).Concat(new[] { "--notices", notices }));

        result.Code.Should().Be(1);
        result.Output.Should().Contain("FluentAssertions 8.0.0").And.Contain("commercial");
        result.Output.Should().Contain("Acme.GplHelper 1.0.0").And.Contain("GPL-3.0-only");
    }

    [Fact]
    public void Check_fails_when_notices_are_out_of_date()
    {
        var notices = Path.Combine(_temp, "NOTICES.md");
        Run(Args("notices", Deps()).Concat(new[] { "--out", notices })).Code.Should().Be(0);

        var bumped = Bump(Path.Combine(DataDir, "deps", "xrmd.deps.json"), "Newtonsoft.Json/13.0.1", "Newtonsoft.Json/13.0.3");
        var bumpedHost = Bump(Path.Combine(DataDir, "deps", "Xrmd.Host.deps.json"), "Newtonsoft.Json/13.0.1", "Newtonsoft.Json/13.0.3");
        var result = Run(Args("check", Deps(bumped, bumpedHost)).Concat(new[] { "--notices", notices }));

        result.Code.Should().Be(1);
        result.Output.Should().MatchRegex("Newtonsoft.Json.*13\\.0\\.1.*13\\.0\\.3");
    }

    [Fact]
    public void Notices_are_deterministic_and_contain_each_shipped_package_once()
    {
        var first = Path.Combine(_temp, "first.md");
        var second = Path.Combine(_temp, "second.md");
        Run(Args("notices", Deps()).Concat(new[] { "--out", first })).Code.Should().Be(0);
        Run(Args("notices", Deps()).Concat(new[] { "--out", second })).Code.Should().Be(0);

        var text = File.ReadAllText(first);
        text.Should().Be(File.ReadAllText(second));
        text.Should().Contain("third-party software included in the xrmverse server image, the `xrmd` CLI and the web admin");
        text.Should().NotMatchRegex(@"\b20\d\d-\d\d-\d\d\b", "the file has no dates");
        text.Should().NotContain("\r");

        foreach (var heading in new[]
                 {
                     "### Newtonsoft.Json 13.0.1", "### Acme.Native 1.0.0", "### Acme.Proprietary 1.0.0", "### Acme.WebUrl 1.2.27",
                     "### Microsoft.VisualBasic 10.3.0", "### Acme.FileLicensed 1.0.0", "### @fluentui/react-components 9.75.1",
                     "### @swc/helpers 0.5.24", "### tslib 2.8.1", "### react 18.3.1",
                 })
        {
            Regex.Matches(text, "^" + Regex.Escape(heading) + "$", RegexOptions.Multiline).Should().ContainSingle(heading);
        }

        text.Should().NotContain("System.Runtime").And.NotContain("FluentAssertions").And.NotContain("vite").And.NotContain("caniuse-lite");
        text.Should().Contain("Copyright © James Newton-King 2008");
        text.Should().Contain("Acme Native Team", "authors stand in for a missing copyright");
        text.Should().Contain("Microsoft Corp.", "the npm author stands in for the copyright");
        Regex.Matches(text, "Permission is hereby granted, free of charge").Should().HaveCount(1, "the MIT text appears once");
        text.Should().Contain("TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION", "Apache-2.0 text");
        text.Should().Contain("ACME PROPRIETARY RUNTIME", "the excepted package's license file is included verbatim");
        text.Should().Contain("https://example.com/acme/eula", "a package without a license file gets its URL");
        var npm = text.IndexOf("### @fluentui/react-components", StringComparison.Ordinal);
        var nuget = text.IndexOf("### Acme.FileLicensed", StringComparison.Ordinal);
        npm.Should().BeLessThan(nuget, "sorted by ecosystem, then id");
        text.IndexOf("### Acme.FileLicensed", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("### Newtonsoft.Json", StringComparison.Ordinal));

        var check = Run(Args("check", Deps()).Concat(new[] { "--notices", first }));
        check.Code.Should().Be(0, check.Output);
        check.Output.Should().Contain("System.Runtime 4.3.0").And.Contain("LicenseRef-MS-NET-Library").And.Contain("caniuse-lite");
    }

    [Fact]
    public void Sbom_is_cyclonedx_1_5_with_purls_and_licenses()
    {
        var server = Path.Combine(_temp, "server.cdx.json");
        var cli = Path.Combine(_temp, "cli.cdx.json");
        Run(Args("sbom", Deps()).Concat(new[] { "--scope", "server", "--version", "1.2.3", "--out", server })).Code.Should().Be(0);
        Run(Args("sbom", Deps()).Concat(new[] { "--scope", "cli", "--version", "1.2.3", "--out", cli })).Code.Should().Be(0);

        using var serverDoc = JsonDocument.Parse(File.ReadAllText(server));
        var root = serverDoc.RootElement;
        root.GetProperty("bomFormat").GetString().Should().Be("CycloneDX");
        root.GetProperty("specVersion").GetString().Should().Be("1.5");
        root.GetProperty("serialNumber").GetString().Should().MatchRegex("^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        root.GetProperty("metadata").GetProperty("component").GetProperty("name").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("metadata").GetProperty("component").GetProperty("version").GetString().Should().Be("1.2.3");
        var serverComponents = Components(root);
        serverComponents.Keys.Should().Contain("pkg:nuget/Newtonsoft.Json@13.0.1")
            .And.Contain("pkg:npm/%40fluentui/react-components@9.75.1")
            .And.Contain("pkg:nuget/Acme.WebUrl@1.2.27")
            .And.NotContain(k => k.Contains("Acme.Native"), "Acme.Native only ships with the CLI")
            .And.NotContain(k => k.Contains("System.Runtime") || k.Contains("vite"), "not shipped");
        License(serverComponents["pkg:nuget/Newtonsoft.Json@13.0.1"]).GetProperty("license").GetProperty("id").GetString().Should().Be("MIT");
        License(serverComponents["pkg:nuget/Acme.WebUrl@1.2.27"]).GetProperty("license").GetProperty("name").GetString().Should().Be("LicenseRef-unknown");
        License(serverComponents["pkg:nuget/Acme.WebUrl@1.2.27"]).GetProperty("license").TryGetProperty("id", out _).Should().BeFalse();

        using var cliDoc = JsonDocument.Parse(File.ReadAllText(cli));
        var cliComponents = Components(cliDoc.RootElement);
        cliComponents.Keys.Should().Contain("pkg:nuget/Newtonsoft.Json@13.0.1").And.NotContain(k => k.StartsWith("pkg:npm/"));
        License(cliComponents["pkg:nuget/Acme.Native@1.0.0"]).GetProperty("expression").GetString().Should().Be("Apache-2.0 OR MIT");
        cliDoc.RootElement.GetProperty("metadata").GetProperty("component").GetProperty("name").GetString()
            .Should().NotBe(root.GetProperty("metadata").GetProperty("component").GetProperty("name").GetString());
    }

    [Fact]
    public void Usage_errors_exit_with_2()
    {
        Run(new[] { "frobnicate" }).Code.Should().Be(2);
        Run(Args("sbom", Deps()).Concat(new[] { "--scope", "desktop", "--version", "1", "--out", Path.Combine(_temp, "x.json") })).Code.Should().Be(2);
        Run(Args("check", Deps()).Concat(new[] { "--notices", Path.Combine(_temp, "missing.md") })).Code.Should().Be(2);
    }

    [Fact]
    public void Npm_copyright_falls_back_to_the_license_file()
    {
        var packages = LoadInventory();
        Find(packages, "npm", "@emotion/hash", "0.9.2").Copyright.Should().Be("Copyright (c) Emotion team and other contributors");
        Find(packages, "npm", "tslib", "2.8.1").Copyright.Should().Be("Microsoft Corp.", "the npm author comes first");
        Find(packages, "npm", "nocopy-a", "1.0.0").EmbedLicenseText.Should().BeTrue();
        Find(packages, "npm", "react", "18.3.1").Copyright.Should().BeNull("there is no author and no license file");
        Find(packages, "nuget", "Acme.Native", "1.0.0").Copyright.Should().Be("Acme Native Team", "nuspec authors are the last fallback");

        var notices = Path.Combine(_temp, "NOTICES.md");
        Run(Args("notices", Deps()).Concat(new[] { "--out", notices })).Code.Should().Be(0);
        var text = File.ReadAllText(notices);
        text.Should().Contain("### @emotion/hash 0.9.2\n\n- License: MIT\n- Copyright: Copyright (c) Emotion team and other contributors\n");
        text.Should().NotContain("/repo/").And.NotContain(DataDir, "no machine paths");

        // Two packages whose license files state no copyright line share one reproduced file.
        Regex.Matches(text, "Permission to use, copy, modify, and/or distribute this software for any purpose is hereby granted\\.")
            .Should().HaveCount(1);
        text.Should().Contain("### License file 1\n\nUsed by: nocopy-a 1.0.0, nocopy-b 1.0.0.");
        Regex.Matches(text, "reproduced below as \"License file 1\"").Should().HaveCount(2);

        var check = Run(Args("check", Deps()).Concat(new[] { "--notices", notices }));
        check.Code.Should().Be(0, check.Output);
        check.Output.Should().Contain("react 18.3.1 is shipped but states no copyright and has no license file");
        check.Output.Should().NotContain("@emotion/hash 0.9.2 is shipped but states no copyright");
    }

    [Fact]
    public void Package_override_sets_copyright_and_project_url_of_that_version_only()
    {
        var policyPath = Path.Combine(_temp, "policy.json");
        var policy = File.ReadAllText(PolicyPath).TrimEnd().TrimEnd('}').TrimEnd() + """
            ,
              "packageOverrides": [
                { "id": "acme.native", "version": "1.0.0", "copyright": "Copyright (c) Acme Corporation",
                  "projectUrl": "https://example.com/acme-native-source", "source": "LICENSE of the upstream repository" },
                { "id": "Newtonsoft.Json", "version": "12.0.0", "copyright": "Copyright (c) Nobody", "source": "an older version" }
              ]
            }
            """;
        File.WriteAllText(policyPath, policy);
        var loaded = LicensePolicy.Load(policyPath, Path.Combine(DataDir, "reviewed-exceptions.json"));

        var packages = LoadInventory(loaded);

        var native = Find(packages, "nuget", "Acme.Native", "1.0.0");
        native.Copyright.Should().Be("Copyright (c) Acme Corporation", "the override replaces the nuspec authors fallback");
        native.ProjectUrl.Should().Be("https://example.com/acme-native-source");
        Find(packages, "nuget", "Newtonsoft.Json", "13.0.1").Copyright.Should().NotBe("Copyright (c) Nobody", "the override names another version");
        loaded.Check(packages).Warnings.Should().Contain(w => w.Contains("Newtonsoft.Json 12.0.0") && w.Contains("packageOverrides"))
            .And.NotContain(w => w.Contains("Acme.Native", StringComparison.OrdinalIgnoreCase) && w.Contains("packageOverrides"));
    }

    [Fact]
    public void Package_override_also_applies_to_npm_packages()
    {
        var policyPath = Path.Combine(_temp, "policy-npm.json");
        var policy = File.ReadAllText(PolicyPath).TrimEnd().TrimEnd('}').TrimEnd() + """
            ,
              "packageOverrides": [
                { "id": "tslib", "version": "2.8.1", "copyright": "Copyright (c) The tslib Authors",
                  "projectUrl": "https://example.com/tslib", "source": "test" }
              ]
            }
            """;
        File.WriteAllText(policyPath, policy);
        var loaded = LicensePolicy.Load(policyPath, Path.Combine(DataDir, "reviewed-exceptions.json"));

        var packages = LoadInventory(loaded);

        var tslib = Find(packages, "npm", "tslib", "2.8.1");
        tslib.Copyright.Should().Be("Copyright (c) The tslib Authors", "the override beats the npm author");
        tslib.ProjectUrl.Should().Be("https://example.com/tslib");
        loaded.Check(packages).Warnings.Should().NotContain(w => w.Contains("packageOverrides"));
    }

    [Fact]
    public void Repository_policy_overrides_the_sora_copyright()
    {
        var sora = Policy().Override("@fontsource-variable/sora", "5.3.0");

        sora.Should().NotBeNull("the npm author of the fontsource packager is Google Inc., but OFL clause 2 needs the font's notice");
        sora!.Copyright.Should().Be("Copyright 2019 The Sora Project Authors (https://github.com/sora-xor/sora-font)");
    }

    [Fact]
    public void Repository_policy_overrides_the_corewf_copyright()
    {
        var corewf = Policy().Override("UiPath.Workflow.Runtime", "6.0.3");

        corewf.Should().NotBeNull("the UiPath.Workflow.Runtime nuspec has no <copyright> and no license file; its authors are 'System.Activities'");
        corewf!.Copyright.Should().Be("Copyright (c) .NET Foundation and Contributors", "the LICENSE of github.com/UiPath/CoreWF");
        corewf.ProjectUrl.Should().Be("https://github.com/UiPath/CoreWF");
        corewf.Source.Should().Contain("github.com/UiPath/CoreWF");
    }

    [Fact]
    public void Notice_file_of_an_apache_package_is_reproduced()
    {
        Find(LoadInventory(), "nuget", "Acme.Native", "1.0.0").NoticeText.Should().StartWith("Acme Native\nCopyright 2024 The Acme Native Authors");

        var notices = Path.Combine(_temp, "NOTICES.md");
        Run(Args("notices", Deps()).Concat(new[] { "--out", notices })).Code.Should().Be(0);
        var text = File.ReadAllText(notices);
        var section = text[text.IndexOf("### Acme.Native 1.0.0", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n### ", 5, StringComparison.Ordinal)];
        section.Should().Contain("The NOTICE file of the package:")
            .And.Contain("Copyright 2024 The Acme Native Authors\n\nThis product includes software developed at Acme (https://example.com/).");
    }

    /// <summary>
    /// Plan 2.7b, ruling Q9 (Task 9 fix round 1): the PostgreSQL client tools the server image builds from source are listed from
    /// eng/licenses/image-packages.json as one generic component, in the notices and in the server's SBOM only, under the
    /// PostgreSQL License.
    /// </summary>
    [Fact]
    public void Image_packages_are_shipped_with_the_server_only()
    {
        var imagePackages = Path.Combine(RepoRoot, "eng", "licenses", "image-packages.json");
        string[] image = ["--image-packages", imagePackages];

        var notices = Path.Combine(_temp, "NOTICES.md");
        Run(Args("notices", Deps()).Concat(image).Concat(new[] { "--out", notices })).Code.Should().Be(0);
        var text = File.ReadAllText(notices);
        var section = text[text.IndexOf("## Software built into the server image", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n## ", 5, StringComparison.Ordinal)];
        section.Should().Contain($"### postgresql {PinnedPostgresVersion()}\n\n- Included: PostgreSQL client tools pg_dump and pg_restore, and the libpq library\n- License: PostgreSQL")
            .And.Contain("PostgreSQL Global Development Group")
            .And.Contain("Philip Warner");
        text.Should().Contain("\n### PostgreSQL\n");

        var check = Run(Args("check", Deps()).Concat(image).Concat(new[] { "--notices", notices }));
        check.Output.Should().NotContain("generic package", "an allowed license with a copyright gives no finding");

        var server = Path.Combine(_temp, "server.cdx.json");
        var cli = Path.Combine(_temp, "cli.cdx.json");
        Run(Args("sbom", Deps()).Concat(image).Concat(new[] { "--scope", "server", "--version", "1", "--out", server })).Code.Should().Be(0);
        Run(Args("sbom", Deps()).Concat(image).Concat(new[] { "--scope", "cli", "--version", "1", "--out", cli })).Code.Should().Be(0);
        using var serverDoc = JsonDocument.Parse(File.ReadAllText(server));
        var client = Components(serverDoc.RootElement)[$"pkg:generic/postgresql@{PinnedPostgresVersion()}"];
        License(client).GetProperty("license").GetProperty("id").GetString().Should().Be("PostgreSQL");
        using var cliDoc = JsonDocument.Parse(File.ReadAllText(cli));
        Components(cliDoc.RootElement).Keys.Should().NotContain(k => k.StartsWith("pkg:generic/"), "the CLI does not ship the image");
    }

    [Theory]
    [InlineData("""{ "packages": [ { "ecosystem": "generic", "id": "x", "version": "1", "description": "d", "copyright": "c", "projectUrl": "u" } ] }""")]
    [InlineData("""{ "packages": [ { "ecosystem": "generic", "id": "x", "version": "1", "license": "MIT", "copyright": "c", "projectUrl": "u" } ] }""")]
    [InlineData("""{ "packages": [ { "ecosystem": "deb", "id": "x", "version": "1", "description": "d", "license": "MIT", "copyright": "c", "projectUrl": "u" } ] }""")]
    [InlineData("""{ "items": [] }""")]
    public void Malformed_image_packages_exit_2(string json)
    {
        var file = Path.Combine(_temp, "image.json");
        File.WriteAllText(file, json);
        Run(Args("notices", Deps()).Concat(new[] { "--image-packages", file, "--out", Path.Combine(_temp, "n.md") })).Code.Should().Be(2);
    }

    /// <summary>The version the notices state is the one the Dockerfile builds (PG_VERSION), so the two cannot drift apart.</summary>
    [Fact]
    public void Image_packages_state_the_postgresql_version_the_dockerfile_builds()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot, "docker", "Dockerfile"));
        var pinned = Regex.Match(dockerfile, @"^ARG PG_VERSION=(\S+)$", RegexOptions.Multiline);
        pinned.Success.Should().BeTrue("the Dockerfile pins the PostgreSQL source version");
        Regex.IsMatch(dockerfile, @"^ARG PG_SHA256=[0-9a-f]{64}$", RegexOptions.Multiline).Should().BeTrue("and its SHA-256");
        PinnedPostgresVersion().Should().Be(pinned.Groups[1].Value);
    }

    private static string PinnedPostgresVersion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "eng", "licenses", "image-packages.json")));
        return doc.RootElement.GetProperty("packages").EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == "postgresql").GetProperty("version").GetString()!;
    }

    [Fact]
    public void Malformed_input_exits_2()
    {
        var noVersions = Path.Combine(_temp, "pnpm.json");
        File.WriteAllText(noVersions, """{ "MIT": [ { "name": "x", "license": "MIT" } ] }""");
        var librariesArray = Path.Combine(_temp, "xrmd.deps.json");
        File.WriteAllText(librariesArray, """{ "targets": {}, "libraries": [] }""");
        var notJson = Path.Combine(_temp, "dotnet.json");
        File.WriteAllText(notJson, "not json");
        var output = Path.Combine(_temp, "out.md");

        Run(Args("notices", Deps()).Concat(new[] { "--out", output }).Select(a => a.EndsWith("pnpm-licenses-prod.json") ? noVersions : a))
            .Code.Should().Be(2);
        var malformed = Run(Args("notices", new[] { librariesArray }).Concat(new[] { "--out", output }));
        malformed.Code.Should().Be(2);
        malformed.Output.Should().StartWith("error:");
        Run(Args("notices", Deps(), notJson).Concat(new[] { "--out", output })).Code.Should().Be(2);
        Run(Args("notices", Deps()).Concat(new[] { "--out", Path.Combine(_temp, "no-such-dir", "out.md") })).Code.Should().Be(2);
    }

    private static Dictionary<string, JsonElement> Components(JsonElement root) =>
        root.GetProperty("components").EnumerateArray().ToDictionary(c => c.GetProperty("purl").GetString()!, c => c.Clone());

    private static JsonElement License(JsonElement component) =>
        component.GetProperty("licenses").EnumerateArray().Should().ContainSingle().Subject;

    private static LicensePolicy Policy() =>
        LicensePolicy.Load(PolicyPath, Path.Combine(DataDir, "reviewed-exceptions.json"));

    private static IReadOnlyList<Package> LoadInventory(LicensePolicy? policy = null) =>
        Inventory.Load(
            new InventoryInputs(
                Deps(),
                Path.Combine(DataDir, "nuget"),
                Path.Combine(DataDir, "pnpm-licenses-prod.json"),
                Path.Combine(DataDir, "dotnet-packages.json"),
                Path.Combine(DataDir, "pnpm-licenses-all.json")),
            policy ?? Policy());

    private static Package Find(IReadOnlyList<Package> packages, string ecosystem, string id, string version) =>
        packages.Should().ContainSingle(p => p.Ecosystem == ecosystem && p.Id == id && p.Version == version).Subject;

    private static string[] Deps(string? cli = null, string? host = null) => new[]
    {
        cli ?? Path.Combine(DataDir, "deps", "xrmd.deps.json"),
        host ?? Path.Combine(DataDir, "deps", "Xrmd.Host.deps.json"),
    };

    private static IEnumerable<string> Args(string command, string[] deps, string? allPackages = null)
    {
        yield return command;
        yield return "--deps";
        foreach (var d in deps) yield return d;
        yield return "--nuget-packages";
        yield return Path.Combine(DataDir, "nuget");
        yield return "--pnpm-licenses";
        yield return Path.Combine(DataDir, "pnpm-licenses-prod.json");
        yield return "--all-packages";
        yield return allPackages ?? Path.Combine(DataDir, "dotnet-packages.json");
        yield return "--pnpm-licenses-all";
        yield return Path.Combine(DataDir, "pnpm-licenses-all.json");
        yield return "--policy";
        yield return PolicyPath;
        yield return "--exceptions";
        yield return Path.Combine(DataDir, "reviewed-exceptions.json");
    }

    /// <summary>Copies a deps.json into a fresh directory (same file name, so the scope is kept) with one library renamed.</summary>
    private string Bump(string depsFile, string from, string to)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_temp, Guid.NewGuid().ToString("N"))).FullName;
        var target = Path.Combine(dir, Path.GetFileName(depsFile));
        var (fromId, fromVersion) = (from.Split('/')[0], from.Split('/')[1]);
        var toVersion = to.Split('/')[1];
        var text = File.ReadAllText(depsFile)
            .Replace(from, to)
            .Replace($"{fromId.ToLowerInvariant()}/{fromVersion}", $"{fromId.ToLowerInvariant()}/{toVersion}")
            .Replace($"\"{fromId}\": \"{fromVersion}\"", $"\"{fromId}\": \"{toVersion}\"");
        File.WriteAllText(target, text);
        return target;
    }

    private static (int Code, string Output) Run(IEnumerable<string> args)
    {
        var output = new StringWriter();
        var code = Program.Run(args.ToArray(), output, output);
        return (code, output.ToString());
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "xrmverse.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("xrmverse.sln not found above " + AppContext.BaseDirectory);
    }
}

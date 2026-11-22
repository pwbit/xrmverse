namespace Xrmd.Licenses;

/// <summary>
/// <c>Xrmd.Licenses</c>: third-party notices, license check and SBOM (Plan 10, Task 1), vulnerability gate (Task 3). Reads only local files: it never
/// calls the network (ruling P1). Exit codes: 0 ok, 1 violations found, 2 usage or input error.
/// </summary>
public static class Program
{
    private const string Usage = """
        usage: Xrmd.Licenses <command> --deps <deps.json>... --nuget-packages <dir> --pnpm-licenses <json>
                             [--all-packages <dotnet-list.json>] [--pnpm-licenses-all <json>] [--image-packages <json>]
                             [--policy <policy.json>] [--exceptions <reviewed-exceptions.json>]
          notices --out <THIRD_PARTY_NOTICES.md>
          check   --notices <THIRD_PARTY_NOTICES.md>      exit 1 on violations or stale notices
          sbom    --scope cli|server --version <v> --out <file.cdx.json>
        usage: Xrmd.Licenses vulnerabilities --dotnet <dotnet-list-vulnerable.json> --pnpm-audit <pnpm-audit.json>
                                             exit 1 on a High or Critical advisory
        --policy and --exceptions default to eng/licenses/ of the repository that contains the current directory.
        """;

    private static readonly IReadOnlyDictionary<string, (string DepsFile, bool Npm, string Name)> Scopes =
        new Dictionary<string, (string, bool, string)>(StringComparer.Ordinal)
        {
            ["cli"] = ("xrmd.deps.json", false, "xrmd"),
            ["server"] = ("Xrmd.Host.deps.json", true, "xrmverse-server"),
        };

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            if (args.Length == 0 || args[0] is not ("notices" or "check" or "sbom" or "vulnerabilities"))
            {
                throw new UsageException(args.Length == 0 ? "missing command" : $"unknown command '{args[0]}'");
            }

            var options = ParseOptions(args.Skip(1).ToArray());
            return args[0] switch
            {
                "notices" => Notices(options, stdout),
                "check" => Check(options, stdout, stderr),
                "vulnerabilities" => Vulnerabilities(options, stdout, stderr),
                _ => Sbom(options, stdout),
            };
        }
        catch (UsageException e)
        {
            stderr.WriteLine($"error: {e.Message}");
            stderr.WriteLine(Usage);
            return 2;
        }
        catch (InputException e)
        {
            stderr.WriteLine($"error: {e.Message}");
            return 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                      or KeyNotFoundException or InvalidOperationException or FormatException
                                      or ArgumentException or NullReferenceException or IndexOutOfRangeException)
        {
            // A file that is valid JSON/XML but not in the expected shape, or that cannot be read or written.
            stderr.WriteLine($"error: malformed or unreadable input ({e.GetType().Name}): {e.Message}");
            return 2;
        }
    }

    private static int Notices(Dictionary<string, List<string>> options, TextWriter stdout)
    {
        var (policy, inputs) = Load(options);
        var output = Single(options, "--out");
        var packages = Inventory.Load(inputs, policy);
        File.WriteAllText(output, NoticesWriter.Write(packages, policy));
        stdout.WriteLine($"wrote {output}: {packages.Count(p => p.Shipped)} shipped packages");
        return 0;
    }

    private static int Check(Dictionary<string, List<string>> options, TextWriter stdout, TextWriter stderr)
    {
        var (policy, inputs) = Load(options);
        var noticesPath = Single(options, "--notices");
        var committed = InputFiles.Read(noticesPath);
        var packages = Inventory.Load(inputs, policy);
        var findings = policy.Check(packages);
        var stale = NoticesWriter.Compare(committed, NoticesWriter.Write(packages, policy), Path.GetFileName(noticesPath));

        foreach (var warning in findings.Warnings)
        {
            stdout.WriteLine($"warning: {warning}");
        }

        var violations = findings.Violations.Concat(stale).ToArray();
        foreach (var violation in violations)
        {
            stderr.WriteLine($"error: {violation}");
        }

        stdout.WriteLine(
            $"{packages.Count} packages ({packages.Count(p => p.Shipped)} shipped): {violations.Length} violations, {findings.Warnings.Count} warnings");
        return violations.Length == 0 ? 0 : 1;
    }

    private static int Vulnerabilities(Dictionary<string, List<string>> options, TextWriter stdout, TextWriter stderr)
    {
        var dotnet = Single(options, "--dotnet");
        var pnpm = Single(options, "--pnpm-audit");
        var advisories = VulnerabilityGate.Load(dotnet, pnpm);
        foreach (var advisory in advisories)
        {
            if (advisory.Blocking)
            {
                stderr.WriteLine($"error: {advisory}");
            }
            else
            {
                stdout.WriteLine($"warning: {advisory}");
            }
        }

        var blocking = advisories.Count(a => a.Blocking);
        stdout.WriteLine($"{advisories.Count} advisories: {blocking} high or critical, {advisories.Count - blocking} moderate or low");
        return blocking == 0 ? 0 : 1;
    }

    private static int Sbom(Dictionary<string, List<string>> options, TextWriter stdout)
    {
        var scopeName = Single(options, "--scope");
        if (!Scopes.TryGetValue(scopeName, out var scope))
        {
            throw new UsageException($"unknown scope '{scopeName}' (cli or server)");
        }

        var version = Single(options, "--version");
        var output = Single(options, "--out");
        var (policy, inputs) = Load(options);
        var deps = inputs.DepsFiles.Where(d => string.Equals(Path.GetFileName(d), scope.DepsFile, StringComparison.Ordinal)).ToArray();
        if (deps.Length == 0)
        {
            throw new UsageException($"--scope {scopeName} needs a {scope.DepsFile} among --deps");
        }

        // Only what this artifact ships: its deps.json, plus the web admin's prod packages and what the image builds in for the server.
        var scoped = inputs with { DepsFiles = deps, AllPackages = null, PnpmLicensesAll = null };
        var packages = Inventory.Load(scoped, policy).Where(p => scope.Npm || p.Ecosystem is not ("npm" or "generic")).ToArray();
        File.WriteAllText(output, SbomWriter.Write(packages, scope.Name, version, DateTimeOffset.UtcNow, Guid.NewGuid()));
        stdout.WriteLine($"wrote {output}: {packages.Count(p => p.Shipped)} components");
        return 0;
    }

    private static (LicensePolicy Policy, InventoryInputs Inputs) Load(Dictionary<string, List<string>> options)
    {
        var policyPath = Optional(options, "--policy") ?? DefaultPolicyFile("policy.json");
        var exceptionsPath = Optional(options, "--exceptions") ?? DefaultPolicyFile("reviewed-exceptions.json");
        if (!options.TryGetValue("--deps", out var deps) || deps.Count == 0)
        {
            throw new UsageException("missing --deps");
        }

        var nugetPackages = Single(options, "--nuget-packages");
        if (!Directory.Exists(nugetPackages))
        {
            throw new InputException($"NuGet packages folder not found: {nugetPackages}");
        }

        var inputs = new InventoryInputs(
            deps,
            nugetPackages,
            Single(options, "--pnpm-licenses"),
            Optional(options, "--all-packages"),
            Optional(options, "--pnpm-licenses-all"),
            Optional(options, "--image-packages"));
        return (LicensePolicy.Load(policyPath, exceptionsPath), inputs);
    }

    private static string DefaultPolicyFile(string name)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "eng", "licenses", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InputException($"eng/licenses/{name} not found above {Directory.GetCurrentDirectory()}; pass it explicitly");
    }

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "--deps", "--nuget-packages", "--pnpm-licenses", "--all-packages", "--pnpm-licenses-all", "--image-packages", "--policy", "--exceptions",
        "--out", "--notices", "--scope", "--version", "--dotnet", "--pnpm-audit",
    };

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (!Known.Contains(arg))
                {
                    throw new UsageException($"unknown option '{arg}'");
                }

                if (options.ContainsKey(arg))
                {
                    throw new UsageException($"option '{arg}' given twice");
                }

                options[arg] = current = new List<string>();
            }
            else if (current is null)
            {
                throw new UsageException($"unexpected argument '{arg}'");
            }
            else
            {
                current.Add(arg);
            }
        }

        foreach (var (name, values) in options)
        {
            if (values.Count == 0 || (name != "--deps" && values.Count > 1))
            {
                throw new UsageException($"option '{name}' needs {(name == "--deps" ? "at least one value" : "exactly one value")}");
            }
        }

        return options;
    }

    private static string Single(Dictionary<string, List<string>> options, string name) =>
        Optional(options, name) ?? throw new UsageException($"missing {name}");

    private static string? Optional(Dictionary<string, List<string>> options, string name) =>
        options.TryGetValue(name, out var values) ? values[0] : null;

    private sealed class UsageException(string message) : Exception(message);
}

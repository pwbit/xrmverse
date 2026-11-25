using System.CommandLine;
using System.Reflection;
using System.Text;
using Xrmd.Cli.Config;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd connector export</c>: writes the custom connector package (OpenAPI definition, API properties, icon) of one organization,
/// from the files embedded in the CLI (<c>connectors/xrmverse</c>), with the host, the resource URI and the Entra client id filled in.
/// It reads the profile only; there is no server call.
/// </summary>
public static class ConnectorCommands
{
    private static readonly string[] TextFiles = ["apiDefinition.swagger.json", "apiProperties.json"];
    private const string IconFile = "icon.png";

    public static Command Build(CliContext ctx) => new("connector", "Export the Power Platform custom connector package of the organization.")
    {
        Export(ctx),
    };

    private static Command Export(CliContext ctx)
    {
        var outDir = new Option<string>("--out") { Description = "Folder to write the connector package to.", Required = true };
        var clientId = new Option<string>("--client-id") { Description = "Application (client) ID of the Entra app registration the connector signs in with.", Required = true };
        var resource = new Option<string?>("--resource") { Description = "Application ID URI of the server's Entra app registration; default: the profile URL." };
        var force = new Option<bool>("--force") { Description = "Overwrite files that already exist in --out." };
        var command = new Command("export", "Write apiDefinition.swagger.json, apiProperties.json and icon.png for the organization.") { outDir, clientId, resource, force };
        command.SetAction((parse, _) =>
        {
            var (_, profile) = ctx.RequireProfile();
            if (profile.IsLocalIdp)
                throw new CliException(ExitCodes.Usage, "Local identity provider organizations are not supported by the connector yet.");
            if (profile.Mode == AuthModes.Dev)
                throw new CliException(ExitCodes.Usage, "A profile in dev mode cannot be exported: the connector signs in through Entra ID.");
            if (!Uri.TryCreate(profile.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new CliException(ExitCodes.Usage, "The connector needs an https server URL.");

            var host = uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";
            var resourceUri = parse.GetValue(resource) is { Length: > 0 } r ? r : profile.Url.TrimEnd('/');
            var replacements = new Dictionary<string, string>
            {
                ["{{HOST}}"] = host,
                ["{{RESOURCE_URI}}"] = resourceUri,
                ["{{CLIENT_ID}}"] = parse.GetValue(clientId)!,
            };

            var dir = Path.GetFullPath(parse.GetValue(outDir)!);
            string[] names = [.. TextFiles, IconFile];
            if (!parse.GetValue(force))
            {
                var existing = names.Where(n => File.Exists(Path.Combine(dir, n))).ToArray();
                if (existing.Length > 0)
                    throw new CliException(ExitCodes.Usage, $"{string.Join(", ", existing)} already exist in {dir}: use --force to overwrite.");
            }

            Directory.CreateDirectory(dir);
            foreach (var name in TextFiles)
            {
                var text = Encoding.UTF8.GetString(Resource(name));
                foreach (var (placeholder, value) in replacements)
                    text = text.Replace(placeholder, JsonEscape(value), StringComparison.Ordinal);
                File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
            }
            File.WriteAllBytes(Path.Combine(dir, IconFile), Resource(IconFile));

            ctx.WriteResult(new { directory = dir, host, resource = resourceUri, files = names }, $"Wrote the connector package for {host} to {dir}.");
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    /// <summary>The value as it goes inside a JSON string literal (the files are JSON).</summary>
    private static string JsonEscape(string value)
    {
        var quoted = System.Text.Json.JsonSerializer.Serialize(value);
        return quoted[1..^1];
    }

    private static byte[] Resource(string file)
    {
        using var stream = typeof(ConnectorCommands).Assembly.GetManifestResourceStream("connector/" + file)
            ?? throw new CliException(ExitCodes.Unexpected, $"The connector file {file} is not embedded in this build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

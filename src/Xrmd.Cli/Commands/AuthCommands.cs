using System.CommandLine;
using Xrmd.Cli.Auth;
using Xrmd.Cli.Config;

namespace Xrmd.Cli.Commands;

/// <summary><c>xrmd auth login|status|logout</c>: profiles in <c>config.json</c> and their tokens.</summary>
public static class AuthCommands
{
    public const string DefaultProfile = "default";

    public static Command Build(CliContext ctx) => new("auth", "Sign in to an xrmverse server and manage profiles.")
    {
        Login(ctx),
        Status(ctx),
        Logout(ctx),
    };

    private static Command Login(CliContext ctx)
    {
        var mode = new Option<string>("--mode")
        {
            Description = "dev | device-code | browser | client-secret.",
            Required = true,
        };
        mode.AcceptOnlyFromAmong(AuthModes.All);
        var clientId = new Option<string?>("--client-id") { Description = "Entra application (client) id (device-code, browser, client-secret)." };
        var tenantId = new Option<string?>("--tenant-id") { Description = "Entra tenant id (required for client-secret; default organizations)." };
        var objectId = new Option<string?>("--object-id") { Description = $"dev: oid claim (default {DevTokenProvider.DefaultObjectId})." };
        var idp = new Option<string>("--idp")
        {
            Description = "entra | local: the identity provider of device-code, browser and client-secret. local = the server's own " +
                          "(Auth:Mode=Local; authority <url>adfs/, client id default xrmd-cli).",
            DefaultValueFactory = _ => IdentityProviders.Entra,
        };
        idp.AcceptOnlyFromAmong(IdentityProviders.All);
        var caFile = new Option<string?>("--ca-file")
        {
            Description = "PEM root certificate trusted for the server's host only (a local identity provider behind Caddy's internal CA: " +
                          "<dir>/certs/caddy-root.crt after `xrmd server up`). Kept by a later login of the same profile.",
        };
        var signingKeyEnv = new Option<string?>("--signing-key-env") { Description = $"dev: environment variable holding the signing key (default {DevTokenProvider.DefaultSigningKeyEnv})." };

        var command = new Command("login", "Sign in and save the profile (--url required; --profile default 'default').")
        {
            mode, idp, caFile, clientId, tenantId, objectId, signingKeyEnv,
        };
        command.SetAction(async (parse, ct) =>
        {
            var url = ctx.Url ?? throw new CliException(ExitCodes.Usage, "auth login requires --url.");
            var name = ctx.ProfileName ?? DefaultProfile;
            var selected = parse.GetValue(mode)!;
            var isDev = selected == AuthModes.Dev;
            var isLocal = parse.GetValue(idp) == IdentityProviders.Local;
            if (isLocal && isDev) throw new CliException(ExitCodes.Usage, "--idp local does not apply to --mode dev.");
            if (isLocal && parse.GetValue(tenantId) is not null)
                throw new CliException(ExitCodes.Usage, "--tenant-id applies only to --idp entra: the local identity provider has no tenant.");

            // Options of another mode are refused, never ignored.
            Refuse(isDev, parse.GetValue(clientId), "--client-id", "is not used by --mode dev");
            Refuse(isDev, parse.GetValue(tenantId), "--tenant-id", "is not used by --mode dev");
            Refuse(!isDev, parse.GetValue(objectId), "--object-id", "applies only to --mode dev");
            Refuse(!isDev, parse.GetValue(signingKeyEnv), "--signing-key-env", "applies only to --mode dev");
            if (!isDev && string.IsNullOrWhiteSpace(parse.GetValue(clientId)) && !(isLocal && selected != AuthModes.ClientSecret))
                throw new CliException(ExitCodes.Usage, $"--mode {selected} requires --client-id.");
            if (selected == AuthModes.ClientSecret && !isLocal && string.IsNullOrWhiteSpace(parse.GetValue(tenantId)))
                throw new CliException(ExitCodes.Usage, "--mode client-secret requires --tenant-id.");
            if (parse.GetValue(objectId) is { } oid && !Guid.TryParse(oid, out _))
                throw new CliException(ExitCodes.Usage, $"--object-id must be a GUID: '{oid}'.");

            var config0 = ctx.Profiles.Load();
            var given = parse.GetValue(caFile);
            if (given is not null && (isDev || !isLocal))
                throw new CliException(ExitCodes.Usage, "--ca-file applies only to --idp local.");
            if (given is not null && !File.Exists(given))
                throw new CliException(ExitCodes.Usage, $"--ca-file {given} does not exist.");
            // A login of an existing profile of the same host keeps its CA file (H7), as `server up` recorded it.
            var keptCa = isLocal && config0.Profiles.TryGetValue(name, out var previous) && previous.CaFile is not null
                && Uri.TryCreate(previous.Url, UriKind.Absolute, out var oldUri) && Uri.TryCreate(url, UriKind.Absolute, out var newUri)
                && string.Equals(oldUri.IdnHost, newUri.IdnHost, StringComparison.OrdinalIgnoreCase) ? previous.CaFile : null;
            var profile = new Profile
            {
                CaFile = given is not null ? Path.GetFullPath(given) : keptCa,
                Url = url,
                Mode = selected,
                Idp = isLocal ? IdentityProviders.Local : null,
                ClientId = parse.GetValue(clientId),
                TenantId = parse.GetValue(tenantId),
                ObjectId = isDev ? parse.GetValue(objectId) ?? DevTokenProvider.DefaultObjectId : null,
                SigningKeyEnv = isDev ? parse.GetValue(signingKeyEnv) ?? DevTokenProvider.DefaultSigningKeyEnv : null,
            };
            profile = await TokenProviders.Create(profile, ctx.Env).LoginAsync(ct);

            var config = ctx.Profiles.Load();
            config.Profiles[name] = profile;
            config.CurrentProfile = name;
            ctx.Profiles.Save(config);

            ctx.WriteResult(new { profile = name, url = profile.Url, mode = profile.Mode, idp = profile.Idp ?? IdentityProviders.Entra },
                $"Logged in: profile '{name}' ({profile.Mode}) for {profile.Url}");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Status(CliContext ctx)
    {
        var command = new Command("status", "Show the current profile and the server's view of the caller (WhoAmI).");
        command.SetAction(async (_, ct) =>
        {
            var (name, profile) = ctx.RequireProfile();
            var who = await ctx.CreateClient().GetJsonAsync("WhoAmI", ct);
            ctx.Write(new
            {
                profile = name,
                url = profile.Url,
                mode = profile.Mode,
                userId = Text(who, "UserId"),
                businessUnitId = Text(who, "BusinessUnitId"),
                organizationId = Text(who, "OrganizationId"),
            });
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Logout(CliContext ctx)
    {
        var command = new Command("logout", "Remove the profile (--profile, default the current one) and its cached tokens.");
        command.SetAction(async (_, ct) =>
        {
            var config = ctx.Profiles.Load();
            var name = ctx.ProfileName ?? config.CurrentProfile ?? throw new CliException(ExitCodes.Auth, CliContext.NoProfileMessage);
            if (!config.Profiles.TryGetValue(name, out var profile))
                throw new CliException(ExitCodes.Auth, $"Profile '{name}' not found.");

            await TokenProviders.Create(profile, ctx.Env).LogoutAsync(ct);
            config.Profiles.Remove(name);
            if (config.CurrentProfile == name) config.CurrentProfile = null;
            ctx.Profiles.Save(config);

            ctx.WriteResult(new { profile = name, removed = true }, $"Logged out: profile '{name}' removed.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static void Refuse(bool refused, string? value, string option, string reason)
    {
        if (refused && value is not null) throw new CliException(ExitCodes.Usage, $"{option} {reason}.");
    }

    private static string? Text(System.Text.Json.JsonElement json, string property) =>
        json.ValueKind == System.Text.Json.JsonValueKind.Object && json.TryGetProperty(property, out var v) ? v.ToString() : null;
}

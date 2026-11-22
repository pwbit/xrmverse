using System.CommandLine;
using System.Text.Json;
using Xrmd.Cli.Http;

namespace Xrmd.Cli.Commands;

/// <summary>
/// <c>xrmd idp user|client</c>: the local identity provider of a server with <c>Auth:Mode=Local</c> (plan 2.8), through
/// <c>/admin/idp</c> (System Administrator). Users are existing <c>systemuser</c>s; their local credential is managed here.
/// </summary>
public static class IdpCommands
{
    public const string SecretWarning = "Store this secret now: it is not shown again.";

    public static Command Build(CliContext ctx) => new("idp", "Manage the local identity provider (servers with Auth:Mode=Local): user credentials and OAuth clients.")
    {
        User(ctx),
        Client(ctx),
    };

    private static Argument<string> UserArgument() => new("user")
    {
        Description = "The user: systemuserid (or Entra object id) as a GUID, or the domain name.",
    };

    /// <summary>The systemuserid of a user given as a GUID (systemuserid or object id) or a domain name; exit 4 when there is none.</summary>
    private static async Task<Guid> ResolveAsync(XrmdClient client, string user, CancellationToken ct)
    {
        var filter = Guid.TryParse(user, out var id)
            ? $"systemuserid eq {id} or azureactivedirectoryobjectid eq {id}"
            : $"domainname eq {UserRoleLookup.Literal(user)}";
        var rows = await client.GetAllPagesAsync($"systemusers?$select=systemuserid&$filter={filter}", ct);
        if (rows.Count == 0) throw new CliException(ExitCodes.NotFound, $"User '{user}' not found.");
        if (rows.Count > 1) throw new CliException(ExitCodes.Rejected, $"{rows.Count} users match '{user}'.");
        return Guid.Parse(UserRoleLookup.Str(rows[0], "systemuserid")!);
    }

    private static Command User(CliContext ctx) => new("user", "Local credentials of users.")
    {
        SetPassword(ctx),
        Totp(ctx),
        RemoveUser(ctx),
    };

    private static Command SetPassword(CliContext ctx)
    {
        var user = UserArgument();
        var stdin = new Option<bool>("--password-stdin") { Description = "Read the new password (one line) from standard input. Required: a password is never a command-line argument." };
        var command = new Command("set-password", "Set the user's local password (at least 12 characters); creates the credential, clears a lockout.") { user, stdin };
        command.SetAction(async (parse, ct) =>
        {
            if (!parse.GetValue(stdin))
                throw new CliException(ExitCodes.Usage, "set-password reads the password from standard input: pass --password-stdin.");
            var password = (await ctx.Env.In.ReadLineAsync(ct))?.TrimEnd('\r');
            if (string.IsNullOrEmpty(password)) throw new CliException(ExitCodes.Usage, "No password on standard input.");
            var client = ctx.CreateClient();
            var id = await ResolveAsync(client, parse.GetValue(user)!, ct);
            await client.AdminAsync(HttpMethod.Put, $"idp/users/{id}/password", XrmdClient.JsonContent(new { password }), ct);
            ctx.WriteResult(new { systemuserid = id, passwordSet = true }, $"Password set for user {id}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Totp(CliContext ctx)
    {
        var enableUser = UserArgument();
        var enable = new Command("enable", "Give the user a TOTP key (the user needs a local credential) and print its otpauth:// URI once; sign-in then needs a code.") { enableUser };
        enable.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var id = await ResolveAsync(client, parse.GetValue(enableUser)!, ct);
            var response = await client.AdminAsync(HttpMethod.Post, $"idp/users/{id}/totp", cancellationToken: ct);
            var uri = response.Json is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("otpauthUri", out var u) ? u.GetString() : null;
            if (uri is null) throw new CliException(ExitCodes.Unexpected, "The server returned no otpauthUri.");
            ctx.WriteResult(new { systemuserid = id, otpauthUri = uri },
                $"Add this to the user's authenticator app; it is not shown again:{Environment.NewLine}{uri}");
            return ExitCodes.Ok;
        });
        var disableUser = UserArgument();
        var disable = new Command("disable", "Remove the user's TOTP key.") { disableUser };
        disable.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var id = await ResolveAsync(client, parse.GetValue(disableUser)!, ct);
            await client.AdminAsync(HttpMethod.Delete, $"idp/users/{id}/totp", cancellationToken: ct);
            ctx.WriteResult(new { systemuserid = id, totp = false }, $"TOTP disabled for user {id}.");
            return ExitCodes.Ok;
        });
        return new Command("totp", "Second factor of a user.") { enable, disable };
    }

    private static Command RemoveUser(CliContext ctx)
    {
        var user = UserArgument();
        var command = new Command("remove", "Remove the user's local credential: the user can no longer sign in locally (the user itself stays).") { user };
        command.SetAction(async (parse, ct) =>
        {
            var client = ctx.CreateClient();
            var id = await ResolveAsync(client, parse.GetValue(user)!, ct);
            await client.AdminAsync(HttpMethod.Delete, $"idp/users/{id}", cancellationToken: ct);
            ctx.WriteResult(new { systemuserid = id, removed = true }, $"Removed the local credential of user {id}.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Client(CliContext ctx) => new("client", "OAuth clients of the local identity provider.")
    {
        ClientAdd(ctx),
        ClientList(ctx),
        ClientRemove(ctx),
    };

    private static Command ClientAdd(CliContext ctx)
    {
        var name = new Option<string>("--name") { Description = "Display name of the client.", Required = true };
        var appUser = new Option<string?>("--app-user") { Description = "Confidential client (client credentials): the user its tokens are issued for (GUID or domain name)." };
        var isPublic = new Option<bool>("--public") { Description = "Public client (authorization code with PKCE); needs --redirect-uri." };
        var redirect = new Option<string[]>("--redirect-uri") { Description = "Redirect URI of a public client, repeatable." };
        var command = new Command("add", "Create a client. A confidential client's secret is printed once.") { name, appUser, isPublic, redirect };
        command.SetAction(async (parse, ct) =>
        {
            var user = parse.GetValue(appUser);
            var publicClient = parse.GetValue(isPublic);
            var uris = parse.GetValue(redirect) ?? [];
            if (publicClient == (user is not null))
                throw new CliException(ExitCodes.Usage, "Give exactly one of --app-user (confidential client) or --public.");
            if (publicClient && uris.Length == 0) throw new CliException(ExitCodes.Usage, "--public requires --redirect-uri.");
            if (!publicClient && uris.Length > 0) throw new CliException(ExitCodes.Usage, "--redirect-uri applies only to --public.");

            var client = ctx.CreateClient();
            object body = publicClient
                ? new { displayName = parse.GetValue(name), type = "public", redirectUris = uris }
                : new { displayName = parse.GetValue(name), type = "confidential", systemuserid = await ResolveAsync(client, user!, ct) };
            var response = await client.AdminAsync(HttpMethod.Post, "idp/clients", XrmdClient.JsonContent(body), ct);
            var json = response.Json ?? throw new CliException(ExitCodes.Unexpected, "The server returned no client.");
            var clientId = UserRoleLookup.Str(json, "clientId");
            var secret = UserRoleLookup.Str(json, "clientSecret");
            ctx.WriteResult(secret is null ? new { clientId } : new { clientId, clientSecret = secret },
                secret is null
                    ? $"Created client {clientId}."
                    : $"Created client {clientId}.{Environment.NewLine}Client secret: {secret}{Environment.NewLine}{SecretWarning}");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command ClientList(CliContext ctx)
    {
        var command = new Command("list", "List the clients (the secrets are never shown).");
        command.SetAction(async (_, ct) =>
        {
            var response = await ctx.CreateClient().AdminAsync(HttpMethod.Get, "idp/clients", cancellationToken: ct);
            var rows = response.Json is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray().ToList() : [];
            ctx.Write(rows.Select(r => new
            {
                clientId = UserRoleLookup.Str(r, "clientId"),
                displayName = UserRoleLookup.Str(r, "displayName"),
                type = UserRoleLookup.Str(r, "type"),
                systemuserid = UserRoleLookup.Str(r, "systemuserid"),
                redirectUris = r.TryGetProperty("redirectUris", out var uris) && uris.ValueKind == JsonValueKind.Array
                    ? string.Join(' ', uris.EnumerateArray().Select(u => u.GetString())) : "",
            }).ToList());
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command ClientRemove(CliContext ctx)
    {
        var id = new Argument<string>("client-id") { Description = "The client id." };
        var command = new Command("remove", "Delete a client (the built-in xrmd-cli and xrmd-web cannot be deleted).") { id };
        command.SetAction(async (parse, ct) =>
        {
            var clientId = parse.GetValue(id)!;
            await ctx.CreateClient().AdminAsync(HttpMethod.Delete, $"idp/clients/{Uri.EscapeDataString(clientId)}", cancellationToken: ct);
            ctx.WriteResult(new { clientId, removed = true }, $"Deleted client {clientId}.");
            return ExitCodes.Ok;
        });
        return command;
    }
}

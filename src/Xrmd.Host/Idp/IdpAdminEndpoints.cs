using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Core.Errors;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Xrmd.Host.Idp;

/// <summary>
/// The Admin API of the local identity provider (plan 2.8): <c>/admin/idp</c>, under the System Administrator rule of the
/// <c>/admin</c> group (401, then 403, before anything here runs). Every route answers 409 when <c>Auth:Mode</c> is not Local.
/// Handlers read their input themselves (no bound parameters), as the rest of the Admin API does: binding would answer 400 before 403.
/// </summary>
public static class IdpAdminEndpoints
{
    public const string NotEnabledMessage = "The local identity provider is not enabled (Auth:Mode is not Local).";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapIdpAdminEndpoints(this RouteGroupBuilder admin)
    {
        var idp = admin.MapGroup("/idp");
        idp.MapPut("/users/{systemuserid}/password", (Func<HttpContext, Task<IResult>>)SetPasswordAsync);
        idp.MapPost("/users/{systemuserid}/totp", (Func<HttpContext, Task<IResult>>)EnableTotpAsync);
        idp.MapDelete("/users/{systemuserid}/totp", (Func<HttpContext, Task<IResult>>)DisableTotpAsync);
        idp.MapDelete("/users/{systemuserid}", (Func<HttpContext, Task<IResult>>)RemoveUserAsync);
        idp.MapGet("/clients", (Func<HttpContext, Task<IResult>>)ListClientsAsync);
        idp.MapPost("/clients", (Func<HttpContext, Task<IResult>>)CreateClientAsync);
        idp.MapDelete("/clients/{clientId}", (Func<HttpContext, Task<IResult>>)DeleteClientAsync);
        return admin;
    }

    private static XrmdFaultException Fault(string message, int status) => new(ErrorCodes.InvalidArgument, message, status);

    private static void RequireLocal(HttpContext context)
    {
        if (context.RequestServices.GetRequiredService<IConfiguration>()["Auth:Mode"] != "Local")
            throw Fault(NotEnabledMessage, StatusCodes.Status409Conflict);
    }

    private static async Task<Guid> RouteUserAsync(HttpContext context)
    {
        var text = context.Request.RouteValues["systemuserid"] as string ?? "";
        if (!Guid.TryParse(text, out var id)) throw XrmdFaultException.InvalidArgument($"The user id must be a GUID: '{text}'.");
        if (!await UserExistsAsync(context, id, enabledOnly: false))
            throw XrmdFaultException.ObjectDoesNotExist("systemuser", id);
        return id;
    }

    private static async Task<bool> UserExistsAsync(HttpContext context, Guid id, bool enabledOnly)
    {
        var database = context.RequestServices.GetRequiredService<IDbConnectionFactory>();
        var d = database.Dialect;
        await using var conn = await database.OpenAsync(context.RequestAborted);
        await using var cmd = conn.Command("SELECT 1 FROM data.systemuser WHERE systemuserid = @id"
                                           + (enabledOnly ? $" AND {d.IsNull("isdisabled", d.BooleanLiteral(false))} = {d.BooleanLiteral(false)}" : ""));
        cmd.AddParameter("@id", DbType.Guid, id);
        return await cmd.ExecuteScalarAsync(context.RequestAborted) is not null;
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpContext context)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<JsonObject>(context.RequestAborted)
                   ?? throw XrmdFaultException.InvalidArgument("The request body must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw XrmdFaultException.InvalidArgument("The request body must be a JSON object: " + ex.Message);
        }
        catch (InvalidOperationException)
        {
            throw XrmdFaultException.InvalidArgument("The request body must be application/json.");
        }
    }

    private static string? OptionalString(JsonObject body, string name) => body[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => throw XrmdFaultException.InvalidArgument($"'{name}' must be a string."),
    };

    /// <summary>Runs a credential operation; a refusal of the service becomes a 400 (invalid argument) or 409 (state).</summary>
    private static async Task<T> CredentialsAsync<T>(HttpContext context, Func<CredentialService, Task<T>> action)
    {
        try
        {
            return await action(context.RequestServices.GetRequiredService<CredentialService>());
        }
        catch (ArgumentException ex)
        {
            throw XrmdFaultException.InvalidArgument(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw Fault(ex.Message, StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> SetPasswordAsync(HttpContext context)
    {
        RequireLocal(context);
        var id = await RouteUserAsync(context);
        var body = await ReadBodyAsync(context);
        var password = OptionalString(body, "password") ?? throw XrmdFaultException.InvalidArgument("'password' is required.");
        await CredentialsAsync(context, async c => { await c.SetPasswordAsync(id, password, context.RequestAborted); return 0; });
        return Results.NoContent();
    }

    private static async Task<IResult> EnableTotpAsync(HttpContext context)
    {
        RequireLocal(context);
        var id = await RouteUserAsync(context);
        var uri = await CredentialsAsync(context, c => c.EnableTotpAsync(id, context.RequestAborted));
        return Results.Json(new { otpauthUri = uri }, WebJson);
    }

    private static async Task<IResult> DisableTotpAsync(HttpContext context)
    {
        RequireLocal(context);
        var id = await RouteUserAsync(context);
        await CredentialsAsync(context, async c => { await c.DisableTotpAsync(id, context.RequestAborted); return 0; });
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveUserAsync(HttpContext context)
    {
        RequireLocal(context);
        var id = await RouteUserAsync(context);
        await CredentialsAsync(context, async c => { await c.RemoveAsync(id, context.RequestAborted); return 0; });
        return Results.NoContent();
    }

    private static bool IsBuiltIn(string? clientId) => clientId is IdpClients.Cli or IdpClients.Web;

    private static async Task<IResult> ListClientsAsync(HttpContext context)
    {
        RequireLocal(context);
        var manager = context.RequestServices.GetRequiredService<IOpenIddictApplicationManager>();
        var ct = context.RequestAborted;
        var rows = new List<(string ClientId, object Row)>();
        await foreach (var application in manager.ListAsync(null, null, ct))
        {
            var properties = await manager.GetPropertiesAsync(application, ct);
            string? systemUserId = properties.TryGetValue(IdpClients.SystemUserIdProperty, out var bound) && bound.ValueKind == JsonValueKind.String
                ? bound.GetString() : null;
            var id = await manager.GetClientIdAsync(application, ct) ?? "";
            rows.Add((id, new
            {
                clientId = id,
                displayName = await manager.GetDisplayNameAsync(application, ct),
                type = await manager.GetClientTypeAsync(application, ct),
                systemuserid = systemUserId,
                redirectUris = (await manager.GetRedirectUrisAsync(application, ct)).ToArray(),
            }));
        }
        return Results.Json(rows.OrderBy(r => r.ClientId, StringComparer.Ordinal).Select(r => r.Row).ToList(), WebJson);
    }

    /// <summary>
    /// <c>POST /admin/idp/clients</c> <c>{displayName, type, systemuserid?, redirectUris?}</c>. A confidential client gets the
    /// client-credentials grant only and is bound to an enabled user; a public client gets the authorization code flow with PKCE
    /// (and refresh tokens) for its redirect URIs. The secret is in the answer once and stored hashed.
    /// </summary>
    private static async Task<IResult> CreateClientAsync(HttpContext context)
    {
        RequireLocal(context);
        var body = await ReadBodyAsync(context);
        var displayName = OptionalString(body, "displayName");
        if (string.IsNullOrWhiteSpace(displayName)) throw XrmdFaultException.InvalidArgument("'displayName' is required.");
        var type = OptionalString(body, "type");
        var redirects = new List<Uri>();
        if (body["redirectUris"] is { } uris)
        {
            if (uris is not JsonArray array) throw XrmdFaultException.InvalidArgument("'redirectUris' must be an array of strings.");
            foreach (var item in array)
            {
                if (item is not JsonValue v || !v.TryGetValue<string>(out var text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
                    throw XrmdFaultException.InvalidArgument("'redirectUris' must hold absolute URIs.");
                redirects.Add(uri);
            }
        }
        var systemUserText = OptionalString(body, "systemuserid");

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = Guid.NewGuid().ToString("D"),
            DisplayName = displayName.Trim(),
        };
        string? secret = null;
        switch (type)
        {
            case ClientTypes.Confidential:
                if (systemUserText is null)
                    throw XrmdFaultException.InvalidArgument("A confidential client needs 'systemuserid': the user its tokens are issued for.");
                if (!Guid.TryParse(systemUserText, out var userId))
                    throw XrmdFaultException.InvalidArgument($"'systemuserid' must be a GUID: '{systemUserText}'.");
                if (redirects.Count > 0) throw XrmdFaultException.InvalidArgument("'redirectUris' applies only to public clients.");
                if (!await UserExistsAsync(context, userId, enabledOnly: true))
                    throw XrmdFaultException.InvalidArgument($"'systemuserid' {userId} is not an enabled user of this organization (not found).");
                secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                descriptor.ClientType = ClientTypes.Confidential;
                descriptor.ClientSecret = secret;
                descriptor.Properties[IdpClients.SystemUserIdProperty] = JsonSerializer.SerializeToElement(userId.ToString());
                descriptor.Permissions.Add(Permissions.Endpoints.Token);
                descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
                break;
            case ClientTypes.Public:
                if (systemUserText is not null) throw XrmdFaultException.InvalidArgument("'systemuserid' applies only to confidential clients.");
                if (redirects.Count == 0) throw XrmdFaultException.InvalidArgument("A public client needs at least one of 'redirectUris'.");
                descriptor.ClientType = ClientTypes.Public;
                // OpenIddict allows a loopback redirect with any port (RFC 8252 §7.3) only for a native application.
                descriptor.ApplicationType = redirects.All(IsHttpLoopback) ? ApplicationTypes.Native : ApplicationTypes.Web;
                foreach (var uri in redirects) descriptor.RedirectUris.Add(uri);
                foreach (var permission in new[]
                {
                    Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession,
                    Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                }) descriptor.Permissions.Add(permission);
                descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
                break;
            default:
                throw XrmdFaultException.InvalidArgument("'type' must be 'confidential' or 'public'.");
        }

        await context.RequestServices.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(descriptor, context.RequestAborted);
        return Results.Json(new { clientId = descriptor.ClientId, clientSecret = secret }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static bool IsHttpLoopback(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host == "localhost");

    private static async Task<IResult> DeleteClientAsync(HttpContext context)
    {
        RequireLocal(context);
        var clientId = context.Request.RouteValues["clientId"] as string ?? "";
        if (IsBuiltIn(clientId)) throw XrmdFaultException.InvalidArgument($"The built-in client '{clientId}' cannot be deleted.");
        var manager = context.RequestServices.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await manager.FindByClientIdAsync(clientId, context.RequestAborted)
                          ?? throw new XrmdFaultException(ErrorCodes.ObjectDoesNotExist, $"Client '{clientId}' does not exist.", StatusCodes.Status404NotFound);
        await manager.DeleteAsync(application, context.RequestAborted);
        return Results.NoContent();
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Xrmd.Host.Auth;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Xrmd.Host.Idp;

/// <summary>
/// The pages of the local identity provider (L11): sign-in (password, then a TOTP code when the user has one), device code
/// verification and sign-out. Server-rendered HTML without JavaScript; every form carries an antiforgery token; the headers of
/// L11 are set for everything under <c>/adfs</c> by <see cref="IdpSetup.UseXrmdIdpHeaders"/>.
/// </summary>
internal static class LoginPages
{
    public const string LoginPath = "/adfs/login";
    public const string DevicePath = "/adfs/oauth2/deviceauth";
    public const string DeviceDonePath = "/adfs/device/done";
    public const string LogoutPath = "/adfs/oauth2/logout";
    public const string StylePath = "/adfs/idp.css";

    // Generic on purpose: an unknown user, a wrong password or code and a locked credential look the same (Review Focus 4).
    private const string SignInFailed = "The user name, password or code is incorrect, or the account is temporarily locked.";
    private const string LoginStatePurpose = "Xrmd.Idp.LoginState";
    private static readonly TimeSpan LoginStateLifetime = TimeSpan.FromMinutes(5);

    public static void Map(WebApplication app)
    {
        app.MapGet(LoginPath, LoginFormAsync).AllowAnonymous();
        app.MapPost(LoginPath, LoginAsync).AllowAnonymous();
        app.MapMethods(DevicePath, [HttpMethods.Get, HttpMethods.Post], DeviceAsync).AllowAnonymous();
        app.MapGet(DeviceDonePath, () => Page("Device signed in",
            "<p>The device is signed in. You can close this window and return to the device.</p>")).AllowAnonymous();
        app.MapMethods(LogoutPath, [HttpMethods.Get, HttpMethods.Post], LogoutAsync).AllowAnonymous();
        app.MapGet(StylePath, () => Results.Text(Style, "text/css; charset=utf-8")).AllowAnonymous();
    }

    // ---- sign-in ----

    private static async Task<IResult> LoginFormAsync(HttpContext context, IAntiforgery antiforgery, CredentialService credentials,
        CancellationToken ct)
    {
        var returnUrl = SafeReturnUrl(context.Request.Query["returnUrl"]);
        if (returnUrl is null && await IdpSession.CurrentUserAsync(context, credentials, ct) is { } user)
            return Page("Signed in", $"<p>You are signed in as {E(user.Name)}.</p>");
        return LoginForm(context, antiforgery, returnUrl, userName: null, error: null);
    }

    private static async Task<IResult> LoginAsync(HttpContext context, IAntiforgery antiforgery, CredentialService credentials,
        IDataProtectionProvider dataProtection, CancellationToken ct)
    {
        if (!await antiforgery.IsRequestValidAsync(context)) return FormExpired();
        var form = await context.Request.ReadFormAsync(ct);
        var returnUrl = SafeReturnUrl(form["returnUrl"]);
        var protector = dataProtection.CreateProtector(LoginStatePurpose).ToTimeLimitedDataProtector();

        // Second step: the password checked by the first step travels encrypted and time-limited, never in clear in the page.
        string userName, password;
        string? totp = null;
        if (!string.IsNullOrEmpty(form["state"]))
        {
            if (!TryUnprotect(protector, form["state"]!, out userName, out password))
                return LoginForm(context, antiforgery, returnUrl, null, "The sign-in took too long. Sign in again.");
            totp = form["totp"];
        }
        else
        {
            userName = form["username"].ToString();
            password = form["password"].ToString();
        }

        var result = await credentials.SignInAsync(userName, password, totp, ct);
        if (result is SignInResult.Succeeded succeeded)
        {
            if (await credentials.FindActiveUserAsync(succeeded.SystemUserId, ct) is not { } active)
                return LoginForm(context, antiforgery, returnUrl, userName, SignInFailed);
            await context.SignInAsync(IdpSession.Scheme, IdpSession.Principal(active),
                new AuthenticationProperties { IsPersistent = false });
            return Results.Redirect(returnUrl ?? LoginPath);
        }
        if (result == SignInResult.TotpRequired)
        {
            var state = protector.Protect(JsonSerializer.Serialize(new[] { userName, password }), LoginStateLifetime);
            return Page("Verification code", Form(context, antiforgery, LoginPath, $"""
                {Hidden("state", state)}{Hidden("returnUrl", returnUrl)}
                <label for="totp">Code from your authenticator app</label>
                <input id="totp" name="totp" type="text" inputmode="numeric" autocomplete="one-time-code" maxlength="6" required autofocus>
                <button type="submit">Verify</button>
                """));
        }
        return LoginForm(context, antiforgery, returnUrl, userName, SignInFailed);
    }

    private static IResult LoginForm(HttpContext context, IAntiforgery antiforgery, string? returnUrl, string? userName, string? error) =>
        Page("Sign in", Error(error) + Form(context, antiforgery, LoginPath, $"""
            {Hidden("returnUrl", returnUrl)}
            <label for="username">User name</label>
            <input id="username" name="username" type="text" autocomplete="username" value="{E(userName)}" required autofocus>
            <label for="password">Password</label>
            <input id="password" name="password" type="password" autocomplete="current-password" required>
            <button type="submit">Sign in</button>
            """));

    private static bool TryUnprotect(ITimeLimitedDataProtector protector, string state, out string userName, out string password)
    {
        userName = password = "";
        try
        {
            if (JsonSerializer.Deserialize<string[]>(protector.Unprotect(state)) is not [var u, var p]) return false;
            (userName, password) = (u, p);
            return true;
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Only the authorize and device pages of this IdP: never another site (no open redirect through the login page).</summary>
    internal static string? SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 }
        && (returnUrl.StartsWith("/adfs/oauth2/authorize", StringComparison.Ordinal)
            || returnUrl.StartsWith(DevicePath, StringComparison.Ordinal))
        && !returnUrl.Contains('\\') && !returnUrl.Contains("//", StringComparison.Ordinal)
            ? returnUrl
            : null;

    // ---- device code verification (RFC 8628 §3.3) ----

    private static async Task<IResult> DeviceAsync(HttpContext context, IAntiforgery antiforgery, CredentialService credentials,
        IOpenIddictApplicationManager applications, IConfiguration configuration, CancellationToken ct)
    {
        var post = HttpMethods.IsPost(context.Request.Method);
        if (post && !await antiforgery.IsRequestValidAsync(context)) return FormExpired();

        if (await IdpSession.CurrentUserAsync(context, credentials, ct) is not { } user)
            return Results.Challenge(new AuthenticationProperties { RedirectUri = DevicePath + (post ? "" : context.Request.QueryString) },
                [IdpSession.Scheme]);

        // OpenIddict has read user_code (query or form) and resolved it to the device request it belongs to.
        var device = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var userCode = device.Properties?.GetTokenValue(OpenIddictServerAspNetCoreConstants.Tokens.UserCode);
        if (!device.Succeeded || device.Principal.GetClaim(Claims.ClientId) is not { Length: > 0 } clientId)
            return UserCodeForm(context, antiforgery, string.IsNullOrEmpty(userCode) ? null : "The code is not valid or has expired.");

        var action = post ? (await context.Request.ReadFormAsync(ct))["action"].ToString() : "";
        if (action == "approve")
        {
            var audience = JwtSetup.LocalAudience(configuration.GetSection("Auth"));
            if (!IdpScopes.TryResolve(device.Principal.GetScopes(), audience, out var granted))
                return TokenEndpoint.Refuse(Errors.InvalidScope, "The requested scope is not supported by this environment.");
            return Results.SignIn(IdpPrincipal.ForUser(user, granted, audience),
                new AuthenticationProperties { RedirectUri = DeviceDonePath }, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }
        if (action == "deny")
            return Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.AccessDenied,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user denied the device.",
            }) { RedirectUri = LoginPath }, [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

        var application = await applications.FindByClientIdAsync(clientId, ct);
        var displayName = application is null ? clientId : await applications.GetDisplayNameAsync(application, ct) ?? clientId;
        return Page("Sign in a device", $"""
            <p>{E(displayName)} asks to sign in as <strong>{E(user.Name)}</strong> with code <strong>{E(userCode)}</strong>.</p>
            <p>Approve only if you started this sign-in on your device and the code matches.</p>
            """ + Form(context, antiforgery, DevicePath, $"""
            {Hidden("user_code", userCode)}
            <button type="submit" name="action" value="approve">Approve</button>
            <button type="submit" name="action" value="deny">Deny</button>
            """));
    }

    private static IResult UserCodeForm(HttpContext context, IAntiforgery antiforgery, string? error) =>
        Page("Sign in a device", Error(error) + Form(context, antiforgery, DevicePath, """
            <label for="user_code">Code shown on the device</label>
            <input id="user_code" name="user_code" type="text" autocomplete="off" required autofocus>
            <button type="submit">Next</button>
            """));

    // ---- sign-out ----

    private static async Task<IResult> LogoutAsync(HttpContext context, IAntiforgery antiforgery, CancellationToken ct)
    {
        if (HttpMethods.IsGet(context.Request.Method))
        {
            // The request's parameters go back with the form, so OpenIddict validates the same request on the POST.
            var hidden = new StringBuilder();
            foreach (var (name, values) in context.Request.Query)
                foreach (var value in values)
                    hidden.Append(Hidden(name, value));
            return Page("Sign out", Form(context, antiforgery, LogoutPath, $"""
                {hidden}<p>Sign out of this environment?</p>
                <button type="submit">Sign out</button>
                """));
        }

        if (!await antiforgery.IsRequestValidAsync(context)) return FormExpired();
        await context.SignOutAsync(IdpSession.Scheme);
        // OpenIddict redirects to the validated post_logout_redirect_uri when there is one, else to the login page.
        return Results.SignOut(new AuthenticationProperties { RedirectUri = LoginPath },
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    // ---- HTML ----

    private static IResult FormExpired() =>
        Page("Form expired", $"<p>The form has expired. <a href=\"{LoginPath}\">Start again</a>.</p>",
            StatusCodes.Status400BadRequest);

    /// <summary>A POST form with the antiforgery token (L11: on every form).</summary>
    private static string Form(HttpContext context, IAntiforgery antiforgery, string action, string content)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return $"<form method=\"post\" action=\"{E(action)}\">\n{Hidden(tokens.FormFieldName, tokens.RequestToken)}\n{content}\n</form>\n";
    }

    private static IResult Page(string title, string body, int status = StatusCodes.Status200OK)
    {
        var html = $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{E(title)}</title>
            <link rel="stylesheet" href="{StylePath}">
            </head>
            <body>
            <main>
            <h1>{E(title)}</h1>
            {body}
            </main>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    private static string Hidden(string name, string? value) =>
        value is null ? "" : $"<input type=\"hidden\" name=\"{E(name)}\" value=\"{E(value)}\">";

    private static string Error(string? error) => error is null ? "" : $"<p class=\"error\" role=\"alert\">{E(error)}</p>\n";

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    private const string Style = """
        body { font-family: system-ui, sans-serif; background: #f5f5f5; color: #1b1b1b; margin: 0; }
        main { max-width: 22rem; margin: 4rem auto; padding: 2rem; background: #fff; border-radius: 4px; box-shadow: 0 1px 4px rgba(0,0,0,.15); }
        h1 { font-size: 1.4rem; margin-top: 0; }
        label { display: block; margin: 1rem 0 .3rem; }
        input[type=text], input[type=password] { width: 100%; box-sizing: border-box; padding: .5rem; font-size: 1rem; }
        button { margin-top: 1.2rem; margin-right: .5rem; padding: .5rem 1.2rem; font-size: 1rem; }
        .error { color: #a4262c; }
        """;
}

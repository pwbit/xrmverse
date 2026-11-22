using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Xrmd.Abstractions;
using Xrmd.Core.Errors;

namespace Xrmd.WebApi;

/// <summary>
/// Reads the caller's identity from the JWT claims of the current HTTP request (raw claim names), and the impersonation
/// headers <c>MSCRMCallerID</c> and <c>CallerObjectId</c>.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <summary>The header carrying the <c>systemuserid</c> to impersonate.</summary>
    public const string CallerIdHeader = "MSCRMCallerID";

    /// <summary>The header carrying the Entra object id of the user to impersonate.</summary>
    public const string CallerObjectIdHeader = "CallerObjectId";

    private HttpContext Context => accessor.HttpContext ?? throw new InvalidOperationException("No HTTP context available.");

    private ClaimsPrincipal Principal => Context.User;

    public Guid ObjectId
    {
        get
        {
            var raw = Principal.FindFirstValue("oid") ?? Principal.FindFirstValue("sub");
            return Guid.TryParse(raw, out var id)
                ? id
                : throw new InvalidOperationException("Token has no valid 'oid' or 'sub' claim.");
        }
    }

    public string Name =>
        Principal.FindFirstValue("name") ?? Principal.FindFirstValue("preferred_username") ?? "";

    public Guid? CallerId => HeaderGuid(CallerIdHeader);

    public Guid? CallerObjectId => HeaderGuid(CallerObjectIdHeader);

    /// <summary>The header's value as a GUID; null when the header is absent or empty.</summary>
    /// <exception cref="XrmdFaultException">400 InvalidArgument when the value is not one GUID.</exception>
    private Guid? HeaderGuid(string name)
    {
        var values = Context.Request.Headers[name];
        if (values.Count == 0) return null;
        var raw = values.Count == 1 ? values[0]?.Trim() : null;
        if (values.Count == 1 && string.IsNullOrEmpty(raw)) return null;
        return Guid.TryParse(raw, out var id)
            ? id
            : throw XrmdFaultException.InvalidArgument($"The value of the HTTP header '{name}' is not a valid GUID.");
    }
}

using System.Security.Claims;
using Xrmd.Abstractions;

namespace Xrmd.Tds.Execution;

/// <summary>
/// The caller of a SQL endpoint request: the principal the authenticator returned at login (<c>oid</c> claim, falling back to
/// <c>sub</c>, as <c>Xrmd.WebApi.CurrentUser</c> reads a token). TDS has no impersonation headers.
/// </summary>
public sealed class TdsCurrentUser(ClaimsPrincipal? principal) : ICurrentUser
{
    public Guid ObjectId
    {
        get
        {
            var raw = principal?.FindFirstValue("oid") ?? principal?.FindFirstValue("sub");
            return Guid.TryParse(raw, out var id)
                ? id
                : throw new InvalidOperationException("The SQL endpoint login has no valid 'oid' or 'sub' claim.");
        }
    }

    public string Name => principal?.FindFirstValue("name") ?? principal?.FindFirstValue("preferred_username") ?? "";

    public Guid? CallerId => null;

    public Guid? CallerObjectId => null;
}

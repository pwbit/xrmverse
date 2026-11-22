using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Abstractions;
using Xrmd.Core;
using Xrmd.Core.Errors;
using Xrmd.Security;

namespace Xrmd.Admin;

/// <summary>
/// Lets a request through only when the authenticated user (and, when impersonating, the impersonator) is a System
/// Administrator. The check is the Core's: <see cref="CallerResolver"/> then <see cref="SecurityGuard.IsAdministratorAsync"/>;
/// the Admin API has no path of its own around the security model. A caller who cannot be resolved (not a member,
/// disabled, no role) is no administrator either. Runs before the handler reads anything: input validation never
/// answers before the role check.
/// </summary>
public sealed class AdminOnlyFilter : IEndpointFilter
{
    public const string Message = "The Admin API requires the System Administrator role.";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var ct = http.RequestAborted;
        var services = http.RequestServices;
        Caller caller;
        try
        {
            caller = await services.GetRequiredService<CallerResolver>().ResolveAsync(ct);
        }
        catch (XrmdFaultException ex) when (ex.HttpStatus == StatusCodes.Status403Forbidden)
        {
            throw Denied();
        }

        if (!await services.GetRequiredService<SecurityGuard>().IsAdministratorAsync(caller, ct)) throw Denied();
        return await next(context);
    }

    private static XrmdFaultException Denied() => new(ErrorCodes.PrivilegeDenied, Message, StatusCodes.Status403Forbidden);
}

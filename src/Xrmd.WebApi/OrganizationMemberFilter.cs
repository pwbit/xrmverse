using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xrmd.Core;

namespace Xrmd.WebApi;

public static class OrganizationMemberFilter
{
    /// <summary>
    /// Requires the caller to map to a <c>systemuser</c> before the endpoint runs (403 NotMemberOfOrganization otherwise).
    /// The scoped <see cref="CallerResolver"/> caches the caller, so the organization service reuses it.
    /// </summary>
    public static TBuilder RequireOrganizationMember<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            await http.RequestServices.GetRequiredService<CallerResolver>().ResolveAsync(http.RequestAborted);
            return await next(context);
        });
}

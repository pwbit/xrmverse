using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Xrmd.Tds;

public static class TdsSetup
{
    /// <summary>
    /// Registers the SQL endpoint (section <c>Tds</c>). The authenticator and the query handler are registered only when none is
    /// registered yet; the defaults refuse every login and every request.
    /// </summary>
    public static IServiceCollection AddXrmdTds(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TdsOptions>().Bind(configuration.GetSection(TdsOptions.Section));
        services.TryAddScoped<ITdsAuthenticator, RefuseAllTdsAuthenticator>();
        services.TryAddScoped<ITdsQueryHandler, UnavailableTdsQueryHandler>();
        services.AddSingleton<TdsListener>();
        services.AddHostedService(sp => sp.GetRequiredService<TdsListener>());
        return services;
    }
}

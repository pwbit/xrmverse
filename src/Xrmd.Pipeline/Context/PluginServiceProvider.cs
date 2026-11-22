using Microsoft.Xrm.Sdk;

namespace Xrmd.Pipeline;

/// <summary>
/// The <see cref="IServiceProvider"/> handed to a plugin: context, tracing, service factory, logger and environment variables
/// (an <see cref="EnvironmentVariableService"/> reading as the SYSTEM user, created on the first request).
/// </summary>
public sealed class PluginServiceProvider(XrmdPluginExecutionContext context, TracingService tracing,
    IOrganizationServiceFactory factory, Microsoft.Xrm.Sdk.PluginTelemetry.ILogger logger) : IServiceProvider
{
    private EnvironmentVariableService? _environmentVariables;

    /// <inheritdoc />
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IEnvironmentVariableService))
            return _environmentVariables ??= new EnvironmentVariableService(factory.CreateOrganizationService(null));
        if (serviceType == typeof(IExecutionContext) || serviceType == typeof(IPluginExecutionContext)
            || serviceType == typeof(IPluginExecutionContext2) || serviceType == typeof(IPluginExecutionContext3)
            || serviceType == typeof(IPluginExecutionContext4) || serviceType == typeof(IPluginExecutionContext5)
            || serviceType == typeof(IPluginExecutionContext6) || serviceType == typeof(IPluginExecutionContext7)
            || serviceType == typeof(XrmdPluginExecutionContext)) return context;
        if (serviceType == typeof(ITracingService) || serviceType == typeof(TracingService)) return tracing;
        if (serviceType == typeof(IOrganizationServiceFactory)) return factory;
        if (serviceType == typeof(Microsoft.Xrm.Sdk.PluginTelemetry.ILogger)) return logger;
        return null;
    }
}

using System.Runtime.CompilerServices;
using Xrmd.Abstractions.Sdk;
using Xrmd.Host;

var builder = WebApplication.CreateBuilder(args);

// An invalid Proxy:TrustedProxies or Proxy:AllowedHosts entry refuses the start, naming the entry (ruling H4).
if (ProxyOptions.Validate(builder.Configuration) is { } proxyRefusal)
{
    await Console.Error.WriteLineAsync(proxyRefusal);
    return ProxyOptions.ExitCode;
}

// The Microsoft SDK comes from the customer's SDK folder (Plan 12): load it before anything that uses it is compiled.
if (SdkBootstrap.Ensure(builder.Configuration) is { } refusal)
{
    await Console.Error.WriteLineAsync(refusal);
    return SdkBootstrap.ExitCode;
}

if (await Program.RunAsync(builder) is { } sdkFreeRefusal)
{
    await Console.Error.WriteLineAsync(sdkFreeRefusal);
    return SdkBootstrap.ExitCode;
}

// Environment.ExitCode, not 0: the instance lock watchdog sets 1 when the host stops because it lost its lock.
return Environment.ExitCode;

public partial class Program
{
    /// <summary>The host itself; not inlined into the entry point, so nothing of it is compiled before <see cref="SdkBootstrap.Ensure"/>.</summary>
    /// <remarks>
    /// Null once the host has run. In mode <c>none</c> (plan S.2a), a part of the host that still needs the SDK fails to load
    /// (the <see cref="SdkFreeResolver"/> refuses the assembly): the result is then the message naming it, without a stack trace.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string?> RunAsync(WebApplicationBuilder builder)
    {
        try
        {
            builder.AddXrmd();
            var app = builder.Build();
            await app.UseXrmdAsync();
            await app.RunAsync();
            return null;
        }
        catch (Exception ex) when (SdkBootstrap.Mode == SdkMode.None && SdkFreeResolver.RefusedAssembly(ex) is not null)
        {
            return SdkFreeResolver.NotYetMessage(SdkFreeResolver.Refusals.FirstOrDefault()?.RequestedBy);
        }
    }
}

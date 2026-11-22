using System.Runtime.CompilerServices;
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

await Program.RunAsync(builder);
// Environment.ExitCode, not 0: the instance lock watchdog sets 1 when the host stops because it lost its lock.
return Environment.ExitCode;

public partial class Program
{
    /// <summary>The host itself; not inlined into the entry point, so nothing of it is compiled before <see cref="SdkBootstrap.Ensure"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunAsync(WebApplicationBuilder builder)
    {
        builder.AddXrmd();
        var app = builder.Build();
        await app.UseXrmdAsync();
        await app.RunAsync();
    }
}

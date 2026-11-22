using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Xrmd.Admin;

/// <summary>
/// The telemetry of xrmd: one <see cref="ActivitySource"/> and one <see cref="Meter"/>, both named <c>Xrmd</c>. The pipeline
/// stages (<c>pipeline.*</c> activities, <c>xrmd.pipeline.stage.duration</c>) are emitted by Xrmd.Pipeline under the same names;
/// this class holds the front-end instruments (<c>xrmd.requests</c>, <c>xrmd.async.queue</c>) and the wiring.
/// </summary>
public static class Telemetry
{
    public const string Name = "Xrmd";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    /// <summary><c>xrmd.requests</c>: HTTP requests served, tags <c>route</c> (the matched route pattern) and <c>status</c>.</summary>
    public static readonly Counter<long> Requests = Meter.CreateCounter<long>("xrmd.requests", description: "HTTP requests served.");

    /// <summary>The metrics path served when <c>Observability:Metrics:Enabled</c> is not false.</summary>
    public const string MetricsPath = "/metrics";

    /// <summary>
    /// Tracing (ASP.NET Core and the <c>Xrmd</c> source; OTLP export when <c>Observability:Otlp:Endpoint</c> is configured) and
    /// metrics (the <c>Xrmd</c> meter; a Prometheus scraping endpoint unless <c>Observability:Metrics:Enabled</c> is false).
    /// </summary>
    public static IServiceCollection AddXrmdTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otlp = configuration["Observability:Otlp:Endpoint"];
        var prometheus = MetricsEnabled(configuration);

        services.AddSingleton<AsyncQueueGauge>();
        services.AddHostedService(sp => sp.GetRequiredService<AsyncQueueGauge>());

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("xrmd"))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation().AddSource(Name);
                if (!string.IsNullOrWhiteSpace(otlp)) t.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
            })
            .WithMetrics(m =>
            {
                m.AddMeter(Name);
                if (prometheus) m.AddPrometheusExporter();
            });
        return services;
    }

    /// <summary><c>Observability:Metrics:Enabled</c>; true unless it is configured as false.</summary>
    public static bool MetricsEnabled(IConfiguration configuration) =>
        !bool.TryParse(configuration["Observability:Metrics:Enabled"], out var enabled) || enabled;

    /// <summary>Counts every request in <see cref="Requests"/> (also the ones the authentication refuses).</summary>
    public static IApplicationBuilder UseXrmdRequestMetrics(this IApplicationBuilder app) => app.UseMiddleware<RequestMetricsMiddleware>();

    /// <summary>
    /// <c>GET /metrics</c> without authentication (a scraper has no token): the Prometheus endpoint, or a plain 404 when
    /// <c>Observability:Metrics:Enabled</c> is false.
    /// </summary>
    public static void MapXrmdMetrics(this IEndpointRouteBuilder app, IConfiguration configuration)
    {
        if (MetricsEnabled(configuration)) app.MapPrometheusScrapingEndpoint(MetricsPath).AllowAnonymous();
        else app.MapGet(MetricsPath, () => Results.NotFound()).AllowAnonymous();
    }
}

internal sealed class RequestMetricsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            // An exception escaping the pipeline becomes a 500 after this middleware has returned.
            var status = failed && !context.Response.HasStarted ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            Telemetry.Requests.Add(1, new KeyValuePair<string, object?>("route", route), new KeyValuePair<string, object?>("status", status));
        }
    }
}

/// <summary>Registers the observable gauge <c>xrmd.async.queue</c> (tag <c>state</c>: waiting | inProgress).</summary>
/// <remarks>
/// The gauge lives on a <see cref="Meter"/> of this host (same name, <see cref="Telemetry.Name"/>), disposed with it: a meter
/// cannot drop one instrument, so a gauge on the static <see cref="Telemetry.Meter"/> would keep its callback, and through it
/// this host's services, for the life of the process (every in-process host of the test suites).
/// </remarks>
internal sealed class AsyncQueueGauge(AsyncQueueStats stats) : IHostedService, IDisposable
{
    private readonly Meter _meter = new(Telemetry.Name);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _meter.CreateObservableGauge("xrmd.async.queue", () =>
        {
            var (waiting, inProgress) = stats.Cached();
            return new[]
            {
                new Measurement<int>(waiting, new KeyValuePair<string, object?>("state", "waiting")),
                new Measurement<int>(inProgress, new KeyValuePair<string, object?>("state", "inProgress")),
            };
        }, description: "Asynchronous jobs by state.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _meter.Dispose();
}

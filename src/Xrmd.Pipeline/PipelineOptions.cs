namespace Xrmd.Pipeline;

/// <summary>Tunable limits of the plugin pipeline.</summary>
public sealed class PipelineOptions
{
    /// <summary>Maximum time a single plugin execution may run.</summary>
    public TimeSpan PluginTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the async job worker polls for work.</summary>
    public TimeSpan AsyncPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum retries of a failed async job.</summary>
    public int AsyncMaxRetries { get; set; } = 3;

    /// <summary>Delay before the first retry; the n-th delay is <c>AsyncRetryDelay * 2^(n-1)</c>.</summary>
    public TimeSpan AsyncRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether the host runs the background async job worker (<c>Pipeline:AsyncWorkerEnabled</c>, default true). The host
    /// turns it off when <c>ConnectionStrings:Org</c> is not configured; jobs are still queued when it is off.
    /// </summary>
    public bool AsyncWorkerEnabled { get; set; } = true;

    /// <summary>
    /// Whether a webhook url may use any port (<c>Pipeline:WebhookAllowAnyPort</c>, default false: 80 or 443 only, as in
    /// Dataverse). For tests and development against a local receiver.
    /// </summary>
    public bool WebhookAllowAnyPort { get; set; }

    /// <summary>
    /// How long one attempt of a webhook delivery may wait for the answer (<c>Pipeline:WebhookTimeout</c>, a time span written
    /// <c>hh:mm:ss</c> such as <c>00:01:00</c>; default 60 s); past it the delivery is a retryable failure. Zero, negative
    /// or more than <see cref="int.MaxValue"/> milliseconds is refused at startup.
    /// </summary>
    public TimeSpan WebhookTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

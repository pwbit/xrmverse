using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Xrmd.Pipeline;

/// <summary>
/// Activities and durations of the pipeline stages. The source and the meter are named <c>Xrmd</c>, like the ones of
/// <c>Xrmd.Admin.Telemetry</c> (which this project cannot reference): the exporters subscribe by name, so both feed the same
/// stream. Without a listener or exporter a stage costs a stopwatch read.
/// </summary>
internal static class PipelineTelemetry
{
    internal const string Name = "Xrmd";

    private static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> StageDuration =
        Meter.CreateHistogram<double>("xrmd.pipeline.stage.duration", unit: "ms", description: "Duration of a pipeline stage.");

    /// <summary>Starts the activity <c>pipeline.&lt;stage&gt;</c> (tags <c>message</c> and <c>entity</c>); dispose it when the stage ends.</summary>
    public static StageScope Stage(string stage, string message, string? entity) => new(stage, message, entity);

    internal readonly struct StageScope : IDisposable
    {
        private readonly Activity? _activity;
        private readonly long _start;
        private readonly string _stage;
        private readonly string _message;

        public StageScope(string stage, string message, string? entity)
        {
            _stage = stage;
            _message = message;
            _activity = Source.StartActivity("pipeline." + stage);
            _activity?.SetTag("message", message);
            if (entity is not null) _activity?.SetTag("entity", entity);
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            StageDuration.Record(Stopwatch.GetElapsedTime(_start).TotalMilliseconds,
                new KeyValuePair<string, object?>("message", _message), new KeyValuePair<string, object?>("stage", _stage));
            _activity?.Dispose();
        }
    }
}

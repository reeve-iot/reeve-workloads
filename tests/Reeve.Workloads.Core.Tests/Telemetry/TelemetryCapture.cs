using System.Diagnostics;
using System.Diagnostics.Metrics;
using Reeve.Workloads.Core.Telemetry;
using Xunit;

namespace Reeve.Workloads.Core.Tests.Telemetry;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkloadsTelemetryCollection
{
    public const string Name = "workloads-telemetry";
}

internal sealed class MeterCapture : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly object gate = new();
    private readonly List<Measured> measured = [];

    public MeterCapture()
    {
        listener.InstrumentPublished = (instrument, subscriber) =>
        {
            if (instrument.Meter.Name == WorkloadsTelemetry.Name)
            {
                subscriber.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();
    }

    internal readonly record struct Measured(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags);

    public void CollectGauges() => listener.RecordObservableInstruments();

    public IReadOnlyList<double> Values(string instrument, params (string Key, string Value)[] tags)
    {
        lock (gate)
        {
            return measured
                .Where(m => m.Instrument == instrument)
                .Where(m => tags.All(t => m.Tags.TryGetValue(t.Key, out var value) && value == t.Value))
                .Select(m => m.Value)
                .ToList();
        }
    }

    public void Dispose() => listener.Dispose();

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        var copied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            copied[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        lock (gate)
        {
            measured.Add(new Measured(instrument.Name, Convert.ToDouble(value), copied));
        }
    }
}

internal sealed class ActivityCapture : IDisposable
{
    private readonly ActivityListener listener;
    private readonly object gate = new();
    private readonly List<Activity> stopped = [];

    public ActivityCapture()
    {
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WorkloadsTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (gate) { stopped.Add(activity); } },
        };
        ActivitySource.AddActivityListener(listener);
    }

    public IReadOnlyList<Activity> Stopped { get { lock (gate) { return [.. stopped]; } } }

    public void Dispose() => listener.Dispose();
}

using System.Diagnostics.Metrics;
using Xunit;

namespace NBenchmark.Tests.Engine;

public sealed class DiagnosticsIntegrationTests
{
    [Fact]
    public async Task Full_Suite_Emits_Metrics_Through_Diagnostics()
    {
        var histogramsSeen = new HashSet<string>();

        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "NBenchmark")
            {
                histogramsSeen.Add(instrument.Name);
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => { });
        listener.Start();

        await new BenchmarkSuite("diag-smoke").WithIsolation(Isolation.Preferred)
            .Add("work", () => { })
            .WithWarmupSamples(0)
            .WithSamples(3)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Contains("nbenchmark.sample.duration", histogramsSeen);
        Assert.Contains("nbenchmark.samples.count", histogramsSeen);

        // Observable gauges are discovered on start, not by having samples.
        Assert.Contains("nbenchmark.ci.relative_half_width", histogramsSeen);
    }
}

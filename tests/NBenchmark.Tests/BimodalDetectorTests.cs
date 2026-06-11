using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class BimodalDetectorTests
{
    [Fact]
    public void Tight_Slow_Cluster_Is_Flagged()
    {
        // Main distribution centred near 100; the discarded tail is a tight
        // secondary peak near 500 (a repeatable structural bottleneck).
        var kept = BuildSorted(start: 95, count: 100, step: 0.1);
        var discarded = new double[] { 498, 500, 501, 502, 505 };

        var cluster = BimodalDetector.DetectSlowCluster(kept, discarded, totalSamples: kept.Length + discarded.Length);

        Assert.NotNull(cluster);
        Assert.Equal(5, cluster!.Value.Count);
        Assert.InRange(cluster.Value.Center, 495, 505);
    }

    [Fact]
    public void Scattered_Slow_Tail_Is_Not_Flagged()
    {
        // Discarded samples above the median are spread across a wide range:
        // scheduling noise, not a structural secondary mode.
        var kept = BuildSorted(start: 95, count: 100, step: 0.1);
        var discarded = new double[] { 200, 600, 1500, 4000 };

        var cluster = BimodalDetector.DetectSlowCluster(kept, discarded, totalSamples: kept.Length + discarded.Length);

        Assert.Null(cluster);
    }

    [Fact]
    public void Too_Few_Slow_Samples_Is_Not_Flagged()
    {
        var kept = BuildSorted(start: 95, count: 100, step: 0.1);
        var discarded = new double[] { 500, 501 };

        var cluster = BimodalDetector.DetectSlowCluster(kept, discarded, totalSamples: kept.Length + discarded.Length);

        Assert.Null(cluster);
    }

    [Fact]
    public void Empty_Inputs_Return_Null()
    {
        Assert.Null(BimodalDetector.DetectSlowCluster([], [1, 2, 3], 3));
        Assert.Null(BimodalDetector.DetectSlowCluster([1, 2, 3], [], 3));
        Assert.Null(BimodalDetector.DetectSlowCluster([1, 2, 3], [4, 5, 6], 0));
    }

    private static double[] BuildSorted(double start, int count, double step)
    {
        var values = new double[count];

        for (var i = 0; i < count; i++)
        {
            values[i] = start + (i * step);
        }

        return values;
    }
}

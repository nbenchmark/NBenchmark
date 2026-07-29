using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     A worker returns a bounded, representative subset of its raw samples rather than all of them.
///     These tests pin the three properties that make the subset safe to reason about: it describes
///     the same distribution, it stays in measurement order, and the trimmed-sample marks still point
///     at the samples they belong to.
/// </summary>
public class SampleReservoirTests
{
    [Fact]
    public void An_Array_Within_The_Cap_Is_Returned_Untouched()
    {
        var samples = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();

        var (reduced, _) = SampleReservoir.Reduce(samples, [], capacity: 4096, seed: 1);

        Assert.Same(samples, reduced);
    }

    [Fact]
    public void An_Unbounded_Cap_Returns_Everything()
    {
        var samples = Enumerable.Range(0, 50_000).Select(i => (double)i).ToArray();

        var (reduced, _) = SampleReservoir.Reduce(
            samples, [], MeasurementOptions.UnboundedRawSamples, seed: 1);

        Assert.Equal(50_000, reduced.Length);
    }

    [Fact]
    public void An_Oversized_Array_Is_Reduced_To_The_Cap()
    {
        var samples = Enumerable.Range(0, 50_000).Select(i => (double)i).ToArray();

        var (reduced, _) = SampleReservoir.Reduce(samples, [], capacity: 4096, seed: 1);

        Assert.Equal(4096, reduced.Length);
    }

    /// <summary>
    ///     <see cref="BenchmarkResult.RawSamples" /> is documented as being in measurement order, and
    ///     the Console sparkline draws it left to right on that basis.
    /// </summary>
    [Fact]
    public void The_Kept_Samples_Stay_In_Measurement_Order()
    {
        var samples = Enumerable.Range(0, 50_000).Select(i => (double)i).ToArray();

        var (reduced, _) = SampleReservoir.Reduce(samples, [], capacity: 4096, seed: 7);

        // The values are the indices, so ascending values means ascending original positions.
        Assert.Equal(reduced.OrderBy(v => v), reduced);
        Assert.Equal(reduced.Distinct().Count(), reduced.Length);
    }

    /// <summary>
    ///     The load-bearing property. A prefix would pass every other test here and still be wrong:
    ///     the first n samples are the slice nearest warmup, so a subset drawn that way would report a
    ///     distribution the run did not have.
    /// </summary>
    [Fact]
    public void The_Subset_Describes_The_Whole_Run_Rather_Than_Its_Opening()
    {
        // A deliberate step change halfway through: the first half is slow, the second fast. A
        // prefix would see only the slow half and report a median of 100.
        var samples = Enumerable.Range(0, 40_000)
            .Select(i => i < 20_000 ? 100.0 : 10.0)
            .ToArray();

        var (reduced, _) = SampleReservoir.Reduce(samples, [], capacity: 4096, seed: 3);

        var slowShare = reduced.Count(v => v == 100.0) / (double)reduced.Length;

        // Uniform selection puts the split within a couple of points of the true 50%.
        Assert.InRange(slowShare, 0.45, 0.55);
    }

    /// <summary>
    ///     Reproducibility. Two runs of the same configuration must ship the same samples, or a
    ///     repeat of an identical run looks like a measurement that moved.
    /// </summary>
    [Fact]
    public void The_Same_Seed_Selects_The_Same_Samples()
    {
        var samples = Enumerable.Range(0, 50_000).Select(i => (double)i).ToArray();

        var (first, _) = SampleReservoir.Reduce(samples, [], capacity: 4096, seed: 11);
        var (second, _) = SampleReservoir.Reduce(samples, [], capacity: 4096, seed: 11);
        var (other, _) = SampleReservoir.Reduce(samples, [], capacity: 4096, seed: 12);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    /// <summary>
    ///     The trap this class exists to avoid. <see cref="BenchmarkResult.TrimmedOrdinals" /> holds
    ///     positions <i>into</i> the sample array, so shipping a reduced array beside ordinals
    ///     computed against the full one marks the wrong samples - and marks that are merely wrong,
    ///     rather than absent, look exactly like correct ones.
    /// </summary>
    [Fact]
    public void Trimmed_Ordinals_Are_Remapped_Onto_The_Reduced_Array()
    {
        // Encode each sample's original index in its value, so a remapped ordinal can be checked
        // against the sample it is supposed to be pointing at.
        var samples = Enumerable.Range(0, 40_000).Select(i => (double)i).ToArray();

        // Every fifth sample was trimmed.
        var trimmed = Enumerable.Range(0, 40_000).Where(i => i % 5 == 0).ToArray();

        var (reduced, remapped) = SampleReservoir.Reduce(samples, trimmed, capacity: 4096, seed: 5);

        Assert.NotEmpty(remapped);

        foreach (var ordinal in remapped)
        {
            Assert.InRange(ordinal, 0, reduced.Length - 1);

            // The sample at the remapped position must be one that was genuinely trimmed.
            Assert.Equal(0, (int)reduced[ordinal] % 5);
        }

        // And nothing that survived untrimmed is marked.
        var marked = remapped.ToHashSet();

        for (var i = 0; i < reduced.Length; i++)
        {
            if ((int)reduced[i] % 5 != 0)
                Assert.DoesNotContain(i, marked);
        }
    }

    [Fact]
    public void Every_Trimmed_Sample_That_Survived_Selection_Is_Still_Marked()
    {
        var samples = Enumerable.Range(0, 40_000).Select(i => (double)i).ToArray();
        var trimmed = Enumerable.Range(0, 40_000).Where(i => i % 5 == 0).ToArray();

        var (reduced, remapped) = SampleReservoir.Reduce(samples, trimmed, capacity: 4096, seed: 5);

        var expected = Enumerable.Range(0, reduced.Length).Where(i => (int)reduced[i] % 5 == 0);

        Assert.Equal(expected, remapped);
    }

    [Fact]
    public void An_Untrimmed_Result_Reduces_To_No_Marks()
    {
        var samples = Enumerable.Range(0, 40_000).Select(i => (double)i).ToArray();

        var (_, remapped) = SampleReservoir.Reduce(samples, [], capacity: 4096, seed: 5);

        Assert.Empty(remapped);
    }

    [Fact]
    public void The_Default_Cap_Is_Whole_And_The_Unbounded_Sentinel_Is_Not_A_Count()
    {
        Assert.True(MeasurementOptions.DefaultMaxRawSamples > 0);
        Assert.Equal(0, MeasurementOptions.UnboundedRawSamples);
    }

    [Fact]
    public void A_Negative_Cap_Is_Refused_At_The_Option()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeasurementOptions { MaxRawSamples = -1 });
    }
}

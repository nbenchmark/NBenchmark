using NBenchmark;
using NBenchmark.Discovery;
using NBenchmark.Tests.ErrorFixtures;
using Xunit;

namespace NBenchmark.Tests.Discovery;

/// <summary>
///     What discovery actually <b>runs</b>, and when.
/// </summary>
/// <remarks>
///     <para>
///         Discovery reads attributes, which reads as a pure operation - but a
///         <c>[ArgumentsSource]</c> source is user code, and discovery invokes it. So a whole-assembly
///         pass has every class's side effects, and a worker measuring one class per group ran all of
///         them, once per group, to use one.
///     </para>
///     <para>
///         Counted rather than reasoned about, because "how many times did this run" is invisible in
///         every output the run produces.
///     </para>
/// </remarks>
public sealed class CaseSourceScopeTests
{
    /// <summary>
    ///     Discovery restricted to one class invokes only that class's case source.
    /// </summary>
    [Fact]
    public void Discover_RestrictedToOneClass_RunsOnlyThatClassesCaseSource()
    {
        CountingCaseSources.Reset();

        var suites = new BenchmarkDiscoverer()
            .Discover(typeof(FirstCountedBenchmarks).Assembly, typeof(FirstCountedBenchmarks).FullName);

        Assert.Single(suites);
        Assert.Equal(typeof(FirstCountedBenchmarks), suites[0].Type);

        Assert.Equal(1, CountingCaseSources.First);
        Assert.Equal(0, CountingCaseSources.Second);
    }

    /// <summary>
    ///     The unrestricted pass still covers the whole assembly, so the restriction is a filter rather
    ///     than a change of contract.
    /// </summary>
    [Fact]
    public void Discover_Unrestricted_StillRunsEveryCaseSource()
    {
        CountingCaseSources.Reset();

        _ = new BenchmarkDiscoverer().Discover(typeof(FirstCountedBenchmarks).Assembly);

        Assert.Equal(1, CountingCaseSources.First);
        Assert.Equal(1, CountingCaseSources.Second);
    }

    /// <summary>
    ///     A class named by a filter that does not exist yields nothing and runs nothing.
    /// </summary>
    [Fact]
    public void Discover_ForAnUnknownClass_YieldsNothing()
    {
        CountingCaseSources.Reset();

        Assert.Empty(new BenchmarkDiscoverer()
            .Discover(typeof(FirstCountedBenchmarks).Assembly, "NBenchmark.Tests.Discovery.NoSuchClass"));

        Assert.Equal(0, CountingCaseSources.First);
    }

    /// <summary>
    ///     An <b>instance</b> case source on a class discovery cannot construct is refused with a message
    ///     that names dependency injection and the remedy.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Case values decide how many benchmarks there are, so they are needed before any instance
    ///         exists - and the coordinator does not build its container until something is about to be
    ///         measured. Discovery therefore has only the type's own constructor, which a DI-only class
    ///         does not have.
    ///     </para>
    ///     <para>
    ///         The bare reflection error ("No parameterless constructor defined") sends the reader
    ///         looking for a constructor they deliberately did not write. Saying that instances come from
    ///         a factory, and that a static source needs no receiver, is what makes it actionable.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Discover_WithAFactoryAndAnUnconstructableCaseSource_NamesTheRemedy()
    {
        var discoverer = new BenchmarkDiscoverer(InstanceLifetime.PerMethod, factoryResolvedInstances: true);

        var error = Assert.Throws<InvalidOperationException>(
            () => discoverer.Discover(typeof(InjectedCaseSourceBenchmarks)));

        Assert.Contains("factory or service provider", error.Message, StringComparison.Ordinal);
        Assert.Contains("static", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InjectedCaseSourceBenchmarks.Cases), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Without a factory, the same class reports the plain construction failure and the static
    ///     remedy - the DI sentence would be a claim about a container that does not exist.
    /// </summary>
    [Fact]
    public void Discover_WithoutAFactory_ReportsThePlainConstructionFailure()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new BenchmarkDiscoverer().Discover(typeof(InjectedCaseSourceBenchmarks)));

        Assert.DoesNotContain("factory or service provider", error.Message, StringComparison.Ordinal);
        Assert.Contains("static", error.Message, StringComparison.Ordinal);
    }
}

/// <summary>
///     Counters in the discovering process, so "how many times did this source run" is answerable.
/// </summary>
internal static class CountingCaseSources
{
    public static int First;

    public static int Second;

    public static void Reset()
    {
        First = 0;
        Second = 0;
    }
}

public class FirstCountedBenchmarks
{
    [ArgumentsSource(nameof(Cases))]
    [Benchmark]
    public int Compute(int n) => n;

    public static IEnumerable<ValueTuple<int>> Cases()
    {
        CountingCaseSources.First++;

        yield return new ValueTuple<int>(1);
    }
}

public class SecondCountedBenchmarks
{
    [ArgumentsSource(nameof(Cases))]
    [Benchmark]
    public int Compute(int n) => n;

    public static IEnumerable<ValueTuple<int>> Cases()
    {
        CountingCaseSources.Second++;

        yield return new ValueTuple<int>(2);
    }
}

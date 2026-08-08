using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NBenchmark.DependencyInjection.Tests;

/// <summary>
///     The factory-shaped DI APIs, and what they let the harness decide.
/// </summary>
/// <remarks>
///     <para>
///         Until these overloads existed, every scoped-DI benchmark was permanently measured in the
///         host process: the only scoped API took a live <see cref="IServiceProvider" />, which is the
///         one thing that cannot cross a process boundary. The flagship EF Core guide taught exactly
///         that shape while separately promising isolation by default.
///     </para>
///     <para>
///         The end-to-end proof that a worker really rebuilds the container and scopes per instance
///         lives in <c>InstanceSourceIsolationTests</c>, which has a worker deployed beside it. What
///         is pinned here is the decision the coordinator makes, which is what routes a run to that
///         worker in the first place.
///     </para>
/// </remarks>
public class ScopedFactoryIsolationTests
{
    private static IServiceProvider BuildServices() => new ServiceCollection()
        .AddScoped<ScopedThing>()
        .AddTransient<FactoryScopedBenchmark>()
        .BuildServiceProvider();

    /// <summary>The headline: a scoped container passed as a factory no longer costs the run its isolation.</summary>
    [Fact]
    public void ScopedServiceProvider_Factory_CanIsolate()
    {
        var harness = BenchmarkHarness.Create([]).WithScopedServiceProvider(BuildServices);

        Assert.Null(harness.InstanceSourceRefusalForTesting());
    }

    [Fact]
    public void UseScopedDependencyInjection_Factory_CanIsolate()
    {
        var harness = BenchmarkHarness.Create([])
            .UseScopedDependencyInjection<FactoryScopedBenchmark>(BuildServices);

        Assert.Null(harness.InstanceSourceRefusalForTesting());
    }

    /// <summary>A live provider still cannot, and says which API to reach for instead.</summary>
    [Fact]
    public void ScopedServiceProvider_LiveProvider_CannotIsolate()
    {
        var harness = BenchmarkHarness.Create([]).WithScopedServiceProvider(BuildServices());

        var refusal = harness.InstanceSourceRefusalForTesting();

        Assert.NotNull(refusal);
        Assert.Contains("service provider", refusal);
        Assert.Contains("factory", refusal);
    }

    /// <summary>
    ///     A capturing factory is refused for the reason a capturing benchmark body is, and the message
    ///     names the fix.
    /// </summary>
    [Fact]
    public void A_Capturing_Scoped_Factory_CannotIsolate()
    {
        var tag = Guid.NewGuid().ToString();

        var harness = BenchmarkHarness.Create([])
            .WithScopedServiceProvider(() => new ServiceCollection()
                .AddSingleton(tag)
                .BuildServiceProvider());

        var refusal = harness.InstanceSourceRefusalForTesting();

        Assert.NotNull(refusal);
        Assert.Contains("captures", refusal);
        Assert.Contains("static method", refusal);
    }

    /// <summary>
    ///     W-17: the coordinator does not build the container at configuration time.
    /// </summary>
    /// <remarks>
    ///     It used to, unconditionally - so a run that would be fully isolated still opened a database
    ///     and constructed an EF model in a process that then measured nothing. For the ASP.NET guide's
    ///     own scenario that is a real connection on every run.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_Container_Is_Not_Built_When_The_Harness_Is_Configured(bool scoped)
    {
        var built = 0;

        IServiceProvider Factory()
        {
            Interlocked.Increment(ref built);

            return BuildServices();
        }

        var harness = BenchmarkHarness.Create([]);

        _ = scoped
            ? harness.WithScopedServiceProvider(Factory)
            : harness.WithServiceProvider(Factory);

        Assert.Equal(0, built);
    }
}

public sealed class ScopedThing;

public class FactoryScopedBenchmark(ScopedThing thing)
{
    [Attributes.Benchmark]
    public int Measure() => thing.GetHashCode();
}

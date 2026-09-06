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
        var harness = BenchmarkHarness.Create([]).WithScopedServices(BuildServices);

        Assert.Null(harness.InstanceSourceRefusalForTesting());
    }

    [Fact]
    public void ScopedServices_Factory_CanIsolate()
    {
        var harness = BenchmarkHarness.Create([])
            .AddFromAssembly<FactoryScopedBenchmark>().WithScopedServices(BuildServices);

        Assert.Null(harness.InstanceSourceRefusalForTesting());
    }

    /// <summary>
    ///     A factory that closes over a local is addressable: what it captures travels with it, and the
    ///     container is still built in the process that measures.
    /// </summary>
    /// <remarks>
    ///     This used to be refused alongside a built provider, which put the two very different things
    ///     under one rule. A container is live code and genuinely cannot cross - which is now a compile
    ///     error rather than a thrown run, since the overload taking one no longer exists (S3) - while a
    ///     connection string the factory reads is a parameter it did not get to declare, and sending it
    ///     leaves the recipe a recipe.
    /// </remarks>
    [Fact]
    public void A_Capturing_Scoped_Factory_CanIsolate()
    {
        var tag = Guid.NewGuid().ToString();

        var harness = BenchmarkHarness.Create([])
            .WithScopedServices(() => new ServiceCollection()
                .AddSingleton(tag)
                .BuildServiceProvider());

        Assert.Null(harness.InstanceSourceRefusalForTesting());
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
            ? harness.WithScopedServices(Factory)
            : harness.WithServices(Factory);

        Assert.Equal(0, built);
    }
}

public sealed class ScopedThing;

public class FactoryScopedBenchmark(ScopedThing thing)
{
    [Benchmark]
    public int Measure() => thing.GetHashCode();
}

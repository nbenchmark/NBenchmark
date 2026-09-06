using Microsoft.AspNetCore.Http;
using NBenchmark;

namespace NBenchmark.Tests.WebFixture;

/// <summary>
///     A benchmark assembly that needs the ASP.NET Core shared framework, so the worker's
///     framework-merging launch path can be tested against the thing it exists for rather than
///     against a synthetic runtimeconfig.
/// </summary>
/// <remarks>
///     <para>
///         The failure this reproduces is not about the benchmark. <c>nbworker</c> declares
///         <c>Microsoft.NETCore.App</c> and nothing else, so a target whose graph reaches
///         <c>Microsoft.AspNetCore.App</c> fails to load in it - the framework's assemblies are on no
///         list the worker's process has, and the resolver correctly declines to find them on disk.
///         The coordinator now extends the worker's framework set from this assembly's
///         <c>runtimeconfig.json</c> before the process starts.
///     </para>
///     <para>
///         An executable rather than a library, because only an application is given a
///         <c>runtimeconfig.json</c>. Nothing ever runs <see cref="Main" />; the worker loads this
///         assembly by path and calls a benchmark on it.
///     </para>
/// </remarks>
public static class Program
{
    public static void Main() => Console.WriteLine("This fixture exists to be loaded, not run.");
}

/// <summary>
///     Benchmarks that genuinely cannot run without the ASP.NET Core shared framework.
/// </summary>
/// <remarks>
///     <para>
///         Needing the framework has to be arranged deliberately, because the runtime resolves types
///         lazily: an assembly full of ASP.NET references loads perfectly well in a worker that has
///         no ASP.NET, right up until something asks for one of those types. A first attempt at this
///         fixture held an unused <c>IHostedService</c> property and measured plain arithmetic - and
///         passed against an unfixed worker, because nothing ever touched the property.
///     </para>
///     <para>
///         So the dependency is placed on the two paths the worker actually walks. <see cref="_path" />
///         is a <i>value</i>-typed field, whose type must be loaded to compute this class's instance
///         layout - so the failure lands where the report described it, on the load rather than on a
///         measurement. <see cref="IsGet" /> then calls into the framework from a measured body, so
///         the coverage does not rest on that one subtlety of type loading.
///     </para>
/// </remarks>
public class WebFixtureBenchmarks
{
    private PathString _path = new("/benchmarks");

    [Benchmark]
    public bool IsGet() => HttpMethods.IsGet("GET");

    [Benchmark]
    public bool HasPath() => _path.HasValue;
}

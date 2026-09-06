using System.Diagnostics.CodeAnalysis;
using NBenchmark.Discovery;

namespace NBenchmark.Engine;

internal static class BenchmarkLifecycle
{
    /// <summary>
    ///     Stands in for the receiver of a static class's benchmarks, which never read it.
    /// </summary>
    private static readonly object StaticClassReceiver = new();

    /// <param name="failure">
    ///     Why the instance could not be created, or <c>null</c> on success. Returned as well as
    ///     printed so a caller can put it on the errored row: the console line scrolls past and is
    ///     absent entirely from every file reporter, which is where a CI reader looks.
    /// </param>
    public static (object Instance, Action InstanceTeardown)? CreateInstance(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type type,
        Func<Type, InstanceHandle>? instanceFactory,
        out string? failure)
    {
        failure = null;

        try
        {
            // A static class (abstract and sealed) has no instance and needs none: every delegate
            // built for its methods ignores the receiver. Trying to construct one throws, which
            // would report a perfectly measurable benchmark as un-instantiable.
            if (type.IsAbstract && type.IsSealed)
                return (StaticClassReceiver, () => { });

            if (instanceFactory is null)
            {
                var instance = Activator.CreateInstance(type);
                return instance is null ? null : (instance, () => { });
            }

            var handle = instanceFactory(type);
            return (handle.Instance, handle.Teardown);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var hint = instanceFactory is null
                ? "the type must have a public parameterless constructor, or be internal with "
                  + "a public constructor and InternalsVisibleTo. To fix: (1) add a parameterless "
                  + "constructor, (2) install NBenchmark.Analyzers for compile-time detection, or "
                  + "(3) call WithInstanceFactory or WithServices on BenchmarkHarness. "
                  + "See https://docs.nbenchmark.net/features/dependency-injection for details. "
                : "the instance factory threw during resolution. ";

            failure = $"Could not instantiate {type.Name} - {hint}Details: {ex.Message}";

            Console.WriteLine($"[Error] {failure}");

            return null;
        }
    }

    [RequiresUnreferencedCode("Harness mode discovers [Benchmark] methods by reflecting over the assembly's types, and the run itself reflects over the discovered members; a trimmed or AOT-compiled app keeps neither.")]
    [RequiresDynamicCode("Harness mode discovers [Benchmark] methods by reflecting over the assembly's types, and the run itself reflects over the discovered members; a trimmed or AOT-compiled app keeps neither.")]
    public static (bool Success, IReadOnlyList<BenchmarkResult>? ErroredResults) TryRunSetup(
        BenchmarkSuiteDefinition suite, object instance, MeasurementOptions suiteOptions)
    {
        try
        {
            suite.SetupDelegate?.Invoke(instance);
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[Error] Setup failed for {suite.Type.Name}: {ex.Message}");
            var qualifiedClassName = BenchmarkEnvelope.QualifiedDiscoveredClassName(suite.Type);

            var errored = suite.Benchmarks
                .Select(b =>
                    OutcomeBuilder.Build(
                        new RunOutcome.Errored(ex, $"Suite setup failed: {ex.Message}"),
                        BenchmarkEnvelope.QualifiedDiscoveredBenchmarkName(suite.Type, b.DisplayName),
                        qualifiedClassName,
                        b.Attribute.Description,
                        b.IsBaseline,
                        suiteOptions, TimeSpan.Zero, TimeSpan.Zero).Result)
                .ToList();

            return (false, errored);
        }
    }

    public static async Task RunTeardown(
        BenchmarkSuiteDefinition suite, object instance,
        bool instanceFromFactory, Action instanceTeardown, Action? postSuiteCleanup)
    {
        try
        {
            suite.TeardownDelegate?.Invoke(instance);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[Warning] Teardown failed for {suite.Type.Name}: {ex.Message}");
        }

        try
        {
            instanceTeardown();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[Warning] Instance teardown failed for {suite.Type.Name}: {ex.Message}");
        }

        postSuiteCleanup?.Invoke();

        if (!instanceFromFactory)
        {
            if (instance is IAsyncDisposable ad)
                await ad.DisposeAsync();
            else if (instance is IDisposable d)
                d.Dispose();
        }
    }
}

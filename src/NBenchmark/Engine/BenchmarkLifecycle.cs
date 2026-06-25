using NBenchmark.Discovery;

namespace NBenchmark.Engine;

internal static class BenchmarkLifecycle
{
    public static (object Instance, Action InstanceTeardown)? CreateInstance(
        Type type, Func<Type, InstanceHandle>? instanceFactory)
    {
        try
        {
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
                   + "(3) call WithInstanceFactory or WithServiceProvider on BenchmarkHarness. "
                  + "See https://www.nbenchmark.net/features/dependency-injection for details. "
                : "the instance factory threw during resolution. ";

            Console.WriteLine($"[Error] Could not instantiate {type.Name} - "
                              + hint
                              + $"Details: {ex.Message}");

            return null;
        }
    }

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

            var errored = suite.Benchmarks
                .Select(b =>
                    OutcomeBuilder.Build(
                        new RunOutcome.Errored(ex, $"Suite setup failed: {ex.Message}"),
                        $"{suite.Type.Name}.{b.DisplayName}", suite.Type.Name, b.Attribute.Description, b.IsBaseline,
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

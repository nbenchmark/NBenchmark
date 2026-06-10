using NBenchmark.Discovery;

namespace NBenchmark.Engine;

internal sealed record BenchmarkEnvelope(
    string Name,
    string? Description,
    bool IsBaseline,
    Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> RunAsync)
{
    public static BenchmarkEnvelope FromDiscovered(
        BenchmarkMethodDefinition method,
        string className,
        object instance)
    {
        var name = $"{className}.{method.DisplayName}";
        var description = method.Attribute.Description;
        var isBaseline = method.Attribute.Baseline;
        var attributeIterations = method.Attribute.Iterations;
        var attributeWarmupIterations = method.Attribute.WarmupIterations;
        var hasIterationsOverride = method.Attribute.HasIterationsOverride;
        var hasWarmupIterationsOverride = method.Attribute.HasWarmupIterationsOverride;
        var iterationSetupDel = method.IterationSetupDelegate;
        var iterationTeardownDel = method.IterationTeardownDelegate;
        var asyncDel = method.AsyncDelegate;
        var syncDel = method.SyncDelegate;
        var resultConsumer = method.ResultConsumer;

        Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> runAsync = (spec, ct) =>
        {
            var specWithOverride = spec;

            if (spec.Options.Iterations > 0)
            {
                var overriddenOptions = spec.Options;

                if (hasIterationsOverride)
                    overriddenOptions = overriddenOptions with { Iterations = attributeIterations };

                if (hasWarmupIterationsOverride)
                    overriddenOptions = overriddenOptions with { WarmupIterations = attributeWarmupIterations };

                specWithOverride = spec with { Options = overriddenOptions };
            }

            var specWithIter = (iterationSetupDel, iterationTeardownDel) switch
            {
                (null, null) => specWithOverride,
                (not null, null) => specWithOverride with
                {
                    IterationSetup = () => iterationSetupDel(instance),
                },
                (null, not null) => specWithOverride with
                {
                    IterationTeardown = () => iterationTeardownDel(instance),
                },
                (not null, not null) => specWithOverride with
                {
                    IterationSetup = () => iterationSetupDel(instance),
                    IterationTeardown = () => iterationTeardownDel(instance),
                },
            };

            return ExecuteAsync(name, asyncDel, syncDel, resultConsumer, instance, specWithIter, ct);
        };

        return new BenchmarkEnvelope(name, description, isBaseline, runAsync);
    }

    private static Task<MeasurementOutcome> ExecuteAsync(
        string name,
        Func<object, Task>? asyncDel,
        Func<object, object?>? syncDel,
        Action<Task>? resultConsumer,
        object instance,
        RunSpec spec,
        CancellationToken ct)
    {
        if (asyncDel is not null)
        {
            if (resultConsumer is not null)
            {
                var returningBody = async () =>
                {
                    var task = asyncDel(instance);
                    await task.ConfigureAwait(false);
                    resultConsumer(task);
                };

                return BenchmarkRunner.Instance.RunAsync(name, returningBody, spec, ct);
            }

            var voidBody = async () =>
            {
                var task = asyncDel(instance);
                await task.ConfigureAwait(false);
            };

            return BenchmarkRunner.Instance.RunAsync(name, voidBody, spec, ct);
        }

        var sd = syncDel!;
        var syncBody = () => sd(instance);
        return Task.FromResult(BenchmarkRunner.Instance.Run(name, syncBody, spec, ct));
    }
}

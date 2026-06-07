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
        var iterationSetupDel = method.IterationSetupDelegate;
        var iterationTeardownDel = method.IterationTeardownDelegate;
        var asyncDel = method.AsyncDelegate;
        var syncDel = method.SyncDelegate;
        var resultExtractor = method.ResultExtractor;

        Func<RunSpec, CancellationToken, Task<MeasurementOutcome>> runAsync = (spec, ct) =>
        {
            var specWithOverride = attributeIterations.HasValue && spec.Options.Iterations > 0
                ? spec with { Options = spec.Options with { Iterations = attributeIterations.Value } }
                : spec;

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

            return ExecuteAsync(name, asyncDel, syncDel, resultExtractor, instance, specWithIter, ct);
        };

        return new BenchmarkEnvelope(name, description, isBaseline, runAsync);
    }

    private static Task<MeasurementOutcome> ExecuteAsync(
        string name,
        Func<object, Task>? asyncDel,
        Func<object, object?>? syncDel,
        Func<Task, object?>? resultExtractor,
        object instance,
        RunSpec spec,
        CancellationToken ct)
    {
        if (asyncDel is not null)
        {
            if (resultExtractor is not null)
            {
                Func<Task<object?>> returningBody = async () =>
                {
                    var task = asyncDel(instance);
                    await task.ConfigureAwait(false);
                    return resultExtractor(task);
                };

                return BenchmarkRunner.Instance.RunAsync(name, returningBody, spec, ct);
            }

            Func<Task> voidBody = async () =>
            {
                var task = asyncDel(instance);
                await task.ConfigureAwait(false);
            };

            return BenchmarkRunner.Instance.RunAsync(name, voidBody, spec, ct);
        }

        var sd = syncDel!;
        Func<object?> syncBody = () => sd(instance);
        return Task.FromResult(BenchmarkRunner.Instance.Run(name, syncBody, spec, ct));
    }
}

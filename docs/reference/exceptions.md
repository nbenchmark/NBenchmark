---
title: Exceptions
description: The exception types NBenchmark raises, and what each one means.
order: 5
---

# Exceptions

Every exception NBenchmark raises deliberately derives from `BenchmarkException`. Catch that base type to tell a failure the library refused from one thrown by the code you are measuring.

```csharp
try
{
    await suite.RunAsync();
}
catch (BenchmarkIsolationException ex)
{
    // The run was refused because it could not be measured in a worker process.
    Console.Error.WriteLine($"{ex.Status}: {ex.Remedy}");
}
catch (BenchmarkException ex)
{
    // Anything else NBenchmark refused.
    Console.Error.WriteLine(ex.Message);
}
```

## The hierarchy

| Type | Raised when |
| --- | --- |
| `BenchmarkException` | The base type. Never thrown directly. |
| `BenchmarkConfigurationException` | The benchmark definition cannot produce a measurement: a `[BenchmarkPlan]` that is not static, `WithParameter` values that do not match the parameterized bodies, a baseline name no benchmark carries, a delegate shape the engine cannot measure, a duplicate reporter or observer name. |
| `BenchmarkIsolationException` | Isolation is required and a benchmark could not be measured in a worker process. |
| `BenchmarkExecutionException` | A correctly configured run could not be carried out: a worker died mid-run, a protocol frame exceeded the transport ceiling, an auto-tune cap was hit under `AutoTuneCapBehavior.Error`. |
| `WorkerStartException` | A worker process could not be started or could not be trusted. Callers treat this as "fall back and say why", never as a reason to report a measurement. |

A configuration failure is deterministic: the same program fails the same way on every run until the definition changes. An execution failure depends on the machine and the run.

## Isolation refusals carry their remedy

`BenchmarkIsolationException` carries the refusal as data, so a test adapter or a CI reporter can act on it without parsing the message:

- `Status` is the `IsolationStatus` naming why the measurement did not happen in a worker. When several benchmarks are refused at once, it is the first offender's and the message lists every one.
- `Remedy` is what to change so the benchmark can be isolated, or `null` when the status has no remedy - the host process was asked for deliberately, for example.

Both also appear in `Message`. For the statuses and their remedies, see [Isolated runs](../features/isolated-runs.md).

## What is not a `BenchmarkException`

- **Argument validation.** Options records and builder methods throw `ArgumentOutOfRangeException` or `ArgumentException` from their initializers. Those report a bad call rather than a refused run, and they fail at the point of configuration instead of deep in a measurement.
- **A benchmark body that throws.** The body's exception is captured onto the result (`BenchmarkResult.Errored` and `BenchmarkResult.ErrorMessage`) so the rest of the suite still runs and the report still names the failure. Nothing is thrown at the caller.
- **A misbehaving reporter or auto-attached observer.** Failures there are traced through `System.Diagnostics.Trace` and skipped, so a broken plugin cannot lose a measured run.

## See also

- [Isolated runs](../features/isolated-runs.md) - Isolation statuses, refusals, and remedies.
- [Troubleshooting](../troubleshooting.md) - Symptom-first guide to common failures.
- [Configuration](./configuration.md) - The `MeasurementOptions` surface and its validation.

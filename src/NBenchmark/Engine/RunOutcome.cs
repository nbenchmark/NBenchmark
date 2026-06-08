namespace NBenchmark.Engine;

/// <summary>
///     Discriminated input for <see cref="OutcomeBuilder" />. One of three sealed cases:
///     <see cref="Success" /> for a measured run, <see cref="DryRun" /> for a
///     configuration pass that did not invoke the body, or <see cref="Errored" /> for
///     an exception that aborted the run.
/// </summary>
internal abstract record RunOutcome
{
    public sealed record Success(ProcessedMeasurements Result, double[] RawSamples) : RunOutcome;

    public sealed record DryRun : RunOutcome;

    public sealed record Errored(Exception Error, string? ErrorMessageOverride = null) : RunOutcome;
}

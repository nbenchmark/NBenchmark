namespace NBenchmark.Engine;

/// <summary>
///     Discriminated input for <see cref="OutcomeBuilder" />. One of three sealed cases:
///     <see cref="Success" /> for a measured run, <see cref="DryRun" /> for a
///     configuration pass that did not invoke the body, or <see cref="Errored" /> for
///     an exception that aborted the run.
/// </summary>
internal abstract record OutcomeInput
{
    public sealed record Success(PipelineResult Result, double[] RawTimings) : OutcomeInput;

    public sealed record DryRun : OutcomeInput;

    public sealed record Errored(Exception Error, string? ErrorMessageOverride = null) : OutcomeInput;
}

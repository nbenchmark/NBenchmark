namespace NBenchmark;

/// <summary>
///     The setup warnings the engine emits before or during a run, as a set that can be suppressed
///     through <see cref="MeasurementOptions.SuppressedWarnings" />.
/// </summary>
/// <remarks>
///     <para>
///         Every one of these reports a condition that makes the numbers less trustworthy but never
///         makes them impossible to produce - the engine warns and proceeds, it never refuses. A
///         suppression therefore says "I know, and I meant it", which is why they are collected in
///         one set rather than spread across a boolean per warning.
///     </para>
///     <para>
///         Suppressing a warning does not change what the engine measures - only whether it says so.
///     </para>
/// </remarks>
[Flags]
public enum BenchmarkWarnings
{
    /// <summary>No warning is suppressed. This is the default.</summary>
    None = 0,

    /// <summary>
    ///     The entry assembly was built in a non-Release configuration, or a debugger is attached.
    ///     Suppress this only when measuring that build is the point.
    /// </summary>
    BuildConfiguration = 1 << 0,

    /// <summary>
    ///     A <c>[InstanceLifetime(InstanceLifetime.PerClass)]</c> class shares one instance across
    ///     its <c>[Benchmark]</c> methods without resetting between them. Prefer
    ///     <c>[SharedState]</c> on the class, which records the intent where a reader will find it;
    ///     this flag silences the warning for the whole run.
    /// </summary>
    PerClassIndependence = 1 << 1,

    /// <summary>
    ///     The process was started without the environment variables the requested runtime profile
    ///     needs, so the profile could not be applied to this process.
    /// </summary>
    RuntimeProfile = 1 << 2,
}

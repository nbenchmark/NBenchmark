namespace NBenchmark.Reporters;

/// <summary>
///     What a reporter is being asked to report: the detail level, where the output goes, and when
///     the run started.
/// </summary>
/// <remarks>
///     <para>
///         Detail is a property of the <i>run</i>, not of the reporter object. It used to be a settable
///         property on <see cref="IReporter" /> that the builders wrote behind the caller's back, so
///         a reporter constructed at one detail level and then attached to a builder set to another
///         silently became the builder's. Passing it in makes the reporter stateless with respect to the run, and gives
///         the output directory, the file name and the run timestamp somewhere to live that is not a
///         constructor parameter on every implementation.
///     </para>
///     <para>
///         The path members are the run's defaults, not orders: a reporter constructed with its own
///         directory or file name keeps them, and reads these when it was given neither.
///     </para>
/// </remarks>
/// <param name="Detail">How much of each result to report.</param>
public sealed record ReportContext(ReportDetail Detail)
{
    /// <summary>
    ///     A context for a default-detail run with no output directory, file name or pinned start
    ///     time. A property rather than a field because <see cref="StartedUtc" /> is stamped per
    ///     instance - a shared singleton would hand every run in a process the same timestamp, and
    ///     that timestamp is what the file reporters name their output after.
    /// </summary>
    public static ReportContext Default => new(ReportDetail.Simple);

    /// <summary>
    ///     Where a reporter that writes files should write them, when it was not constructed with a
    ///     directory of its own. <c>null</c> leaves the choice to the reporter.
    /// </summary>
    public string? OutputDirectory { get; init; }

    /// <summary>
    ///     The file name a reporter that writes one file should use, when it was not constructed with
    ///     one. <c>null</c> leaves the reporter to generate a name.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    ///     When <c>true</c>, the run asked for output without colour or styling - <c>--no-color</c>, or
    ///     a non-empty <c>NO_COLOR</c> environment variable (https://no-color.org). A reporter that
    ///     colours its output should honour it; one that writes plain text can ignore it.
    /// </summary>
    public bool NoColor { get; init; }

    /// <summary>
    ///     When the run this reports on started. Reporters that generate a file name stamp it with
    ///     this, so every file written for one run carries the same timestamp.
    /// </summary>
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
}

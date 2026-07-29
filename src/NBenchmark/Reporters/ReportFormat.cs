namespace NBenchmark.Reporters;

/// <summary>
///     The version stamps every report carries, so a consumer storing NBenchmark output over time
///     can tell whether two files may be compared.
/// </summary>
/// <remarks>
///     <para>
///         Two numbers, because two independent things can change and a consumer cares about them
///         for different reasons. <see cref="SchemaVersion" /> says whether a file can still be
///         <i>parsed</i>. <see cref="MeasurementEpoch" /> says whether its numbers can still be
///         <i>compared</i>. Conflating them means either silently breaking parsers or silently
///         plotting a step change as a regression.
///     </para>
///     <para>
///         NBenchmark does not read its own reports back - nothing here is used to gate an internal
///         comparison. These stamps exist entirely for whoever stores the files: a CI trend
///         dashboard, a regression script, a spreadsheet. They are the only way such a consumer can
///         learn that a jump in its chart was the harness changing rather than the code.
///     </para>
/// </remarks>
public static class ReportFormat
{
    /// <summary>
    ///     The shape of the report: field names, nesting, and types. Bump when a consumer that
    ///     parsed the previous version would now fail or silently read the wrong field.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Bump for: renaming or removing a field, changing a field's type, restructuring the
    ///         envelope. Do <b>not</b> bump for adding an optional field - a consumer that ignores
    ///         unknown fields is unaffected, and bumping for additions trains consumers to ignore
    ///         the version.
    ///     </para>
    ///     <para>
    ///         History: <b>1</b> - first declared version, adding <c>schemaVersion</c> and
    ///         <c>measurementEpoch</c> to the envelope. Files written before this carry neither
    ///         field.
    ///     </para>
    /// </remarks>
    public const int SchemaVersion = 1;

    /// <summary>
    ///     The comparability of the numbers. Bump when a change to NBenchmark itself moves what a
    ///     benchmark reports, so that two files with different epochs must not be plotted on one
    ///     axis or diffed by a regression gate.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the stamp with no natural home elsewhere, and the one a schema version cannot
    ///         stand in for. The change that motivated it - replacing a boxing dispatch path with
    ///         typed delegates - altered the schema not at all and moved the calibration standard
    ///         from 9.34 ns / 24 B per op to 2.53 ns / 0 B. Every stored baseline became
    ///         incomparable, and every file describing that fact looked identical to one that did
    ///         not.
    ///     </para>
    ///     <para>
    ///         Bump when: harness overhead changes (dispatch, allocation measurement, the timing
    ///         loop); the default runtime profile or its knobs change; the definition of a reported
    ///         statistic changes (what counts as an outlier, how ns/op is derived). Do <b>not</b>
    ///         bump for: new fields, reporter formatting, or fixes that leave the numbers where they
    ///         were.
    ///     </para>
    ///     <para>
    ///         An absent stamp is not epoch 0 - it means the file predates the concept, and nothing
    ///         is known about its comparability. Consumers should reject such files rather than
    ///         assume them equivalent to the earliest declared epoch.
    ///     </para>
    ///     <para>
    ///         History: <b>1</b> - first declared epoch. Monomorphic dispatch (no per-op boxing for
    ///         value-returning benchmarks), suites isolated in worker processes by default under the
    ///         <c>steady-state</c> runtime profile.
    ///     </para>
    /// </remarks>
    public const int MeasurementEpoch = 1;
}

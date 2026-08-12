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
    ///         History:
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///                 First declared epoch. Monomorphic dispatch (no per-op boxing for
    ///                 value-returning benchmarks), suites isolated in worker processes by default
    ///                 under the <c>steady-state</c> runtime profile.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 The multi-launch reporting overhaul. Three definitional changes to reported
    ///                 statistics, all of which move stored numbers:
    ///                 <list type="bullet">
    ///                     <item>
    ///                         <description>
    ///                             A multi-launch benchmark reports the <i>average</i> of its launches
    ///                             rather than the fastest of them, so medians rise for every benchmark
    ///                             measured with <c>LaunchCount &gt; 1</c> - the Harness default.
    ///                         </description>
    ///                     </item>
    ///                     <item>
    ///                         <description>
    ///                             Its interval comes from the spread <i>between</i> launches rather
    ///                             than from within one, so intervals widen wherever between-worker
    ///                             spread is real. On this repository's own sample the in-process row
    ///                             moved from 1.66 ns with a sub-nanosecond interval to 3.20 ns ±3.42,
    ///                             which is the honest description of three launches reading 4.32, 3.63
    ///                             and 1.66.
    ///                         </description>
    ///                     </item>
    ///                     <item>
    ///                         <description>
    ///                             The ratio is the geometric mean of the <i>per-launch</i> ratios
    ///                             rather than the quotient of two aggregated medians. The two differ
    ///                             whenever the launches disagree, which is the case the pairing exists
    ///                             for. <c>--threshold-pct</c> gates on the paired value, so a gate can
    ///                             change verdict on unchanged code.
    ///                         </description>
    ///                     </item>
    ///                 </list>
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <term>3</term>
    ///             <description>
    ///                 Shapes that previously fell back to the host process are now measured in a
    ///                 worker: parameter sweeps, suite and per-iteration lifecycle, custom statistical
    ///                 strategies built with constructor arguments, and DI-resolved benchmark instances.
    ///                 <para>
    ///                     No statistic was redefined and no harness overhead changed - a row that was
    ///                     already isolated reports the same number as under epoch 2. But a row that was
    ///                     <i>not</i> moves by however much the host's JIT tiering was worth to it, which
    ///                     on bodies of provably identical cost was up to 3.3x. That is a change of
    ///                     measurement regime for those rows, which is exactly what this counter exists to
    ///                     announce; a stored baseline covering any of them is not comparable across it.
    ///                 </para>
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <term>4</term>
    ///             <description>
    ///                 Clock-resolution-derived sample sizing, and a higher Harness launch default.
    ///                 <list type="bullet">
    ///                     <item>
    ///                         <description>
    ///                             Ops-per-sample calibration now resolves against a target raised to span
    ///                             at least <c>AutoTuneOptions.MinQuantaPerSample</c> steps of the clock's
    ///                             <i>measured</i> resolution, rather than a fixed 10 µs. On a host whose
    ///                             clock is coarse relative to that target - Apple Silicon at 41.667 ns,
    ///                             Windows QPC at 100 ns - <c>K</c> rises and each sample spans more work,
    ///                             which changes reported per-op numbers slightly (fixed timer overhead is
    ///                             amortised over more operations, so the figure generally improves) and
    ///                             changes the sample count that reaches the statistics. A TSC-backed Linux
    ///                             host already cleared the floor and is unaffected.
    ///                         </description>
    ///                     </item>
    ///                     <item>
    ///                         <description>
    ///                             <c>LaunchCounts.HarnessDefault</c> is 5 rather than 3. Since epoch 2 a
    ///                             multi-launch row reports the average of its launches with a
    ///                             between-launch interval, so changing the replicate count changes both
    ///                             the reported median (a mean over five draws rather than three) and the
    ///                             interval width (Student-t on 4 degrees of freedom rather than 2, a 35%
    ///                             narrower critical value on the same spread).
    ///                         </description>
    ///                     </item>
    ///                 </list>
    ///                 <para>
    ///                     Neither change alters what a statistic <i>means</i>, but both move stored
    ///                     numbers on most hosts, and the second moves the interval a
    ///                     <c>--threshold-pct</c> gate reads.
    ///                 </para>
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    public const int MeasurementEpoch = 4;
}

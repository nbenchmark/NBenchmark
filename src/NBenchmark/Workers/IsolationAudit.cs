using System.Globalization;
using System.Text;

namespace NBenchmark.Workers;

/// <summary>
///     One refused benchmark, class or suite: what it was called, how it was refused, and the
///     explanation to quote back.
/// </summary>
internal readonly record struct IsolationRefusal(string Name, IsolationStatus Status, string? Explanation);

/// <summary>
///     What happens when isolation was refused, and the two commands that make isolation checkable
///     rather than merely claimed.
///     <para>
///         <see cref="ThrowIfRequired(MeasurementOptions, string, IsolationStatus, string?)" /> is the
///         gate itself, on by default. <c>--strict-isolation</c> turns a label into an exit code, and
///         <c>--verify-isolation</c> turns the argument for isolating into a measurement on the user's
///         own code.
///     </para>
/// </summary>
internal static class IsolationAudit
{
    /// <summary>
    ///     Throws when <see cref="MeasurementOptions.Isolation" /> is set and isolation was
    ///     <b>refused</b>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The library-side counterpart to <see cref="Enforce" />, and deliberately a different
    ///         mechanism rather than the same one. <c>--strict-isolation</c> audits <i>results</i>, which
    ///         is right for a CLI: it can name every offender in one report and set an exit code CI reads.
    ///         A library caller has no exit code, so waiting until the run finished would mean measuring
    ///         everything first and then discarding it. This throws at the point of refusal instead,
    ///         before any work is done.
    ///     </para>
    ///     <para>
    ///         Keyed on <see cref="IsolationStatusExtensions.IsRefusal" />, never on
    ///         <c>!IsIsolated()</c>. <c>--dry-run</c>, <c>--in-process</c>, <c>[Isolation(Isolation.Off)]</c>,
    ///         <c>Benchmark.RunInProcess</c>, <c>WithIsolation(Isolation.Off)</c> and
    ///         <c>BenchmarkSuite.AddInProcess</c> all produce
    ///         <see cref="IsolationStatus.InProcessRequested" /> and must stay legal - the whole point of
    ///         the default being on is that asking for the host process is still a thing you can do.
    ///     </para>
    ///     <para>
    ///         The message carries the refusal verbatim. A "strict isolation was required" exception with
    ///         no cause would send the reader back to a stderr line that, in this mode, was never printed.
    ///     </para>
    /// </remarks>
    public static void ThrowIfRequired(
        MeasurementOptions options,
        string name,
        IsolationStatus status,
        string? refusal)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.RequiresIsolation || !status.IsRefusal())
            return;

        throw new InvalidOperationException(Explain(name, status, refusal));
    }

    /// <summary>
    ///     The text of a hard isolation failure: what was refused, why, the remedy, and - see
    ///     <see cref="OptOut" /> - the deliberate opt-out.
    /// </summary>
    internal static string Explain(string name, IsolationStatus status, string? refusal)
    {
        var remedy = status.ToRemedy() is { } text ? $" To isolate it: {text}." : "";

        return $"'{name}' could not be measured in an isolated worker, and isolation is required. "
               + $"It was refused because {refusal ?? UnaddressableFallback}"
               + remedy
               + OptOut;
    }

    /// <summary>
    ///     Throws when <see cref="MeasurementOptions.Isolation" /> is set and anything in
    ///     <paramref name="refusals" /> was refused - reporting all of them in one message.
    /// </summary>
    /// <remarks>
    ///     Harness mode's form of the gate, and the reason it is a list rather than a loop over the
    ///     single-name overload. Isolatability is decided for every discovered class in one pass before
    ///     the first benchmark runs, so a run with three un-isolatable classes can say so once. Throwing
    ///     on the first would report class 1 and leave classes 2 and 3 to be discovered on the next run,
    ///     one per attempt.
    /// </remarks>
    public static void ThrowIfRequired(MeasurementOptions options, IReadOnlyList<IsolationRefusal> refusals)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(refusals);

        if (!options.RequiresIsolation)
            return;

        var offenders = refusals.Where(r => r.Status.IsRefusal()).ToList();

        if (offenders.Count == 0)
            return;

        if (offenders.Count == 1)
            throw new InvalidOperationException(Explain(offenders[0].Name, offenders[0].Status, offenders[0].Explanation));

        var message = new StringBuilder();

        message.AppendLine(CultureInfo.InvariantCulture,
            $"{offenders.Count} benchmark groups could not be measured in an isolated worker, and "
            + $"isolation is required. Nothing has been measured.");

        foreach (var offender in offenders)
        {
            message.AppendLine(CultureInfo.InvariantCulture,
                $"  '{offender.Name}': {offender.Explanation ?? UnaddressableFallback}");

            if (offender.Status.ToRemedy() is { } remedy)
                message.AppendLine(CultureInfo.InvariantCulture, $"    To isolate it: {remedy}.");
        }

        message.Append(OptOut.TrimStart());

        throw new InvalidOperationException(message.ToString());
    }

    private const string UnaddressableFallback = "it could not be addressed across a process boundary.";

    /// <summary>
    ///     Named in the message rather than left to the docs, because this gate is now the default: the
    ///     first time most users meet it is as a thrown exception on a run that used to produce numbers.
    ///     A message that only says "no" turns a labelled fallback into a dead end; naming the opt-outs
    ///     keeps the fallback available to anyone who wants it, which is the whole shape of the decision
    ///     - in-process becomes something you ask for rather than something that happens to you.
    /// </summary>
    private const string OptOut =
        " To measure in this process deliberately, use [Isolation(Isolation.Off)] (Harness mode), "
        + "Benchmark.RunInProcess (Single mode), or BenchmarkSuite.AddInProcess / "
        + "WithIsolation(Isolation.Off) (Suite mode) - or set Isolation = Isolation.Preferred to accept "
        + "labelled fallbacks everywhere.";

    /// <summary>
    ///     Fails the run when isolation was refused for any result, naming each one and what to do
    ///     about it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every refused result is already labelled on the row and explained on the console. This
    ///         exists because neither survives CI: a label scrolls past, and an advisory warning in a log
    ///         nobody reads is indistinguishable from no warning at all. A build that must not accept
    ///         host-process numbers needs them to be an error.
    ///     </para>
    ///     <para>
    ///         Keyed on <see cref="IsolationStatusExtensions.IsRefusal" /> rather than
    ///         <c>!IsIsolated()</c>. The old rule failed <c>--strict-isolation --dry-run</c>, and every
    ///         other combination where the user asked for the host process and got it - the flag is
    ///         "fail if isolation was refused", not "fail if you did not isolate", and a run that never
    ///         intended to isolate anything has nothing to act on.
    ///     </para>
    /// </remarks>
    /// <returns><c>true</c> when nothing was refused.</returns>
    public static bool Enforce(IReadOnlyList<BenchmarkResult> results, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(error);

        // Errored rows are excluded on purpose. A benchmark that threw was not measured in this
        // process - it was not measured anywhere - so counting it as an offender tells the user their
        // numbers carry the host's configuration when there are no numbers. Its own error message is
        // the thing to act on, and it is already on the row.
        var offenders = results
            .Where(r => !r.Errored && r.IsolationStatus.IsRefusal())
            .ToList();

        if (offenders.Count == 0)
            return true;

        // Denominator matches the numerator's population: counting an errored row in "3 of 10" while
        // excluding it from the three makes the two numbers describe different sets.
        var measured = results.Count(r => !r.Errored);

        error.WriteLine(
            $"--strict-isolation: {offenders.Count} of {measured} measured benchmark(s) ran in "
            + "this process rather than an isolated worker, so their numbers carry the host's JIT and "
            + "GC configuration.");

        // Grouped by reason, because one cause usually explains many rows and each cause has a
        // different remedy. A flat list of twenty names with the same fix repeated twenty times is
        // harder to act on than three grouped lines.
        foreach (var group in offenders.GroupBy(r => r.IsolationStatus))
        {
            var names = group.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal);

            error.WriteLine($"  {group.Key.ToLabel()}: {string.Join(", ", names)}");

            if (group.Key.ToRemedy() is { } remedy)
                error.WriteLine($"    {remedy}");
        }

        return false;
    }

    /// <summary>
    ///     Renders the isolated-versus-host comparison: the same benchmarks, measured both ways.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately reports the <i>ratio</i> per benchmark rather than an aggregate verdict.
    ///         The failure mode being demonstrated is not that host measurements are uniformly slower
    ///         - it is that they are <i>unpredictable</i>, so one row reading 21x beside another
    ///         reading 1.0x is the finding, and averaging them away would destroy it.
    ///     </para>
    ///     <para>
    ///         Benchmarks that could not be isolated in the first place are shown as such rather than
    ///         compared against themselves, which would print a meaningless 1.00x.
    ///     </para>
    /// </remarks>
    /// <summary>
    ///     Says why <c>--verify-isolation</c> has nothing to offer a cross-runtime run.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This process is one runtime. A run that measured net8.0 and net9.0 builds in their own
    ///         workers has no in-process counterpart here to compare against, and "in-process" has no
    ///         defined meaning for a net8.0 build measured from a net10.0 coordinator.
    ///     </para>
    ///     <para>
    ///         Refusing beats the alternative. <see cref="Render" /> keys the host side by name, and a
    ///         moniker is the only thing distinguishing multi-runtime rows, so every runtime would be
    ///         compared against the same unlabelled host row - a table that looks like a finding and is
    ///         not one. Printing the reason is the same trade the rest of this area makes: refuse rather
    ///         than guess, and say which.
    ///     </para>
    /// </remarks>
    public static void RefuseCrossRuntimeComparison(IEnumerable<string> runtimes, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(output);

        var names = string.Join(", ", runtimes);

        output.WriteLine();

        output.WriteLine(
            "--verify-isolation: skipped, because this run measured more than one runtime"
            + (names.Length == 0 ? "" : $" ({names})") + ".");

        output.WriteLine(
            "  The comparison re-measures in this process, and this process is one runtime - so there "
            + "is no in-process counterpart for the other builds to be compared against. Re-run without "
            + "--runtimes to compare this build's isolated numbers against in-process ones.");
    }

    public static void Render(
        IReadOnlyList<BenchmarkResult> isolated,
        IReadOnlyList<BenchmarkResult> inProcess,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(isolated);
        ArgumentNullException.ThrowIfNull(inProcess);
        ArgumentNullException.ThrowIfNull(output);

        // The host side is keyed by name alone, so a cross-runtime run - where rows share a name and
        // differ only by moniker - would compare every runtime against one unlabelled host row. Guarded
        // here as well as at the call site, so no future caller can produce that table by accident.
        if (isolated.Any(r => !string.IsNullOrEmpty(r.RuntimeMoniker)))
        {
            RefuseCrossRuntimeComparison(
                isolated.Select(r => r.RuntimeMoniker).Where(m => !string.IsNullOrEmpty(m)).Distinct()!,
                output);

            return;
        }

        var hostByName = inProcess.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var rows = new List<(string Name, string Isolated, string Host, string Ratio, double? Magnitude)>();

        foreach (var reference in isolated)
        {
            if (!hostByName.TryGetValue(reference.Name, out var host))
                continue;

            if (!reference.IsolationStatus.IsIsolated())
            {
                rows.Add((reference.Name, "-", Format(host.Median), "not isolated", null));
                continue;
            }

            if (reference.Errored || host.Errored || reference.Median <= 0)
            {
                rows.Add((reference.Name, Format(reference.Median), Format(host.Median), "-", null));
                continue;
            }

            var ratio = host.Median / reference.Median;

            rows.Add((
                reference.Name,
                Format(reference.Median),
                Format(host.Median),
                ratio.ToString("0.00", CultureInfo.InvariantCulture) + "x",

                // Distance from 1.0 in either direction: a host reading half the isolated one is
                // just as wrong as one at double, and sorting by the raw ratio would bury the former.
                Math.Max(ratio, 1 / ratio)));
        }

        if (rows.Count == 0)
        {
            output.WriteLine("--verify-isolation: nothing to compare.");
            return;
        }

        var nameWidth = Math.Max(9, rows.Max(r => r.Name.Length));

        output.WriteLine();
        output.WriteLine("Isolation verification - the same benchmarks measured both ways:");
        output.WriteLine();
        output.WriteLine($"  {"Benchmark".PadRight(nameWidth)}  {"Isolated",12}  {"In-process",12}  Difference");
        output.WriteLine($"  {new string('-', nameWidth)}  {new string('-', 12)}  {new string('-', 12)}  {new string('-', 10)}");

        foreach (var row in rows.OrderByDescending(r => r.Magnitude ?? 0))
        {
            output.WriteLine($"  {row.Name.PadRight(nameWidth)}  {row.Isolated,12}  {row.Host,12}  {row.Ratio}");
        }

        output.WriteLine();

        // Materialized and length-checked rather than fed straight to MaxBy: on an empty sequence
        // MaxBy throws for a non-nullable value type like this tuple, and a run where nothing could
        // be isolated produces exactly that - the case the comparison is most needed for.
        var comparable = rows.Where(r => r.Magnitude is not null).ToList();

        if (comparable.Count == 0)
        {
            output.WriteLine(
                "  No benchmark was isolated, so there is nothing to compare against. The reasons are "
                + "listed above each result.");

            output.WriteLine();

            return;
        }

        var worst = comparable.MaxBy(r => r.Magnitude);

        if (worst.Magnitude > 1.5)
        {
            output.WriteLine(
                $"  In-process measurement was off by up to {worst.Magnitude:0.0}x on '{worst.Name}', "
                + "and reported a confidence interval as though it were not.");
        }
        else
        {
            // Worth saying explicitly. A user who runs this and sees agreement should be told their
            // in-process numbers were fine for this workload, not left to guess whether the check
            // ran at all.
            output.WriteLine(
                "  The two agree closely here, so this workload is not sensitive to the host's "
                + "runtime configuration. That is a property of these benchmarks, not a general one.");
        }

        output.WriteLine();
    }

    private static string Format(double nanoseconds) => nanoseconds switch
    {
        <= 0 => "-",
        < 1_000 => $"{nanoseconds:0.##} ns",
        < 1_000_000 => $"{nanoseconds / 1_000:0.##} µs",
        _ => $"{nanoseconds / 1_000_000:0.##} ms",
    };
}

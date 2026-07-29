using System.Globalization;

namespace NBenchmark.Workers;

/// <summary>
///     The two commands that make isolation checkable rather than merely claimed:
///     <c>--strict-isolation</c> turns a label into an exit code, and <c>--verify-isolation</c> turns
///     the argument for isolating into a measurement on the user's own code.
/// </summary>
internal static class IsolationAudit
{
    /// <summary>
    ///     Fails the run when any result was not measured in a worker, naming each one and what to do
    ///     about it.
    /// </summary>
    /// <remarks>
    ///     Every non-isolated result is already labelled on the row and explained on the console. This
    ///     exists because neither survives CI: a label scrolls past, and an advisory warning in a log
    ///     nobody reads is indistinguishable from no warning at all. A build that must not accept
    ///     host-process numbers needs them to be an error.
    /// </remarks>
    /// <returns><c>true</c> when every result was isolated.</returns>
    public static bool Enforce(IReadOnlyList<BenchmarkResult> results, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(error);

        var offenders = results.Where(r => !r.IsolationStatus.IsIsolated()).ToList();

        if (offenders.Count == 0)
            return true;

        error.WriteLine(
            $"--strict-isolation: {offenders.Count} of {results.Count} benchmark(s) were measured in "
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
    public static void Render(
        IReadOnlyList<BenchmarkResult> isolated,
        IReadOnlyList<BenchmarkResult> inProcess,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(isolated);
        ArgumentNullException.ThrowIfNull(inProcess);
        ArgumentNullException.ThrowIfNull(output);

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

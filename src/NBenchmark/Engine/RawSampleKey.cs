namespace NBenchmark.Engine;

/// <summary>
///     The single source of truth for the key that pairs a <see cref="BenchmarkResult" />
///     with its raw pre-trim samples. Significance testing keys samples by benchmark name,
///     but a multi-runtime run produces one result per <see cref="BenchmarkResult.TargetFramework" />
///     under the same name, so the key must carry both.
///     <para>
///         This type exists because the format was previously inlined at nine call sites
///         across <c>BenchmarkHarness</c> and <c>BenchmarkSuite</c>,
///         and two of them disagreed about whether a dictionary was keyed by plain name or by
///         the composite key. The result was that every isolated Harness child returned zero
///         raw samples and significance testing silently produced nothing in the library's
///         default mode. Keeping exactly one formatter means a mismatch is a compile error
///         rather than an empty lookup.
///     </para>
/// </summary>
internal static class RawSampleKey
{
    /// <summary>The composite sample key for a completed result.</summary>
    public static string For(BenchmarkResult result) => For(result.Name, result.TargetFramework);

    /// <summary>
    ///     The composite sample key for a benchmark name and runtime moniker. <c>\0</c> is the
    ///     separator because it cannot occur in either component, so the key is unambiguous
    ///     even for a benchmark name containing punctuation.
    /// </summary>
    public static string For(string name, string runtimeMoniker) => $"{name}\0{runtimeMoniker}";

    /// <summary>
    ///     Re-keys a name-keyed sample dictionary - the shape
    ///     <see cref="SuiteRunner.RunAsync" /> returns - into the composite key space, using
    ///     each result's runtime moniker. Results with no matching samples are omitted.
    /// </summary>
    public static Dictionary<string, double[]> ToComposite(
        IReadOnlyList<BenchmarkResult> results,
        IReadOnlyDictionary<string, double[]> nameKeyedSamples)
    {
        var composite = new Dictionary<string, double[]>(nameKeyedSamples.Count);

        foreach (var result in results)
        {
            if (nameKeyedSamples.TryGetValue(result.Name, out var samples))
                composite[For(result)] = samples;
        }

        return composite;
    }
}

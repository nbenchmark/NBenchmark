namespace NBenchmark.Engine;

/// <summary>
///     Applies <see cref="RunOrder" /> to a list of things about to be measured.
/// </summary>
/// <remarks>
///     <para>
///         Randomizing execution order turns "the second benchmark ran on a warmer cache" from a
///         fixed confound into a nuisance factor that averages out across replicates. That only works
///         if every path that runs benchmarks actually applies it - and the shuffle had been written
///         out by hand in four places (the harness, the suite, the shared group executor, and the
///         per-suite loop), which is how the worker path came to run in declaration order regardless
///         of what the caller asked for.
///     </para>
///     <para>
///         The seed is threaded through rather than drawn here so a replicate's order is reproducible
///         from the session seed. <c>null</c> means "no seed was pinned", in which case each call
///         draws its own and the run is deliberately not reproducible.
///     </para>
/// </remarks>
internal static class RunOrdering
{
    /// <summary>
    ///     Returns <paramref name="items" /> in execution order: unchanged under
    ///     <see cref="RunOrder.Declaration" />, shuffled under <see cref="RunOrder.Random" />.
    /// </summary>
    public static List<T> Apply<T>(IReadOnlyList<T> items, RunOrder order, int? seed)
    {
        ArgumentNullException.ThrowIfNull(items);

        var list = items.ToList();

        if (order != RunOrder.Random || list.Count < 2)
            return list;

        Shuffle(list, new Random(seed ?? Random.Shared.Next()));

        return list;
    }

    /// <summary>
    ///     Groups <paramref name="items" /> by <paramref name="groupKey" /> and shuffles within each
    ///     group, keeping the groups themselves in first-appearance order.
    /// </summary>
    /// <remarks>
    ///     This is what a parameter sweep needs: the reader expects the parameter values to appear in
    ///     the order they were declared, and randomizing across them would interleave a table that is
    ///     read as a progression. Randomizing <i>within</i> a parameter value still removes the order
    ///     effect from every comparison that is actually made, because those are all within-group.
    /// </remarks>
    public static List<T> ApplyWithinGroups<T, TKey>(
        IReadOnlyList<T> items,
        RunOrder order,
        int? seed,
        Func<T, TKey> groupKey)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(groupKey);

        if (order != RunOrder.Random || items.Count < 2)
            return items.ToList();

        // One RNG seeding the per-group RNGs, so the whole arrangement follows from the session seed
        // rather than every group sharing one sequence position.
        var groupSeeds = new Random(seed ?? Random.Shared.Next());
        var ordered = new List<T>(items.Count);

        foreach (var group in items.GroupBy(groupKey))
        {
            var members = group.ToList();
            Shuffle(members, new Random(groupSeeds.Next()));
            ordered.AddRange(members);
        }

        return ordered;
    }

    private static void Shuffle<T>(List<T> items, Random rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

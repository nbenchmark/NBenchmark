namespace NBenchmark.Attributes;

/// <summary>
///     Declares that this type's measured behaviour is fully determined by its serialized contents,
///     so a value of it captured by a benchmark can be sent to the measuring process.
/// </summary>
/// <remarks>
///     <para>
///         Benchmarks are measured in a separate process by default, so a lambda that closes over a
///         value has to get that value across a boundary. NBenchmark transfers a closed set of types
///         whose behaviour it can verify - primitives, strings, arrays, the standard collections when
///         they carry a default comparer - and refuses everything else rather than guess. This
///         attribute is how you extend that set to a type of your own.
///     </para>
///     <para>
///         <b>It is an assertion, and NBenchmark cannot check it.</b> What you are claiming is not
///         that the type round-trips - most types do - but that nothing about how it performs is
///         carried outside its serialized data. A type holding an open file handle, a warmed cache, a
///         pooled buffer, a custom comparer or a lazily-computed field does not qualify: it would
///         arrive intact and measure differently, which is the one failure a benchmark must not have.
///     </para>
///     <para>
///         When in doubt, do not use this. Naming the preparation costs one delegate and is strictly
///         more faithful, because the value is then built in the process that measures it rather than
///         reconstructed there:
///     </para>
///     <code>
///     // Instead of capturing a value of an attributed type:
///     Benchmark.Run(prepare: () => BuildIndex(), body: index => index.Lookup("key"));
///     </code>
/// </remarks>
/// <example>
///     A record of plain data qualifies:
///     <code>
///     [BenchmarkState]
///     public sealed record Query(string Text, int Limit, string[] Fields);
///
///     var query = new Query("select", 10, ["id", "name"]);
///
///     Benchmark.Run(() => Search(query));   // isolated: the query is sent by value
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class BenchmarkStateAttribute : Attribute;

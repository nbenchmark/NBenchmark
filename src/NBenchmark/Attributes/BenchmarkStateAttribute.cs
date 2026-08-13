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
///         <b>What you are claiming is not that the type round-trips</b> - most types do - but that
///         nothing about how it performs is carried outside its serialized data. A type holding an
///         open file handle, a warmed cache or a pooled buffer does not qualify: it would arrive
///         intact and measure differently, which is the one failure a benchmark must not have.
///     </para>
///     <para>
///         The attribute admits the type; it does not admit what the type holds. Every member is
///         still held to the ordinary rule, so a dictionary built with a custom comparer is refused
///         inside an attributed type exactly as it is outside one. The attribute used to end the walk
///         there, which made it a way to get such a value across without ever reaching the check that
///         exists to stop it - the escape hatch waived the rule it was an escape from.
///     </para>
///     <para>
///         <b>State the serializer cannot restore is refused rather than sent.</b> The payload is
///         written by System.Text.Json, which reads back public fields and properties with a setter
///         and nothing else - but it <i>writes</i> more than it can read. A public readonly field and
///         a get-only property both appear in the payload in full and are both discarded on arrival;
///         a private field never appears at all. Each would reach the measuring process at its
///         default, so a type whose real state is private is declined with the member named.
///     </para>
///     <para>
///         What remains unchecked is the part only you can know: whether a member's <i>value</i>
///         means something outside its own contents. When in doubt, do not use this. Naming the
///         preparation costs one delegate and is strictly more faithful, because the value is then
///         built in the process that measures it rather than reconstructed there:
///     </para>
///     <code>
///     // Instead of capturing a value of an attributed type:
///     Benchmark.Run(prepare: () => BuildIndex(), body: index => index.Lookup("key"));
///     </code>
/// </remarks>
/// <example>
///     A record of plain data qualifies - its positional members become properties with a setter, so
///     the serializer carries every one of them:
///     <code>
///     [BenchmarkState]
///     public sealed record Query(string Text, int Limit, string[] Fields);
///
///     var query = new Query("select", 10, ["id", "name"]);
///
///     Benchmark.Run(() => Search(query));   // isolated: the query is sent by value
///     </code>
///     A type keeping its state to itself does not, and says which member is the problem:
///     <code>
///     [BenchmarkState]
///     public sealed class Index
///     {
///         private readonly int[] _postings;   // refused: written by nothing, restored by nothing
///     }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class BenchmarkStateAttribute : Attribute;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Splits the isolation suite by what a second operating system actually learns from re-running
///     it, so a CI matrix can skip the half that learns nothing.
/// </summary>
/// <remarks>
///     <para>
///         Every test in this namespace that spawns a real worker costs a <c>dotnet exec</c> process
///         start, and process creation is the one operation where Windows is not within a small
///         constant of Linux. At roughly 150 spawning tests per target framework that is the single
///         largest line item in the Windows CI job - large enough that it once ran for two hours.
///     </para>
///     <para>
///         What divides them is whether the operating system is part of the assertion.
///         <em>Protocol</em> tests - the handshake, a coordinator's death closing a pipe, a worker
///         that hard-crashes, muxer resolution - assert behaviour that genuinely differs across
///         platforms: Windows sizes a pipe buffer at 4 KB against Linux's 64 KB, inherits handles by
///         process rather than by descriptor, and reports exit codes its own way. Those carry no
///         trait and run everywhere.
///     </para>
///     <para>
///         Everything marked <see cref="Semantics" /> asserts platform-neutral logic that merely
///         happens to travel over that protocol - a captured value arriving intact, an instance
///         lifetime, a parametric expansion, a recipe argument. Once the protocol tests prove the
///         boundary works on a platform, re-running the semantics over it there proves only that
///         serialization is still deterministic, which is not an operating-system question.
///     </para>
///     <para>
///         So: add no trait if the test would catch a platform bug, and <see cref="Semantics" /> if it
///         would only catch a logic bug. A new spawning test with no trait costs Windows a process
///         start on every push, which is the right default - it fails loudly rather than silently
///         going uncovered.
///     </para>
/// </remarks>
internal static class IsolationTraits
{
    /// <summary>The trait name. Filter with <c>--filter "Isolation!=Semantics"</c>.</summary>
    public const string Name = "Isolation";

    /// <summary>Platform-neutral logic carried over the worker protocol.</summary>
    public const string Semantics = "Semantics";
}

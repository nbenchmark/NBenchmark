using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace NBenchmark.Reporters;

public sealed record ReporterInfo(string Name, string Description);

/// <summary>
///     The name-to-factory map behind <c>--reporter &lt;name&gt;</c>. A plugin package adds to it from a
///     <c>[ModuleInitializer]</c>; the built-in reporters seed it.
/// </summary>
/// <remarks>
///     Registration is expected during module initialization and reads afterwards. Every member is
///     safe to call from any thread, but a reporter registered concurrently with the resolution of a
///     <c>--reporter</c> name may or may not be seen by it - which is a race in the plugin, not in the
///     registry, and the reason self-registration belongs in a module initializer rather than in
///     arbitrary startup code.
/// </remarks>
public static class ReporterRegistry
{
    private static readonly Entry[] _seed =
    [
        new("json", "JSON file output (one file per run)", (dir, detail) => new JsonReporter(dir ?? ".", null, detail)),
        new("markdown", "Markdown table output", (dir, detail) => new MarkdownReporter(dir ?? ".", null, detail)),
        new("csv", "CSV file output", (dir, detail) => new CsvReporter(dir ?? ".", null, detail)),
    ];

    private static readonly object _lock = new();
    private static List<Entry> _entries = new(_seed);
    private static List<Entry>? _initialState;

    // Auto-attached reporters are distinct from explicit opt-in reporters registered via Register.
    // They fire on every RunAsync after the user's explicit reporters unless dedup'd out by name.
    // The seed list is empty - external packages (e.g. NBenchmark.Studio) self-register via
    // [ModuleInitializer] calling RegisterAutoAttach, mirroring how NBenchmark.Reporters.Console
    // self-registers the `console` reporter via Register.
    private static List<Entry> _autoAttachEntries = [];
    private static List<Entry>? _autoAttachInitialState;

    private static IReadOnlyList<ReporterInfo>? _availableCache;
    private static IReadOnlyList<ReporterInfo>? _autoAttachedCache;
    private static int _extensionsLoaded;

    public static IReadOnlyList<ReporterInfo> Available
    {
        get
        {
            EnsureExtensionsLoaded();

            lock (_lock)
            {
                return _availableCache ??= _entries
                    .Select(e => new ReporterInfo(e.Name, e.Description))
                    .ToArray()
                    .AsReadOnly();
            }
        }
    }

    /// <summary>
    ///     The list of auto-attached reporters registered via <see cref="RegisterAutoAttach" />. Auto-attached
    ///     reporters fire on every run after the user's explicit reporters (unless dedup'd out by name),
    ///     and are distinct from the explicit opt-in reporters in <see cref="Available" />. External packages
    ///     self-register via a <c>[ModuleInitializer]</c> calling <see cref="RegisterAutoAttach" />.
    /// </summary>
    public static IReadOnlyList<ReporterInfo> AutoAttached
    {
        get
        {
            EnsureExtensionsLoaded();

            lock (_lock)
            {
                return _autoAttachedCache ??= _autoAttachEntries
                    .Select(e => new ReporterInfo(e.Name, e.Description))
                    .ToArray()
                    .AsReadOnly();
            }
        }
    }

    public static void Register(string name, string description, Func<string?, ReportDetail, IReporter> factory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_lock)
        {
            if (ContainsName(_entries, name) || ContainsName(_autoAttachEntries, name))
                throw new InvalidOperationException($"Reporter '{name}' is already registered.");

            _entries.Add(new Entry(name, description, factory));
            _availableCache = null;
        }
    }

    /// <summary>
    ///     Registers a reporter that auto-attaches to every run. Auto-attached reporters fire once per
    ///     <c>RunAsync</c> after the user's explicit reporters, with dedup so passing <c>--reporter &lt;name&gt;</c>
    ///     does not fire the reporter twice. The same name cannot be registered via both <see cref="Register" />
    ///     and <see cref="RegisterAutoAttach" />. External packages self-register via a
    ///     <c>[ModuleInitializer]</c> calling this method, mirroring how <c>NBenchmark.Reporters.Console</c>
    ///     self-registers the <c>console</c> reporter via <see cref="Register" />.
    /// </summary>
    public static void RegisterAutoAttach(string name, string description, Func<string?, ReportDetail, IReporter> factory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_lock)
        {
            if (ContainsName(_entries, name) || ContainsName(_autoAttachEntries, name))
                throw new InvalidOperationException($"Reporter '{name}' is already registered.");

            _autoAttachEntries.Add(new Entry(name, description, factory));
            _autoAttachedCache = null;
        }
    }

    /// <summary>
    ///     Checks whether a reporter with the given name is registered (in either the explicit
    ///     opt-in list or the auto-attached list) without constructing an instance - the reporter
    ///     counterpart of <see cref="NBenchmark.Observers.ObserverRegistry.IsRegistered" />, and used the same way, to
    ///     validate <c>--reporter</c> without running a factory twice.
    /// </summary>
    public static bool IsRegistered(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        EnsureExtensionsLoaded();

        lock (_lock)
        {
            return ContainsName(_entries, name) || ContainsName(_autoAttachEntries, name);
        }
    }

    public static bool TryCreate(string name, string? outputDir, ReportDetail detail, [NotNullWhen(true)] out IReporter? reporter)
    {
        ArgumentNullException.ThrowIfNull(name);

        EnsureExtensionsLoaded();

        Entry? entry;

        lock (_lock)
        {
            entry = _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        if (entry is not null)
        {
            reporter = entry.Factory(outputDir, detail);
            return true;
        }

        reporter = null;
        return false;
    }

    /// <summary>
    ///     Creates fresh instances of every auto-attached reporter, skipping any whose name appears in
    ///     <paramref name="explicitNames" /> so the reporter does not fire twice when the user also passed
    ///     <c>--reporter &lt;name&gt;</c> or <c>.WithReporter(...)</c>. Called once per <c>RunAsync</c> so
    ///     each run gets fresh instances, mirroring how <see cref="TryCreate" /> is called per-run for
    ///     explicit reporters.
    /// </summary>
    internal static IReadOnlyList<IReporter> CreateAutoAttachedReporters(ReportDetail detail, IReadOnlySet<string> explicitNames)
    {
        EnsureExtensionsLoaded();

        List<Entry> snapshot;

        lock (_lock)
        {
            if (_autoAttachEntries.Count == 0)
                return [];

            snapshot = new List<Entry>(_autoAttachEntries);
        }

        var reporters = new List<IReporter>(snapshot.Count);

        foreach (var entry in snapshot)
        {
            if (explicitNames.Contains(entry.Name))
                continue;

            try
            {
                reporters.Add(entry.Factory(null, detail));
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "NBenchmark: auto-attached reporter '{0}' factory threw and was skipped: {1}",
                    entry.Name, ex.Message);
            }
        }

        return reporters;
    }

    /// <summary>
    ///     Runs the explicit reporters, then fans out to auto-attached reporters (skipping any whose
    ///     name matches an explicit reporter). Each auto-attached reporter is wrapped in try/catch so
    ///     a misbehaving reporter cannot kill the run. Called once per <c>RunAsync</c> by both
    ///     <c>BenchmarkHarness</c> and <c>BenchmarkSuite</c>.
    /// </summary>
    internal static async Task InvokeReportersAsync(
        IReadOnlyList<IReporter> explicitReporters,
        ReportDetail detail,
        IReadOnlyList<BenchmarkResult> results,
        CancellationToken cancellationToken)
    {
        foreach (var reporter in explicitReporters)
        {
            await reporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
        }

        var explicitNames = new HashSet<string>(explicitReporters.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var reporter in explicitReporters)
        {
            explicitNames.Add(reporter.Name);
        }

        foreach (var autoReporter in CreateAutoAttachedReporters(detail, explicitNames))
        {
            try
            {
                await autoReporter.ReportAsync(results, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "NBenchmark: auto-attached reporter '{0}' threw and was skipped: {1}",
                    autoReporter.Name, ex.Message);
            }
        }
    }

    private static void EnsureExtensionsLoaded()
    {
        if (Interlocked.Exchange(ref _extensionsLoaded, 1) != 0)
            return;

        var entryAssembly = Assembly.GetEntryAssembly();

        if (entryAssembly is null)
            return;

        var thisAssemblyName = typeof(ReporterRegistry).Assembly.GetName().Name;

        foreach (var reference in entryAssembly.GetReferencedAssemblies())
        {
            if (reference.Name is null)
                continue;

            if (!reference.Name.StartsWith("NBenchmark.", StringComparison.Ordinal)
                || string.Equals(reference.Name, thisAssemblyName, StringComparison.Ordinal))
                continue;

            try
            {
                Assembly.Load(reference);
            }
            catch
            {
            }
        }
    }

    internal static void Reset()
    {
        lock (_lock)
        {
            _initialState ??= new List<Entry>(_entries);
            _autoAttachInitialState ??= new List<Entry>(_autoAttachEntries);

            _entries = new List<Entry>(_initialState);
            _autoAttachEntries = new List<Entry>(_autoAttachInitialState);
            _availableCache = null;
            _autoAttachedCache = null;
        }
    }

    private static bool ContainsName(List<Entry> entries, string name)
        => entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private sealed record Entry(string Name, string Description, Func<string?, ReportDetail, IReporter> Factory);
}

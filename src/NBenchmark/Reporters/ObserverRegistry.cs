using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace NBenchmark.Observers;

/// <summary>Metadata about a registered observer factory.</summary>
public sealed record ObserverInfo(string Name, string Description);

/// <summary>
///     A registry of named <see cref="IMeasurementObserver" /> factories, mirroring the
///     <see cref="ReporterRegistry" /> pattern. External packages (such as
///     <c>NBenchmark.Live</c>) self-register via a <c>[ModuleInitializer]</c> calling
///     <see cref="Register(string, string, Func{IMeasurementObserver})" />, exactly as
///     <c>NBenchmark.Reporters.Console</c> registers into <c>ReporterRegistry</c>.
/// </summary>
public static class ObserverRegistry
{
    private static List<Entry> _entries = [];
    private static List<Entry>? _initialState;

    // Auto-attached observers are distinct from explicit opt-in observers registered via Register.
    // They fire on every RunAsync after the user's explicit observers unless dedup'd out by name.
    // The seed list is empty - external packages (e.g. NBenchmark.Studio) self-register via
    // [ModuleInitializer] calling RegisterAutoAttach, mirroring how NBenchmark.Reporters.Console
    // self-registers the `console` reporter via Register on the reporter side.
    private static List<Entry> _autoAttachEntries = [];
    private static List<Entry>? _autoAttachInitialState;

    private static IReadOnlyList<ObserverInfo>? _availableCache;
    private static IReadOnlyList<ObserverInfo>? _autoAttachedCache;
    private static int _extensionsLoaded;
    private static readonly object _lock = new();

    public static IReadOnlyList<ObserverInfo> Available
    {
        get
        {
            EnsureExtensionsLoaded();

            lock (_lock)
            {
                return _availableCache ??= _entries
                    .Select(e => new ObserverInfo(e.Name, e.Description))
                    .ToArray()
                    .AsReadOnly();
            }
        }
    }

    /// <summary>
    ///     The list of auto-attached observers registered via <see cref="RegisterAutoAttach" />.
    ///     Auto-attached observers fire on every run after the user's explicit observers
    ///     (unless dedup'd out by name), and are distinct from the explicit opt-in observers in
    ///     <see cref="Available" />. External packages self-register via a
    ///     <c>[ModuleInitializer]</c> calling <see cref="RegisterAutoAttach" />.
    /// </summary>
    public static IReadOnlyList<ObserverInfo> AutoAttached
    {
        get
        {
            EnsureExtensionsLoaded();

            lock (_lock)
            {
                return _autoAttachedCache ??= _autoAttachEntries
                    .Select(e => new ObserverInfo(e.Name, e.Description))
                    .ToArray()
                    .AsReadOnly();
            }
        }
    }

    /// <summary>
    ///     Registers an observer factory. Throws if a factory with the same name (case-insensitive)
    ///     is already registered - in either <see cref="Register" /> or
    ///     <see cref="RegisterAutoAttach" /> - so the same name cannot live in both lists.
    /// </summary>
    public static void Register(string name, string description, Func<IMeasurementObserver> factory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_lock)
        {
            if (ContainsName(_entries, name) || ContainsName(_autoAttachEntries, name))
                throw new InvalidOperationException($"Observer '{name}' is already registered.");

            _entries.Add(new Entry(name, description, factory));
            _availableCache = null;
        }
    }

    /// <summary>
    ///     Registers an observer that auto-attaches to every run. Auto-attached observers fire
    ///     once per <c>RunAsync</c> after the user's explicit observers, with dedup so passing
    ///     <c>--observer &lt;name&gt;</c> or <c>.WithObserver(new ...())</c> for the same name
    ///     does not fire the observer twice. The same name cannot be registered via both
    ///     <see cref="Register" /> and <see cref="RegisterAutoAttach" />. External packages
    ///     self-register via a <c>[ModuleInitializer]</c> calling this method, mirroring how
    ///     <c>NBenchmark.Reporters.Console</c> self-registers the <c>console</c> reporter via
    ///     <see cref="Register" /> on the reporter side.
    /// </summary>
    public static void RegisterAutoAttach(string name, string description, Func<IMeasurementObserver> factory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_lock)
        {
            if (ContainsName(_entries, name) || ContainsName(_autoAttachEntries, name))
                throw new InvalidOperationException($"Observer '{name}' is already registered.");

            _autoAttachEntries.Add(new Entry(name, description, factory));
            _autoAttachedCache = null;
        }
    }

    /// <summary>
    ///     Tries to create an observer by name. Returns <c>true</c> and the observer instance
    ///     if the name is found in either the explicit opt-in list (<see cref="Register" />)
    ///     or the auto-attached list (<see cref="RegisterAutoAttach" />); <c>false</c> and
    ///     <c>null</c> otherwise. Looking up both lists makes <c>--observer &lt;name&gt;</c>
    ///     resolve an auto-attached observer (e.g. <c>--observer studio</c>), which the
    ///     <c>--help</c> text advertises as a valid choice. The harness's
    ///     <c>ResolveObserver</c> dedup ensures the observer does not fire twice when the
    ///     name is also auto-attached.
    /// </summary>
    public static bool TryCreate(string name, [NotNullWhen(true)] out IMeasurementObserver? observer)
    {
        ArgumentNullException.ThrowIfNull(name);

        EnsureExtensionsLoaded();

        Entry? entry;

        lock (_lock)
        {
            entry = _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? _autoAttachEntries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        if (entry is not null)
        {
            observer = entry.Factory();
            return true;
        }

        observer = null;
        return false;
    }

    /// <summary>
    ///     Checks whether an observer with the given name is registered (in either the
    ///     explicit opt-in list or the auto-attached list) without constructing an instance.
    ///     Used by <see cref="CliArgs" /> validation to avoid calling the factory twice (once
    ///     for validation, once for construction in <see cref="BenchmarkHarness.Create" />).
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

    /// <summary>
    ///     Creates fresh instances of every auto-attached observer, skipping any whose name
    ///     appears in <paramref name="explicitNames" /> so the observer does not fire twice when
    ///     the user also passed <c>--observer &lt;name&gt;</c> or
    ///     <c>.WithObserver(new ...())</c> for the same name. Called once per <c>RunAsync</c>
    ///     so each run gets fresh instances, mirroring how <see cref="TryCreate" /> is called
    ///     per-run for explicit observers. Observers whose factory throws are skipped and traced,
    ///     so a misbehaving auto-attached observer cannot kill the run.
    /// </summary>
    internal static IReadOnlyList<IMeasurementObserver> CreateAutoAttachedObservers(IReadOnlySet<string> explicitNames)
    {
        EnsureExtensionsLoaded();

        List<Entry> snapshot;

        lock (_lock)
        {
            if (_autoAttachEntries.Count == 0)
                return [];

            snapshot = new List<Entry>(_autoAttachEntries);
        }

        var observers = new List<IMeasurementObserver>(snapshot.Count);

        foreach (var entry in snapshot)
        {
            if (explicitNames.Contains(entry.Name))
                continue;

            try
            {
                var observer = entry.Factory();

                // A factory that returns the null singleton (e.g. a CI-gated observer that
                // no-ops) is skipped so it does not violate the Debug.Assert in
                // CompositeMeasurementObserver's constructor or perturb the hot-path guard.
                // Mirrors the programmatic-observer filtering in ResolveObserver.
                if (observer != NullMeasurementObserver.Instance)
                    observers.Add(observer);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "NBenchmark: auto-attached observer '{0}' factory threw and was skipped: {1}",
                    entry.Name, ex.Message);
            }
        }

        return observers;
    }

    private static void EnsureExtensionsLoaded()
    {
        if (Interlocked.Exchange(ref _extensionsLoaded, 1) != 0)
            return;

        var entryAssembly = Assembly.GetEntryAssembly();

        if (entryAssembly is null)
            return;

        var thisAssemblyName = typeof(ObserverRegistry).Assembly.GetName().Name;

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
            catch (Exception ex)
            {
                // A misconfigured extension (missing file, bad image, security) would otherwise
                // surface as a silently-absent observer with no diagnostic. Trace it so a host
                // with a TraceListener attached can see why a registered observer never appeared.
                // Matches the catch in ReporterRegistry.EnsureExtensionsLoaded; kept silent on
                // the console so benchmark output is not polluted.
                Trace.TraceWarning("NBenchmark: failed to load extension assembly '{0}': {1}", reference.Name, ex.Message);
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

            // Re-arm the extension-load latch so a test that triggered EnsureExtensionsLoaded
            // can be re-run from a clean slate. Without this the latch stays set for the whole
            // process and the registry cannot be re-tested once any test has read Available /
            // TryCreate (both call EnsureExtensionsLoaded).
            Interlocked.Exchange(ref _extensionsLoaded, 0);
        }
    }

    private static bool ContainsName(List<Entry> entries, string name)
        => entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    private sealed record Entry(string Name, string Description, Func<IMeasurementObserver> Factory);
}

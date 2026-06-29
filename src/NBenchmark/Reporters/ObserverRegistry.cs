using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace NBenchmark.Reporters;

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
    private static IReadOnlyList<ObserverInfo>? _availableCache;
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
    ///     Registers an observer factory. Throws if a factory with the same name (case-insensitive)
    ///     is already registered.
    /// </summary>
    public static void Register(string name, string description, Func<IMeasurementObserver> factory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_lock)
        {
            if (_entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Observer '{name}' is already registered.");

            _entries.Add(new Entry(name, description, factory));
            _availableCache = null;
        }
    }

    /// <summary>
    ///     Tries to create an observer by name. Returns <c>true</c> and the observer instance
    ///     if the name is found; <c>false</c> and <c>null</c> otherwise.
    /// </summary>
    public static bool TryCreate(string name, [NotNullWhen(true)] out IMeasurementObserver? observer)
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
            observer = entry.Factory();
            return true;
        }

        observer = null;
        return false;
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
            _entries = new List<Entry>(_initialState);
            _availableCache = null;
            // Re-arm the extension-load latch so a test that triggered EnsureExtensionsLoaded
            // can be re-run from a clean slate. Without this the latch stays set for the whole
            // process and the registry cannot be re-tested once any test has read Available /
            // TryCreate (both call EnsureExtensionsLoaded).
            Interlocked.Exchange(ref _extensionsLoaded, 0);
        }
    }

    private sealed record Entry(string Name, string Description, Func<IMeasurementObserver> Factory);
}

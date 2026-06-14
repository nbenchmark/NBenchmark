using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace NBenchmark.Reporters;

public sealed record ReporterInfo(string Name, string Description);

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
    private static IReadOnlyList<ReporterInfo>? _availableCache;
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

    public static void Register(string name, string description, Func<string?, ReportDetail, IReporter> factory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_lock)
        {
            if (_entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Reporter '{name}' is already registered.");

            _entries.Add(new Entry(name, description, factory));
            _availableCache = null;
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
            _entries = new List<Entry>(_initialState);
            _availableCache = null;
        }
    }

    private sealed record Entry(string Name, string Description, Func<string?, ReportDetail, IReporter> Factory);
}

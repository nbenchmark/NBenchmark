using global::NBenchmark;
using Spectre.Console;

namespace NBenchmark.Console;

public class ConsoleBenchmarkProgress : IBenchmarkProgress
{
    private int _suiteTotal;
    private string? _suiteOptions;
    private string? _currentName;

    public ConsoleBenchmarkProgress(int iterations, int warmupIterations)
    {
        _suiteOptions = $"{warmupIterations} warmup / {iterations} measured";
    }

    public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total)
    {
        AnsiConsole.MarkupLine($"[bold]Starting {total} benchmark(s)...[/]");
        return Task.CompletedTask;
    }

    public Task OnWarmupStarting(string name, int totalWarmupIterations)
    {
        _currentName = name;
        AnsiConsole.MarkupLine($"  [grey][[[/][bold]{EscapeMarkup(name)}[/][grey]]][/] warming up ({totalWarmupIterations} iterations)...");
        return Task.CompletedTask;
    }

    public Task OnWarmupCompleted(string name)
    {
        return Task.CompletedTask;
    }

    public Task OnBenchmarkStarting(string name, int index, int total)
    {
        _suiteTotal = total;
        AnsiConsole.MarkupLine($"  [grey][[{index}/{total}]][/] {EscapeMarkup(name)} — running ({_suiteOptions})...");
        return Task.CompletedTask;
    }

    public Task OnBenchmarkCompleted(BenchmarkResult result)
    {
        if (result.Errored)
            AnsiConsole.MarkupLine($"[red]  Error: {EscapeMarkup(result.ErrorMessage)}[/]");

        return Task.CompletedTask;
    }

    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
    {
        AnsiConsole.MarkupLine($"  Completed {results.Count} benchmark(s).");
        return Task.CompletedTask;
    }

    private static string EscapeMarkup(string? text) =>
        text?.Replace("[", "[[").Replace("]", "]]") ?? "";
}

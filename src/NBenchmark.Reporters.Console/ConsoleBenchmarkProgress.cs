using System.Diagnostics;
using Spectre.Console;
using SysConsole = System.Console;

namespace NBenchmark.Reporters.Console;

public class ConsoleBenchmarkProgress : IBenchmarkProgress
{
    private readonly Stopwatch _benchmarkStopwatch = new();
    private readonly Stopwatch _suiteStopwatch = new();
    private int _completedBenchmarks;
    private int _currentIndex;
    private int _currentIteration;
    private string _currentName = "";
    private int _currentTotalIterations;
    private bool _inWarmup;
    private int _pulse;
    private int _suiteTotal;

    public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total)
    {
        _suiteTotal = total;
        _completedBenchmarks = 0;
        _suiteStopwatch.Restart();

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[steelblue1]Running {total} benchmark(s)[/]").LeftJustified().RuleStyle(Style.Parse("grey")));
        AnsiConsole.WriteLine();

        return Task.CompletedTask;
    }

    public Task OnWarmupStarting(string name, int totalWarmupIterations)
    {
        _inWarmup = true;
        _currentIteration = 0;
        _currentTotalIterations = totalWarmupIterations;
        RenderStatus();
        return Task.CompletedTask;
    }

    public Task OnWarmupCompleted(string name)
    {
        _inWarmup = false;
        return Task.CompletedTask;
    }

    public Task OnBenchmarkStarting(string name, int index, int total)
    {
        _currentName = name;
        _currentIndex = index;
        _suiteTotal = total;
        _currentIteration = 0;
        _pulse = 0;
        _benchmarkStopwatch.Restart();
        return Task.CompletedTask;
    }

    public Task OnIterationCompleted(string name, int iteration, int totalIterations)
    {
        _currentIteration = iteration;
        _currentTotalIterations = totalIterations;
        RenderStatus();
        return Task.CompletedTask;
    }

    public Task OnBenchmarkCompleted(BenchmarkResult result)
    {
        _completedBenchmarks++;
        _benchmarkStopwatch.Stop();

        // Clear the status line
        SysConsole.Write("\r\x1b[2K");

        var elapsed = _benchmarkStopwatch.Elapsed;
        var icon = result.Errored ? "[red]✗[/]" : "[green]✓[/]";

        var timing = result.Errored
            ? $"[red]{Esc(result.ErrorMessage)}[/]"
            : $"[dim]{BenchmarkFormatter.FormatNs(result.Median)}[/]";

        AnsiConsole.MarkupLine($"  {icon} [bold]{Esc(result.Name)}[/] {timing} [dim]({elapsed.TotalSeconds:F1}s)[/]");

        return Task.CompletedTask;
    }

    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
    {
        _suiteStopwatch.Stop();
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[steelblue1]Completed in {_suiteStopwatch.Elapsed.TotalSeconds:F1}s[/]").LeftJustified().RuleStyle(Style.Parse("grey")));
        AnsiConsole.WriteLine();
        return Task.CompletedTask;
    }

    private void RenderStatus()
    {
        var phase = _inWarmup ? "warmup" : "measuring";

        if (_currentTotalIterations <= 0)
        {
            // An auto-resolved count has no honest denominator, so show a moving indicator and the
            // live sample count instead of a fake percentage and ETA.
            var indeterminate = IndeterminateBar(20);
            var count = _currentIteration > 0 ? $" ({_currentIteration} samples)" : "";

            SysConsole.Write(
                $"\r\x1b[2K  [{_currentIndex}/{_suiteTotal}] \x1b[1m{_currentName}\x1b[0m \x1b[38;5;75m{indeterminate}\x1b[0m \x1b[90m{phase}{count}\x1b[0m");

            return;
        }

        var pct = _currentTotalIterations > 0
            ? (int)Math.Round(100.0 * _currentIteration / _currentTotalIterations)
            : 0;

        var barWidth = 20;
        var filled = (int)Math.Round(barWidth * _currentIteration / (double)Math.Max(1, _currentTotalIterations));
        filled = Math.Clamp(filled, 0, barWidth);
        var empty = barWidth - filled;
        var bar = $"{new string('█', filled)}{new string('░', empty)}";

        var eta = ComputeEta();
        var etaText = eta.HasValue ? $" ETA {FormatTimeSpan(eta.Value)}" : "";

        // \r returns to start of line, \x1b[2K clears the line, then we rewrite
        SysConsole.Write(
            $"\r\x1b[2K  [{_currentIndex}/{_suiteTotal}] \x1b[1m{_currentName}\x1b[0m \x1b[38;5;75m{bar}\x1b[0m \x1b[90m{pct}% {phase} ({_currentIteration}/{_currentTotalIterations}){etaText}\x1b[0m");
    }

    private string IndeterminateBar(int width)
    {
        // A short lit segment that bounces across the track to signal ongoing work.
        const int segment = 3;
        var span = Math.Max(1, width - segment);
        var period = span * 2;
        var pos = _pulse++ % period;

        if (pos > span)
            pos = period - pos;

        var chars = new char[width];

        for (var i = 0; i < width; i++)
        {
            chars[i] = i >= pos && i < pos + segment ? '█' : '░';
        }

        return new string(chars);
    }

    private TimeSpan? ComputeEta()
    {
        if (_currentIteration <= 0 || _currentTotalIterations <= 0)
            return null;

        var elapsed = _benchmarkStopwatch.Elapsed;
        var totalEstimatedForPhase = elapsed / _currentIteration * _currentTotalIterations;
        var remaining = totalEstimatedForPhase - elapsed;

        if (_inWarmup)
        {
            // Warmup remaining + rough estimate for measurement phase
            // Use warmup pace to estimate measurement (they often have similar per-iteration cost)
            var perIter = elapsed / _currentIteration;
            var warmupRemaining = perIter * (_currentTotalIterations - _currentIteration);
            return warmupRemaining + perIter * _currentTotalIterations; // rough guess for measure phase
        }

        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1)
            return "<1s";

        if (ts.TotalSeconds < 60)
            return $"{ts.TotalSeconds:F0}s";

        return $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s";
    }

    private static string Esc(string? text) => Markup.Escape(text ?? "");
}

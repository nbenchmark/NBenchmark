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
    private int _currentSample;
    private string _currentName = "";
    private int _currentTotalSamples;
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

    public Task OnWarmupStarting(string name, int totalWarmupSamples)
    {
        _inWarmup = true;
        _currentSample = 0;
        _currentTotalSamples = totalWarmupSamples;
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
        _currentSample = 0;
        _pulse = 0;
        _benchmarkStopwatch.Restart();
        return Task.CompletedTask;
    }

    public Task OnSampleCompleted(string name, int sample, int totalSamples)
    {
        _currentSample = sample;
        _currentTotalSamples = totalSamples;
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
            : $"[dim]{BenchmarkFormatter.FormatNs(result.MedianNs)}[/]";

        var diagSuffix = "";

        if (result.Diagnostics is { } diag && diag.Gen0Collections.HasValue)
        {
            var gen0 = diag.Gen0Collections.Value;
            var gen1 = diag.Gen1Collections ?? 0;
            var gen2 = diag.Gen2Collections ?? 0;
            diagSuffix = $" [dim]· {gen0}/{gen1}/{gen2} GC[/]";
        }

        AnsiConsole.MarkupLine($"  {icon} [bold]{Esc(result.Name)}[/] {timing}{diagSuffix} [dim]({elapsed.TotalSeconds:F1}s)[/]");

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

        if (_currentTotalSamples <= 0)
        {
            // An auto-resolved count has no honest denominator, so show a moving indicator and the
            // live sample count instead of a fake percentage and ETA.
            var indeterminate = IndeterminateBar(20);
            var count = _currentSample > 0 ? $" ({_currentSample} samples)" : "";

            SysConsole.Write(
                $"\r\x1b[2K  [{_currentIndex}/{_suiteTotal}] \x1b[1m{_currentName}\x1b[0m \x1b[38;5;75m{indeterminate}\x1b[0m \x1b[90m{phase}{count}\x1b[0m");

            return;
        }

        var pct = _currentTotalSamples > 0
            ? (int)Math.Round(100.0 * _currentSample / _currentTotalSamples)
            : 0;

        var barWidth = 20;
        var filled = (int)Math.Round(barWidth * _currentSample / (double)Math.Max(1, _currentTotalSamples));
        filled = Math.Clamp(filled, 0, barWidth);
        var empty = barWidth - filled;
        var bar = $"{new string('█', filled)}{new string('░', empty)}";

        var eta = ComputeEta();
        var etaText = eta.HasValue ? $" ETA {FormatTimeSpan(eta.Value)}" : "";

        // \r returns to start of line, \x1b[2K clears the line, then we rewrite
        SysConsole.Write(
            $"\r\x1b[2K  [{_currentIndex}/{_suiteTotal}] \x1b[1m{_currentName}\x1b[0m \x1b[38;5;75m{bar}\x1b[0m \x1b[90m{pct}% {phase} ({_currentSample}/{_currentTotalSamples}){etaText}\x1b[0m");
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
        if (_currentSample <= 0 || _currentTotalSamples <= 0)
            return null;

        var elapsed = _benchmarkStopwatch.Elapsed;
        var totalEstimatedForPhase = elapsed / _currentSample * _currentTotalSamples;
        var remaining = totalEstimatedForPhase - elapsed;

        if (_inWarmup)
        {
            // Warmup remaining + rough estimate for measurement phase
            // Use warmup pace to estimate measurement (they often have similar per-iteration cost)
            var perIter = elapsed / _currentSample;
            var warmupRemaining = perIter * (_currentTotalSamples - _currentSample);
            return warmupRemaining + perIter * _currentTotalSamples; // rough guess for measure phase
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

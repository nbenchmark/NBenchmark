using System.Diagnostics;

namespace NBenchmark;

/// <summary>
///     A lightweight console progress reporter that works without Spectre.Console.
///     Uses ANSI escape sequences to render inline progress bars with ETA.
///     This is the default progress when no explicit progress is set and the
///     output is a terminal.
/// </summary>
public sealed class DefaultConsoleProgress : IBenchmarkProgress
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
        Console.WriteLine($"  Running {total} benchmark(s)...");
        Console.WriteLine();
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

        // Clear status line and print result
        Console.Write("\r\x1b[2K");
        var icon = result.Errored ? "✗" : "✓";

        var timing = result.Errored
            ? result.ErrorMessage
            : BenchmarkFormatter.FormatNs(result.Median);

        Console.WriteLine($"  {icon} {result.Name}  {timing}  ({_benchmarkStopwatch.Elapsed.TotalSeconds:F1}s)");

        return Task.CompletedTask;
    }

    public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results)
    {
        _suiteStopwatch.Stop();
        Console.WriteLine();
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
            Console.Write(
                $"\r\x1b[2K  [{_currentIndex}/{_suiteTotal}] {_currentName}  {indeterminate} {phase}{count}");
            return;
        }

        var pct = _currentTotalIterations > 0
            ? (int)Math.Round(100.0 * _currentIteration / _currentTotalIterations)
            : 0;

        const int barWidth = 20;
        var filled = (int)Math.Round(barWidth * _currentIteration / (double)Math.Max(1, _currentTotalIterations));
        filled = Math.Clamp(filled, 0, barWidth);
        var empty = barWidth - filled;
        var bar = $"{new string('\u2588', filled)}{new string('\u2591', empty)}";

        var eta = ComputeEta();
        var etaText = eta.HasValue ? $" ETA {FormatTimeSpan(eta.Value)}" : "";

        Console.Write(
            $"\r\x1b[2K  [{_currentIndex}/{_suiteTotal}] {_currentName}  {bar} {pct}% {phase} ({_currentIteration}/{_currentTotalIterations}){etaText}");
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
            chars[i] = i >= pos && i < pos + segment ? '\u2588' : '\u2591';

        return new string(chars);
    }

    private TimeSpan? ComputeEta()
    {
        if (_currentIteration <= 0 || _currentTotalIterations <= 0)
            return null;

        var elapsed = _benchmarkStopwatch.Elapsed;
        var remaining = elapsed / _currentIteration * (_currentTotalIterations - _currentIteration);

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
}

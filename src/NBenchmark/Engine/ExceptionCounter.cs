using System.Runtime.ExceptionServices;

namespace NBenchmark.Engine;

internal static class ExceptionCounter
{
    private static long _count;

    public static void Subscribe()
    {
        Interlocked.Exchange(ref _count, 0);
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    public static void Unsubscribe() => AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;

    public static long Capture() => Interlocked.Read(ref _count);

    public static long Delta(long before) => Math.Max(0, Interlocked.Read(ref _count) - before);

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e) => Interlocked.Increment(ref _count);
}

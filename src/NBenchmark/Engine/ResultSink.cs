using System.Runtime.CompilerServices;

namespace NBenchmark.Engine;

public static class ResultSink
{
    private static volatile object? _hole;
    private static int _holeInt;
    private static long _holeLong;
    private static double _holeDouble;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume<T>(T value)
    {
        _hole = value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume(int value)
    {
        Volatile.Write(ref _holeInt, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume(long value)
    {
        Volatile.Write(ref _holeLong, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume(double value)
    {
        Volatile.Write(ref _holeDouble, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Consume(bool value)
    {
        Volatile.Write(ref _holeInt, value ? 1 : 0);
    }
}
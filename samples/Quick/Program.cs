using NBenchmark;
using NBenchmark.Console;

var result = Benchmark.Run(() =>
{
    for (var i = 0; i < 1000; i++)
    {
    }
});

result.Print();
await result.PrintAsync();
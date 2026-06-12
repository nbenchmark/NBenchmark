using NBenchmark;
using NBenchmark.Reporters.Console;

var result = Benchmark.Run(() =>
{
    for (var i = 0; i < 1000; i++)
    {
    }
});

result.Print();
// Or for rich Spectre.Console output: await result.PrintAsync();

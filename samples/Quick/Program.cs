using NBenchmark;
using NBenchmark.Console;

var result = Bench.Time(() =>
{
    for (int i = 0; i < 1000; i++) { }
});

result.Print();
await result.PrintAsync();
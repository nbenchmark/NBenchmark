using NBenchmark;

var result = Benchmark.Run(() =>
{
    for (var i = 0; i < 1000; i++)
    {
    }
});

result.Print();

// Or, with the NBenchmark.Reporters.Console package, for the Spectre table:
// await result.PrintTableAsync();

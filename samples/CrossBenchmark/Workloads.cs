namespace CrossBenchmark;

public static class Workloads
{
    public static int CountPrimes()
    {
        var count = 0;
        for (var n = 2; n < 30_000; n++)
        {
            var isPrime = true;
            var limit = (int)Math.Sqrt(n);
            for (var d = 2; d <= limit; d++)
            {
                if (n % d == 0)
                {
                    isPrime = false;
                    break;
                }
            }
            if (isPrime) count++;
        }
        return count;
    }

    public static void SortStrings()
    {
        var words = new List<string>();
        for (var i = 0; i < 2_000; i++)
            words.Add($"item-{i:D4}");
        words.Reverse();
        words.Sort(StringComparer.Ordinal);
    }

    public static double LinqAggregate()
    {
        var data = Enumerable.Range(0, 10_000).ToArray();
        return data.Where(x => x % 2 == 0)
                   .Select(x => (double)x * x)
                   .Average();
    }

    public static string StringBuilderAppend()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 5_000; i++)
            sb.Append("hello").Append(i).Append(',');
        return sb.ToString();
    }

    public static int DictionaryLookup()
    {
        var dict = new Dictionary<int, int>();
        for (var i = 0; i < 5_000; i++)
            dict[i] = i * 2;
        var sum = 0;
        for (var i = 0; i < 5_000; i++)
            sum += dict[i];
        return sum;
    }

    public static double MatrixMultiply()
    {
        const int size = 120;
        var a = new double[size, size];
        var b = new double[size, size];
        var r = new Random(42);
        for (var i = 0; i < size; i++)
        {
            for (var j = 0; j < size; j++)
            {
                a[i, j] = r.NextDouble();
                b[i, j] = r.NextDouble();
            }
        }
        var result = new double[size, size];
        for (var i = 0; i < size; i++)
            for (var j = 0; j < size; j++)
            {
                var sum = 0.0;
                for (var k = 0; k < size; k++)
                    sum += a[i, k] * b[k, j];
                result[i, j] = sum;
            }
        return result[size / 2, size / 2];
    }
}

namespace NBenchmark.Engine;

internal static class GlobMatcher
{
    public static bool Match(string pattern, string input)
    {
        if (pattern == "*")
            return true;

        var parts = pattern.Split('*');
        var remaining = input;
        var partIndex = 0;
        var patternEndsWithStar = pattern.EndsWith("*");

        if (parts[0].Length > 0)
        {
            if (!remaining.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase))
                return false;

            remaining = remaining[parts[0].Length..];
            partIndex = 1;
        }

        var lastPartIndex = parts.Length - 1;

        if (!patternEndsWithStar)
        {
            if (lastPartIndex < partIndex)
                return remaining.Length == 0;

            if (parts[lastPartIndex].Length > 0)
            {
                if (!remaining.EndsWith(parts[lastPartIndex], StringComparison.OrdinalIgnoreCase))
                    return false;

                remaining = remaining[..^parts[lastPartIndex].Length];
                lastPartIndex--;
            }
        }
        else
        {
            lastPartIndex--;
        }

        for (var i = partIndex; i <= lastPartIndex; i++)
        {
            if (parts[i].Length == 0)
                continue;

            var idx = remaining.IndexOf(parts[i], StringComparison.OrdinalIgnoreCase);

            if (idx < 0)
                return false;

            remaining = remaining[(idx + parts[i].Length)..];
        }

        return true;
    }
}

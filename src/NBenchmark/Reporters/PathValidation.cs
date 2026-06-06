namespace NBenchmark.Reporters;

internal static class PathValidation
{
    public static string ValidateOutputPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var baseDir  = Path.GetFullPath(Directory.GetCurrentDirectory());
        var withSep = baseDir.EndsWith(Path.DirectorySeparatorChar)
            ? baseDir : baseDir + Path.DirectorySeparatorChar;
        if (fullPath != baseDir && !fullPath.StartsWith(withSep, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Output path must be under the current working directory ({baseDir}). " +
                $"Received: {path}");
        return fullPath;
    }
}

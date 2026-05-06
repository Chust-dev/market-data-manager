namespace HistoricalData.Utils;

public static class PathUtils
{
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath(".");
        }

        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));
    }
}

namespace HistoricalData.DataPool;

public sealed class DataPool
{
    private readonly HashSet<string> _createdDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _directoryLock = new();

    public DataPool(string rootPath)
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public string GetLocalPath(string instrument, int year, int month, int day, string fileName)
    {
        var directory = Path.Combine(RootPath, instrument, year.ToString("0000"), month.ToString("00"), day.ToString("00"));
        EnsureDirectory(directory);
        return Path.Combine(directory, fileName);
    }

    public static bool HasValidFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0;
    }

    private void EnsureDirectory(string path)
    {
        lock (_directoryLock)
        {
            if (_createdDirectories.Contains(path))
            {
                return;
            }

            Directory.CreateDirectory(path);
            _createdDirectories.Add(path);
        }
    }
}

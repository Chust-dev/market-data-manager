using System.Security.Cryptography;
using System.Text.Json;

namespace HistoricalData.DataPool;

public sealed record DataPoolFileMeta(string Sha256, long Size, DateTimeOffset DownloadedUtc)
{
    public static string GetMetaPath(string filePath) => filePath + ".meta.json";

    public static void Write(string filePath)
    {
        var meta = Create(filePath);
        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(GetMetaPath(filePath), json);
    }

    public static bool TryRead(string filePath, out DataPoolFileMeta meta)
    {
        var metaPath = GetMetaPath(filePath);
        if (!File.Exists(metaPath))
        {
            meta = new DataPoolFileMeta(string.Empty, 0, DateTimeOffset.MinValue);
            return false;
        }

        try
        {
            var json = File.ReadAllText(metaPath);
            var result = JsonSerializer.Deserialize<DataPoolFileMeta>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result is null)
            {
                meta = new DataPoolFileMeta(string.Empty, 0, DateTimeOffset.MinValue);
                return false;
            }

            meta = result;
            return true;
        }
        catch
        {
            meta = new DataPoolFileMeta(string.Empty, 0, DateTimeOffset.MinValue);
            return false;
        }
    }

    public static bool VerifyFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        if (!TryRead(filePath, out var meta))
        {
            return false;
        }

        var info = new FileInfo(filePath);
        if (info.Length != meta.Size)
        {
            return false;
        }

        var hash = ComputeSha256(filePath);
        return hash.Equals(meta.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static void DeleteMeta(string filePath)
    {
        var metaPath = GetMetaPath(filePath);
        if (File.Exists(metaPath))
        {
            File.Delete(metaPath);
        }
    }

    private static DataPoolFileMeta Create(string filePath)
    {
        var info = new FileInfo(filePath);
        var hash = ComputeSha256(filePath);
        return new DataPoolFileMeta(hash, info.Length, DateTimeOffset.UtcNow);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
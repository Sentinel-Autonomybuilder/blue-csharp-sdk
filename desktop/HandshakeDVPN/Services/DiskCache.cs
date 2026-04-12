// ─── Disk Cache with Stale-While-Revalidate ───
// Show cached data immediately, refresh in background, update UI if changed.

using System.IO;
using System.Text.Json;

namespace HandshakeDVPN.Services;

public static class DiskCache
{
    private static readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HandshakeDVPN", "cache");

    private static string GetPath(string key) => Path.Combine(_cacheDir, $"{key}.json");

    /// <summary>
    /// Save data to disk cache with timestamp.
    /// </summary>
    public static void Save<T>(string key, T data)
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) Directory.CreateDirectory(_cacheDir);
            var wrapper = new CacheWrapper<T> { Data = data, SavedAt = DateTime.UtcNow };
            File.WriteAllText(GetPath(key), JsonSerializer.Serialize(wrapper));
        }
        catch { /* cache write failed — non-critical */ }
    }

    /// <summary>
    /// Load cached data. Returns null if no cache or deserialization fails.
    /// </summary>
    public static (T data, DateTime savedAt, bool isStale)? Load<T>(string key, TimeSpan maxAge) where T : class
    {
        try
        {
            var path = GetPath(key);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var wrapper = JsonSerializer.Deserialize<CacheWrapper<T>>(json);
            if (wrapper?.Data == null) return null;
            var age = DateTime.UtcNow - wrapper.SavedAt;
            return (wrapper.Data, wrapper.SavedAt, age > maxAge);
        }
        catch { return null; }
    }

    /// <summary>
    /// Delete cached data.
    /// </summary>
    public static void Clear(string key)
    {
        try { var p = GetPath(key); if (File.Exists(p)) File.Delete(p); } catch { }
    }

    private class CacheWrapper<T>
    {
        public T? Data { get; set; }
        public DateTime SavedAt { get; set; }
    }
}

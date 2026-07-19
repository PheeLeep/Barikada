namespace Sipat.Core;

/// <summary>
/// Small file cache under the user's cache directory (XDG_CACHE_HOME or
/// ~/.cache), namespaced to sipat. Entries are plain files; age comes from the
/// file's write time, and HTTP validators live in ".etag" sidecars.
/// </summary>
public static class Cache
{
    public static string Dir
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(root, "sipat");
        }
    }

    public static string? Read(string name, out TimeSpan age)
    {
        age = TimeSpan.Zero;
        var path = Path.Combine(Dir, name);
        if (!File.Exists(path)) return null;

        age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        return File.ReadAllText(path);
    }

    public static void Write(string name, string content)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, name), content);
    }

    /// <summary>Human-readable age for warnings: "3h", "2d".</summary>
    public static string Describe(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "<1m",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes}m",
        { TotalDays: < 1 } => $"{(int)age.TotalHours}h",
        _ => $"{(int)age.TotalDays}d",
    };
}

using System.IO;
using System.Text.Json;

namespace DiametroLineaDesktop.Services;

public static class RecentFilesService
{
    private const int MaxItems = 10;

    private static readonly string _storagePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FlyLineProfiler", "recent.json");

    /// <summary>Returns the current list of recent paths, newest first. Missing files are filtered out.</summary>
    public static List<string> GetAll()
    {
        try
        {
            if (!File.Exists(_storagePath)) return new();
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_storagePath))
                       ?? new List<string>();
            return list.Where(File.Exists).ToList();
        }
        catch { return new(); }
    }

    /// <summary>Pushes <paramref name="path"/> to the front, deduplicates, trims to MaxItems.</summary>
    public static void Add(string path)
    {
        var list = GetAll();
        list.Remove(path);
        list.Insert(0, path);
        if (list.Count > MaxItems)
            list.RemoveRange(MaxItems, list.Count - MaxItems);
        Persist(list);
    }

    /// <summary>Removes a specific path (e.g. after a file-not-found error).</summary>
    public static void Remove(string path)
    {
        var list = GetAll();
        if (list.Remove(path)) Persist(list);
    }

    public static void Clear()
    {
        try { if (File.Exists(_storagePath)) File.Delete(_storagePath); } catch { }
    }

    private static void Persist(List<string> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            File.WriteAllText(_storagePath, JsonSerializer.Serialize(list));
        }
        catch { }
    }
}

using System.IO;
using System.Text;
using System.Text.Json;
using DiametroLineaDesktop.Models;

namespace DiametroLineaDesktop.Services;

public static class ProjectService
{
    public static string DefaultProjectFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     "FlyLineProfiler", "Projects");

    public const string FileFilter    = "FlyLine Profiler project (*.flp)|*.flp";
    public const string FileExtension = ".flp";

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public static void Save(FlyLineProject project, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        project.ModifiedAt = DateTime.UtcNow;
        File.WriteAllText(path, JsonSerializer.Serialize(project, _opts), Encoding.UTF8);
    }

    public static FlyLineProject Load(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<FlyLineProject>(json, _opts)
               ?? throw new InvalidDataException("Invalid or empty project file.");
    }

    public static FlyLineProject New(string name = "Untitled") =>
        new() { Name = name, CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow };
}

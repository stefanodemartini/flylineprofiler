using System.IO;
using System.Text.Json;
using DiametroLineaDesktop.Models;

namespace DiametroLineaDesktop.Services;

public class SettingsService
{
    private readonly string _path;

    public SettingsService(string? path = null)
    {
        _path = path ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
using System;
using System.IO;
using System.Text.Json;

namespace Pako;

public sealed class AppConfig
{
    public string GameDirectory { get; set; } = "";
    public string ExportDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Export");
    public string BlenderPath { get; set; } = "";
    public string ExportFormat { get; set; } = nameof(PakoMeshExportFormat.Glb);
    public bool DarkTheme { get; set; } = true;
}

public sealed class ConfigStore
{
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

    public AppConfig Config { get; }

    public ConfigStore()
    {
        Config = Load();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    private static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }
}

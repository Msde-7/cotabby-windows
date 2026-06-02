using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cotabby.App.Settings;

/// <summary>
/// User-controlled settings persisted to %APPDATA%\Cotabby\settings.json. Kept
/// simple — only the few knobs the tray menu exposes.
/// </summary>
public sealed class AppSettings
{
    public bool Enabled { get; set; } = true;
    public string? ActiveModelId { get; set; }
    public int DebounceMs { get; set; } = 220;
}

/// <summary>
/// Tiny load/save service. Synchronous IO — file is < 1 KB, no need to async.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; }

    public SettingsStore()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cotabby");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            using var fs = File.OpenRead(Path);
            return JsonSerializer.Deserialize<AppSettings>(fs, JsonOpts) ?? new AppSettings();
        }
        catch (Exception) { return new AppSettings(); }
    }

    public void Save(AppSettings settings)
    {
        var tmp = Path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            JsonSerializer.Serialize(fs, settings, JsonOpts);
        }
        File.Move(tmp, Path, overwrite: true);
    }
}

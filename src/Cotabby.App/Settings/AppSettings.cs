using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cotabby.App.Settings;

/// <summary>
/// User-controlled settings persisted to %APPDATA%\Cotabby\settings.json. Mirrors
/// the macOS port's <c>SuggestionSettingsModel</c> + per-pane setting groups.
/// New fields are additive — missing keys deserialize to defaults so existing
/// users keep their prior config across upgrades.
/// </summary>
public sealed class AppSettings
{
    // ---- Core toggles ---------------------------------------------------
    public bool Enabled { get; set; } = true;
    public string? ActiveModelId { get; set; }

    // 80ms keeps time-to-first-suggestion low while still collapsing typing
    // bursts. Per-keystroke cancel-prior in SuggestionWorkController means
    // we never start a generation that the next keystroke would invalidate.
    public int DebounceMs { get; set; } = 80;

    public bool LaunchAtLogin { get; set; }
    public bool AllowMultiLine { get; set; }
    public bool AcceptPunctuationWithWord { get; set; } = true;
    public bool ShowAcceptanceHint { get; set; } = true;
    public bool FastMode { get; set; }
    public bool ShowFirstRunWelcome { get; set; } = true;

    // ---- Appearance -----------------------------------------------------
    /// <summary>
    /// Ghost text color. <c>"auto"</c> tracks system light/dark theme; otherwise
    /// a "#RRGGBB" hex string. Mirrors the macOS color preset list.
    /// </summary>
    public string GhostTextColor { get; set; } = "auto";

    /// <summary>Ghost text opacity 0.30–1.00. macOS default is 1.0.</summary>
    public double GhostTextOpacity { get; set; } = 1.0;

    /// <summary>Suggestion display mode: <c>auto</c>, <c>inline</c>, <c>popup</c>.</summary>
    public string DisplayMode { get; set; } = "auto";

    // ---- Writing profile ------------------------------------------------
    public string UserName { get; set; } = "";

    /// <summary>Up to ~6 language tags. Hint shown in completion prompt.</summary>
    public List<string> Languages { get; set; } = new() { "English" };

    /// <summary>Completion length preset id (see <see cref="CompletionLengthPreset"/>).</summary>
    public string CompletionLengthPreset { get; set; } = "medium";

    // ---- Shortcuts ------------------------------------------------------
    /// <summary>Accept-word key. Currently only <c>"Tab"</c> is supported on Windows
    /// (interception requires WH_KEYBOARD_LL eat-the-key semantics that we ship
    /// only for Tab). Other values are recorded but logged-and-ignored.</summary>
    public string AcceptWordKey { get; set; } = "Tab";

    /// <summary>Accept-whole-suggestion key. Default unbound.</summary>
    public string AcceptSuggestionKey { get; set; } = "";

    /// <summary>Global enable toggle hotkey. Default unbound.</summary>
    public string GlobalToggleKey { get; set; } = "";

    // ---- Apps -----------------------------------------------------------
    /// <summary>
    /// Process names (case-insensitive, no .exe) where suggestions are
    /// suppressed. Mirrors the macOS per-app disable rules list.
    /// </summary>
    public List<string> BlockedApps { get; set; } = new();

    // ---- Advanced -------------------------------------------------------
    /// <summary>Inline emoji autocomplete (<c>:smile:</c> → 😄). Default on.</summary>
    public bool EmojiPickerEnabled { get; set; } = true;
}

/// <summary>
/// Completion length presets — map to MaxTokens used by the request factory.
/// Mirrors the macOS "2–4 / 4–7 / 8–12 / 13+ words" presets.
/// </summary>
public static class CompletionLengthPreset
{
    public const string VeryShort = "very_short";
    public const string Short = "short";
    public const string Medium = "medium";
    public const string Long = "long";

    public static readonly IReadOnlyList<(string Id, string Label)> All =
    [
        (VeryShort, "Very short (2–4 words)"),
        (Short, "Short (4–7 words)"),
        (Medium, "Medium (8–12 words)"),
        (Long, "Long (13+ words)"),
    ];

    /// <summary>Map preset to a (singleLine, multiLine) token budget pair.</summary>
    public static (int Single, int Multi) Tokens(string id) => id switch
    {
        VeryShort => (8, 12),
        Short => (12, 16),
        Long => (28, 40),
        _ => (16, 24), // Medium / default
    };

    public static string Label(string id) =>
        All.FirstOrDefault(t => t.Id == id).Label is { Length: > 0 } l ? l : All[2].Label;
}

/// <summary>
/// Tiny load/save service. Synchronous IO — file is < 4 KB, no need to async.
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

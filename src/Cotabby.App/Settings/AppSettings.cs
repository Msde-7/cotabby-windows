using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cotabby.Core.Emoji;
using Cotabby.Core.Input;
using Cotabby.Core.Spelling;
using Cotabby.Core.Suggestions;

namespace Cotabby.App.Settings;

public enum EngineKind
{
    Local,
    Endpoint,
}

/// <summary>
/// User preferences persisted to %APPDATA%\Cotabby\settings.json. Missing keys
/// deserialize to their defaults, so older files keep working.
/// </summary>
public sealed class AppSettings
{
    // General
    public bool Enabled { get; set; } = true;
    public PauseState? Pause { get; set; }
    public bool PauseOnBatterySaver { get; set; } = true;
    public bool LaunchAtLogin { get; set; }
    public bool ShowFirstRunWelcome { get; set; } = true;
    public bool ShowTrayWordCount { get; set; } = true;
    public long WordsAccepted { get; set; }

    // Engine
    public EngineKind Engine { get; set; } = EngineKind.Local;
    public string? ActiveModelId { get; set; }
    public string EndpointBaseUrl { get; set; } = "";
    public string EndpointModel { get; set; } = "";
    public bool EndpointUsesChatApi { get; set; }
    public int DebounceMs { get; set; } = 20;

    /// <summary>Switches the local model with the power source. An empty id keeps the active model.</summary>
    public bool PowerModelSwitching { get; set; }
    public string BatteryModelId { get; set; } = "";
    public string PluggedInModelId { get; set; } = "";

    // Writing
    public string UserName { get; set; } = "";
    public List<string> Languages { get; set; } = [.. LanguageCatalog.DefaultLanguages];
    public string CompletionLengthPreset { get; set; } = WordCountPreset.Default.Id;
    public bool UseCustomWordRange { get; set; }
    public int CustomLowWords { get; set; } = 5;
    public int CustomHighWords { get; set; } = 15;
    public bool AllowMultiLine { get; set; }
    public bool SuggestWithinWords { get; set; } = true;
    public bool AcceptPunctuationWithWord { get; set; } = true;
    public bool AddSpaceAfterAccept { get; set; }
    public AcceptanceGranularity AcceptanceMode { get; set; }

    // Corrections
    public bool SuppressCompletionsOnTypo { get; set; } = true;
    public bool OfferTypoCorrections { get; set; } = true;
    public bool AutoFixTypos { get; set; }
    public List<string> SpellingDictionaries { get; set; } = [.. SpellingDictionaryCatalog.DefaultEnabledCodes];

    // Context
    public bool ClipboardContext { get; set; }
    public string ExtendedContext { get; set; } = "";

    // Shortcuts
    public KeyBinding AcceptWord { get; set; } = KeyBinding.Tab;
    public KeyBinding AcceptAll { get; set; } = KeyBinding.Backtick;
    public KeyBinding GlobalToggle { get; set; } = KeyBinding.Unbound;
    public bool DoubleTapAcceptsAll { get; set; }
    public List<PerAppShortcutOverride> ShortcutOverrides { get; set; } = [];

    // Apps
    public List<string> BlockedApps { get; set; } = [];
    public bool SuggestInIntegratedTerminals { get; set; }

    // Appearance
    /// <summary>"auto" follows the system theme. Anything else is a "#RRGGBB" color.</summary>
    public string GhostTextColor { get; set; } = "auto";
    public double GhostTextOpacity { get; set; } = 1.0;
    public double GhostTextSizeMultiplier { get; set; } = 1.0;
    public bool ShowAcceptanceHint { get; set; } = true;
    public bool FadeInSuggestions { get; set; } = true;
    public double FadeInSeconds { get; set; } = 0.10;

    // Inline features
    public bool EmojiPickerEnabled { get; set; } = true;
    public bool MacrosEnabled { get; set; } = true;
    public EmojiSkinTone EmojiSkinTone { get; set; }
    public EmojiGender EmojiGender { get; set; }

    /// <summary>The effective word range, from the preset or the custom bounds.</summary>
    [JsonIgnore]
    public (int Low, int High) WordRange =>
        UseCustomWordRange
            ? WordCountPreset.Clamp(CustomLowWords, CustomHighWords)
            : WordCountPreset.Find(CompletionLengthPreset).Range;

    /// <summary>The snapshot the suggestion pipeline reads.</summary>
    public SuggestionSettings ToSuggestionSettings() => new()
    {
        Enabled = Enabled,
        Pause = Pause,
        PauseOnBatterySaver = PauseOnBatterySaver,
        BlockedApps = new HashSet<string>(BlockedApps, StringComparer.OrdinalIgnoreCase),
        SuggestInIntegratedTerminals = SuggestInIntegratedTerminals,
        MaxTokens = SuggestionSettings.TokenBudget(WordRange.High, Languages),
        AllowMultiLine = AllowMultiLine,
        AcceptWord = AcceptWord,
        AcceptAll = DoubleTapAcceptsAll ? KeyBinding.Unbound : AcceptAll,
        ShortcutOverrides = ShortcutOverrides.ToList(),
        DoubleTapAcceptsAll = DoubleTapAcceptsAll && AcceptWord.IsBound,
        Granularity = AcceptanceMode,
        AcceptPunctuationWithWord = AcceptPunctuationWithWord,
        AddSpaceAfterAccept = AddSpaceAfterAccept,
        SuggestWithinWords = SuggestWithinWords,
        Typos = new TypoGateSettings(SuppressCompletionsOnTypo, OfferTypoCorrections, AutoFixTypos),
        SpellingDictionaries = SpellingDictionaryCatalog.Normalize(SpellingDictionaries),
        UserName = UserName,
        Languages = LanguageCatalog.Normalize(Languages),
    };
}

/// <summary>The curated completion lengths. Mirrors the macOS word count presets.</summary>
public sealed record WordCountPreset(string Id, string Title, (int Low, int High) Range)
{
    public const int MinimumWords = 1;
    public const int MaximumWords = 50;

    public static IReadOnlyList<WordCountPreset> All { get; } =
    [
        new("very_short", "Very Short", (2, 4)),
        new("short", "Short", (4, 7)),
        new("medium", "Medium", (7, 12)),
        new("long", "Long", (12, 20)),
    ];

    public static WordCountPreset Default => All[3];

    public string Label => $"{Range.Low}-{Range.High} words";

    public static WordCountPreset Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Default;

    /// <summary>Clamps both ends into range and keeps low at or below high.</summary>
    public static (int Low, int High) Clamp(int low, int high)
    {
        low = Math.Clamp(low, MinimumWords, MaximumWords);
        return (low, Math.Clamp(high, low, MaximumWords));
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/>. The file is a few KB, so IO is synchronous.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; }

    public SettingsStore()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cotabby");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            return Parse(File.ReadAllText(Path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
        File.Move(tmp, Path, overwrite: true);
    }

    public static AppSettings Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return new AppSettings();
        MigrateLegacyKeys(root);
        return root.Deserialize<AppSettings>(JsonOpts) ?? new AppSettings();
    }

    /// <summary>Files written before shortcuts were recordable stored the accept-all key by name.</summary>
    private static void MigrateLegacyKeys(JsonObject root)
    {
        if (root["AcceptSuggestionKey"] is JsonValue legacy && !root.ContainsKey(nameof(AppSettings.AcceptAll)))
        {
            var binding = legacy.GetValue<string>().Trim().ToLowerInvariant() switch
            {
                "tilde" or "~" => new KeyBinding(KeyBinding.VkBacktick, KeyModifiers.Shift, "~"),
                "backslash" or "\\" => new KeyBinding(0xDC, KeyModifiers.None, "\\"),
                "" => KeyBinding.Unbound,
                _ => KeyBinding.Backtick,
            };
            root[nameof(AppSettings.AcceptAll)] = JsonSerializer.SerializeToNode(binding, JsonOpts);
        }
    }
}

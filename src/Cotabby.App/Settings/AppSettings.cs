using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cotabby.Core.Emoji;
using Cotabby.Core.History;
using Cotabby.Core.Input;
using Cotabby.Core.Overlay;
using Cotabby.Core.Overlay.Font;
using Cotabby.Core.Spelling;
using Cotabby.Core.Suggestions;
using Cotabby.Inference.OpenAICompatible;
using Cotabby.Core.Support;
using Cotabby.Win32.Security;

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
    public string EndpointBaseUrl { get; set; } = OpenAICompatibleEndpointConfiguration.DefaultBaseUrl;
    public string EndpointModel { get; set; } = "";

    /// <summary>Chat completions by default, as upstream. Files that already store false keep it.</summary>
    public bool EndpointUsesChatApi { get; set; } = true;
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
    public bool PredictAheadWhileTyping { get; set; } = true;
    public bool PerformanceTracking { get; set; }
    public bool StreamWhileGenerating { get; set; }
    public bool ShowFollowingWords { get; set; } = true;
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

    /// <summary>Tells the model which app and window the text is being written in.</summary>
    public bool IncludeAppContext { get; set; } = true;

    /// <summary>Reads the text on screen around the field so suggestions fit the conversation or document.</summary>
    public bool ScreenContext { get; set; } = true;
    public string ExtendedContext { get; set; } = "";

    // Typing history. Both switches are off by default.
    public bool TypingHistoryEnabled { get; set; }
    public bool TypingHistoryRecording { get; set; }

    /// <summary>Process names, without extension, whose fields are never recorded.</summary>
    public List<string> TypingHistoryExcludedApps { get; set; } = [];

    [JsonIgnore]
    public OpenAICompatibleEndpointSettings EndpointSettings => new(
        EndpointBaseUrl,
        EndpointModel,
        EndpointUsesChatApi ? OpenAICompatibleApiMode.ChatCompletions : OpenAICompatibleApiMode.Completions);

    /// <summary>The preferences the typing history store follows.</summary>
    [JsonIgnore]
    public TypingHistoryPreferences TypingHistoryPreferences =>
        new(TypingHistoryEnabled, TypingHistoryRecording, [.. TypingHistoryExcludedApps]);

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

    /// <summary>Smallest size ghost text renders at, whatever the host's font. Kept at or below the ceiling.</summary>
    public double GhostFontSizeFloor { get; set; } = GhostFontSizeLimits.DefaultFloor;

    /// <summary>Largest size ghost text renders at.</summary>
    public double GhostFontSizeCeiling { get; set; } = GhostFontSizeLimits.DefaultCeiling;
    public bool ShowAcceptanceHint { get; set; } = true;
    public SuggestionDisplayMode DisplayMode { get; set; }
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
        DebounceMs = SettingsValues.DebounceMs(DebounceMs),
        UsesEndpoint = Engine == EngineKind.Endpoint,
        SurfaceContext = IncludeAppContext,
        WordRange = new SuggestionWordRange(WordRange.Low, WordRange.High),
        AllowMultiLine = AllowMultiLine,
        AcceptWord = AcceptWord,
        AcceptAll = DoubleTapAcceptsAll ? KeyBinding.Unbound : AcceptAll,
        ShortcutOverrides = ShortcutOverrides.ToList(),
        DoubleTapAcceptsAll = DoubleTapAcceptsAll && AcceptWord.IsBound,
        Granularity = AcceptanceMode,
        AcceptPunctuationWithWord = AcceptPunctuationWithWord,
        AddSpaceAfterAccept = AddSpaceAfterAccept,
        DisplayMode = DisplayMode,
        SuggestWithinWords = SuggestWithinWords,
        PredictAheadWhileTyping = PredictAheadWhileTyping,
        ShowFollowingWords = ShowFollowingWords,
        // Offering a fix needs typo detection on, and fixing automatically needs offering on.
        Typos = new TypoGateSettings(
            SuppressCompletionsOnTypo,
            SuppressCompletionsOnTypo && OfferTypoCorrections,
            SuppressCompletionsOnTypo && OfferTypoCorrections && AutoFixTypos),
        SpellingDictionaries = SpellingDictionaryCatalog.Normalize(SpellingDictionaries),
        UserName = UserName,
        ExtendedContext = ExtendedContext,
        ClipboardContext = ClipboardContext,
        ScreenContext = ScreenContext,
        StreamWhileGenerating = StreamWhileGenerating,
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

    private const int ReadAttempts = 3;
    private const int ReadRetryMs = 50;

    public string Path { get; }

    public SettingsStore()
        : this(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cotabby"))
    {
    }

    private SettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "settings.json");
    }

    /// <summary>A store that keeps settings.json in <paramref name="directory"/>, for tests.</summary>
    public static SettingsStore InDirectory(string directory) => new(directory);

    /// <summary>
    /// The saved settings, or the defaults when there are none. A file that cannot be read or
    /// parsed is first copied aside as settings.json.corrupt-&lt;time&gt;, because the next save
    /// replaces it with the defaults.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            return Parse(ReadWithRetry());
        }
        catch (Exception)
        {
            BackUpUnreadable();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllBytes(Path, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOpts));

    /// <summary>Another process, such as a virus scanner, may hold the file for a moment.</summary>
    private string ReadWithRetry()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(Path);
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                Thread.Sleep(ReadRetryMs);
            }
        }
    }

    private void BackUpUnreadable()
    {
        try
        {
            File.Copy(Path, $"{Path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch (Exception)
        {
            // Nothing more can be kept. The defaults still load.
        }
    }

    /// <summary>Parses a settings file. Throws when the text is not a JSON object.</summary>
    public static AppSettings Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            throw new JsonException("The settings file does not hold a JSON object.");
        }
        MigrateLegacyKeys(root);
        AppSettings settings;
        try
        {
            settings = root.Deserialize<AppSettings>(JsonOpts) ?? new AppSettings();
        }
        catch (Exception)
        {
            settings = ParseFieldByField(root);
        }
        RestoreNulls(settings);
        Sanitize(settings);
        return settings;
    }

    /// <summary>
    /// Reads each value on its own, so one value of the wrong type falls back to its default
    /// instead of resetting every setting.
    /// </summary>
    private static AppSettings ParseFieldByField(JsonObject root)
    {
        var settings = new AppSettings();
        foreach (var (name, node) in root)
        {
            var property = typeof(AppSettings).GetProperty(name);
            if (property is not { CanWrite: true }) continue;
            try
            {
                var single = new JsonObject { [name] = node?.DeepClone() };
                if (single.Deserialize<AppSettings>(JsonOpts) is { } parsed)
                {
                    property.SetValue(settings, property.GetValue(parsed));
                }
            }
            catch (Exception)
            {
            }
        }
        return settings;
    }

    /// <summary>
    /// A hand edit may set a list or text to null, which the rest of the app never expects. Every
    /// such value goes back to its default, and null entries are dropped from lists of names.
    /// </summary>
    private static void RestoreNulls(AppSettings settings)
    {
        var defaults = new AppSettings();
        var nullability = new NullabilityInfoContext();
        foreach (var property in typeof(AppSettings).GetProperties())
        {
            if (!property.CanRead || !property.CanWrite || property.PropertyType.IsValueType) continue;
            var value = property.GetValue(settings);
            if (value is List<string> names)
            {
                names.RemoveAll(name => name is null);
            }
            else if (value is null && nullability.Create(property).WriteState == NullabilityState.NotNull)
            {
                property.SetValue(settings, property.GetValue(defaults));
            }
        }
        settings.AcceptWord = WithLabel(settings.AcceptWord, defaults.AcceptWord);
        settings.AcceptAll = WithLabel(settings.AcceptAll, defaults.AcceptAll);
        settings.GlobalToggle = WithLabel(settings.GlobalToggle, defaults.GlobalToggle);

        static KeyBinding WithLabel(KeyBinding binding, KeyBinding fallback) =>
            binding.Label is not null ? binding : fallback;
    }

    /// <summary>Puts values a hand edit or an older version may have left out of range back in it.</summary>
    private static void Sanitize(AppSettings settings)
    {
        settings.DebounceMs = SettingsValues.DebounceMs(settings.DebounceMs);
        settings.FadeInSeconds = SettingsValues.FadeSeconds(settings.FadeInSeconds);
        settings.GhostTextOpacity = SettingsValues.Opacity(settings.GhostTextOpacity);
        settings.GhostTextSizeMultiplier = SettingsValues.SizeMultiplier(settings.GhostTextSizeMultiplier);
        (settings.GhostFontSizeFloor, settings.GhostFontSizeCeiling) =
            GhostFontSizeLimits.Normalized(settings.GhostFontSizeFloor, settings.GhostFontSizeCeiling);
        settings.GhostTextColor = SettingsValues.GhostColor(settings.GhostTextColor);
        settings.ShortcutOverrides = ShortcutConflicts.Normalize(settings.ShortcutOverrides);
        settings.Languages ??= [.. LanguageCatalog.DefaultLanguages];
        settings.BlockedApps ??= [];
        settings.SpellingDictionaries ??= [.. SpellingDictionaryCatalog.DefaultEnabledCodes];
        settings.TypingHistoryExcludedApps ??= [];
    }

    /// <summary>Files written before shortcuts were recordable stored the accept-all key by name.</summary>
    private static void MigrateLegacyKeys(JsonObject root)
    {
        // A legacy value that is not a string carries no choice, so the default stays.
        if (root["AcceptSuggestionKey"] is JsonValue legacy && legacy.TryGetValue<string>(out var name)
            && !root.ContainsKey(nameof(AppSettings.AcceptAll)))
        {
            var binding = name.Trim().ToLowerInvariant() switch
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

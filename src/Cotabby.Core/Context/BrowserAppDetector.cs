namespace Cotabby.Core.Context;

/// <summary>Identifies web browsers and Chromium based editors by process name.</summary>
/// <remarks>
/// <see cref="IsBrowser"/> is the broad check used for prompt tone. <see cref="NeedsWebAccessibilityPriming"/>
/// is the narrow check for hosts that keep their web accessibility tree asleep until a client asks
/// for it. Firefox builds its tree without that, so it is a browser but is never primed.
/// </remarks>
public static class BrowserAppDetector
{
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc",
    };

    private static readonly HashSet<string> ChromiumProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "opera", "vivaldi", "arc",
    };

    /// <summary>
    /// Electron apps with editors worth covering. A named list on purpose: most Electron apps are
    /// not text surfaces, and helper processes of these apps must not match.
    /// </summary>
    private static readonly HashSet<string> ElectronEditorProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "VSCodium", "Obsidian", "ClickUp",
        // The ChatGPT desktop app ships as the Codex build.
        "Codex", "ChatGPT",
        "Slack", "Discord", "Notion",
    };

    public static bool IsBrowser(string? processName) =>
        !string.IsNullOrEmpty(processName) && BrowserProcessNames.Contains(processName);

    /// <summary>Chromium browsers, whose page text sits behind a lazily built accessibility tree.</summary>
    public static bool IsChromiumBrowser(string? processName) =>
        !string.IsNullOrEmpty(processName) && ChromiumProcessNames.Contains(processName);

    /// <summary>A named Electron editor. Matched by exact process name, ignoring case.</summary>
    public static bool IsElectronEditor(string? processName) =>
        !string.IsNullOrEmpty(processName) && ElectronEditorProcessNames.Contains(processName);

    /// <summary>True for hosts whose accessibility tree must be woken before a field can be read.</summary>
    public static bool NeedsWebAccessibilityPriming(string? processName) =>
        IsChromiumBrowser(processName) || IsElectronEditor(processName);
}

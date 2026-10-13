using Cotabby.Core.Focus;

namespace Cotabby.Core.Context;

/// <summary>The coarse kind of writing surface the focused app presents.</summary>
public enum AppSurfaceClass
{
    CodeEditor,
    Terminal,
    Email,
    Chat,
    Browser,
    Other,
}

/// <summary>
/// Maps a host process to its surface class. The sets are small on purpose and cover the
/// surfaces people write in most. Everything else is <see cref="AppSurfaceClass.Other"/>.
/// </summary>
public static class AppSurfaceClassifier
{
    private static readonly HashSet<string> CodeEditorProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Code", "Code - Insiders", "Cursor", "VSCodium", "devenv", "sublime_text", "notepad++",
        "rider64", "idea64", "pycharm64", "webstorm64", "clion64", "goland64", "phpstorm64",
        "rubymine64", "rustrover64", "datagrip64",
    };

    private static readonly HashSet<string> EmailProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OUTLOOK", "olk", "HxOutlook", "thunderbird",
    };

    private static readonly HashSet<string> ChatProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "slack", "ms-teams", "Teams", "Discord", "Telegram", "WhatsApp", "Signal",
    };

    /// <param name="processName">Host process name without extension.</param>
    /// <param name="isIntegratedTerminal">True when the caret sits in an xterm.js surface.</param>
    public static AppSurfaceClass Classify(string? processName, bool isIntegratedTerminal = false)
    {
        // Terminal beats every other class. Shell prompts must never get app conditioned prose.
        if (isIntegratedTerminal || TerminalAppDetector.IsTerminal(processName)) return AppSurfaceClass.Terminal;
        if (string.IsNullOrEmpty(processName)) return AppSurfaceClass.Other;
        if (CodeEditorProcessNames.Contains(processName)) return AppSurfaceClass.CodeEditor;
        if (EmailProcessNames.Contains(processName)) return AppSurfaceClass.Email;
        if (ChatProcessNames.Contains(processName)) return AppSurfaceClass.Chat;
        if (BrowserAppDetector.IsBrowser(processName)) return AppSurfaceClass.Browser;
        return AppSurfaceClass.Other;
    }
}

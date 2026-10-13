namespace Cotabby.Core.Focus;

/// <summary>
/// Identifies terminal surfaces. Terminals have their own completion and history,
/// so ghost text stays out of the way there.
/// </summary>
public static class TerminalAppDetector
{
    private static readonly HashSet<string> TerminalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh",
        "wezterm-gui", "alacritty", "kitty", "mintty", "ConEmu", "ConEmu64",
        "Hyper", "Tabby", "warp", "rio", "putty", "wsl", "bash",
    };

    public static bool IsTerminal(string? processName) =>
        !string.IsNullOrEmpty(processName) && TerminalProcessNames.Contains(processName);

    /// <summary>
    /// True when a focused web element belongs to an xterm.js terminal. VS Code,
    /// Cursor and browser hosted terminals all render through xterm.js, and the
    /// editor shares one process with its terminal, so the class list is the only signal.
    /// </summary>
    public static bool IsIntegratedTerminal(string? classList)
    {
        if (string.IsNullOrEmpty(classList)) return false;
        foreach (var name in classList.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (name.StartsWith("xterm", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

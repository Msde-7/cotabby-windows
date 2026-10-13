namespace Cotabby.Core.Context;

/// <summary>Identifies web browsers by process name.</summary>
public static class BrowserAppDetector
{
    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc",
    };

    public static bool IsBrowser(string? processName) =>
        !string.IsNullOrEmpty(processName) && BrowserProcessNames.Contains(processName);
}

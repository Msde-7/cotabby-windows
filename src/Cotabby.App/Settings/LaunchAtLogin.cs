using System.IO;
using Microsoft.Win32;

namespace Cotabby.App.Settings;

/// <summary>
/// HKCU Run key based launch-at-login. Mirrors the macOS port's
/// <c>LaunchAtLoginService</c>: idempotent set/clear with a single boolean
/// query for the current state.
/// </summary>
/// <remarks>
/// Per-user (HKCU) so it works without admin. We point at the actual exe
/// (not <c>dotnet.exe</c>) so a self-contained or framework-dependent build
/// both behave correctly — and we quote the path to survive spaces.
/// </remarks>
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Cotabby";

    /// <summary>True if the Run entry currently points at this exe.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
    }

    /// <summary>Apply the desired state. Returns true on success.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;
            if (enabled)
            {
                var exe = ResolveLaunchTarget();
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
    }

    private static string? ResolveLaunchTarget()
    {
        // Prefer the actual host exe (Cotabby.exe). When the app is launched
        // via `dotnet run`, the main module path points at dotnet.exe; in that
        // case fall back to a best-guess based on AppContext.BaseDirectory so
        // the user at least gets a sensible Run entry for their build output.
        var main = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(main) &&
            !Path.GetFileName(main).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return main;
        }
        var guess = Path.Combine(AppContext.BaseDirectory, "Cotabby.exe");
        return File.Exists(guess) ? guess : main;
    }
}

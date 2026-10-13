using System.IO;
using Microsoft.Win32;

namespace Cotabby.App.Settings;

/// <summary>
/// Launch at login through the per-user Run key, which needs no administrator rights. The entry
/// points at the app's own exe and quotes the path so spaces survive.
/// </summary>
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
        // Under `dotnet run` the main module is dotnet.exe, so fall back to Cotabby.exe next to
        // the app's assemblies.
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

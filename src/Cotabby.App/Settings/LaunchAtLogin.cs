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

    /// <summary>Applies the desired state. A registry that refuses the change leaves it as it was.</summary>
    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var exe = ResolveLaunchTarget();
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception) { }
    }

    /// <summary>Whether the Run key has a Cotabby entry, or null when the registry cannot be read.</summary>
    public static bool? IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the Run key starts a Cotabby that is still on disk, or null when the registry cannot
    /// be read. An entry left pointing into a build folder that was cleaned or moved counts as not
    /// registered, so the startup check points it at this copy. One naming another copy that
    /// exists, such as an installed build seen from a dev build, is left alone.
    /// </summary>
    public static bool? IsRegisteredToExistingApp()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && TargetPath(value) is { } exe && File.Exists(exe);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The program a Run value starts: the quoted path, or the text up to the first space.</summary>
    public static string? TargetPath(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }
        int space = value.IndexOf(' ');
        var path = space < 0 ? value : value[..space];
        return path.Length > 0 ? path : null;
    }

    private static string? ResolveLaunchTarget()
    {
        // Under `dotnet run` the main module is dotnet.exe, so fall back to Cotabby.exe next to
        // the app's assemblies. Without one nothing is registered, since dotnet.exe alone would
        // start nothing at login.
        var main = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(main) &&
            !Path.GetFileName(main).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return main;
        }
        var guess = Path.Combine(AppContext.BaseDirectory, "Cotabby.exe");
        return File.Exists(guess) ? guess : null;
    }
}

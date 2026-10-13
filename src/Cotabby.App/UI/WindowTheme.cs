using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Cotabby.App.UI;

/// <summary>Keeps the Theme.xaml brushes and the title bar of a window in step with the Windows app theme.</summary>
public static class WindowTheme
{
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("WindowBg", "#F5F5F7", "#1F1F1F"),
        ("SidebarBg", "#ECECEE", "#252525"),
        ("CardBg", "#FFFFFF", "#2A2A2A"),
        ("CardBorder", "#E3E3E3", "#3D3D3D"),
        ("TextPrimary", "#1D1D1F", "#E8E8E8"),
        ("TextSecondary", "#6E6E73", "#9A9A9A"),
        ("InputBg", "#F2F2F4", "#333333"),
        ("KeycapBg", "#EFEFF1", "#3A3A3A"),
        ("TrackOff", "#D1D1D6", "#4A4A4A"),
        ("HoverBg", "#0D000000", "#12FFFFFF"),
    ];

    /// <summary>Applies the current theme and reapplies it whenever Windows switches theme.</summary>
    public static void Follow(Window window)
    {
        void Apply()
        {
            ApplyPalette(window.Resources, SystemTheme.IsDark);
            SystemTheme.ApplyTitleBar(window);
        }

        void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => window.Dispatcher.BeginInvoke(Apply);

        Apply();
        // The title bar can only be tinted once the window has a handle.
        window.SourceInitialized += (_, _) => SystemTheme.ApplyTitleBar(window);
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }

    public static void ApplyPalette(ResourceDictionary resources, bool dark)
    {
        foreach (var (key, light, darkValue) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkValue : light));
            brush.Freeze();
            resources[key] = brush;
        }
    }
}

using System.Windows;

namespace Cotabby.App.UI.Settings.Panes;

internal static class GeneralPane
{
    public static SettingsPane Build(Rows r) => new("General", "\uE713", "Core toggles and behavior",
        new SettingsCard("Status",
            r.Toggle("Enable Cotabby", "Turn Cotabby off everywhere without quitting the app.",
                s => s.Enabled, (s, v) => s.Enabled = v)
                .Tagged("on off disable toggle global pause resume active status stop start"),
            r.Toggle("Open at Login", "Start Cotabby automatically when you sign in to Windows.",
                s => s.LaunchAtLogin, (s, v) => s.LaunchAtLogin = v)
                .Tagged("startup launch boot start autostart sign in")),
        new SettingsCard("Power",
            r.Toggle("Pause on Battery Saver", "Pause suggestions while battery saver is on to reduce battery use.",
                s => s.PauseOnBatterySaver, (s, v) => s.PauseOnBatterySaver = v)
                .Tagged("low power energy automatic auto disable")),
        new SettingsCard("System Tray",
            r.Toggle("Show Word Count in Tray", "Show a running count of words you've accepted in the tray menu.",
                s => s.ShowTrayWordCount, (s, v) => s.ShowTrayWordCount = v)
                .Tagged("words stats counter statistics menu")),
        new SettingsCard("Help",
            r.Action("Onboarding", "Replay the first-run setup walkthrough.", "Open Welcome Guide",
                () => WelcomeWindow.ShowFor(r.Context.Host))
                .Tagged("welcome guide tutorial intro help getting started first run")),
        new SettingsCard("Reset",
            r.Action("Reset All Settings",
                "Restore every Cotabby setting to its original default. "
                + "This does not change your chosen model or your accepted-word count.",
                "Reset All Settings…", () => Reset(r.Context))
                .Tagged("defaults restore factory clear start over revert wipe erase")));

    private static void Reset(SettingsContext context)
    {
        var answer = MessageBox.Show(
            "Every Cotabby setting returns to its original default. This can't be undone.",
            "Reset all settings to their defaults?",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) context.Host.ResetSettings();
    }
}

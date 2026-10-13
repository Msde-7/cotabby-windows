using System.Windows;
using System.Windows.Controls;
using Cotabby.App.Settings;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Input;

namespace Cotabby.App.UI.Settings.Panes;

internal static class ShortcutsPane
{
    public static SettingsPane Build(Rows r) => new("Shortcuts", "\uE765", "Keys that accept suggestions",
        new SettingsCard("Keys",
            r.Shortcut(GlobalShortcut.AcceptWord, "Insert the next word of the suggestion.")
                .Tagged("tab key keybind binding hotkey next word"),
            r.Shortcut(GlobalShortcut.AcceptAll, "Insert the whole remaining suggestion in one keystroke.")
                .Tagged("accept all full complete whole everything keybind binding hotkey"),
            r.Toggle("Double Tap to Accept Entire Suggestion",
                "Press the Accept Word key twice quickly to insert the whole suggestion. "
                + "A single press still inserts the next word.",
                s => s.DoubleTapAcceptsAll, (s, v) => s.DoubleTapAcceptsAll = v)
                .Tagged("double-tap twice tab tab press twice accept all"),
            r.Shortcut(GlobalShortcut.Toggle,
                "Turn Cotabby on or off from anywhere without opening the system tray.")
                .Tagged("global on off hotkey pause enable disable keybind binding")),
        new SettingsCard("Per-App Shortcuts",
            Rows.Note("Give a specific app its own accept key, disable an accept action there, "
                + "or keep inheriting the global shortcut."),
            Rows.Custom(Overrides(r.Context)),
            r.Stacked("Add an App", "Enter the process name of the app, such as notepad.",
                Ui.AddBox(AppsPane.ProcessNameHint, name => AddOverride(r.Context, name)))
                .Tagged("per app override process accept key")));

    private static void AddOverride(SettingsContext context, string name)
    {
        var processName = AppsPane.ProcessName(name);
        if (Find(context.Settings, processName) >= 0) return;
        context.Change(s => s.ShortcutOverrides.Add(new PerAppShortcutOverride { ProcessName = processName }));
    }

    private static int Find(AppSettings settings, string processName) => settings.ShortcutOverrides.FindIndex(
        o => string.Equals(o.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

    private static StackPanel Overrides(SettingsContext context)
    {
        var panel = new StackPanel();
        context.OnRefresh(() =>
        {
            panel.Children.Clear();
            foreach (var entry in context.Settings.ShortcutOverrides)
            {
                if (panel.Children.Count > 0) panel.Children.Add(Ui.Separator());
                panel.Children.Add(Override(context, entry));
            }
            if (panel.Children.Count == 0)
            {
                var empty = Ui.Text("No per-app shortcuts. Cotabby uses the global accept keys everywhere.", "Caption");
                empty.Margin = new Thickness(0, 12, 0, 12);
                panel.Children.Add(empty);
            }
        });
        return panel;
    }

    private static StackPanel Override(SettingsContext context, PerAppShortcutOverride entry)
    {
        void Replace(PerAppShortcutOverride? updated) => context.Change(s =>
        {
            int index = Find(s, entry.ProcessName);
            if (index < 0) return;
            if (updated is null) s.ShortcutOverrides.RemoveAt(index);
            else s.ShortcutOverrides[index] = updated;
        });

        var name = new TextBlock { Text = entry.ProcessName, FontSize = 13, FontWeight = FontWeights.SemiBold };
        var remove = Ui.Button("Remove", () => Replace(null));
        DockPanel.SetDock(remove, Dock.Right);
        name.VerticalAlignment = VerticalAlignment.Center;

        return new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 10),
            Children =
            {
                new DockPanel { Children = { remove, name } },
                OverrideKey(context, GlobalShortcut.AcceptWord, entry.AcceptWord, key => Replace(entry with { AcceptWord = key })),
                OverrideKey(context, GlobalShortcut.AcceptAll, entry.AcceptAll, key => Replace(entry with { AcceptAll = key })),
            },
        };
    }

    /// <summary>
    /// One action of an override. A null key inherits the global shortcut and an unbound
    /// key turns the action off in that app.
    /// </summary>
    private static DockPanel OverrideKey(
        SettingsContext context, GlobalShortcut global, KeyBinding? key, Action<KeyBinding?> set)
    {
        var recorder = new KeyRecorder();
        recorder.Recorded += binding => set(binding);
        recorder.Show(key switch
        {
            null => $"Uses global ({global.Label(context.Settings)})",
            { IsBound: false } => "Disabled",
            { } bound => bound.Label,
        });

        var inherit = Ui.Button("Inherit", () => set(null));
        inherit.IsEnabled = key is not null;
        var disable = Ui.Button("Disable", () => set(KeyBinding.Unbound));
        disable.IsEnabled = key is not { IsBound: false };

        var buttons = Ui.Across(recorder, inherit, disable);
        DockPanel.SetDock(buttons, Dock.Right);
        var label = Ui.Text(global.Name, "Caption");
        label.VerticalAlignment = VerticalAlignment.Center;
        return new DockPanel { Margin = new Thickness(0, 8, 0, 0), Children = { buttons, label } };
    }
}

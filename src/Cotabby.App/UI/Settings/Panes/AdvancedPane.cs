using Cotabby.App.Hosting;
using Cotabby.App.UI.Controls;

namespace Cotabby.App.UI.Settings.Panes;

internal static class AdvancedPane
{
    public static SettingsPane Build(Rows r)
    {
        var settingsPath = r.Context.Host.SettingsStore.Path;
        return new SettingsPane("Advanced", "\uE9E9", "Timing, files, and logs",
            new SettingsCard("Timing",
                r.Slider("Suggestion Delay",
                    "How long Cotabby waits after a keystroke before asking the model. "
                    + "Lower feels snappier; higher saves work while you type in bursts.",
                    0, 300, 10, v => $"{v:0} ms",
                    s => s.DebounceMs, (s, v) => s.DebounceMs = (int)v)
                    .Tagged("debounce trigger wait pause typing latency")),
            new SettingsCard("Files",
                r.Action("Settings File", settingsPath, "Open", () => Ui.OpenPath(settingsPath))
                    .Tagged("path json config preferences location"),
                EnginePane.ModelsFolder(r),
                r.Action("Log File", AppHost.LogPath, "Open", () => Ui.OpenPath(AppHost.LogPath))
                    .Tagged("path logs diagnostics debug troubleshooting"),
                Rows.Note("Set the environment variable COTABBY_DEBUG_LOG=1 before starting Cotabby to write "
                    + "verbose logs. Verbose logs include the text you type.")));
    }
}

using Cotabby.App.UI.Settings.Panes;

namespace Cotabby.App.UI.Settings;

internal static class SettingsPanes
{
    /// <summary>Every pane in sidebar order. Each inner list is one visually separated group.</summary>
    public static IReadOnlyList<IReadOnlyList<SettingsPane>> Build(SettingsContext context)
    {
        var rows = new Rows(context);
        IReadOnlyList<IReadOnlyList<SettingsPane>> groups =
        [
            [GeneralPane.Build(rows), AppearancePane.Build(rows), EmojiPane.Build(rows)],
            [WritingPane.Build(rows), EnginePane.Build(rows)],
            [ShortcutsPane.Build(rows), AppsPane.Build(rows)],
            [PerformancePane.Build(rows), AdvancedPane.Build(rows), AboutPane.Build(rows)],
        ];
        // Home links to every other pane, so it is built last and shown first.
        return [[HomePane.Build(rows, [.. groups.SelectMany(group => group)])], .. groups];
    }
}

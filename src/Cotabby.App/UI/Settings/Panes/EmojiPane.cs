using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cotabby.App.Settings;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Emoji;

namespace Cotabby.App.UI.Settings.Panes;

internal static class EmojiPane
{
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji");

    /// <summary>
    /// Cell colors in <see cref="EmojiSkinTone"/> order. WPF draws emoji in one color,
    /// so the cell behind the sample hand carries the tone it stands for.
    /// </summary>
    private static readonly Color[] SkinToneColors =
    [
        Color.FromRgb(0xFF, 0xCC, 0x4D),
        Color.FromRgb(0xF7, 0xDE, 0xCE),
        Color.FromRgb(0xF3, 0xD2, 0xA2),
        Color.FromRgb(0xD5, 0xAB, 0x88),
        Color.FromRgb(0xAF, 0x7E, 0x57),
        Color.FromRgb(0x7C, 0x53, 0x3E),
    ];

    public static SettingsPane Build(Rows r)
    {
        var context = r.Context;
        return new SettingsPane("Emoji & Macros", "\uE76E", "The inline emoji picker and slash macros",
            new SettingsCard("Emoji Picker",
                r.Toggle("Inline Emoji Picker",
                    "Type a name like :smile to search inline, then press your accept-word shortcut "
                    + "to insert the selected emoji.",
                    s => s.EmojiPickerEnabled, (s, v) => s.EmojiPickerEnabled = v)
                    .Tagged("smile colon emoticon face symbol")),
            new SettingsCard("Suggestions",
                r.Labeled("Skin Tone",
                    "For non-default tones, suggestions show your selected tone first and keep the default emoji next.",
                    GlyphPicker(context, Enum.GetValues<EmojiSkinTone>(),
                        tone => (tone.SampleGlyph(), $"{tone.DisplayName()} skin tone", SkinToneColors[(int)tone]),
                        s => s.EmojiSkinTone, (s, v) => s.EmojiSkinTone = v))
                    .Tagged("color complexion"),
                r.Labeled("People Emoji Style",
                    "Choose person, man, or woman variants when an emoji offers them.",
                    GlyphPicker(context, Enum.GetValues<EmojiGender>(),
                        gender => (gender.SampleGlyph(), $"{gender.DisplayName()} variant", null),
                        s => s.EmojiGender, (s, v) => s.EmojiGender = v))
                    .Tagged("gender person man woman neutral default"),
                r.Action("Emoji History",
                    "Forget recently and frequently used emoji so the picker ranks from scratch.",
                    "Clear History", () => context.Host.EmojiPicker.ClearHistory())
                    .Tagged("recent recents reset frequently used"))
                .VisibleWhen(s => s.EmojiPickerEnabled),
            new SettingsCard("Macros",
                r.Toggle("Inline Macros",
                    "Type / then a macro: dates (today, tmrw, next-fri), math (5+5=), units (10km to mi), "
                    + "currency ($100 to eur), or random (dice, random(1,6)). "
                    + "Then press your accept-word shortcut to insert the result.",
                    s => s.MacrosEnabled, (s, v) => s.MacrosEnabled = v)
                    .Tagged("macro math convert currency date random expansion slash snippet formula calculator")));
    }

    /// <summary>A row of emoji cells where one is selected. A fill colors the cell behind the glyph.</summary>
    private static StackPanel GlyphPicker<T>(
        SettingsContext context, IReadOnlyList<T> values, Func<T, (string Glyph, string Name, Color? Fill)> describe,
        Func<AppSettings, T> get, Action<AppSettings, T> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        context.OnRefresh(() =>
        {
            panel.Children.Clear();
            var current = get(context.Settings);
            foreach (var value in values)
            {
                var (glyph, name, fill) = describe(value);
                bool selected = EqualityComparer<T>.Default.Equals(value, current);
                var text = new TextBlock
                {
                    Text = glyph,
                    FontFamily = EmojiFont,
                    FontSize = 17,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var cell = new Border
                {
                    Width = 34,
                    Height = 30,
                    Margin = new Thickness(6, 0, 0, 0),
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(selected ? 2 : 1),
                    Cursor = Cursors.Hand,
                    ToolTip = name,
                    Child = text,
                }.Themed(Border.BorderBrushProperty, selected ? "AccentBrush" : "CardBorder");
                if (fill is { } color)
                {
                    // The glyph is drawn dark on light tones and light on dark ones so it stays readable.
                    bool light = (color.R * 299 + color.G * 587 + color.B * 114) / 1000 > 140;
                    cell.Background = new SolidColorBrush(color);
                    text.Foreground = light ? Brushes.Black : Brushes.White;
                }
                else
                {
                    cell.Themed(Border.BackgroundProperty, selected ? "AccentTint" : "InputBg");
                }
                cell.MouseLeftButtonUp += (_, _) => context.Change(s => set(s, value));
                panel.Children.Add(cell);
            }
        });
        return panel;
    }
}

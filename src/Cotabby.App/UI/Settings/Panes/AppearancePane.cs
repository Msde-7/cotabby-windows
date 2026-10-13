using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Overlay;

namespace Cotabby.App.UI.Settings.Panes;

internal static class AppearancePane
{
    // The ranges and steps match the macOS app.
    private const double MinOpacity = 0.3;
    private const double MinSize = 0.7;
    private const double MaxSize = 1.3;
    private const double FastestFade = 0.05;
    private const double SlowestFade = 0.30;
    private const double PreviewFontSize = 16;

    /// <summary>Half dark and half light, which reads as following the system theme.</summary>
    private static readonly Brush AutomaticFill = new LinearGradientBrush(
        [
            new GradientStop(Color.FromRgb(0x3A, 0x3A, 0x3A), 0.5),
            new GradientStop(Color.FromRgb(0xD6, 0xD6, 0xD6), 0.5),
        ],
        new Point(0, 0), new Point(1, 0));

    public static SettingsPane Build(Rows r) => new("Appearance", "\uE790", "Ghost text style and display",
        new SettingsCard("Preview", Rows.Custom(Preview(r.Context))),
        new SettingsCard("Display",
            r.Segmented("Suggestion Style",
                "Inline continues your line as ghost text. Popup shows a card under the cursor. "
                + "Auto uses the card only where inline text would cover what you already wrote.",
                [
                    (SuggestionDisplayMode.Auto, "Auto"),
                    (SuggestionDisplayMode.Inline, "Inline"),
                    (SuggestionDisplayMode.Popup, "Popup"),
                ],
                s => s.DisplayMode, (s, v) => s.DisplayMode = v)
                .Tagged("mirror card ghost overlay render mode position"),
            r.Toggle("Fade In Suggestions",
                "Let a new suggestion fade in smoothly instead of appearing all at once.",
                s => s.FadeInSuggestions, (s, v) => s.FadeInSuggestions = v)
                .Tagged("animation animate transition smooth appear instant motion"),
            // The slider runs from slow to fast, so its value is the duration mirrored inside the range.
            r.Slider("Fade Speed", "Drag right to make a new suggestion fade in more quickly.",
                FastestFade, SlowestFade, 0.05, v => $"{Mirror(v) * 1000:0} ms",
                s => Mirror(s.FadeInSeconds), (s, v) => s.FadeInSeconds = Mirror(v))
                .Tagged("duration faster slower quick animation")
                .VisibleWhen(s => s.FadeInSuggestions),
            r.Toggle("Show Accept-Key Hint",
                "Show the accept-key badge next to the ghost text so you remember which key inserts it.",
                s => s.ShowAcceptanceHint, (s, v) => s.ShowAcceptanceHint = v)
                .Tagged("badge keycap tip label tab")),
        new SettingsCard("Ghost Text",
            r.Stacked("Ghost Text Color",
                "The color of the inline suggestion. Automatic adapts to light and dark.", Swatches(r.Context))
                .Tagged("theme dark light tint highlight shade"),
            r.Slider("Ghost Text Opacity", "How faint the inline suggestion looks before you accept it.",
                MinOpacity, 1.0, 0.1, v => $"{v * 100:0}%",
                s => s.GhostTextOpacity, (s, v) => s.GhostTextOpacity = v)
                .Tagged("transparency fade alpha translucent dim brightness visibility"),
            r.Slider("Ghost Text Size",
                "Fine-tune how large suggestions appear. Lower it if the ghost text looks too big.",
                MinSize, MaxSize, 0.1, v => $"{v * 100:0}%",
                s => s.GhostTextSizeMultiplier, (s, v) => s.GhostTextSizeMultiplier = v)
                .Tagged("font scale bigger smaller larger zoom multiplier")));

    private static double Mirror(double fade) => Math.Round(FastestFade + SlowestFade - fade, 2);

    private static Border Preview(SettingsContext context)
    {
        var typed = new TextBlock { Text = "This is how your ", VerticalAlignment = VerticalAlignment.Center };
        var ghost = new TextBlock { Text = "suggestions will look", VerticalAlignment = VerticalAlignment.Center };
        var hintLabel = Ui.Text("", "KeycapText");
        var hint = new Border { Margin = new Thickness(6, 0, 0, 0), Child = hintLabel }.Styled("Keycap");

        context.OnRefresh(() =>
        {
            var settings = context.Settings;
            typed.FontSize = ghost.FontSize = PreviewFontSize * settings.GhostTextSizeMultiplier;
            ghost.Opacity = settings.GhostTextOpacity;
            if (GhostTextPalette.Resolve(settings.GhostTextColor) is { } color)
            {
                ghost.Foreground = new SolidColorBrush(color);
            }
            else
            {
                ghost.Themed(TextBlock.ForegroundProperty, "TextSecondary");
            }
            hintLabel.Text = settings.AcceptWord.Label;
            hint.Visibility = settings.ShowAcceptanceHint && settings.AcceptWord.IsBound
                ? Visibility.Visible
                : Visibility.Collapsed;
        });

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 11, 0, 11),
            MinHeight = 48,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { typed, ghost, hint } },
        }.Themed(Border.BackgroundProperty, "InputBg").Themed(Border.BorderBrushProperty, "CardBorder");
    }

    private static StackPanel Swatches(SettingsContext context)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        context.OnRefresh(() =>
        {
            panel.Children.Clear();
            foreach (var (id, label) in GhostTextPalette.Presets)
            {
                bool selected = string.Equals(context.Settings.GhostTextColor, id, StringComparison.OrdinalIgnoreCase);
                panel.Children.Add(Swatch(id, label, selected, () => context.Change(s => s.GhostTextColor = id)));
            }
        });
        return panel;
    }

    private static Grid Swatch(string id, string label, bool selected, Action choose)
    {
        var cell = new Grid
        {
            Width = 26,
            Height = 26,
            Margin = new Thickness(0, 0, 5, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = label,
        };
        if (selected)
        {
            cell.Children.Add(new Ellipse { Width = 24, Height = 24, StrokeThickness = 2 }
                .Themed(Shape.StrokeProperty, "AccentBrush"));
        }
        cell.Children.Add(new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = GhostTextPalette.Resolve(id) is { } color ? new SolidColorBrush(color) : AutomaticFill,
            // A faint outline keeps light swatches visible on white cards.
            Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)),
            StrokeThickness = 1,
        });
        cell.MouseLeftButtonUp += (_, _) => choose();
        return cell;
    }
}

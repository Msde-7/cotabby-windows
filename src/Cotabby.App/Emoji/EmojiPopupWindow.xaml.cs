using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Cotabby.App.Overlay;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;

namespace Cotabby.App.Emoji;

/// <summary>
/// Topmost, click-through panel that shows the emoji ribbon near the caret while a <c>:query</c>
/// capture is open. It never steals focus. All members must be called on the UI thread.
/// </summary>
public partial class EmojiPopupWindow : Window
{
    private static readonly Brush SelectionFill = new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
    private static readonly Brush GlyphBrush = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly FontFamily GlyphFont = new("Segoe UI Emoji");

    private HwndSource? _source;
    private int _selectedIndex;

    public EmojiPopupWindow()
    {
        InitializeComponent();
        QueryRow.Height = EmojiPickerMetrics.QueryRowHeight;
        QueryRow.Margin = new Thickness(EmojiPickerMetrics.HorizontalInset, 0, EmojiPickerMetrics.HorizontalInset, 0);
        RibbonRow.Height = EmojiPickerMetrics.RibbonRowHeight;
        Cells.Margin = QueryRow.Margin;
        EmptyLabel.Margin = QueryRow.Margin;

        SourceInitialized += (_, _) =>
        {
            _source = (HwndSource)PresentationSource.FromVisual(this)!;
            OverlayWindowInterop.MakeClickThrough(_source.Handle);
            // The picker only shows while typing a shortcode, which screen context must not read back.
            OverlayWindowInterop.SetExcludedFromCapture(_source.Handle, true);
        };
    }

    public void ShowMatches(string query, IReadOnlyList<EmojiMatch> matches, int selectedIndex, ScreenRect caretRect)
    {
        QueryText.Text = query;
        // A typed query without matches says so. A bare colon without recents keeps the row blank.
        EmptyLabel.Visibility = Ui.Shown(matches.Count == 0 && query.Length > 0);

        Cells.Children.Clear();
        foreach (var match in matches)
        {
            Cells.Children.Add(new Border
            {
                Width = EmojiPickerMetrics.CellSize,
                Height = EmojiPickerMetrics.CellSize,
                Margin = new Thickness(Cells.Children.Count == 0 ? 0 : EmojiPickerMetrics.CellSpacing, 0, 0, 0),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = match.Glyph,
                    FontFamily = GlyphFont,
                    FontSize = 20,
                    Foreground = GlyphBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        var (width, height) = EmojiPickerMetrics.ContentSize(matches.Count);
        Width = width;
        Height = height;
        _selectedIndex = 0;
        SetSelectedIndex(selectedIndex);

        // Placed before it shows, so it never flashes where the last capture left it.
        PositionAt(caretRect, width, height);
        Show();
    }

    /// <summary>Moves the selection chip and scrolls the ribbon so the selected cell is centered.</summary>
    public void SetSelectedIndex(int index)
    {
        if (index < 0 || index >= Cells.Children.Count) return;

        ((Border)Cells.Children[_selectedIndex]).Background = Brushes.Transparent;
        ((Border)Cells.Children[index]).Background = SelectionFill;
        _selectedIndex = index;

        double pitch = EmojiPickerMetrics.CellSize + EmojiPickerMetrics.CellSpacing;
        double cellCenter = EmojiPickerMetrics.HorizontalInset + index * pitch + EmojiPickerMetrics.CellSize / 2;
        Ribbon.ScrollToHorizontalOffset(cellCenter - Width / 2);
    }

    private void PositionAt(ScreenRect caretRect, double width, double height)
    {
        if (_source is null) return;

        // Metrics are device independent units while caret and monitor rectangles are physical
        // pixels. The scale is the one of the monitor the panel is going to, not the one it was on.
        double centerX = caretRect.X + caretRect.Width / 2;
        double centerY = caretRect.Y + caretRect.Height / 2;
        double scale = OverlayWindowInterop.DipScaleAt(centerX, centerY);
        double pixelWidth = Math.Ceiling(width * scale);
        double pixelHeight = Math.Ceiling(height * scale);

        var work = OverlayWindowInterop.WorkAreaAt(centerX, centerY);
        var frame = EmojiPickerPanelLayout.Frame(caretRect, pixelWidth, pixelHeight, work);
        OverlayWindowInterop.PlaceTopmost(_source.Handle, frame.X, frame.Y, frame.Width, frame.Height);
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Cotabby.Core.Metrics;

namespace Cotabby.App.UI.Controls;

/// <summary>
/// A small filled line graph for one series. A missing value breaks the line, so unmeasured
/// time shows as a gap. While there is nothing to draw it shows <see cref="EmptyMessage"/>.
/// </summary>
internal sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MutedProperty = DependencyProperty.Register(
        nameof(Muted), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private IReadOnlyList<double?> _values = [];
    private double _upper = 100;

    public Sparkline() => Height = 90;

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>The brush for the empty state and the scale labels.</summary>
    public Brush Muted
    {
        get => (Brush)GetValue(MutedProperty);
        set => SetValue(MutedProperty, value);
    }

    public string EmptyMessage { get; set; } = "Collecting…";

    /// <summary>Formats the top of the scale, shown in the corner.</summary>
    public Func<double, string> ScaleFormat { get; set; } = value => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Replaces the series. The scale runs from zero to <paramref name="upper"/>.</summary>
    public void Show(IReadOnlyList<double?> values, double upper)
    {
        _values = values;
        _upper = upper > 0 ? upper : 1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        var box = new Rect(0, 0, width, height);
        var segments = MetricSeries.Segments(_values);
        if (segments.Count == 0)
        {
            dc.DrawRoundedRectangle(Tint(Muted, 0.08), null, box, 6, 6);
            var message = Label(EmptyMessage, 12);
            dc.DrawText(message, new Point((width - message.Width) / 2, (height - message.Height) / 2));
            return;
        }

        // A faint midline and the top value give the line a scale without a full axis.
        var grid = new Pen(Tint(Muted, 0.18), 1);
        dc.DrawLine(grid, new Point(0, 0.5), new Point(width, 0.5));
        dc.DrawLine(grid, new Point(0, height / 2), new Point(width, height / 2));
        dc.DrawLine(grid, new Point(0, height - 0.5), new Point(width, height - 0.5));
        dc.DrawText(Label(ScaleFormat(_upper), 10), new Point(2, 2));

        int slots = Math.Max(_values.Count - 1, 1);
        Point At(int index, double value) => new(
            _values.Count == 1 ? 0 : index * width / slots,
            height - Math.Clamp(value / _upper, 0, 1) * height);

        var fill = Tint(Stroke, 0.15);
        var line = new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round };
        foreach (var segment in segments)
        {
            var points = segment.Select(p => At(p.Index, p.Value)).ToList();
            if (points.Count == 1)
            {
                // A reading between two gaps is a dot. The very first reading of a session spans the width.
                if (_values.Count > 1)
                {
                    dc.DrawEllipse(Stroke, null, points[0], 1.5, 1.5);
                    continue;
                }
                points.Add(new Point(width, points[0].Y));
            }

            var area = new StreamGeometry();
            using (var context = area.Open())
            {
                context.BeginFigure(new Point(points[0].X, height), true, true);
                context.PolyLineTo(points, false, true);
                context.LineTo(new Point(points[^1].X, height), false, true);
            }
            area.Freeze();
            dc.DrawGeometry(fill, null, area);

            var stroke = new StreamGeometry();
            using (var context = stroke.Open())
            {
                context.BeginFigure(points[0], false, false);
                context.PolyLineTo(points.Skip(1).ToList(), true, true);
            }
            stroke.Freeze();
            dc.DrawGeometry(null, line, stroke);
        }
    }

    private FormattedText Label(string text, double size) => new(
        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Muted,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Tint(Brush brush, double opacity)
    {
        var tinted = brush.CloneCurrentValue();
        tinted.Opacity = opacity;
        tinted.Freeze();
        return tinted;
    }
}

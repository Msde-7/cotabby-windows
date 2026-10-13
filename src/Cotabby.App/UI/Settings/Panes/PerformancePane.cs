using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Cotabby.App.UI.Controls;

namespace Cotabby.App.UI.Settings.Panes;

internal static class PerformancePane
{
    public static SettingsPane Build(Rows r)
    {
        var context = r.Context;
        var metrics = context.Host.Coordinator.Metrics;

        TextBlock shown = Value(), accepted = Value(), rate = Value(), words = Value();
        var latency = Ui.Text("", "Caption");
        var sparkline = new Polyline { Stretch = Stretch.Fill, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round }
            .Themed(Shape.StrokeProperty, "AccentBrush");
        var chart = new StackPanel
        {
            Margin = new Thickness(0, 12, 0, 12),
            Children = { new Border { Height = 56, Child = sparkline }, latency },
        };
        latency.Margin = new Thickness(0, 8, 0, 0);

        void Update()
        {
            long shownCount = metrics.SuggestionsShown;
            shown.Text = $"{shownCount:N0}";
            accepted.Text = $"{metrics.SuggestionsAccepted:N0}";
            rate.Text = shownCount == 0 ? "-" : $"{(double)metrics.SuggestionsAccepted / shownCount:P0}";
            words.Text = $"{metrics.WordsAccepted:N0}";

            var samples = metrics.LatenciesMs;
            sparkline.Points = [.. samples.Select((ms, index) => new Point(index, -ms))];
            latency.Text = samples.Count == 0
                ? "No suggestions yet. Type somewhere to see how long they take."
                : $"Last {samples[^1]} ms · Median {samples.Order().ElementAt(samples.Count / 2)} ms · "
                    + $"{samples.Count} most recent suggestions";
        }

        // The metrics change off the UI thread, and only a visible pane needs to follow them.
        void OnMetricsChanged() => chart.Dispatcher.BeginInvoke(Update);
        chart.IsVisibleChanged += (_, _) =>
        {
            metrics.Changed -= OnMetricsChanged;
            if (!chart.IsVisible) return;
            metrics.Changed += OnMetricsChanged;
            Update();
        };
        context.Closed += () => metrics.Changed -= OnMetricsChanged;

        return new SettingsPane("Performance", "\uE9D9", "Suggestion quality and latency",
            new SettingsCard("Suggestion Quality",
                r.Labeled("Suggestions Shown", "How many suggestions appeared since Cotabby started.", shown)
                    .Tagged("quality stats counters"),
                r.Labeled("Suggestions Accepted", "How many of them you accepted at least part of.", accepted)
                    .Tagged("quality stats counters"),
                r.Labeled("Acceptance Rate", "The share of shown suggestions you accepted.", rate)
                    .Tagged("quality stats percent"),
                r.Labeled("Words Completed", "Words you have accepted from suggestions so far.", words)
                    .Tagged("word count lifetime total")),
            new SettingsCard("Latency",
                r.Stacked("Latency", "How long recent suggestions took to appear.", chart)
                    .Tagged("speed timing duration recent requests chart graph milliseconds")));
    }

    private static TextBlock Value() => Ui.Text("", "Mono");
}

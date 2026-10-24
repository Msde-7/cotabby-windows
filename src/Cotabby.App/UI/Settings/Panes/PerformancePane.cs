using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Metrics;
using Cotabby.Win32.Diagnostics;

namespace Cotabby.App.UI.Settings.Panes;

internal static class PerformancePane
{
    private const string Missing = "—";

    public static SettingsPane Build(Rows r)
    {
        var context = r.Context;
        var metrics = context.Host.Coordinator.Metrics;

        TextBlock shown = Value(), accepted = Value(), rate = Value(), words = Value();
        var latency = Graph("Latency", "OrangeBrush");
        latency.Line.ScaleFormat = ms => $"{ms:N0} ms";
        var latencyCaption = Ui.Text("", "Caption");
        latencyCaption.Margin = new Thickness(0, 8, 0, 0);
        var chart = new StackPanel { Margin = new Thickness(0, 12, 0, 12), Children = { latency.View, latencyCaption } };

        void Update()
        {
            long shownCount = metrics.SuggestionsShown;
            shown.Text = $"{shownCount:N0}";
            accepted.Text = $"{metrics.SuggestionsAccepted:N0}";
            rate.Text = shownCount == 0 ? "-" : $"{(double)metrics.SuggestionsAccepted / shownCount:P0}";
            words.Text = $"{metrics.WordsAccepted:N0}";

            var samples = metrics.LatenciesMs;
            latency.Line.Show([.. samples.Select(ms => (double?)ms)], MetricSeries.UpperBound(samples.Select(ms => (double)ms), 100));
            latency.Value.Text = samples.Count == 0 ? Missing : $"{samples[^1]} ms";
            latency.Line.EmptyMessage = "No suggestions yet";
            latencyCaption.Text = samples.Count == 0
                ? "Type somewhere to see how long suggestions take."
                : $"Median {samples.Order().ElementAt(samples.Count / 2)} ms · {samples.Count} most recent suggestions";
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

        return new SettingsPane("Performance", "\uE9D9", "Resource use, suggestion quality and latency",
            new SettingsCard("Live Resource Usage",
                new SettingsRow("Resource Usage", "Live CPU, memory, and GPU graphs for the app.", LiveResources(context))
                    .Tagged("cpu processor memory ram gpu graphics usage resource graph chart live load monitor"),
                Rows.Note(
                    "Updated every second while this pane is open. CPU can exceed 100% across multiple cores. "
                    + "Memory is the app's private memory. GPU is Cotabby's own share of the GPU, the busiest "
                    + "engine as in Task Manager, where the Open Source engine runs its model. An OpenAI-compatible "
                    + "endpoint such as Ollama runs outside Cotabby, so its work doesn't count toward it. The line "
                    + "below the GPU graph covers every app on this PC.")),
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

    /// <summary>
    /// CPU, memory and GPU graphs. Sampling runs only while they are on screen, so a closed pane
    /// or a hidden settings window costs nothing.
    /// </summary>
    private static StackPanel LiveResources(SettingsContext context)
    {
        var store = new SystemMetricsStore(() => new SystemMetricsSampler());
        var cpu = Graph("CPU", "AccentBrush");
        cpu.Line.ScaleFormat = percent => $"{percent:N0}%";
        var memory = Graph("Memory", "GreenBrush");
        memory.Line.ScaleFormat = mb => $"{mb:N0} MB";
        var gpu = Graph("GPU", "PurpleBrush");
        gpu.Line.ScaleFormat = percent => $"{percent:N0}%";
        var device = Ui.Text("", "Caption");
        device.Margin = new Thickness(0, 6, 0, 0);

        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        foreach (var graph in new[] { cpu.View, memory.View, gpu.View }) panel.Children.Add(graph);
        cpu.View.Margin = memory.View.Margin = new Thickness(0, 0, 0, 14);
        panel.Children.Add(device);

        void Update()
        {
            var samples = store.Samples;
            var latest = samples.Count > 0 ? samples[^1] : null;

            cpu.Line.Show([.. samples.Select(s => (double?)s.CpuPercent)],
                MetricSeries.UpperBound(samples.Select(s => s.CpuPercent), 100));
            cpu.Value.Text = latest is null ? Missing : $"{latest.CpuPercent:N0}%";

            memory.Line.Show([.. samples.Select(s => (double?)Megabytes(s.MemoryBytes))],
                MetricSeries.UpperBound(samples.Select(s => Megabytes(s.MemoryBytes)), 128));
            memory.Value.Text = latest is null ? Missing : $"{Megabytes(latest.MemoryBytes):N0} MB";

            // The first reading only sets the baseline, so an empty GPU graph is normal for a moment.
            gpu.Line.EmptyMessage = samples.Count >= 3 ? "No GPU readings for Cotabby" : "Collecting…";
            gpu.Line.Show([.. samples.Select(s => s.GpuPercent)], 100);
            gpu.Value.Text = latest?.GpuPercent is { } share ? $"{share:N0}%" : Missing;

            string all = latest?.DeviceGpuPercent is { } percent ? $"{percent:N0}%" : Missing;
            string used = latest?.GpuMemoryBytes is { } bytes ? FormatBytes(bytes) : Missing;
            device.Text = $"All apps: GPU {all} · GPU memory in use {used}";
        }

        // Readings arrive on a timer thread.
        void OnChanged() => panel.Dispatcher.BeginInvoke(Update);
        store.Changed += OnChanged;

        bool sampling = false;
        void Sample(bool on)
        {
            if (on == sampling) return;
            sampling = on;
            if (on) store.BeginSampling();
            else store.EndSampling();
        }

        panel.IsVisibleChanged += (_, _) => Sample(panel.IsVisible);
        context.Closed += () =>
        {
            store.Changed -= OnChanged;
            Sample(false);
            store.Dispose();
        };
        Update();
        return panel;
    }

    /// <summary>A graph with its name on the left and the latest value, in the graph's color, on the right.</summary>
    private static (FrameworkElement View, TextBlock Value, Sparkline Line) Graph(string title, string brushKey)
    {
        var name = Ui.Text(title, "Caption");
        name.FontWeight = FontWeights.SemiBold;
        var value = Ui.Text(Missing, "Mono").Themed(TextBlock.ForegroundProperty, brushKey);
        value.FontWeight = FontWeights.SemiBold;
        value.HorizontalAlignment = HorizontalAlignment.Right;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6), Children = { name, value } };
        var line = new Sparkline()
            .Themed(Sparkline.StrokeProperty, brushKey)
            .Themed(Sparkline.MutedProperty, "TextSecondary");
        return (new StackPanel { Children = { header, line } }, value, line);
    }

    private static double Megabytes(long bytes) => bytes / (1024.0 * 1024);

    private static string FormatBytes(long bytes) => bytes >= 1L << 30
        ? string.Format(CultureInfo.CurrentCulture, "{0:0.0} GB", bytes / (double)(1L << 30))
        : string.Format(CultureInfo.CurrentCulture, "{0:N0} MB", Megabytes(bytes));

    private static TextBlock Value() => Ui.Text("", "Mono");
}

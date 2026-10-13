using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Cotabby.App.Settings;
using Cotabby.App.UI.Controls;

namespace Cotabby.App.UI.Settings;

/// <summary>
/// Builds settings rows. Each factory wires one control to a getter and a setter on
/// <see cref="AppSettings"/>, so adding a setting to a pane is one line.
/// </summary>
internal sealed class Rows(SettingsContext context)
{
    public SettingsContext Context => context;

    /// <summary>A row with its label on the left and a control on the right.</summary>
    public SettingsRow Labeled(string title, string subtitle, FrameworkElement control)
    {
        var label = Label(title, subtitle);
        label.Margin = new Thickness(0, 9, 16, 9);
        label.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);

        var grid = new Grid { Children = { label, control } }.Styled("SettingRow");
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return new SettingsRow(title, subtitle, grid);
    }

    /// <summary>A row with its label above content that needs the full width.</summary>
    public SettingsRow Stacked(string title, string subtitle, FrameworkElement content)
    {
        content.Margin = new Thickness(0, 10, 0, 0);
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 12), Children = { Label(title, subtitle), content } };
        return new SettingsRow(title, subtitle, panel);
    }

    /// <summary>Free form content that search does not index.</summary>
    public static SettingsRow Custom(FrameworkElement content) => new("", "", content);

    public static SettingsRow Note(string text)
    {
        var note = Ui.Text(text, "Caption");
        note.Margin = new Thickness(0, 12, 0, 12);
        return Custom(note);
    }

    public SettingsRow Toggle(
        string title, string subtitle, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
    {
        var toggle = new CheckBox().Styled("Toggle");
        toggle.Click += (_, _) => context.Change(s => set(s, toggle.IsChecked == true));
        context.OnRefresh(() => toggle.IsChecked = get(context.Settings));
        return Labeled(title, subtitle, toggle);
    }

    /// <summary>A snapping slider with the formatted value beside it.</summary>
    public SettingsRow Slider(
        string title, string subtitle, double minimum, double maximum, double step, Func<double, string> format,
        Func<AppSettings, double> get, Action<AppSettings, double> set)
    {
        var slider = new Slider
        {
            Width = 180,
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = step,
            LargeChange = step,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
        }.Styled("ModernSlider");
        var value = Ui.Text("", "Mono");
        value.Width = 56;
        value.TextAlignment = TextAlignment.Right;

        slider.ValueChanged += (_, _) =>
        {
            // Rounding drops the float noise that snapping to a fractional step leaves behind.
            double rounded = Math.Round(slider.Value, 2);
            value.Text = format(rounded);
            context.Change(s => set(s, rounded));
        };
        context.OnRefresh(() =>
        {
            slider.Value = Math.Clamp(get(context.Settings), minimum, maximum);
            value.Text = format(Math.Round(slider.Value, 2));
        });
        return Labeled(title, subtitle, Ui.Across(slider, value));
    }

    /// <summary>A choice between a few short options, shown side by side.</summary>
    public SettingsRow Segmented<T>(
        string title, string subtitle, IReadOnlyList<(T Value, string Label)> options,
        Func<AppSettings, T> get, Action<AppSettings, T> set)
    {
        var buttons = RadioGroup(options, "Segment", Orientation.Horizontal, get, set);
        var track = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(2), Child = buttons }
            .Themed(Border.BackgroundProperty, "InputBg");
        return Labeled(title, subtitle, track);
    }

    /// <summary>A choice between options that each deserve their own line.</summary>
    public SettingsRow Radios<T>(
        string title, string subtitle, IReadOnlyList<(T Value, string Label)> options,
        Func<AppSettings, T> get, Action<AppSettings, T> set) =>
        Stacked(title, subtitle, RadioGroup(options, "RadioRow", Orientation.Vertical, get, set));

    /// <summary>A choice from a drop down list.</summary>
    public SettingsRow Combo<T>(
        string title, string subtitle, IReadOnlyList<(T Value, string Label)> options,
        Func<AppSettings, T> get, Action<AppSettings, T> set)
    {
        var combo = new ComboBox { ItemsSource = options.Select(o => o.Label).ToList() }.Styled("Combo");
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0) context.Change(s => set(s, options[combo.SelectedIndex].Value));
        };
        context.OnRefresh(() => combo.SelectedIndex = IndexOf(options, get(context.Settings)));
        return Labeled(title, subtitle, combo);
    }

    /// <summary>A whole number with step buttons. Typed values apply on Enter or focus loss.</summary>
    public SettingsRow Number(
        string title, string subtitle, int minimum, int maximum,
        Func<AppSettings, int> get, Action<AppSettings, int> set)
    {
        var box = new TextBox { Width = 52, TextAlignment = TextAlignment.Right }.Styled("Input");

        void Apply(int value)
        {
            value = Math.Clamp(value, minimum, maximum);
            if (value != get(context.Settings)) context.Change(s => set(s, value));
            // Shows the stored value again when the typed one was out of range or not a number.
            box.Text = get(context.Settings).ToString(CultureInfo.CurrentCulture);
        }

        box.OnCommit(text => Apply(int.TryParse(text, out int typed) ? typed : get(context.Settings)));
        context.OnRefresh(() => box.Text = get(context.Settings).ToString(CultureInfo.CurrentCulture));
        return Labeled(title, subtitle, Ui.Across(
            box,
            StepButton("\u2212", () => Apply(get(context.Settings) - 1)),
            StepButton("+", () => Apply(get(context.Settings) + 1))));
    }

    private static Button StepButton(string sign, Action step)
    {
        var button = Ui.Button(sign, step);
        button.Width = 30;
        button.Padding = new Thickness(0, 3, 0, 5);
        button.FontSize = 15;
        return button;
    }

    /// <summary>A single line of text that applies on Enter or focus loss.</summary>
    public SettingsRow Text(
        string title, string subtitle, string placeholder,
        Func<AppSettings, string> get, Action<AppSettings, string> set)
    {
        var box = new TextBox { Tag = placeholder, Width = 240 }.Styled("Input");
        Bind(box, get, set);
        return Labeled(title, subtitle, box);
    }

    /// <summary>
    /// Connects a text box to a string setting. The box keeps what the user is typing
    /// when a refresh arrives from elsewhere, such as the tray menu.
    /// </summary>
    public void Bind(TextBox box, Func<AppSettings, string> get, Action<AppSettings, string> set)
    {
        box.OnCommit(text =>
        {
            if (text != get(context.Settings)) context.Change(s => set(s, text));
        });
        context.OnRefresh(() =>
        {
            if (!box.IsKeyboardFocused) box.Text = get(context.Settings);
        });
    }

    public SettingsRow Shortcut(GlobalShortcut shortcut, string subtitle)
    {
        var editor = new ShortcutEditor(shortcut, () => context.Settings, context.Change);
        context.OnRefresh(editor.Refresh);
        return Labeled(shortcut.Name, subtitle, editor);
    }

    public SettingsRow Action(string title, string subtitle, string buttonLabel, Action onClick) =>
        Labeled(title, subtitle, Ui.Button(buttonLabel, onClick));

    private static StackPanel Label(string title, string subtitle)
    {
        var label = new StackPanel { Children = { Ui.Text(title) } };
        if (subtitle.Length > 0)
        {
            var caption = Ui.Text(subtitle, "Caption");
            caption.Margin = new Thickness(0, 2, 0, 0);
            label.Children.Add(caption);
        }
        return label;
    }

    private StackPanel RadioGroup<T>(
        IReadOnlyList<(T Value, string Label)> options, string styleKey, Orientation orientation,
        Func<AppSettings, T> get, Action<AppSettings, T> set)
    {
        // Radio buttons that share a parent form one group without a group name.
        var panel = new StackPanel { Orientation = orientation };
        foreach (var (value, label) in options)
        {
            var button = new RadioButton { Content = label }.Styled(styleKey);
            button.Click += (_, _) => context.Change(s => set(s, value));
            panel.Children.Add(button);
        }
        context.OnRefresh(() =>
        {
            int selected = IndexOf(options, get(context.Settings));
            for (int i = 0; i < panel.Children.Count; i++) ((RadioButton)panel.Children[i]).IsChecked = i == selected;
        });
        return panel;
    }

    private static int IndexOf<T>(IReadOnlyList<(T Value, string Label)> options, T value)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(options[i].Value, value)) return i;
        }
        return -1;
    }
}

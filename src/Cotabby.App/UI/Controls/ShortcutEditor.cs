using System.Windows;
using System.Windows.Controls;
using Cotabby.App.Settings;
using Cotabby.Core.Input;
using KeyBinding = Cotabby.Core.Input.KeyBinding;

namespace Cotabby.App.UI.Controls;

/// <summary>One of the app wide shortcuts, with how to read and write it on the settings.</summary>
internal sealed class GlobalShortcut
{
    // The double tap is a double press of the accept-word key, so it goes when that key is cleared.
    public static GlobalShortcut AcceptWord { get; } =
        new(ShortcutAction.AcceptWord, KeyBinding.Tab, s => s.AcceptWord, (s, key) =>
        {
            s.AcceptWord = key;
            if (!key.IsBound) s.DoubleTapAcceptsAll = false;
        });

    // Accept-all holds either a key or the double tap, so setting a key or clearing ends the double tap.
    public static GlobalShortcut AcceptAll { get; } =
        new(ShortcutAction.AcceptAll, KeyBinding.Backtick, s => s.AcceptAll, (s, key) =>
        {
            s.AcceptAll = key;
            s.DoubleTapAcceptsAll = false;
        });

    public static GlobalShortcut Toggle { get; } =
        new(ShortcutAction.Toggle, KeyBinding.Unbound, s => s.GlobalToggle, (s, key) => s.GlobalToggle = key);

    private readonly Func<AppSettings, KeyBinding> _get;

    private GlobalShortcut(
        ShortcutAction action, KeyBinding @default, Func<AppSettings, KeyBinding> get, Action<AppSettings, KeyBinding> set)
    {
        Action = action;
        Default = @default;
        _get = get;
        Set = set;
    }

    public ShortcutAction Action { get; }
    public string Name => ShortcutConflicts.Name(Action);
    public KeyBinding Default { get; }
    public Action<AppSettings, KeyBinding> Set { get; }

    /// <summary>True when a double tap of the accept-word key stands in for this shortcut.</summary>
    public bool UsesDoubleTap(AppSettings settings) => this == AcceptAll && settings.DoubleTapAcceptsAll;

    /// <summary>The binding in effect. Double tap leaves the stored accept-all key unused.</summary>
    public KeyBinding Binding(AppSettings settings) => UsesDoubleTap(settings) ? KeyBinding.Unbound : _get(settings);

    public string Label(AppSettings settings) => LabelFor(settings, processName: null);

    /// <summary>The label for an app that inherits this shortcut, which may differ through the app's own accept-word key.</summary>
    public string LabelFor(AppSettings settings, string? processName)
    {
        if (this == AcceptAll)
        {
            return ShortcutResolver.AcceptAllLabel(
                processName, settings.ShortcutOverrides, settings.AcceptWord, settings.AcceptAll,
                settings.DoubleTapAcceptsAll) ?? NotSet;
        }
        return Binding(settings) is { IsBound: true } binding ? binding.Label : NotSet;
    }

    private const string NotSet = "Not set";

    /// <summary>The name of another shortcut already on this key, or null when the key is free.</summary>
    public string? Conflict(AppSettings settings, KeyBinding candidate) =>
        ShortcutConflicts.Global(Bindings(settings), Action, candidate);

    /// <summary>The name of the shortcut a per-app key would collide with in that app, or null.</summary>
    public string? Conflict(AppSettings settings, string processName, KeyBinding candidate) =>
        ShortcutConflicts.PerApp(Bindings(settings), processName, Action, candidate);

    private static ShortcutBindings Bindings(AppSettings settings) => new(
        AcceptWord.Binding(settings), AcceptAll.Binding(settings), Toggle.Binding(settings),
        settings.ShortcutOverrides);
}

/// <summary>A key recorder for a global shortcut with its Reset and Clear buttons.</summary>
internal sealed class ShortcutEditor : StackPanel
{
    private readonly GlobalShortcut _shortcut;
    private readonly Func<AppSettings> _settings;
    private readonly KeyRecorder _recorder;
    private readonly Button _reset;
    private readonly Button _clear;

    /// <param name="shortcut">The shortcut to edit.</param>
    /// <param name="settings">Reads the live settings, which a reset replaces.</param>
    /// <param name="change">Applies and saves one settings mutation.</param>
    public ShortcutEditor(GlobalShortcut shortcut, Func<AppSettings> settings, Action<Action<AppSettings>> change)
    {
        _shortcut = shortcut;
        _settings = settings;
        Orientation = Orientation.Horizontal;

        _recorder = new KeyRecorder { Conflict = candidate => shortcut.Conflict(settings(), candidate) };
        _recorder.Recorded += binding => change(s => shortcut.Set(s, binding));
        _reset = Ui.Button("Reset", () => change(s => shortcut.Set(s, shortcut.Default)));
        _clear = Ui.Button("Clear", () => change(s => shortcut.Set(s, KeyBinding.Unbound)));
        _reset.Margin = _clear.Margin = new Thickness(8, 0, 0, 0);

        Children.Add(_recorder);
        Children.Add(_reset);
        Children.Add(_clear);
    }

    /// <summary>Shows the current binding. Call once after construction and after every settings change.</summary>
    public void Refresh()
    {
        var settings = _settings();
        var binding = _shortcut.Binding(settings);
        bool doubleTap = _shortcut.UsesDoubleTap(settings);

        // Recording a key over the double tap replaces it, as Reset and Clear do.
        _recorder.Show(_shortcut.Label(settings));
        _reset.Visibility = Shown(_shortcut.Default.IsBound && (doubleTap || binding != _shortcut.Default));
        _clear.Visibility = Shown(doubleTap || binding.IsBound);
    }

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

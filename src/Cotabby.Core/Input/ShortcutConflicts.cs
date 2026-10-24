namespace Cotabby.Core.Input;

/// <summary>The three recordable shortcuts.</summary>
public enum ShortcutAction
{
    AcceptWord,
    AcceptAll,
    Toggle,
}

/// <summary>The bindings a conflict check compares against.</summary>
/// <param name="AcceptAll">The one-press accept-all key. Unbound while double tap holds the slot.</param>
public sealed record ShortcutBindings(
    KeyBinding AcceptWord,
    KeyBinding AcceptAll,
    KeyBinding Toggle,
    IReadOnlyList<PerAppShortcutOverride> Overrides);

/// <summary>Keeps the shortcuts unambiguous, so one key never means two actions.</summary>
public static class ShortcutConflicts
{
    public static string Name(ShortcutAction action) => action switch
    {
        ShortcutAction.AcceptWord => "Accept Word",
        ShortcutAction.AcceptAll => "Accept Entire Suggestion",
        _ => "Toggle Cotabby",
    };

    /// <summary>The name of another global shortcut already on <paramref name="candidate"/>, or null.</summary>
    public static string? Global(ShortcutBindings bindings, ShortcutAction editing, KeyBinding candidate)
    {
        if (!candidate.IsBound) return null;
        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            if (action != editing && Same(GlobalBinding(bindings, action), candidate)) return Name(action);
        }
        return null;
    }

    /// <summary>
    /// The name of the shortcut <paramref name="candidate"/> would collide with in one app, or null.
    /// It may not equal the app's other accept binding, inherited or its own, nor the global toggle,
    /// which would take the key first. Other apps' bindings never conflict.
    /// </summary>
    public static string? PerApp(
        ShortcutBindings bindings, string processName, ShortcutAction editing, KeyBinding candidate)
    {
        if (!candidate.IsBound) return null;
        if (editing != ShortcutAction.AcceptWord
            && Same(ShortcutResolver.AcceptWord(processName, bindings.Overrides, bindings.AcceptWord), candidate))
        {
            return Name(ShortcutAction.AcceptWord);
        }
        if (editing != ShortcutAction.AcceptAll
            && Same(ShortcutResolver.AcceptAll(processName, bindings.Overrides, bindings.AcceptAll), candidate))
        {
            return Name(ShortcutAction.AcceptAll);
        }
        return Same(bindings.Toggle, candidate) ? Name(ShortcutAction.Toggle) : null;
    }

    /// <summary>
    /// Trims process names, drops empty ones, keeps the last row for each app ignoring case and
    /// sorts by name, so a hand-edited file cannot hold two rows for one app.
    /// </summary>
    public static List<PerAppShortcutOverride> Normalize(IEnumerable<PerAppShortcutOverride?>? overrides)
    {
        var byName = new Dictionary<string, PerAppShortcutOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in overrides ?? [])
        {
            string? name = entry?.ProcessName?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            byName[name] = entry! with { ProcessName = name };
        }
        return byName.Values
            .OrderBy(o => o.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.ProcessName, StringComparer.Ordinal)
            .ToList();
    }

    private static KeyBinding GlobalBinding(ShortcutBindings bindings, ShortcutAction action) => action switch
    {
        ShortcutAction.AcceptWord => bindings.AcceptWord,
        ShortcutAction.AcceptAll => bindings.AcceptAll,
        _ => bindings.Toggle,
    };

    private static bool Same(KeyBinding a, KeyBinding b) =>
        a.IsBound && a.VirtualKey == b.VirtualKey && a.Modifiers == b.Modifiers;
}

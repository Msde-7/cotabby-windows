namespace Cotabby.Core.Input;

/// <summary>
/// One physical shortcut. Key code, modifiers and label travel together so a
/// binding can never be half updated. A zero key code means the action is unbound.
/// </summary>
public readonly record struct KeyBinding(int VirtualKey, KeyModifiers Modifiers, string Label)
{
    public const int VkTab = 0x09;
    public const int VkBacktick = 0xC0;

    public static KeyBinding Unbound { get; } = new(0, KeyModifiers.None, "");
    public static KeyBinding Tab { get; } = new(VkTab, KeyModifiers.None, "Tab");
    public static KeyBinding Backtick { get; } = new(VkBacktick, KeyModifiers.None, "`");

    public bool IsBound => VirtualKey != 0;

    public bool Matches(KeyboardEvent ev) =>
        IsBound && ev.VirtualKey == VirtualKey && ev.Modifiers == Modifiers;
}

/// <summary>
/// Shortcut choices for one app, keyed by process name. A null action inherits
/// the global binding. An unbound binding disables the action in that app.
/// </summary>
public sealed record PerAppShortcutOverride
{
    public required string ProcessName { get; init; }
    public KeyBinding? AcceptWord { get; init; }
    public KeyBinding? AcceptAll { get; init; }
}

/// <summary>Resolves the effective accept bindings for the focused app.</summary>
public static class ShortcutResolver
{
    public static KeyBinding AcceptWord(
        string? processName, IReadOnlyList<PerAppShortcutOverride> overrides, KeyBinding global) =>
        Find(processName, overrides)?.AcceptWord ?? global;

    public static KeyBinding AcceptAll(
        string? processName, IReadOnlyList<PerAppShortcutOverride> overrides, KeyBinding global) =>
        Find(processName, overrides)?.AcceptAll ?? global;

    /// <summary>True when the app replaces the global accept-all binding, which also opts it out of double tap.</summary>
    public static bool OverridesAcceptAll(string? processName, IReadOnlyList<PerAppShortcutOverride> overrides) =>
        Find(processName, overrides)?.AcceptAll is not null;

    private static PerAppShortcutOverride? Find(string? processName, IReadOnlyList<PerAppShortcutOverride> overrides)
    {
        if (string.IsNullOrEmpty(processName)) return null;
        foreach (var o in overrides)
        {
            if (string.Equals(o.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) return o;
        }
        return null;
    }
}

/// <summary>
/// Presents the user's accept-word key to the inline features as Tab, which is the
/// key their state machines commit on.
/// </summary>
public static class InlineCommitKey
{
    public static KeyboardEvent Normalize(KeyboardEvent ev, KeyBinding commit)
    {
        if (commit.Matches(ev)) return ev with { Kind = KeyKind.Tab, Modifiers = KeyModifiers.None };
        // A Tab that is not the commit key reaches the host and moves focus.
        return ev.Kind == KeyKind.Tab ? ev with { Kind = KeyKind.Arrow } : ev;
    }
}

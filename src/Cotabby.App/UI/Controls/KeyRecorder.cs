using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyBinding = Cotabby.Core.Input.KeyBinding;
using KeyModifiers = Cotabby.Core.Input.KeyModifiers;

namespace Cotabby.App.UI.Controls;

/// <summary>
/// A button that shows a shortcut and, once clicked, records the next key press with its
/// modifiers. Escape or losing focus cancels the recording.
/// </summary>
public sealed partial class KeyRecorder : Button
{
    private const string Prompt = "Press a key…";
    private const uint MAPVK_VK_TO_CHAR = 2;

    private static readonly Dictionary<Key, string> NamedKeys = new()
    {
        [Key.Tab] = "Tab",
        [Key.Space] = "Space",
        [Key.Return] = "Enter",
        [Key.Back] = "Backspace",
        [Key.Delete] = "Delete",
        [Key.Insert] = "Insert",
        [Key.Home] = "Home",
        [Key.End] = "End",
        [Key.PageUp] = "Page Up",
        [Key.PageDown] = "Page Down",
        [Key.Left] = "Left",
        [Key.Right] = "Right",
        [Key.Up] = "Up",
        [Key.Down] = "Down",
        [Key.Escape] = "Esc",
        [Key.CapsLock] = "Caps Lock",
    };

    private string _idleText = "";
    private bool _recording;

    public KeyRecorder()
    {
        this.Styled("Btn");
        FontWeight = FontWeights.SemiBold;
        MinWidth = 44;
        Click += (_, _) => Begin();
        LostKeyboardFocus += (_, _) => End();
    }

    /// <summary>Names the shortcut that already uses a binding, or returns null when it is free.</summary>
    public Func<KeyBinding, string?>? Conflict { get; init; }

    public event Action<KeyBinding>? Recorded;

    /// <summary>Sets the text shown while not recording, normally the current key label.</summary>
    public void Show(string text)
    {
        _idleText = text;
        if (!_recording) Content = text;
    }

    private void Begin()
    {
        _recording = true;
        Content = Prompt;
        SetResourceReference(BorderBrushProperty, "AccentBrush");
        Focus();
    }

    private void End()
    {
        _recording = false;
        Content = _idleText;
        ClearValue(BorderBrushProperty);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recording)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        e.Handled = true;

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        var modifiers = CurrentModifiers();
        if (key == Key.Escape && modifiers == KeyModifiers.None)
        {
            End();
            return;
        }
        if (IsModifier(key))
        {
            ShowHeldModifiers();
            return;
        }
        // Shift alone still types the character, so it does not make a letter safe to bind.
        if (TypesLetterOrDigit(key) && (modifiers & ~KeyModifiers.Shift) == KeyModifiers.None)
        {
            Content = "Add Ctrl, Alt or Win";
            return;
        }

        var binding = new KeyBinding(KeyInterop.VirtualKeyFromKey(key), modifiers, Label(key, modifiers));
        if (Conflict?.Invoke(binding) is { } owner)
        {
            Content = $"Used by {owner}";
            return;
        }
        End();
        Recorded?.Invoke(binding);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (_recording && Content is string text && text.EndsWith('…')) ShowHeldModifiers();
    }

    private void ShowHeldModifiers()
    {
        var modifiers = CurrentModifiers();
        Content = modifiers == KeyModifiers.None ? Prompt : ModifierPrefix(modifiers) + "…";
    }

    private static KeyModifiers CurrentModifiers()
    {
        var held = Keyboard.Modifiers;
        var modifiers = KeyModifiers.None;
        if (held.HasFlag(ModifierKeys.Control)) modifiers |= KeyModifiers.Control;
        if (held.HasFlag(ModifierKeys.Alt)) modifiers |= KeyModifiers.Alt;
        if (held.HasFlag(ModifierKeys.Shift)) modifiers |= KeyModifiers.Shift;
        if (held.HasFlag(ModifierKeys.Windows)) modifiers |= KeyModifiers.Win;
        return modifiers;
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static bool TypesLetterOrDigit(Key key) => key is
        (>= Key.A and <= Key.Z) or (>= Key.D0 and <= Key.D9) or (>= Key.NumPad0 and <= Key.NumPad9);

    /// <summary>A readable label such as "Ctrl+Shift+K", "Tab" or "F6".</summary>
    private static string Label(Key key, KeyModifiers modifiers) => ModifierPrefix(modifiers) + KeyName(key);

    private static string ModifierPrefix(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? "Ctrl+" : "")
        + (modifiers.HasFlag(KeyModifiers.Alt) ? "Alt+" : "")
        + (modifiers.HasFlag(KeyModifiers.Shift) ? "Shift+" : "")
        + (modifiers.HasFlag(KeyModifiers.Win) ? "Win+" : "");

    private static string KeyName(Key key)
    {
        if (NamedKeys.TryGetValue(key, out var name)) return name;
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return $"Num {key - Key.NumPad0}";
        // The low word is the character the key types on the active layout.
        char typed = (char)(MapVirtualKey((uint)KeyInterop.VirtualKeyFromKey(key), MAPVK_VK_TO_CHAR) & 0xFFFF);
        return typed > ' ' ? char.ToUpperInvariant(typed).ToString() : key.ToString();
    }

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static partial uint MapVirtualKey(uint code, uint mapType);
}

using Cotabby.Core.Focus;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Insertion;

/// <summary>
/// Checks that the field an insertion was meant for still has the keyboard. Synthetic keys go to
/// whatever window is in front, so keys sent after the user switched windows would land there.
/// </summary>
public static class ForegroundTarget
{
    /// <summary>True when the foreground window still holds <paramref name="target"/>.</summary>
    public static bool Holds(FocusedField target)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        return Accepts(target.ProcessId, foregroundPid, () => FieldInForegroundRoot(target.FieldRect, foreground));
    }

    /// <summary>
    /// The decision behind <see cref="Holds"/>. A field of the foreground process passes. So does
    /// one whose center lies in the foreground window, because a packaged app's window belongs
    /// to ApplicationFrameHost and a console's to conhost while the field reports the app's own
    /// process. A target without a process id cannot be checked and passes.
    /// </summary>
    public static bool Accepts(int targetPid, uint foregroundPid, Func<bool> fieldInForegroundRoot) =>
        targetPid == 0 || foregroundPid == (uint)targetPid || fieldInForegroundRoot();

    /// <summary>
    /// Whether the window under the field's center belongs to the same top-level window as the
    /// foreground one. Cotabby's own overlays are click-through, so they are never the window
    /// found there.
    /// </summary>
    private static bool FieldInForegroundRoot(ScreenRect field, IntPtr foreground)
    {
        if (field.IsEmpty) return false;
        var center = new POINT { x = (int)(field.X + field.Width / 2), y = (int)(field.Y + field.Height / 2) };
        var root = GetAncestor(foreground, GA_ROOT);
        if (root == IntPtr.Zero || !GetWindowRect(root, out var bounds)) return false;
        if (center.x < bounds.Left || center.x >= bounds.Right || center.y < bounds.Top || center.y >= bounds.Bottom)
        {
            return false;
        }
        var under = WindowFromPoint(center);
        return under != IntPtr.Zero && GetAncestor(under, GA_ROOT) == root;
    }
}

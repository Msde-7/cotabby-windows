using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Cotabby.Core.Input;
using Cotabby.Win32.Interop;
using Microsoft.Extensions.Logging;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Input;

/// <summary>
/// WH_KEYBOARD_LL implementation of <see cref="IKeyboardHook"/>.
/// </summary>
/// <remarks>
/// Two invariants matter here:
///
/// 1. The hook callback must run on a thread with a message pump — Windows
///    delivers WH_KEYBOARD_LL callbacks by posting into the installer thread's
///    queue. We start a dedicated background thread, install the hook on it,
///    and run GetMessage/DispatchMessage there so the callbacks actually fire.
///    Installing on the UI thread works too but stalls when WPF is doing layout.
///
/// 2. The callback must return as fast as possible. Windows enforces a per-hook
///    timeout (LowLevelHooksTimeout, default 300ms) and will silently unhook us
///    if we miss it. The callback only normalizes the event and raises the
///    public event synchronously — heavy work (LLM inference, UIA reads) must
///    NOT happen on this stack; subscribers should hand off via a channel or
///    SynchronizationContext.Post.
/// </remarks>
public sealed class KeyboardHook : IKeyboardHook
{
    // Escape hatch for automated end-to-end testing: SendInput sets LLKHF_INJECTED
    // and we normally drop those events to avoid a Tab-accept feedback loop.
    // The smoke harness needs the hook to observe synthesized typing, so it
    // sets COTABBY_ALLOW_INJECTED=1 before launch. Never set this in production.
    private static readonly bool AllowInjected =
        Environment.GetEnvironmentVariable("COTABBY_ALLOW_INJECTED") == "1";

    private readonly ILogger<KeyboardHook> _logger;
    private readonly object _gate = new();

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookHandle;
    private LowLevelKeyboardProc? _proc; // pinned by holding the delegate ref
    private bool _disposed;
    private volatile bool _running;

    // Keyboard state buffer reused across ToUnicodeEx calls. The hook fires from
    // a single thread (the message-pump thread) so we don't need locking, but
    // it's cleared per-call to keep the dead-key state from leaking across taps.
    private readonly byte[] _keyState = new byte[256];

    public KeyboardHook(ILogger<KeyboardHook> logger) => _logger = logger;

    public event EventHandler<KeyEventArgs>? KeyEvent;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            _running = true;

            var started = new ManualResetEventSlim(false);
            Exception? startError = null;

            _thread = new Thread(() =>
            {
                try
                {
                    _threadId = GetCurrentThreadId();
                    _proc = HookCallback;
                    _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                    if (_hookHandle == IntPtr.Zero)
                    {
                        throw new InvalidOperationException(
                            $"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
                    }
                    _logger.LogInformation(
                        "Keyboard hook installed (thread {ThreadId}, allowInjected={Allow}).",
                        _threadId, AllowInjected);
                    started.Set();

                    while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(in msg);
                        DispatchMessage(in msg);
                    }
                }
                catch (Exception ex)
                {
                    startError = ex;
                    started.Set();
                }
                finally
                {
                    if (_hookHandle != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(_hookHandle);
                        _hookHandle = IntPtr.Zero;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "Cotabby.KeyboardHook",
            };

            _thread.Start();
            started.Wait();
            if (startError is not null)
            {
                _running = false;
                throw startError;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;

            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            _thread?.Join(TimeSpan.FromSeconds(2));
            _thread = null;
            _threadId = 0;
            _logger.LogInformation("Keyboard hook removed.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>
    /// Test backdoor: raise a synthetic <see cref="IKeyboardHook.KeyEvent"/> as if
    /// the OS hook had fired. Used by the App's self-test when the sandbox blocks
    /// SendInput so we can still exercise the coordinator end-to-end. Never call
    /// from production code paths.
    /// </summary>
    public void FireSyntheticKey(KeyboardEvent ev)
    {
        var args = new KeyEventArgs(ev);
        KeyEvent?.Invoke(this, args);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // Ignore events we synthesize ourselves. SendInput sets LLKHF_INJECTED,
            // and re-entering acceptance would amplify a tab-to-accept loop.
            if ((data.flags & LLKHF_INJECTED) != 0 && !AllowInjected)
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            int msg = (int)wParam;
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
            if (!isDown && !isUp)
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            var (kind, ch) = Classify(data.vkCode, data.scanCode);
            bool nonShiftMod = (GetKeyState(VK_CONTROL) & 0x8000) != 0
                || (GetKeyState(VK_MENU) & 0x8000) != 0
                || (GetKeyState(VK_LWIN) & 0x8000) != 0
                || (GetKeyState(VK_RWIN) & 0x8000) != 0;

            var evt = new KeyboardEvent
            {
                Kind = kind,
                Character = ch,
                HasNonShiftModifier = nonShiftMod,
                IsKeyDown = isDown,
            };

            var args = new KeyEventArgs(evt);
            try
            {
                KeyEvent?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KeyEvent subscriber threw — swallowing to keep hook alive.");
                Debug.WriteLine($"KeyboardHook subscriber error: {ex}");
            }

            if (args.Suppress)
            {
                // Returning non-zero eats the event before it reaches the host app.
                return new IntPtr(1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hook callback threw; passing event through.");
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private (KeyKind kind, char ch) Classify(uint vk, uint scanCode)
    {
        switch (vk)
        {
            case VK_TAB: return (KeyKind.Tab, '\0');
            case VK_ESCAPE: return (KeyKind.Escape, '\0');
            case VK_RETURN: return (KeyKind.Enter, '\0');
            case VK_BACK: return (KeyKind.Backspace, '\0');
            case VK_DELETE: return (KeyKind.Delete, '\0');
            case VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN
                or VK_HOME or VK_END or VK_PRIOR or VK_NEXT:
                return (KeyKind.Arrow, '\0');
            case VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN:
                return (KeyKind.Other, '\0');
        }

        // Translate to a printable character honoring the current keyboard layout
        // and modifier state. Skip ToUnicodeEx if any non-shift modifier is held —
        // we don't want Ctrl+A to surface as 'a' for the coordinator.
        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0
            || (GetKeyState(VK_MENU) & 0x8000) != 0
            || (GetKeyState(VK_LWIN) & 0x8000) != 0
            || (GetKeyState(VK_RWIN) & 0x8000) != 0)
        {
            return (KeyKind.Other, '\0');
        }

        Array.Clear(_keyState);
        GetKeyboardState(_keyState);
        var layout = GetKeyboardLayout(0);
        var buf = new StringBuilder(8);
        int count = ToUnicodeEx(vk, scanCode, _keyState, buf, buf.Capacity, 0, layout);
        if (count > 0 && buf.Length > 0)
        {
            char c = buf[0];
            if (!char.IsControl(c))
            {
                return (KeyKind.Character, c);
            }
        }
        return (KeyKind.Other, '\0');
    }
}

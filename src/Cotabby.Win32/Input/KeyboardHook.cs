using System.Runtime.InteropServices;
using Cotabby.Core.Input;
using Microsoft.Extensions.Logging;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Input;

/// <summary>
/// WH_KEYBOARD_LL implementation of <see cref="IKeyboardHook"/>.
/// </summary>
/// <remarks>
/// Windows delivers the callbacks through the message queue of the thread that installed the
/// hook, so the hook lives on a dedicated thread with its own message loop. The UI thread would
/// stall the callbacks during layout. The thread runs at the highest priority so a busy machine
/// does not push a callback past its deadline.
/// <para>
/// The callback must return quickly. Windows silently removes a hook that exceeds
/// LowLevelHooksTimeout, which is 300 ms by default. Subscribers are called synchronously and
/// must hand inference and UI Automation reads to another thread. A <see cref="HookWatchdog"/>
/// notices a hook Windows dropped anyway and installs it again.
/// </para>
/// </remarks>
public sealed class KeyboardHook : IKeyboardHook
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromSeconds(1);

    /// <summary>Posted to the hook thread to install the hook again.</summary>
    private const uint WM_REINSTALL_HOOK = WM_APP + 1;

    private readonly ILogger<KeyboardHook> _logger;
    private readonly object _gate = new();
    private readonly HookWatchdog _watchdog = new();

    private Thread? _thread;
    private volatile uint _threadId;
    private IntPtr _hookHandle;
    private IntPtr _rawInputWindow;
    private LowLevelKeyboardProc? _proc; // Held so the delegate is not collected.
    private Timer? _watchdogTimer;
    private bool _disposed;
    private volatile bool _running;

    // Set between Start and Stop, so the watchdog can bring back a hook thread that died.
    private volatile bool _wanted;

    private long _callbacks;
    private long _rawKeys;

    // Buffers reused across ToUnicodeEx calls. Only the hook thread touches them.
    private readonly byte[] _keyState = new byte[256];
    private readonly char[] _translated = new char[8];

    // Keys currently down, used to tell auto-repeat from a second press.
    private readonly HeldKeys _heldKeys = new();

    // A dead key was typed and the character it combines with has not come yet.
    private bool _afterDeadKey;

    // The key being classified, read by one cached translate delegate instead of a closure per key.
    private uint _pendingVk;
    private uint _pendingScanCode;
    private readonly Func<char> _translatePending;

    public KeyboardHook(ILogger<KeyboardHook> logger)
    {
        _logger = logger;
        _translatePending = () => Translate(_pendingVk, _pendingScanCode);
    }

    public event EventHandler<KeyEventArgs>? KeyEvent;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _wanted = true;
            if (_running) return;

            // A Stop that timed out left its thread running with WM_QUIT queued. A second hook
            // installed beside it would see every key twice, so it must be gone first.
            if (_thread is { IsAlive: true } stale && !stale.Join(StopTimeout))
            {
                throw new InvalidOperationException("The previous keyboard hook thread has not exited.");
            }
            _thread = null;
            _threadId = 0;
            _running = true;

            var started = new ManualResetEventSlim(false);
            Exception? startError = null;

            _thread = new Thread(() => RunHookThread(started, error => startError = error))
            {
                IsBackground = true,
                Name = "Cotabby.KeyboardHook",
                Priority = ThreadPriority.Highest,
            };

            _thread.Start();
            started.Wait();
            if (startError is not null)
            {
                _running = false;
                _wanted = false;
                _thread.Join(StopTimeout);
                _thread = null;
                throw startError;
            }

            _watchdogTimer ??= new Timer(_ => CheckHook(), null, WatchdogPeriod, WatchdogPeriod);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _wanted = false;
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
            if (!_running) return;
            _running = false;

            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
            if (_thread?.Join(StopTimeout) == false)
            {
                // The thread is stuck in a callback and will exit when it returns. It stays
                // referenced so Start waits for it instead of installing a second hook.
                _logger.LogWarning("Keyboard hook thread did not exit within {Timeout}.", StopTimeout);
                return;
            }
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

    private void RunHookThread(ManualResetEventSlim started, Action<Exception> fail)
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
            bool watched = RegisterRawKeyboard();
            _logger.LogInformation(
                "Keyboard hook installed (thread {ThreadId}, watchdog={Watched}).", _threadId, watched);
            started.Set();

            while (true)
            {
                int result = GetMessage(out MSG msg, IntPtr.Zero, 0, 0);
                if (result == 0) break;
                if (result == -1)
                {
                    // The loop cannot continue, so the hook is marked stopped and the watchdog starts
                    // a fresh thread.
                    _logger.LogError("Keyboard hook message loop failed: {Error}.", Marshal.GetLastWin32Error());
                    _running = false;
                    break;
                }
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_REINSTALL_HOOK)
                {
                    ReinstallHook();
                    continue;
                }
                if (msg.message == WM_INPUT) Interlocked.Increment(ref _rawKeys);
                TranslateMessage(in msg);
                DispatchMessage(in msg);
            }
        }
        catch (Exception ex)
        {
            if (started.IsSet)
            {
                _logger.LogError(ex, "Keyboard hook thread failed.");
                _running = false;
            }
            else
            {
                fail(ex);
                started.Set();
            }
        }
        finally
        {
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            UnregisterRawKeyboard();
        }
    }

    /// <summary>
    /// Has raw keyboard input delivered to a message-only window on the hook thread, so the
    /// watchdog can count keys without the hook. False when that is unavailable, which leaves the
    /// hook unwatched.
    /// </summary>
    private bool RegisterRawKeyboard()
    {
        // "Message" is the system class for message-only windows. WM_INPUT is posted, so the
        // message loop counts it before the class's default procedure cleans it up.
        _rawInputWindow = CreateWindowEx(
            0, "Message", null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_rawInputWindow == IntPtr.Zero)
        {
            _logger.LogWarning("Keyboard hook watchdog is off: no message window ({Error}).", Marshal.GetLastWin32Error());
            return false;
        }
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = HID_USAGE_PAGE_GENERIC,
            usUsage = HID_USAGE_GENERIC_KEYBOARD,
            dwFlags = RIDEV_INPUTSINK,
            hwndTarget = _rawInputWindow,
        };
        if (RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>())) return true;
        _logger.LogWarning("Keyboard hook watchdog is off: raw input unavailable ({Error}).", Marshal.GetLastWin32Error());
        DestroyWindow(_rawInputWindow);
        _rawInputWindow = IntPtr.Zero;
        return false;
    }

    private void UnregisterRawKeyboard()
    {
        if (_rawInputWindow == IntPtr.Zero) return;
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = HID_USAGE_PAGE_GENERIC,
            usUsage = HID_USAGE_GENERIC_KEYBOARD,
            dwFlags = RIDEV_REMOVE,
            hwndTarget = IntPtr.Zero,
        };
        RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        DestroyWindow(_rawInputWindow);
        _rawInputWindow = IntPtr.Zero;
    }

    /// <summary>
    /// Runs on the hook thread between callbacks. The new hook goes in before the old one comes
    /// out, so no key slips past in between. Unhooking a hook Windows already removed fails
    /// harmlessly.
    /// </summary>
    private void ReinstallHook()
    {
        var fresh = SetWindowsHookEx(WH_KEYBOARD_LL, _proc!, GetModuleHandle(null), 0);
        if (fresh == IntPtr.Zero)
        {
            _logger.LogError("Reinstalling the keyboard hook failed: {Error}.", Marshal.GetLastWin32Error());
            return;
        }
        if (_hookHandle != IntPtr.Zero) UnhookWindowsHookEx(_hookHandle);
        _hookHandle = fresh;
        _heldKeys.Clear();
        _logger.LogInformation("Keyboard hook reinstalled.");
    }

    /// <summary>Runs on a timer thread once a period.</summary>
    private void CheckHook()
    {
        try
        {
            if (!_running)
            {
                if (_wanted && !_disposed)
                {
                    _logger.LogWarning("Keyboard hook thread ended unexpectedly. Starting it again.");
                    Start();
                }
                return;
            }

            bool dropped;
            lock (_watchdog) dropped = _watchdog.Check(Interlocked.Read(ref _callbacks), Interlocked.Read(ref _rawKeys));
            if (!dropped) return;
            _logger.LogWarning(
                "Keyboard hook heard no keys while the keyboard was in use. Windows likely removed it after a slow " +
                "callback, or an elevated window has focus. Reinstalling it.");
            if (_threadId != 0) PostThreadMessage(_threadId, WM_REINSTALL_HOOK, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Keyboard hook watchdog failed.");
        }
    }

    private unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Interlocked.Increment(ref _callbacks);
        if (nCode < 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        try
        {
            var data = *(KBDLLHOOKSTRUCT*)lParam;

            // Cotabby's own SendInput is marked. Reacting to it would loop. Input that other
            // software injects is a person typing through it and is handled like a key press.
            if ((data.flags & LLKHF_INJECTED) != 0
                && (data.dwExtraInfo == CotabbyInputMarker || data.dwExtraInfo == CotabbyRestoreMarker))
            {
                // A modifier pressed again after a batch is dropped when the user let go of it
                // before the press arrived, so it is not left down. Events reach the hook in order,
                // so the release has been seen by now.
                if (data.dwExtraInfo == CotabbyRestoreMarker && !_heldKeys.IsHeld(data.vkCode)) return new IntPtr(1);
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            int msg = (int)wParam;
            bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
            if (!isDown && !isUp)
            {
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            _pendingVk = data.vkCode;
            _pendingScanCode = data.scanCode;
            var key = KeyClassifier.Classify((int)data.vkCode, CurrentModifiers(), isDown, _translatePending);
            if (isDown) KeyTranslation.Untranslated((int)data.vkCode, ref _afterDeadKey);
            bool isRepeat = isDown && _heldKeys.Down(data.vkCode, data.time);
            if (isUp) _heldKeys.Up(data.vkCode);

            var evt = new KeyboardEvent
            {
                Kind = key.Kind,
                Character = key.Character,
                IsKeyDown = isDown,
                VirtualKey = (int)data.vkCode,
                Modifiers = key.Modifiers,
                IsRepeat = isRepeat,
            };

            var args = new KeyEventArgs(evt);
            Raise(args);

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

    /// <summary>
    /// Calls each subscriber on its own. A plain multicast invoke stops at the first throwing
    /// handler, and the later ones, among them the coordinator that swallows the accept key,
    /// would never see the key.
    /// </summary>
    private void Raise(KeyEventArgs args)
    {
        if (KeyEvent is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<KeyEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A KeyEvent subscriber threw. The other subscribers still see the key.");
            }
        }
    }

    private static KeyModifiers CurrentModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (IsDown(VK_CONTROL)) modifiers |= KeyModifiers.Control;
        if (IsDown(VK_MENU)) modifiers |= KeyModifiers.Alt;
        if (IsDown(VK_SHIFT)) modifiers |= KeyModifiers.Shift;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) modifiers |= KeyModifiers.Win;
        return modifiers;
    }

    private const uint VK_PACKET = 0xE7;

    private static bool IsDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// The character the foreground window's layout gives the key, or '\0'. The hook thread's own
    /// layout never follows an input language switch.
    /// </summary>
    private char Translate(uint vk, uint scanCode)
    {
        // The touch keyboard, the emoji panel and text-sending tools type through VK_PACKET, which
        // carries the UTF-16 unit in the scan code. Half of a surrogate pair is no character alone.
        if (vk == VK_PACKET) return KeyTranslation.FromPacket((char)(scanCode & 0xFFFF));
        GetKeyboardState(_keyState);
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        // The state is kept so the host still applies a dead key, which also means the key after
        // it translates here as its plain letter while the host shows an accented one.
        int count = ToUnicodeEx(
            vk, scanCode, _keyState, _translated, _translated.Length, TOUNICODE_KEEP_STATE, layout);
        return KeyTranslation.FromLayout(count, _translated[0], ref _afterDeadKey);
    }
}

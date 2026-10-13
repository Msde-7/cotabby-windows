using System.Diagnostics;
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
/// stall the callbacks during layout.
/// <para>
/// The callback must return quickly. Windows silently removes a hook that exceeds
/// LowLevelHooksTimeout, which is 300 ms by default. Subscribers are called synchronously and
/// must hand inference and UI Automation reads to another thread.
/// </para>
/// </remarks>
public sealed class KeyboardHook : IKeyboardHook
{
    // Injected events are dropped so the app never reacts to its own SendInput. An automated
    // end-to-end run sets COTABBY_ALLOW_INJECTED=1 so the hook sees synthesized typing.
    private static readonly bool AllowInjected =
        Environment.GetEnvironmentVariable("COTABBY_ALLOW_INJECTED") == "1";

    private readonly ILogger<KeyboardHook> _logger;
    private readonly object _gate = new();

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookHandle;
    private LowLevelKeyboardProc? _proc; // Held so the delegate is not collected.
    private bool _disposed;
    private volatile bool _running;

    // Buffers reused across ToUnicodeEx calls. Only the hook thread touches them.
    private readonly byte[] _keyState = new byte[256];
    private readonly char[] _translated = new char[8];

    // Keys currently down, used to tell auto-repeat from a second press.
    private readonly HashSet<uint> _heldKeys = new();

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

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // SendInput sets LLKHF_INJECTED. Reacting to the app's own input would loop.
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

            var key = KeyClassifier.Classify(
                (int)data.vkCode, CurrentModifiers(), isDown, () => Translate(data.vkCode, data.scanCode));
            bool isRepeat = isDown && !_heldKeys.Add(data.vkCode);
            if (isUp) _heldKeys.Remove(data.vkCode);

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
            try
            {
                KeyEvent?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A KeyEvent subscriber threw. The hook stays installed.");
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

    private static KeyModifiers CurrentModifiers()
    {
        var modifiers = KeyModifiers.None;
        if (IsDown(VK_CONTROL)) modifiers |= KeyModifiers.Control;
        if (IsDown(VK_MENU)) modifiers |= KeyModifiers.Alt;
        if (IsDown(VK_SHIFT)) modifiers |= KeyModifiers.Shift;
        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) modifiers |= KeyModifiers.Win;
        return modifiers;
    }

    private static bool IsDown(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// The character the foreground window's layout gives the key, or '\0'. The hook thread's own
    /// layout never follows an input language switch.
    /// </summary>
    private char Translate(uint vk, uint scanCode)
    {
        GetKeyboardState(_keyState);
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        int count = ToUnicodeEx(
            vk, scanCode, _keyState, _translated, _translated.Length, TOUNICODE_KEEP_STATE, layout);
        return count > 0 ? _translated[0] : '\0';
    }
}

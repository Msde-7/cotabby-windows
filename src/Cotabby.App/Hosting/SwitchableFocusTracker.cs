using Cotabby.Core.Focus;

namespace Cotabby.App.Hosting;

/// <summary>
/// A focus tracker the host can switch off while Cotabby is off, so nothing watches focus through
/// UI Automation then. Chromium and Electron apps keep their accessibility mode on while a client
/// listens, which costs them memory and speed.
/// </summary>
/// <remarks>
/// Switching off tells every listener that focus left (null), as leaving the last supported field
/// would, so none keeps a session for a field from before. Switching on tells them where focus is
/// now, as a focus change would. <see cref="Start"/> and <see cref="Stop"/> stay with the owner
/// that runs the tracker, the coordinator, and tracking runs only while both allow it.
/// </remarks>
public sealed class SwitchableFocusTracker : IFocusTracker
{
    private readonly IFocusTracker _inner;
    private readonly object _gate = new();
    private bool _started;
    private bool _enabled = true;

    public SwitchableFocusTracker(IFocusTracker inner)
    {
        _inner = inner;
        _inner.FocusChanged += OnInnerFocusChanged;
    }

    public FocusedField? Current => _inner.Current;

    public event EventHandler<FocusedField?>? FocusChanged;

    public FocusedField? Refresh(bool readHostText = false) => _inner.Refresh(readHostText);

    public void Start()
    {
        lock (_gate)
        {
            _started = true;
            if (_enabled) _inner.Start();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _started = false;
            _inner.Stop();
        }
    }

    /// <summary>
    /// Turns tracking on or off. Starting reads the focused field through UI Automation, which can
    /// block, and a read of Cotabby's own window from its UI thread can hang, so call it off the UI thread.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            if (!_started) return;
            if (enabled)
            {
                // Starting raises FocusChanged with where focus is now.
                _inner.Start();
            }
            else
            {
                _inner.Stop();
                FocusChanged?.Invoke(this, null);
            }
        }
    }

    public void Dispose()
    {
        _inner.FocusChanged -= OnInnerFocusChanged;
        _inner.Dispose();
    }

    private void OnInnerFocusChanged(object? sender, FocusedField? field) => FocusChanged?.Invoke(this, field);
}

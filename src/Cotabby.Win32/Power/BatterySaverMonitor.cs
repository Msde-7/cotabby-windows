using Windows.System.Power;

namespace Cotabby.Win32.Power;

/// <summary>
/// Tracks battery saver, which Windows 11 24H2 renamed Energy Saver and allows on mains power.
/// </summary>
/// <remarks>
/// The state comes from <see cref="PowerManager.EnergySaverStatus"/>, which reports Disabled, Off
/// or On. An earlier version decoded the raw GUID_ENERGY_SAVER_STATUS notification and counted any
/// value above zero as on, but Windows sends 1 there when Energy Saver is off, so Cotabby paused
/// itself on PCs where Energy Saver had never been turned on. The legacy battery saver flag is
/// still read too, and a slow poll covers a missed change event.
/// </remarks>
public sealed class BatterySaverMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Timer _poll;
    private readonly bool _subscribed;
    private bool _last;
    private int _disposed;

    public BatterySaverMonitor()
    {
        _last = IsOn;
        try
        {
            PowerManager.EnergySaverStatusChanged += OnStatusChanged;
            _subscribed = true;
        }
        catch (Exception)
        {
            // Without the event the poll still notices a change, only later.
        }
        _poll = new Timer(_ => Check(), null, PollInterval, PollInterval);
    }

    /// <summary>Raised on a thread-pool or system thread with the new state.</summary>
    public event Action<bool>? Changed;

    public bool IsOn => EnergySaverOn() || PowerStatus.IsBatterySaverOn;

    private static bool EnergySaverOn()
    {
        try
        {
            return PowerManager.EnergySaverStatus == EnergySaverStatus.On;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnStatusChanged(object? sender, object e) => Check();

    private void Check()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        bool on = IsOn;
        lock (_gate)
        {
            if (on == _last) return;
            _last = on;
        }
        Changed?.Invoke(on);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _poll.Dispose();
        if (_subscribed)
        {
            try
            {
                PowerManager.EnergySaverStatusChanged -= OnStatusChanged;
            }
            catch (Exception)
            {
            }
        }
    }
}

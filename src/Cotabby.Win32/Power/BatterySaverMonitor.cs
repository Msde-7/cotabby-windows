using System.Runtime.InteropServices;

namespace Cotabby.Win32.Power;

/// <summary>
/// Tracks battery saver, which Windows 11 24H2 renamed Energy Saver and allows on mains power.
/// It subscribes to both status settings, and polls instead when Windows refuses the subscription.
/// </summary>
public sealed partial class BatterySaverMonitor : IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtPowerSettingChange = 0x8013;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly Guid PowerSavingStatus = new("e00958c0-c213-4ace-ac77-fecced2eeea5");
    private static readonly Guid EnergySaverStatus = new("550e8400-e29b-41d4-a716-446655440000");

    private readonly DeviceNotifyCallbackRoutine _callback;
    private readonly IntPtr _parameters;
    private readonly IntPtr[] _registrations;
    private readonly Timer? _poll;
    private volatile bool _energySaverOn;
    private bool _last;
    private int _disposed;

    public BatterySaverMonitor()
    {
        _last = IsOn;
        _callback = OnNotify;
        _parameters = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceNotifySubscribeParameters>());
        Marshal.StructureToPtr(
            new DeviceNotifySubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(_callback) },
            _parameters,
            fDeleteOld: false);

        _registrations = [Register(PowerSavingStatus), Register(EnergySaverStatus)];
        if (_registrations[0] == IntPtr.Zero)
        {
            _poll = new Timer(_ => Check(), null, PollInterval, PollInterval);
        }
    }

    /// <summary>Raised on a thread-pool or system thread with the new state.</summary>
    public event Action<bool>? Changed;

    public bool IsOn => _energySaverOn || PowerStatus.IsBatterySaverOn;

    private IntPtr Register(Guid setting) =>
        PowerSettingRegisterNotification(ref setting, DeviceNotifyCallback, _parameters, out var handle) == 0
            ? handle
            : IntPtr.Zero;

    private uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        if (type != PbtPowerSettingChange) return 0;
        // POWERBROADCAST_SETTING: the setting's GUID, the data length, then the data.
        if (setting != IntPtr.Zero && Marshal.PtrToStructure<Guid>(setting) == EnergySaverStatus
            && Marshal.ReadInt32(setting, 16) >= sizeof(int))
        {
            _energySaverOn = Marshal.ReadInt32(setting, 20) > 0;
        }
        Check();
        return 0;
    }

    private void Check()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        bool on = IsOn;
        lock (_callback)
        {
            if (on == _last) return;
            _last = on;
        }
        Changed?.Invoke(on);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _poll?.Dispose();
        foreach (var registration in _registrations)
        {
            if (registration != IntPtr.Zero) PowerSettingUnregisterNotification(registration);
        }
        Marshal.FreeHGlobal(_parameters);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSettingRegisterNotification(
        ref Guid settingGuid, uint flags, IntPtr recipient, out IntPtr registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSettingUnregisterNotification(IntPtr registrationHandle);
}

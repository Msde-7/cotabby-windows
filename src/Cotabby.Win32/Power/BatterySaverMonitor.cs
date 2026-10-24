using System.Runtime.InteropServices;

namespace Cotabby.Win32.Power;

/// <summary>
/// Raises <see cref="Changed"/> when battery saver turns on or off. It subscribes to the
/// power-saving-status setting, and polls instead when that subscription is refused.
/// </summary>
public sealed partial class BatterySaverMonitor : IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtPowerSettingChange = 0x8013;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly Guid PowerSavingStatus = new("e00958c0-c213-4ace-ac77-fecced2eeea5");

    private readonly DeviceNotifyCallbackRoutine _callback;
    private readonly IntPtr _parameters;
    private readonly IntPtr _registration;
    private readonly Timer? _poll;
    private bool _last;
    private int _disposed;

    public BatterySaverMonitor()
    {
        _last = PowerStatus.IsBatterySaverOn;
        _callback = OnNotify;
        _parameters = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceNotifySubscribeParameters>());
        Marshal.StructureToPtr(
            new DeviceNotifySubscribeParameters
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_callback),
                Context = IntPtr.Zero,
            },
            _parameters,
            fDeleteOld: false);

        var guid = PowerSavingStatus;
        if (PowerSettingRegisterNotification(ref guid, DeviceNotifyCallback, _parameters, out _registration) != 0)
        {
            _registration = IntPtr.Zero;
            _poll = new Timer(_ => Check(), null, PollInterval, PollInterval);
        }
    }

    /// <summary>Raised on a thread-pool or system thread with the new state.</summary>
    public event Action<bool>? Changed;

    private uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        if (type == PbtPowerSettingChange) Check();
        return 0;
    }

    private void Check()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        bool on = PowerStatus.IsBatterySaverOn;
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
        if (_registration != IntPtr.Zero) PowerSettingUnregisterNotification(_registration);
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

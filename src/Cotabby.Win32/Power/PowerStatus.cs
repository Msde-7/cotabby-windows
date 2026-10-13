using System.Runtime.InteropServices;

namespace Cotabby.Win32.Power;

/// <summary>Reads the live power state. The call is cheap enough to make per request.</summary>
public static partial class PowerStatus
{
    /// <summary>True while Windows battery saver (energy saver) is on.</summary>
    public static bool IsBatterySaverOn =>
        GetSystemPowerStatus(out var status) && (status.SystemStatusFlag & 1) != 0;

    /// <summary>True when running on battery. Unknown line status counts as plugged in.</summary>
    public static bool IsOnBattery =>
        GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

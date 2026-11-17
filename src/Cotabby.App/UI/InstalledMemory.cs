using System.Runtime.InteropServices;

namespace Cotabby.App.UI;

/// <summary>
/// The memory installed in this PC, which the onboarding recommender reads. The installed amount is
/// what users know their PC by. The usable total is a little lower because firmware and graphics
/// reserve some, which would make a 16 GB PC read as below 16 GB.
/// </summary>
internal static partial class InstalledMemory
{
    public static ulong Bytes()
    {
        if (GetPhysicallyInstalledSystemMemory(out ulong kilobytes) && kilobytes > 0) return kilobytes * 1024;
        // Some virtual machines report no installed memory, so fall back to the usable total.
        return (ulong)Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
}

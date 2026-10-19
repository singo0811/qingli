using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TidyWin.Platform;

/// <summary>
/// Gives back this program's own memory. Called when the window is minimized: nobody is looking, so a full
/// collection and a trimmed working set cost nothing visible, and pages come back in on demand.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SelfTrim
{
    public static void Run()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        K32EmptyWorkingSet(GetCurrentProcess());
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet")] private static extern bool K32EmptyWorkingSet(IntPtr process);
}

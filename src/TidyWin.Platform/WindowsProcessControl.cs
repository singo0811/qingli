using System.Diagnostics;
using System.Runtime.InteropServices;
using TidyWin.Core.Memory;

namespace TidyWin.Platform;

public sealed class WindowsProcessControl : IProcessControl
{
    private const uint PROCESS_SET_QUOTA = 0x0100;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    public TrimOutcome Trim(int pid)
    {
        var h = WindowsMemorySource.OpenProcess(PROCESS_SET_QUOTA | WindowsMemorySource.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error() switch
            {
                ErrorAccessDenied => TrimOutcome.AccessDenied,
                ErrorInvalidParameter => TrimOutcome.Gone,
                _ => TrimOutcome.Failed,
            };
        }

        try { return K32EmptyWorkingSet(h) ? TrimOutcome.Trimmed : TrimOutcome.Failed; }
        finally { WindowsMemorySource.CloseHandle(h); }
    }

    public bool RequestClose(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.CloseMainWindow(); // posts WM_CLOSE: the app decides, unsaved work still gets its prompt
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    private static extern bool K32EmptyWorkingSet(IntPtr process);
}

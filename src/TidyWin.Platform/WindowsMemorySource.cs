using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TidyWin.Core.Memory;

namespace TidyWin.Platform;

public sealed class WindowsMemorySource : IMemorySource
{
    public MemorySnapshot Read()
    {
        var now = DateTime.UtcNow;
        return new MemorySnapshot(now, ReadSystem(), ReadProcesses(), ForegroundPid());
    }

    private static SystemMemory ReadSystem()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);

        var perf = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
        long cache = 0, commitUsed = 0, commitLimit = 0;
        if (K32GetPerformanceInfo(out perf, perf.cb))
        {
            var page = (long)perf.PageSize;
            cache = (long)perf.SystemCache * page;
            commitUsed = (long)perf.CommitTotal * page;
            commitLimit = (long)perf.CommitLimit * page;
        }

        return new SystemMemory((long)mem.ullTotalPhys, (long)mem.ullAvailPhys, cache, commitUsed, commitLimit);
    }

    private static IReadOnlyList<ProcInfo> ReadProcesses()
    {
        var windows = VisibleWindowTitles();
        var list = new List<ProcInfo>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var cpu = -1.0;
                    try { cpu = p.TotalProcessorTime.TotalSeconds; } catch { /* protected process */ }

                    windows.TryGetValue(p.Id, out var title);
                    list.Add(new ProcInfo(
                        p.Id,
                        p.ProcessName,
                        QueryImagePath(p.Id),
                        p.SessionId,
                        p.WorkingSet64,
                        p.PrivateMemorySize64,
                        cpu,
                        title is not null,
                        title));
                }
                catch
                {
                    // The process ended while we were looking at it.
                }
            }
        }
        return list;
    }

    private static int? ForegroundPid()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    /// <summary>One pass over all top-level windows. A program "has a window" when it owns a visible, titled, non-tool window.</summary>
    private static Dictionary<int, string> VisibleWindowTitles()
    {
        var map = new Dictionary<int, string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;

            var len = GetWindowTextLengthW(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(h, sb, sb.Capacity);

            GetWindowThreadProcessId(h, out var pid);
            if (pid != 0 && !map.ContainsKey((int)pid)) map[(int)pid] = sb.ToString();
            return true;
        }, IntPtr.Zero);
        return map;
    }

    internal static string? QueryImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo")]
    private static extern bool K32GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
}

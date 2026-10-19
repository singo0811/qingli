using System.Runtime.InteropServices;
using TidyWin.Core.Status;

namespace TidyWin.Platform;

public sealed record SystemSnapshot(
    double MemoryPercent,
    ulong MemoryTotalBytes,
    double SystemDiskUsedPercent,
    ulong SystemDiskTotalBytes,
    ulong SystemDiskFreeBytes,
    double UptimeDays);

/// <summary>Reads what the status page needs. All Win32 calls for this live here.</summary>
public static class SystemReader
{
    public static SystemSnapshot Read()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        var memPercent = 0.0;
        ulong memTotal = 0;
        if (GlobalMemoryStatusEx(ref mem))
        {
            memPercent = mem.dwMemoryLoad;
            memTotal = mem.ullTotalPhys;
        }

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var drive = Path.GetPathRoot(string.IsNullOrEmpty(systemRoot) ? @"C:\Windows" : systemRoot)!;

        double diskPercent = 0;
        ulong total = 0, free = 0;
        if (GetDiskFreeSpaceExW(drive, out _, out var t, out var f))
        {
            total = t;
            free = f;
            diskPercent = total == 0 ? 0 : (double)(total - free) / total * 100.0;
        }

        var uptimeDays = GetTickCount64() / 86_400_000.0;
        return new SystemSnapshot(memPercent, memTotal, diskPercent, total, free, uptimeDays);
    }

    public static HealthInputs ToHealthInputs(SystemSnapshot s, double cpuPercent) =>
        new(cpuPercent, s.MemoryPercent, s.SystemDiskUsedPercent, BatteryHealthPercent: null, s.UptimeDays, RebootPending: false);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong free, out ulong total, out ulong totalFree);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}

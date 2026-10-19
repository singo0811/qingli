using System.Diagnostics;
using System.Runtime.InteropServices;
using TidyWin.Core.Disk;
using TidyWin.Core.Optimize;
using TidyWin.Core.Localization;

namespace TidyWin.Platform;

/// <summary>Sends one file or folder to the Recycle Bin so it can be put back. Windows asks before anything is deleted for good.</summary>
public sealed class ShellRecycler : IRecycler
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000; // if it is too big for the bin, ask instead of deleting for good

    public string? Recycle(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return Loc.T("它已经不存在了");

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_WANTNUKEWARNING,
        };
        var rc = SHFileOperationW(ref op);
        if (op.fAnyOperationsAborted) return Loc.T("已取消");
        if (rc != 0) return Loc.F("Windows 没能删除它(错误码 {0:X})。可能正被占用或没有权限。",rc);
        return File.Exists(path) || Directory.Exists(path) ? Loc.T("没有删除成功") : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);
}

/// <summary>The four small fixes. None touches personal files; the confirmations live in the page, before these run.</summary>
public sealed class WindowsOptimizer : IOptimizer
{
    public long? RecycleBinBytes()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? info.i64Size : null;
    }

    public Task<OptimizeResult> RunAsync(string taskId, CancellationToken ct = default) =>
        Task.Run(async () => taskId switch
        {
            OptimizeCatalog.Recycle => EmptyRecycleBin(),
            OptimizeCatalog.Dns => await FlushDns(ct),
            OptimizeCatalog.Explorer => await RestartExplorer(deleteIconCaches: false),
            OptimizeCatalog.IconCache => await RestartExplorer(deleteIconCaches: true),
            _ => new OptimizeResult(false, Loc.T("不认识这项任务")),
        }, ct);

    private OptimizeResult EmptyRecycleBin()
    {
        var before = RecycleBinBytes();
        const uint NoConfirmation = 1, NoProgress = 2, NoSound = 4;
        var rc = SHEmptyRecycleBinW(IntPtr.Zero, null, NoConfirmation | NoProgress | NoSound);
        // 0x8000000F (S_OK variants) is returned when it was already empty.
        if (rc != 0 && (uint)rc != 0x8000000F) return new OptimizeResult(false, Loc.F("清空失败(错误码 {0:X})。可能有文件正被占用。",rc));
        return new OptimizeResult(true, before is > 0 ? Loc.F("已清空,释放了 {0}。",Format(before.Value)) : Loc.T("回收站本来就是空的。"));
    }

    private static async Task<OptimizeResult> FlushDns(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig.exe", "/flushdns") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi);
            if (p is null) return new OptimizeResult(false, Loc.T("没能启动 ipconfig"));
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? new OptimizeResult(true, Loc.T("已刷新 DNS 缓存。")) : new OptimizeResult(false, Loc.T("刷新没有成功,可能需要管理员权限。"));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new OptimizeResult(false, e.Message);
        }
    }

    private static async Task<OptimizeResult> RestartExplorer(bool deleteIconCaches)
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                try { if (p.SessionId == session) p.Kill(); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        await Task.Delay(800);

        long freed = 0;
        if (deleteIconCaches) freed = DeleteIconCaches();

        // Windows normally brings the shell back by itself. Only start it ourselves if it did not.
        for (var i = 0; i < 12; i++)
        {
            if (Process.GetProcessesByName("explorer").Any(p => { using (p) return p.SessionId == session; })) break;
            await Task.Delay(500);
            if (i == 11)
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { }
            }
        }

        return new OptimizeResult(true, deleteIconCaches
            ? Loc.F("已重建。清掉了 {0} 的旧缓存,桌面和任务栏已重新启动。",Format(freed))
            : Loc.T("桌面和任务栏已重新启动。"));
    }

    /// <summary>Only the three well-known cache file patterns, in their two well-known folders. Nothing else is ever touched.</summary>
    private static long DeleteIconCaches()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var explorer = Path.Combine(local, "Microsoft", "Windows", "Explorer");
        long freed = 0;

        void Delete(string file)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
                var len = info.Length;
                info.Delete();
                freed += len;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        Delete(Path.Combine(local, "IconCache.db"));
        if (Directory.Exists(explorer))
        {
            foreach (var pattern in new[] { "iconcache_*.db", "thumbcache_*.db" })
                foreach (var f in Directory.EnumerateFiles(explorer, pattern)) Delete(f);
        }
        return freed;
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);
}

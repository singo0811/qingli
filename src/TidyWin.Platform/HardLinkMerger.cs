using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TidyWin.Core.Disk;
using TidyWin.Core.Localization;

namespace TidyWin.Platform;

/// <summary>
/// Turns a second copy of a file into a hard link to the first. Nothing disappears: both paths still open the same
/// content, programs keep working, and the disk holds it once. The content is compared in full before anything changes.
/// </summary>
public sealed class HardLinkMerger : IFileMerger
{
    public string? Merge(string duplicate, string original)
    {
        try
        {
            if (!File.Exists(duplicate) || !File.Exists(original)) return Loc.T("文件已经不存在了");

            var d = new FileInfo(duplicate);
            var o = new FileInfo(original);
            if (d.LinkTarget is not null || o.LinkTarget is not null) return Loc.T("其中一个是快捷方式,不处理");
            if (d.Length != o.Length) return Loc.T("两份的大小已经不一样了,没有合并");
            if (!string.Equals(Path.GetPathRoot(d.FullName), Path.GetPathRoot(o.FullName), StringComparison.OrdinalIgnoreCase))
                return Loc.T("不在同一个盘上,无法合并");
            if (SameFile(duplicate, original)) return null; // already one file

            if (!SameContent(d.FullName, o.FullName)) return Loc.T("两份的内容并不完全一样,没有合并");

            // Make the link next to the duplicate under another name, then swap it in. If the swap fails nothing was lost.
            var temp = d.FullName + ".tidywin-link";
            if (File.Exists(temp)) File.Delete(temp);
            if (!CreateHardLinkW(temp, o.FullName, IntPtr.Zero))
                return Loc.F("这个磁盘不支持合并(云同步文件夹、U 盘、网络盘常见,错误 {0})",Marshal.GetLastWin32Error());

            try
            {
                if ((d.Attributes & FileAttributes.ReadOnly) != 0) d.Attributes &= ~FileAttributes.ReadOnly;
                File.Move(temp, d.FullName, overwrite: true);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (IOException) { }
                return Loc.T("文件正被其他程序使用或没有权限,没有合并");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Loc.T("没能完成:") + e.Message;
        }
    }

    /// <summary>True when both paths lead to the same file on disk (they are hard links of each other).</summary>
    public static bool SameFile(string a, string b)
    {
        try
        {
            return Identity(a) is { } x && Identity(b) is { } y && x == y;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (uint Volume, uint High, uint Low)? Identity(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(h, out var info) ? (info.dwVolumeSerialNumber, info.nFileIndexHigh, info.nFileIndexLow) : null;
    }

    private static bool SameContent(string a, string b)
    {
        const int Size = 4 * 1024 * 1024;
        using var x = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        using var y = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var bx = new byte[Size];
        var by = new byte[Size];
        while (true)
        {
            var n = x.ReadAtLeast(bx, Size, throwOnEndOfStream: false);
            var m = y.ReadAtLeast(by, Size, throwOnEndOfStream: false);
            if (n != m) return false;
            if (n == 0) return true;
            if (!bx.AsSpan(0, n).SequenceEqual(by.AsSpan(0, n))) return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr security);
}

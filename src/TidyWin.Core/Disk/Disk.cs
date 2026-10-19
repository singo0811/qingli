using TidyWin.Core.Clean;
using TidyWin.Core.Paths;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Disk;

/// <summary>A folder with everything below it added up.</summary>
public sealed class DiskNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public DiskNode? Parent { get; init; }

    /// <summary>Files directly in this folder.</summary>
    public long LooseBytes { get; set; }

    /// <summary>Everything below, including loose files. Filled in when the scan ends.</summary>
    public long Bytes { get; set; }

    public long FileCount { get; set; }
    public List<DiskNode> Folders { get; } = [];

    /// <summary>Folders deeper than <see cref="DiskScanner.MaxDepth"/> are counted here but not kept one by one.</summary>
    public bool HasHiddenDepth { get; set; }
}

public sealed record DiskScan(DiskNode Root, IReadOnlyList<FsFile> TopFiles, long TotalBytes, long FileCount, bool Cancelled);

public sealed record DiskProgress(long Files, long Bytes, string Current);

/// <summary>
/// Adds up where the space went. Memory stays small however big the disk is: folders are kept to a fixed depth, and
/// of all the files only the largest few hundred are remembered.
/// </summary>
public static class DiskScanner
{
    public const int MaxDepth = 5;
    public const int TopFilesKept = 300;

    public static DiskScan Scan(string rootPath, IFileSystem fs, IProgress<DiskProgress>? progress = null, CancellationToken ct = default)
    {
        var root = WindowsPath.Normalize(rootPath) ?? rootPath.TrimEnd('\\');
        var rootNode = new DiskNode { Name = Last(root), Path = root };
        var nodes = new Dictionary<string, DiskNode>(StringComparer.OrdinalIgnoreCase) { [root] = rootNode };
        var top = new PriorityQueue<FsFile, long>();

        long files = 0, bytes = 0;
        string? lastDir = null;
        DiskNode? lastNode = null;
        var cancelled = false;

        foreach (var f in fs.EnumerateFilesRecursiveFast(root, ct))
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            var size = f.IsLink ? 0 : f.Length;
            var cut = f.Path.LastIndexOf('\\');
            var dir = cut <= 0 ? root : f.Path[..cut];

            if (!string.Equals(dir, lastDir, StringComparison.OrdinalIgnoreCase))
            {
                lastDir = dir;
                lastNode = NodeFor(dir, root, rootNode, nodes);
            }

            lastNode!.LooseBytes += size;
            lastNode.FileCount++;
            files++;
            bytes += size;

            if (size > 0)
            {
                if (top.Count < TopFilesKept) top.Enqueue(f, size);
                else if (top.TryPeek(out _, out var smallest) && size > smallest) { top.Dequeue(); top.Enqueue(f, size); }
            }

            if (progress is not null && (files & 0x1FFF) == 0) progress.Report(new DiskProgress(files, bytes, dir));
        }

        progress?.Report(new DiskProgress(files, bytes, root));
        Total(rootNode);
        Sort(rootNode);

        var biggest = new List<FsFile>();
        while (top.TryDequeue(out var file, out _)) biggest.Add(file);
        biggest.Reverse();

        return new DiskScan(rootNode, biggest, bytes, files, cancelled);
    }

    private static DiskNode NodeFor(string dir, string root, DiskNode rootNode, Dictionary<string, DiskNode> nodes)
    {
        if (nodes.TryGetValue(dir, out var known)) return known;
        if (dir.Length <= root.Length || !WindowsPath.IsStrictlyUnder(dir, root)) return rootNode;

        var cut = dir.LastIndexOf('\\');
        var parentPath = cut <= 0 ? root : dir[..cut];
        var parent = NodeFor(parentPath, root, rootNode, nodes);

        if (Depth(dir, root) > MaxDepth)
        {
            parent.HasHiddenDepth = true;
            return parent; // counted in the nearest folder we keep
        }

        var node = new DiskNode { Name = Last(dir), Path = dir, Parent = parent };
        parent.Folders.Add(node);
        nodes[dir] = node;
        return node;
    }

    // How many folders below the root, the same for "C:\" and for "C:\Data".
    private static int Depth(string dir, string root)
    {
        var rest = dir[root.Length..].Trim('\\');
        return rest.Length == 0 ? 0 : rest.Count(c => c == '\\') + 1;
    }

    private static long Total(DiskNode n)
    {
        long sum = n.LooseBytes;
        long count = n.FileCount;
        foreach (var c in n.Folders) { sum += Total(c); count += c.FileCount; }
        n.Bytes = sum;
        n.FileCount = count;
        return sum;
    }

    private static void Sort(DiskNode n)
    {
        n.Folders.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        foreach (var c in n.Folders) Sort(c);
    }

    private static string Last(string path)
    {
        var cut = path.TrimEnd('\\').LastIndexOf('\\');
        var name = cut < 0 ? path : path[(cut + 1)..];
        return string.IsNullOrEmpty(name) ? path : name;
    }
}

public readonly record struct Box(double X, double Y, double W, double H);

/// <summary>Squarified treemap: areas proportional to values, as close to squares as possible.</summary>
public static class TreemapLayout
{
    /// <returns>One box per value in the original order. A value of zero or less gets an empty box.</returns>
    public static Box[] Squarify(IReadOnlyList<double> values, double x, double y, double w, double h)
    {
        var result = new Box[values.Count];
        if (values.Count == 0 || w <= 0 || h <= 0) return result;

        var order = Enumerable.Range(0, values.Count).Where(i => values[i] > 0).OrderByDescending(i => values[i]).ToArray();
        if (order.Length == 0) return result;

        var scale = w * h / order.Sum(i => values[i]);
        double cx = x, cy = y, cw = w, ch = h;
        var start = 0;

        while (start < order.Length)
        {
            var side = Math.Min(cw, ch);
            var end = start;
            double sum = 0, min = double.MaxValue, max = 0, worst = double.MaxValue;

            while (end < order.Length)
            {
                var a = values[order[end]] * scale;
                var nSum = sum + a;
                var nMin = Math.Min(min, a);
                var nMax = Math.Max(max, a);
                var nWorst = Math.Max(side * side * nMax / (nSum * nSum), nSum * nSum / (side * side * nMin));
                if (end > start && nWorst > worst) break;
                sum = nSum; min = nMin; max = nMax; worst = nWorst;
                end++;
            }

            var thick = sum / side;
            if (cw >= ch)
            {
                var yy = cy;
                for (var i = start; i < end; i++)
                {
                    var hh = values[order[i]] * scale / thick;
                    result[order[i]] = new Box(cx, yy, thick, hh);
                    yy += hh;
                }
                cx += thick; cw -= thick;
            }
            else
            {
                var xx = cx;
                for (var i = start; i < end; i++)
                {
                    var ww = values[order[i]] * scale / thick;
                    result[order[i]] = new Box(xx, cy, ww, thick);
                    xx += ww;
                }
                cy += thick; ch -= thick;
            }
            start = end;
        }
        return result;
    }
}

/// <summary>
/// Things that quietly use tens of gigabytes and are not ordinary clutter. They are explained, not deleted:
/// each one needs a decision the person has to understand first.
/// </summary>
public sealed record KnownSpaceSpec(string Id, string Title, string[] Locations, string About, string HowTo, string? Command = null);

public static class KnownSpace
{
    // {SystemDrive} {Windows} {LocalAppData}; a "*" stands for one whole folder name.
    public static IReadOnlyList<KnownSpaceSpec> All { get; } =
    [
        new("hiberfil", Loc.T("休眠文件 hiberfil.sys"), [@"{SystemDrive}\hiberfil.sys"],
            Loc.T("用来在休眠和「快速启动」时保存内存内容,通常是内存大小的一半到全部。"),
            Loc.T("不用休眠的台式机可以关掉,会同时关闭休眠和快速启动。以管理员身份打开终端运行下面的命令,随时可以用 powercfg /h on 再开。"),
            "powercfg /h off"),
        new("pagefile", Loc.T("虚拟内存 pagefile.sys"), [@"{SystemDrive}\pagefile.sys"],
            Loc.T("内存不够时 Windows 把暂时不用的数据放在这里,也用于崩溃转储。"),
            Loc.T("不要手动删除。想挪到别的盘:系统属性 → 高级 → 性能 → 设置 → 高级 → 虚拟内存。")),
        new("swapfile", Loc.T("应用交换文件 swapfile.sys"), [@"{SystemDrive}\swapfile.sys"],
            Loc.T("给 Microsoft Store 应用用的小交换文件。"), Loc.T("系统自己管理,不需要处理。")),
        new("windowsold", Loc.T("旧系统 Windows.old"), [@"{SystemDrive}\Windows.old"],
            Loc.T("大版本升级后留下的上一个系统,方便回退。系统会在约 10 天后自己清掉。"),
            Loc.T("设置 → 系统 → 存储 → 临时文件,勾选「以前的 Windows 安装」后删除。删除后就不能回退到旧版本了。")),
        new("wuDownload", Loc.T("Windows 更新下载缓存"), [@"{Windows}\SoftwareDistribution\Download"],
            Loc.T("已经下载的更新安装包。装完后就没用了,但系统有时不会及时清理。"),
            Loc.T("设置 → 系统 → 存储 → 临时文件,勾选「Windows 更新清理」。不要手动删除文件夹。")),
        new("wsl", Loc.T("WSL 虚拟磁盘 ext4.vhdx"), [@"{LocalAppData}\Packages\*\LocalState\ext4.vhdx", @"{LocalAppData}\wsl\*\ext4.vhdx"],
            Loc.T("Linux 子系统的整块磁盘,只会变大不会自动变小。在里面删文件不会让它缩小。"),
            Loc.T("先在 Linux 里清好文件,然后在 PowerShell 里先关闭 WSL,再压缩磁盘:wsl --shutdown,接着用 diskpart 的 compact vdisk,或 Optimize-VHD。"),
            "wsl --shutdown"),
        new("docker", Loc.T("Docker 虚拟磁盘"), [@"{LocalAppData}\Docker\wsl\disk\docker_data.vhdx", @"{LocalAppData}\Docker\wsl\data\ext4.vhdx"],
            Loc.T("Docker 的镜像、容器和卷都在这里,同样只会变大。"),
            Loc.T("先清理没用的镜像和缓存,再压缩磁盘,这一步不要手动删文件。"),
            "docker system prune"),
        new("installer", Loc.T("Windows Installer 缓存"), [@"{Windows}\Installer"],
            Loc.T("每个用 .msi 安装的软件在这里留着修复和卸载要用的包。"),
            Loc.T("千万不要手动删除,会让软件无法卸载或修复。")),
        new("winsxs", Loc.T("组件仓库 WinSxS"), [@"{Windows}\WinSxS"],
            Loc.T("Windows 的系统组件仓库。看到的大小有很大一部分是和别处共用的,实际占用比资源管理器显示的小。"),
            Loc.T("不能手动删除。可用 DISM 清理旧组件。"),
            "DISM /Online /Cleanup-Image /StartComponentCleanup"),
    ];
}

/// <summary>
/// What may be sent to the Recycle Bin from the disk analysis. System and program folders, drive roots and a person's
/// main personal folders are never offered, however large they are.
/// </summary>
public static class RecyclePolicy
{
    private static readonly string[] PersonalFolders =
        ["Documents", "Desktop", "Pictures", "Videos", "Music", "Downloads", "AppData", "OneDrive", "Favorites", "Contacts", "Links", "Saved Games", "Searches", ".ssh"];

    /// <returns>Null when allowed, otherwise the reason in plain words.</returns>
    public static string? Refuse(string? path, IEnumerable<string> systemRoots, string userProfile)
    {
        var p = WindowsPath.Normalize(path);
        if (p is null) return Loc.T("路径无效");
        if (WindowsPath.IsRoot(p)) return Loc.T("不能删除整个磁盘");

        foreach (var root in systemRoots)
            if (WindowsPath.Normalize(root) is { } r && WindowsPath.IsSameOrUnder(p, r))
                return Loc.T("这是系统或程序文件夹,删除可能让 Windows 或软件无法使用");

        // A folder that merely contains a protected one is as dangerous as the protected one itself
        // (C:\Users contains the person's whole profile; a drive's top folder may contain Windows).
        foreach (var root in systemRoots)
            if (WindowsPath.Normalize(root) is { } r && WindowsPath.IsSameOrUnder(r, p))
                return Loc.T("里面包含系统或程序文件夹,不能整个移走。请打开它,选里面具体的东西");
        if (WindowsPath.Normalize(userProfile) is { } profile && WindowsPath.IsSameOrUnder(profile, p))
            return p.Equals(profile, StringComparison.OrdinalIgnoreCase)
                ? Loc.T("这是你的用户文件夹,里面有你全部的个人数据")
                : Loc.T("里面包含你的用户文件夹,不能整个移走。请打开它,选里面具体的东西");

        var segments = p.Split('\\');
        if (segments.Any(s => s.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || s.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)))
            return Loc.T("这是系统保留的文件夹");

        if (WindowsPath.Normalize(userProfile) is { } home)
        {
            if (p.Equals(home, StringComparison.OrdinalIgnoreCase)) return Loc.T("这是你的用户文件夹,里面有你全部的个人数据");
            foreach (var f in PersonalFolders)
                if (p.Equals(home + "\\" + f, StringComparison.OrdinalIgnoreCase))
                    return Loc.T("这是你的个人文件夹本身,请打开它,选里面具体的文件");
        }
        return null;
    }
}

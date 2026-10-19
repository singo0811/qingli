using TidyWin.Core.Mascot;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Insights;

public enum InsightLevel { Good = 0, Tip = 1, Warn = 2, Urgent = 3 }

/// <summary>Where the button of a suggestion leads.</summary>
public enum Destination { None, SystemClean, AiClean, Models, Finder, Disk, Software, Updates, Startup, Optimize, Memory, Security, Status }

public sealed record Insight(string Id, InsightLevel Level, string Title, string Detail, Destination Go, string ActionLabel, int Priority)
{
    /// <summary>How the whale looks when she says it.</summary>
    public Mood Mood => Level switch
    {
        InsightLevel.Urgent => Mood.Worried,
        InsightLevel.Warn => Mood.Worried,
        InsightLevel.Tip => Mood.Happy,
        _ => Mood.Satisfied,
    };
}

/// <summary>Everything the engine may know. A null means "not measured yet": it never produces advice by itself.</summary>
public sealed record Facts
{
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;

    public double DiskUsedPercent { get; init; }
    public long DiskFreeBytes { get; init; }
    public long DiskTotalBytes { get; init; }
    public double MemoryPercent { get; init; }
    public double UptimeDays { get; init; }
    public bool RebootPending { get; init; }

    /// <summary>Bytes the system clean-up could free, split by how safe they are.</summary>
    public long? SystemSafeBytes { get; init; }
    public long? SystemOtherBytes { get; init; }
    public long? AiCleanBytes { get; init; }
    public long? RecycleBinBytes { get; init; }

    public int StartupEnabled { get; init; }
    public int StartupDroppable { get; init; }
    public int StartupDead { get; init; }
    public int StartupSuspicious { get; init; }

    public IReadOnlyList<AiTool> AiToolsInUse { get; init; } = [];

    /// <summary>Known after a disk analysis has been run in this session.</summary>
    public string? BiggestFolderName { get; init; }
    public long? BiggestFolderBytes { get; init; }

    public int? UpgradableApps { get; init; }
    public bool OsUnsupported { get; init; }
    public int? UpdateStaleDays { get; init; }
    public bool DiskUnhealthy { get; init; }
    public int ProblemDevices { get; init; }
    public bool NotActivated { get; init; }
    public int SecurityRisks { get; init; }
    public int SecurityWarns { get; init; }

    // What the app remembers from earlier visits.
    public DateTime? LastCleanUtc { get; init; }
    public long LastCleanBytes { get; init; }
    public DateTime? LastVisitUtc { get; init; }
    public long? DiskFreeAtLastVisit { get; init; }
}

/// <summary>
/// Turns what is known about this computer into a short, ranked list of things worth doing. Every suggestion says what
/// was seen, why it matters, and where to go. Signals are combined (a nearly full disk and a big clean-up potential
/// become one sentence), and a suggestion is only made when it is backed by a real measurement.
/// </summary>
public static class InsightEngine
{
    public static IReadOnlyList<Insight> Evaluate(Facts f, ISet<string>? snoozed = null)
    {
        var list = new List<Insight>();
        void Add(Insight i) { if (snoozed is null || !snoozed.Contains(i.Id)) list.Add(i); }

        var free = Size(f.DiskFreeBytes);
        var freePercent = 100 - f.DiskUsedPercent;
        var safe = f.SystemSafeBytes ?? 0;

        // ── disk ──
        if (f.DiskUnhealthy)
            Add(new("disk.unhealthy", InsightLevel.Urgent, Loc.T("有一块磁盘报告状态不健康"),
                Loc.T("Windows 认为其中一块磁盘有问题,可能很快损坏。先备份重要文件,再考虑更换。"), Destination.Status, Loc.T("查看详情"), 100));

        var critical = f.DiskTotalBytes > 0 && (f.DiskUsedPercent >= 95 || f.DiskFreeBytes < 5L << 30);
        var low = f.DiskTotalBytes > 0 && (f.DiskUsedPercent >= 88 || f.DiskFreeBytes < 15L << 30);
        if (critical || low)
        {
            var title = Loc.F("系统盘只剩 {0}({1:0}%)",free,freePercent);
            string detail;
            Destination go;
            string label;
            if (safe >= 1L << 30)
            {
                detail = Loc.F("系统清理里有 {0} 是稳妥可清的,先清它。",Size(safe));
                go = Destination.SystemClean; label = Loc.T("去清理");
            }
            else if (f.AiToolsInUse.Count > 0)
            {
                // The usual culprit on a machine that runs AI tools: models, virtual environments and copies of both.
                // The finder explains each find, so the person is never asked to guess what is safe.
                detail = Loc.F("你在用 {0},模型和 Python 环境通常是占地大户。",Personas.ToolName(f.AiToolsInUse[0])) +
                         Loc.T("「找 AI 大文件」会逐项说明哪些能放心处理、哪些不要动。");
                go = Destination.Finder; label = Loc.T("找 AI 大文件");
            }
            else if (f.BiggestFolderName is not null && f.BiggestFolderBytes is { } big)
            {
                detail = Loc.F("能直接清的垃圾不多。你这块盘最大的是「{0}」,占 {1}。",f.BiggestFolderName,Size(big));
                go = Destination.Disk; label = Loc.T("去磁盘分析");
            }
            else
            {
                detail = f.SystemSafeBytes is null
                    ? Loc.T("先到系统清理看看有多少能清,再用磁盘分析找出最占地方的文件夹。")
                    : Loc.F("能直接清的垃圾只有 {0},不够。用磁盘分析找出最占地方的文件夹。",Size(safe));
                go = f.SystemSafeBytes is null ? Destination.SystemClean : Destination.Disk;
                label = f.SystemSafeBytes is null ? Loc.T("去清理") : Loc.T("去磁盘分析");
            }
            Add(new("disk.low", critical ? InsightLevel.Urgent : InsightLevel.Warn, title, detail, go, label, critical ? 90 : 70));
        }

        if (f.DiskFreeAtLastVisit is { } before && f.LastVisitUtc is { } visited && f.DiskTotalBytes > 0)
        {
            var days = (f.NowUtc - visited).TotalDays;
            var lost = before - f.DiskFreeBytes;
            if (days >= 1 && lost >= Math.Max(3L << 30, f.DiskTotalBytes / 100))
            {
                var usesAi = f.AiToolsInUse.Count > 0;
                Add(new("disk.shrinking", InsightLevel.Tip, Loc.F("{0}以来,系统盘少了 {1}",Span(days),Size(lost)),
                    usesAi ? Loc.F("空间减少得比平时快。你在用 {0},下载模型和建环境都会这样。",Personas.ToolName(f.AiToolsInUse[0]))
                           : Loc.T("空间减少得比平时快。看看是哪个文件夹变大了。"),
                    usesAi ? Destination.Finder : Destination.Disk, usesAi ? Loc.T("找 AI 大文件") : Loc.T("去磁盘分析"), 60));
            }
        }

        // ── memory ──
        if (f.MemoryPercent >= 85)
        {
            var tool = f.AiToolsInUse.Count > 0 ? Personas.ToolName(f.AiToolsInUse[0]) : null;
            Add(new("memory.high", InsightLevel.Warn, Loc.F("内存用了 {0:0}%",f.MemoryPercent),
                tool is null
                    ? Loc.T("内存很紧张,电脑可能变卡。看看哪些程序在白占内存。")
                    : Loc.F("你在用 {0},大模型和它的缓存很吃内存。看看能不能释放不用的模型和闲置程序。",tool),
                Destination.Memory, Loc.T("去看内存"), tool is null ? 55 : 65));
        }

        // ── safety and health ──
        if (f.SecurityRisks > 0)
            Add(new("security.risk", InsightLevel.Urgent, Loc.F("安全体检发现 {0} 项需要处理",f.SecurityRisks),
                Loc.T("有防护被关闭或服务暴露在网络上。"), Destination.Security, Loc.T("去处理"), 85));
        else if (f.SecurityWarns > 0)
            Add(new("security.warn", InsightLevel.Tip, Loc.F("安全体检有 {0} 项建议留意",f.SecurityWarns), Loc.T("不紧急,有空看看。"), Destination.Security, Loc.T("去看看"), 40));

        if (f.OsUnsupported)
            Add(new("os.unsupported", InsightLevel.Warn, Loc.T("你的 Windows 版本已不再获得常规安全更新"),
                Loc.T("没有安全补丁,新漏洞不会被修复。建议升级到 Windows 11,或确认已加入延长支持。"), Destination.Status, Loc.T("了解详情"), 62));

        if (f.UpdateStaleDays is { } stale && stale >= 30)
            Add(new("update.stale", stale >= 60 ? InsightLevel.Warn : InsightLevel.Tip, Loc.F("Windows 已 {0} 天没有成功更新",stale),
                Loc.T("长期不更新会错过安全补丁。打开「设置 → Windows 更新」检查一下。"), Destination.Status, Loc.T("了解详情"), stale >= 60 ? 50 : 34));

        if (f.RebootPending)
            Add(new("reboot.pending", InsightLevel.Warn, Loc.T("有更新在等你重启"),
                Loc.T("补丁装了一半,重启后才会生效。"), Destination.Status, Loc.T("了解详情"), 58));
        else if (f.UptimeDays >= 14)
            Add(new("uptime.long", InsightLevel.Tip, Loc.F("已经 {0:0} 天没重启了",f.UptimeDays),
                Loc.T("重启能清掉长期运行积累的内存碎片和卡顿。"), Destination.Status, Loc.T("了解详情"), 35));

        if (f.ProblemDevices > 0)
            Add(new("devices.problem", InsightLevel.Warn, Loc.F("设备管理器里有 {0} 个设备报错",f.ProblemDevices),
                Loc.T("可能是驱动缺失或损坏,对应的硬件用起来会异常。"), Destination.Status, Loc.T("查看是哪个"), 52));

        if (f.NotActivated)
            Add(new("license.missing", InsightLevel.Tip, Loc.T("Windows 看起来还没有激活"), Loc.T("未激活会限制个性化设置并显示水印。"), Destination.Status, Loc.T("了解详情"), 30));

        // ── clean-up ──
        if (!(critical || low) && safe >= 500L << 20)
        {
            var since = f.LastCleanUtc is { } lc ? Loc.F("上次清理是 {0}",Ago((f.NowUtc - lc).TotalDays)) +
                                                  (f.LastCleanBytes > 0 ? Loc.F(",当时清了 {0}。",Size(f.LastCleanBytes)) : Loc.T("。")) : Loc.T("你还没清理过。");
            Add(new("clean.available", InsightLevel.Tip, Loc.F("有 {0} 垃圾可以放心清掉",Size(safe)),
                since + Loc.T("这部分都是自动重建的,不会影响使用。"), Destination.SystemClean, Loc.T("去清理"), 45));
        }

        if (f.AiCleanBytes is { } ai && ai >= 500L << 20 && f.AiToolsInUse.Count > 0)
            Add(new("ai.clean", InsightLevel.Tip, Loc.F("AI 工具留下了 {0} 缓存",Size(ai)),
                Loc.F("你在用 {0},它们的编译缓存和模型缓存会越攒越多。",Personas.ToolName(f.AiToolsInUse[0])), Destination.AiClean, Loc.T("去清理"), 44));

        if (f.RecycleBinBytes is { } bin && bin >= 1L << 30)
            Add(new("recycle.big", InsightLevel.Tip, Loc.F("回收站里还压着 {0}",Size(bin)),
                Loc.T("放进回收站的文件还占着磁盘,清空后才会真正释放。"), Destination.Optimize, Loc.T("打开回收站"), 42));

        // ── start-up and software ──
        if (f.StartupSuspicious > 0)
            Add(new("startup.suspicious", InsightLevel.Warn, Loc.F("有 {0} 个开机启动项看起来可疑",f.StartupSuspicious),
                Loc.T("它们放在临时文件夹里,或者用了编码过的命令,正规软件很少这样。"), Destination.Startup, Loc.T("去检查"), 66));

        if (f.StartupDead > 0)
            Add(new("startup.dead", InsightLevel.Tip, Loc.F("{0} 个开机启动项指向的程序已经不存在",f.StartupDead),
                Loc.T("多半是软件卸载后留下的,每次开机都白找一遍。"), Destination.Startup, Loc.T("去移除"), 38));

        if (f.StartupDroppable >= 4 || f.StartupEnabled >= 25)
            Add(new("startup.many", InsightLevel.Tip, Loc.F("开机自动启动了 {0} 个程序",f.StartupEnabled),
                f.StartupDroppable > 0 ? Loc.F("其中 {0} 个(聊天、更新、游戏平台之类)关掉也不影响使用,开机会快一些。",f.StartupDroppable)
                                       : Loc.T("太多自启程序会拖慢开机。"), Destination.Startup, Loc.T("去整理"), 41));

        if (f.UpgradableApps is { } up && up > 0)
            Add(new("software.updates", InsightLevel.Tip, Loc.F("{0} 个软件有新版本",up), Loc.T("更新能修复漏洞和问题。"), Destination.Updates, Loc.T("去更新"), up >= 5 ? 44 : 36));

        if (!list.Any(i => i.Level >= InsightLevel.Tip))
        {
            var parts = new List<string> { Loc.F("系统盘剩 {0}",free) };
            if (f.UptimeDays >= 1) parts.Add(Loc.F("已开机 {0:0} 天",f.UptimeDays));
            if (f.LastCleanUtc is { } lc) parts.Add(Loc.F("上次清理 {0}",Ago((f.NowUtc - lc).TotalDays)));
            list.Add(new Insight("all.good", InsightLevel.Good, Loc.T("一切正常,没有需要你操心的事"), string.Join(" · ", parts), Destination.None, "", 0));
        }

        return list.OrderByDescending(i => i.Level).ThenByDescending(i => i.Priority).ToList();
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    public static string Span(double days) => days switch
    {
        < 1 => Loc.T("今天"),
        < 14 => Loc.F("{0:0} 天",Math.Max(1, Math.Round(days))),
        < 60 => Loc.F("{0:0} 周",days / 7),
        < 700 => Loc.F("{0:0} 个月",days / 30),
        _ => Loc.F("{0:0} 年",days / 365),
    };

    public static string Ago(double days) => days switch
    {
        < 1 => Loc.T("今天"),
        < 2 => Loc.T("昨天"),
        _ => Span(days) + Loc.T("前"),
    };
    /// <summary>One sentence for the whale: the title, and what to do about it when there is something to do.</summary>
    public static MascotState Say(Insight i) =>
        new(i.Mood, i.Go == Destination.None ? i.Title + Loc.T("。") : Loc.F("{0}。{1}",i.Title,i.Detail));
}

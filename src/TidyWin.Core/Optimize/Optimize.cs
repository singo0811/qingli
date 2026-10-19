using TidyWin.Core.Localization;
namespace TidyWin.Core.Optimize;

/// <summary>A small, safe fix for one specific annoyance. None of them delete personal files or change settings.</summary>
public sealed record OptimizeTask(string Id, string Title, string When, string Effect, string? Warning = null);

public sealed record OptimizeResult(bool Ok, string Message);

public interface IOptimizer
{
    /// <summary>What is sitting in the Recycle Bin, or null when it cannot be read.</summary>
    long? RecycleBinBytes();

    Task<OptimizeResult> RunAsync(string taskId, CancellationToken ct = default);
}

public static class OptimizeCatalog
{
    public const string Recycle = "recycle", Dns = "dns", Explorer = "explorer", IconCache = "iconcache";

    public static IReadOnlyList<OptimizeTask> All { get; } =
    [
        new(Recycle, Loc.T("清空回收站"),
            Loc.T("删掉的文件还在回收站里占着空间,清空后才真正释放。"),
            Loc.T("立刻释放回收站占用的全部空间。"),
            Loc.T("清空后里面的文件无法再找回。")),
        new(Dns, Loc.T("刷新网络 DNS 缓存"),
            Loc.T("个别网站打不开、换了网络后访问异常,而别的网站正常时。"),
            Loc.T("清掉电脑记住的「网址对应哪台服务器」,下次访问重新查询。不影响任何已保存的数据。")),
        new(Explorer, Loc.T("重启资源管理器"),
            Loc.T("桌面、任务栏、文件夹窗口卡住或没有反应时。"),
            Loc.T("重新启动桌面和任务栏,通常几秒就恢复流畅。"),
            Loc.T("桌面和任务栏会闪一下,已打开的文件夹窗口会关闭。正在编辑的文件不受影响。")),
        new(IconCache, Loc.T("重建图标和缩略图缓存"),
            Loc.T("图标变成白纸、图片缩略图不显示或显示错乱时。"),
            Loc.T("删掉旧的图标和缩略图缓存,让 Windows 重新生成。"),
            Loc.T("会重启资源管理器。第一次打开图片很多的文件夹时,缩略图要重新生成,会慢一点。")),
    ];
}

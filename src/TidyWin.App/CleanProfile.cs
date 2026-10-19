using TidyWin.Core.AiJunk;
using TidyWin.Core.Mascot;
using TidyWin.Core.Localization;

namespace TidyWin.App;

/// <summary>
/// The clean-up page is the same screen for every list of places. A profile says which places it looks at
/// and what it calls itself.
/// </summary>
public sealed record CleanProfile(
    IReadOnlyList<AiTarget> Catalog,
    string ScanTitle,
    string EmptyText,
    MascotState Scanning)
{
    public static CleanProfile Ai { get; } = new(
        AiCatalog.All,
        Loc.T("正在盘点 AI 工具留下的东西"),
        Loc.T("很干净,没有发现可以清理的 AI 缓存。"),
        MascotLines.AiScanning());

    public static CleanProfile System { get; } = new(
        SystemCatalog.All,
        Loc.T("正在盘点临时文件和各种缓存"),
        Loc.T("很干净,没有发现可以清理的垃圾。"),
        MascotLines.SystemScanning());
}

using TidyWin.Core.Localization;
namespace TidyWin.Core.AiJunk;

/// <summary>
/// Ordinary computer clutter: temp files, browser and app caches, error reports, and the caches developer tools leave
/// behind. It uses the same target type, scanner, path guard and executor as the AI list, so every safety rule that
/// protects the AI clean-up protects this one too.
/// </summary>
/// <remarks>
/// Only per-user places that need no administrator rights are listed. Machine-wide places (the Windows Update download
/// folder, Delivery Optimization, Windows.old) need elevation and a stopped service, so they are not here yet.
/// Nothing in this list overlaps <see cref="AiCatalog"/>, so no folder shows up on two pages.
/// </remarks>
public static class SystemCatalog
{
    private static readonly AiOverride[] None = [];

    // Browser profile folders that hold only caches. Cookies, passwords, history and extensions are never in these.
    private static AiTarget Browser(string id, string tool, string root, string profile, string folder, string title) =>
        new(id, tool, title, AiCategory.BrowserCache, AiSafety.Regenerable,
            KnownFolder.LocalAppData, $@"{root}\User Data\{profile}\{folder}", None,
            Loc.T("网页和图片会重新下载,打开网页第一次会稍慢。登录状态、密码、书签不受影响"), MinAgeDays: 3);

    // Electron apps keep their disposable files in these three folders.
    private static AiTarget[] Electron(string idPrefix, string tool, string roaming) =>
    [
        new($"{idPrefix}.cache", tool, Loc.F("{0} 缓存",tool), AiCategory.AppCache, AiSafety.Regenerable,
            KnownFolder.AppData, $@"{roaming}\Cache", None, Loc.T("下次启动自动重建"), MinAgeDays: 7),
        new($"{idPrefix}.code", tool, Loc.F("{0} 脚本缓存",tool), AiCategory.AppCache, AiSafety.Regenerable,
            KnownFolder.AppData, $@"{roaming}\Code Cache", None, Loc.T("下次启动自动重建"), MinAgeDays: 7),
        new($"{idPrefix}.gpu", tool, Loc.F("{0} GPU 缓存",tool), AiCategory.AppCache, AiSafety.Regenerable,
            KnownFolder.AppData, $@"{roaming}\GPUCache", None, Loc.T("下次启动自动重建"), MinAgeDays: 7),
    ];

    public static IReadOnlyList<AiTarget> All { get; } =
    [
        // ---- Windows ----
        new("win.temp", "Windows", Loc.T("用户临时文件夹"), AiCategory.SystemTemp, AiSafety.Regenerable,
            KnownFolder.Temp, "", None,
            Loc.T("程序用完就丢下的临时文件。只清理 3 天前的,正在被程序使用的文件会自动跳过"), MinAgeDays: 3),
        new("win.inetcache", "Windows", Loc.T("网页临时文件"), AiCategory.SystemTemp, AiSafety.Regenerable,
            KnownFolder.LocalAppData, @"Microsoft\Windows\INetCache", None,
            Loc.T("系统组件缓存的网页内容,需要时会重新下载"), MinAgeDays: 7),
        new("win.wer", "Windows", Loc.T("Windows 错误报告"), AiCategory.Diagnostics, AiSafety.Regenerable,
            KnownFolder.LocalAppData, @"Microsoft\Windows\WER", None,
            Loc.T("程序出错时留下的诊断记录,不影响使用,删除后无法再用它分析旧的崩溃"), MinAgeDays: 7),
        new("win.crashdumps", "Windows", Loc.T("程序崩溃转储"), AiCategory.Diagnostics, AiSafety.Regenerable,
            KnownFolder.LocalAppData, "CrashDumps", None,
            Loc.T("程序崩溃时保存的内存快照,常常很大。只用于排查问题,不影响使用"), MinAgeDays: 7),
        new("win.d3d", "DirectX", Loc.T("DirectX 着色器缓存"), AiCategory.AppCache, AiSafety.Regenerable,
            KnownFolder.LocalAppData, "D3DSCache", None,
            Loc.T("游戏和应用首次启动会重新编译着色器,可能卡一下"), MinAgeDays: 7),
        new("amd.dx", "AMD", Loc.T("AMD 着色器缓存"), AiCategory.AppCache, AiSafety.Regenerable,
            KnownFolder.LocalAppData, @"AMD\DxCache", None,
            Loc.T("游戏首次启动会重新编译着色器,可能卡一下"), MinAgeDays: 7),

        // ---- Browsers (default profile and the first extra one) ----
        Browser("chrome.cache", "Chrome", @"Google\Chrome", "Default", "Cache", Loc.T("Chrome 网页缓存")),
        Browser("chrome.code", "Chrome", @"Google\Chrome", "Default", "Code Cache", Loc.T("Chrome 脚本缓存")),
        Browser("chrome.gpu", "Chrome", @"Google\Chrome", "Default", "GPUCache", Loc.T("Chrome GPU 缓存")),
        Browser("chrome.sw", "Chrome", @"Google\Chrome", "Default", @"Service Worker\CacheStorage", Loc.T("Chrome 离线网页缓存")),
        Browser("chrome.p1.cache", "Chrome", @"Google\Chrome", "Profile 1", "Cache", Loc.T("Chrome 网页缓存(用户 2)")),
        Browser("edge.cache", "Edge", @"Microsoft\Edge", "Default", "Cache", Loc.T("Edge 网页缓存")),
        Browser("edge.code", "Edge", @"Microsoft\Edge", "Default", "Code Cache", Loc.T("Edge 脚本缓存")),
        Browser("edge.gpu", "Edge", @"Microsoft\Edge", "Default", "GPUCache", Loc.T("Edge GPU 缓存")),
        Browser("edge.sw", "Edge", @"Microsoft\Edge", "Default", @"Service Worker\CacheStorage", Loc.T("Edge 离线网页缓存")),
        Browser("edge.p1.cache", "Edge", @"Microsoft\Edge", "Profile 1", "Cache", Loc.T("Edge 网页缓存(用户 2)")),

        // ---- Apps built on Electron ----
        .. Electron("vscode", "VS Code", "Code"),
        new("vscode.logs", "VS Code", Loc.T("VS Code 日志"), AiCategory.AppCache, AiSafety.Regenerable,
            KnownFolder.AppData, @"Code\logs", None, Loc.T("旧日志不再可查"), MinAgeDays: 14),
        new("vscode.vsix", "VS Code", Loc.T("VS Code 扩展安装包缓存"), AiCategory.AppCache, AiSafety.ReDownloadable,
            KnownFolder.AppData, @"Code\CachedExtensionVSIXs", None, Loc.T("更新或重装扩展时需要重新下载")),
        .. Electron("discord", "Discord", "discord"),
        .. Electron("slack", "Slack", "Slack"),

        // ---- Developer tools: the biggest wins on a developer's machine ----
        new("npm.cache", "npm", Loc.T("npm 缓存"), AiCategory.DevCache, AiSafety.ReDownloadable,
            KnownFolder.LocalAppData, "npm-cache", [new("npm_config_cache")],
            Loc.T("下次安装依赖会重新下载,已装好的项目不受影响")),
        new("yarn.cache", "Yarn", Loc.T("Yarn 缓存"), AiCategory.DevCache, AiSafety.ReDownloadable,
            KnownFolder.LocalAppData, @"Yarn\Cache", None,
            Loc.T("下次安装依赖会重新下载,已装好的项目不受影响")),
        new("pnpm.store", "pnpm", Loc.T("pnpm 全局存储"), AiCategory.DevCache, AiSafety.ReDownloadable,
            KnownFolder.LocalAppData, @"pnpm\store", None,
            Loc.T("下次安装依赖会重新下载。已装好的项目通过硬链接保留自己的文件,不受影响")),
        new("nuget.http", "NuGet", Loc.T("NuGet 网络缓存"), AiCategory.DevCache, AiSafety.Regenerable,
            KnownFolder.LocalAppData, @"NuGet\v3-cache", None,
            Loc.T("下次还原项目会重新查询,首次略慢"), MinAgeDays: 7),
        new("nuget.packages", "NuGet", Loc.T("NuGet 全局包"), AiCategory.DevCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".nuget\packages", [new("NUGET_PACKAGES")],
            Loc.T("下次还原项目会重新下载,没有网络时项目无法编译")),
        new("gradle.caches", "Gradle", Loc.T("Gradle 缓存"), AiCategory.DevCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".gradle\caches", [new("GRADLE_USER_HOME", "caches")],
            Loc.T("下次构建会重新下载依赖,首次构建会很慢")),
        new("maven.repo", "Maven", Loc.T("Maven 本地仓库"), AiCategory.DevCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".m2\repository", None,
            Loc.T("下次构建会重新下载依赖。注意:你用 mvn install 装进来的本地包也在这里,删了需要重新构建")),
        new("cargo.cache", "Cargo", Loc.T("Cargo 注册表缓存"), AiCategory.DevCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @".cargo\registry\cache", [new("CARGO_HOME", @"registry\cache")],
            Loc.T("下次构建会重新下载 crate 包")),
        new("go.build", "Go", Loc.T("Go 编译缓存"), AiCategory.DevCache, AiSafety.Regenerable,
            KnownFolder.LocalAppData, "go-build", [new("GOCACHE")],
            Loc.T("下次编译会重新生成,首次略慢"), MinAgeDays: 7),
        new("go.mod", "Go", Loc.T("Go 模块下载缓存"), AiCategory.DevCache, AiSafety.ReDownloadable,
            KnownFolder.UserProfile, @"go\pkg\mod\cache", None,
            Loc.T("下次构建会重新下载模块")),
    ];
}

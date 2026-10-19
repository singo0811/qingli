using System.Text.Json;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Status;

public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

public static class CpuMeter
{
    /// <summary>Busy share between two readings. Windows' kernel time already includes idle time.</summary>
    public static double Percent(CpuTimes before, CpuTimes after)
    {
        var idle = after.Idle - before.Idle;
        var total = (after.Kernel - before.Kernel) + (after.User - before.User);
        if (total == 0 || after.Kernel < before.Kernel) return 0;
        return Math.Clamp((double)(total - idle) * 100.0 / total, 0, 100);
    }
}

/// <summary>Per-second throughput from two counter readings.</summary>
public static class Throughput
{
    public static double PerSecond(long before, long after, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 || after < before ? 0 : (after - before) / elapsed.TotalSeconds;

    public static string Format(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => $"{bytesPerSecond:0} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024:0.0} KB/s",
        _ => $"{bytesPerSecond / 1024 / 1024:0.0} MB/s",
    };
}

/// <summary>"What is this process?" in plain words, for programs a normal person sees in a list and wonders about.</summary>
public static class ProcessExplainer
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system"] = Loc.T("Windows 内核本身。看到它占资源不用担心,也无法关闭。"),
        ["system idle process"] = Loc.T("不是程序,表示 CPU 空闲的比例。数字越大越清闲。"),
        ["registry"] = Loc.T("Windows 注册表在内存里的部分。"),
        ["memory compression"] = Loc.T("Windows 把不常用的内存压缩起来省空间,说明内存有点紧张但在正常处理。"),
        ["smss"] = Loc.T("Windows 会话管理器,开机时启动,系统必需。"),
        ["csrss"] = Loc.T("Windows 客户端/服务器运行时,系统必需,结束它会蓝屏。"),
        ["wininit"] = Loc.T("Windows 开机初始化,系统必需。"),
        ["winlogon"] = Loc.T("负责登录和锁屏,系统必需。"),
        ["services"] = Loc.T("管理所有 Windows 服务的核心进程,系统必需。"),
        ["lsass"] = Loc.T("本地安全认证,负责登录验证,系统必需。"),
        ["svchost"] = Loc.T("Windows 服务的「外壳」。很多系统服务共用它,有很多个是正常的。"),
        ["dwm"] = Loc.T("桌面窗口管理器,负责窗口的显示和特效。用显卡,关不掉。"),
        ["explorer"] = Loc.T("资源管理器:桌面、任务栏和文件窗口。卡死时可在「优化」页重启它。"),
        ["taskmgr"] = Loc.T("任务管理器。"),
        ["sihost"] = Loc.T("Shell 基础架构,负责开始菜单和通知等,系统必需。"),
        ["fontdrvhost"] = Loc.T("字体驱动的宿主进程,系统自带。"),
        ["ctfmon"] = Loc.T("输入法和文字服务,负责切换输入法,系统自带。"),
        ["conhost"] = Loc.T("命令行窗口的宿主进程。"),
        ["runtimebroker"] = Loc.T("管理 Microsoft Store 应用的权限,系统自带。"),
        ["searchhost"] = Loc.T("Windows 搜索界面。"),
        ["searchindexer"] = Loc.T("Windows 搜索的索引程序,为了让搜索更快。硬盘忙的时候它可能在建索引。"),
        ["startmenuexperiencehost"] = Loc.T("开始菜单。"),
        ["shellexperiencehost"] = Loc.T("任务栏和部分系统界面。"),
        ["textinputhost"] = Loc.T("触摸键盘和表情面板。"),
        ["securityhealthservice"] = Loc.T("Windows 安全中心的后台服务。"),
        ["securityhealthsystray"] = Loc.T("任务栏右下角的 Windows 安全图标。"),
        ["msmpeng"] = Loc.T("Microsoft Defender 杀毒引擎。扫描文件时占用高是正常的,通常几分钟后恢复。"),
        ["nissrv"] = Loc.T("Microsoft Defender 的网络防护。"),
        ["mpdefendercoreservice"] = Loc.T("Microsoft Defender 的核心服务。"),
        ["wuauserv"] = Loc.T("Windows 更新服务。"),
        ["tiworker"] = Loc.T("Windows 更新在后台安装更新,完成后自己退出。"),
        ["trustedinstaller"] = Loc.T("Windows 更新和组件安装服务。"),
        ["compattelrunner"] = Loc.T("Windows 兼容性遥测,收集兼容性数据。可能在后台占一会儿 CPU,会自己结束。"),
        ["audiodg"] = Loc.T("Windows 音频引擎,播放声音时占用是正常的。"),
        ["spoolsv"] = Loc.T("打印后台处理程序。没有打印机也会运行。"),
        ["wmiprvse"] = Loc.T("Windows 管理规范(WMI)的提供程序,被监控或管理软件调用时出现。"),
        ["dllhost"] = Loc.T("COM 代理,很多系统组件借它运行。"),
        ["backgroundtaskhost"] = Loc.T("运行 Microsoft Store 应用的后台任务。"),
        ["applicationframehost"] = Loc.T("显示 Microsoft Store 应用的窗口框架。"),
        ["msedge"] = Loc.T("Microsoft Edge 浏览器。每个标签页、每个插件都是单独的进程,所以有很多个。"),
        ["msedgewebview2"] = Loc.T("给软件内嵌网页界面用的 Edge 组件,很多应用都在用它。"),
        ["chrome"] = Loc.T("Google Chrome 浏览器。每个标签页和插件都是单独的进程。"),
        ["firefox"] = Loc.T("Firefox 浏览器。"),
        ["code"] = Loc.T("Visual Studio Code 编辑器及其插件。"),
        ["cursor"] = Loc.T("Cursor 编辑器及其插件。"),
        ["devenv"] = Loc.T("Visual Studio。"),
        ["node"] = Loc.T("Node.js 程序,常见于开发工具、前端构建和一些桌面软件。"),
        ["python"] = Loc.T("Python 程序。"),
        ["java"] = Loc.T("Java 程序。"),
        ["dotnet"] = Loc.T(".NET 程序。"),
        ["docker desktop"] = Loc.T("Docker Desktop。"),
        ["com.docker.backend"] = Loc.T("Docker Desktop 的后台。"),
        ["vmmemwsl"] = Loc.T("WSL2(Linux 子系统)占用的内存。它默认会把内存占着不还,可在 .wslconfig 限制上限。"),
        ["vmmem"] = Loc.T("虚拟机或 WSL2 占用的内存。"),
        ["wsl"] = Loc.T("Linux 子系统的命令行入口。"),
        ["wslservice"] = Loc.T("Linux 子系统的服务。"),
        ["ollama"] = Loc.T("Ollama 本地大模型服务,模型加载时会占很多内存。"),
        ["ollama app"] = Loc.T("Ollama 的托盘程序。"),
        ["lm studio"] = Loc.T("LM Studio,本地大模型桌面软件。"),
        ["onedrive"] = Loc.T("OneDrive 同步程序。"),
        ["teams"] = Loc.T("Microsoft Teams。"),
        ["ms-teams"] = Loc.T("Microsoft Teams。"),
        ["wechat"] = Loc.T("微信。"),
        ["weixin"] = Loc.T("微信。"),
        ["qq"] = Loc.T("QQ。"),
        ["dingtalk"] = Loc.T("钉钉。"),
        ["feishu"] = Loc.T("飞书。"),
        ["steam"] = Loc.T("Steam 游戏平台。"),
        ["steamwebhelper"] = Loc.T("Steam 内嵌的网页组件,Steam 开着就会有好几个。"),
        ["discord"] = Loc.T("Discord。"),
        ["spotify"] = Loc.T("Spotify。"),
        ["nvcontainer"] = Loc.T("NVIDIA 驱动的后台服务。"),
        ["nvidia share"] = Loc.T("NVIDIA 的录制和分享功能(GeForce Experience)。"),
        ["igfxem"] = Loc.T("Intel 核显的辅助程序。"),
        ["rtkaudservice"] = Loc.T("Realtek 声卡服务。"),
        ["tidywin"] = Loc.T("就是我自己。"),
    };

    /// <summary>Null when we have nothing honest to say.</summary>
    public static string? Explain(string processName)
    {
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return Known.TryGetValue(name, out var text) ? text : null;
    }
}

/// <summary>Things only a system query can tell: disk health, devices with errors, activation.</summary>
public sealed record CheckupFacts(IReadOnlyList<string> UnhealthyDisks, IReadOnlyList<string> ProblemDevices, bool? Activated)
{
    public static readonly CheckupFacts Unknown = new([], [], null);
}

public static class CheckupParser
{
    /// <summary>Reads the small JSON the system query prints. Anything unreadable means "unknown", never "healthy" or "broken".</summary>
    public static CheckupFacts Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return CheckupFacts.Unknown;
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return CheckupFacts.Unknown;

            bool? activated = null;
            if (root.TryGetProperty("license", out var lic))
            {
                if (lic.ValueKind == JsonValueKind.Number && lic.TryGetInt32(out var n)) activated = n == 1;
            }
            return new CheckupFacts(Strings(root, "badDisks"), Strings(root, "problemDevices"), activated);
        }
        catch (JsonException) { return CheckupFacts.Unknown; }
    }

    // PowerShell prints a one-item array as a plain string.
    private static IReadOnlyList<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var e)) return [];
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() is { Length: > 0 } s ? [s] : [],
            JsonValueKind.Array => e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Length > 0).ToList(),
            _ => [],
        };
    }
}

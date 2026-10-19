using TidyWin.Core.Paths;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Startup;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

public sealed record StartupEntry(string Name, string Command, StartupSource Source, bool Enabled)
{
    public string Id => $"{Source}|{Name}";

    /// <summary>Changing machine-wide entries needs administrator rights.</summary>
    public bool IsMachineWide => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;
}

public enum StartupKind { System, Driver, Chat, Cloud, Updater, Game, Tool, Other, Dead, Suspicious }

public enum StartupVerdict { Keep, Optional, Remove }

public sealed record StartupAdvice(StartupKind Kind, string KindLabel, StartupVerdict Verdict, string Note, string? Exe, bool TargetExists);

/// <summary>
/// Explains each start-up entry in plain words and says whether it is safe to turn off. Turning an entry off is always
/// reversible, so the advice only has to be honest, not perfect.
/// </summary>
public static class StartupAdvisor
{
    // Order matters: the first group that matches wins. A key starting with "=" must equal the file name exactly
    // (short names like "qq" would otherwise match inside unrelated words).
    private static readonly (StartupKind Kind, string[] Keys)[] Known =
    [
        (StartupKind.Updater, ["googleupdate", "msedgeupdate", "adobeupdate", "adobegcinvoker", "jusched", "javaupdate", "updater", "autoupdate"]),
        (StartupKind.System, ["securityhealth", "windows security", "windowsdefender", "msascui", "ctfmon"]),
        (StartupKind.Cloud, ["onedrive", "dropbox", "baidunetdisk", "baiduyun", "aliyundrive", "googledrive", "icloud", "nutstore", Loc.T("百度网盘"), Loc.T("坚果云")]),
        (StartupKind.Game, ["steam", "epicgameslauncher", "epic games", "battle.net", "battlenet", "wegame", "uplay", "ubisoft", "ea desktop", "gog galaxy", "riotclient", "leagueclient", "=origin"]),
        (StartupKind.Chat, ["teams", "skype", "wechat", "weixin", "wxwork", "dingtalk", "feishu", "lark", "discord", "telegram", "slack", "zoom", "whatsapp", "=qq", "=tim", "=line", Loc.T("微信"), Loc.T("钉钉"), Loc.T("飞书")]),
        (StartupKind.Driver, ["nvidia", "nvbackend", "nvcontainer", "geforce", "realtek", "rtkaud", "rthdv", "radeon", "amdrsserv", "igfx", "intel", "synaptics", "logitech", "lghub", "razer", "steelseries", "corsair", "icue", "armoury", "lenovo", "thinkpad", "bluetooth", "touchpad", "wacom", "=msi", "msicenter", "=hpsupport"]),
        (StartupKind.Tool, ["docker", "ollama", "lmstudio", "jetbrains", "everything", "powertoys", "clash", "v2ray", "sunlogin", "todesk", "anydesk", "teamviewer", "utools", "snipaste", "autohotkey", "listary", "ditto"]),
    ];

    public static StartupAdvice Advise(StartupEntry entry, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var (exe, args) = CommandLine.Split(entry.Command, fileExists);
        var fileName = string.IsNullOrEmpty(exe) ? "" : Path.GetFileNameWithoutExtension(exe);
        var haystack = (entry.Name + " " + fileName).ToLowerInvariant();
        var cmd = entry.Command.ToLowerInvariant();
        var exists = string.IsNullOrEmpty(exe) || !Path.IsPathRooted(exe) || fileExists(exe);

        if (IsSuspicious(exe, cmd))
            return new(StartupKind.Suspicious, Loc.T("可疑"), StartupVerdict.Remove,
                Loc.T("它放在临时文件夹或公共文件夹里,或者用了编码过的命令。正规软件很少这样启动。建议先关掉,再去「安全体检」看看。"), exe, exists);

        if (!exists)
            return new(StartupKind.Dead, Loc.T("已失效"), StartupVerdict.Remove,
                Loc.T("它指向的程序已经不存在了(多半是软件卸载后留下的)。每次开机 Windows 都白找一遍,可以放心移除。"), exe, false);

        foreach (var (kind, keys) in Known)
        {
            foreach (var key in keys)
            {
                var hit = key[0] == '=' ? fileName.Equals(key[1..], StringComparison.OrdinalIgnoreCase) : haystack.Contains(key);
                if (hit) return For(kind, exe);
            }
        }
        return For(StartupKind.Other, exe);
    }

    private static StartupAdvice For(StartupKind kind, string? exe) => kind switch
    {
        StartupKind.System => new(kind, Loc.T("系统组件"), StartupVerdict.Keep, Loc.T("Windows 自带的组件,建议保留。"), exe, true),
        StartupKind.Driver => new(kind, Loc.T("硬件相关"), StartupVerdict.Keep,
            Loc.T("显卡、声卡、键鼠、笔记本的辅助程序。关掉通常不影响使用,但可能失去快捷键、灯效或音效面板。不确定就保留。"), exe, true),
        StartupKind.Chat => new(kind, Loc.T("聊天软件"), StartupVerdict.Optional, Loc.T("不需要开机就上线的话可以关掉,用的时候手动打开。会省一点开机时间和内存。"), exe, true),
        StartupKind.Cloud => new(kind, Loc.T("网盘同步"), StartupVerdict.Optional, Loc.T("关掉后文件不会自动同步,需要时手动打开。每天都用的话建议保留。"), exe, true),
        StartupKind.Updater => new(kind, Loc.T("更新程序"), StartupVerdict.Optional, Loc.T("只负责检查软件新版本。关掉不影响使用,想更新时打开软件本身就会检查。"), exe, true),
        StartupKind.Game => new(kind, Loc.T("游戏平台"), StartupVerdict.Optional, Loc.T("游戏平台开机自启很占资源。关掉后想玩时再手动打开。"), exe, true),
        StartupKind.Tool => new(kind, Loc.T("常驻工具"), StartupVerdict.Optional, Loc.T("你自己装的常驻工具。依赖它的快捷功能时保留,否则可以关掉。"), exe, true),
        _ => new(kind, Loc.T("其他软件"), StartupVerdict.Optional, Loc.T("不认识的自启项。如果用得少,关掉不会有任何损失,随时可以再打开。"), exe, true),
    };

    private static bool IsSuspicious(string exe, string cmd)
    {
        var p = exe.ToLowerInvariant();
        if (p.Contains(@"\appdata\local\temp\") || p.Contains(@"\windows\temp\") || p.Contains(@"\users\public\")) return true;
        if (cmd.Contains("-encodedcommand") || cmd.Contains(" -enc ") || cmd.Contains("frombase64string")) return true;
        if (cmd.Contains("http://") || cmd.Contains("https://")) return true;
        return false;
    }

    /// <summary>How many enabled entries could be turned off without losing anything the person cares about.</summary>
    public static int Droppable(IEnumerable<(StartupEntry Entry, StartupAdvice Advice)> all) =>
        all.Count(x => x.Entry.Enabled
                       && (x.Advice.Kind is StartupKind.Chat or StartupKind.Updater or StartupKind.Game
                           || x.Advice.Verdict == StartupVerdict.Remove));
}

using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Paths;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Erase;

/// <summary>What kind of trace a place holds. Decides what is ticked for the person to begin with.</summary>
public enum EraseKind
{
    /// <summary>What was said and done: conversation transcripts, prompt history, edit history. Ticked.</summary>
    Conversations,

    /// <summary>Who you are to the service: tokens, account files, browser-style login state. Ticked.</summary>
    Credentials,

    /// <summary>Caches, logs, telemetry, temporary files. Ticked.</summary>
    CachesAndLogs,

    /// <summary>Things the person wrote or chose: settings, custom commands, instructions, plugins, installed extensions. Never ticked on its own.</summary>
    Settings,

    /// <summary>What the tool made for the person: generated images and videos. Their work, so never ticked on its own.</summary>
    Creations,
}

/// <summary>
/// Where a location is measured from. PackagedRoaming is the roaming folder inside a Microsoft Store app's package
/// (its folder name carries a publisher hash, so it is found by the tool's package prefix). Absolute is a full path found at scan time.
/// </summary>
public enum EraseRoot { Home, UserProfile, AppData, LocalAppData, PackagedRoaming, Absolute }

/// <summary>
/// One place a tool leaves things. A path ending in a file name is a single file; otherwise it is a folder.
/// <paramref name="Host"/> is the program that has the place open (an editor, for an extension's data); it must be closed before the place is cleaned.
/// </summary>
public sealed record EraseLocation(string Id, EraseKind Kind, string Title, string Detail, EraseRoot Root, string RelativePath, bool IsFile = false, string? Host = null);

/// <summary>
/// Everything one AI tool leaves on a computer: the folders and files, the keys it may have put in environment variables,
/// the entries in Windows' credential store, and the things only the person can do (signing out, deleting server-side data).
/// </summary>
/// <param name="HomeVariable">The variable that moves the tool's own folder, when the person has set it.</param>
public sealed record EraseTool(
    string Id,
    string Name,
    string Summary,
    KnownFolder HomeAnchor,
    string HomeRelative,
    string? HomeVariable,
    IReadOnlyList<string> Processes,
    IReadOnlyList<EraseLocation> Locations,
    IReadOnlyList<string> Variables,
    IReadOnlyList<string> CredentialKeywords,
    IReadOnlyList<string> BeforeSteps,
    IReadOnlyList<string> AfterSteps,
    string? PackagePrefix = null,
    Func<IAiEnvironment, IFileSystem, IReadOnlyList<ResolvedErase>>? Dynamic = null);

public sealed record ResolvedErase(EraseLocation Location, string Path);

/// <summary>What was found at one location. Nothing about the content is read; only names, sizes and dates.</summary>
public sealed record EraseScanItem(ResolvedErase Resolved, bool Exists, int FileCount, long Bytes, DateTime? NewestUtc, IReadOnlyList<FsFile> Files)
{
    public EraseLocation Location => Resolved.Location;
    public string Path => Resolved.Path;
}

/// <summary>An environment variable that holds a secret. Only its name and where it lives are known; the value is never kept.</summary>
public sealed record SecretVariable(string Name, bool InUserScope, bool InMachineScope, bool InThisProcess)
{
    /// <summary>Only the person's own stored variables can be removed here; a machine-wide one needs administrator rights, and a process one is gone when its program is.</summary>
    public bool Removable => InUserScope;
}

/// <summary>An entry in Windows Credential Manager. <see cref="Type"/> is needed to delete it.</summary>
public sealed record CredentialEntry(string Target, int Type);

public interface IEraseSystem
{
    bool IsRunning(string processName);
    IReadOnlyList<SecretVariable> FindVariables(IEnumerable<string> names);
    bool RemoveUserVariable(string name);
    IReadOnlyList<CredentialEntry> FindCredentials(IEnumerable<string> keywords);
    bool RemoveCredential(CredentialEntry entry);
}

public static class AiErase
{
    public static string KindTitle(EraseKind kind) => kind switch
    {
        EraseKind.Conversations => Loc.T("对话与操作记录"),
        EraseKind.Credentials => Loc.T("登录与账号信息"),
        EraseKind.CachesAndLogs => Loc.T("缓存、日志与统计"),
        EraseKind.Creations => Loc.T("它生成的作品"),
        _ => Loc.T("设置与你自己写的内容"),
    };

    public static string KindNote(EraseKind kind) => kind switch
    {
        EraseKind.Conversations => Loc.T("你和它说过的话、让它做过的事。删掉后无法恢复。"),
        EraseKind.Credentials => Loc.T("删掉后这台电脑就不再是登录状态,再用要重新登录。"),
        EraseKind.CachesAndLogs => Loc.T("删掉没有代价,用到时会自己重建。"),
        EraseKind.Creations => Loc.T("生成出来的图片和视频是你的成果,删掉就没了。我没有替你勾,想清就自己勾上。"),
        _ => Loc.T("你自己写的设置、命令、规则和装的扩展。我没有替你勾,想一起清就自己勾上。"),
    };

    public static bool SelectedByDefault(EraseKind kind) => kind is not (EraseKind.Settings or EraseKind.Creations);

    /// <summary>The programs that must be closed before this scan can be cleaned: the tool's own, plus the host of each place that exists.</summary>
    public static IReadOnlyList<string> ProcessesFor(EraseTool tool, IEnumerable<EraseScanItem> scan) =>
        tool.Processes.Concat(scan.Select(s => s.Location.Host).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static EraseTool? ById(string id) => Tools.FirstOrDefault(t => t.Id == id);

    private static EraseLocation C(string id, EraseKind k, string title, string detail, string rel, bool file = false, EraseRoot root = EraseRoot.Home) =>
        new(id, k, title, detail, root, rel, file);

    /// <summary>Claude desktop places, once for the installer version (AppData\Roaming\Claude) and once for the Store version.</summary>
    private static IEnumerable<EraseLocation> Desktop(params (string Id, EraseKind Kind, string Title, string Detail, string Rel, bool File)[] items)
    {
        foreach (var i in items)
        {
            yield return new EraseLocation("claude.desktop." + i.Id, i.Kind, Loc.T("桌面版:") + i.Title, i.Detail, EraseRoot.AppData, @"Claude\" + i.Rel, i.File);
            yield return new EraseLocation("claude.desktop." + i.Id + ".msix", i.Kind, Loc.T("桌面版(应用商店版):") + i.Title, i.Detail, EraseRoot.PackagedRoaming, @"Claude\" + i.Rel, i.File);
        }
    }
    /// <summary>Cline's data inside one editor. The editor has to be closed before it is cleaned, but only when it actually has Cline data.</summary>
    private static IEnumerable<EraseLocation> ClineIn(string key, string appFolder, string process)
    {
        var b = appFolder + @"\User\globalStorage\saoudrizwan.claude-dev\";
        var where = $"({appFolder})";
        EraseLocation L(string id, EraseKind kind, string title, string detail, string rel) =>
            new($"cline.{key}.{id}", kind, $"{title} {where}", detail, EraseRoot.AppData, b + rel, false, process);

        yield return L("tasks", EraseKind.Conversations, Loc.T("任务与对话记录"), Loc.T("每个任务一份:对话、它改过的文件的快照、命令输出。"), "tasks");
        yield return L("state", EraseKind.Conversations, Loc.T("任务历史列表与状态"), "", "state");
        yield return L("cache", EraseKind.CachesAndLogs, Loc.T("缓存"), "", "cache");
        yield return L("checkpoints", EraseKind.CachesAndLogs, Loc.T("检查点"), Loc.T("它做过的每一步改动的快照。"), "checkpoints");
        yield return L("settings", EraseKind.Settings, Loc.T("扩展设置"), "", "settings");
    }

    internal static IEnumerable<EraseLocation> ComfyPlaces(string idPrefix, EraseRoot root, string baseRel)
    {
        EraseLocation L(string id, EraseKind kind, string title, string detail, string rel, bool file = false) =>
            new(idPrefix + id, kind, title, detail, root, baseRel + @"\" + rel, file);

        yield return L("input", EraseKind.Conversations, Loc.T("你放进去的素材"), Loc.T("喂给它的图片、遮罩等,是你的素材,不是它生成的。"), "input");
        yield return L("output", EraseKind.Creations, Loc.T("生成出来的作品"), Loc.T("它生成的图片和视频,里面还嵌着当时的提示词和工作流。这是你的成果,默认不勾。"), "output");
        yield return L("temp", EraseKind.CachesAndLogs, Loc.T("临时文件"), Loc.T("预览图等中间产物。"), "temp");
        yield return L("log", EraseKind.CachesAndLogs, Loc.T("运行日志"), "", @"user\comfyui.log", true);
        yield return L("logprev", EraseKind.CachesAndLogs, Loc.T("上一次的运行日志"), "", @"user\comfyui.prev.log", true);
        yield return L("workflows", EraseKind.Settings, Loc.T("保存的工作流"), Loc.T("你搭的节点流程。"), @"user\default\workflows");
        yield return L("settings", EraseKind.Settings, Loc.T("界面设置"), "", @"user\default\comfy.settings.json", true);
        yield return L("customnodes", EraseKind.Settings, Loc.T("自定义节点"), Loc.T("装的第三方节点,删后要重装。"), "custom_nodes");
        yield return L("models", EraseKind.Settings, Loc.T("已下载的模型"), Loc.T("占磁盘最多的部分。删了要重新下载,所以没有替你勾。"), "models");
    }

    public static IReadOnlyList<EraseTool> Tools { get; } =
    [
        // ───────────────────────── Claude ─────────────────────────
        new("claude", Loc.T("Claude(Claude Code 与桌面版)"),
            Loc.T("命令行 Claude Code 在 .claude 文件夹里留的东西,加上 Claude 桌面版(安装包版和应用商店版都认)的登录状态、会话和缓存。"),
            KnownFolder.UserProfile, ".claude", "CLAUDE_CONFIG_DIR",
            ["claude"],
            [
                C("claude.projects", EraseKind.Conversations, Loc.T("对话记录"), Loc.T("按项目保存,每个项目一份,里面是你和 Claude 的完整对话。"), "projects"),
                C("claude.history", EraseKind.Conversations, Loc.T("输入过的提示词历史"), Loc.T("你在终端里敲过的每一句。"), "history.jsonl", true),
                C("claude.todos", EraseKind.Conversations, Loc.T("任务清单记录"), Loc.T("对话里生成过的待办。"), "todos"),
                C("claude.plans", EraseKind.Conversations, Loc.T("计划文件"), Loc.T("计划模式下生成的计划。"), "plans"),
                C("claude.filehistory", EraseKind.Conversations, Loc.T("改过的文件的历史版本"), Loc.T("它每次改文件前留下的快照,里面是你项目文件的内容。"), "file-history"),

                C("claude.credentials", EraseKind.Credentials, Loc.T("登录凭据"), Loc.T("访问令牌和刷新令牌。"), ".credentials.json", true),
                C("claude.globalstate", EraseKind.Credentials, Loc.T("账号信息与全局状态"), Loc.T("含账号邮箱、各项目的信任与状态,以及 MCP 服务配置。"), ".claude.json", true, EraseRoot.UserProfile),
                C("claude.globalstate.home", EraseKind.Credentials, Loc.T("账号信息与全局状态(配置目录内)"), Loc.T("设了 CLAUDE_CONFIG_DIR 时它放在配置目录里。"), ".claude.json", true),

                C("claude.shell", EraseKind.CachesAndLogs, Loc.T("终端环境快照"), Loc.T("每次会话记下的终端环境。"), "shell-snapshots"),
                C("claude.statsig", EraseKind.CachesAndLogs, Loc.T("功能开关与统计缓存"), Loc.T("含一个设备标识。"), "statsig"),
                C("claude.telemetry", EraseKind.CachesAndLogs, Loc.T("遥测暂存"), Loc.T("没发出去的使用统计。"), "telemetry"),
                C("claude.debug", EraseKind.CachesAndLogs, Loc.T("调试日志"), "", "debug"),
                C("claude.cache", EraseKind.CachesAndLogs, Loc.T("缓存"), "", "cache"),
                C("claude.ide", EraseKind.CachesAndLogs, Loc.T("编辑器连接记录"), "", "ide"),
                C("claude.sessionenv", EraseKind.CachesAndLogs, Loc.T("会话环境"), "", "session-env"),
                C("claude.backups", EraseKind.CachesAndLogs, Loc.T("配置备份"), Loc.T("全局状态文件的自动备份,里面同样含账号信息。"), "backups"),
                C("claude.paste", EraseKind.CachesAndLogs, Loc.T("粘贴缓存"), Loc.T("你粘贴给它的内容。"), "paste-cache"),
                C("claude.image", EraseKind.CachesAndLogs, Loc.T("图片缓存"), Loc.T("你给它看过的图片。"), "image-cache"),

                C("claude.settings", EraseKind.Settings, Loc.T("设置"), Loc.T("settings.json:权限、环境变量、钩子等。"), "settings.json", true),
                C("claude.settings.local", EraseKind.Settings, Loc.T("本机设置"), "settings.local.json", "settings.local.json", true),
                C("claude.md", EraseKind.Settings, Loc.T("全局说明 CLAUDE.md"), Loc.T("你写给它的长期指示。"), "CLAUDE.md", true),
                C("claude.commands", EraseKind.Settings, Loc.T("自定义命令"), "", "commands"),
                C("claude.agents", EraseKind.Settings, Loc.T("自定义子代理"), "", "agents"),
                C("claude.skills", EraseKind.Settings, Loc.T("技能"), "", "skills"),
                C("claude.plugins", EraseKind.Settings, Loc.T("插件"), Loc.T("装了的插件,删后要重装。"), "plugins"),
                C("claude.hooks", EraseKind.Settings, Loc.T("钩子脚本"), "", "hooks"),
                C("claude.styles", EraseKind.Settings, Loc.T("输出风格"), "", "output-styles"),
                C("claude.keys", EraseKind.Settings, Loc.T("快捷键"), "", "keybindings.json", true),

                // Desktop app: written twice because the installer version keeps it in AppData\Roaming and the Store version inside its own package.
                .. Desktop(
                    ("cookies", EraseKind.Credentials, Loc.T("登录状态(Cookie)"), Loc.T("Claude 桌面版的登录 Cookie。"), "Cookies", true),
                    ("network", EraseKind.Credentials, Loc.T("网络状态"), Loc.T("新版 Chromium 把 Cookie 放在这里。"), "Network", false),
                    ("local", EraseKind.Credentials, Loc.T("本地存储"), Loc.T("网页应用的本地数据,含登录与界面状态。"), "Local Storage", false),
                    ("session", EraseKind.Credentials, Loc.T("会话存储"), "", "Session Storage", false),
                    ("indexeddb", EraseKind.Credentials, "IndexedDB", "", "IndexedDB", false),
                    ("webstorage", EraseKind.Credentials, Loc.T("网页存储"), "", "WebStorage", false),
                    ("appconfig", EraseKind.Credentials, Loc.T("应用状态文件 config.json"), Loc.T("里面可能含加密保存的登录令牌。"), "config.json", true),
                    ("did", EraseKind.Credentials, Loc.T("设备标识"), Loc.T("ant-did:这台电脑在服务端的标识。"), "ant-did", true),
                    ("localstate", EraseKind.Credentials, Loc.T("加密密钥 Local State"), Loc.T("用来解开上面加密内容的密钥;和登录状态一起清掉才彻底。"), "Local State", true),

                    ("codesessions", EraseKind.Conversations, Loc.T("代码会话记录"), Loc.T("桌面版里「代码」功能的会话。"), "claude-code-sessions", false),
                    ("agentsessions", EraseKind.Conversations, Loc.T("智能体模式会话"), Loc.T("桌面版里本地智能体模式的会话和中间文件。"), "local-agent-mode-sessions", false),

                    ("logs", EraseKind.CachesAndLogs, Loc.T("日志"), "", "logs", false),
                    ("cache", EraseKind.CachesAndLogs, Loc.T("缓存"), "", "Cache", false),
                    ("code", EraseKind.CachesAndLogs, Loc.T("脚本缓存"), "", "Code Cache", false),
                    ("gpu", EraseKind.CachesAndLogs, Loc.T("GPU 缓存"), "", "GPUCache", false),
                    ("dawn", EraseKind.CachesAndLogs, Loc.T("图形缓存"), "", "DawnGraphiteCache", false),
                    ("dawn2", EraseKind.CachesAndLogs, Loc.T("图形缓存(WebGPU)"), "", "DawnWebGPUCache", false),
                    ("blob", EraseKind.CachesAndLogs, Loc.T("临时数据"), "", "blob_storage", false),
                    ("crash", EraseKind.CachesAndLogs, Loc.T("崩溃报告"), "", "Crashpad", false),
                    ("sentry", EraseKind.CachesAndLogs, Loc.T("错误上报暂存"), "", "sentry", false),
                    ("dict", EraseKind.CachesAndLogs, Loc.T("拼写词典"), Loc.T("用到时会重新下载。"), "Dictionaries", false),
                    ("shareddict", EraseKind.CachesAndLogs, Loc.T("共享压缩字典"), "", "Shared Dictionary", false),
                    ("protodb", EraseKind.CachesAndLogs, Loc.T("内部数据库缓存"), "", "shared_proto_db", false),
                    ("usage", EraseKind.CachesAndLogs, Loc.T("用量历史"), Loc.T("plan-usage-history.json:额度使用记录。"), "plan-usage-history.json", true),
                    ("builtincode", EraseKind.CachesAndLogs, Loc.T("内置的 Claude Code 程序"), Loc.T("桌面版自己下载的一份命令行程序,通常几百 MB,用到时会重新下载。"), "claude-code", false),

                    ("config", EraseKind.Settings, Loc.T("配置(含 MCP 服务)"), "claude_desktop_config.json", "claude_desktop_config.json", true),
                    ("prefs", EraseKind.Settings, Loc.T("偏好设置"), "", "Preferences", true),
                    ("window", EraseKind.Settings, Loc.T("窗口位置"), "", "window-state.json", true),
                    ("developer", EraseKind.Settings, Loc.T("开发者设置"), "", "developer_settings.json", true),
                    ("nativehost", EraseKind.Settings, Loc.T("浏览器扩展连接组件"), Loc.T("让浏览器里的 Claude 扩展连到桌面版;删后要重新安装扩展的连接。"), "ChromeNativeHost", false)),
            ],
            ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN"],
            ["claude code", "claude-code", "anthropic", "claude"],
            [
                Loc.T("先在 Claude Code 里输入 /logout 退出登录。只删本地文件不会让已经签发的登录令牌失效,退出登录才会在服务器上把它收回。"),
                Loc.T("关掉所有 Claude Code 终端窗口和 Claude 桌面版(托盘里的也要退出)。"),
            ],
            [
                Loc.T("对话在服务器上也有一份(claude.ai 网页端和桌面版的对话在那里)。到 claude.ai 的设置里可以导出、删除对话,也可以删除账号;具体入口以官网当时的页面为准。本机删除不会改变服务器上保存的内容。"),
                Loc.T("浏览器里还留着登录状态:在浏览器设置里清除 claude.ai 和 anthropic.com 的站点数据。"),
                Loc.T("如果用过 API Key,到 Anthropic 的开发者控制台把那把密钥作废;本地删掉只是找不到它,密钥本身还有效。"),
                Loc.T("各项目文件夹里的 .claude 文件夹和 CLAUDE.local.md 也列在「设置与你自己写的内容」里,默认不勾,因为它们是项目自己的一部分(可能已提交到 git);CLAUDE.md 是你写给它的说明,这里不碰。"),
                Loc.T("Claude 桌面版本体用「软件管理」卸载。"),
            ],
            "Claude_",
            Dynamic: EraseDynamic.Projects("claude", (".claude", false), ("CLAUDE.local.md", true))),

        // ───────────────────────── Codex ─────────────────────────
        new("codex", Loc.T("Codex(OpenAI 的命令行)"),
            Loc.T("Codex 在 .codex 文件夹里留的登录令牌、会话记录和日志。"),
            KnownFolder.UserProfile, ".codex", "CODEX_HOME",
            ["codex"],
            [
                C("codex.sessions", EraseKind.Conversations, Loc.T("会话记录"), Loc.T("每次会话一份完整记录。"), "sessions"),
                C("codex.archived", EraseKind.Conversations, Loc.T("已归档的会话"), "", "archived_sessions"),
                C("codex.history", EraseKind.Conversations, Loc.T("输入过的提示词历史"), "", "history.jsonl", true),
                C("codex.memories", EraseKind.Conversations, Loc.T("记忆"), Loc.T("它自己记下的关于你和项目的内容。"), "memories"),

                C("codex.auth", EraseKind.Credentials, Loc.T("登录凭据"), Loc.T("auth.json:ChatGPT 登录令牌或 API Key。"), "auth.json", true),
                C("codex.env", EraseKind.Credentials, Loc.T("环境文件"), Loc.T(".env:可能放着 API Key 和代理设置。"), ".env", true),
                C("codex.install", EraseKind.Credentials, Loc.T("安装标识"), "", "installation_id", true),

                C("codex.log", EraseKind.CachesAndLogs, Loc.T("日志"), "", "log"),
                C("codex.logs", EraseKind.CachesAndLogs, Loc.T("日志(新版)"), "", "logs"),
                C("codex.shell", EraseKind.CachesAndLogs, Loc.T("终端环境快照"), "", "shell_snapshots"),
                C("codex.tmp", EraseKind.CachesAndLogs, Loc.T("临时文件"), "", "tmp"),
                C("codex.tmp2", EraseKind.CachesAndLogs, Loc.T("临时文件(隐藏)"), "", ".tmp"),
                C("codex.cache", EraseKind.CachesAndLogs, Loc.T("缓存"), "", "cache"),
                C("codex.version", EraseKind.CachesAndLogs, Loc.T("版本检查缓存"), "", "version.json", true),
                C("codex.models", EraseKind.CachesAndLogs, Loc.T("模型列表缓存"), "", "models_cache.json", true),

                C("codex.config", EraseKind.Settings, Loc.T("设置"), Loc.T("config.toml:模型、权限、MCP 服务等。"), "config.toml", true),
                C("codex.agents", EraseKind.Settings, Loc.T("全局说明 AGENTS.md"), Loc.T("你写给它的长期指示。"), "AGENTS.md", true),
                C("codex.prompts", EraseKind.Settings, Loc.T("自定义提示"), "", "prompts"),
                C("codex.skills", EraseKind.Settings, Loc.T("技能"), "", "skills"),
                C("codex.rules", EraseKind.Settings, Loc.T("规则"), "", "rules"),
            ],
            ["OPENAI_API_KEY", "CODEX_API_KEY"],
            ["codex"],
            [
                Loc.T("先在终端运行 codex logout 退出登录,让登录令牌在服务器上失效,再关掉所有 Codex 窗口。"),
            ],
            [
                Loc.T("ChatGPT 账号的对话在服务器上:到 chatgpt.com 的设置 → 数据控制里可以删除聊天、导出数据,也可以删除账号;具体入口以官网当时的页面为准。"),
                Loc.T("浏览器里清除 chatgpt.com 和 openai.com 的站点数据,否则网页上还是登录的。"),
                Loc.T("如果用过 API Key,到 OpenAI 平台把那把密钥作废;本地删掉只是找不到它,密钥本身还有效。"),
                Loc.T("各项目文件夹里的 .codex 文件夹列在「设置与你自己写的内容」里,默认不勾;AGENTS.md 属于项目本身,这里不碰。"),
            ],
            Dynamic: EraseDynamic.Projects("codex", (".codex", false))),

        // ───────────────────────── Cursor ─────────────────────────
        new("cursor", "Cursor",
            Loc.T("Cursor 在 AppData 里的账号状态、各项目对话和缓存,以及用户文件夹里的 .cursor。"),
            KnownFolder.AppData, "Cursor", null,
            ["Cursor"],
            [
                C("cursor.workspace", EraseKind.Conversations, Loc.T("各项目的对话与状态"), Loc.T("每个项目一份:聊天记录、打开的文件、状态。"), @"User\workspaceStorage"),
                C("cursor.history", EraseKind.Conversations, Loc.T("文件本地历史"), Loc.T("编辑器自动留下的文件旧版本。"), @"User\History"),
                C("cursor.projects", EraseKind.Conversations, Loc.T("智能体对话记录"), Loc.T("用户文件夹里 .cursor\\projects:每个项目的对话文本和终端记录。"), @".cursor\projects", false, EraseRoot.UserProfile),
                C("cursor.chats", EraseKind.Conversations, Loc.T("旧版对话"), "", @".cursor\chats", false, EraseRoot.UserProfile),
                C("cursor.tracking", EraseKind.Conversations, Loc.T("AI 代码统计"), "", @".cursor\ai-tracking", false, EraseRoot.UserProfile),

                C("cursor.global", EraseKind.Credentials, Loc.T("登录账号与全局数据"), Loc.T("globalStorage(state.vscdb):登录状态,也含其他扩展存的数据。"), @"User\globalStorage"),
                C("cursor.cookies", EraseKind.Credentials, Loc.T("登录 Cookie"), "", "Cookies", true),
                C("cursor.network", EraseKind.Credentials, Loc.T("网络状态"), "", "Network"),
                C("cursor.local", EraseKind.Credentials, Loc.T("本地存储"), "", "Local Storage"),
                C("cursor.session", EraseKind.Credentials, Loc.T("会话存储"), "", "Session Storage"),
                C("cursor.indexeddb", EraseKind.Credentials, "IndexedDB", "", "IndexedDB"),

                C("cursor.cache", EraseKind.CachesAndLogs, Loc.T("缓存"), "", "Cache"),
                C("cursor.cacheddata", EraseKind.CachesAndLogs, Loc.T("编译脚本缓存"), "", "CachedData"),
                C("cursor.code", EraseKind.CachesAndLogs, Loc.T("脚本缓存"), "", "Code Cache"),
                C("cursor.gpu", EraseKind.CachesAndLogs, Loc.T("GPU 缓存"), "", "GPUCache"),
                C("cursor.dawn", EraseKind.CachesAndLogs, Loc.T("图形缓存"), "", "DawnGraphiteCache"),
                C("cursor.dawn2", EraseKind.CachesAndLogs, Loc.T("图形缓存(WebGPU)"), "", "DawnWebGPUCache"),
                C("cursor.logs", EraseKind.CachesAndLogs, Loc.T("日志"), "", "logs"),
                C("cursor.crash", EraseKind.CachesAndLogs, Loc.T("崩溃报告"), "", "Crashpad"),
                C("cursor.blob", EraseKind.CachesAndLogs, Loc.T("临时数据"), "", "blob_storage"),
                C("cursor.vsix", EraseKind.CachesAndLogs, Loc.T("扩展安装包缓存"), "", "CachedExtensionVSIXs"),
                C("cursor.updater", EraseKind.CachesAndLogs, Loc.T("更新程序缓存"), "", "cursor-updater", false, EraseRoot.LocalAppData),

                C("cursor.settings", EraseKind.Settings, Loc.T("设置"), "settings.json", @"User\settings.json", true),
                C("cursor.keys", EraseKind.Settings, Loc.T("快捷键"), "", @"User\keybindings.json", true),
                C("cursor.snippets", EraseKind.Settings, Loc.T("代码片段"), "", @"User\snippets"),
                C("cursor.mcp", EraseKind.Settings, Loc.T("MCP 配置"), Loc.T("mcp.json:可能含你填进去的密钥。"), @".cursor\mcp.json", true, EraseRoot.UserProfile),
                C("cursor.rules", EraseKind.Settings, Loc.T("全局规则"), "", @".cursor\rules", false, EraseRoot.UserProfile),
                C("cursor.skills", EraseKind.Settings, Loc.T("技能"), "", @".cursor\skills", false, EraseRoot.UserProfile),
                C("cursor.extensions", EraseKind.Settings, Loc.T("已装扩展"), Loc.T("删后要重新安装。"), @".cursor\extensions", false, EraseRoot.UserProfile),
                C("cursor.argv", EraseKind.Settings, Loc.T("启动参数"), "", @".cursor\argv.json", true, EraseRoot.UserProfile),
            ],
            ["CURSOR_API_KEY"],
            ["cursor"],
            [
                Loc.T("先关掉所有 Cursor 窗口。包括正在和你聊的那个:这场对话就存在上面的文件里,清除之后找不回来。"),
                Loc.T("在 Cursor 的设置 → 账号里退出登录。"),
            ],
            [
                Loc.T("服务器上的账号和数据:到 cursor.com 的账号页面可以管理或删除账号;具体入口以官网当时的页面为准。"),
                Loc.T("浏览器里清除 cursor.com 的站点数据。"),
                Loc.T("各项目文件夹里的 .cursor 文件夹(规则、技能等)列在「设置与你自己写的内容」里,默认不勾,因为它们是项目自己的一部分。"),
                Loc.T("Cursor 本体用「软件管理」卸载。"),
            ],
            Dynamic: EraseDynamic.Projects("cursor", (".cursor", false))),

        // ───────────────────────── Gemini CLI ─────────────────────────
        new("gemini", Loc.T("Gemini 命令行"),
            Loc.T("Gemini CLI 在 .gemini 文件夹里留的登录令牌和对话检查点。"),
            KnownFolder.UserProfile, ".gemini", null,
            ["gemini"],
            [
                C("gemini.tmp", EraseKind.Conversations, Loc.T("对话与检查点"), Loc.T("按项目保存的对话、检查点和日志。"), "tmp"),
                C("gemini.history", EraseKind.Conversations, Loc.T("历史"), "", "history"),

                C("gemini.oauth", EraseKind.Credentials, Loc.T("Google 登录令牌"), "oauth_creds.json", "oauth_creds.json", true),
                C("gemini.accounts", EraseKind.Credentials, Loc.T("账号信息"), "google_accounts.json", "google_accounts.json", true),
                C("gemini.mcp", EraseKind.Credentials, Loc.T("MCP 登录令牌"), "", "mcp-oauth-tokens.json", true),
                C("gemini.install", EraseKind.Credentials, Loc.T("安装标识"), "", "installation_id", true),

                C("gemini.settings", EraseKind.Settings, Loc.T("设置"), "settings.json", "settings.json", true),
                C("gemini.md", EraseKind.Settings, Loc.T("全局说明 GEMINI.md"), "", "GEMINI.md", true),
                C("gemini.extensions", EraseKind.Settings, Loc.T("扩展"), "", "extensions"),
                C("gemini.commands", EraseKind.Settings, Loc.T("自定义命令"), "", "commands"),
                C("gemini.trusted", EraseKind.Settings, Loc.T("信任的文件夹"), "", "trustedFolders.json", true),
            ],
            ["GEMINI_API_KEY", "GOOGLE_API_KEY"],
            ["gemini"],
            [
                Loc.T("关掉所有 gemini 命令行窗口。"),
            ],
            [
                Loc.T("Google 账号里的 Gemini 活动记录在服务器上:到 myactivity.google.com 可以查看和删除。要收回授权,到 Google 账号的「第三方访问」里移除 Gemini CLI。"),
                Loc.T("如果用过 API Key,到 Google AI Studio 把那把密钥作废;本地删掉只是找不到它,密钥本身还有效。"),
                Loc.T("各项目文件夹里的 .gemini 文件夹列在「设置与你自己写的内容」里,默认不勾;GEMINI.md 属于项目本身,这里不碰。"),
            ],
            Dynamic: EraseDynamic.Projects("gemini", (".gemini", false))),

        // ───────────────────────── Windsurf ─────────────────────────
        new("windsurf", "Windsurf",
            Loc.T("Windsurf 在 AppData 里的账号状态和缓存,以及用户文件夹里 .codeium 下的 Cascade 对话、记忆和代码索引。"),
            KnownFolder.AppData, "Windsurf", null,
            ["Windsurf"],
            [
                C("windsurf.workspace", EraseKind.Conversations, Loc.T("各项目的对话与状态"), Loc.T("每个项目一份:聊天记录、打开的文件、状态。"), @"User\workspaceStorage"),
                C("windsurf.history", EraseKind.Conversations, Loc.T("文件本地历史"), Loc.T("编辑器自动留下的文件旧版本。"), @"User\History"),
                C("windsurf.cascade", EraseKind.Conversations, Loc.T("Cascade 对话记录"), Loc.T("用户文件夹里 .codeium\\windsurf\\cascade。"), @".codeium\windsurf\cascade", false, EraseRoot.UserProfile),
                C("windsurf.memories", EraseKind.Conversations, Loc.T("Cascade 记忆"), Loc.T("它自己记下的关于你和项目的内容。"), @".codeium\windsurf\memories", false, EraseRoot.UserProfile),

                C("windsurf.global", EraseKind.Credentials, Loc.T("登录账号与全局数据"), Loc.T("globalStorage(state.vscdb):登录状态,也含其他扩展存的数据。"), @"User\globalStorage"),
                C("windsurf.cookies", EraseKind.Credentials, Loc.T("登录 Cookie"), "", "Cookies", true),
                C("windsurf.network", EraseKind.Credentials, Loc.T("网络状态"), "", "Network"),
                C("windsurf.local", EraseKind.Credentials, Loc.T("本地存储"), "", "Local Storage"),
                C("windsurf.session", EraseKind.Credentials, Loc.T("会话存储"), "", "Session Storage"),
                C("windsurf.indexeddb", EraseKind.Credentials, "IndexedDB", "", "IndexedDB"),
                C("windsurf.codeiumconfig", EraseKind.Credentials, Loc.T("Codeium 配置"), Loc.T("config.json:可能含登录信息。"), @".codeium\config.json", true, EraseRoot.UserProfile),
                C("windsurf.install", EraseKind.Credentials, Loc.T("安装标识"), "", @".codeium\windsurf\installation_id", true, EraseRoot.UserProfile),

                C("windsurf.cache", EraseKind.CachesAndLogs, Loc.T("缓存"), "", "Cache"),
                C("windsurf.cacheddata", EraseKind.CachesAndLogs, Loc.T("编译脚本缓存"), "", "CachedData"),
                C("windsurf.code", EraseKind.CachesAndLogs, Loc.T("脚本缓存"), "", "Code Cache"),
                C("windsurf.gpu", EraseKind.CachesAndLogs, Loc.T("GPU 缓存"), "", "GPUCache"),
                C("windsurf.dawn", EraseKind.CachesAndLogs, Loc.T("图形缓存"), "", "DawnGraphiteCache"),
                C("windsurf.dawn2", EraseKind.CachesAndLogs, Loc.T("图形缓存(WebGPU)"), "", "DawnWebGPUCache"),
                C("windsurf.logs", EraseKind.CachesAndLogs, Loc.T("日志"), "", "logs"),
                C("windsurf.crash", EraseKind.CachesAndLogs, Loc.T("崩溃报告"), "", "Crashpad"),
                C("windsurf.blob", EraseKind.CachesAndLogs, Loc.T("临时数据"), "", "blob_storage"),
                C("windsurf.vsix", EraseKind.CachesAndLogs, Loc.T("扩展安装包缓存"), "", "CachedExtensionVSIXs"),
                C("windsurf.index", EraseKind.CachesAndLogs, Loc.T("代码索引"), Loc.T("对你项目代码建的索引,里面有代码片段;用到时会重建。"), @".codeium\windsurf\database", false, EraseRoot.UserProfile),
                C("windsurf.tracker", EraseKind.CachesAndLogs, Loc.T("代码改动记录"), "", @".codeium\windsurf\code_tracker", false, EraseRoot.UserProfile),
                C("windsurf.implicit", EraseKind.CachesAndLogs, Loc.T("隐式上下文缓存"), "", @".codeium\windsurf\implicit", false, EraseRoot.UserProfile),
                C("windsurf.server", EraseKind.CachesAndLogs, Loc.T("语言服务程序"), Loc.T("自己下载的后台程序,通常几十到几百 MB,用到时会重新下载。"), @".codeium\bin", false, EraseRoot.UserProfile),

                C("windsurf.settings", EraseKind.Settings, Loc.T("设置"), "settings.json", @"User\settings.json", true),
                C("windsurf.keys", EraseKind.Settings, Loc.T("快捷键"), "", @"User\keybindings.json", true),
                C("windsurf.snippets", EraseKind.Settings, Loc.T("代码片段"), "", @"User\snippets"),
                C("windsurf.mcp", EraseKind.Settings, Loc.T("MCP 配置"), Loc.T("mcp_config.json:可能含你填进去的密钥。"), @".codeium\windsurf\mcp_config.json", true, EraseRoot.UserProfile),
                C("windsurf.workflows", EraseKind.Settings, Loc.T("全局工作流"), "", @".codeium\windsurf\global_workflows", false, EraseRoot.UserProfile),
                C("windsurf.extensions", EraseKind.Settings, Loc.T("已装扩展"), Loc.T("删后要重新安装。"), @".windsurf\extensions", false, EraseRoot.UserProfile),
                C("windsurf.argv", EraseKind.Settings, Loc.T("启动参数"), "", @".windsurf\argv.json", true, EraseRoot.UserProfile),
            ],
            [],
            ["windsurf", "codeium"],
            [
                Loc.T("先关掉所有 Windsurf 窗口。"),
                Loc.T("在 Windsurf 里退出登录。"),
            ],
            [
                Loc.T("服务器上的账号和数据:到 windsurf.com 的账号页面可以管理或删除账号;具体入口以官网当时的页面为准。"),
                Loc.T("浏览器里清除 windsurf.com 和 codeium.com 的站点数据。"),
                Loc.T("各项目文件夹里的 .windsurf 文件夹(规则等)列在「设置与你自己写的内容」里,默认不勾,因为它们是项目自己的一部分。"),
                Loc.T("Windsurf 本体用「软件管理」卸载。"),
            ],
            Dynamic: EraseDynamic.Projects("windsurf", (".windsurf", false))),

        // ───────────────────────── Cline ─────────────────────────
        new("cline", Loc.T("Cline(编辑器里的扩展)"),
            Loc.T("Cline 在编辑器(VS Code、Cursor、Windsurf 等)的扩展数据里留的任务记录,以及用户文件夹里的 .cline。"),
            KnownFolder.UserProfile, ".cline", null,
            [],
            [
                .. ClineIn("code", "Code", "Code"),
                .. ClineIn("insiders", "Code - Insiders", "Code - Insiders"),
                .. ClineIn("vscodium", "VSCodium", "VSCodium"),
                .. ClineIn("cursor", "Cursor", "Cursor"),
                .. ClineIn("windsurf", "Windsurf", "Windsurf"),

                C("cline.data.tasks", EraseKind.Conversations, Loc.T("任务与对话记录(新版)"), "", @"data\tasks"),
                C("cline.secrets", EraseKind.Credentials, Loc.T("密钥文件"), Loc.T("secrets.json:各家模型的 API Key。"), @"data\secrets.json", true),
                C("cline.dataLogs", EraseKind.CachesAndLogs, Loc.T("日志(新版)"), "", @"data\logs"),
                C("cline.dataSettings", EraseKind.Settings, Loc.T("设置(新版)"), "", @"data\settings"),
                C("cline.rules", EraseKind.Settings, Loc.T("全局规则与工作流"), Loc.T("文档文件夹里的 Cline 文件夹;文档位置被搬走的话找不到。"), @"Documents\Cline", false, EraseRoot.UserProfile),
            ],
            [],
            ["cline", "claude-dev"],
            [
                Loc.T("先关掉装了 Cline 的编辑器。哪个编辑器里有 Cline 的数据,就要求哪个编辑器已经关掉;没有数据的编辑器开着不影响。"),
                Loc.T("在 Cline 的设置里把各家的 API Key 清空:有的密钥存在编辑器自己的加密存储里,这里没法单独删它。"),
            ],
            [
                Loc.T("密钥本身还有效:到各家模型服务的控制台把用过的 API Key 作废;服务器上保留的对话和账号按各家服务的说明处理。"),
                Loc.T("用 Cline 账号登录的话,到 cline.bot 的账号页面管理或删除账号;具体入口以官网当时的页面为准。"),
                Loc.T("Cline 扩展本体到编辑器的扩展面板里卸载。"),
            ]),

        // ───────────────────────── Ollama ─────────────────────────
        new("ollama", Loc.T("Ollama(本地模型)"),
            Loc.T("Ollama 的提示词历史、登录用的密钥对、日志,以及下载到本机的模型(通常是最占磁盘的部分)。"),
            KnownFolder.UserProfile, ".ollama", null,
            ["ollama", "ollama app"],
            [
                C("ollama.history", EraseKind.Conversations, Loc.T("提示词历史"), Loc.T("你在命令行里和模型说过的话。"), "history", true),

                C("ollama.key", EraseKind.Credentials, Loc.T("身份私钥"), Loc.T("id_ed25519:连 ollama.com 时证明你是谁。"), "id_ed25519", true),
                C("ollama.pub", EraseKind.Credentials, Loc.T("身份公钥"), "id_ed25519.pub", "id_ed25519.pub", true),

                C("ollama.applog", EraseKind.CachesAndLogs, Loc.T("应用日志"), "", @"Ollama\app.log", true, EraseRoot.LocalAppData),
                C("ollama.serverlog", EraseKind.CachesAndLogs, Loc.T("服务日志"), "", @"Ollama\server.log", true, EraseRoot.LocalAppData),
                C("ollama.upgradelog", EraseKind.CachesAndLogs, Loc.T("更新日志"), "", @"Ollama\upgrade.log", true, EraseRoot.LocalAppData),
                C("ollama.updates", EraseKind.CachesAndLogs, Loc.T("已下载的更新包"), "", @"Ollama\updates", false, EraseRoot.LocalAppData),

                C("ollama.models", EraseKind.Settings, Loc.T("已下载的模型"), Loc.T("占磁盘最多的部分。删了要重新下载,所以没有替你勾。设了 OLLAMA_MODELS 的话模型在那个位置,这里不处理。"), "models"),
                C("ollama.config", EraseKind.Settings, Loc.T("应用设置"), "", @"Ollama\config.json", true, EraseRoot.LocalAppData),
            ],
            ["OLLAMA_API_KEY"],
            ["ollama"],
            [
                Loc.T("先在托盘里退出 Ollama。如果登录过 ollama.com,可以先运行 ollama signout。"),
            ],
            [
                Loc.T("ollama.com 账号在服务器上:登录过的话,到账号设置里把这台电脑的公钥(Keys)删掉,再决定要不要删账号;具体入口以官网当时的页面为准。"),
                Loc.T("本地对话只存在本机,删了就没有了;ollama.com 上云端模型的对话遵循它的隐私说明。"),
                Loc.T("Ollama 本体用「软件管理」卸载。"),
            ]),

        // ───────────────────────── ComfyUI ─────────────────────────
        new("comfyui", Loc.T("ComfyUI(桌面版)"),
            Loc.T("ComfyUI 桌面版的登录状态、日志,以及它的数据文件夹(默认在「文档」里)里的素材、生成的作品、工作流和模型。"),
            KnownFolder.AppData, "ComfyUI", null,
            ["ComfyUI"],
            [
                C("comfyui.cookies", EraseKind.Credentials, Loc.T("登录状态(Cookie)"), Loc.T("用 Comfy 账号登录过的话,登录状态在这里。"), "Cookies", true),
                C("comfyui.network", EraseKind.Credentials, Loc.T("网络状态"), "", "Network"),
                C("comfyui.local", EraseKind.Credentials, Loc.T("本地存储"), "", "Local Storage"),
                C("comfyui.session", EraseKind.Credentials, Loc.T("会话存储"), "", "Session Storage"),
                C("comfyui.indexeddb", EraseKind.Credentials, "IndexedDB", "", "IndexedDB"),

                C("comfyui.logs", EraseKind.CachesAndLogs, Loc.T("日志"), "", "logs"),
                C("comfyui.cache", EraseKind.CachesAndLogs, Loc.T("缓存"), "", "Cache"),
                C("comfyui.code", EraseKind.CachesAndLogs, Loc.T("脚本缓存"), "", "Code Cache"),
                C("comfyui.gpu", EraseKind.CachesAndLogs, Loc.T("GPU 缓存"), "", "GPUCache"),

                C("comfyui.appconfig", EraseKind.Settings, Loc.T("桌面版配置"), Loc.T("config.json:记着数据文件夹放在哪。"), "config.json", true),
                C("comfyui.extramodels", EraseKind.Settings, Loc.T("额外模型位置"), "extra_models_config.yaml", "extra_models_config.yaml", true),

                .. ComfyPlaces("comfyui.docs.", EraseRoot.UserProfile, @"Documents\ComfyUI"),
            ],
            [],
            ["comfy"],
            [
                Loc.T("先把 ComfyUI 完全关掉(包括托盘里的)。"),
                Loc.T("生成的图片和视频、保存的工作流默认没有替你勾:它们是你的作品和你搭的流程,想清再自己勾上。"),
            ],
            [
                Loc.T("如果用过 Comfy 账号(在线节点、云端算力),服务器上的账号和用量记录要到 comfy.org 的账号页面处理;具体入口以官网当时的页面为准。"),
                Loc.T("这里只认桌面版和它记着的数据文件夹。便携版或自己搭的 ComfyUI 位置不固定,请到它所在文件夹里清 output、input、temp 和 user。"),
                Loc.T("ComfyUI 本体用「软件管理」卸载。"),
            ],
            Dynamic: (env, fs) => EraseDynamic.ComfyBases(env, fs, ComfyPlaces)),
    ];

    // ───────────────────────── resolving ─────────────────────────

    /// <summary>
    /// A folder named by a variable is only trusted when it cannot swallow the profile or a system folder:
    /// a drive root, a system folder, or anything that contains the user's profile or AppData is refused.
    /// </summary>
    public static bool IsSafeHomeOverride(string? path, IAiEnvironment env)
    {
        var p = WindowsPath.Normalize(path);
        if (p is null || WindowsPath.IsRoot(p)) return false;

        var anchors = new[] { KnownFolder.UserProfile, KnownFolder.AppData, KnownFolder.LocalAppData }
            .Select(k => WindowsPath.Normalize(env.Folder(k))).Where(a => a is not null).Select(a => a!).ToList();
        if (anchors.Any(a => WindowsPath.IsSameOrUnder(a, p))) return false; // the same as, or above, the profile or AppData

        // A folder inside the person's own profile is theirs to place; elsewhere, system folders are refused.
        if (anchors.Any(a => WindowsPath.IsStrictlyUnder(p, a))) return true;

        var first = p.Split('\\', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "";
        return first is not ("Windows" or "Program Files" or "Program Files (x86)" or "ProgramData" or "Users" or "System Volume Information");
    }

    public static string? HomePath(EraseTool tool, IAiEnvironment env)
    {
        if (tool.HomeVariable is { } v && env.Variable(v) is { Length: > 0 } custom && IsSafeHomeOverride(custom, env))
            return WindowsPath.Normalize(custom);

        var baseDir = env.Folder(tool.HomeAnchor);
        return string.IsNullOrWhiteSpace(baseDir) ? null : WindowsPath.Normalize(baseDir.TrimEnd('\\', '/') + "\\" + tool.HomeRelative);
    }

    /// <summary>The roaming folders inside the tool's Microsoft Store packages. Needs the disk to look; without it there are none.</summary>
    private static IReadOnlyList<string> PackageRoaming(EraseTool tool, IAiEnvironment env, IFileSystem? fs)
    {
        if (fs is null || string.IsNullOrEmpty(tool.PackagePrefix)) return [];
        var local = env.Folder(KnownFolder.LocalAppData);
        if (string.IsNullOrWhiteSpace(local)) return [];
        return fs.EnumerateDirectories(local.TrimEnd('\\', '/') + @"\Packages")
            .Where(d => System.IO.Path.GetFileName(d.TrimEnd('\\', '/')).StartsWith(tool.PackagePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.TrimEnd('\\', '/') + @"\LocalCache\Roaming")
            .ToList();
    }

    public static IReadOnlyList<ResolvedErase> Resolve(EraseTool tool, IAiEnvironment env, IFileSystem? fs = null)
    {
        var home = HomePath(tool, env);
        var list = new List<ResolvedErase>();
        IReadOnlyList<string>? packages = null;
        foreach (var loc in tool.Locations)
        {
            if (loc.Root == EraseRoot.Absolute)
            {
                if (WindowsPath.Normalize(loc.RelativePath) is { } abs && !WindowsPath.IsRoot(abs)) list.Add(new ResolvedErase(loc, abs));
                continue;
            }

            if (loc.Root == EraseRoot.PackagedRoaming)
            {
                foreach (var pkg in packages ??= PackageRoaming(tool, env, fs))
                {
                    var pp = WindowsPath.Normalize(pkg + "\\" + loc.RelativePath);
                    if (pp is not null && !WindowsPath.IsRoot(pp)) list.Add(new ResolvedErase(loc, pp));
                }
                continue;
            }

            var baseDir = loc.Root switch
            {
                EraseRoot.Home => home,
                EraseRoot.UserProfile => env.Folder(KnownFolder.UserProfile),
                EraseRoot.AppData => env.Folder(KnownFolder.AppData),
                _ => env.Folder(KnownFolder.LocalAppData),
            };
            if (string.IsNullOrWhiteSpace(baseDir)) continue;

            var path = WindowsPath.Normalize(baseDir.TrimEnd('\\', '/') + "\\" + loc.RelativePath);
            if (path is not null && !WindowsPath.IsRoot(path)) list.Add(new ResolvedErase(loc, path));
        }

        // Places only the disk can tell (projects an editor remembers, a data folder an app was pointed at).
        if (fs is not null && tool.Dynamic is { } dynamic)
        {
            try { list.AddRange(dynamic(env, fs)); }
            catch (Exception) { /* a file that cannot be read or parsed just means no extra places */ }
        }

        // The same place can be named twice (a variable that points back at the default); it is listed once.
        return list.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    // ───────────────────────── scanning ─────────────────────────

    /// <summary>Only names, sizes and dates are read. Folders are walked without following links.</summary>
    public static EraseScanItem ScanOne(ResolvedErase r, IFileSystem fs)
    {
        if (r.Location.IsFile)
        {
            return fs.GetFile(r.Path) is { } f
                ? new EraseScanItem(r, true, 1, f.IsLink ? 0 : f.Length, f.LastWriteUtc, [f])
                : new EraseScanItem(r, false, 0, 0, null, []);
        }

        if (!fs.DirectoryExists(r.Path)) return new EraseScanItem(r, false, 0, 0, null, []);

        var files = new List<FsFile>();
        long total = 0;
        DateTime? newest = null;
        foreach (var f in fs.EnumerateFilesRecursive(r.Path))
        {
            files.Add(f);
            total += f.IsLink ? 0 : f.Length;
            if (newest is null || f.LastWriteUtc > newest) newest = f.LastWriteUtc;
        }

        // An empty folder is still a trace of the tool, so it is listed (and then tidied away).
        return new EraseScanItem(r, true, files.Count, total, newest, files);
    }

    public static IReadOnlyList<EraseScanItem> Scan(EraseTool tool, IAiEnvironment env, IFileSystem fs, CancellationToken ct = default)
    {
        var items = new List<EraseScanItem>();
        foreach (var r in Resolve(tool, env, fs))
        {
            ct.ThrowIfCancellationRequested();
            var item = ScanOne(r, fs);
            if (item.Exists) items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// The guard for exactly the picked items: each folder as a root, each single file by its exact path.
    /// Drive roots are refused by <see cref="PathGuard"/> itself.
    /// </summary>
    public static PathGuard BuildGuard(IEnumerable<EraseScanItem> picked, IReparseProbe probe)
    {
        var list = picked.ToList();
        var roots = list.Where(i => !i.Location.IsFile).Select(i => i.Path);
        var files = list.Where(i => i.Location.IsFile).Select(i => i.Path);
        return new PathGuard(roots, [], probe, files);
    }
}

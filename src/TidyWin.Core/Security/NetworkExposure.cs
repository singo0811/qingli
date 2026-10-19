using TidyWin.Core.Localization;
namespace TidyWin.Core.Security;

/// <summary>A well-known local AI service that usually has no login of its own.</summary>
public sealed record AiService(
    string Name,
    int[] Ports,
    string[] ProcessNames,
    bool PortIsDistinctive,
    Severity BaseSeverity,
    string Why,
    string HowToClose);

/// <summary>
/// Looks at which programs listen for connections from other machines. The point is local AI tools: many of them
/// (Ollama, LM Studio, ComfyUI, Gradio apps) have no password, so listening on the LAN hands your GPU to anyone nearby.
/// </summary>
public static class NetworkExposure
{
    public static readonly IReadOnlyList<AiService> Services =
    [
        new("Ollama", [11434], ["ollama", "ollama app", "ollama_llama_server"], true, Severity.Risk,
            Loc.T("Ollama 的接口没有身份验证。能连上的人可以直接用你的显卡跑模型、查看并删除你的模型。"),
            Loc.T("把环境变量 OLLAMA_HOST 设为 127.0.0.1(或直接删掉它),然后重启 Ollama。")),
        new(Loc.T("LM Studio 服务"), [1234], ["lm studio", "lms"], true, Severity.Risk,
            Loc.T("LM Studio 的本地服务默认没有密码。对局域网开放后,别人能直接调用你加载的模型。"),
            Loc.T("在 LM Studio 的开发者页里,把「在局域网中提供服务」关掉。")),
        new("ComfyUI", [8188], [], true, Severity.Risk,
            Loc.T("ComfyUI 没有登录,而且装了自定义节点后可以执行任意代码。对局域网开放等于让别人能在你电脑上跑东西。"),
            Loc.T("启动时去掉 --listen 参数(或改成 --listen 127.0.0.1)。")),
        new(Loc.T("Gradio / WebUI(Stable Diffusion、text-generation-webui 等)"), [7860, 7861], [], false, Severity.Risk,
            Loc.T("这类网页界面默认没有密码,对局域网开放后任何人都能用,有些还能读写你电脑上的文件。"),
            Loc.T("启动参数里去掉 --listen,或改为只监听 127.0.0.1;确实需要远程访问时加上用户名和密码。")),
        new(Loc.T("llama.cpp 服务"), [8080], ["llama-server"], false, Severity.Risk,
            Loc.T("llama.cpp 的服务默认没有密码,对局域网开放后任何人都能调用。"),
            Loc.T("启动时用 --host 127.0.0.1,需要远程访问时加 --api-key。")),
        new(Loc.T("本地代理软件(Clash、V2Ray 等)"), [7890, 7891, 7897, 10808, 10809, 1080, 9090, 9097],
            ["clash", "clash-verge", "clash-win64", "verge-mihomo", "mihomo", "clash-meta", "v2ray", "v2rayn", "xray", "sing-box", "nekoray", "hysteria"],
            false, Severity.Warn,
            Loc.T("代理软件开了「允许局域网连接」后,同一网络里的任何人都能借你的代理上网,你的出口 IP 和流量会算在你头上;它的控制面板端口如果也对外开放,别人还能改你的规则。"),
            Loc.T("在代理软件里关掉「允许局域网连接」(Allow LAN);控制面板(External Controller)改为只监听 127.0.0.1,并设置访问密钥。")),
        new("Jupyter", [8888], ["jupyter", "jupyter-lab", "jupyter-notebook"], false, Severity.Warn,
            Loc.T("Jupyter 默认有令牌保护,但令牌一旦泄露(比如在截图或链接里),别人就能在你电脑上运行代码。"),
            Loc.T("启动时用 --ip=127.0.0.1;不要把带 token 的链接发给别人。")),
    ];

    private static readonly HashSet<string> GenericRuntimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "pythonw", "node", "uvicorn", "gunicorn", "conda", "wslrelay", "com.docker.backend",
    };

    public sealed record Match(AiService Service, bool Sure, IReadOnlyList<Listener> Listeners);

    public static IReadOnlyList<Match> FindAiServices(IEnumerable<Listener> listeners)
    {
        var matches = new Dictionary<string, (AiService svc, bool sure, List<Listener> items)>();

        foreach (var l in listeners)
        {
            if (!l.ReachableFromOutside) continue;
            var name = l.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? l.ProcessName[..^4] : l.ProcessName;

            foreach (var svc in Services)
            {
                var byName = svc.ProcessNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase));
                var byPort = svc.Ports.Contains(l.Port) && (svc.PortIsDistinctive || GenericRuntimes.Contains(name));
                if (!byName && !byPort) continue;

                var sure = byName;
                if (!matches.TryGetValue(svc.Name, out var cur)) cur = (svc, sure, []);
                if (!cur.items.Any(x => x.Port == l.Port)) cur.items.Add(l); // IPv4 and IPv6 are one service
                matches[svc.Name] = (cur.svc, cur.sure || sure, cur.items);
                break;
            }
        }
        return matches.Values.Select(m => new Match(m.svc, m.sure, m.items)).ToList();
    }

    /// <summary>Findings for exposed AI services plus a general note about everything else that listens outside.</summary>
    public static IReadOnlyList<Finding> Assess(IReadOnlyList<Listener> listeners, bool firewallConfirmedOn)
    {
        var findings = new List<Finding>();
        var matches = FindAiServices(listeners);

        foreach (var m in matches)
        {
            var ports = string.Join(Loc.T("、"), m.Listeners.Select(l => l.Port).Distinct().OrderBy(p => p));
            var proc = m.Listeners[0].ProcessName;

            // A firewall that is on keeps strangers out unless an allow rule exists. Those rules get created
            // when someone clicks "Allow access" the first time, so this lowers the alarm but does not remove it.
            var severity = m.Service.BaseSeverity;
            var detail = m.Service.Why;
            if (severity == Severity.Risk && firewallConfirmedOn)
            {
                severity = Severity.Warn;
                detail += Loc.T(" Windows 防火墙是开着的,默认会挡住陌生设备,但你第一次启动它时如果点过「允许访问」,这条规则就放行了。");
            }
            else if (!firewallConfirmedOn)
                detail += Loc.T(" 而且没能确认防火墙在保护这台电脑。");

            var title = m.Sure
                ? Loc.F("{0} 对局域网开放了(端口 {1})",m.Service.Name,ports)
                : Loc.F("可能有 {0} 对局域网开放了(端口 {1},程序 {2})",m.Service.Name,ports,proc);

            findings.Add(new Finding("net.ai." + Slug(m.Service.Name), severity, title, detail, m.Service.HowToClose));
        }

        // A proxy tool that listens outside is reported on its own; it does not make "no AI service is exposed" untrue.
        if (matches.All(m => m.Service.Name.StartsWith(Loc.T("本地代理"), StringComparison.Ordinal)))
            findings.Add(new Finding("net.ai.ok", Severity.Ok, Loc.T("没有发现对局域网开放的本地 AI 服务"),
                Loc.T("Ollama、LM Studio、ComfyUI、WebUI、Jupyter 等都没有监听在外网卡上。")));

        var others = ExposedPrograms(listeners)
            .Where(g => !matches.Any(m => m.Listeners.Any(l => l.Pid == g.Pid && l.Port == g.Port)))
            .ToList();
        if (others.Count > 0)
        {
            var shown = string.Join(Loc.T("、"), others.Take(6).Select(o => $"{o.ProcessName}:{o.Port}"));
            findings.Add(new Finding("net.other", Severity.Info, Loc.F("另有 {0} 个程序在监听外部连接",others.Count),
                Loc.F("{0}{1}。多数是正常软件(聊天、同步、游戏),这里只是让你知道。Windows 自带服务不在此列。",shown,(others.Count > 6 ? " …" : ""))));
        }
        return findings;
    }

    /// <summary>Programs outside the Windows folder that accept connections from other machines, one entry per program and port.</summary>
    public static IReadOnlyList<Listener> ExposedPrograms(IEnumerable<Listener> listeners) =>
        listeners
            .Where(l => l.ReachableFromOutside && l.Pid > 4 && !IsWindowsPath(l.ImagePath))
            .GroupBy(l => (l.Pid, l.Port))
            .Select(g => g.First())
            .OrderBy(l => l.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Port)
            .ToList();

    private static bool IsWindowsPath(string? path) =>
        path is null || // unreadable image path means a protected system process
        path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase);

    private static string Slug(string name) =>
        new string(name.ToLowerInvariant().TakeWhile(c => c != ' ' && c != '(' && c != '/').ToArray());
}

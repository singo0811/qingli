using System.Net;
using System.Text.Json;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Security;

/// <summary>
/// Turns the raw machine state into findings. It does not scan files and does not pretend to be an antivirus:
/// the antivirus job belongs to Windows Defender (or whatever the user installed). We only report whether it is working.
/// </summary>
public static class SystemSecurityAdvisor
{
    public const int SignatureWarnDays = 7;
    public const int SignatureRiskDays = 30;

    public static IReadOnlyList<Finding> Assess(SecuritySnapshot s)
    {
        var list = new List<Finding>();
        AddDefender(s.Defender, list);
        AddFirewall(s, list);
        if (s.UacEnabled == false)
            list.Add(new Finding("uac.off", Severity.Risk, Loc.T("用户账户控制(UAC)已关闭"),
                Loc.T("程序可以不经询问就获得管理员权限,恶意程序也一样。"),
                Loc.T("在 Windows 设置里搜索「更改用户账户控制设置」,调回默认级别。")));
        else if (s.UacEnabled == true)
            list.Add(new Finding("uac.on", Severity.Ok, Loc.T("用户账户控制已开启"), Loc.T("程序要管理员权限时会先询问你。")));

        AddHosts(s.HostsText, list);
        AddProxy(s.Proxy, list);
        return Sort(list);
    }

    public static IReadOnlyList<Finding> Sort(IEnumerable<Finding> findings) =>
        findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();

    // ───────── Defender ─────────

    private static void AddDefender(DefenderStatus? d, List<Finding> list)
    {
        if (d is null)
        {
            list.Add(new Finding("defender.unknown", Severity.Info, Loc.T("没能读取 Windows Defender 的状态"),
                Loc.T("可能是你用了别的杀毒软件,也可能是系统策略不让读取。这里无法判断你是否受到保护。")));
            return;
        }

        if (d.RunningMode is { Length: > 0 } mode && mode.Contains("Passive", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new Finding("defender.passive", Severity.Info, Loc.T("Windows Defender 处于被动模式"),
                Loc.T("说明有别的杀毒软件在负责实时防护。它是否正常工作,这里无法检查。")));
            return;
        }

        if (d.AntivirusEnabled == false || d.RealTimeProtection == false)
            list.Add(new Finding("defender.realtime.off", Severity.Risk, Loc.T("实时防护已关闭"),
                Loc.T("现在运行或下载的程序不会被实时检查。"),
                Loc.T("打开「Windows 安全中心 → 病毒和威胁防护」,重新开启实时保护。如果是你为了装某个软件临时关的,装完记得打开。")));
        else if (d.RealTimeProtection == true)
        {
            var age = d.SignatureAgeDays;
            if (age >= SignatureRiskDays)
                list.Add(new Finding("defender.signature.stale", Severity.Risk, Loc.F("病毒库已经 {0} 天没有更新",age),
                    Loc.T("太旧的病毒库认不出新出现的威胁。"),
                    Loc.T("在「Windows 更新」里检查更新;如果一直失败,说明系统更新本身有问题。")));
            else if (age >= SignatureWarnDays)
                list.Add(new Finding("defender.signature.old", Severity.Warn, Loc.F("病毒库 {0} 天没有更新",age),
                    Loc.T("正常情况下每天都会自动更新。"), Loc.T("在「Windows 更新」里检查更新。")));
            else
                list.Add(new Finding("defender.ok", Severity.Ok, Loc.T("Windows Defender 实时防护已开启"),
                    age is null ? Loc.T("病毒库更新时间未知。") : age == 0 ? Loc.T("病毒库今天已更新。") : Loc.F("病毒库 {0} 天前更新。",age)));
        }
        else
            list.Add(new Finding("defender.unknown", Severity.Info, Loc.T("没能确认实时防护是否开启"), Loc.T("系统没有返回这一项。")));

        if (d.TamperProtected == false)
            list.Add(new Finding("defender.tamper.off", Severity.Info, Loc.T("篡改防护已关闭"),
                Loc.T("开启后,恶意程序无法悄悄把防护关掉。"), Loc.T("在「Windows 安全中心 → 病毒和威胁防护设置」里开启。")));
    }

    /// <summary>Parses the JSON that Get-MpComputerStatus gives. Returns null when it is not usable.</summary>
    public static DefenderStatus? ParseDefenderJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;

            bool? Bool(string n) => r.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;
            int? Int(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
                ? i : null;
            string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var d = new DefenderStatus(
                Bool("RealTimeProtectionEnabled"), Int("AntivirusSignatureAge"), Bool("AntivirusEnabled"),
                Str("AMRunningMode"), Bool("IsTamperProtected"));
            // Nothing recognisable at all means the shape was not what we asked for.
            return d.RealTimeProtection is null && d.AntivirusEnabled is null && d.SignatureAgeDays is null ? null : d;
        }
        catch (JsonException) { return null; }
    }

    // ───────── Firewall ─────────

    private static void AddFirewall(SecuritySnapshot s, List<Finding> list)
    {
        var profiles = new (string Name, bool? On)[]
        {
            (Loc.T("域网络"), s.FirewallDomain), (Loc.T("专用网络"), s.FirewallPrivate), (Loc.T("公用网络"), s.FirewallPublic),
        };

        var off = profiles.Where(p => p.On == false).Select(p => p.Name).ToList();
        if (off.Count > 0)
        {
            list.Add(new Finding("firewall.off", Severity.Risk, Loc.F("Windows 防火墙在「{0}」下是关闭的",string.Join(Loc.T("、"), off)),
                Loc.T("别的设备可以直接尝试连接这台电脑上正在监听的程序。"),
                Loc.T("打开「Windows 安全中心 → 防火墙和网络保护」,把对应网络的防火墙打开。")));
        }
        else if (profiles.All(p => p.On == true))
            list.Add(new Finding("firewall.ok", Severity.Ok, Loc.T("Windows 防火墙已开启"), Loc.T("域、专用、公用三种网络都开着。")));
        else
            list.Add(new Finding("firewall.unknown", Severity.Info, Loc.T("没能确认 Windows 防火墙的全部状态"),
                Loc.T("有的网络类型读不到,可能被别的防火墙软件接管了。")));
    }

    /// <summary>True only when every profile is confirmed on. Unknown does not count as on.</summary>
    public static bool FirewallConfirmedOn(SecuritySnapshot s) =>
        s.FirewallDomain == true && s.FirewallPrivate == true && s.FirewallPublic == true;

    // ───────── hosts file ─────────

    private static readonly HashSet<string> IgnoredHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost", "broadcasthost", "ip6-localhost", "ip6-loopback",
        // Docker Desktop writes these on purpose and points them at the LAN address.
        "host.docker.internal", "gateway.docker.internal", "kubernetes.docker.internal",
    };

    public sealed record HostsEntry(string Address, string HostName);

    public static IReadOnlyList<HostsEntry> ParseHosts(string? text)
    {
        var entries = new List<HostsEntry>();
        if (string.IsNullOrEmpty(text)) return entries;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var parts = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out _)) continue;
            foreach (var name in parts.Skip(1))
                if (!IgnoredHostNames.Contains(name)) entries.Add(new HostsEntry(parts[0], name));
        }
        return entries;
    }

    private static bool IsBlockAddress(string address) =>
        IPAddress.TryParse(address, out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(ip));

    private static void AddHosts(string? text, List<Finding> list)
    {
        if (text is null)
        {
            list.Add(new Finding("hosts.unreadable", Severity.Info, Loc.T("没能读取 hosts 文件"), Loc.T("无法检查域名有没有被改写。")));
            return;
        }

        var entries = ParseHosts(text);
        var redirects = entries.Where(e => !IsBlockAddress(e.Address)).ToList();
        var blocks = entries.Where(e => IsBlockAddress(e.Address)).ToList();

        if (redirects.Count > 0)
        {
            var sample = string.Join(Loc.T("、"), redirects.Take(4).Select(e => $"{e.HostName} → {e.Address}"));
            list.Add(new Finding("hosts.redirect", Severity.Warn, Loc.F("hosts 文件把 {0} 个域名指向了别的地址",redirects.Count),
                Loc.F("{0}{1}。有些开发工具会这样做,但恶意程序也会用这一招把你带到假网站。",sample,(redirects.Count > 4 ? " …" : "")),
                Loc.T("如果这些不是你或你信任的软件加的,用记事本(管理员)打开 C:\\Windows\\System32\\drivers\\etc\\hosts 删掉对应行。")));
        }
        if (blocks.Count > 0)
            list.Add(new Finding("hosts.block", Severity.Info, Loc.F("hosts 文件屏蔽了 {0} 个域名",blocks.Count),
                Loc.T("这些域名被指向了本机或空地址,常见于去广告和屏蔽跟踪的做法。")));
        if (redirects.Count == 0 && blocks.Count == 0)
            list.Add(new Finding("hosts.ok", Severity.Ok, Loc.T("hosts 文件没有被改动"), Loc.T("没有发现自定义的域名映射。")));
    }

    // ───────── proxy ─────────

    public static IReadOnlyList<string> ProxyHosts(string? server)
    {
        var hosts = new List<string>();
        if (string.IsNullOrWhiteSpace(server)) return hosts;

        foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var addr = part;
            var eq = addr.IndexOf('=');
            if (eq >= 0) addr = addr[(eq + 1)..];
            var scheme = addr.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) addr = addr[(scheme + 3)..];

            string host;
            if (addr.StartsWith('['))
            {
                var end = addr.IndexOf(']');
                host = end > 0 ? addr[1..end] : addr;
            }
            else
            {
                var colon = addr.LastIndexOf(':');
                host = colon > 0 ? addr[..colon] : addr;
            }
            hosts.Add(host.Trim());
        }
        return hosts;
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    private static void AddProxy(ProxySetting? p, List<Finding> list)
    {
        if (p is null) return;

        if (p.Enabled && !string.IsNullOrWhiteSpace(p.Server))
        {
            var hosts = ProxyHosts(p.Server);
            var remote = hosts.Where(h => !IsLocalHost(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (remote.Count > 0)
                list.Add(new Finding("proxy.remote", Severity.Warn, Loc.T("系统流量正经过另一台机器的代理"),
                    Loc.F("代理地址:{0}。经过代理的网页请求,代理方都能看到,也能改。",string.Join(Loc.T("、"), remote)),
                    Loc.T("如果这不是你公司或你自己设置的,到「设置 → 网络和 Internet → 代理」把它关掉。")));
            else
                list.Add(new Finding("proxy.local", Severity.Info, Loc.T("系统代理指向本机"),
                    Loc.F("{0}。这是 Clash、V2Ray 之类工具常见的做法;如果你没装过这类软件,请留意。",p.Server)));
        }
        if (!string.IsNullOrWhiteSpace(p.PacUrl))
            list.Add(new Finding("proxy.pac", Severity.Info, Loc.T("系统使用了自动代理脚本"),
                Loc.F("脚本地址:{0}。脚本决定哪些网址走代理,如果不是你设置的,请留意。",p.PacUrl)));
        if (!p.Enabled && string.IsNullOrWhiteSpace(p.PacUrl))
            list.Add(new Finding("proxy.none", Severity.Ok, Loc.T("没有设置系统代理"), Loc.T("网络请求直接发出。")));
    }
}

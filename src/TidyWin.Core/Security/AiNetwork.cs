using System.Net;
using System.Net.Sockets;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Security;

/// <summary>
/// A website an AI tool needs. The check sends one plain request with no account, key or cookie, and any answer
/// from the server (even "401 not logged in") proves the network path works.
/// </summary>
/// <param name="Tool">Which program needs it, so the results can be read per tool.</param>
/// <param name="RegionMarkers">Text a server puts in its refusal when the caller's region is not supported.</param>
public sealed record AiEndpoint(string Id, string Tool, string Name, string Host, string Path, string Used, bool IsApi, params string[] RegionMarkers)
{
    public string Url => $"https://{Host}{Path}";
}

/// <summary><see cref="Challenged"/>: the server answered with a human-check page instead of the content (Cloudflare "cf-mitigated: challenge").</summary>
/// <remarks><see cref="Blocked"/>: the site's edge refused this address outright (Cloudflare error 1020 / 1006-1008 or "you have been blocked"). <see cref="RateLimited"/>: HTTP 429 (Cloudflare 1015).</remarks>
public enum ProbeKind { Ok, RegionBlocked, Challenged, Blocked, RateLimited, Timeout, DnsFailed, TlsFailed, Refused, ProxyFailed, Failed }

public sealed record ProbeOutcome(ProbeKind Kind, int? Status = null, int Ms = 0, string? Note = null)
{
    /// <summary>The server answered, so the network path works (even if it then refused the region, asked for a human check, or turned the address away).</summary>
    public bool Reached => Kind is ProbeKind.Ok or ProbeKind.RegionBlocked or ProbeKind.Challenged or ProbeKind.Blocked or ProbeKind.RateLimited;
}

/// <summary>Which address family a trace is forced onto. <see cref="Default"/> lets the system choose, as every ordinary program does.</summary>
public enum TraceFamily { Default, V4, V6 }

/// <summary>
/// What a site's own edge says about the connection that reached it (Cloudflare's /cdn-cgi/trace: ip, loc, colo).
/// This is the address and country the service itself sees, not what a third-party lookup site sees.
/// </summary>
public sealed record HostTrace(string Tool, string Host, TraceFamily Family, bool ViaProxy, string? Ip, string? Country, string? Colo)
{
    public bool Reached => !string.IsNullOrEmpty(Ip);
    public bool IsV6 => Ip?.Contains(':') == true;
}

/// <summary>Where a connection to the internet appears to come from, as a public lookup site reports it.</summary>
public sealed record ExitInfo(string Ip, string? Country, string? Region, string? City, string? Org, string? TimeZone = null)
{
    public string Where => string.Join(" · ", new[] { City, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public override string ToString() => string.Join("  ", new[] { Ip, Where, Org }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>A certificate file an environment variable points at, and whether it is really there.</summary>
public sealed record CertFile(string Variable, string Path, bool Exists);

/// <summary>A proxy a tool keeps in its own configuration (for example ~/.codex/.env), which wins over the system's.</summary>
public sealed record ToolProxy(string Tool, string Source, string Variable, string Value);

/// <param name="ViaProxy">How the computer's own settings would connect (system proxy, PAC or environment variables). Null when nothing is configured.</param>
public sealed record EndpointCheck(AiEndpoint Endpoint, ProbeOutcome Direct, ProbeOutcome? ViaProxy, IReadOnlyList<string> Addresses)
{
    /// <summary>What a program that follows the computer's settings would get.</summary>
    public ProbeOutcome Effective => ViaProxy ?? Direct;
}

/// <summary>What the machine says about proxies and tunnels. All of it is read, none of it is changed.</summary>
public sealed record NetworkEnv(
    ProxySetting? System,
    string? HttpProxy,
    string? HttpsProxy,
    string? AllProxy,
    string? NoProxy,
    IReadOnlyList<string> VirtualAdapters)
{
    public IReadOnlyList<CertFile> CertFiles { get; init; } = [];
    public IReadOnlyList<ToolProxy> ToolProxies { get; init; } = [];

    public string? EnvProxy => FirstText(HttpsProxy, HttpProxy, AllProxy);
    public bool SystemProxyOn => System is { Enabled: true } s && !string.IsNullOrWhiteSpace(s.Server);
    public bool PacOn => !string.IsNullOrWhiteSpace(System?.PacUrl);
    public bool ProxyConfigured => SystemProxyOn || PacOn || EnvProxy is not null;

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

public interface IAiNetworkSource
{
    NetworkEnv ReadEnvironment();
    Task<ProbeOutcome> ProbeAsync(AiEndpoint endpoint, bool followComputerProxy, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct = default);

    /// <summary>Asks a public lookup site who it sees. Null when the site cannot be reached or answers nonsense.</summary>
    Task<ExitInfo?> LookupExitAsync(bool followComputerProxy, CancellationToken ct = default);

    /// <summary>Who owns and where is some other address (for example the DNS server the computer's lookups leave from). Null when unknown.</summary>
    Task<ExitInfo?> LookupAddressAsync(string ip, CancellationToken ct = default) => Task.FromResult<ExitInfo?>(null);

    /// <summary>The reputation of the address the connection leaves from (VPN, proxy, Tor, abuse flags). Needs the person's own ipapi.is key.</summary>
    Task<ReputationLookup> LookupReputationAsync(bool followComputerProxy, CancellationToken ct = default) =>
        Task.FromResult(new ReputationLookup(ReputationStatus.NoKey));

    /// <summary>The earlier exit checks kept on this computer, newest first.</summary>
    IReadOnlyList<ExitHistoryEntry> ReadExitHistory() => [];

    void SaveExitHistory(IReadOnlyList<ExitHistoryEntry> history) { }

    /// <summary>The country the computer's region setting names (two letters), or null.</summary>
    string? SystemCountry();

    /// <summary>Asks the site's own edge what address and country it sees. Never throws: a failure is a trace with no address.</summary>
    Task<HostTrace> TraceAsync(string tool, string host, TraceFamily family, bool followComputerProxy, CancellationToken ct = default);
}

public static class AiNetwork
{
    /// <summary>
    /// Hosts come from the vendors' own documentation: Claude Code's network page lists api.anthropic.com, claude.ai,
    /// platform.claude.com and downloads.claude.ai (sign-in, token refresh and updates); OpenAI's help centre and the Codex
    /// issue tracker show Codex needs auth.openai.com (sign-in), chatgpt.com (the ChatGPT-login backend) and api.openai.com (API keys).
    /// </summary>
    public static readonly IReadOnlyList<AiEndpoint> Endpoints =
    [
        new("claude-api", "Claude Code", Loc.T("Claude 接口"), "api.anthropic.com", "/v1/models", Loc.T("对话、功能开关"), true,
            "Request not allowed"),
        new("claude-login", "Claude Code", Loc.T("Claude 登录"), "claude.ai", "/", Loc.T("账号登录"), true),
        new("claude-console", "Claude Code", Loc.T("Claude 控制台"), "platform.claude.com", "/", Loc.T("登录令牌换取和刷新"), true),
        new("claude-update", "Claude Code", Loc.T("Claude 下载与更新"), "downloads.claude.ai", "/", Loc.T("安装、自动更新、插件"), false),
        new("codex-auth", "Codex / ChatGPT", Loc.T("OpenAI 登录"), "auth.openai.com", "/", Loc.T("登录、换取令牌(登录失败多半卡在这)"), true),
        new("codex-backend", "Codex / ChatGPT", Loc.T("ChatGPT 后端"), "chatgpt.com", "/", Loc.T("用 ChatGPT 账号登录时的对话通道"), true),
        new("openai-api", "Codex / ChatGPT", Loc.T("OpenAI 接口"), "api.openai.com", "/v1/models", Loc.T("用 API Key 时的对话通道"), true,
            "unsupported_country_region_territory"),
        new("gemini", "Gemini", "Google Gemini", "generativelanguage.googleapis.com", "/v1beta/models", Loc.T("Gemini、AI Studio"), true,
            "User location is not supported"),
        new("cursor", "Cursor", Loc.T("Cursor 服务"), "api2.cursor.sh", "/", Loc.T("Cursor 编辑器的 AI 功能"), true),
        new("huggingface", Loc.T("下载"), "Hugging Face", "huggingface.co", "/", Loc.T("下载模型、数据集"), false),
        new("github", Loc.T("下载"), "GitHub", "github.com", "/", Loc.T("下载代码、插件、发布包"), false),
        new("pypi", Loc.T("下载"), "PyPI", "pypi.org", "/simple/", Loc.T("pip 安装 Python 包"), false),
        new("npm", Loc.T("下载"), "npm", "registry.npmjs.org", "/", Loc.T("npm 安装 Node 包、Claude Code 插件"), false),
        new("ollama", Loc.T("下载"), Loc.T("Ollama 模型库"), "registry.ollama.ai", "/", Loc.T("ollama pull 拉取模型"), false),
    ];

    /// <summary>
    /// A human-check answer only says something about this connection from a real API path. Web front doors (sign-in pages,
    /// the site root) put every script-made request behind a check, whoever is asking.
    /// </summary>
    public static bool ChallengeIsMeaningful(AiEndpoint e) => e.IsApi && e.Path != "/";

    /// <summary>
    /// Says what a refusal (400, 403 or 429) from a server means for this connection, or null when it is just an ordinary refusal
    /// (for example 401 "no key"). Region wording wins over the generic block page.
    /// </summary>
    public static ProbeKind? ClassifyRefusal(AiEndpoint endpoint, int status, string body)
    {
        if (status is 400 or 403 && endpoint.RegionMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return ProbeKind.RegionBlocked;
        if (status == 403 && EdgeBlockMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return ProbeKind.Blocked;
        if (status == 429) return ProbeKind.RateLimited;
        return null;
    }

    // Cloudflare: 1020 access denied by a firewall rule; 1006-1008 the address (or its network) is banned. 1010 (browser signature) is left out on purpose: it is about the client, not the address.
    private static readonly string[] EdgeBlockMarkers =
        ["error code: 1020", "error code: 1006", "error code: 1007", "error code: 1008", "sorry, you have been blocked", "you are unable to access"];

    /// <summary>
    /// Countries and regions the services commonly do not serve (mainland China, Hong Kong, Macau, Russia, Belarus, Iran, North Korea,
    /// Cuba, Syria). A short, conservative list; the vendors' own current lists are the authority and change.
    /// </summary>
    public static readonly IReadOnlySet<string> LikelyUnsupported =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CN", "HK", "MO", "RU", "BY", "IR", "KP", "CU", "SY" };

    /// <summary>The hosts whose own edge can say what it sees: Cloudflare-fronted ones answer /cdn-cgi/trace.</summary>
    public static readonly IReadOnlyList<(string Tool, string Host)> TraceTargets =
    [
        ("Claude Code", "api.anthropic.com"),
        ("Claude Code", "claude.ai"),
        ("Claude Code", "platform.claude.com"),
        ("Codex / ChatGPT", "auth.openai.com"),
        ("Codex / ChatGPT", "chatgpt.com"),
        ("Codex / ChatGPT", "api.openai.com"),
    ];

    /// <summary>Reads Cloudflare's trace text ("key=value" per line). Null when there is no address in it.</summary>
    public static (string Ip, string? Country, string? Colo)? ParseTrace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string? ip = null, loc = null, colo = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            switch (line[..eq])
            {
                case "ip": ip = value; break;
                case "loc": loc = value; break;
                case "colo": colo = value; break;
            }
        }
        return IPAddress.TryParse(ip, out _) ? (ip!, string.IsNullOrWhiteSpace(loc) || loc == "XX" ? null : loc, colo) : null;
    }

    /// <summary>
    /// Reads what the services' own edges saw. This is the strongest local evidence about "will they notice this is another network":
    /// it is the country and address the service sees, per site, and per address family.
    /// </summary>
    public static IReadOnlyList<Finding> AssessTraces(NetworkEnv env, IReadOnlyList<HostTrace> traces)
    {
        var list = new List<Finding>();
        var seen = traces.Where(t => t.Reached).ToList();
        if (seen.Count == 0)
        {
            list.Add(new Finding("ainet.trace.none", Severity.Info, Loc.T("没能让这些网站报出它们看到的出口"),
                Loc.T("这些网站没有回应出口查询(多半是上面已经连不上了),所以没法核对它们看到的国家和地址。")));
            return list;
        }

        string Scope = Loc.T("这只代表 TidyWin 这个程序此刻的路径。浏览器或别的程序如果被代理规则分流,看到的可能不同。");
        static string Way(HostTrace t) => t.ViaProxy ? Loc.T("按代理设置") : t.Family switch { TraceFamily.V4 => "IPv4", TraceFamily.V6 => "IPv6", _ => Loc.T("默认路径") };
        static string Short(HostTrace t) => $"{t.Host}({Way(t)}){(t.Country is null ? "" : " " + t.Country.ToUpperInvariant())}";

        // 1. A country the service is known not to serve.
        var bad = seen.Where(t => t.Country is { Length: > 0 } c && LikelyUnsupported.Contains(c)).ToList();
        if (bad.Count > 0)
            list.Add(new Finding("ainet.trace.region", Severity.Warn,
                Loc.F("{0} 看到你的出口在不受支持的地区",string.Join(Loc.T("、"), bad.Select(Short).Take(4))),
                Loc.T("这是网站自己报出来的国家(它的边缘节点看到的来源),比任何第三方查询站都准。Anthropic 和 OpenAI 目前都不向中国大陆、香港、澳门等地区提供服务,")
                + Loc.T("官方也明确:用 VPN 并不能让不受支持地区的使用变得合规。") + Scope,
                Loc.T("以两家官方当前的支持地区名单为准。要让账号长期稳定,让所有程序、IPv4 和 IPv6 都走同一个受支持地区的稳定出口。")));

        // 2. IPv4 and IPv6 leave from different countries: the same account shows up from two places.
        var split4v6 = new List<string>();
        foreach (var g in seen.Where(t => !t.ViaProxy).GroupBy(t => t.Host))
        {
            var v4 = g.FirstOrDefault(t => t.Family == TraceFamily.V4);
            var v6 = g.FirstOrDefault(t => t.Family == TraceFamily.V6);
            if (v4?.Country is { } a && v6?.Country is { } b && !a.Equals(b, StringComparison.OrdinalIgnoreCase))
                split4v6.Add($"{g.Key}:IPv4 {a.ToUpperInvariant()} / IPv6 {b.ToUpperInvariant()}");
        }
        if (split4v6.Count > 0)
            list.Add(new Finding("ainet.trace.family", Severity.Warn, Loc.T("IPv4 和 IPv6 走了不同国家的出口"),
                string.Join(";", split4v6.Take(4)) + Loc.T("。代理或 TUN 只接管了其中一种,另一种直接从本地网络出去。")
                + Loc.T("同一个账号会同时从两个国家出现,这是最容易被判成异常的情形;而且哪种先被用到取决于系统,所以有时好有时坏。") + Scope,
                Loc.T("让代理同时接管 IPv6,或者在网卡属性里关掉 IPv6(协议版本 6),让所有流量只走一个出口。")));

        // 3. Sites of the same tool see different places.
        var splitTool = new List<string>();
        var splitFamily = new List<string>();
        foreach (var g in seen.Where(t => t.Family == TraceFamily.Default).GroupBy(t => (t.Tool, t.ViaProxy)))
        {
            var countries = g.Select(t => t.Country?.ToUpperInvariant()).Where(c => c is not null).Distinct().ToList();
            if (countries.Count > 1)
                splitTool.Add($"{g.Key.Tool}:" + string.Join(Loc.T("、"), g.Where(t => t.Country is not null).Select(t => $"{t.Host} {t.Country!.ToUpperInvariant()}")));
            else if (g.Select(t => t.IsV6).Distinct().Count() > 1)
                splitFamily.Add(g.Key.Tool);
        }
        if (splitTool.Count > 0)
            list.Add(new Finding("ainet.trace.split", Severity.Warn, Loc.T("同一个工具的不同网站看到的不是同一个国家"),
                string.Join(";", splitTool.Take(3)) + Loc.T("。登录、换令牌、对话分别走了不同出口,服务端会把它们当成账号在不同国家之间来回跳。") + Scope,
                Loc.T("检查代理规则:这些域名必须走同一个节点。分流规则里漏了某个域名(比如只写了 chatgpt.com 没写 auth.openai.com),就会出现这种情况。")));
        else if (splitFamily.Count > 0)
            list.Add(new Finding("ainet.trace.mixed", Severity.Info, Loc.T("有的网站走 IPv4,有的走 IPv6"),
                Loc.F("{0} 里,各网站实际用的地址类型不一样。国家相同时问题不大,但出口可能不是同一台机器。",string.Join(Loc.T("、"), splitFamily)) + Scope));

        // 4. Through the computer's proxy settings the sites see something other than the straight-out view.
        var proxyDiff = new List<string>();
        foreach (var g in seen.GroupBy(t => t.Host))
        {
            var direct = g.FirstOrDefault(t => !t.ViaProxy && t.Family == TraceFamily.Default);
            var proxied = g.FirstOrDefault(t => t.ViaProxy);
            if (direct?.Country is { } a && proxied?.Country is { } b && !a.Equals(b, StringComparison.OrdinalIgnoreCase))
                proxyDiff.Add(Loc.F("{0}:直连 {1} / 走代理 {2}",g.Key,a.ToUpperInvariant(),b.ToUpperInvariant()));
        }
        if (proxyDiff.Count > 0)
            list.Add(new Finding("ainet.trace.proxydiff", Severity.Info, Loc.T("直连和走代理,网站看到的国家不一样"),
                string.Join(";", proxyDiff.Take(4)) + Loc.T("。哪个程序按哪条路走,取决于它认不认系统代理:浏览器通常认,命令行常常不认。"),
                Loc.T("让所有 AI 工具走同一条路:要么都走代理(环境变量或 TUN),要么都不走。")));

        if (!list.Any(f => f.Severity >= Severity.Warn))
        {
            var where = string.Join(Loc.T("、"), seen.Select(t => t.Country?.ToUpperInvariant()).Where(c => c is not null).Distinct());
            list.Add(new Finding("ainet.trace.ok", Severity.Ok,
                Loc.F("这些网站看到的出口一致{0}",(where.Length > 0 ? ":" + where : "")),
                Loc.F("{0} 次查询里,网站自己报出的国家一致,IPv4 和 IPv6 没有分到两个国家,也不在常见的不受支持地区。",seen.Count) + Scope));
        }

        return list;
    }

    /// <summary>Network names that mean a data centre or cloud host rather than a home or mobile line.</summary>
    private static readonly string[] HostingWords =
    [
        "amazon", "aws", "google llc", "google cloud", "microsoft", "azure", "digitalocean", "linode", "akamai connected", "vultr", "choopa",
        "hetzner", "ovh", "oracle", "contabo", "m247", "datacamp", "leaseweb", "scaleway", "alibaba", "tencent cloud", "cloudflare",
        "colocrossing", "psychz", "quadranet", "frantech", "buyvm", "racknerd", "dmit", "bandwagon", "hosting", "datacenter", "data center", "vps",
    ];

    public static bool LooksLikeHosting(string? org) =>
        !string.IsNullOrWhiteSpace(org) && HostingWords.Any(w => org.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] TunnelWords =
        ["wintun", "tap-windows", "clash", "mihomo", "meta", "sing-box", "v2ray", "xray", "wireguard", "openvpn", "tailscale", "zerotier", "nekoray", "tun"];

    /// <summary>Which of the up network adapters look like a VPN or a proxy tool's tunnel.</summary>
    public static bool LooksVirtual(string nameOrDescription) =>
        TunnelWords.Any(w => nameOrDescription.Contains(w, StringComparison.OrdinalIgnoreCase));

    // ───────── reading proxy addresses ─────────

    /// <summary>Host and port out of "127.0.0.1:7890", "http://user:pw@host:7890/", "socks5://[::1]:1080".</summary>
    public static (string Host, int Port)? ParseEndpoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var slash = s.IndexOfAny(['/', '?']);
        if (slash >= 0) s = s[..slash];
        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];

        string host;
        string port;
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            if (end < 0) return null;
            host = s[1..end];
            port = s[(end + 1)..].TrimStart(':');
        }
        else
        {
            var colon = s.LastIndexOf(':');
            if (colon < 0) return null;
            host = s[..colon];
            port = s[(colon + 1)..];
        }
        return host.Length > 0 && int.TryParse(port, out var p) && p is > 0 and < 65536 ? (host, p) : null;
    }

    /// <summary>Every address in a Windows proxy setting: "host:port" or "http=h:p;https=h:p;socks=h:p".</summary>
    public static IReadOnlyList<(string Host, int Port)> SystemProxyTargets(string? server)
    {
        var list = new List<(string, int)>();
        if (string.IsNullOrWhiteSpace(server)) return list;
        foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            var addr = eq >= 0 ? part[(eq + 1)..] : part;
            if (ParseEndpoint(addr) is { } t && !list.Contains(t)) list.Add(t);
        }
        return list;
    }

    public static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>"user:password@" inside a proxy address.</summary>
    public static bool HasCredentials(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return false;
        var s = proxy;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        var at = s.LastIndexOf('@');
        return at > 0 && s[..at].Contains(':');
    }

    // ───────── DNS answers that cannot be right ─────────

    /// <summary>Clash's "fake-ip" mode answers every name from 198.18.0.0/15 on purpose; it is normal there.</summary>
    public static bool IsFakeIp(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes() is [198, 18 or 19, ..];

    /// <summary>A public AI website never lives on these, so such an answer was rewritten, polluted or blocked.</summary>
    public static bool IsImpossibleForPublicSite(IPAddress ip)
    {
        if (IsFakeIp(ip)) return false;
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
    }

    // ───────── what the settings alone reveal (no network needed) ─────────

    public static IReadOnlyList<Finding> AssessConfig(NetworkEnv env, IReadOnlyList<Listener> listeners)
    {
        var list = new List<Finding>();
        bool Listening(int port) => listeners.Any(l => l.Port == port);

        // A proxy on this computer that nobody is running breaks every program that was told to use it.
        var deadSystem = env.SystemProxyOn
            ? SystemProxyTargets(env.System!.Server).Where(t => IsLoopbackHost(t.Host) && !Listening(t.Port)).ToList()
            : [];
        if (deadSystem.Count > 0)
            list.Add(new Finding("ainet.proxy.dead", Severity.Warn, Loc.T("系统代理指向的程序没在运行"),
                Loc.F("系统代理设成了 {0},但没有程序在听这个端口。",string.Join(Loc.T("、"), deadSystem.Select(t => $"{t.Host}:{t.Port}")))
                + Loc.T("所有听从系统代理的程序(浏览器、AI 客户端、更新程序)现在多半都连不上网,不只是 AI 工具。"),
                Loc.T("打开你的代理软件;如果已经不用它了,到「设置 → 网络和 Internet → 代理」把代理关掉。")));

        if (ParseEndpoint(env.EnvProxy) is { } envTarget && IsLoopbackHost(envTarget.Host) && !Listening(envTarget.Port))
            list.Add(new Finding("ainet.env.dead", Severity.Warn, Loc.T("环境变量里的代理没在运行"),
                Loc.F("环境变量 HTTP(S)_PROXY 指向 {0}:{1},但没有程序在听这个端口。",envTarget.Host,envTarget.Port)
                + Loc.T("命令行里的 git、pip、npm、Claude Code、Codex、huggingface-cli、ollama 等都会因此连不上。")
                + Loc.T("典型表现:浏览器能登录,Codex 却报 token_exchange_failed(请求 auth.openai.com 失败)。这类问题在 Codex 的官方问题库里反复出现,根源就是环境变量里残留的旧代理端口。")
                + Loc.T("改完后要把终端和相关程序完全退出再开,环境变量只对新启动的程序生效。"),
                Loc.T("打开代理软件;或者删掉这个环境变量(设置 → 系统 → 系统信息 → 高级系统设置 → 环境变量)。")));

        if (new[] { env.HttpProxy, env.HttpsProxy, env.AllProxy }.Any(HasCredentials))
            list.Add(new Finding("ainet.env.secret", Severity.Warn, Loc.T("代理的账号密码明文写在环境变量里"),
                Loc.T("环境变量能被你电脑上运行的任何程序读到,而且常常被带进日志和截图。"),
                Loc.T("尽量改用不带密码的本地代理,或把密码交给代理软件保管。")));

        // Many command-line tools ignore the Windows proxy and only read the environment variables.
        var systemLoopbackAlive = env.SystemProxyOn
                                  && SystemProxyTargets(env.System!.Server).Any(t => IsLoopbackHost(t.Host) && Listening(t.Port));
        if (systemLoopbackAlive && env.EnvProxy is null && env.VirtualAdapters.Count == 0)
            list.Add(new Finding("ainet.env.missing", Severity.Info, Loc.T("系统代理开着,但命令行里的工具不一定会用它"),
                Loc.T("浏览器和很多桌面程序会跟着系统代理走;而 git、npm、curl、用 Node 写的 AI 命令行(如 Claude Code)只认环境变量 HTTP_PROXY / HTTPS_PROXY。")
                + Loc.T("所以浏览器能上、终端里却连不上,是很常见的情况。"),
                Loc.T("要让终端也走代理:在终端里设置 HTTPS_PROXY(例如 http://127.0.0.1:你的端口),或在代理软件里打开 TUN 模式,让所有程序都走代理。")));

        if (env.VirtualAdapters.Count > 0)
            list.Add(new Finding("ainet.tun", Severity.Info, Loc.T("检测到 VPN / 代理的虚拟网卡"),
                Loc.F("{0}。这通常是 TUN 模式或 VPN:流量在系统层被接管,连不读代理设置的程序也会走代理。",string.Join(Loc.T("、"), env.VirtualAdapters.Take(4)))
                + Loc.T("副作用是:断开代理软件却没关 TUN 时,可能整台电脑断网。")));

        AssessProxyShape(env, listeners, list);
        AssessCertificates(env, list);
        AssessNoProxy(env, list);
        return list;
    }

    /// <summary>The proxy values themselves: a kind the tools do not speak, a form they cannot parse, or one kept in a tool's own file.</summary>
    private static void AssessProxyShape(NetworkEnv env, IReadOnlyList<Listener> listeners, List<Finding> list)
    {
        var all = new List<(string Where, string Name, string Value)>();
        foreach (var (name, value) in new[] { ("HTTPS_PROXY", env.HttpsProxy), ("HTTP_PROXY", env.HttpProxy), ("ALL_PROXY", env.AllProxy) })
            if (!string.IsNullOrWhiteSpace(value)) all.Add((Loc.T("环境变量"), name, value));
        foreach (var t in env.ToolProxies) all.Add((Loc.F("{0} 自己的配置({1})",t.Tool,t.Source), t.Variable, t.Value));

        var socks = all.Where(a => a.Value.TrimStart().StartsWith("socks", StringComparison.OrdinalIgnoreCase)).ToList();
        if (socks.Count > 0)
            list.Add(new Finding("ainet.socks", Severity.Warn, Loc.T("代理用的是 SOCKS,Claude Code 不支持,Codex 在 Windows 上也不稳"),
                Loc.F("{0} 是 SOCKS 代理。Claude Code 官方文档写明「不支持 SOCKS 代理」;",string.Join(Loc.T("、"), socks.Select(s => Loc.F("{0}的 {1}",s.Where,s.Name))))
                + Loc.T("Codex 的官方问题库里有 Windows 用户反馈:SOCKS5 下对话流不断中断、登录失败,改成 HTTP 代理后就恢复了。"),
                Loc.T("代理软件一般同时开着 HTTP 端口(Clash 的混合端口或 HTTP 端口,V2RayN 默认 10809)。把这些变量改成 http://127.0.0.1:<HTTP 端口>,并删掉 ALL_PROXY。")));

        var noScheme = all.Where(a => !a.Value.Contains("://") && !a.Value.TrimStart().StartsWith("socks", StringComparison.OrdinalIgnoreCase)).ToList();
        if (noScheme.Count > 0)
            list.Add(new Finding("ainet.scheme", Severity.Warn, Loc.T("代理地址缺少 http:// 开头"),
                Loc.F("{0}。Claude Code 官方文档说明:代理地址解析不了(例如少了 http://)时,它会在启动时直接报错退出。",string.Join(Loc.T("、"), noScheme.Select(s => Loc.F("{0}的 {1}={2}",s.Where,s.Name,s.Value)))),
                Loc.T("写成 http://127.0.0.1:端口 的完整形式。")));

        foreach (var t in env.ToolProxies)
        {
            if (ParseEndpoint(t.Value) is { } target && IsLoopbackHost(target.Host) && !listeners.Any(l => l.Port == target.Port))
                list.Add(new Finding("ainet.toolproxy.dead." + Slug(t.Tool), Severity.Warn, Loc.F("{0} 自己配置里的代理没在运行",t.Tool),
                    Loc.F("{0} 里设了 {1}={2}:{3},但没有程序在听这个端口。这份配置的优先级高于系统代理,所以浏览器正常、{4} 却连不上。",t.Source,t.Variable,target.Host,target.Port,t.Tool),
                    Loc.F("改成代理软件现在真正的端口,或删掉这一行让它用系统的设置。改完后完全退出 {0} 再开。",t.Tool)));
        }
    }

    /// <summary>Variables that make a program trust a different set of certificates than the system's.</summary>
    public static readonly string[] CertVariables = ["SSL_CERT_FILE", "CODEX_CA_CERTIFICATE", "NODE_EXTRA_CA_CERTS", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE"];

    private static void AssessCertificates(NetworkEnv env, List<Finding> list)
    {
        var missing = env.CertFiles.Where(c => !c.Exists).ToList();
        if (missing.Count > 0)
            list.Add(new Finding("ainet.cert.missing", Severity.Warn, Loc.T("证书环境变量指向的文件不存在"),
                Loc.F("{0}。程序会按这个路径找证书,找不到就连不上任何 HTTPS 网站,常见报错是 UnknownIssuer 或「Failed to read CA certificate file」。",string.Join(Loc.T("、"), missing.Select(c => $"{c.Variable}={c.Path}"))),
                Loc.T("到「环境变量」里删掉这些变量(很可能是以前某个项目留下的),然后重开终端和程序。")));

        var present = env.CertFiles.Where(c => c.Exists && c.Variable is "SSL_CERT_FILE" or "CODEX_CA_CERTIFICATE").ToList();
        if (present.Count > 0)
            list.Add(new Finding("ainet.cert.custom", Severity.Info, Loc.T("设置了自定义证书,会改变 Codex 等程序信任谁"),
                Loc.F("{0}。Codex 的官方问题库里有多起「浏览器和 curl 都正常,Codex 登录却失败」的案例,原因就是遗留的 SSL_CERT_FILE 让它不再信任正常的证书。",string.Join(Loc.T("、"), present.Select(c => $"{c.Variable}={c.Path}"))),
                Loc.T("如果你现在并不需要(比如公司抓包代理),就删掉它;需要时,确认这个文件是完整、最新的证书包。")));
    }

    private static void AssessNoProxy(NetworkEnv env, List<Finding> list)
    {
        if (!env.ProxyConfigured || string.IsNullOrWhiteSpace(env.NoProxy)) return;
        var tokens = env.NoProxy.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Covers(string host) => tokens.Any(t => t == "*" || host.Equals(t.TrimStart('.', '*'), StringComparison.OrdinalIgnoreCase)
                                                    || host.EndsWith("." + t.TrimStart('.', '*'), StringComparison.OrdinalIgnoreCase));
        var bypassed = Endpoints.Where(e => Covers(e.Host)).ToList();
        if (bypassed.Count > 0)
            list.Add(new Finding("ainet.noproxy", Severity.Info, Loc.T("NO_PROXY 让一些 AI 网站不走代理"),
                Loc.F("NO_PROXY={0} 覆盖了:{1}。读这个变量的程序访问它们时会直连,和走代理的其他程序出口就不一样了。",env.NoProxy,string.Join(Loc.T("、"), bypassed.Select(e => e.Host).Take(6))),
                Loc.T("如果这些网站需要代理才能访问,把它们从 NO_PROXY 里去掉。")));
    }

    private static string Slug(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // ───────── where the connection appears to come from ─────────

    public static IReadOnlyList<Finding> AssessExit(NetworkEnv env, ExitInfo? direct, ExitInfo? viaProxy, string? systemCountry)
    {
        var list = new List<Finding>();
        var main = viaProxy ?? direct;
        if (main is null)
        {
            list.Add(new Finding("ainet.exit.unknown", Severity.Info, Loc.T("没能查到出口 IP"),
                Loc.T("用来查询出口的网站没连上,所以这一项没有结果。这不代表其他网站连不上。")));
            return list;
        }

        list.Add(new Finding("ainet.exit", Severity.Info, Loc.F("当前出口:{0}({1})",main.Ip,main.Where),
            (main.Org is { Length: > 0 } ? Loc.F("所属网络:{0}。",main.Org) : "") + Loc.T("这是网站看到的你的地址,不是你电脑的内网地址。")));

        if (direct is not null && viaProxy is not null && direct.Ip != viaProxy.Ip)
        {
            var countryDiffers = !string.Equals(direct.Country, viaProxy.Country, StringComparison.OrdinalIgnoreCase);
            list.Add(new Finding("ainet.exit.split", countryDiffers ? Severity.Warn : Severity.Info, Loc.T("直连和走代理的出口不一样"),
                Loc.F("直连:{0};走代理:{1}。同一个账号,有的程序从这边出去、有的从那边出去,账号看到的登录地就会来回跳。",direct,viaProxy)
                + Loc.T("OpenAI 官方把「登录地忽然变化」「同时多处登录」列为触发安全提醒的原因。"),
                Loc.T("让这个账号用到的所有程序走同一个出口:统一用 TUN 模式,或给每个工具都设同样的代理,别让一部分直连、一部分走代理。")));
        }

        if (LooksLikeHosting(main.Org))
            list.Add(new Finding("ainet.exit.hosting", Severity.Info, Loc.T("出口属于机房或云服务商的网络"),
                Loc.F("{0}。这类出口很多人共用,经常被 Cloudflare 要求额外验证(返回 403 或人机验证页),命令行和客户端过不去。",main.Org)
                + Loc.T("OpenAI 官方的排查办法是关掉 VPN / 代理、用一个稳定的网络。这只能说明「容易被拦」,不能说明账号会不会出问题。")));

        if (!string.IsNullOrEmpty(systemCountry) && !string.IsNullOrEmpty(main.Country)
            && !string.Equals(systemCountry, main.Country, StringComparison.OrdinalIgnoreCase))
            list.Add(new Finding("ainet.exit.country", Severity.Info, Loc.F("出口在 {0},这台电脑的地区设置是 {1}",main.Country,systemCountry),
                Loc.T("出口所在地和系统地区不一致,常见于用了代理。需要知道:Anthropic 的支持地区政策看的是使用者本人所在的地区(以及公司注册地),不是出口 IP,")
                + Loc.T("官方明确写明用 VPN 不能让不受支持地区的使用变得合规;OpenAI 也会把登录地突变当作可疑活动。具体以各服务官方公布的支持地区和条款为准。")));

        return list;
    }

    // ───────── what the live checks show ─────────

    public static IReadOnlyList<Finding> AssessChecks(NetworkEnv env, IReadOnlyList<EndpointCheck> checks)
    {
        var list = new List<Finding>();
        if (checks.Count == 0) return list;

        static string Names(IEnumerable<EndpointCheck> cs) => string.Join(Loc.T("、"), cs.Select(c => c.Endpoint.Name));

        // Names that resolve to an address a public website cannot have.
        var poisoned = checks.Where(c => c.Addresses.Any(a => IPAddress.TryParse(a, out var ip) && IsImpossibleForPublicSite(ip))).ToList();
        if (poisoned.Count > 0)
            list.Add(new Finding("ainet.dns.bad", Severity.Warn, Loc.F("{0} 的域名被解析到了不可能的地址",Names(poisoned)),
                Loc.T("解析结果是本机、内网或空地址。常见原因:hosts 文件被改过、DNS 被污染或被劫持、某个软件在改写解析。"),
                Loc.T("先看 hosts 文件里有没有这些域名;再把网卡的 DNS 换成可信的(如运营商以外的公共 DNS)后重新检查。")));

        if (checks.Any(c => c.Addresses.Any(a => IPAddress.TryParse(a, out var ip) && IsFakeIp(ip))))
            list.Add(new Finding("ainet.dns.fakeip", Severity.Info, Loc.T("域名被解析成了 198.18.x.x(fake-ip)"),
                Loc.T("这是 Clash 等代理软件的 fake-ip 模式,用来让程序把域名交给代理去解析,属于正常现象。如果你没开过这类软件才需要留意。")));

        var broken = checks.Where(c => !c.Direct.Reached && !(c.ViaProxy?.Reached ?? false)).ToList();
        var needProxy = checks.Where(c => !c.Direct.Reached && (c.ViaProxy?.Reached ?? false)).ToList();
        var region = checks.Where(c => c.Direct.Kind == ProbeKind.RegionBlocked || c.ViaProxy?.Kind == ProbeKind.RegionBlocked).ToList();

        if (broken.Count > 0)
        {
            var apiDown = broken.Any(c => c.Endpoint.IsApi);
            var kinds = broken.Select(c => (c.ViaProxy ?? c.Direct).Kind).ToList();
            var why = string.Join(" ", kinds.Distinct().Select(Explain));
            var title = env.ProxyConfigured
                ? Loc.F("{0} 直连和走代理都连不上",Names(broken))
                : Loc.F("{0} 现在连不上(而且没有设置代理)",Names(broken));
            list.Add(new Finding("ainet.broken", apiDown ? Severity.Warn : Severity.Info, title, why,
                Fix(kinds, env.ProxyConfigured)));
        }

        if (needProxy.Count > 0)
        {
            // Without anything that carries the tools that ignore the system proxy, those tools will fail.
            var tooling = env.EnvProxy is null && env.VirtualAdapters.Count == 0;
            var sev = tooling ? Severity.Warn : Severity.Info;
            var viaTun = env.VirtualAdapters.Count > 0;
            list.Add(new Finding("ainet.needproxy", sev, Loc.F("{0} 只有走代理才连得上",Names(needProxy)),
                Loc.T("直接连不通,走代理可以。浏览器和跟随系统代理的程序没问题。")
                + (tooling ? Loc.T("git、npm、curl、Node 写的 AI 命令行等只认环境变量的工具,现在会连不上。") : Loc.T("你已经设置了环境变量或虚拟网卡,命令行工具多半也能走通。")),
                tooling
                    ? Loc.T("在终端里设置 HTTPS_PROXY(指向系统代理的地址),或在代理软件里开 TUN 模式。")
                    : viaTun ? Loc.T("如果某个程序仍然连不上,确认代理软件的 TUN 模式开着,并且规则里包含这些域名。") : null));
        }

        if (region.Count > 0)
            list.Add(new Finding("ainet.region", Severity.Warn, Loc.F("{0} 拒绝了当前网络出口所在的地区",Names(region)),
                Loc.T("能连上服务器,但服务器的回复说这个出口地区不在支持范围内(或很可能是这个原因)。这不是网络不通。"),
                Loc.T("以服务官方公布的支持地区为准(例如 anthropic.com/supported-countries)。Anthropic 的政策按使用者本人所在的地区判断,官方明确写明:用 VPN 并不能让不受支持地区的使用变得合规。")));

        var challenged = checks.Where(c => c.Direct.Kind == ProbeKind.Challenged || c.ViaProxy?.Kind == ProbeKind.Challenged).ToList();
        if (challenged.Count > 0)
            list.Add(new Finding("ainet.challenge", Severity.Warn, Loc.F("{0} 对当前出口要求先通过人机验证",Names(challenged)),
                Loc.T("网络是通的,但网站没有放行,而是返回了 Cloudflare 的人机验证页。浏览器能点掉它,命令行和客户端点不掉,所以登录和对话会失败。")
                + Loc.T("OpenAI 官方给的排查办法是:关掉 VPN / 代理,用一台设备、一个稳定的网络再试;很多人共用的机房出口最容易遇到。"),
                Loc.T("先暂时关掉代理看能否正常访问;必须走代理时,换一个稳定、不与很多人共用的出口,并让所有程序走同一个出口。")));

        var blocked = checks.Where(c => c.Direct.Kind == ProbeKind.Blocked || c.ViaProxy?.Kind == ProbeKind.Blocked).ToList();
        if (blocked.Count > 0)
            list.Add(new Finding("ainet.blocked", Severity.Risk, Loc.F("{0} 的服务器直接拒绝了这个出口地址",Names(blocked)),
                Loc.T("网络是通的,但网站的防护层(Cloudflare 的 1020 或 1006-1008,或\"you have been blocked\"页)明确拒绝了这个来源地址。")
                + Loc.T("这是对地址或它所在网络的封锁,不是你的账号被封;换一个出口通常就好,而不是反复重试。"),
                Loc.T("换一个不同网段的出口再试;不要在短时间内反复切换节点重试,那样更容易被当成异常。")));

        var limited = checks.Where(c => c.Direct.Kind == ProbeKind.RateLimited || c.ViaProxy?.Kind == ProbeKind.RateLimited).ToList();
        if (limited.Count > 0)
            list.Add(new Finding("ainet.ratelimit", Severity.Warn, Loc.F("{0} 对这个出口限流了(429)",Names(limited)),
                Loc.T("这个出口在短时间内发出了太多请求,很多人共用同一个出口时最常见。网站让它先停一停。"),
                Loc.T("停十来分钟再试,不要连续重试;长期这样就换一个不和很多人共用的出口。")));

        var slow = checks.Where(c => c.Effective.Reached && c.Effective.Ms >= 4000).ToList();
        if (slow.Count > 0)
            list.Add(new Finding("ainet.slow", Severity.Info, Loc.F("{0} 能连上,但很慢",Names(slow)),
                Loc.F("连接一次用了 {0:0.#} 秒以上。AI 对话会出现长时间没有第一个字、下载模型时速度很低。",slow.Max(c => c.Effective.Ms) / 1000.0),
                Loc.T("换一个更近或更稳的节点;如果不需要代理,先检查是不是有的网站走了不必要的绕路。")));

        if (broken.Count == 0 && needProxy.Count == 0 && region.Count == 0 && challenged.Count == 0 && poisoned.Count == 0
            && blocked.Count == 0 && limited.Count == 0)
            list.Add(new Finding("ainet.ok", Severity.Ok, Loc.F("{0} 个常用 AI 网站都连得上",checks.Count),
                env.ProxyConfigured ? Loc.T("直连和按系统设置连接,都能到达这些网站。") : Loc.T("没有设置代理,直连就能到达这些网站。")));

        return list;
    }

    private static string Explain(ProbeKind kind) => kind switch
    {
        ProbeKind.Timeout => Loc.T("连接超时:请求发出去了,但一直没有回应,多半是线路或节点不通,或被丢包。"),
        ProbeKind.DnsFailed => Loc.T("域名解析失败:电脑拿不到这个网站的地址,是 DNS 的问题(也可能是网络整个断了)。"),
        ProbeKind.TlsFailed => Loc.T("加密握手失败:连上了但中途被打断,常见于线路被干扰、系统时间不准,或有软件在拦截 HTTPS。"),
        ProbeKind.Refused => Loc.T("对方直接拒绝了连接:多半被主动阻断。"),
        ProbeKind.ProxyFailed => Loc.T("代理那一段出了问题:代理软件没运行、没放行这个域名,或代理的上游不通。"),
        _ => Loc.T("连接失败,没有拿到具体原因。"),
    };

    private static string Fix(IReadOnlyList<ProbeKind> kinds, bool proxyConfigured)
    {
        if (!proxyConfigured && kinds.All(k => k is ProbeKind.Timeout or ProbeKind.DnsFailed or ProbeKind.Refused))
            return Loc.T("如果你平时靠代理上这些网站,先打开代理软件再重新检查。");
        if (kinds.Contains(ProbeKind.ProxyFailed)) return Loc.T("确认代理软件在运行,规则里这些域名走代理而不是直连。");
        if (kinds.Contains(ProbeKind.DnsFailed)) return Loc.T("检查 DNS:代理软件的 DNS / fake-ip 设置,或网卡里的 DNS 服务器。");
        if (kinds.Contains(ProbeKind.TlsFailed)) return Loc.T("先核对系统时间是否准确,再看有没有杀毒或抓包软件在拦截 HTTPS。");
        return Loc.T("换一个节点或线路再试;确认这些域名的规则走代理。");
    }
}

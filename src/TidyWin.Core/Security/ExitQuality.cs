using System.Net;
using System.Text.Json;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Security;

/// <summary>
/// What a reputation service says about an address. Every flag is nullable: null means "not asked / not answered", which is
/// never read as "clean". Only the ipapi.is answer with a key carries the flags; without one the service gives ownership and
/// location only.
/// </summary>
public sealed record IpReputation(
    string Ip,
    bool? Datacenter, bool? Vpn, bool? Proxy, bool? Tor, bool? Mobile, bool? Satellite, bool? Abuser,
    string? Crawler,
    string? AbuserLabel,
    string? Company, string? Asn, string? Country, string? City, string? TimeZone)
{
    public bool HasFlags => Datacenter.HasValue || Vpn.HasValue || Proxy.HasValue || Tor.HasValue || Abuser.HasValue;
}

public enum ReputationStatus { NoKey, Ok, Failed }

public sealed record ReputationLookup(ReputationStatus Status, IpReputation? Reputation = null, string? Problem = null);

/// <summary>One remembered check of the exit address, kept on this computer only.</summary>
public sealed record ExitHistoryEntry(DateTime AtUtc, string Ip, string? Country, string? Org, int Score);

/// <summary>Everything the quality check needs, gathered by the caller so the judging below needs no network.</summary>
public sealed record ExitQualityInput(
    ExitInfo? Exit,
    ReputationLookup Reputation,
    IReadOnlyList<string> DnsAddresses,
    ExitInfo? DnsResolver,
    bool ProxyOrTunnel,
    TimeSpan LocalStandardOffset,
    bool RegionRefused,
    bool Challenged,
    bool Blocked,
    ExitHistoryEntry? Previous);

public sealed record ScoreItem(string Reason, int Points);

public sealed record ExitQualityResult(int Score, IReadOnlyList<ScoreItem> Deductions, IReadOnlyList<Finding> Findings, string Text);

/// <summary>
/// The exit-address health check: how the address that AI services see you from looks (type, VPN / proxy / Tor / abuse flags),
/// whether DNS lookups leave from the same place, and whether the computer's time zone fits. The score is TidyWin's own estimate
/// from these facts. It is not Anthropic's or OpenAI's judgment, and no service publishes such a score.
/// </summary>
public static class ExitQuality
{
    // ───────── reading the answer ─────────

    /// <summary>Reads ipapi.is' JSON, the full form (nested objects, with a key) and the short form (strings, without). Null when it has no address.</summary>
    public static IpReputation? ParseIpapiIs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (Str(r, "ip") is not { Length: > 0 } ip) return null;

            r.TryGetProperty("company", out var company);
            r.TryGetProperty("asn", out var asn);
            r.TryGetProperty("location", out var loc);
            var place = loc.ValueKind == JsonValueKind.Object ? loc : r;

            var crawler = r.TryGetProperty("is_crawler", out var c) ? c.ValueKind switch
            {
                JsonValueKind.String when !string.IsNullOrWhiteSpace(c.GetString()) => c.GetString(),
                JsonValueKind.True => "yes",
                _ => null,
            } : null;

            // The label ("High") is the network's abuse rating; the company's is more specific than the AS's.
            var label = Label(company.ValueKind == JsonValueKind.Object ? Str(company, "abuser_score") : null)
                        ?? Label(asn.ValueKind == JsonValueKind.Object ? Str(asn, "abuser_score") : null);

            var asnText = asn.ValueKind switch
            {
                JsonValueKind.Object => $"AS{(asn.TryGetProperty("asn", out var n) ? n.ToString() : "?")} {Str(asn, "org")}".Trim(),
                JsonValueKind.String => asn.GetString(),
                _ => null,
            };

            return new IpReputation(
                ip,
                Flag(r, "is_datacenter"), Flag(r, "is_vpn"), Flag(r, "is_proxy"), Flag(r, "is_tor"),
                Flag(r, "is_mobile"), Flag(r, "is_satellite"), Flag(r, "is_abuser"),
                crawler, label,
                company.ValueKind == JsonValueKind.Object ? Str(company, "name") : company.ValueKind == JsonValueKind.String ? company.GetString() : null,
                asnText,
                Str(place, "country_code") ?? Str(place, "country"),
                Str(place, "city"),
                Str(place, "timezone"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"0.1016 (High)" gives "High". Anything else gives null.</summary>
    public static string? Label(string? score)
    {
        if (string.IsNullOrWhiteSpace(score)) return null;
        var open = score.IndexOf('(');
        var close = score.IndexOf(')');
        return open >= 0 && close > open ? score[(open + 1)..close].Trim() : null;
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.ToString(), _ => null }
            : null;

    /// <summary>Test for truthiness, not for "true": the service documents that a VPN record may carry another truthy value.</summary>
    private static bool? Flag(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.String => !string.IsNullOrEmpty(v.GetString()),
            JsonValueKind.Number => v.GetDouble() != 0,
            _ => true,
        };
    }

    // ───────── time zones ─────────

    /// <summary>
    /// Standard (no daylight saving) offset of an IANA zone. The system database is tried first; with invariant globalization it
    /// cannot resolve IANA names on Windows, so a small table of common zones backs it up. Null when unknown.
    /// </summary>
    public static TimeSpan? StandardOffset(string? iana)
    {
        if (string.IsNullOrWhiteSpace(iana)) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(iana).BaseUtcOffset;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            // fall through to the table
        }
        return Table.TryGetValue(iana, out var minutes) ? TimeSpan.FromMinutes(minutes) : null;
    }

    private static readonly Dictionary<string, int> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Asia/Shanghai"] = 480, ["Asia/Chongqing"] = 480, ["Asia/Urumqi"] = 360, ["Asia/Hong_Kong"] = 480, ["Asia/Macau"] = 480, ["Asia/Taipei"] = 480,
        ["Asia/Singapore"] = 480, ["Asia/Kuala_Lumpur"] = 480, ["Asia/Manila"] = 480, ["Australia/Perth"] = 480,
        ["Asia/Tokyo"] = 540, ["Asia/Seoul"] = 540, ["Asia/Bangkok"] = 420, ["Asia/Ho_Chi_Minh"] = 420, ["Asia/Jakarta"] = 420,
        ["Asia/Kolkata"] = 330, ["Asia/Calcutta"] = 330, ["Asia/Karachi"] = 300, ["Asia/Dhaka"] = 360, ["Asia/Dubai"] = 240, ["Asia/Tehran"] = 210,
        ["Europe/Moscow"] = 180, ["Europe/Istanbul"] = 180, ["Asia/Riyadh"] = 180, ["Asia/Jerusalem"] = 120,
        ["Europe/Kyiv"] = 120, ["Europe/Kiev"] = 120, ["Europe/Athens"] = 120, ["Europe/Helsinki"] = 120, ["Africa/Cairo"] = 120, ["Africa/Johannesburg"] = 120,
        ["Europe/Paris"] = 60, ["Europe/Berlin"] = 60, ["Europe/Madrid"] = 60, ["Europe/Rome"] = 60, ["Europe/Amsterdam"] = 60, ["Europe/Brussels"] = 60,
        ["Europe/Zurich"] = 60, ["Europe/Vienna"] = 60, ["Europe/Stockholm"] = 60, ["Europe/Oslo"] = 60, ["Europe/Copenhagen"] = 60, ["Europe/Warsaw"] = 60,
        ["Europe/Prague"] = 60, ["Europe/Budapest"] = 60, ["Africa/Lagos"] = 60,
        ["Europe/London"] = 0, ["Europe/Dublin"] = 0, ["Europe/Lisbon"] = 0, ["Atlantic/Reykjavik"] = 0, ["UTC"] = 0, ["Etc/UTC"] = 0,
        ["America/Sao_Paulo"] = -180, ["America/Argentina/Buenos_Aires"] = -180,
        ["America/New_York"] = -300, ["America/Toronto"] = -300, ["America/Detroit"] = -300, ["America/Bogota"] = -300, ["America/Lima"] = -300,
        ["America/Chicago"] = -360, ["America/Mexico_City"] = -360, ["America/Denver"] = -420, ["America/Phoenix"] = -420,
        ["America/Los_Angeles"] = -480, ["America/Vancouver"] = -480, ["America/Anchorage"] = -540, ["Pacific/Honolulu"] = -600,
        ["Australia/Sydney"] = 600, ["Australia/Melbourne"] = 600, ["Australia/Brisbane"] = 600, ["Australia/Adelaide"] = 570, ["Pacific/Auckland"] = 720,
    };

    // ───────── judging ─────────

    public static ExitQualityResult Assess(ExitQualityInput x)
    {
        var findings = new List<Finding>();
        var deductions = new List<ScoreItem>();
        var rep = x.Reputation.Reputation;
        var exit = x.Exit;
        var lines = new List<string>();

        if (exit is null)
            return new ExitQualityResult(0, [], [], "");

        // — what the address is —
        lines.Add(Loc.F("出口 {0}  {1}",exit.Ip,exit.Where));
        if (!string.IsNullOrWhiteSpace(exit.Org) || rep?.Asn is not null) lines.Add(Loc.F("网络 {0}",rep?.Asn ?? exit.Org));

        var datacenter = rep?.Datacenter ?? (AiNetwork.LooksLikeHosting(exit.Org) ? true : null);
        var guessed = rep?.Datacenter is null && datacenter == true;
        lines.Add(Loc.F("类型 {0}",datacenter switch
        {
            true when guessed => Loc.T("像机房/云服务商(按网络名称推测)"),
            true => Loc.T("机房/云服务商"),
            false when rep?.Mobile == true => Loc.T("移动网络(很多人共用一个地址)"),
            false when rep?.Satellite == true => Loc.T("卫星网络"),
            false => Loc.T("不像机房(住宅或办公网络)"),
            null => Loc.T("没有判断"),
        }));
        if (datacenter == true) deductions.Add(new ScoreItem(guessed ? Loc.T("出口像机房网络(推测)") : Loc.T("出口是机房网络"), 20));

        // — the flags (only with a key) —
        switch (x.Reputation.Status)
        {
            case ReputationStatus.NoKey:
                lines.Add(Loc.T("标记 没有查询(没有填 ipapi.is 的密钥)"));
                findings.Add(new Finding("ainet.q.nokey", Severity.Info, Loc.T("没有查 VPN、代理、Tor 和滥用记录"),
                    Loc.T("这些标记需要 ipapi.is 的数据,它不给匿名请求提供。免费账号每天 1000 次,够用。没有它,上面的评分只算已知的几项,会偏乐观。"),
                    Loc.T("在 ipapi.is 注册免费账号,把密钥写进设置文件的 \"ipQualityKey\" 项(或设置环境变量 TIDYWIN_IPAPI_KEY)。密钥只保存在这台电脑上,只用来查你自己的出口。")));
                break;

            case ReputationStatus.Failed:
                lines.Add(Loc.F("标记 查询没成功:{0}",x.Reputation.Problem ?? "?"));
                findings.Add(new Finding("ainet.q.failed", Severity.Info, Loc.T("出口标记查询没有成功"),
                    Loc.F("原因:{0}。这不代表出口有问题,只是这一项没有结果。",x.Reputation.Problem ?? "?")));
                break;

            default:
                if (rep is { HasFlags: true })
                {
                    var set = new List<string>();
                    if (rep.Tor == true) { set.Add("Tor"); deductions.Add(new ScoreItem(Loc.T("出口是 Tor 出口节点"), 50)); }
                    if (rep.Vpn == true) { set.Add("VPN"); deductions.Add(new ScoreItem(Loc.T("出口被标记为 VPN"), 20)); }
                    if (rep.Proxy == true) { set.Add(Loc.T("公开代理")); deductions.Add(new ScoreItem(Loc.T("出口被标记为公开代理"), 20)); }
                    if (rep.Abuser == true) { set.Add(Loc.T("近期有滥用记录")); deductions.Add(new ScoreItem(Loc.T("出口近期有滥用记录"), 30)); }
                    if (rep.Crawler is not null) { set.Add(Loc.T("爬虫/机器人")); deductions.Add(new ScoreItem(Loc.T("出口被标记为爬虫"), 10)); }
                    if (rep.AbuserLabel is "High" or "Very High" && rep.Abuser != true)
                        deductions.Add(new ScoreItem(Loc.F("所在网络整体滥用评级为 {0}",rep.AbuserLabel), 10));

                    lines.Add(set.Count == 0
                        ? Loc.T("标记 没有被标为 VPN、代理、Tor、爬虫或滥用来源")
                        : Loc.F("标记 {0}",string.Join(Loc.T("、"), set)));
                    if (rep.AbuserLabel is not null) lines.Add(Loc.F("所在网络滥用评级 {0}",rep.AbuserLabel));

                    if (set.Count == 0)
                        findings.Add(new Finding("ainet.q.clean", Severity.Ok, Loc.T("出口没有被标为 VPN、代理、Tor 或滥用来源"),
                            Loc.T("数据来自 ipapi.is。它是第三方的判断,漏报和误报都有可能;各 AI 服务有自己的规则,这里看不到。")));
                    else
                        findings.Add(new Finding("ainet.q.flags", rep.Tor == true ? Severity.Risk : Severity.Warn,
                            Loc.F("出口被标记为:{0}",string.Join(Loc.T("、"), set)),
                            Loc.T("这类出口很多人共用,常被网站要求额外验证或限流,命令行和客户端过不去。数据来自 ipapi.is,是第三方的判断,可能有误。")
                            + Loc.T("这只能说明「容易被拦」,不能说明你的账号会不会出问题。"),
                            Loc.T("换一个不和很多人共用的稳定出口;不要在短时间内反复切换节点。")));
                }
                break;
        }

        // — evidence from the services themselves —
        if (x.RegionRefused) deductions.Add(new ScoreItem(Loc.T("服务器明确拒绝了这个出口所在的地区"), 100));
        if (x.Blocked) deductions.Add(new ScoreItem(Loc.T("服务器的防护层直接拒绝了这个地址"), 40));
        if (x.Challenged) deductions.Add(new ScoreItem(Loc.T("网站对这个出口要求人机验证"), 15));

        // — DNS —
        AssessDns(x, findings, deductions, lines);

        // — time zone —
        var ipZone = rep?.TimeZone ?? exit.TimeZone;
        if (StandardOffset(ipZone) is { } ipOffset)
        {
            var diff = x.LocalStandardOffset - ipOffset;
            lines.Add(Loc.F("时区 出口 {0},这台电脑 {1}",Fmt(ipOffset),Fmt(x.LocalStandardOffset)));
            if (Math.Abs(diff.TotalMinutes) >= 90)
            {
                deductions.Add(new ScoreItem(Loc.T("系统时区和出口所在地差得多"), 5));
                findings.Add(new Finding("ainet.q.tz", Severity.Info,
                    Loc.F("系统时区和出口所在地相差 {0:0.#} 小时",Math.Abs(diff.TotalHours)),
                    Loc.F("出口在 {0}(UTC{1}),这台电脑设的是 UTC{2}。网页能读到系统时区,出口所在地和它差得多时,有的风控会当作一个异常信号;是否真的有影响取决于各服务自己的规则,这里无法判断。",
                        ipZone ?? "?",Fmt(ipOffset),Fmt(x.LocalStandardOffset)),
                    Loc.T("如果你本人就在出口所在的地区附近,不用管;不要为了「对上」而随便改时区,时区不准会让 HTTPS 和登录出别的问题。")));
            }
        }

        // — history —
        if (x.Previous is { } prev && !string.Equals(prev.Ip, exit.Ip, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new Finding("ainet.q.changed", Severity.Info, Loc.T("出口地址和上次检查时不同"),
                Loc.F("上次({0:yyyy-MM-dd HH:mm}):{1}{2};这次:{3}。同一个账号的登录地来回变化,有的服务会提醒或要求重新验证。",
                    prev.AtUtc.ToLocalTime(),prev.Ip,prev.Country is null ? "" : $"({prev.Country})",exit.Ip)));
        }

        var score = Math.Clamp(100 - deductions.Sum(d => d.Points), 0, 100);
        var level = score switch { >= 80 => Severity.Ok, >= 55 => Severity.Info, >= 35 => Severity.Warn, _ => Severity.Risk };
        var verdict = score switch { >= 80 => Loc.T("较好"), >= 55 => Loc.T("一般"), >= 35 => Loc.T("较差"), _ => Loc.T("很差") };
        var detail = (deductions.Count == 0
                         ? Loc.T("没有发现扣分项。")
                         : Loc.T("扣分:") + string.Join(Loc.T("；"), deductions.Select(d => $"{d.Reason} −{d.Points}")) + Loc.T("。"))
                     + Loc.T("这个分是 TidyWin 按上面这几项自己估的,不是 Anthropic 或 OpenAI 的评价,也没有任何服务公布过这样的分数;")
                     + (x.Reputation.Status == ReputationStatus.Ok ? Loc.T("它只是帮你判断出口值不值得换。") : Loc.T("没有查标记,所以会偏乐观。它只是帮你判断出口值不值得换。"));
        findings.Insert(0, new Finding("ainet.q.score", level, Loc.F("出口体检估分 {0}/100({1})",score,verdict), detail));

        lines.Insert(0, Loc.F("出口体检估分 {0}/100({1})",score,verdict));
        return new ExitQualityResult(score, deductions, findings, string.Join("\n", lines));
    }

    private static void AssessDns(ExitQualityInput x, List<Finding> findings, List<ScoreItem> deductions, List<string> lines)
    {
        if (x.DnsAddresses.Count == 0)
        {
            lines.Add(Loc.T("DNS 没能测出来"));
            return;
        }

        var first = x.DnsAddresses.Select(a => IPAddress.TryParse(a, out var ip) ? ip : null).FirstOrDefault(ip => ip is not null);
        if (first is null) { lines.Add(Loc.T("DNS 没能测出来")); return; }

        if (AiNetwork.IsFakeIp(first) || AiNetwork.IsImpossibleForPublicSite(first))
        {
            lines.Add(Loc.T("DNS 已交给代理软件解析,这里测不到真正的 DNS 出口(正常)"));
            return;
        }

        var r = x.DnsResolver;
        lines.Add(Loc.F("DNS 出口 {0}",r is null ? first.ToString() : r.ToString()));

        var exitCountry = x.Exit?.Country;
        if (r?.Country is not { Length: > 0 } dnsCountry || string.IsNullOrEmpty(exitCountry)) return;

        if (string.Equals(dnsCountry, exitCountry, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new Finding("ainet.q.dns.ok", Severity.Ok, Loc.F("域名查询和网络出口在同一个国家({0})",dnsCountry),
                Loc.T("DNS 没有明显泄露你的真实所在地。")));
        }
        else if (x.ProxyOrTunnel)
        {
            deductions.Add(new ScoreItem(Loc.T("DNS 查询没有跟着出口走"), 10));
            findings.Add(new Finding("ainet.q.dns.leak", Severity.Warn, Loc.F("DNS 在 {0},网络出口在 {1}",dnsCountry,exitCountry),
                Loc.F("你的域名查询走的是 {0} 的 DNS 服务器,上网流量却从 {1} 出去。解析 AI 网站域名的 DNS 服务商能看到查询来自哪里,这就是常说的 DNS 泄露。",r.ToString(),exitCountry),
                Loc.T("在代理软件里启用远程 DNS 或 fake-ip,TUN 模式下开启 DNS 劫持,让域名也从代理出口解析。")));
        }
        else
        {
            findings.Add(new Finding("ainet.q.dns.local", Severity.Info, Loc.F("DNS 在 {0},网络出口在 {1}",dnsCountry,exitCountry),
                Loc.T("两者不一致,但没有检测到代理或虚拟网卡,多半是出口和 DNS 来自不同运营商。")));
        }
    }

    private static string Fmt(TimeSpan o) => (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm");

    // ───────── remembered checks ─────────

    public const int MaxHistory = 20;

    public static IReadOnlyList<ExitHistoryEntry> ParseHistory(string? json)
    {
        var list = new List<ExitHistoryEntry>();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= MaxHistory) break;
                if (e.ValueKind != JsonValueKind.Object) continue;
                var ip = Str(e, "ip");
                if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out _)) continue;
                if (!DateTime.TryParse(Str(e, "at"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var at)) continue;
                var score = e.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var sc) ? Math.Clamp(sc, 0, 100) : 0;
                list.Add(new ExitHistoryEntry(at.ToUniversalTime(), ip, Str(e, "country"), Str(e, "org"), score));
            }
        }
        catch (JsonException) { /* a damaged history is an empty one */ }
        return list;
    }

    /// <summary>Newest first, at most <see cref="MaxHistory"/>.</summary>
    public static IReadOnlyList<ExitHistoryEntry> AddHistory(IReadOnlyList<ExitHistoryEntry> old, ExitHistoryEntry entry) =>
        old.Prepend(entry).OrderByDescending(e => e.AtUtc).Take(MaxHistory).ToList();

    public static string WriteHistory(IReadOnlyList<ExitHistoryEntry> list)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartArray();
            foreach (var e in list)
            {
                w.WriteStartObject();
                w.WriteString("at", e.AtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                w.WriteString("ip", e.Ip);
                if (e.Country is not null) w.WriteString("country", e.Country);
                if (e.Org is not null) w.WriteString("org", e.Org);
                w.WriteNumber("score", e.Score);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}

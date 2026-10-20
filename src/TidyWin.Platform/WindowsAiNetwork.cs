using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using TidyWin.Core.Security;
using TidyWin.Core.Localization;

namespace TidyWin.Platform;

/// <summary>
/// Reads the proxy settings and tries to reach a few websites. A request carries no account, key or cookie,
/// only a plain GET, and nothing is kept from the answer except whether it came and how long it took.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsAiNetwork : IAiNetworkSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public NetworkEnv ReadEnvironment() => new(
        ReadSystemProxy(),
        Env("HTTP_PROXY"), Env("HTTPS_PROXY"), Env("ALL_PROXY"), Env("NO_PROXY"),
        ReadVirtualAdapters())
    {
        CertFiles = AiNetwork.CertVariables
            .Select(v => (Name: v, Path: Env(v)))
            .Where(x => x.Path is not null)
            .Select(x => new CertFile(x.Name, x.Path!, File.Exists(x.Path)))
            .ToList(),
        ToolProxies = ReadToolProxies(),
    };

    private static readonly string[] ProxyNames = ["HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY"];

    /// <summary>
    /// Codex keeps variables in ~/.codex/.env and Claude Code in the "env" block of ~/.claude/settings.json. Both are
    /// read only for proxy settings, and nothing else in them is looked at or kept.
    /// </summary>
    private static IReadOnlyList<ToolProxy> ReadToolProxies()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = new List<ToolProxy>();

        var codex = Path.Combine(home, ".codex", ".env");
        foreach (var line in ReadSmallText(codex)?.Split('\n') ?? [])
        {
            var s = line.Trim();
            if (s.StartsWith("export ", StringComparison.Ordinal)) s = s[7..].TrimStart();
            var eq = s.IndexOf('=');
            if (eq <= 0) continue;
            var name = s[..eq].Trim();
            if (!ProxyNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var value = s[(eq + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0) found.Add(new ToolProxy("Codex", @"~\.codex\.env", name.ToUpperInvariant(), value));
        }

        var claude = Path.Combine(home, ".claude", "settings.json");
        if (ReadSmallText(claude) is { } json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("env", out var env) && env.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var p in env.EnumerateObject())
                        if (ProxyNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                            && p.Value.GetString() is { Length: > 0 } v)
                            found.Add(new ToolProxy("Claude Code", @"~\.claude\settings.json", p.Name.ToUpperInvariant(), v));
                }
            }
            catch (System.Text.Json.JsonException) { /* a settings file we cannot read tells us nothing */ }
        }
        return found;
    }

    private static string? ReadSmallText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1024 * 1024) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public string? SystemCountry()
    {
        var sb = new System.Text.StringBuilder(85);
        if (GetUserDefaultLocaleName(sb, sb.Capacity) <= 0) return null;
        var name = sb.ToString(); // e.g. "zh-CN", "en-US"
        var dash = name.LastIndexOf('-');
        return dash > 0 && name.Length - dash - 1 == 2 ? name[(dash + 1)..].ToUpperInvariant() : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetUserDefaultLocaleName(System.Text.StringBuilder name, int capacity);

    /// <summary>The lookup site sees the address every other site already sees; it is told nothing else.</summary>
    public Task<ExitInfo?> LookupExitAsync(bool followComputerProxy, CancellationToken ct = default) =>
        LookupAsync(null, followComputerProxy, ct);

    public Task<ExitInfo?> LookupAddressAsync(string ip, CancellationToken ct = default) =>
        IPAddress.TryParse(ip, out var parsed) ? LookupAsync(parsed.ToString(), false, ct) : Task.FromResult<ExitInfo?>(null);

    private async Task<ExitInfo?> LookupAsync(string? address, bool followComputerProxy, CancellationToken ct)
    {
        // The address was parsed as an IP first, so it is safe to put in a path.
        var slash = address is null ? "" : address + "/";
        using var handler = new SocketsHttpHandler
        {
            UseProxy = followComputerProxy,
            Proxy = followComputerProxy ? HttpClient.DefaultProxy : null,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.FromSeconds(1),
        };
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TidyWin-NetworkCheck/1.0");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        // Keyless lookups are rate limited and often shared, so a second and third service are tried in turn.
        foreach (var (url, asnKey, orgKey, countryKey) in new[]
        {
            ($"https://ipinfo.io/{slash}json", (string?)null, "org", "country"),
            ($"https://ipapi.co/{slash}json/", null, "org", "country_code"),
            (address is null ? "https://api.ip.sb/geoip" : $"https://api.ip.sb/geoip/{address}", "asn", "organization", "country_code"),
        })
        {
            try
            {
                var json = await http.GetStringAsync(url, cts.Token);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var r = doc.RootElement;
                string? Get(string? n) =>
                    n is null || !r.TryGetProperty(n, out var v) ? null
                    : v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()
                    : v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.ToString() : null;
                var org = Get(orgKey);
                if (Get(asnKey) is { Length: > 0 } asn) org = $"AS{asn} {org}".Trim();
                if (Get("ip") is { Length: > 0 } ip) return new ExitInfo(ip, Get(countryKey), Get("region"), Get("city"), org, Get("timezone"));
            }
            catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException) { /* try the next service */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; /* the shared time limit is used up */ }
        }
        return null;
    }

    /// <summary>
    /// Asks ipapi.is how it sees the address the request comes from. The person's own key is used (the anonymous answer has no
    /// flags). The key goes only to ipapi.is, is never written to a log, and never appears in a message shown on screen.
    /// </summary>
    public async Task<ReputationLookup> LookupReputationAsync(bool followComputerProxy, CancellationToken ct = default)
    {
        var key = AppSettings.IpQualityKey(AppSettings.Load());
        if (string.IsNullOrEmpty(key)) return new ReputationLookup(ReputationStatus.NoKey);

        using var handler = new SocketsHttpHandler
        {
            UseProxy = followComputerProxy,
            Proxy = followComputerProxy ? HttpClient.DefaultProxy : null,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.FromSeconds(1),
        };
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 128 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TidyWin-NetworkCheck/1.0");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync($"https://api.ipapi.is/?key={Uri.EscapeDataString(key)}", cts.Token);
            switch ((int)response.StatusCode)
            {
                case 429: return new ReputationLookup(ReputationStatus.Failed, Problem: Loc.T("今天的查询次数用完了"));
                case 401 or 403: return new ReputationLookup(ReputationStatus.Failed, Problem: Loc.T("密钥被拒绝,请检查是否填对"));
                case >= 400: return new ReputationLookup(ReputationStatus.Failed, Problem: $"HTTP {(int)response.StatusCode}");
            }
            var rep = ExitQuality.ParseIpapiIs(await response.Content.ReadAsStringAsync(cts.Token));
            return rep is null
                ? new ReputationLookup(ReputationStatus.Failed, Problem: Loc.T("返回的内容看不懂"))
                : new ReputationLookup(ReputationStatus.Ok, rep);
        }
        catch (HttpRequestException) { return new ReputationLookup(ReputationStatus.Failed, Problem: Loc.T("连不上 ipapi.is")); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new ReputationLookup(ReputationStatus.Failed, Problem: Loc.T("超时")); }
    }

    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWin", "exit-history.json");

    public IReadOnlyList<ExitHistoryEntry> ReadExitHistory()
    {
        try
        {
            var info = new FileInfo(HistoryPath);
            return info.Exists && info.Length <= 64 * 1024 ? ExitQuality.ParseHistory(File.ReadAllText(HistoryPath)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Kept only on this computer, newest first and capped. Failing to write it is never an error.</summary>
    public void SaveExitHistory(IReadOnlyList<ExitHistoryEntry> history)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            var temp = HistoryPath + ".tmp";
            File.WriteAllText(temp, ExitQuality.WriteHistory(history));
            File.Move(temp, HistoryPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the history is a convenience */ }
    }

    /// <summary>The process copy can be older than what the person set since this program started, so the stored ones count too.</summary>
    private static string? Env(string name)
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            foreach (var n in new[] { name, name.ToLowerInvariant() })
            {
                try
                {
                    if (Environment.GetEnvironmentVariable(n, target) is { Length: > 0 } v) return v;
                }
                catch (Exception e) when (e is System.Security.SecurityException or IOException) { /* unreadable counts as not set */ }
            }
        }
        return null;
    }

    private static ProxySetting? ReadSystemProxy()
    {
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64)
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (k is null) return null;
            return new ProxySetting(k.GetValue("ProxyEnable") is int e && e != 0, k.GetValue("ProxyServer") as string, k.GetValue("AutoConfigURL") as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static IReadOnlyList<string> ReadVirtualAdapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => AiNetwork.LooksVirtual(n.Name) || AiNetwork.LooksVirtual(n.Description))
                .Select(n => n.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (NetworkInformationException) { return []; }
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            var ips = await Dns.GetHostAddressesAsync(host, cts.Token);
            return ips.Select(i => i.ToString()).ToList();
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException) { return []; }
    }

    public async Task<ProbeOutcome> ProbeAsync(AiEndpoint endpoint, bool followComputerProxy, CancellationToken ct = default)
    {
        // The handler is built per call and thrown away, so nothing stays pooled after the check.
        using var handler = new SocketsHttpHandler
        {
            UseProxy = followComputerProxy,
            // Without this the handler would take the Windows proxy even for the "direct" test.
            Proxy = followComputerProxy ? HttpClient.DefaultProxy : null,
            AllowAutoRedirect = false,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.FromSeconds(1),
        };
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TidyWin-NetworkCheck/1.0");

        var clock = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(endpoint.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            var ms = (int)clock.ElapsedMilliseconds;

            // A proxy that cannot reach the site answers for it with 407 / 502 / 504; that is the proxy failing, not the site.
            if (status == 407 || (followComputerProxy && status is 502 or 504))
                return new ProbeOutcome(ProbeKind.ProxyFailed, status, ms, Loc.F("代理返回了 {0}",status));

            // Cloudflare answers with a human-check page, and says so in this header, instead of the content.
            // Only real API paths count: web front doors challenge every script-made request, which says nothing about this connection.
            if (AiNetwork.ChallengeIsMeaningful(endpoint) && response.Headers.TryGetValues("cf-mitigated", out var mitigated) && mitigated.Any(m => m.Equals("challenge", StringComparison.OrdinalIgnoreCase)))
                return new ProbeOutcome(ProbeKind.Challenged, status, ms, Loc.T("要求人机验证"));

            // Only a refusal can say "your region" or "this address is blocked", so only then is the start of the body read.
            if (status is 400 or 403 or 429)
            {
                var body = status == 429 ? "" : await ReadStartAsync(response, cts.Token);
                switch (AiNetwork.ClassifyRefusal(endpoint, status, body))
                {
                    case ProbeKind.RegionBlocked: return new ProbeOutcome(ProbeKind.RegionBlocked, status, ms, Loc.T("服务器的回复提到地区不受支持"));
                    case ProbeKind.Blocked: return new ProbeOutcome(ProbeKind.Blocked, status, ms, Loc.T("网站的防护层拒绝了这个地址"));
                    case ProbeKind.RateLimited: return new ProbeOutcome(ProbeKind.RateLimited, status, ms, Loc.T("被限流"));
                }
            }
            return new ProbeOutcome(ProbeKind.Ok, status, ms);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeOutcome(ProbeKind.Timeout, null, (int)clock.ElapsedMilliseconds);
        }
        catch (HttpRequestException e)
        {
            var kind = Classify(e);
            // Refused on the way through a proxy means the proxy's own door was shut.
            if (followComputerProxy && kind == ProbeKind.Refused) kind = ProbeKind.ProxyFailed;
            return new ProbeOutcome(kind, null, (int)clock.ElapsedMilliseconds, e.InnerException?.Message ?? e.Message);
        }
    }

    public async Task<HostTrace> TraceAsync(string tool, string host, TraceFamily family, bool followComputerProxy, CancellationToken ct = default)
    {
        var none = new HostTrace(tool, host, family, followComputerProxy, null, null, null);
        // Forcing a family means connecting by hand; through a proxy the proxy decides, so only the default path is used there.
        if (followComputerProxy && family != TraceFamily.Default) return none;

        using var handler = new SocketsHttpHandler
        {
            UseProxy = followComputerProxy,
            Proxy = followComputerProxy ? HttpClient.DefaultProxy : null,
            AllowAutoRedirect = false,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.FromSeconds(1),
        };
        if (family != TraceFamily.Default)
        {
            var wanted = family == TraceFamily.V4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
            handler.ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, wanted, token);
                if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                var socket = new Socket(wanted, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TidyWin-NetworkCheck/1.0");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync($"https://{host}/cdn-cgi/trace", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode) return none;
            return AiNetwork.ParseTrace(await ReadStartAsync(response, cts.Token)) is { } t
                ? new HostTrace(tool, host, family, followComputerProxy, t.Ip, t.Country, t.Colo)
                : none;
        }
        catch (Exception e) when (e is HttpRequestException or SocketException or OperationCanceledException or IOException) { return none; }
    }

    private static async Task<string> ReadStartAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[2048];
        var read = await stream.ReadAsync(buffer, ct);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static ProbeKind Classify(HttpRequestException e) => e.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => ProbeKind.DnsFailed,
        HttpRequestError.SecureConnectionError => ProbeKind.TlsFailed,
        HttpRequestError.ProxyTunnelError => ProbeKind.ProxyFailed,
        HttpRequestError.ConnectionError => e.InnerException switch
        {
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => ProbeKind.Refused,
            SocketException { SocketErrorCode: SocketError.TimedOut } => ProbeKind.Timeout,
            _ => ProbeKind.Failed,
        },
        _ => e.InnerException is System.Security.Authentication.AuthenticationException ? ProbeKind.TlsFailed : ProbeKind.Failed,
    };
}

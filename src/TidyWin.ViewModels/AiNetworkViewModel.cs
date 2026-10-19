using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TidyWin.Core.Security;
using TidyWin.Core.Localization;

namespace TidyWin.ViewModels;

public enum NetPhase { Idle, Checking, Ready }

/// <summary>How one connection attempt ended, as a colour class: 0 reached, 1 reached but refused the region, 2 failed, 3 not tried.</summary>
public sealed class NetRow
{
    public NetRow(EndpointCheck c)
    {
        Name = c.Endpoint.Name;
        Used = $"{c.Endpoint.Tool} · {c.Endpoint.Used}";
        Direct = Describe(c.Direct);
        DirectLevel = Level(c.Direct);
        Proxy = c.ViaProxy is null ? Loc.T("没设置代理") : Describe(c.ViaProxy);
        ProxyLevel = c.ViaProxy is null ? 3 : Level(c.ViaProxy);
    }

    public string Name { get; }
    public string Used { get; }
    public string Direct { get; }
    public int DirectLevel { get; }
    public string Proxy { get; }
    public int ProxyLevel { get; }

    private static int Level(ProbeOutcome o) => o.Kind switch { ProbeKind.Ok => 0, ProbeKind.RegionBlocked or ProbeKind.Challenged or ProbeKind.RateLimited => 1, _ => 2 };

    public static string Describe(ProbeOutcome o) => o.Kind switch
    {
        ProbeKind.Ok => Loc.F("通 · {0} ms",o.Ms),
        ProbeKind.RegionBlocked => Loc.F("通,但地区被拒 · {0} ms",o.Ms),
        ProbeKind.Challenged => Loc.F("通,但要人机验证 · {0} ms",o.Ms),
        ProbeKind.Blocked => Loc.F("通,但地址被挡 · {0} ms",o.Ms),
        ProbeKind.RateLimited => Loc.F("通,但被限流 · {0} ms",o.Ms),
        ProbeKind.Timeout => Loc.T("超时"),
        ProbeKind.DnsFailed => Loc.T("域名解析失败"),
        ProbeKind.TlsFailed => Loc.T("加密握手失败"),
        ProbeKind.Refused => Loc.T("被拒绝连接"),
        ProbeKind.ProxyFailed => Loc.T("代理这一段不通"),
        _ => Loc.T("连不上"),
    };
}

/// <summary>
/// Tries the websites AI tools need, once without any proxy and once the way the computer is set up, and says in plain
/// words which tools will work. It only reads: nothing about the computer or its proxy settings is changed.
/// </summary>
public sealed partial class AiNetworkViewModel(IAiNetworkSource source) : ObservableObject
{
    private const int AtOnce = 4;

    public ObservableCollection<NetRow> Rows { get; } = [];
    public ObservableCollection<FindingViewModel> Findings { get; } = [];

    [ObservableProperty] private NetPhase _phase = NetPhase.Idle;
    [ObservableProperty] private int _done;
    [ObservableProperty] private string? _setup;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _exitText;
    [ObservableProperty] private string? _traceText;

    public int Total => AiNetwork.Endpoints.Count;
    public int Problems => Findings.Count(f => f.Severity is Severity.Warn or Severity.Risk);

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (Phase == NetPhase.Checking) return;
        Phase = NetPhase.Checking;
        Done = 0;
        Error = null;

        try
        {
            var env = await Task.Run(source.ReadEnvironment, ct);
            Setup = DescribeSetup(env);

            // Started and awaited on the calling (UI) thread, so Done can be raised straight from here.
            using var gate = new SemaphoreSlim(AtOnce);
            var checks = await Task.WhenAll(AiNetwork.Endpoints.Select(e => CheckOneAsync(e, env, gate, ct)).ToList());

            // Who the websites see: once straight out, and once the way the computer's settings would go.
            var direct = source.LookupExitAsync(followComputerProxy: false, ct);
            var viaProxy = env.ProxyConfigured ? source.LookupExitAsync(followComputerProxy: true, ct) : null;
            var exitDirect = await direct;
            var exitProxy = viaProxy is null ? null : await viaProxy;
            ExitText = DescribeExit(env, exitDirect, exitProxy);

            // What the services' own edges say they see: per site, and per address family, because that is where split routing shows.
            var traces = await TraceAllAsync(env, gate, ct);
            TraceText = DescribeTraces(traces);

            Rows.Clear();
            foreach (var c in checks) Rows.Add(new NetRow(c));

            Findings.Clear();
            var found = AiNetwork.AssessChecks(env, checks)
                .Concat(AiNetwork.AssessExit(env, exitDirect, exitProxy, source.SystemCountry()))
                .Concat(AiNetwork.AssessTraces(env, traces));
            foreach (var f in SystemSecurityAdvisor.Sort(found)) Findings.Add(new FindingViewModel(f));
            OnPropertyChanged(nameof(Problems));
            Phase = NetPhase.Ready;
        }
        catch (OperationCanceledException)
        {
            Phase = Rows.Count > 0 ? NetPhase.Ready : NetPhase.Idle;
        }
        catch (Exception e)
        {
            Error = e.Message;
            Phase = NetPhase.Idle;
        }
    }

    private async Task<EndpointCheck> CheckOneAsync(AiEndpoint e, NetworkEnv env, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            // The direct try and the name lookup run side by side; the proxied try follows.
            var direct = source.ProbeAsync(e, followComputerProxy: false, ct);
            var addresses = source.ResolveAsync(e.Host, ct);
            ProbeOutcome? viaProxy = env.ProxyConfigured ? await source.ProbeAsync(e, followComputerProxy: true, ct) : null;
            return new EndpointCheck(e, await direct, viaProxy, await addresses);
        }
        finally
        {
            gate.Release();
            Done++;
        }
    }

    private async Task<IReadOnlyList<HostTrace>> TraceAllAsync(NetworkEnv env, SemaphoreSlim gate, CancellationToken ct)
    {
        async Task<HostTrace> One(string tool, string host, TraceFamily family, bool viaProxy)
        {
            await gate.WaitAsync(ct);
            try { return await source.TraceAsync(tool, host, family, viaProxy, ct); }
            finally { gate.Release(); }
        }

        var jobs = new List<Task<HostTrace>>();
        foreach (var (tool, host) in AiNetwork.TraceTargets)
        {
            foreach (var family in new[] { TraceFamily.Default, TraceFamily.V4, TraceFamily.V6 })
                jobs.Add(One(tool, host, family, false));
            if (env.ProxyConfigured) jobs.Add(One(tool, host, TraceFamily.Default, true));
        }
        return await Task.WhenAll(jobs);
    }

    /// <summary>One line per site: what it sees by default, over IPv4 and over IPv6.</summary>
    public static string DescribeTraces(IReadOnlyList<HostTrace> traces)
    {
        static string Show(HostTrace? t) => t is { Reached: true } ? $"{t.Country ?? "??"} {t.Ip}" : "—";
        var lines = new List<string>();
        foreach (var g in traces.Where(t => !t.ViaProxy).GroupBy(t => t.Host))
        {
            var d = g.FirstOrDefault(t => t.Family == TraceFamily.Default);
            var v4 = g.FirstOrDefault(t => t.Family == TraceFamily.V4);
            var v6 = g.FirstOrDefault(t => t.Family == TraceFamily.V6);
            if (d is not { Reached: true } && v4 is not { Reached: true } && v6 is not { Reached: true }) { lines.Add(Loc.F("{0}\n   没有回应",g.Key)); continue; }
            lines.Add(Loc.F("{0}\n   实际走 {1}   |   IPv4 {2}   |   IPv6 {3}",g.Key,Show(d),Show(v4),Show(v6)));
        }
        foreach (var t in traces.Where(t => t.ViaProxy && t.Reached))
            lines.Add(Loc.F("{0}(按电脑的代理设置)\n   {1}",t.Host,Show(t)));
        return lines.Count == 0 ? "" : Loc.T("各网站自己看到的你:\n") + string.Join("\n", lines);
    }

    public static string DescribeExit(NetworkEnv env, ExitInfo? direct, ExitInfo? viaProxy)
    {
        string Show(ExitInfo? e) => e is null ? Loc.T("没查到") : e.ToString();
        return env.ProxyConfigured
            ? Loc.F("出口 · 直连:{0}\n出口 · 按电脑的代理设置:{1}",Show(direct),Show(viaProxy))
            : Loc.F("出口:{0}",Show(direct));
    }

    /// <summary>One line on how this computer is set up, so the results below can be read against it.</summary>
    public static string DescribeSetup(NetworkEnv env)
    {
        var parts = new List<string>();
        if (env.SystemProxyOn) parts.Add(Loc.F("系统代理 {0}",env.System!.Server));
        if (env.PacOn) parts.Add(Loc.T("自动代理脚本"));
        if (env.EnvProxy is not null) parts.Add(Loc.T("环境变量里设了代理"));
        if (env.VirtualAdapters.Count > 0) parts.Add(Loc.F("虚拟网卡 {0}",env.VirtualAdapters[0]));
        if (parts.Count == 0) return Loc.T("没有设置任何代理,只测直连。");
        return Loc.T("当前设置:") + string.Join(" · ", parts)
               + (env.ProxyConfigured ? Loc.T("。每个网站会测两次:直连,和按这些设置连。") : Loc.T("。没有代理设置,每个网站测一次;虚拟网卡会在系统层接管流量。"));
    }
}

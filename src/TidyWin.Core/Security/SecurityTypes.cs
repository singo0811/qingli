using System.Net;

namespace TidyWin.Core.Security;

public enum Severity { Ok, Info, Warn, Risk }

/// <summary>One thing the security check found. <see cref="Advice"/> says what the person can do; this app never changes settings itself.</summary>
public sealed record Finding(string Id, Severity Severity, string Title, string Detail, string? Advice = null);

public enum BindScope
{
    /// <summary>Only this computer can connect.</summary>
    Loopback,
    /// <summary>Listening on every network card (0.0.0.0 / ::).</summary>
    AnyAddress,
    /// <summary>Listening on one specific non-loopback address, e.g. the LAN address.</summary>
    Specific,
}

/// <summary>A program waiting for incoming TCP connections.</summary>
public sealed record Listener(string Address, int Port, int Pid, string ProcessName, string? ImagePath)
{
    public BindScope Scope => ScopeOf(Address);

    public static BindScope ScopeOf(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return BindScope.Specific;
        if (IPAddress.IsLoopback(ip)) return BindScope.Loopback;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return BindScope.AnyAddress;
        return BindScope.Specific;
    }

    public bool ReachableFromOutside => Scope != BindScope.Loopback;
}

public sealed record DefenderStatus(
    bool? RealTimeProtection,
    int? SignatureAgeDays,
    bool? AntivirusEnabled,
    string? RunningMode,
    bool? TamperProtected);

public sealed record ProxySetting(bool Enabled, string? Server, string? PacUrl);

/// <summary>Everything read from the machine. A null means "could not be read", which is never treated as "fine".</summary>
public sealed record SecuritySnapshot(
    DefenderStatus? Defender,
    bool? FirewallDomain,
    bool? FirewallPrivate,
    bool? FirewallPublic,
    bool? UacEnabled,
    string? HostsText,
    ProxySetting? Proxy);

public interface ISecuritySource
{
    SecuritySnapshot ReadSnapshot();
    IReadOnlyList<Listener> ReadListeners();
}

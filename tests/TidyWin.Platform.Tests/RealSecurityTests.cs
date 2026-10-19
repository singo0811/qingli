using System.Net;
using System.Net.Sockets;
using TidyWin.Core.Security;
using TidyWin.Platform;
using Xunit;

namespace TidyWin.Platform.Tests;

/// <summary>These talk to the real operating system, so they check the parts that unit tests with fakes cannot: struct offsets and byte order.</summary>
public class RealSecurityTests
{
    private static int PortOf(TcpListener l) => ((IPEndPoint)l.LocalEndpoint).Port;

    [Fact]
    public void Real_listeners_are_read_with_the_right_port_address_and_owner()
    {
        using var any = new TcpListener(IPAddress.Any, 0);
        using var loop = new TcpListener(IPAddress.Loopback, 0);
        using var any6 = new TcpListener(IPAddress.IPv6Any, 0);
        any.Start(); loop.Start(); any6.Start();

        var all = new WindowsSecuritySource().ReadListeners();
        var me = Environment.ProcessId;

        var a = Assert.Single(all, l => l.Port == PortOf(any) && l.Pid == me);
        Assert.Equal("0.0.0.0", a.Address);
        Assert.Equal(BindScope.AnyAddress, a.Scope);

        var b = Assert.Single(all, l => l.Port == PortOf(loop) && l.Pid == me);
        Assert.Equal("127.0.0.1", b.Address);
        Assert.Equal(BindScope.Loopback, b.Scope);
        Assert.False(b.ReachableFromOutside);

        var c = Assert.Single(all, l => l.Port == PortOf(any6) && l.Pid == me);
        Assert.Equal("::", c.Address);
        Assert.Equal(BindScope.AnyAddress, c.Scope);
    }

    [Fact]
    public void The_owner_of_a_listener_is_named_and_has_a_path()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();

        var mine = new WindowsSecuritySource().ReadListeners().Single(x => x.Port == PortOf(l));
        Assert.False(string.IsNullOrWhiteSpace(mine.ProcessName));
        Assert.NotNull(mine.ImagePath);
        Assert.EndsWith(".exe", mine.ImagePath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_listener_that_stops_is_gone_from_the_next_reading()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = PortOf(l);
        var src = new WindowsSecuritySource();
        Assert.Contains(src.ReadListeners(), x => x.Port == port);

        l.Stop();
        Assert.DoesNotContain(src.ReadListeners(), x => x.Port == port && x.Pid == Environment.ProcessId);
    }

    [Fact]
    public void Reading_listeners_is_quick_enough_to_run_whenever_the_page_opens()
    {
        var src = new WindowsSecuritySource();
        src.ReadListeners(); // warm up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) src.ReadListeners();
        Assert.True(sw.ElapsedMilliseconds / 5 < 1500, $"avg {sw.ElapsedMilliseconds / 5} ms");
    }

    [Fact]
    public void The_snapshot_reads_the_things_every_windows_machine_has()
    {
        var s = new WindowsSecuritySource().ReadSnapshot();

        Assert.NotNull(s.HostsText);          // the hosts file always exists (it may be only comments)
        Assert.NotNull(s.FirewallPublic);     // these registry values exist on every installation
        Assert.NotNull(s.UacEnabled);
        Assert.NotNull(s.Proxy);

        // Whatever the machine's state, assessing it must not throw and must say something about every area.
        var findings = SystemSecurityAdvisor.Assess(s);
        Assert.Contains(findings, f => f.Id.StartsWith("firewall."));
        Assert.Contains(findings, f => f.Id.StartsWith("hosts."));
        Assert.Contains(findings, f => f.Id.StartsWith("defender."));
    }
}

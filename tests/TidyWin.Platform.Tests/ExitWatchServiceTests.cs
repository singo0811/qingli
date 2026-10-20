using TidyWin.Core.Security;
using TidyWin.Platform;

namespace TidyWin.Platform.Tests;

[CollectionDefinition("exit-watch", DisableParallelization = true)]
public sealed class ExitWatchCollection;

[Collection("exit-watch")]
public sealed class ExitWatchServiceTests
{
    [Fact]
    public async Task A_drop_alerts_once_and_a_Chinese_exit_alerts_after_it_recovers()
    {
        var up = false;
        var fake = new FakeExit();
        using var watch = new ExitWatchService(fake, () => up);
        var alerts = new List<ExitAlert>();
        watch.Alerted += (alert, _, _) => alerts.Add(alert);
        watch.Start();

        await watch.CheckNowAsync();
        Assert.Equal(ExitAlert.Offline, Assert.Single(alerts));
        await watch.CheckNowAsync();
        Assert.Single(alerts);

        up = true;
        fake.Exit = new ExitInfo("203.0.113.8", "JP", null, null, "Example");
        await watch.CheckNowAsync();
        Assert.Single(alerts);
        Assert.Equal(ExitWatchState.Clear, watch.State);

        fake.Exit = new ExitInfo("1.2.3.4", "CN", null, null, "Local");
        await watch.CheckNowAsync();
        Assert.Equal(ExitAlert.China, alerts[^1]);
        Assert.Equal(2, alerts.Count);
        await watch.CheckNowAsync();
        Assert.Equal(2, alerts.Count);
    }

    [Fact]
    public async Task A_dead_proxy_whose_bare_exit_is_Chinese_alerts_and_a_missed_lookup_does_not()
    {
        var fake = new FakeExit { Proxy = true, Direct = new ExitInfo("1.2.3.4", "CN", null, null, "Local") };
        using var watch = new ExitWatchService(fake, () => true);
        ExitAlert? alert = null;
        watch.Alerted += (a, ip, _) => alert = a;
        watch.Start();

        await watch.CheckNowAsync();
        Assert.Equal(ExitAlert.China, alert);
        Assert.Equal("1.2.3.4", watch.LastIp);

        alert = null;
        fake.Direct = null;
        fake.Fail = true;
        await watch.CheckNowAsync();
        Assert.Null(alert);
        Assert.Equal(ExitWatchState.China, watch.State);
    }

    private sealed class FakeExit : IAiNetworkSource
    {
        public bool Proxy { get; set; }
        public ExitInfo? Exit { get; set; }
        public ExitInfo? Direct { get; set; }
        public bool Fail { get; set; }

        public NetworkEnv ReadEnvironment() => new(
            new ProxySetting(Proxy, Proxy ? "127.0.0.1:7890" : null, null),
            null, null, null, null, []);

        public Task<ExitInfo?> LookupExitAsync(bool followComputerProxy, CancellationToken ct = default)
        {
            if (Fail) throw new HttpRequestException("down");
            return Task.FromResult(followComputerProxy ? Exit : Direct ?? Exit);
        }

        public Task<ProbeOutcome> ProbeAsync(AiEndpoint endpoint, bool followComputerProxy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ResolveAsync(string host, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public string? SystemCountry() => null;

        public Task<HostTrace> TraceAsync(string tool, string host, TraceFamily family, bool followComputerProxy, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

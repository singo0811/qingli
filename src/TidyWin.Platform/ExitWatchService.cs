using System.Net.NetworkInformation;
using TidyWin.Core.Security;

namespace TidyWin.Platform;

/// <summary>
/// While the program is open and the switch is on: the moment the network changes, and again every
/// <see cref="ExitWatch.BackupInterval"/>, look at whether the network is up and which country the exit is in.
/// A drop, or an exit that has become Chinese, raises <see cref="Alerted"/>. Lookups are the same public exit
/// query the network check uses. Nothing is sent when the switch is off.
/// </summary>
public sealed class ExitWatchService : IDisposable
{
    private readonly IAiNetworkSource _source;
    private readonly Func<bool> _networkUp;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Timer? _debounce;
    private int _running;
    private bool _busy;
    private bool _again;
    private bool _disposed;

    public ExitWatchService(IAiNetworkSource source, Func<bool>? networkUp = null)
    {
        _source = source;
        _networkUp = networkUp ?? NetworkInterface.GetIsNetworkAvailable;
        Running = this;
    }

    /// <summary>The one watcher the window started. The security page reads it to show the switch's state.</summary>
    public static ExitWatchService? Running { get; private set; }

    public bool Enabled => Volatile.Read(ref _running) == 1;

    public ExitWatchState State { get; private set; } = ExitWatchState.Unknown;

    public string? LastIp { get; private set; }

    public string? LastCountry { get; private set; }

    public event Action? Updated;

    public event Action<ExitAlert, string?, string?>? Alerted;

    public void Start()
    {
        if (_disposed || Interlocked.Exchange(ref _running, 1) == 1) return;
        _stop?.Dispose();
        _stop = new CancellationTokenSource();
        NetworkChange.NetworkAddressChanged += OnNetwork;
        NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        _debounce = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        _ = PollAsync(_stop.Token);
        Updated?.Invoke();
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0) return;
        NetworkChange.NetworkAddressChanged -= OnNetwork;
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        try { _stop?.Cancel(); } catch (ObjectDisposedException) { }
        try { _debounce?.Dispose(); } catch (ObjectDisposedException) { }
        _debounce = null;
        Updated?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _stop?.Dispose();
        if (Running == this) Running = null;
    }

    private void OnNetwork(object? sender, EventArgs e)
    {
        if (!Enabled) return;
        // Windows reports a burst of changes, and a VPN reconnect can look offline for a moment.
        // One check, a moment later, sees the settled state.
        try { _debounce?.Change(TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(ExitWatch.BackupInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await CheckAsync();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One reading now. The timer and a network change call this too.</summary>
    public Task CheckNowAsync() => CheckAsync();

    private async Task CheckAsync()
    {
        if (!Enabled) return;
        lock (_gate)
        {
            if (_busy) { _again = true; return; }
            _busy = true;
        }
        try
        {
            do
            {
                _again = false;
                var stop = _stop;
                if (stop is null || stop.IsCancellationRequested || !Enabled) return;

                ExitWatchState reading;
                string? ip = null;
                string? country = null;
                if (!_networkUp())
                {
                    reading = ExitWatchState.Offline;
                }
                else
                {
                    var (seenIp, seenCountry) = await LookAsync(stop.Token);
                    ip = seenIp;
                    country = seenCountry;
                    reading = ExitWatch.Read(true, country);
                }

                ExitWatchStep step;
                lock (_gate)
                {
                    step = ExitWatch.Step(State, reading);
                    State = step.State;
                    if (reading == ExitWatchState.Offline)
                    {
                        LastIp = null;
                        LastCountry = null;
                    }
                    else if (reading != ExitWatchState.Unknown)
                    {
                        LastIp = ip;
                        LastCountry = country;
                    }
                }
                Updated?.Invoke();
                if (step.Alert != ExitAlert.None)
                    Alerted?.Invoke(step.Alert, LastIp, LastCountry);
            }
            while (_again && Enabled);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // One bad reading changes nothing. The next network change, or the backup check, tries again.
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    private async Task<(string? Ip, string? Country)> LookAsync(CancellationToken stop)
    {
        var env = _source.ReadEnvironment();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cts.CancelAfter(TimeSpan.FromSeconds(4));

        ExitInfo? via = null;
        ExitInfo? direct = null;
        if (env.ProxyConfigured)
            via = await OneAsync(true, cts.Token, stop);
        if (!env.ProxyConfigured || via is null)
            direct = await OneAsync(false, cts.Token, stop);

        var country = ExitWatch.CountrySeen(env.ProxyConfigured, via?.Country, direct?.Country);
        var chosen = via is not null && !string.IsNullOrWhiteSpace(via.Country) ? via : direct;
        return (chosen?.Ip, country);
    }

    private async Task<ExitInfo?> OneAsync(bool followProxy, CancellationToken limit, CancellationToken stop)
    {
        try
        {
            return await _source.LookupExitAsync(followProxy, limit);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return null; // the short time limit ran out; that is not "the network is down"
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return null;
        }
    }
}

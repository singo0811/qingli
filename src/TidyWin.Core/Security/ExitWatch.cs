using TidyWin.Core.Localization;

namespace TidyWin.Core.Security;

/// <summary>What the exit watch last concluded. <see cref="Unknown"/> means this reading could not be judged.</summary>
public enum ExitWatchState { Unknown, Clear, Offline, China }

public enum ExitAlert { None, Offline, China }

public readonly record struct ExitWatchStep(ExitWatchState State, ExitAlert Alert);

/// <summary>
/// Decides when to tell the person to close Claude: the network dropped, or the exit address became a Chinese one.
/// A change from one foreign exit to another is not an alert. The same bad state does not alert twice.
/// </summary>
public static class ExitWatch
{
    public static readonly TimeSpan BackupInterval = TimeSpan.FromSeconds(90);

    /// <summary>Mainland China as a lookup site writes it. Hong Kong, Macau and Taiwan are different codes and are not this alert.</summary>
    public static bool IsChina(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return false;
        var c = country.Trim();
        return c.Equals("CN", StringComparison.OrdinalIgnoreCase)
               || c.Equals("CHN", StringComparison.OrdinalIgnoreCase)
               || c.Equals("China", StringComparison.OrdinalIgnoreCase)
               || c is "中国" or "中國";
    }

    /// <summary>
    /// The country to judge. When a proxy is set, programs follow it, so that answer wins.
    /// When that path does not answer, the bare exit is used: a dead tunnel whose bare exit is Chinese is the drop being watched for.
    /// Null means this reading could not be judged.
    /// </summary>
    public static string? CountrySeen(bool proxyOn, string? viaProxy, string? direct)
    {
        if (proxyOn)
            return Clean(viaProxy) ?? Clean(direct);
        return Clean(direct) ?? Clean(viaProxy);
    }

    private static string? Clean(string? country) => string.IsNullOrWhiteSpace(country) ? null : country.Trim();

    /// <summary>A missing country is unknown, never "fine" and never "China". No network wins over any country.</summary>
    public static ExitWatchState Read(bool networkUp, string? country)
    {
        if (!networkUp) return ExitWatchState.Offline;
        if (string.IsNullOrWhiteSpace(country)) return ExitWatchState.Unknown;
        return IsChina(country) ? ExitWatchState.China : ExitWatchState.Clear;
    }

    /// <summary>
    /// Alert when the exit becomes unreachable or Chinese, including the first reading if it is already bad.
    /// Staying in the same bad state does not alert again. A reading that could not be judged leaves the previous state as it was.
    /// </summary>
    public static ExitWatchStep Step(ExitWatchState previous, ExitWatchState reading)
    {
        if (reading == ExitWatchState.Unknown)
            return new ExitWatchStep(previous, ExitAlert.None);
        if (reading == previous)
            return new ExitWatchStep(reading, ExitAlert.None);
        var alert = reading switch
        {
            ExitWatchState.Offline => ExitAlert.Offline,
            ExitWatchState.China => ExitAlert.China,
            _ => ExitAlert.None,
        };
        return new ExitWatchStep(reading, alert);
    }

    public static string Where(string? ip, string? country)
    {
        var place = Clean(country)?.ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(ip) && place is not null) return ip.Trim() + " · " + place;
        return !string.IsNullOrWhiteSpace(ip) ? ip.Trim() : place ?? "?";
    }

    public static string Status(ExitWatchState state, string? ip, string? country) => state switch
    {
        ExitWatchState.Offline => Loc.T("网络断了,先关掉 Claude"),
        ExitWatchState.China => Loc.F("出口在中国({0}),先关掉 Claude", Where(ip, country)),
        ExitWatchState.Clear => Loc.F("守望中,出口 {0}", Where(ip, country)),
        _ => Loc.T("守望中,出口还没查到"),
    };
}

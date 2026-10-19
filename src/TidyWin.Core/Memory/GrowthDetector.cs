namespace TidyWin.Core.Memory;

public sealed record GrowthAlert(string Program, double MegabytesPerMinute, long GrowthBytes, double Minutes);

/// <summary>
/// Spots programs whose private memory keeps climbing: a straight upward trend over several minutes,
/// not a single spike. Needs at least <see cref="MinSamples"/> readings spanning <see cref="MinSpan"/>.
/// </summary>
public sealed class GrowthDetector
{
    public int MinSamples { get; init; } = 8;
    public TimeSpan MinSpan { get; init; } = TimeSpan.FromMinutes(4);
    public TimeSpan KeepFor { get; init; } = TimeSpan.FromMinutes(15);
    public double MinMegabytesPerMinute { get; init; } = 15;
    public long MinGrowthBytes { get; init; } = 80L * 1024 * 1024;
    public double MinRisingShare { get; init; } = 0.8;
    public double MinHalfShare { get; init; } = 0.25;

    private readonly Dictionary<string, List<(DateTime At, long Bytes)>> _series = new(StringComparer.OrdinalIgnoreCase);

    public void Add(DateTime at, IEnumerable<ProcessGroup> groups)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            seen.Add(g.Name);
            if (!_series.TryGetValue(g.Name, out var list)) _series[g.Name] = list = [];

            // A group that lost processes shrank on purpose (a restart, a closed tab): start over.
            if (list.Count > 0 && g.PrivateBytes < list[^1].Bytes * 0.7) list.Clear();

            list.Add((at, g.PrivateBytes));
            list.RemoveAll(s => at - s.At > KeepFor);
        }
        foreach (var gone in _series.Keys.Where(k => !seen.Contains(k)).ToList()) _series.Remove(gone);
    }

    public IReadOnlyList<GrowthAlert> Alerts()
    {
        var result = new List<GrowthAlert>();
        foreach (var (name, s) in _series)
        {
            if (s.Count < MinSamples) continue;
            var span = s[^1].At - s[0].At;
            if (span < MinSpan) continue;

            var minutes = span.TotalMinutes;
            var growth = s[^1].Bytes - s[0].Bytes;
            if (growth < MinGrowthBytes) continue;

            var slope = Slope(s);                        // bytes per minute
            var mbPerMin = slope / (1024.0 * 1024.0);
            if (mbPerMin < MinMegabytesPerMinute) continue;

            // A leak keeps climbing: both halves of the window must contribute. One big step followed by a flat line fails this.
            var mid = s.Count / 2;
            var firstHalf = s[mid].Bytes - s[0].Bytes;
            var secondHalf = s[^1].Bytes - s[mid].Bytes;
            if (firstHalf < growth * MinHalfShare || secondHalf < growth * MinHalfShare) continue;

            var steps = 0; var rising = 0;
            for (var i = 1; i < s.Count; i++) { steps++; if (s[i].Bytes >= s[i - 1].Bytes) rising++; }
            if ((double)rising / steps < MinRisingShare) continue;

            result.Add(new GrowthAlert(name, mbPerMin, growth, minutes));
        }
        return result.OrderByDescending(a => a.GrowthBytes).ToList();
    }

    private static double Slope(List<(DateTime At, long Bytes)> s)
    {
        var t0 = s[0].At;
        var xs = s.Select(p => (p.At - t0).TotalMinutes).ToArray();
        var ys = s.Select(p => (double)p.Bytes).ToArray();
        var mx = xs.Average(); var my = ys.Average();
        var num = 0.0; var den = 0.0;
        for (var i = 0; i < xs.Length; i++) { num += (xs[i] - mx) * (ys[i] - my); den += (xs[i] - mx) * (xs[i] - mx); }
        return den == 0 ? 0 : num / den;
    }
}

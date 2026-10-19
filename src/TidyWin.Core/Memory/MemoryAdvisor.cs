using TidyWin.Core.Paths;

namespace TidyWin.Core.Memory;

/// <summary>Decides which processes the app may ever touch. Anything it says is protected is never trimmed or closed.</summary>
public sealed class ProtectionPolicy(string windowsDirectory, int selfPid)
{
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "memcompression",
        "explorer", "dwm", "csrss", "smss", "wininit", "winlogon", "services", "lsass", "svchost",
        "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray",
        "searchhost", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "lockapp",
    };

    private readonly string? _windowsDir = WindowsPath.Normalize(windowsDirectory);

    public bool IsProtected(ProcInfo p)
    {
        if (p.Pid == selfPid || p.Pid <= 4) return true;
        if (p.SessionId == 0) return true;                       // service session
        if (ProtectedNames.Contains(StripExe(p.Name))) return true;
        if (_windowsDir is not null && p.ImagePath is not null && WindowsPath.IsSameOrUnder(p.ImagePath, _windowsDir)) return true;
        return false;
    }

    public static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}

public sealed record MemoryOptions(
    long MinTrimBytes = 100L * 1024 * 1024,
    double IdleCpuPercent = 1.0,
    TimeSpan? RecentWindow = null,
    int Cores = 1)
{
    public TimeSpan Recent => RecentWindow ?? TimeSpan.FromMinutes(3);
}

/// <summary>All processes of one program, e.g. forty "chrome" processes shown as one line.</summary>
public sealed record ProcessGroup(
    string Name,
    IReadOnlyList<int> Pids,
    long WorkingSetBytes,
    long PrivateBytes,
    double? CpuPercent,
    bool HasWindow,
    string? WindowTitle,
    bool IsForeground,
    bool IsRecentlyUsed,
    bool IsProtected)
{
    public int Count => Pids.Count;
    public bool CanClose => !IsProtected && HasWindow;
}

public sealed record MemoryAnalysis(
    MemoryPressure Pressure,
    IReadOnlyList<ProcessGroup> Groups,
    IReadOnlyList<ProcessGroup> TrimCandidates)
{
    public long TrimCandidateBytes => TrimCandidates.Sum(g => g.WorkingSetBytes);
}

/// <summary>Remembers which programs the user was in front of recently, so they are never trimmed behind their back.</summary>
public sealed class ActivityTracker
{
    private readonly Dictionary<string, DateTime> _lastForeground = new(StringComparer.OrdinalIgnoreCase);

    public void Observe(DateTime now, string? foregroundProgram)
    {
        if (!string.IsNullOrEmpty(foregroundProgram)) _lastForeground[foregroundProgram] = now;
    }

    public bool IsRecent(string program, DateTime now, TimeSpan window) =>
        _lastForeground.TryGetValue(program, out var t) && now - t <= window;
}

public static class MemoryAdvisor
{
    public static MemoryPressure PressureOf(SystemMemory m)
    {
        var p = m.AvailablePercent switch
        {
            >= 40 => MemoryPressure.Calm,
            >= 20 => MemoryPressure.Moderate,
            >= 10 => MemoryPressure.High,
            _ => MemoryPressure.Critical,
        };
        // Running out of commit hurts even when RAM looks free: allocations start to fail.
        if (m.CommitPercent >= 95 && p < MemoryPressure.Critical) p = MemoryPressure.Critical;
        else if (m.CommitPercent >= 90 && p < MemoryPressure.High) p = MemoryPressure.High;
        return p;
    }

    public static MemoryAnalysis Analyze(
        MemorySnapshot current,
        MemorySnapshot? previous,
        ProtectionPolicy policy,
        MemoryOptions options,
        ActivityTracker activity)
    {
        var cpu = CpuPercentByPid(current, previous, options.Cores);

        var groups = current.Processes
            .GroupBy(p => ProtectionPolicy.StripExe(p.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g => BuildGroup(g.Key, g.ToList(), cpu, current, policy, options, activity))
            .OrderByDescending(g => g.WorkingSetBytes) // the same number the list shows
            .ToList();

        var candidates = groups.Where(g =>
                !g.IsProtected
                && !g.IsForeground
                && !g.IsRecentlyUsed
                && g.CpuPercent is { } c && c < options.IdleCpuPercent   // unknown CPU counts as "maybe busy"
                && g.WorkingSetBytes >= options.MinTrimBytes)
            .OrderByDescending(g => g.WorkingSetBytes)
            .ToList();

        return new MemoryAnalysis(PressureOf(current.System), groups, candidates);
    }

    private static ProcessGroup BuildGroup(
        string name, List<ProcInfo> procs, Dictionary<int, double> cpu,
        MemorySnapshot snap, ProtectionPolicy policy, MemoryOptions options, ActivityTracker activity)
    {
        var known = procs.Where(p => cpu.ContainsKey(p.Pid)).ToList();
        // If even one process of the group has no reading, the group is "unknown", not "idle".
        double? groupCpu = known.Count == procs.Count && known.Count > 0 ? known.Sum(p => cpu[p.Pid]) : null;

        var window = procs.FirstOrDefault(p => p.HasWindow);
        return new ProcessGroup(
            name,
            procs.Select(p => p.Pid).ToList(),
            procs.Sum(p => p.WorkingSetBytes),
            procs.Sum(p => p.PrivateBytes),
            groupCpu,
            window is not null,
            window?.WindowTitle,
            snap.ForegroundPid is { } fg && procs.Any(p => p.Pid == fg),
            activity.IsRecent(name, snap.At, options.Recent),
            procs.Any(policy.IsProtected));
    }

    private static Dictionary<int, double> CpuPercentByPid(MemorySnapshot cur, MemorySnapshot? prev, int cores)
    {
        var result = new Dictionary<int, double>();
        if (prev is null) return result;
        var seconds = (cur.At - prev.At).TotalSeconds;
        if (seconds < 0.5) return result;

        var before = prev.Processes.ToDictionary(p => p.Pid);
        foreach (var p in cur.Processes)
        {
            if (p.CpuSeconds < 0) continue;
            if (!before.TryGetValue(p.Pid, out var b) || b.CpuSeconds < 0) continue;
            if (!string.Equals(b.Name, p.Name, StringComparison.OrdinalIgnoreCase)) continue; // pid was reused
            var delta = Math.Max(0, p.CpuSeconds - b.CpuSeconds);
            result[p.Pid] = delta / seconds / Math.Max(1, cores) * 100.0;
        }
        return result;
    }
}

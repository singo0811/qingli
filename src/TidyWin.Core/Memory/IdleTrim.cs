namespace TidyWin.Core.Memory;

/// <summary>
/// Remembers the last reading so the next one can see which programs are idle.
/// The first reading only establishes that. Trimming never includes a protected, foreground or recently used program.
/// </summary>
public sealed class IdleTrim
{
    private readonly ActivityTracker _activity = new();
    private MemorySnapshot? _previous;

    public IReadOnlyList<ProcessGroup> Candidates(MemorySnapshot snap, ProtectionPolicy policy, int cores)
    {
        var fgName = snap.ForegroundPid is { } fg
            ? snap.Processes.FirstOrDefault(p => p.Pid == fg && !policy.IsProtected(p))?.Name
            : null;
        _activity.Observe(snap.At, fgName is null ? null : ProtectionPolicy.StripExe(fgName));
        var analysis = MemoryAdvisor.Analyze(snap, _previous, policy, new MemoryOptions(Cores: Math.Max(1, cores)), _activity);
        _previous = snap;
        return analysis.TrimCandidates;
    }
}

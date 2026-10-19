using TidyWin.Core.Memory;

namespace TidyWin.Core.Status;

/// <summary>One program (all its processes together) and what it costs right now.</summary>
public sealed record ProcessLoad(string Name, int Count, double CpuPercent, long MemoryBytes, string? Explanation);

public static class ProcessLoadCalculator
{
    /// <summary>
    /// CPU share needs two readings: processor seconds used between them, divided by the time passed and the number of
    /// cores, so 100% means every core is busy. Processes of the same program are added together, because a browser with
    /// thirty tabs is one thing to the person looking at the list.
    /// </summary>
    public static IReadOnlyList<ProcessLoad> Compute(MemorySnapshot? before, MemorySnapshot now, int cores)
    {
        var elapsed = before is null ? 0 : (now.At - before.At).TotalSeconds;
        var previous = before?.Processes
            .Where(p => p.CpuSeconds >= 0)
            .GroupBy(p => p.Pid)
            .ToDictionary(g => g.Key, g => g.First().CpuSeconds) ?? [];

        var groups = new Dictionary<string, (int Count, double Cpu, long Mem)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in now.Processes)
        {
            double cpu = 0;
            if (elapsed > 0 && p.CpuSeconds >= 0 && previous.TryGetValue(p.Pid, out var was) && p.CpuSeconds >= was)
                cpu = (p.CpuSeconds - was) / elapsed / Math.Max(1, cores) * 100.0;

            var key = p.Name;
            groups.TryGetValue(key, out var g);
            groups[key] = (g.Count + 1, g.Cpu + cpu, g.Mem + p.WorkingSetBytes);
        }

        return groups
            .Select(kv => new ProcessLoad(kv.Key, kv.Value.Count, Math.Min(100, kv.Value.Cpu), kv.Value.Mem, ProcessExplainer.Explain(kv.Key)))
            .ToList();
    }
}

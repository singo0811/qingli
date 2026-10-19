using TidyWin.Core.Insights;
using TidyWin.Core.Mascot;
using TidyWin.Core.Startup;

namespace TidyWin.Platform;

/// <summary>
/// Gathers the measurements the suggestion engine works from. Only things that were really read go in: whatever could
/// not be read stays "unknown" (null or zero), and the engine never turns "unknown" into advice.
/// </summary>
public static class FactsCollector
{
    /// <summary>Cheap reads (registry, one disk query, the Recycle Bin size). Safe to call from a worker thread.</summary>
    public static Facts CollectFast(AppSettings settings, IReadOnlyList<AiTool> aiToolsInUse, DateTime nowUtc)
    {
        var snap = SystemReader.Read();

        var startupEnabled = 0;
        var startupDroppable = 0;
        var startupDead = 0;
        var startupSuspicious = 0;
        try
        {
            var pairs = new WindowsStartupSource().Read()
                .Select(e => (Entry: e, Advice: StartupAdvisor.Advise(e)))
                .ToList();
            startupEnabled = pairs.Count(p => p.Entry.Enabled);
            startupDroppable = StartupAdvisor.Droppable(pairs);
            startupDead = pairs.Count(p => p.Entry.Enabled && p.Advice.Kind == StartupKind.Dead);
            startupSuspicious = pairs.Count(p => p.Entry.Enabled && p.Advice.Kind == StartupKind.Suspicious);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }

        long? bin = null;
        try { bin = new WindowsOptimizer().RecycleBinBytes(); }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or DllNotFoundException) { }

        return new Facts
        {
            NowUtc = nowUtc,
            DiskUsedPercent = snap.SystemDiskUsedPercent,
            DiskFreeBytes = (long)snap.SystemDiskFreeBytes,
            DiskTotalBytes = (long)snap.SystemDiskTotalBytes,
            MemoryPercent = snap.MemoryPercent,
            UptimeDays = snap.UptimeDays,
            RebootPending = SystemExtras.RebootPending(),
            OsUnsupported = SystemExtras.OsUnsupported(nowUtc),
            RecycleBinBytes = bin,
            StartupEnabled = startupEnabled,
            StartupDroppable = startupDroppable,
            StartupDead = startupDead,
            StartupSuspicious = startupSuspicious,
            AiToolsInUse = aiToolsInUse,
            LastCleanUtc = settings.LastCleanUtc,
            LastCleanBytes = settings.LastCleanBytes,
            LastVisitUtc = settings.LastVisitUtc,
            DiskFreeAtLastVisit = settings.DiskFreeAtLastVisit,
        };
    }

    /// <summary>Adds disk health, devices with errors and activation (one PowerShell call, cached for 15 minutes).</summary>
    public static async Task<Facts> WithCheckupAsync(Facts facts, CancellationToken ct = default)
    {
        var c = await CheckupReader.ReadAsync(ct);
        return facts with
        {
            DiskUnhealthy = c.UnhealthyDisks.Count > 0,
            ProblemDevices = c.ProblemDevices.Count,
            NotActivated = c.Activated == false,
        };
    }
}

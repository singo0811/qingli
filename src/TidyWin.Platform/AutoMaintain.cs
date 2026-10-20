using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Localization;
using TidyWin.Core.Memory;
using TidyWin.Core.Paths;
using TidyWin.Core.Safety;

namespace TidyWin.Platform;

/// <summary>
/// The work the program does on its own while it sits in the tray. Junk is only caches that rebuild themselves.
/// Memory is only idle programs' unused pages, which come back when those programs need them. Nothing is closed here.
/// </summary>
public static class AutoMaintain
{
    public static readonly TimeSpan JunkInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan MemoryInterval = TimeSpan.FromMinutes(15);

    /// <summary>Bytes freed, or null when another operation was already running.</summary>
    public static long? CleanRegenerable(CancellationToken ct = default)
    {
        using var lease = OperationGate.Shared.TryEnter(Loc.T("自动清理缓存"));
        if (lease is null) return null;

        var catalog = AutoClean.Regenerable(SystemCatalog.All.Concat(AiCatalog.All));
        var fs = new WindowsFileSystem();
        var resolved = AiCatalog.ExpandWildcards(AiCatalog.Resolve(new ProcessAiEnvironment(), catalog), fs);
        var scans = AiScanner.Scan(resolved, fs, DateTime.UtcNow, ct);
        var files = scans.Where(s => s.Target.Safety == AiSafety.Regenerable).SelectMany(s => s.Eligible).ToList();
        if (files.Count == 0) return 0;

        var guard = new PathGuard(
            AiCatalog.AllowedRoots(resolved),
            AiCatalog.ProtectedPaths(AiCatalog.Resolve(new ProcessAiEnvironment(), SystemCatalog.All.Concat(AiCatalog.All))),
            new CachingReparseProbe(new FileSystemReparseProbe()));
        var report = CleanExecutor.Run(files, guard, fs, dryRun: false, progress: null, ct);
        return report.BytesFreed;
    }
}

/// <summary>One memory reader and the idle-trim memory that goes with it.</summary>
public sealed class IdleMemory
{
    private readonly IdleTrim _trim = new();
    private readonly ProtectionPolicy _policy = new(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.ProcessId);

    /// <summary>How many processes had idle pages returned. Zero on the first call, which only takes a baseline.</summary>
    public int TrimOnce()
    {
        var source = new WindowsMemorySource();
        var candidates = _trim.Candidates(source.Read(), _policy, Environment.ProcessorCount);
        if (candidates.Count == 0) return 0;
        var control = new WindowsProcessControl();
        var trimmed = 0;
        foreach (var pid in candidates.Where(g => !g.IsProtected && !g.IsForeground).SelectMany(g => g.Pids))
            if (control.Trim(pid) == TrimOutcome.Trimmed) trimmed++;
        return trimmed;
    }
}

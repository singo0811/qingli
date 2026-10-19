using TidyWin.Core.Clean;

namespace TidyWin.Core.AiJunk;

/// <summary>
/// Everything found for one location. <see cref="Eligible"/> already respects the target's minimum age,
/// so what the UI shows as "can be freed" is exactly what executing would try to delete.
/// </summary>
public sealed record AiScanItem(
    ResolvedAiTarget Resolved,
    bool Exists,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<FsFile> Eligible,
    long EligibleBytes,
    DateTime? NewestWriteUtc = null)
{
    public AiTarget Target => Resolved.Target;
    public string Path => Resolved.Path;
}

public static class AiScanner
{
    public static AiScanItem ScanOne(ResolvedAiTarget r, IFileSystem fs, DateTime nowUtc)
    {
        if (!fs.DirectoryExists(r.Path))
            return new AiScanItem(r, false, 0, 0, [], 0);

        var cutoff = r.Target.MinAgeDays is { } d ? nowUtc - TimeSpan.FromDays(d) : (DateTime?)null;

        var count = 0;
        long total = 0;
        long eligibleBytes = 0;
        var eligible = new List<FsFile>();
        DateTime? newest = null;

        foreach (var f in fs.EnumerateFilesRecursive(r.Path))
        {
            count++;
            if (newest is null || f.LastWriteUtc > newest) newest = f.LastWriteUtc;
            var size = f.IsLink ? 0 : f.Length;
            total += size;

            if (cutoff is { } c && f.LastWriteUtc > c) continue;
            eligible.Add(f);
            eligibleBytes += size;
        }

        return new AiScanItem(r, true, count, total, eligible, eligibleBytes, newest);
    }

    /// <summary>
    /// Scans every deletable target. "Never" targets are not enumerated at all: they are reported as
    /// "present or not" so the UI can show that they were seen and left alone.
    /// </summary>
    public static IReadOnlyList<AiScanItem> Scan(IEnumerable<ResolvedAiTarget> resolved, IFileSystem fs, DateTime nowUtc,
        CancellationToken ct = default)
    {
        var items = new List<AiScanItem>();
        foreach (var r in resolved)
        {
            ct.ThrowIfCancellationRequested();
            items.Add(r.Target.CanBeDeleted
                ? ScanOne(r, fs, nowUtc)
                : new AiScanItem(r, fs.DirectoryExists(r.Path), 0, 0, [], 0));
        }
        return items;
    }
}

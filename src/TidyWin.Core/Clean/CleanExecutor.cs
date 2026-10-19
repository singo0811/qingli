using TidyWin.Core.Paths;

namespace TidyWin.Core.Clean;

public enum SkipReason { Guarded, InUse, AccessDenied, Failed }

public sealed record SkippedFile(string Path, SkipReason Reason, GuardVerdict? Verdict = null);

public sealed record CleanProgress(int Processed, int Total, long BytesFreedSoFar, string CurrentPath);

public sealed record CleanReport(
    bool DryRun,
    int Deleted,
    long BytesFreed,
    int AlreadyGone,
    int DirectoriesRemoved,
    IReadOnlyList<SkippedFile> Skipped,
    bool Cancelled)
{
    public int SkippedCount => Skipped.Count;
}

/// <summary>
/// Deletes files the caller picked. Nothing is removed unless <see cref="PathGuard"/> allows it right
/// before the delete. A dry run asks the guard the same questions and reports what would happen.
/// </summary>
public static class CleanExecutor
{
    public static CleanReport Run(
        IReadOnlyList<FsFile> files,
        PathGuard guard,
        IFileSystem fs,
        bool dryRun,
        IProgress<CleanProgress>? progress = null,
        CancellationToken ct = default,
        ICollection<string>? deletedPaths = null)
    {
        var deleted = 0;
        long freed = 0;
        var gone = 0;
        var skipped = new List<SkippedFile>();
        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cancelled = false;

        for (var i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            var f = files[i];
            var verdict = guard.Check(f.Path, allowLeafLink: f.IsLink);
            if (!verdict.IsAllowed)
            {
                skipped.Add(new SkippedFile(f.Path, SkipReason.Guarded, verdict.Verdict));
            }
            else if (dryRun)
            {
                deleted++;
                freed += f.IsLink ? 0 : f.Length;
            }
            else
            {
                switch (fs.TryDeleteFile(verdict.NormalizedPath!))
                {
                    case DeleteOutcome.Deleted:
                        deleted++;
                        freed += f.IsLink ? 0 : f.Length;
                        touchedDirs.Add(ParentOf(verdict.NormalizedPath!));
                        deletedPaths?.Add(verdict.NormalizedPath!);
                        break;
                    case DeleteOutcome.Missing:
                        gone++;
                        break;
                    case DeleteOutcome.InUse:
                        skipped.Add(new SkippedFile(f.Path, SkipReason.InUse));
                        break;
                    case DeleteOutcome.AccessDenied:
                        skipped.Add(new SkippedFile(f.Path, SkipReason.AccessDenied));
                        break;
                    default:
                        skipped.Add(new SkippedFile(f.Path, SkipReason.Failed));
                        break;
                }
            }

            progress?.Report(new CleanProgress(i + 1, files.Count, freed, f.Path));
        }

        var dirsRemoved = dryRun ? 0 : RemoveEmptyDirectories(touchedDirs, guard, fs);
        return new CleanReport(dryRun, deleted, freed, gone, dirsRemoved, skipped, cancelled);
    }

    /// <summary>Walks up from each touched folder, deepest first. A root itself is never removed: the guard says "strictly under".</summary>
    private static int RemoveEmptyDirectories(HashSet<string> start, PathGuard guard, IFileSystem fs)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in start)
        {
            var cur = d;
            while (cur is not null && guard.Check(cur).IsAllowed && all.Add(cur))
                cur = ParentOf(cur) is { Length: > 0 } p && p != cur ? p : null;
        }

        var removed = 0;
        foreach (var dir in all.OrderByDescending(d => d.Length))
        {
            if (fs.TryDeleteEmptyDirectory(dir)) removed++;
        }
        return removed;
    }

    private static string ParentOf(string path)
    {
        var cut = path.LastIndexOf('\\');
        return cut <= 0 ? string.Empty : path[..cut];
    }
}

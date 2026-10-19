namespace TidyWin.Core.AiJunk;

/// <summary>A blob is a content-addressed file; several snapshots may point to the same one.</summary>
public sealed record HfBlob(string Id, long Bytes);

public sealed record HfSnapshot(string Revision, DateTime LastUsed, IReadOnlyList<HfBlob> Blobs);

/// <summary>
/// One cached repository (models--org--name). <see cref="RefRevisions"/> are the commits that refs such as
/// "main" currently point to.
/// </summary>
public sealed record HfRepo(string Name, IReadOnlyList<string> RefRevisions, IReadOnlyList<HfSnapshot> Snapshots);

public sealed record HfRevisionPlan(string Repo, IReadOnlyList<string> RevisionsToRemove, IReadOnlyList<string> BlobsToRemove, long BytesFreed);

public sealed record HfUnusedRepo(string Repo, DateTime LastUsed, long BytesFreed);

public static class HfCachePlanner
{
    /// <summary>
    /// Snapshots that no ref points to are old versions. They are safe to drop, but a blob is only freed
    /// when no kept snapshot still uses it. With no refs at all, the newest snapshot is kept so the repo stays usable.
    /// </summary>
    public static HfRevisionPlan StaleRevisions(HfRepo repo)
    {
        if (repo.Snapshots.Count == 0)
            return new HfRevisionPlan(repo.Name, [], [], 0);

        var keep = new HashSet<string>(
            repo.Snapshots.Select(s => s.Revision).Where(r => repo.RefRevisions.Contains(r, StringComparer.Ordinal)),
            StringComparer.Ordinal);

        if (keep.Count == 0)
            keep.Add(repo.Snapshots.OrderByDescending(s => s.LastUsed).First().Revision);

        var stale = repo.Snapshots.Where(s => !keep.Contains(s.Revision)).ToList();
        var keptBlobs = repo.Snapshots.Where(s => keep.Contains(s.Revision))
            .SelectMany(s => s.Blobs).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);

        var freed = stale.SelectMany(s => s.Blobs)
            .Where(b => !keptBlobs.Contains(b.Id))
            .GroupBy(b => b.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        return new HfRevisionPlan(
            repo.Name,
            stale.Select(s => s.Revision).ToList(),
            freed.Select(b => b.Id).ToList(),
            freed.Sum(b => b.Bytes));
    }

    /// <summary>
    /// Repositories whose every snapshot was last used more than <paramref name="unusedDays"/> days ago.
    /// These are suggestions for the user to pick from, never selected automatically.
    /// </summary>
    public static IReadOnlyList<HfUnusedRepo> UnusedRepos(IEnumerable<HfRepo> repos, DateTime now, int unusedDays)
    {
        if (unusedDays <= 0) throw new ArgumentOutOfRangeException(nameof(unusedDays));
        var cutoff = now - TimeSpan.FromDays(unusedDays);

        var list = new List<HfUnusedRepo>();
        foreach (var r in repos)
        {
            if (r.Snapshots.Count == 0) continue;
            var last = r.Snapshots.Max(s => s.LastUsed);
            if (last > cutoff) continue;

            var bytes = r.Snapshots.SelectMany(s => s.Blobs)
                .GroupBy(b => b.Id, StringComparer.Ordinal)
                .Sum(g => g.First().Bytes);
            list.Add(new HfUnusedRepo(r.Name, last, bytes));
        }
        return list.OrderByDescending(x => x.BytesFreed).ToList();
    }
}

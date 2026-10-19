using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Models;

/// <summary>
/// Reads the Hugging Face hub cache (models--org--name, datasets--..., spaces--...). For each repository it offers
/// "the whole thing", and, when old versions are lying around, a second entry for just those old versions.
/// </summary>
public static class HfInventory
{
    public static IReadOnlyList<ModelEntry> Scan(string hubRoot, IFileSystem fs)
    {
        if (!fs.DirectoryExists(hubRoot)) return [];

        var entries = new List<ModelEntry>();
        foreach (var dir in fs.EnumerateDirectories(hubRoot))
        {
            var folder = dir[(dir.LastIndexOf('\\') + 1)..];
            if (!TryParseRepo(folder, out var kind, out var name)) continue;

            var files = fs.EnumerateFilesRecursive(dir).ToList();
            if (files.Count == 0) continue;

            var blobs = new Dictionary<string, FsFile>(StringComparer.OrdinalIgnoreCase);
            var refs = new List<string>();
            var snapshotFiles = new Dictionary<string, List<FsFile>>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in files)
            {
                var rel = f.Path[(dir.Length + 1)..].Split('\\');
                switch (rel[0].ToLowerInvariant())
                {
                    case "blobs" when rel.Length == 2:
                        blobs[rel[1]] = f;
                        break;
                    case "refs":
                        if (fs.ReadText(f.Path, 256)?.Trim() is { Length: > 0 } rev) refs.Add(rev);
                        break;
                    case "snapshots" when rel.Length >= 3:
                        if (!snapshotFiles.TryGetValue(rel[1], out var list)) snapshotFiles[rel[1]] = list = [];
                        list.Add(f);
                        break;
                }
            }

            var snapshots = snapshotFiles.Select(kv => new HfSnapshot(
                kv.Key,
                kv.Value.Max(f => f.LastWriteUtc),
                kv.Value.Select(f => BlobOf(f, blobs, fs)).ToList())).ToList();

            var repo = new HfRepo(name, refs, snapshots);
            var updated = snapshots.Count > 0 ? snapshots.Max(s => s.LastUsed) : files.Max(f => f.LastWriteUtc);

            entries.Add(new ModelEntry(
                "hf:" + folder, ModelSource.HuggingFace, name, kind, updated,
                files.Select(f => new ModelPart(f)).ToList(),
                snapshots.Count > 1 ? Loc.F("{0} 个版本",snapshots.Count) : null));

            // Without a readable ref we cannot tell which version is the current one, so we do not guess.
            var stale = refs.Count > 0 ? HfCachePlanner.StaleRevisions(repo) : new HfRevisionPlan(name, [], [], 0);
            if (stale.RevisionsToRemove.Count > 0)
            {
                var parts = new List<ModelPart>();
                foreach (var rev in stale.RevisionsToRemove)
                    if (snapshotFiles.TryGetValue(rev, out var sf)) parts.AddRange(sf.Select(f => new ModelPart(f)));
                foreach (var blobId in stale.BlobsToRemove)
                    if (blobs.TryGetValue(blobId, out var b)) parts.Add(new ModelPart(b));

                entries.Add(new ModelEntry(
                    "hf-old:" + folder, ModelSource.HuggingFace, name + Loc.T("(旧版本)"), kind,
                    snapshots.Where(s => stale.RevisionsToRemove.Contains(s.Revision)).Max(s => s.LastUsed),
                    parts,
                    Loc.F("{0} 个旧版本,当前版本保留",stale.RevisionsToRemove.Count)));
            }
        }
        return entries.OrderByDescending(e => e.OwnBytes).ToList();
    }

    /// <summary>"models--org--name" → ("模型", "org/name"). Folders that are not repositories are ignored.</summary>
    public static bool TryParseRepo(string folder, out string kind, out string name)
    {
        kind = name = "";
        var parts = folder.Split("--");
        if (parts.Length < 3) return false;
        kind = parts[0] switch { "models" => Loc.T("Hugging Face 模型"), "datasets" => Loc.T("Hugging Face 数据集"), "spaces" => "Hugging Face Space", _ => "" };
        if (kind.Length == 0) return false;
        name = string.Join('/', parts[1..]);
        return true;
    }

    private static HfBlob BlobOf(FsFile snapshotFile, Dictionary<string, FsFile> blobs, IFileSystem fs)
    {
        if (snapshotFile.IsLink)
        {
            // A snapshot entry is a link into blobs\<hash>. Only the last path segment is trusted as the blob id.
            var target = fs.LinkTarget(snapshotFile.Path);
            var id = target is null ? "" : target[(target.LastIndexOfAny(['\\', '/']) + 1)..];
            return new HfBlob(id, blobs.TryGetValue(id, out var b) ? b.Length : 0);
        }
        // Without symlink support Windows keeps a real copy in the snapshot itself.
        return new HfBlob("copy:" + snapshotFile.Path, snapshotFile.Length);
    }
}

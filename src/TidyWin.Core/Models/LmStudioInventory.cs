using TidyWin.Core.Clean;
using TidyWin.Core.Localization;

namespace TidyWin.Core.Models;

/// <summary>LM Studio keeps models as models\&lt;publisher&gt;\&lt;repo&gt;\*.gguf. One entry per publisher/repo.</summary>
public static class LmStudioInventory
{
    public static IReadOnlyList<ModelEntry> Scan(string root, IFileSystem fs)
    {
        if (!fs.DirectoryExists(root)) return [];

        var entries = new List<ModelEntry>();
        foreach (var publisher in fs.EnumerateDirectories(root))
        {
            foreach (var repo in fs.EnumerateDirectories(publisher))
            {
                var files = fs.EnumerateFilesRecursive(repo).ToList();
                if (files.Count == 0) continue;

                var name = LastSegment(publisher) + "/" + LastSegment(repo);
                var note = files.Count == 1 ? null : Loc.F("{0} 个文件",files.Count);
                entries.Add(new ModelEntry(
                    "lmstudio:" + name, ModelSource.LmStudio, name, Loc.T("LM Studio 模型"),
                    files.Max(f => f.LastWriteUtc),
                    files.Select(f => new ModelPart(f)).ToList(),
                    note));
            }
        }
        return entries.OrderByDescending(e => e.OwnBytes).ToList();
    }

    private static string LastSegment(string path) => path[(path.LastIndexOf('\\') + 1)..];
}

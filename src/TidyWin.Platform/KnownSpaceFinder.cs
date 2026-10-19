using TidyWin.Core.Clean;
using TidyWin.Core.Disk;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Localization;

namespace TidyWin.Platform;

/// <summary>Finds the well-known space users that exist on this computer, and measures the ones that are cheap to measure.</summary>
public sealed class KnownSpaceFinder : IKnownSpaceReader
{
    // Counting the contents of these takes minutes or is blocked by Windows, and the number would not be honest anyway.
    private static readonly HashSet<string> NoSize = new(StringComparer.Ordinal) { "winsxs", "installer" };

    public IReadOnlyList<KnownSpaceHit> Read()
    {
        var hits = new List<KnownSpaceHit>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var drive = Path.GetPathRoot(windows)?.TrimEnd('\\') ?? "C:";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var spec in KnownSpace.All)
        {
            foreach (var template in spec.Locations)
            {
                var path = template.Replace("{SystemDrive}", drive).Replace("{Windows}", windows).Replace("{LocalAppData}", local);
                foreach (var found in Expand(path))
                {
                    long? bytes = null;
                    try
                    {
                        if (File.Exists(found)) bytes = new FileInfo(found).Length;
                        else if (Directory.Exists(found) && !NoSize.Contains(spec.Id)) bytes = SizeOf(found);
                        else if (!Directory.Exists(found)) continue;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    hits.Add(new KnownSpaceHit(spec, found, bytes));
                }
            }
        }
        return hits;
    }

    /// <summary>A "*" stands for exactly one folder name.</summary>
    private static IEnumerable<string> Expand(string path)
    {
        var star = path.IndexOf('*');
        if (star < 0) { yield return path; yield break; }

        var cut = path.LastIndexOf('\\', star);
        var parent = path[..cut];
        var rest = path[(path.IndexOf('\\', star))..];
        IEnumerable<string> dirs;
        try { dirs = Directory.Exists(parent) ? Directory.EnumerateDirectories(parent).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var d in dirs)
        {
            var candidate = d + rest;
            if (File.Exists(candidate) || Directory.Exists(candidate)) yield return candidate;
        }
    }

    private static long SizeOf(string dir)
    {
        long total = 0;
        foreach (var f in new WindowsFileSystem().EnumerateFilesRecursive(dir)) if (!f.IsLink) total += f.Length;
        return total;
    }
}

/// <summary>Remembers what clean-ups did, as small JSON files the person can open or export.</summary>
public sealed class ManifestStore : IManifestSink
{
    private const int KeepFiles = 30;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWin", "logs");

    public void Record(ManifestEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var safeSource = new string(entry.Source.Where(char.IsLetterOrDigit).ToArray());
            var file = Path.Combine(Folder, $"clean-{entry.AtUtc:yyyyMMdd-HHmmss}-{safeSource}.json");
            File.WriteAllText(file, CleanManifest.ToJson(entry));

            foreach (var old in new DirectoryInfo(Folder).EnumerateFiles("clean-*.json").OrderByDescending(f => f.Name).Skip(KeepFiles))
                old.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A record that could not be written must never turn a finished clean-up into an error.
        }
    }
}

/// <summary>Reads the person's own rule files from the rules folder. A damaged or oversized file is skipped with a note.</summary>
public static class RuleStore
{
    private const long MaxFileBytes = 256 * 1024;
    private const int MaxFiles = 10;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWin", "rules");

    public static RuleResult Load(string? folder = null)
    {
        folder ??= Folder;
        var targets = new List<AiTarget>();
        var problems = new List<string>();
        try
        {
            if (!Directory.Exists(folder)) return new RuleResult(targets, problems);

            foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(MaxFiles))
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes) { problems.Add(Loc.F("{0}:文件太大,已跳过",info.Name)); continue; }

                var parsed = RuleFile.Parse(File.ReadAllText(file));
                foreach (var t in parsed.Targets)
                {
                    // Ids must be unique across files and must not collide with a built-in target.
                    if (targets.Any(x => x.Id == t.Id) || SystemCatalog.All.Any(x => x.Id == t.Id) || AiCatalog.All.Any(x => x.Id == t.Id))
                        problems.Add(Loc.F("{0}:id「{1}」重复,已跳过",info.Name,t.Id));
                    else targets.Add(t);
                }
                problems.AddRange(parsed.Problems.Select(p => $"{info.Name}:{p}"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add(Loc.T("读取规则文件夹失败:") + e.Message);
        }
        return new RuleResult(targets, problems);
    }
}

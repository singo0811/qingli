using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Paths;
using Xunit;

namespace TidyWin.Core.Tests;

public class CleanExecutorTests
{
    internal sealed class FakeFs : IFileSystem
    {
        public Dictionary<string, FsFile> Files = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Locked = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DeleteCalls = [];
        public Dictionary<string, string> Text = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Links = new(StringComparer.OrdinalIgnoreCase);

        public FakeFs AddText(string path, string text, int ageDays = 10)
        {
            Add(path, text.Length, ageDays);
            Text[path] = text;
            return this;
        }

        public FakeFs AddLink(string path, string target, int ageDays = 10)
        {
            Add(path, 0, ageDays, link: true);
            Links[path] = target;
            return this;
        }

        public string? ReadText(string path, long maxBytes) =>
            Text.TryGetValue(path, out var t) && t.Length <= maxBytes ? t : null;

        public string? LinkTarget(string path) => Links.TryGetValue(path, out var t) ? t : null;

        public FakeFs Add(string path, long len = 10, int ageDays = 100, bool link = false)
        {
            Files[path] = new FsFile(path, len, Now.AddDays(-ageDays), false, link);
            for (var d = Parent(path); d.Length > 3; d = Parent(d)) Dirs.Add(d);
            return this;
        }

        private static string Parent(string p) => p[..p.LastIndexOf('\\')];
        public bool DirectoryExists(string path) => Dirs.Contains(path);
        public FsFile? GetFile(string path) => Files.TryGetValue(path, out var f) ? f : null;
        public IEnumerable<string> EnumerateDirectories(string dir) =>
            Dirs.Where(d => d.Length > dir.Length && Parent(d).Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        public IEnumerable<FsFile> EnumerateFilesRecursive(string dir) =>
            Files.Values.Where(f => f.Path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

        public DeleteOutcome TryDeleteFile(string path)
        {
            DeleteCalls.Add(path);
            if (Locked.Contains(path)) return DeleteOutcome.InUse;
            return Files.Remove(path) ? DeleteOutcome.Deleted : DeleteOutcome.Missing;
        }

        public bool TryDeleteEmptyDirectory(string path)
        {
            if (Files.Keys.Any(k => k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))) return false;
            if (Dirs.Any(d => d.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))) return false;
            return Dirs.Remove(path);
        }
    }

    internal sealed class Probe(params string[] links) : IReparseProbe
    {
        public bool IsReparsePoint(string p) => links.Contains(p, StringComparer.OrdinalIgnoreCase);
    }

    internal static readonly DateTime Now = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
    private const string Root = @"C:\Users\Ann\.cache\torch";

    private static PathGuard Guard(params string[] links) =>
        new([Root], [], new Probe(links));

    [Fact]
    public void Deletes_allowed_files_and_reports_bytes()
    {
        var fs = new FakeFs().Add(Root + @"\a.bin", 100).Add(Root + @"\sub\b.bin", 50);
        var report = CleanExecutor.Run(fs.Files.Values.ToList(), Guard(), fs, dryRun: false);

        Assert.Equal(2, report.Deleted);
        Assert.Equal(150, report.BytesFreed);
        Assert.Empty(fs.Files);
    }

    [Fact]
    public void Removes_emptied_folders_but_never_the_root()
    {
        var fs = new FakeFs().Add(Root + @"\sub\deep\b.bin");
        var report = CleanExecutor.Run(fs.Files.Values.ToList(), Guard(), fs, dryRun: false);

        Assert.Equal(2, report.DirectoriesRemoved);
        Assert.DoesNotContain(Root + @"\sub", fs.Dirs);
        Assert.Contains(Root, fs.Dirs);
    }

    [Fact]
    public void Dry_run_deletes_nothing_but_reports_the_same_numbers()
    {
        var fs = new FakeFs().Add(Root + @"\a.bin", 100);
        var report = CleanExecutor.Run(fs.Files.Values.ToList(), Guard(), fs, dryRun: true);

        Assert.True(report.DryRun);
        Assert.Equal(1, report.Deleted);
        Assert.Equal(100, report.BytesFreed);
        Assert.Empty(fs.DeleteCalls);
        Assert.Single(fs.Files);
    }

    [Fact]
    public void Files_outside_the_allowed_roots_are_refused_and_never_touch_the_disk()
    {
        var fs = new FakeFs().Add(@"C:\Users\Ann\Documents\thesis.docx").Add(Root + @"\a.bin");
        var report = CleanExecutor.Run(fs.Files.Values.ToList(), Guard(), fs, dryRun: false);

        Assert.Equal(1, report.Deleted);
        var skipped = Assert.Single(report.Skipped);
        Assert.Equal(SkipReason.Guarded, skipped.Reason);
        Assert.Equal(GuardVerdict.OutsideAllowedRoots, skipped.Verdict);
        Assert.DoesNotContain(@"C:\Users\Ann\Documents\thesis.docx", fs.DeleteCalls);
    }

    [Fact]
    public void Files_below_a_linked_directory_are_refused()
    {
        var fs = new FakeFs().Add(Root + @"\linked\secret.bin");
        var report = CleanExecutor.Run(fs.Files.Values.ToList(), Guard(Root + @"\linked"), fs, dryRun: false);

        Assert.Equal(0, report.Deleted);
        Assert.Equal(GuardVerdict.ReparsePoint, report.Skipped[0].Verdict);
        Assert.Empty(fs.DeleteCalls);
    }

    [Fact]
    public void A_link_file_itself_can_be_removed_and_counts_no_bytes()
    {
        var link = Root + @"\snap\model.bin";
        var fs = new FakeFs().Add(link, 999, link: true);
        var report = CleanExecutor.Run(fs.Files.Values.ToList(), Guard(link), fs, dryRun: false);

        Assert.Equal(1, report.Deleted);
        Assert.Equal(0, report.BytesFreed);
    }

    [Fact]
    public void A_link_that_the_scan_did_not_mark_as_link_is_still_refused()
    {
        // The file system says "link" at delete time, but the caller listed it as a normal file.
        var p = Root + @"\swapped.bin";
        var fs = new FakeFs().Add(p, 5);
        var report = CleanExecutor.Run(fs.Files.Values.ToList(), Guard(p), fs, dryRun: false);

        Assert.Equal(0, report.Deleted);
        Assert.Equal(GuardVerdict.ReparsePoint, report.Skipped[0].Verdict);
    }

    [Fact]
    public void Locked_file_is_skipped_and_the_rest_go_on()
    {
        var fs = new FakeFs().Add(Root + @"\busy.bin").Add(Root + @"\free.bin");
        fs.Locked.Add(Root + @"\busy.bin");
        var report = CleanExecutor.Run(fs.Files.Values.ToList(), Guard(), fs, dryRun: false);

        Assert.Equal(1, report.Deleted);
        Assert.Equal(SkipReason.InUse, report.Skipped.Single().Reason);
    }

    [Fact]
    public void Cancellation_stops_between_files()
    {
        var fs = new FakeFs().Add(Root + @"\a.bin").Add(Root + @"\b.bin").Add(Root + @"\c.bin");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Processed == 1) cts.Cancel(); });

        var report = CleanExecutor.Run(fs.Files.Values.ToList(), Guard(), fs, false, progress, cts.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(1, report.Deleted);
        Assert.Equal(2, fs.Files.Count);
    }

    private sealed class SyncProgress(Action<CleanProgress> a) : IProgress<CleanProgress>
    {
        public void Report(CleanProgress value) => a(value);
    }

    // ---- Scanner ----

    private static ResolvedAiTarget Target(string id, string path, int? minAge = null) =>
        new(new AiTarget(id, "t", id, AiCategory.PackageCache, AiSafety.Regenerable, KnownFolder.UserProfile, "x", [], "c", minAge), path);

    [Fact]
    public void Scan_counts_everything_but_only_old_files_are_eligible()
    {
        var fs = new FakeFs().Add(Root + @"\old.bin", 100, ageDays: 30).Add(Root + @"\new.bin", 40, ageDays: 1);
        var item = AiScanner.ScanOne(Target("t", Root, minAge: 7), fs, Now);

        Assert.Equal(2, item.FileCount);
        Assert.Equal(140, item.TotalBytes);
        Assert.Equal(100, item.EligibleBytes);
        Assert.Single(item.Eligible);
    }

    [Fact]
    public void Scan_ignores_link_sizes()
    {
        var fs = new FakeFs().Add(Root + @"\l.bin", 5000, link: true).Add(Root + @"\r.bin", 7);
        var item = AiScanner.ScanOne(Target("t", Root), fs, Now);
        Assert.Equal(7, item.TotalBytes);
    }

    [Fact]
    public void Missing_directory_scans_as_not_existing()
    {
        var item = AiScanner.ScanOne(Target("t", @"C:\nope"), new FakeFs(), Now);
        Assert.False(item.Exists);
    }

    [Fact]
    public void Never_targets_are_not_enumerated()
    {
        var never = new ResolvedAiTarget(
            new AiTarget("n", "t", "n", AiCategory.UserData, AiSafety.Never, KnownFolder.AppData, "x", [], "c"), Root);
        var fs = new FakeFs().Add(Root + @"\chat.db", 1000);

        var item = AiScanner.Scan([never], fs, Now).Single();

        Assert.True(item.Exists);
        Assert.Equal(0, item.TotalBytes);
        Assert.Empty(item.Eligible);
    }

    // ---- Wildcard expansion ----

    [Fact]
    public void Wildcard_expands_to_matching_directories_and_becomes_allowed_roots()
    {
        var temp = @"C:\Users\Ann\AppData\Local\Temp";
        var fs = new FakeFs().Add(temp + @"\torchinductor_ann\k.bin").Add(temp + @"\other\x.bin");
        var wild = new ResolvedAiTarget(AiCatalog.All.Single(t => t.Id == "torch.inductor"), temp + @"\torchinductor_*");

        Assert.DoesNotContain(temp + @"\torchinductor_*", AiCatalog.AllowedRoots([wild]));

        var expanded = AiCatalog.ExpandWildcards([wild], fs);

        Assert.Equal(temp + @"\torchinductor_ann", Assert.Single(expanded).Path);
        Assert.Equal([temp + @"\torchinductor_ann"], AiCatalog.AllowedRoots(expanded));
    }

    [Theory]
    [InlineData("torchinductor_ann", "torchinductor_*", true)]
    [InlineData("TORCHINDUCTOR_x", "torchinductor_*", true)]
    [InlineData("other", "torchinductor_*", false)]
    [InlineData("a.b", "a.b", true)]
    [InlineData("axb", "a.b", false)]
    public void Glob_matching(string name, string pattern, bool expected) =>
        Assert.Equal(expected, AiCatalog.GlobMatch(name, pattern));
}


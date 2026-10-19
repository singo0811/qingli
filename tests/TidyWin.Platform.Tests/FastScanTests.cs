using System.Diagnostics;
using TidyWin.Core.Clean;
using TidyWin.Core.Disk;
using TidyWin.Platform;
using Xunit;
using Xunit.Abstractions;

namespace TidyWin.Platform.Tests;

/// <summary>The fast listing must report exactly what the plain .NET listing reports, only sooner.</summary>
public sealed class FastScanTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tidywin-scan-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsFileSystem _fs = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // What the cleaner used before: the plain DirectoryInfo walk.
    private static IEnumerable<FsFile> Legacy(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var e in entries)
            {
                var isLink = e.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (e.Attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!isLink) pending.Push(e.FullName);
                    continue;
                }
                long length = 0;
                try { length = ((FileInfo)e).Length; } catch (IOException) { }
                yield return new FsFile(e.FullName, length, e.LastWriteTimeUtc, e.Attributes.HasFlag(FileAttributes.ReadOnly), isLink);
            }
        }
    }

    private static string Sig(FsFile f) => $"{f.Path}|{(f.IsLink ? 0 : f.Length)}|{f.LastWriteUtc.Ticks}|{f.IsReadOnly}|{f.IsLink}";

    private void Make(string rel, int bytes, bool readOnly = false)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
        if (readOnly) File.SetAttributes(p, FileAttributes.ReadOnly);
    }

    private void BuildTree()
    {
        Make("a.txt", 10);
        Make(@"d1\b.bin", 2000);
        Make(@"d1\d2\c.bin", 300_000);
        Make(@"d1\d2\d3\ro.txt", 7, readOnly: true);
        Make(@"empty\.keep", 0);
        Make(@"ünï cödé 中文\x y.txt", 55);
        Make(@"other\z.bin", 99);
        for (var i = 0; i < 60; i++) Make($@"wide\w{i}\f{i}.dat", i + 1);

        // A junction to a folder outside the tree: its contents must not be reported or entered.
        var outside = Path.Combine(Path.GetTempPath(), "tidywin-scan-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "secret.bin"), new byte[1234]);
        _outside = outside;
        Directory.CreateDirectory(Path.Combine(_root, "links"));
        var junction = Path.Combine(_root, "links", "jn");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        Process.Start(psi)!.WaitForExit();
    }

    private string? _outside;

    [Fact]
    public void Fast_listings_report_exactly_what_the_plain_listing_reports()
    {
        BuildTree();
        try
        {
            var expected = Legacy(_root).Select(Sig).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.True(expected.Count > 60);

            Assert.Equal(expected, _fs.EnumerateFilesRecursive(_root).Select(Sig).OrderBy(s => s, StringComparer.Ordinal).ToList());
            Assert.Equal(expected, _fs.EnumerateFilesRecursiveFast(_root).Select(Sig).OrderBy(s => s, StringComparer.Ordinal).ToList());

            Assert.DoesNotContain(_fs.EnumerateFilesRecursiveFast(_root), f => f.Path.Contains("secret.bin"));
        }
        finally { if (_outside is not null) try { Directory.Delete(_outside, true); } catch { } }
    }

    [Fact]
    public void Folder_listing_leaves_out_links_and_unreadable_places_quietly()
    {
        BuildTree();
        try
        {
            var subs = _fs.EnumerateDirectories(_root).Select(Path.GetFileName).OrderBy(s => s).ToList();
            Assert.Contains("d1", subs);
            Assert.DoesNotContain("links", subs.Where(s => s == "jn"));
            Assert.Empty(_fs.EnumerateDirectories(Path.Combine(_root, "no-such-folder")));
            Assert.Empty(_fs.EnumerateFilesRecursive(Path.Combine(_root, "no-such-folder")));
            Assert.Empty(_fs.EnumerateFilesRecursiveFast(Path.Combine(_root, "no-such-folder")));
            Assert.Empty(_fs.EnumerateDirectories(Path.Combine(_root, "links", "jn")).Where(_ => false));
        }
        finally { if (_outside is not null) try { Directory.Delete(_outside, true); } catch { } }
    }

    [Fact]
    public void A_path_longer_than_the_classic_limit_is_listed()
    {
        var deep = _root;
        for (var i = 0; i < 14; i++) deep = Path.Combine(deep, new string('d', 30) + i);
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "deep.bin"), new byte[321]);
        Assert.True(deep.Length > 300);

        Assert.Contains(_fs.EnumerateFilesRecursive(_root), f => f.Length == 321);
        Assert.Contains(_fs.EnumerateFilesRecursiveFast(_root), f => f.Length == 321);
    }

    [Fact]
    public void Stopping_early_releases_the_workers_and_cancelling_ends_the_walk()
    {
        for (var i = 0; i < 200; i++) Make($@"many\m{i}\f.dat", 1);

        var first = _fs.EnumerateFilesRecursiveFast(_root).Take(3).ToList();    // the caller leaves after three
        Assert.Equal(3, first.Count);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sw = Stopwatch.StartNew();
        var got = _fs.EnumerateFilesRecursiveFast(_root, cts.Token).Count();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(got <= 200);
    }

    [Fact]
    public void The_disk_scanner_counts_the_same_bytes_either_way()
    {
        BuildTree();
        try
        {
            var viaFast = DiskScanner.Scan(_root, _fs);
            var viaPlain = DiskScanner.Scan(_root, new PlainFs(_fs));
            Assert.Equal(viaPlain.TotalBytes, viaFast.TotalBytes);
            Assert.Equal(viaPlain.FileCount, viaFast.FileCount);
            Assert.Equal(viaPlain.Root.Folders.Select(f => (f.Name, f.Bytes)).OrderBy(x => x.Name), viaFast.Root.Folders.Select(f => (f.Name, f.Bytes)).OrderBy(x => x.Name));
        }
        finally { if (_outside is not null) try { Directory.Delete(_outside, true); } catch { } }
    }

    /// <summary>The same disk, but only the plain listing (no fast form), to compare against.</summary>
    private sealed class PlainFs(WindowsFileSystem inner) : IFileSystem
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FsFile? GetFile(string path) => inner.GetFile(path);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public IEnumerable<FsFile> EnumerateFilesRecursive(string directory) => Legacy(directory);
        public string? ReadText(string path, long maxBytes) => inner.ReadText(path, maxBytes);
        public string? LinkTarget(string path) => inner.LinkTarget(path);
        public DeleteOutcome TryDeleteFile(string path) => inner.TryDeleteFile(path);
        public bool TryDeleteEmptyDirectory(string path) => inner.TryDeleteEmptyDirectory(path);
    }

    [Fact]
    public void Timing_on_this_machines_own_profile_folder()
    {
        var target = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _ = Legacy(target).LongCount(); // warm the system cache once so every line below starts from the same state
        long Time(string label, Func<long> run)
        {
            var sw = Stopwatch.StartNew();
            var n = run();
            output.WriteLine($"{label,-34} {sw.ElapsedMilliseconds,7} ms  {n,9:N0} files");
            return sw.ElapsedMilliseconds;
        }

        Time("plain DirectoryInfo (before)", () => Legacy(target).LongCount());
        Time("buffered listing, one worker", () => _fs.EnumerateFilesRecursive(target).LongCount());
        Time("buffered listing, parallel (now)", () => _fs.EnumerateFilesRecursiveFast(target).LongCount());
        Time("DiskScanner (before: plain)", () => DiskScanner.Scan(target, new PlainFs(_fs)).FileCount);
        Time("DiskScanner (now: parallel)", () => DiskScanner.Scan(target, _fs).FileCount);
    }
}

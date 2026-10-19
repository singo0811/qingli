using System.Diagnostics;
using TidyWin.Core.Clean;
using TidyWin.Core.Paths;
using TidyWin.Platform;
using Xunit;

namespace TidyWin.Platform.Tests;

public sealed class RealDiskTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "tidywin-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _cache;
    private readonly string _outside;
    private readonly WindowsFileSystem _fs = new();

    public RealDiskTests()
    {
        _cache = Path.Combine(_sandbox, "cache");
        _outside = Path.Combine(_sandbox, "precious");
        Directory.CreateDirectory(_cache);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first so deleting the sandbox cannot follow them.
            foreach (var d in Directory.EnumerateDirectories(_sandbox, "*", SearchOption.AllDirectories))
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d, false);
            Directory.Delete(_sandbox, true);
        }
        catch { /* best effort */ }
    }

    private PathGuard Guard() => new([_cache], [], new CachingReparseProbe(new FileSystemReparseProbe()));

    private static bool MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    [Fact]
    public void Deletes_real_files_empties_folders_and_keeps_root()
    {
        Directory.CreateDirectory(Path.Combine(_cache, "a", "b"));
        File.WriteAllText(Path.Combine(_cache, "a", "b", "x.bin"), "hello");
        File.WriteAllText(Path.Combine(_cache, "y.bin"), "world!");

        var files = _fs.EnumerateFilesRecursive(_cache).ToList();
        var report = CleanExecutor.Run(files, Guard(), _fs, dryRun: false);

        Assert.Equal(2, report.Deleted);
        Assert.Equal(11, report.BytesFreed);
        Assert.False(Directory.Exists(Path.Combine(_cache, "a")));
        Assert.True(Directory.Exists(_cache));
    }

    [Fact]
    public void Read_only_files_are_deleted()
    {
        var f = Path.Combine(_cache, "ro.bin");
        File.WriteAllText(f, "x");
        File.SetAttributes(f, FileAttributes.ReadOnly);

        var report = CleanExecutor.Run(_fs.EnumerateFilesRecursive(_cache).ToList(), Guard(), _fs, false);

        Assert.Equal(1, report.Deleted);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void A_file_held_open_is_reported_in_use_and_survives()
    {
        var f = Path.Combine(_cache, "busy.bin");
        File.WriteAllText(f, "x");
        using var hold = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read);

        var report = CleanExecutor.Run(_fs.EnumerateFilesRecursive(_cache).ToList(), Guard(), _fs, false);

        Assert.Equal(0, report.Deleted);
        Assert.Equal(SkipReason.InUse, report.Skipped.Single().Reason);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Junction_inside_the_cache_is_not_entered_and_its_target_survives()
    {
        File.WriteAllText(Path.Combine(_outside, "keep.txt"), "do not delete");
        var link = Path.Combine(_cache, "shortcut");
        if (!MakeJunction(link, _outside)) return; // junction creation not possible here

        File.WriteAllText(Path.Combine(_cache, "junk.bin"), "x");

        var files = _fs.EnumerateFilesRecursive(_cache).ToList();
        Assert.DoesNotContain(files, f => f.Path.Contains("keep.txt"));

        var report = CleanExecutor.Run(files, Guard(), _fs, false);

        Assert.Equal(1, report.Deleted);
        Assert.True(File.Exists(Path.Combine(_outside, "keep.txt")));
        Assert.True(Directory.Exists(link)); // the junction itself is left alone
    }

    [Fact]
    public void A_file_whose_parent_becomes_a_junction_after_the_scan_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_cache, "d"));
        File.WriteAllText(Path.Combine(_cache, "d", "f.bin"), "x");
        File.WriteAllText(Path.Combine(_outside, "f.bin"), "precious");

        var scanned = _fs.EnumerateFilesRecursive(_cache).ToList();

        // Between scan and delete someone swaps the directory for a junction to somewhere else.
        File.Delete(Path.Combine(_cache, "d", "f.bin"));
        Directory.Delete(Path.Combine(_cache, "d"));
        if (!MakeJunction(Path.Combine(_cache, "d"), _outside)) return;

        var report = CleanExecutor.Run(scanned, Guard(), _fs, false);

        Assert.Equal(0, report.Deleted);
        Assert.True(File.Exists(Path.Combine(_outside, "f.bin")));
    }

    [Fact]
    public void Enumerate_directories_skips_junctions()
    {
        Directory.CreateDirectory(Path.Combine(_cache, "real"));
        if (!MakeJunction(Path.Combine(_cache, "jump"), _outside)) return;

        var dirs = _fs.EnumerateDirectories(_cache).Select(Path.GetFileName).ToList();

        Assert.Equal(["real"], dirs);
    }
}

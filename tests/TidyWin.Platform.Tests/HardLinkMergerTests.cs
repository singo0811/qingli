using TidyWin.Platform;
using Xunit;

namespace TidyWin.Platform.Tests;

/// <summary>Merging really changes files, so it is tested on real files in a sandbox folder.</summary>
public sealed class HardLinkMergerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tidywin-merge-" + Guid.NewGuid().ToString("N"));

    public HardLinkMergerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Make(string name, byte[] content)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, content);
        return p;
    }

    private static byte[] Data(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void Identical_files_become_one_and_each_path_still_opens_the_same_content()
    {
        var content = Data(9 * 1024 * 1024 + 17, 1); // crosses the compare buffer size
        var a = Make("a\\m.bin", content);
        var b = Make("b\\m.bin", content);
        Assert.False(HardLinkMerger.SameFile(a, b));

        Assert.Null(new HardLinkMerger().Merge(b, a));

        Assert.True(HardLinkMerger.SameFile(a, b));
        Assert.Equal(content, File.ReadAllBytes(a));
        Assert.Equal(content, File.ReadAllBytes(b));
        Assert.Empty(Directory.GetFiles(_dir, "*.tidywin-link", SearchOption.AllDirectories));

        // Removing one path must leave the other whole: that is what makes it safe for programs using either.
        File.Delete(a);
        Assert.Equal(content, File.ReadAllBytes(b));
    }

    [Fact]
    public void Same_name_and_size_but_different_content_is_refused_and_both_files_stay_as_they_were()
    {
        var x = Data(5_000_000, 1);
        var y = (byte[])x.Clone();
        y[4_999_999] ^= 0xFF; // differs only in the very last byte
        var a = Make("a\\m.bin", x);
        var b = Make("b\\m.bin", y);

        var error = new HardLinkMerger().Merge(b, a);

        Assert.NotNull(error);
        Assert.Contains("内容", error);
        Assert.False(HardLinkMerger.SameFile(a, b));
        Assert.Equal(x, File.ReadAllBytes(a));
        Assert.Equal(y, File.ReadAllBytes(b));
    }

    [Fact]
    public void Different_sizes_are_refused()
    {
        var a = Make("a.bin", Data(1000, 1));
        var b = Make("b.bin", Data(1001, 1));
        Assert.NotNull(new HardLinkMerger().Merge(b, a));
    }

    [Fact]
    public void A_missing_file_is_reported_not_thrown()
    {
        var a = Make("a.bin", Data(10, 1));
        Assert.NotNull(new HardLinkMerger().Merge(Path.Combine(_dir, "gone.bin"), a));
    }

    [Fact]
    public void Merging_something_already_merged_is_a_harmless_success()
    {
        var content = Data(100_000, 2);
        var a = Make("a.bin", content);
        var b = Make("b.bin", content);
        var m = new HardLinkMerger();
        Assert.Null(m.Merge(b, a));
        Assert.Null(m.Merge(b, a));
        Assert.True(HardLinkMerger.SameFile(a, b));
    }

    [Fact]
    public void A_duplicate_a_program_has_open_is_left_alone_and_no_leftover_is_kept()
    {
        var content = Data(100_000, 3);
        var a = Make("a.bin", content);
        var b = Make("b.bin", content);

        // Opened the way many programs open a model: no sharing of delete or write.
        using (new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = new HardLinkMerger().Merge(b, a);
            Assert.NotNull(error);
        }

        Assert.False(HardLinkMerger.SameFile(a, b));
        Assert.Equal(content, File.ReadAllBytes(b));
        Assert.Empty(Directory.GetFiles(_dir, "*.tidywin-link"));
    }

    [Fact]
    public void A_read_only_duplicate_can_be_merged()
    {
        var content = Data(50_000, 4);
        var a = Make("a.bin", content);
        var b = Make("b.bin", content);
        File.SetAttributes(b, FileAttributes.ReadOnly);

        Assert.Null(new HardLinkMerger().Merge(b, a));
        Assert.True(HardLinkMerger.SameFile(a, b));
    }
}

using TidyWin.Core.Paths;

namespace TidyWin.Core.Tests;

public class WindowsPathTests
{
    [Theory]
    [InlineData(@"c:\Users\a\..\b", @"C:\Users\b")]
    [InlineData(@"C:/Users/./x/", @"C:\Users\x")]
    [InlineData(@"\\?\C:\Temp\f", @"C:\Temp\f")]
    [InlineData(@"\\?\UNC\srv\share\d", @"\\srv\share\d")]
    [InlineData(@"C:\", @"C:\")]
    public void Normalize_makes_canonical_paths(string input, string expected) =>
        Assert.Equal(expected, WindowsPath.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\path")]
    [InlineData(@"C:foo")]
    [InlineData(@"C:\..\Windows")]
    [InlineData(@"\\onlyserver")]
    public void Normalize_rejects_unsafe_input(string? input) =>
        Assert.Null(WindowsPath.Normalize(input));

    [Fact]
    public void IsStrictlyUnder_excludes_the_root_and_lookalike_siblings()
    {
        Assert.True(WindowsPath.IsStrictlyUnder(@"C:\Cache\a", @"C:\Cache"));
        Assert.False(WindowsPath.IsStrictlyUnder(@"C:\Cache", @"C:\Cache"));
        Assert.False(WindowsPath.IsStrictlyUnder(@"C:\CacheEvil\a", @"C:\Cache"));
        Assert.True(WindowsPath.IsStrictlyUnder(@"c:\cache\A", @"C:\Cache"));
    }

    [Fact]
    public void ChainBelow_lists_each_step()
    {
        var chain = WindowsPath.ChainBelow(@"C:\Cache\a\b", @"C:\Cache");
        Assert.Equal(new[] { @"C:\Cache\a", @"C:\Cache\a\b" }, chain);
    }
}

public class PathGuardTests
{
    private sealed class Probe(params string[] reparse) : IReparseProbe
    {
        public bool IsReparsePoint(string path) =>
            reparse.Contains(path, StringComparer.OrdinalIgnoreCase);
    }

    private static PathGuard Make(params string[] reparse) => new(
        allowedRoots: new[] { @"C:\Users\me\AppData\Local\Temp" },
        protectedRoots: new[] { @"C:\Windows", @"C:\Users\me\AppData\Local\Temp\keep" },
        probe: new Probe(reparse));

    [Fact]
    public void Allows_a_file_inside_an_allowed_root() =>
        Assert.True(Make().Check(@"C:\Users\me\AppData\Local\Temp\a\b.tmp").IsAllowed);

    [Fact]
    public void Refuses_the_root_itself() =>
        Assert.Equal(GuardVerdict.OutsideAllowedRoots,
            Make().Check(@"C:\Users\me\AppData\Local\Temp").Verdict);

    [Fact]
    public void Refuses_dot_dot_escape() =>
        Assert.Equal(GuardVerdict.OutsideAllowedRoots,
            Make().Check(@"C:\Users\me\AppData\Local\Temp\..\Roaming\x").Verdict);

    [Fact]
    public void Refuses_protected_locations_even_inside_an_allowed_root() =>
        Assert.Equal(GuardVerdict.ProtectedLocation,
            Make().Check(@"C:\Users\me\AppData\Local\Temp\keep\x").Verdict);

    [Fact]
    public void Refuses_when_any_ancestor_is_a_reparse_point() =>
        Assert.Equal(GuardVerdict.ReparsePoint,
            Make(@"C:\Users\me\AppData\Local\Temp\link").Check(@"C:\Users\me\AppData\Local\Temp\link\deep\f").Verdict);

    [Fact]
    public void Refuses_invalid_input() =>
        Assert.Equal(GuardVerdict.InvalidPath, Make().Check(@"temp\x").Verdict);

    [Fact]
    public void A_drive_root_can_never_be_an_allowed_root() =>
        Assert.Throws<ArgumentException>(() =>
            new PathGuard(new[] { @"C:\" }, Array.Empty<string>(), new Probe()));
}

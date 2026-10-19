using TidyWin.Core.Update;
using Xunit;

namespace TidyWin.Core.Tests;

public class UpdateManifestTests
{
    private static readonly string Hash = new('a', 64);

    private static string Feed(string version = "1.2.0", string url = "https://example.com/Setup.exe", string? sha = null, string size = "1000", string notes = "\"fixes\"") =>
        $$"""{ "version": "{{version}}", "url": "{{url}}", "sha256": "{{sha ?? Hash}}", "size": {{size}}, "notes": {{notes}} }""";

    [Fact]
    public void A_good_feed_is_read()
    {
        var info = UpdateManifest.Parse(Feed(), "en", out var problem);
        Assert.Null(problem);
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 2, 0), info!.Version);
        Assert.Equal("https://example.com/Setup.exe", info.Url.ToString());
        Assert.Equal(1000, info.Size);
        Assert.Equal("fixes", info.Notes);
    }

    [Fact]
    public void The_hash_is_lowercased()
        => Assert.Equal(Hash, UpdateManifest.Parse(Feed(sha: Hash.ToUpperInvariant()), "en", out _)!.Sha256);

    [Theory]
    [InlineData("http://example.com/Setup.exe")]       // plain http
    [InlineData("ftp://example.com/Setup.exe")]
    [InlineData("file:///C:/Setup.exe")]
    [InlineData("https://user:pw@example.com/a.exe")]  // credentials in the address
    [InlineData("//example.com/a.exe")]
    [InlineData("not a url")]
    public void Only_plain_https_addresses_are_accepted(string url)
        => Assert.Null(UpdateManifest.Parse(Feed(url: url), "en", out _));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_missing_or_bad_checksum_is_refused(string sha)
        => Assert.Null(UpdateManifest.Parse(Feed(sha: sha), "en", out _));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("999999999999")]
    [InlineData("\"big\"")]
    public void A_missing_or_absurd_size_is_refused(string size)
        => Assert.Null(UpdateManifest.Parse(Feed(size: size), "en", out _));

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"version\":\"x\"}")]
    public void Garbage_is_refused_without_throwing(string json)
        => Assert.Null(UpdateManifest.Parse(json, "en", out var problem));

    [Fact]
    public void An_oversized_feed_is_refused()
        => Assert.Null(UpdateManifest.Parse(Feed(notes: "\"" + new string('x', UpdateManifest.MaxFeedBytes) + "\""), "en", out _));

    [Fact]
    public void Notes_follow_the_language_and_fall_back_to_english()
    {
        var feed = Feed(notes: """{ "zh": "修复", "en": "Fixes" }""");
        Assert.Equal("修复", UpdateManifest.Parse(feed, "zh", out _)!.Notes);
        Assert.Equal("Fixes", UpdateManifest.Parse(feed, "en", out _)!.Notes);
        Assert.Equal("Fixes", UpdateManifest.Parse(feed, "fr", out _)!.Notes);
    }

    [Fact]
    public void Long_notes_are_cut()
        => Assert.True(UpdateManifest.Parse(Feed(notes: "\"" + new string('n', 5000) + "\""), "en", out _)!.Notes.Length <= 2001);

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.1", true)]
    [InlineData("1.9.0", "1.10.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0", "1.0.0.0", false)]
    [InlineData("2.0.0", "1.9.9", false)]
    public void Newer_means_strictly_newer(string current, string offered, bool expected)
        => Assert.Equal(expected, UpdateManifest.IsNewer(Version.Parse(current), Version.Parse(offered)));

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2", true)]
    [InlineData(" 1.2.3 ", true)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData("1.2.3-beta", false)]
    public void Versions_are_parsed_strictly(string text, bool ok)
        => Assert.Equal(ok, UpdateManifest.TryVersion(text, out _));
}

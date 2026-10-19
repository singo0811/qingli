using System.Net;
using System.Security.Cryptography;
using System.Text;
using TidyWin.Core.Update;
using TidyWin.Platform;
using Xunit;

namespace TidyWin.Platform.Tests;

public class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tidywin-upd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var r = reply(request);
            r.RequestMessage = request;
            return Task.FromResult(r);
        }
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string FeedJson(string version, byte[] installer, string url = "https://example.com/Setup.exe") =>
        $$"""{ "version": "{{version}}", "url": "{{url}}", "sha256": "{{Sha(installer)}}", "size": {{installer.Length}}, "notes": "n" }""";

    [Fact]
    public async Task Nothing_is_contacted_without_a_feed()
    {
        var fake = new Fake(_ => Ok([]));
        var r = await new Updater(fake, _dir).CheckAsync("", new Version(1, 0, 0), "en");
        Assert.Equal(UpdateState.NotConfigured, r.State);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task A_plain_http_feed_is_refused_before_any_request()
    {
        var fake = new Fake(_ => Ok([]));
        var r = await new Updater(fake, _dir).CheckAsync("http://example.com/feed.json", new Version(1, 0, 0), "en");
        Assert.Equal(UpdateState.Failed, r.State);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task A_newer_version_is_reported_and_the_same_one_is_not()
    {
        var installer = Encoding.ASCII.GetBytes("installer");
        var fake = new Fake(_ => Ok(Encoding.UTF8.GetBytes(FeedJson("1.1.0", installer))));
        var u = new Updater(fake, _dir);
        Assert.Equal(UpdateState.Available, (await u.CheckAsync("https://example.com/f.json", new Version(1, 0, 0), "en")).State);
        Assert.Equal(UpdateState.UpToDate, (await u.CheckAsync("https://example.com/f.json", new Version(1, 1, 0), "en")).State);
    }

    [Fact]
    public async Task A_server_error_or_garbage_is_a_failure_not_a_crash()
    {
        var down = new Updater(new Fake(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)), _dir);
        Assert.Equal(UpdateState.Failed, (await down.CheckAsync("https://example.com/f.json", new Version(1, 0, 0), "en")).State);

        var junk = new Updater(new Fake(_ => Ok("<html>nope</html>"u8.ToArray())), _dir);
        Assert.Equal(UpdateState.Failed, (await junk.CheckAsync("https://example.com/f.json", new Version(1, 0, 0), "en")).State);
    }

    [Fact]
    public async Task A_huge_feed_is_cut_off()
    {
        var u = new Updater(new Fake(_ => Ok(new byte[UpdateManifest.MaxFeedBytes + 10])), _dir);
        Assert.Equal(UpdateState.Failed, (await u.CheckAsync("https://example.com/f.json", new Version(1, 0, 0), "en")).State);
    }

    [Fact]
    public async Task A_matching_download_is_kept_under_its_final_name()
    {
        var data = new byte[200_000];
        new Random(1).NextBytes(data);
        var info = UpdateManifest.Parse(FeedJson("1.1.0", data), "en", out _)!;
        var path = await new Updater(new Fake(_ => Ok(data)), _dir).DownloadAsync(info);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.EndsWith("TidyWin-Setup-1.1.0.exe", path);
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }

    [Fact]
    public async Task A_download_with_the_wrong_checksum_is_deleted_and_refused()
    {
        var good = Encoding.ASCII.GetBytes("the real installer");
        var evil = Encoding.ASCII.GetBytes("the fake installer"); // same length, different content
        var info = UpdateManifest.Parse(FeedJson("1.1.0", good), "en", out _)!;
        await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(new Fake(_ => Ok(evil)), _dir).DownloadAsync(info));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_download_bigger_than_promised_is_stopped()
    {
        var small = new byte[100];
        var info = UpdateManifest.Parse(FeedJson("1.1.0", small), "en", out _)!;
        await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(new Fake(_ => Ok(new byte[5000])), _dir).DownloadAsync(info));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_download_smaller_than_promised_is_refused()
    {
        var full = new byte[5000];
        var info = UpdateManifest.Parse(FeedJson("1.1.0", full), "en", out _)!;
        await Assert.ThrowsAsync<InvalidDataException>(() => new Updater(new Fake(_ => Ok(new byte[10])), _dir).DownloadAsync(info));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Old_installers_are_cleaned_up()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "TidyWin-Setup-1.0.0.exe"), "x");
        File.WriteAllText(Path.Combine(_dir, "other.txt"), "x");
        new Updater(new Fake(_ => Ok([])), _dir).CleanOld();
        Assert.False(File.Exists(Path.Combine(_dir, "TidyWin-Setup-1.0.0.exe")));
        Assert.True(File.Exists(Path.Combine(_dir, "other.txt")));
        await Task.CompletedTask;
    }

    [Fact]
    public void Settings_round_trip_and_reject_a_plain_http_feed()
    {
        var path = Path.Combine(_dir, "settings.json");
        var s = new AppSettings { UpdateFeed = "https://example.com/f.json", AutoCheckUpdates = false };
        Assert.True(s.Save(path));
        var back = AppSettings.Load(path);
        Assert.Equal("https://example.com/f.json", back.UpdateFeed);
        Assert.False(back.AutoCheckUpdates);

        File.WriteAllText(path, """{ "updateFeed": "http://evil.example/f.json" }""");
        Assert.Equal("", AppSettings.Load(path).UpdateFeed);
    }
}

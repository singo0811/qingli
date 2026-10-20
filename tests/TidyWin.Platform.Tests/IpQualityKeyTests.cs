using TidyWin.Platform;

namespace TidyWin.Platform.Tests;

public sealed class IpQualityKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tidywin-key-" + Guid.NewGuid().ToString("N"));
    private readonly string? _env = Environment.GetEnvironmentVariable("TIDYWIN_IPAPI_KEY");

    public IpQualityKeyTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("TIDYWIN_IPAPI_KEY", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TIDYWIN_IPAPI_KEY", _env);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void No_key_means_empty() => Assert.Equal("", AppSettings.IpQualityKey(new AppSettings()));

    [Fact]
    public void A_plain_key_in_the_settings_is_used_and_trimmed() =>
        Assert.Equal("abc_DEF-123", AppSettings.IpQualityKey(new AppSettings { IpQualityKeyText = "  abc_DEF-123 \n" }));

    [Theory]
    [InlineData("key with space")]
    [InlineData("key\r\nX-Evil: 1")]
    [InlineData("键")]
    [InlineData("a/b")]
    [InlineData("a&b=c")]
    public void A_key_with_odd_characters_is_refused_so_it_cannot_change_the_request(string text) =>
        Assert.Equal("", AppSettings.IpQualityKey(new AppSettings { IpQualityKeyText = text }));

    [Fact]
    public void A_very_long_key_is_refused() =>
        Assert.Equal("", AppSettings.IpQualityKey(new AppSettings { IpQualityKeyText = new string('a', 129) }));

    [Fact]
    public void The_environment_variable_wins()
    {
        Environment.SetEnvironmentVariable("TIDYWIN_IPAPI_KEY", "fromenv");
        Assert.Equal("fromenv", AppSettings.IpQualityKey(new AppSettings { IpQualityKeyText = "fromfile" }));
    }

    [Fact]
    public void The_key_survives_a_save_and_load_and_other_saves()
    {
        var path = Path.Combine(_dir, "settings.json");
        new AppSettings { IpQualityKeyText = "k123" }.Save(path);
        var again = AppSettings.Load(path);
        Assert.Equal("k123", again.IpQualityKeyText);
        again.Outfit = "llama";
        again.Save(path);
        Assert.Equal("k123", AppSettings.Load(path).IpQualityKeyText);
    }
}

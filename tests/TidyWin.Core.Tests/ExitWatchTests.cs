using TidyWin.Core.Security;

namespace TidyWin.Core.Tests;

public class ExitWatchTests
{
    [Theory]
    [InlineData("CN", true)]
    [InlineData("cn", true)]
    [InlineData("CHN", true)]
    [InlineData("China", true)]
    [InlineData("中国", true)]
    [InlineData("中國", true)]
    [InlineData("HK", false)]
    [InlineData("TW", false)]
    [InlineData("US", false)]
    [InlineData("CNA", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_mainland_China_counts(string? country, bool china) =>
        Assert.Equal(china, ExitWatch.IsChina(country));

    [Fact]
    public void A_working_proxy_hides_the_bare_exit() =>
        Assert.Equal("US", ExitWatch.CountrySeen(proxyOn: true, viaProxy: "US", direct: "CN"));

    [Fact]
    public void A_dead_proxy_falls_back_to_the_bare_exit() =>
        Assert.Equal("CN", ExitWatch.CountrySeen(proxyOn: true, viaProxy: null, direct: "CN"));

    [Fact]
    public void No_answer_at_all_cannot_be_judged() =>
        Assert.Null(ExitWatch.CountrySeen(proxyOn: true, viaProxy: "  ", direct: null));

    [Theory]
    [InlineData(false, "US", ExitWatchState.Offline)]
    [InlineData(true, null, ExitWatchState.Unknown)]
    [InlineData(true, "", ExitWatchState.Unknown)]
    [InlineData(true, "CN", ExitWatchState.China)]
    [InlineData(true, "JP", ExitWatchState.Clear)]
    public void Reading_never_treats_silence_as_safe_or_as_China(bool up, string? country, ExitWatchState state) =>
        Assert.Equal(state, ExitWatch.Read(up, country));

    [Fact]
    public void The_first_bad_reading_alerts_and_a_repeat_does_not()
    {
        var down = ExitWatch.Step(ExitWatchState.Unknown, ExitWatchState.Offline);
        Assert.Equal(ExitAlert.Offline, down.Alert);

        var still = ExitWatch.Step(down.State, ExitWatchState.Offline);
        Assert.Equal(ExitAlert.None, still.Alert);
        Assert.Equal(ExitWatchState.Offline, still.State);
    }

    [Fact]
    public void Becoming_Chinese_alerts_once_until_it_recovers_and_happens_again()
    {
        var clear = ExitWatch.Step(ExitWatchState.Unknown, ExitWatchState.Clear);
        Assert.Equal(ExitAlert.None, clear.Alert);

        var china = ExitWatch.Step(clear.State, ExitWatchState.China);
        Assert.Equal(ExitAlert.China, china.Alert);

        var again = ExitWatch.Step(china.State, ExitWatchState.China);
        Assert.Equal(ExitAlert.None, again.Alert);

        var back = ExitWatch.Step(again.State, ExitWatchState.Clear);
        Assert.Equal(ExitAlert.None, back.Alert);

        Assert.Equal(ExitAlert.China, ExitWatch.Step(back.State, ExitWatchState.China).Alert);
    }

    [Fact]
    public void A_failed_lookup_does_not_forget_a_known_state_or_alert()
    {
        var step = ExitWatch.Step(ExitWatchState.China, ExitWatchState.Unknown);
        Assert.Equal(ExitWatchState.China, step.State);
        Assert.Equal(ExitAlert.None, step.Alert);
    }

    [Fact]
    public void Offline_and_China_are_different_alerts()
    {
        var down = ExitWatch.Step(ExitWatchState.Clear, ExitWatchState.Offline);
        Assert.Equal(ExitAlert.Offline, down.Alert);
        Assert.Equal(ExitAlert.China, ExitWatch.Step(down.State, ExitWatchState.China).Alert);
    }

    [Fact]
    public void Another_foreign_exit_is_not_an_alert() =>
        Assert.Equal(ExitAlert.None, ExitWatch.Step(ExitWatchState.Clear, ExitWatchState.Clear).Alert);

    [Fact]
    public void Status_names_the_bad_cases()
    {
        Assert.Contains("Claude", ExitWatch.Status(ExitWatchState.Offline, null, null));
        Assert.Contains("1.2.3.4", ExitWatch.Status(ExitWatchState.China, "1.2.3.4", "cn"));
        Assert.Contains("JP", ExitWatch.Status(ExitWatchState.Clear, "8.8.8.8", "jp"));
        Assert.Equal("?", ExitWatch.Where(null, null));
    }
}

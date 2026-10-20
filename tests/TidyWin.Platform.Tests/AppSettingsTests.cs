using TidyWin.Platform;

namespace TidyWin.Platform.Tests;

public sealed class AppSettingsMemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tidywin-settings-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Dates_and_snoozes_survive_a_round_trip()
    {
        var now = new DateTime(2026, 10, 17, 8, 30, 0, DateTimeKind.Utc);
        var a = new AppSettings { Outfit = "llama" };
        a.RememberClean(now.AddDays(-2), 123456);
        a.LastVisitUtc = now;
        a.DiskFreeAtLastVisit = 40L << 30;
        a.Snooze("disk.low", now.AddDays(1), now);

        Assert.True(a.Save(File_));
        var b = AppSettings.Load(File_);

        Assert.Equal(now, b.LastVisitUtc);
        Assert.Equal(DateTimeKind.Utc, b.LastVisitUtc!.Value.Kind);
        Assert.Equal(now.AddDays(-2), b.LastCleanUtc);
        Assert.Equal(123456, b.LastCleanBytes);
        Assert.Equal(40L << 30, b.DiskFreeAtLastVisit);
        Assert.Contains("disk.low", b.ActiveSnoozes(now));
        Assert.DoesNotContain("disk.low", b.ActiveSnoozes(now.AddDays(2)));
    }

    [Fact]
    public void A_local_time_in_the_file_is_read_as_the_same_instant()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{\"lastVisitUtc\":\"2026-10-17T16:30:00+08:00\"}");
        var s = AppSettings.Load(File_);
        Assert.Equal(new DateTime(2026, 10, 17, 8, 30, 0, DateTimeKind.Utc), s.LastVisitUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"lastVisitUtc\":\"yesterday-ish\",\"snoozed\":{\"a\":\"??\"},\"diskFreeAtLastVisit\":-5}")]
    [InlineData("{\"lastVisitUtc\":12,\"snoozed\":[],\"outfit\":7}")]
    public void A_damaged_file_gives_defaults_and_never_throws(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, content);

        var s = AppSettings.Load(File_);

        Assert.NotNull(s);
        Assert.Null(s.LastVisitUtc);
        Assert.Null(s.DiskFreeAtLastVisit);
        Assert.Empty(s.SnoozedUntil);
    }

    [Fact]
    public void A_missing_file_gives_defaults()
    {
        var s = AppSettings.Load(File_);
        Assert.Null(s.LastCleanUtc);
        Assert.True(s.WatchExit);
    }

    [Fact]
    public void Turning_the_exit_watch_off_is_remembered_and_turning_it_on_clears_that()
    {
        var off = new AppSettings { WatchExit = false };
        Assert.True(off.Save(File_));
        Assert.False(AppSettings.Load(File_).WatchExit);
        var on = AppSettings.Load(File_);
        on.WatchExit = true;
        Assert.True(on.Save(File_));
        Assert.True(AppSettings.Load(File_).WatchExit);
        Assert.DoesNotContain("watchExit", File.ReadAllText(File_));
    }

    [Fact]
    public void Expired_snoozes_are_dropped_when_a_new_one_is_added()
    {
        var now = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
        var s = new AppSettings();
        s.Snooze("old", now.AddDays(-1), now.AddDays(-2));
        s.Snooze("new", now.AddDays(1), now);
        Assert.DoesNotContain("old", s.SnoozedUntil.Keys);
        Assert.Contains("new", s.SnoozedUntil.Keys);
    }
}

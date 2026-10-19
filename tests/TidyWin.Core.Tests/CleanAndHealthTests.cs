using TidyWin.Core.Clean;
using TidyWin.Core.Status;

namespace TidyWin.Core.Tests;

public class WholeFolderJudgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static FileFacts Old(string name = "a") =>
        new(@"C:\c\" + name, Now.AddDays(-40), false, false);

    private static FolderVerdict Judge(IEnumerable<FileFacts> files, int? days = 30, Func<bool>? over = null, int max = 2000) =>
        WholeFolderJudge.Judge(files, Now, days, over ?? (() => false), max);

    [Fact] public void Old_files_pass() => Assert.Equal(FolderVerdict.SafeToRemove, Judge(new[] { Old("a"), Old("b") }));
    [Fact] public void No_age_rule_means_no_removal() => Assert.Equal(FolderVerdict.NoAgeRule, Judge(new[] { Old() }, days: null));
    [Fact] public void Zero_days_is_not_a_rule() => Assert.Equal(FolderVerdict.NoAgeRule, Judge(new[] { Old() }, days: 0));
    [Fact] public void Empty_folder_is_reported() => Assert.Equal(FolderVerdict.Empty, Judge(Array.Empty<FileFacts>()));

    [Fact]
    public void One_new_file_keeps_the_whole_folder() =>
        Assert.Equal(FolderVerdict.TooNew,
            Judge(new[] { Old(), new FileFacts(@"C:\c\n", Now.AddDays(-1), false, false) }));

    [Fact]
    public void A_file_exactly_at_the_cutoff_is_old_enough() =>
        Assert.Equal(FolderVerdict.SafeToRemove,
            Judge(new[] { new FileFacts(@"C:\c\e", Now.AddDays(-30), false, false) }));

    [Fact]
    public void Read_only_keeps_the_folder() =>
        Assert.Equal(FolderVerdict.ReadOnly, Judge(new[] { new FileFacts(@"C:\c\r", Now.AddDays(-90), true, false) }));

    [Fact]
    public void Open_file_keeps_the_folder() =>
        Assert.Equal(FolderVerdict.InUse, Judge(new[] { new FileFacts(@"C:\c\o", Now.AddDays(-90), false, true) }));

    [Fact]
    public void Too_many_files_stops_early() =>
        Assert.Equal(FolderVerdict.TooManyFiles, Judge(Enumerable.Range(0, 5).Select(i => Old(i.ToString())), max: 3));

    [Fact]
    public void Budget_overrun_stops_early() =>
        Assert.Equal(FolderVerdict.OverBudget, Judge(new[] { Old() }, over: () => true));
}

public class HealthScoreTests
{
    private static HealthInputs Calm() => new(10, 40, 50, 95, 2, false);

    [Fact]
    public void A_calm_machine_scores_100()
    {
        var r = HealthScore.Assess(Calm());
        Assert.Equal(100, r.Score);
        Assert.Equal(HealthBand.Excellent, r.Band);
        Assert.Empty(r.Deductions);
    }

    [Fact]
    public void Every_lost_point_has_a_code()
    {
        var r = HealthScore.Assess(Calm() with { SystemDiskUsedPercent = 96, MemoryPercent = 93 });
        Assert.Equal(55, r.Score);
        Assert.Equal(new[] { "memory.critical", "disk.critical" }, r.Deductions.Select(x => x.Code));
        Assert.Equal(100 - r.Score, r.Deductions.Sum(x => x.Points));
        Assert.Equal(HealthBand.Fair, r.Band);
    }

    [Theory]
    [InlineData(85, HealthBand.Excellent)]
    [InlineData(84, HealthBand.Good)]
    [InlineData(65, HealthBand.Good)]
    [InlineData(64, HealthBand.Fair)]
    [InlineData(45, HealthBand.Fair)]
    [InlineData(44, HealthBand.Poor)]
    public void Band_edges(int score, HealthBand expected) =>
        Assert.Equal(expected, HealthScore.BandFor(score));

    [Fact]
    public void Worst_case_keeps_a_floor_and_is_poor()
    {
        var r = HealthScore.Assess(new HealthInputs(99, 99, 99, 10, 99, true));
        Assert.Equal(25, r.Score);
        Assert.Equal(HealthBand.Poor, r.Band);
    }

    [Fact]
    public void Unknown_battery_is_not_penalised() =>
        Assert.Equal(100, HealthScore.Assess(Calm() with { BatteryHealthPercent = null }).Score);
}

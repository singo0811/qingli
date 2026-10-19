using TidyWin.Core.Insights;
using TidyWin.Core.Mascot;

namespace TidyWin.Core.Tests;

public class InsightFinderTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static Facts Full(params AiTool[] tools) => new()
    {
        NowUtc = Now,
        DiskTotalBytes = 500L << 30,
        DiskFreeBytes = 20L << 30,
        DiskUsedPercent = 96,
        AiToolsInUse = tools,
    };

    private static Insight Find(Facts f, string id) => InsightEngine.Evaluate(f).Single(i => i.Id == id);

    [Fact]
    public void Full_disk_with_an_AI_tool_points_to_the_finder()
    {
        var i = Find(Full(AiTool.ComfyUi), "disk.low");
        Assert.Equal(Destination.Finder, i.Go);
        Assert.Contains("ComfyUI", i.Detail);
    }

    [Fact]
    public void Full_disk_without_AI_tools_keeps_the_old_advice()
    {
        Assert.NotEqual(Destination.Finder, Find(Full(), "disk.low").Go);
    }

    [Fact]
    public void Plenty_of_safe_system_junk_is_still_cleaned_first()
    {
        var i = Find(Full(AiTool.Ollama) with { SystemSafeBytes = 6L << 30 }, "disk.low");
        Assert.Equal(Destination.SystemClean, i.Go);
    }

    [Fact]
    public void Shrinking_disk_with_an_AI_tool_points_to_the_finder()
    {
        var f = new Facts
        {
            NowUtc = Now,
            DiskTotalBytes = 500L << 30,
            DiskFreeBytes = 200L << 30,
            DiskUsedPercent = 60,
            AiToolsInUse = [AiTool.LmStudio],
            LastVisitUtc = Now.AddDays(-3),
            DiskFreeAtLastVisit = 240L << 30,
        };
        var i = Find(f, "disk.shrinking");
        Assert.Equal(Destination.Finder, i.Go);
        Assert.Contains("LM Studio", i.Detail);
    }

    [Fact]
    public void Snoozed_disk_suggestion_stays_quiet()
    {
        var list = InsightEngine.Evaluate(Full(AiTool.ComfyUi), new HashSet<string> { "disk.low" });
        Assert.DoesNotContain(list, i => i.Id == "disk.low");
    }
}

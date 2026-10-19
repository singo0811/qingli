using TidyWin.Core.Insights;
using TidyWin.Core.Mascot;
using TidyWin.Platform;

namespace TidyWin.Platform.Tests;

public class FactsCollectorTests
{
    [Fact]
    public void Fast_facts_read_the_real_machine_without_throwing()
    {
        var f = FactsCollector.CollectFast(new AppSettings(), [AiTool.Ollama], DateTime.UtcNow);

        Assert.True(f.DiskTotalBytes > 0);
        Assert.InRange(f.DiskUsedPercent, 0, 100);
        Assert.InRange(f.MemoryPercent, 1, 100);
        Assert.True(f.UptimeDays > 0);
        Assert.True(f.StartupEnabled >= 0);
        Assert.Equal([AiTool.Ollama], f.AiToolsInUse);
    }

    [Fact]
    public void Settings_memory_flows_into_the_facts()
    {
        var now = DateTime.UtcNow;
        var s = new AppSettings { LastVisitUtc = now.AddDays(-2), DiskFreeAtLastVisit = 50L << 30 };
        s.RememberClean(now.AddDays(-5), 4096);

        var f = FactsCollector.CollectFast(s, [], now);

        Assert.Equal(s.LastVisitUtc, f.LastVisitUtc);
        Assert.Equal(50L << 30, f.DiskFreeAtLastVisit);
        Assert.Equal(4096, f.LastCleanBytes);
    }

    [Fact]
    public void The_engine_can_always_evaluate_what_was_collected()
    {
        var f = FactsCollector.CollectFast(new AppSettings(), [], DateTime.UtcNow);
        var list = InsightEngine.Evaluate(f);
        Assert.NotEmpty(list); // at worst the single "everything is fine" entry
    }
}

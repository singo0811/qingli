using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Memory;
using TidyWin.Core.Security;

namespace TidyWin.Core.Tests;

public class ResidentTests
{
    [Theory]
    [InlineData("claude", true)]
    [InlineData("Claude", true)]
    [InlineData("claude.exe", true)]
    [InlineData("Claude.exe", true)]
    [InlineData("claude-desktop", false)]
    [InlineData("node", false)]
    [InlineData("Code", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_process_named_Claude_is_closed(string? name, bool match) =>
        Assert.Equal(match, ClaudeStop.IsClaude(name));

    [Fact]
    public void Automatic_cleaning_keeps_only_caches_that_rebuild_themselves()
    {
        var all = SystemCatalog.All.Concat(AiCatalog.All).ToList();
        var picked = AutoClean.Regenerable(all);

        Assert.NotEmpty(picked);
        Assert.All(picked, t => Assert.Equal(AiSafety.Regenerable, t.Safety));
        Assert.Contains(all, t => t.Safety == AiSafety.ReDownloadable);
        Assert.Contains(all, t => t.Safety == AiSafety.UserChoice);
        Assert.Contains(all, t => t.Safety == AiSafety.Never);
        Assert.DoesNotContain(picked, t => t.Id is "hf.hub" or "npm.cache");
    }

    [Fact]
    public void The_first_memory_reading_trims_nothing_and_a_busy_program_is_left_alone()
    {
        var policy = new ProtectionPolicy(@"C:\Windows", selfPid: 50);
        var trim = new IdleTrim();
        var t0 = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var idle = Proc("notepad", 100, 200L << 20, 1);
        var busy = Proc("chrome", 200, 400L << 20, 1);
        var system = Proc("explorer", 300, 200L << 20, 1);

        Assert.Empty(trim.Candidates(Snap(t0, idle, busy, system), policy, 8));

        idle = idle with { CpuSeconds = 1.01 };
        busy = busy with { CpuSeconds = 1 + 8 * 30 }; // used the whole machine over those 30 seconds
        var next = trim.Candidates(Snap(t0.AddSeconds(30), idle, busy, system), policy, 8);

        Assert.Equal("notepad", Assert.Single(next).Name);
    }

    private static ProcInfo Proc(string name, int pid, long bytes, double cpu) =>
        new(pid, name, null, 1, bytes, bytes, cpu, false, null);

    private static MemorySnapshot Snap(DateTime at, params ProcInfo[] processes) =>
        new(at, new SystemMemory(16L << 30, 8L << 30, 0, 4L << 30, 32L << 30), processes, null);
}

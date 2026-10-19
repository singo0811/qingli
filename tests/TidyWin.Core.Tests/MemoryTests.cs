using TidyWin.Core.Memory;
using Xunit;

namespace TidyWin.Core.Tests;

public class MemoryTests
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static SystemMemory Sys(double availPercent, double commitPercent = 50) =>
        new(16 * GB, (long)Math.Ceiling(16 * GB * availPercent / 100), 2 * GB, (long)(100 * commitPercent), 10000);

    private static ProcInfo P(int pid, string name, long ws = 300 * MB, double cpu = 10, bool window = false,
        int session = 1, string? path = null, long? priv = null) =>
        new(pid, name, path ?? $@"C:\Program Files\{name}\{name}.exe", session, ws, priv ?? ws, cpu, window, window ? name + " window" : null);

    private static MemorySnapshot Snap(DateTime at, IEnumerable<ProcInfo> procs, int? fg = null, double avail = 50) =>
        new(at, Sys(avail), procs.ToList(), fg);

    private static readonly ProtectionPolicy Policy = new(@"C:\Windows", selfPid: 999);

    private static MemoryAnalysis Analyze(MemorySnapshot cur, MemorySnapshot? prev, ActivityTracker? act = null, int cores = 1) =>
        MemoryAdvisor.Analyze(cur, prev, Policy, new MemoryOptions(Cores: cores), act ?? new ActivityTracker());

    // ---- pressure ----

    [Theory]
    [InlineData(60, MemoryPressure.Calm)]
    [InlineData(40, MemoryPressure.Calm)]
    [InlineData(39.9, MemoryPressure.Moderate)]
    [InlineData(20, MemoryPressure.Moderate)]
    [InlineData(19, MemoryPressure.High)]
    [InlineData(10, MemoryPressure.High)]
    [InlineData(9, MemoryPressure.Critical)]
    public void Pressure_from_available_memory(double avail, MemoryPressure expected) =>
        Assert.Equal(expected, MemoryAdvisor.PressureOf(Sys(avail)));

    [Fact]
    public void Nearly_full_commit_raises_pressure_even_when_ram_looks_free()
    {
        Assert.Equal(MemoryPressure.High, MemoryAdvisor.PressureOf(Sys(70, commitPercent: 91)));
        Assert.Equal(MemoryPressure.Critical, MemoryAdvisor.PressureOf(Sys(70, commitPercent: 96)));
    }

    // ---- protection ----

    [Theory]
    [InlineData("System", 4, 0, null)]
    [InlineData("svchost.exe", 800, 0, @"C:\Windows\System32\svchost.exe")]
    [InlineData("explorer.exe", 5000, 1, @"C:\Windows\explorer.exe")]
    [InlineData("MsMpEng.exe", 2500, 0, @"C:\ProgramData\Microsoft\Windows Defender\MsMpEng.exe")]
    [InlineData("notepad.exe", 6000, 1, @"C:\Windows\System32\notepad.exe")]
    [InlineData("TidyWin.exe", 999, 1, @"D:\Apps\TidyWin.exe")]
    public void System_security_shell_and_self_are_protected(string name, int pid, int session, string? path) =>
        Assert.True(Policy.IsProtected(P(pid, name, session: session, path: path)));

    [Fact]
    public void An_ordinary_user_app_is_not_protected() =>
        Assert.False(Policy.IsProtected(P(7000, "chrome.exe", path: @"C:\Program Files\Google\Chrome\chrome.exe")));

    [Fact]
    public void A_look_alike_folder_does_not_borrow_windows_protection()
    {
        // "C:\WindowsApps" is not inside "C:\Windows".
        Assert.False(Policy.IsProtected(P(7001, "thing.exe", path: @"C:\WindowsApps\thing.exe")));
    }

    // ---- grouping ----

    [Fact]
    public void Processes_of_one_program_become_one_group_biggest_first()
    {
        var snap = Snap(T0, [P(1, "chrome.exe", 400 * MB), P(2, "chrome.exe", 600 * MB), P(3, "code.exe", 500 * MB)]);
        var a = Analyze(snap, null);

        Assert.Equal(["chrome", "code"], a.Groups.Select(g => g.Name).ToArray());
        Assert.Equal(2, a.Groups[0].Count);
        Assert.Equal(1000 * MB, a.Groups[0].WorkingSetBytes);
    }

    [Fact]
    public void Groups_are_ordered_by_the_working_set_that_the_list_displays()
    {
        // "big-private" has more private memory but a smaller working set: the list must follow what it shows.
        var snap = Snap(T0, [P(1, "big-private.exe", ws: 200 * MB, priv: 900 * MB), P(2, "big-ws.exe", ws: 500 * MB, priv: 100 * MB)]);
        Assert.Equal(["big-ws", "big-private"], Analyze(snap, null).Groups.Select(g => g.Name).ToArray());
    }

    // ---- trim candidates ----

    private static (MemorySnapshot prev, MemorySnapshot cur) Pair(Func<double, ProcInfo[]> make, double cpuDelta, double seconds = 10)
    {
        var prev = Snap(T0, make(100));
        var cur = Snap(T0.AddSeconds(seconds), make(100 + cpuDelta));
        return (prev, cur);
    }

    [Fact]
    public void Idle_background_program_with_enough_memory_is_a_candidate()
    {
        var (prev, cur) = Pair(c => [P(10, "slack.exe", 500 * MB, cpu: c)], cpuDelta: 0.0);
        var a = Analyze(cur, prev);

        Assert.Equal(["slack"], a.TrimCandidates.Select(g => g.Name).ToArray());
        Assert.Equal(500 * MB, a.TrimCandidateBytes);
    }

    [Fact]
    public void Without_a_previous_reading_nothing_is_called_idle()
    {
        var a = Analyze(Snap(T0, [P(10, "slack.exe", 500 * MB)]), null);
        Assert.Empty(a.TrimCandidates);
    }

    [Fact]
    public void A_program_using_cpu_is_not_a_candidate()
    {
        var (prev, cur) = Pair(c => [P(10, "render.exe", 900 * MB, cpu: c)], cpuDelta: 5.0); // 50% of one core
        Assert.Empty(Analyze(cur, prev).TrimCandidates);
    }

    [Fact]
    public void Cpu_is_divided_by_core_count()
    {
        var (prev, cur) = Pair(c => [P(10, "x.exe", 500 * MB, cpu: c)], cpuDelta: 0.8); // 8% of one core, 1% of 8 cores...
        Assert.Empty(Analyze(cur, prev, cores: 1).TrimCandidates);   // 8% -> busy
        Assert.Single(Analyze(cur, prev, cores: 16).TrimCandidates); // 0.5% -> idle
    }

    [Fact]
    public void The_program_in_front_is_never_a_candidate()
    {
        var prev = Snap(T0, [P(10, "word.exe", 800 * MB, cpu: 1, window: true)]);
        var cur = Snap(T0.AddSeconds(10), [P(10, "word.exe", 800 * MB, cpu: 1, window: true)], fg: 10);
        Assert.Empty(Analyze(cur, prev).TrimCandidates);
    }

    [Fact]
    public void A_program_used_a_minute_ago_is_left_alone_but_one_used_long_ago_is_not()
    {
        var prev = Snap(T0, [P(10, "word.exe", 800 * MB, cpu: 1)]);
        var cur = Snap(T0.AddSeconds(10), [P(10, "word.exe", 800 * MB, cpu: 1)]);

        var recent = new ActivityTracker();
        recent.Observe(T0.AddSeconds(-60), "word");
        Assert.Empty(Analyze(cur, prev, recent).TrimCandidates);

        var old = new ActivityTracker();
        old.Observe(T0.AddMinutes(-30), "word");
        Assert.Single(Analyze(cur, prev, old).TrimCandidates);
    }

    [Fact]
    public void Small_programs_are_not_worth_trimming()
    {
        var (prev, cur) = Pair(c => [P(10, "tiny.exe", 40 * MB, cpu: c)], 0);
        Assert.Empty(Analyze(cur, prev).TrimCandidates);
    }

    [Fact]
    public void Protected_programs_are_never_candidates_however_idle_and_big()
    {
        var (prev, cur) = Pair(c => [P(800, "svchost.exe", 900 * MB, cpu: c, session: 0, path: @"C:\Windows\System32\svchost.exe")], 0);
        Assert.Empty(Analyze(cur, prev).TrimCandidates);
    }

    [Fact]
    public void A_group_with_one_unreadable_process_is_unknown_not_idle()
    {
        var prev = Snap(T0, [P(10, "app.exe", 300 * MB, cpu: 1), P(11, "app.exe", 300 * MB, cpu: -1)]);
        var cur = Snap(T0.AddSeconds(10), [P(10, "app.exe", 300 * MB, cpu: 1), P(11, "app.exe", 300 * MB, cpu: -1)]);

        var a = Analyze(cur, prev);

        Assert.Null(a.Groups.Single().CpuPercent);
        Assert.Empty(a.TrimCandidates);
    }

    [Fact]
    public void A_reused_pid_with_another_name_does_not_inherit_the_old_cpu_reading()
    {
        var prev = Snap(T0, [P(10, "old.exe", 300 * MB, cpu: 5000)]);
        var cur = Snap(T0.AddSeconds(10), [P(10, "new.exe", 300 * MB, cpu: 1)]);

        Assert.Null(Analyze(cur, prev).Groups.Single().CpuPercent);
    }

    // ---- closable ----

    [Fact]
    public void Only_unprotected_programs_with_a_window_can_be_closed()
    {
        var snap = Snap(T0, [
            P(10, "notes.exe", window: true),
            P(11, "daemon.exe", window: false),
            P(5000, "explorer.exe", window: true, path: @"C:\Windows\explorer.exe"),
        ]);
        var groups = Analyze(snap, null).Groups.ToDictionary(g => g.Name);

        Assert.True(groups["notes"].CanClose);
        Assert.False(groups["daemon"].CanClose);
        Assert.False(groups["explorer"].CanClose);
    }

    // ---- growth detection ----

    private static ProcessGroup G(string name, long bytes) =>
        new(name, [1], bytes, bytes, 1, false, null, false, false, false);

    private static GrowthDetector Feed(string name, Func<int, long> bytesAtMinute, int minutes = 6, int perMinute = 2)
    {
        var d = new GrowthDetector();
        for (var i = 0; i <= minutes * perMinute; i++)
            d.Add(T0.AddSeconds(i * 60.0 / perMinute), [G(name, bytesAtMinute(i))]);
        return d;
    }

    [Fact]
    public void Steady_climb_is_reported_with_rate()
    {
        var d = Feed("leaky", i => 500 * MB + i * 15 * MB); // 30 MB per minute at 2 samples/min
        var alert = Assert.Single(d.Alerts());
        Assert.Equal("leaky", alert.Program);
        Assert.InRange(alert.MegabytesPerMinute, 29, 31);
    }

    [Fact]
    public void Flat_memory_is_not_reported() =>
        Assert.Empty(Feed("calm", _ => 800 * MB).Alerts());

    [Fact]
    public void A_one_time_jump_is_not_a_leak() =>
        Assert.Empty(Feed("spike", i => i < 6 ? 500 * MB : 1500 * MB).Alerts());

    [Fact]
    public void Noisy_ups_and_downs_are_not_a_leak() =>
        Assert.Empty(Feed("noisy", i => 500 * MB + (i % 2 == 0 ? 0 : 200 * MB)).Alerts());

    [Fact]
    public void Too_little_history_gives_no_verdict()
    {
        var d = Feed("new", i => 500 * MB + i * 30 * MB, minutes: 2);
        Assert.Empty(d.Alerts());
    }

    [Fact]
    public void A_program_that_restarts_starts_a_fresh_history()
    {
        var d = Feed("app", i => 500 * MB + i * 15 * MB);
        d.Add(T0.AddMinutes(7), [G("app", 100 * MB)]); // dropped sharply: restarted
        Assert.Empty(d.Alerts());
    }

    [Fact]
    public void Programs_that_exit_are_forgotten()
    {
        var d = Feed("gone", i => 500 * MB + i * 15 * MB);
        d.Add(T0.AddMinutes(7), []);
        Assert.Empty(d.Alerts());
    }

    // ---- Ollama ----

    [Fact]
    public void Parses_ps_output_and_splits_ram_from_gpu()
    {
        const string json = """
        {"models":[
          {"name":"llama3:8b","model":"llama3:8b","size":6000000000,"size_vram":4000000000,"expires_at":"2026-10-16T12:05:00Z"},
          {"name":"qwen2.5:0.5b","size":500000000,"size_vram":0}
        ]}
        """;

        var list = OllamaPsParser.Parse(json);

        Assert.Equal(2, list.Count);
        Assert.Equal("llama3:8b", list[0].Name);
        Assert.Equal(2_000_000_000, list[0].InMemoryBytes);
        Assert.NotNull(list[0].ExpiresAt);
        Assert.Equal(500_000_000, list[1].InMemoryBytes);
        Assert.Null(list[1].ExpiresAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"models\":null}")]
    [InlineData("{\"models\":[{\"size\":1}]}")]
    public void Bad_or_empty_ps_output_gives_an_empty_list(string json) =>
        Assert.Empty(OllamaPsParser.Parse(json));
}

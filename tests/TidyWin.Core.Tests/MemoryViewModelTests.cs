using TidyWin.Core.Memory;
using TidyWin.ViewModels;
using Xunit;

namespace TidyWin.Core.Tests;

public class MemoryViewModelTests
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ProtectionPolicy Policy = new(@"C:\Windows", selfPid: 999);

    private sealed class FakeSource : IMemorySource
    {
        public Func<MemorySnapshot> Next = () => throw new InvalidOperationException();
        public int Reads;
        public MemorySnapshot Read() { Reads++; return Next(); }
    }

    private sealed class FakeControl : IProcessControl
    {
        public List<int> Trimmed = [];
        public List<int> CloseRequests = [];
        public Action<int>? OnTrim;
        public TrimOutcome Trim(int pid) { Trimmed.Add(pid); OnTrim?.Invoke(pid); return TrimOutcome.Trimmed; }
        public bool RequestClose(int pid) { CloseRequests.Add(pid); return true; }
    }

    private sealed class FakeOllama : IOllama
    {
        public IReadOnlyList<LoadedModel>? Loaded;
        public List<string> Unloaded = [];
        public Task<IReadOnlyList<LoadedModel>?> ListLoadedAsync(CancellationToken ct = default) => Task.FromResult(Loaded);
        public Task<bool> UnloadAsync(string model, CancellationToken ct = default)
        {
            Unloaded.Add(model);
            Loaded = Loaded?.Where(m => m.Name != model).ToList();
            return Task.FromResult(true);
        }
    }

    private static ProcInfo P(int pid, string name, long ws, double cpu, bool window = false, int session = 1, string? path = null) =>
        new(pid, name, path ?? $@"C:\Apps\{name}.exe", session, ws, ws, cpu, window, window ? name : null);

    private static MemorySnapshot Snap(double seconds, long avail, IEnumerable<ProcInfo> procs, int? fg = null) =>
        new(T0.AddSeconds(seconds), new SystemMemory(16 * GB, avail, GB, 5000, 10000), procs.ToList(), fg);

    private static (MemoryViewModel vm, FakeSource src, FakeControl ctl, FakeOllama oll) Make()
    {
        var src = new FakeSource(); var ctl = new FakeControl(); var oll = new FakeOllama();
        var vm = new MemoryViewModel(src, ctl, oll, Policy, cores: 1, now: () => T0, delay: _ => Task.CompletedTask);
        return (vm, src, ctl, oll);
    }

    // A world with: an idle big app, a busy app, the app in front, a protected service.
    private static IEnumerable<ProcInfo> World(double t) =>
    [
        P(10, "slack.exe", 600 * MB, cpu: 50.0),                     // idle
        P(20, "render.exe", 900 * MB, cpu: 10 + t * 0.9),            // busy (90% of a core)
        P(30, "word.exe", 700 * MB, cpu: 5, window: true),           // in front
        P(800, "svchost.exe", 800 * MB, cpu: 3, session: 0, path: @"C:\Windows\System32\svchost.exe"),
    ];

    private static async Task<(MemoryViewModel vm, FakeSource src, FakeControl ctl, FakeOllama oll)> Primed()
    {
        var (vm, src, ctl, oll) = Make();
        src.Next = () => Snap(0, 6 * GB, World(0), fg: 30);
        await vm.RefreshAsync();
        src.Next = () => Snap(10, 6 * GB, World(10), fg: 30);
        await vm.RefreshAsync();
        return (vm, src, ctl, oll);
    }

    [Fact]
    public async Task Refresh_fills_numbers_pressure_and_groups()
    {
        var (vm, _, _, _) = await Primed();

        Assert.Equal(16 * GB, vm.TotalBytes);
        Assert.Equal(10 * GB, vm.UsedBytes);
        Assert.Equal(MemoryPressure.Moderate, vm.Pressure); // 6 of 16 = 37.5% free
        Assert.Equal(4, vm.Groups.Count);
        Assert.Equal(["render", "svchost", "word", "slack"], vm.Groups.Select(g => g.Name).ToArray()); // biggest first
    }

    [Fact]
    public async Task Only_the_idle_background_program_is_offered_for_trimming()
    {
        var (vm, _, _, _) = await Primed();

        Assert.Equal(1, vm.IdleCount);
        Assert.Equal(600 * MB, vm.IdleBytes);
        Assert.True(vm.CanTrimIdle);
        Assert.Equal(MemoryGroupState.IdleBackground, vm.Groups.Single(g => g.Name == "slack").State);
        Assert.Equal(MemoryGroupState.InUse, vm.Groups.Single(g => g.Name == "word").State);
        Assert.Equal(MemoryGroupState.System, vm.Groups.Single(g => g.Name == "svchost").State);
    }

    [Fact]
    public async Task Trim_idle_touches_only_idle_pids_and_reports_the_measured_change()
    {
        var (vm, src, ctl, _) = await Primed();
        ctl.OnTrim = _ => src.Next = () => Snap(20, 6 * GB + 450 * MB, World(20), fg: 30); // memory comes back after trimming

        var report = await vm.TrimIdleAsync();

        Assert.Equal([10], ctl.Trimmed);
        Assert.Equal(1, report!.ProcessesTrimmed);
        Assert.Equal(450 * MB, report.Delta);
        Assert.Equal(report, vm.LastTrim);
    }

    [Fact]
    public async Task A_trim_that_helped_nothing_says_so()
    {
        var (vm, _, _, _) = await Primed(); // free memory identical before and after

        var report = await vm.TrimIdleAsync();

        Assert.Equal(0, report!.Delta);
    }

    [Fact]
    public async Task Trim_idle_with_nothing_idle_does_nothing()
    {
        var (vm, src, ctl, _) = Make();
        src.Next = () => Snap(0, 6 * GB, [P(20, "render.exe", 900 * MB, 10)]);
        await vm.RefreshAsync(); // first reading: nothing can be called idle yet

        Assert.Null(await vm.TrimIdleAsync());
        Assert.Empty(ctl.Trimmed);
    }

    [Fact]
    public async Task Trimming_a_single_busy_group_by_hand_is_allowed_but_protected_and_foreground_are_not()
    {
        var (vm, _, ctl, _) = await Primed();

        Assert.Null(await vm.TrimGroupAsync(vm.Groups.Single(g => g.Name == "svchost")));
        Assert.Null(await vm.TrimGroupAsync(vm.Groups.Single(g => g.Name == "word")));
        Assert.NotNull(await vm.TrimGroupAsync(vm.Groups.Single(g => g.Name == "render")));

        Assert.Equal([20], ctl.Trimmed);
    }

    [Fact]
    public async Task Close_is_only_sent_for_unprotected_programs_with_a_window()
    {
        var (vm, _, ctl, _) = await Primed();

        Assert.Equal(0, await vm.CloseGroupAsync(vm.Groups.Single(g => g.Name == "svchost")));
        Assert.Equal(0, await vm.CloseGroupAsync(vm.Groups.Single(g => g.Name == "slack"))); // no window
        Assert.Equal(1, await vm.CloseGroupAsync(vm.Groups.Single(g => g.Name == "word")));

        Assert.Equal([30], ctl.CloseRequests);
        Assert.Contains("word", vm.Notice);
    }

    [Fact]
    public async Task Ollama_models_are_listed_and_can_be_unloaded()
    {
        var (vm, _, _, oll) = await Primed();
        oll.Loaded = [new LoadedModel("llama3:8b", 6 * GB, 0, null)];
        await vm.RefreshAsync();

        Assert.True(vm.OllamaRunning);
        var model = Assert.Single(vm.Models);
        Assert.Equal("内存", model.Where);

        Assert.True(await vm.UnloadModelAsync(model));

        Assert.Equal(["llama3:8b"], oll.Unloaded);
        Assert.Empty(vm.Models);
        Assert.True(vm.OllamaRunning);
    }

    [Fact]
    public async Task Ollama_not_running_is_distinguished_from_nothing_loaded()
    {
        var (vm, _, _, oll) = await Primed();

        oll.Loaded = null;
        await vm.RefreshAsync();
        Assert.False(vm.OllamaRunning);

        oll.Loaded = [];
        await vm.RefreshAsync();
        Assert.True(vm.OllamaRunning);
    }

    [Fact]
    public async Task A_program_that_keeps_growing_raises_an_alert_after_enough_readings()
    {
        var (vm, src, _, _) = Make();
        for (var i = 0; i <= 14; i++)
        {
            var t = i * 30.0;                         // every 30 s for 7 minutes
            var leaky = 500 * MB + i * 20 * MB;       // 40 MB per minute
            src.Next = () => Snap(t, 6 * GB, [P(10, "leaky.exe", leaky, 20)]);
            await vm.RefreshAsync();
        }

        var alert = Assert.Single(vm.Alerts);
        Assert.Equal("leaky", alert.Program);
    }

    [Fact]
    public async Task Rows_are_updated_in_place_between_refreshes()
    {
        var (vm, src, _, _) = await Primed();
        var before = vm.Groups.Single(g => g.Name == "slack");

        src.Next = () => Snap(20, 6 * GB, World(20), fg: 30);
        await vm.RefreshAsync();

        Assert.Same(before, vm.Groups.Single(g => g.Name == "slack"));
    }

    [Fact]
    public async Task Only_the_top_groups_are_shown()
    {
        var (vm, src, _, _) = Make();
        src.Next = () => Snap(0, 6 * GB, Enumerable.Range(1, 40).Select(i => P(i + 100, $"app{i}.exe", (100 + i) * MB, 1)));
        await vm.RefreshAsync();

        Assert.Equal(14, vm.Groups.Count);
    }
}

using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Disk;
using TidyWin.Core.Optimize;
using TidyWin.Core.Paths;
using TidyWin.Core.Safety;
using TidyWin.Core.Software;
using TidyWin.Core.Startup;
using TidyWin.ViewModels;
using Xunit;
using static TidyWin.Core.Tests.CleanExecutorTests;

namespace TidyWin.Core.Tests;

public class StartupViewModelTests
{
    private sealed class FakeStartup(params StartupEntry[] entries) : IStartupSource
    {
        public List<StartupEntry> Entries = [.. entries];
        public string? Fail;
        public List<string> Calls = [];

        public IReadOnlyList<StartupEntry> Read() => Entries.ToList();

        public string? SetEnabled(StartupEntry entry, bool enabled)
        {
            Calls.Add($"{(enabled ? "on" : "off")}:{entry.Name}");
            return Fail;
        }

        public string? Remove(StartupEntry entry)
        {
            Calls.Add($"remove:{entry.Name}");
            return Fail;
        }
    }

    private static StartupEntry E(string name, string cmd, bool on = true) => new(name, cmd, StartupSource.UserRun, on);

    private static readonly StartupEntry Discord = E("Discord", @"C:\Apps\Discord\Discord.exe");
    private static readonly StartupEntry OneDrive = E("OneDrive", @"C:\Apps\OneDrive\OneDrive.exe");
    private static readonly StartupEntry Health = E("SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe");
    private static readonly StartupEntry Mystery = E("Mystery", @"C:\Apps\Mystery\m.exe");
    private static readonly StartupEntry Gone = E("OldTool", @"C:\Apps\gone\old.exe");
    private static readonly StartupEntry Shady = E("Updater2", @"C:\Users\Public\x.exe");

    private static bool Exists(string p) => !p.Contains("gone", StringComparison.OrdinalIgnoreCase);

    private static StartupViewModel Make(FakeStartup src, OperationGate? gate = null, IManifestSink? sink = null) =>
        new(src, Exists, gate, sink);

    [Fact]
    public async Task Suspicious_and_dead_entries_come_first_and_switched_off_ones_last()
    {
        var src = new FakeStartup(OneDrive, E("Off", @"C:\Apps\Off\o.exe", on: false), Health, Gone, Shady, Discord);
        var vm = Make(src);
        await vm.RefreshAsync();

        var names = vm.Items.Select(i => i.Name).ToList();
        Assert.Equal("Updater2", names[0]);
        Assert.Equal("OldTool", names[1]);
        Assert.Equal("Off", names[^1]);
        Assert.Equal(2, vm.DeadCount);
        Assert.Equal(5, vm.EnabledCount);
    }

    [Fact]
    public async Task Turning_one_off_updates_the_counts_and_the_row()
    {
        var src = new FakeStartup(Discord, OneDrive);
        var vm = Make(src);
        await vm.RefreshAsync();
        var item = vm.Items.Single(i => i.Name == "Discord");

        Assert.True(await vm.SetEnabledAsync(item, false));
        Assert.False(item.IsEnabled);
        Assert.Equal(1, vm.EnabledCount);
        Assert.Equal(["off:Discord"], src.Calls);
    }

    [Fact]
    public async Task When_windows_refuses_nothing_changes_and_the_reason_is_shown()
    {
        var src = new FakeStartup(Discord) { Fail = "需要管理员权限" };
        var vm = Make(src);
        await vm.RefreshAsync();
        var item = vm.Items[0];

        Assert.False(await vm.SetEnabledAsync(item, false));
        Assert.True(item.IsEnabled);
        Assert.Equal("需要管理员权限", vm.Message);
    }

    [Fact]
    public async Task Bulk_switch_off_only_touches_chat_updater_and_game_entries()
    {
        var src = new FakeStartup(Discord, OneDrive, Health, Mystery, E("Steam", @"C:\Apps\Steam\steam.exe"));
        var vm = Make(src);
        await vm.RefreshAsync();
        Assert.Equal(2, vm.SuggestedCount);

        var n = await vm.DisableSuggestedAsync();

        Assert.Equal(2, n);
        Assert.Equal(["off:Discord", "off:Steam"], src.Calls.OrderBy(c => c).ToArray());
        Assert.Equal(0, vm.SuggestedCount);
    }

    [Fact]
    public async Task Only_dead_or_suspicious_entries_can_be_removed_and_removal_is_recorded()
    {
        var sink = new Sink();
        var src = new FakeStartup(Discord, Gone);
        var vm = Make(src, sink: sink);
        await vm.RefreshAsync();

        Assert.False(await vm.RemoveAsync(vm.Items.Single(i => i.Name == "Discord")));
        Assert.True(await vm.RemoveAsync(vm.Items.Single(i => i.Name == "OldTool")));

        Assert.Equal(["remove:OldTool"], src.Calls);
        Assert.Single(vm.Items);
        Assert.Single(sink.Entries);
        Assert.Equal("startup", sink.Entries[0].Source);
    }

    [Fact]
    public async Task While_another_operation_runs_changes_are_refused()
    {
        var gate = new OperationGate();
        var src = new FakeStartup(Discord);
        var vm = Make(src, gate);
        await vm.RefreshAsync();

        using var other = gate.TryEnter("清理系统垃圾");
        Assert.False(await vm.SetEnabledAsync(vm.Items[0], false));
        Assert.Contains("清理系统垃圾", vm.Message);
        Assert.Empty(src.Calls);
    }

    internal sealed class Sink : IManifestSink
    {
        public List<ManifestEntry> Entries = [];
        public void Record(ManifestEntry entry) => Entries.Add(entry);
    }
}

public class DiskViewModelTests
{
    private sealed class Drives : IDrives
    {
        public IReadOnlyList<DriveEntry> Read() => [new("C:\\", "System", 1000, 400)];
    }

    private sealed class Recycler : IRecycler
    {
        public List<string> Moved = [];
        public string? Fail;
        public string? Recycle(string path) { if (Fail is not null) return Fail; Moved.Add(path); return null; }
    }

    private sealed class Known : IKnownSpaceReader
    {
        public IReadOnlyList<KnownSpaceHit> Read() => [];
    }

    private static readonly string[] SystemRoots = [@"C:\Windows", @"C:\Program Files", @"C:\ProgramData"];

    private static DiskViewModel Make(FakeFs fs, Recycler rec, OperationGate? gate = null, StartupViewModelTests.Sink? sink = null) =>
        new(fs, new Drives(), rec, new Known(), SystemRoots, @"C:\Users\Ann", sink, gate, () => Now);

    private static FakeFs World() => new FakeFs()
        .Add(@"C:\D\Games\big.bin", 5000)
        .Add(@"C:\D\Games\sub\mid.bin", 800)
        .Add(@"C:\D\Docs\a.txt", 100);

    [Fact]
    public async Task A_scan_shows_the_root_and_the_biggest_files_under_whatever_is_open()
    {
        var vm = Make(World(), new Recycler());
        await vm.ScanAsync(@"C:\D");

        Assert.Equal(DiskPhase.Ready, vm.Phase);
        Assert.Equal(5900, vm.Scan!.TotalBytes);
        Assert.Equal(["big.bin", "mid.bin", "a.txt"], vm.FilesHere().Select(f => Path.GetFileName(f.Path)).ToArray());

        await vm.EnterAsync(vm.Current!.Folders.Single(f => f.Name == "Docs"));
        Assert.Equal(["a.txt"], vm.FilesHere().Select(f => Path.GetFileName(f.Path)).ToArray());
        Assert.True(vm.CanGoUp);

        vm.Up();
        Assert.Equal("D", vm.Current!.Name);
        Assert.False(vm.CanGoUp);
    }

    [Fact]
    public async Task Moving_a_folder_to_the_recycle_bin_takes_it_out_of_every_number()
    {
        var rec = new Recycler();
        var sink = new StartupViewModelTests.Sink();
        var vm = Make(World(), rec, sink: sink);
        await vm.ScanAsync(@"C:\D");

        var games = vm.Current!.Folders.Single(f => f.Name == "Games");
        Assert.True(await vm.RecycleFolderAsync(games));

        Assert.Equal([@"C:\D\Games"], rec.Moved);
        Assert.Equal(100, vm.Scan!.TotalBytes);
        Assert.Equal(100, vm.Scan.Root.Bytes);
        Assert.Empty(vm.Scan.TopFiles.Where(f => f.Path.Contains("Games")));
        Assert.DoesNotContain(vm.Scan.Root.Folders, f => f.Name == "Games");
        Assert.Equal("disk-recycle", sink.Entries.Single().Source);
        Assert.Contains("回收站", vm.Message);
    }

    [Fact]
    public async Task Choosing_another_disk_forgets_the_scan()
    {
        var vm = Make(World(), new Recycler());
        await vm.ScanAsync(@"C:\D");
        Assert.Equal(DiskPhase.Ready, vm.Phase);

        vm.ResetToDrives();

        Assert.Equal(DiskPhase.Idle, vm.Phase);
        Assert.Null(vm.Scan);
        Assert.Null(vm.Current);
        Assert.False(vm.CanGoUp);
        Assert.Null(vm.Message);
    }

    [Fact]
    public async Task Moving_one_file_only_changes_that_file()
    {
        var vm = Make(World(), new Recycler());
        await vm.ScanAsync(@"C:\D");
        var big = vm.Scan!.TopFiles[0];

        Assert.True(await vm.RecycleFileAsync(big));

        Assert.Equal(900, vm.Scan!.TotalBytes);
        Assert.Equal(900, vm.Scan.Root.Bytes);
        Assert.Equal(800, vm.Scan.Root.Folders.Single(f => f.Name == "Games").Bytes);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files\Foo")]
    [InlineData(@"C:\Users\Ann")]
    [InlineData(@"C:\")]
    public async Task System_places_are_refused_before_anything_is_touched(string path)
    {
        var rec = new Recycler();
        var vm = Make(World(), rec);
        await vm.ScanAsync(@"C:\D");

        Assert.False(await vm.RecycleFolderAsync(new DiskNode { Name = "x", Path = path, Bytes = 1 }));
        Assert.Empty(rec.Moved);
        Assert.False(string.IsNullOrEmpty(vm.Message));
    }

    [Fact]
    public async Task A_failed_move_changes_nothing()
    {
        var rec = new Recycler { Fail = "文件正在使用" };
        var vm = Make(World(), rec);
        await vm.ScanAsync(@"C:\D");
        var games = vm.Current!.Folders.Single(f => f.Name == "Games");

        Assert.False(await vm.RecycleFolderAsync(games));
        Assert.Equal(5900, vm.Scan!.TotalBytes);
        Assert.Equal("文件正在使用", vm.Message);
    }

    [Fact]
    public async Task While_something_else_is_running_the_move_is_refused()
    {
        var gate = new OperationGate();
        var rec = new Recycler();
        var vm = Make(World(), rec, gate);
        await vm.ScanAsync(@"C:\D");

        using var other = gate.TryEnter("卸载 Foo");
        Assert.False(await vm.RecycleFolderAsync(vm.Current!.Folders[0]));
        Assert.Empty(rec.Moved);
        Assert.Contains("卸载 Foo", vm.Message);
    }

    [Fact]
    public async Task Opening_a_folder_deeper_than_the_scan_kept_looks_at_just_that_folder_and_Up_comes_back()
    {
        var fs = new FakeFs()
            .Add(@"C:\D\a\b\c\d\e\f\g\deep.bin", 700)
            .Add(@"C:\D\a\b\c\d\e\f\h\deep2.bin", 300);
        var vm = Make(fs, new Recycler());
        await vm.ScanAsync(@"C:\D");

        var e = vm.Scan!.Root.Folders[0].Folders[0].Folders[0].Folders[0].Folders[0];
        Assert.Equal("e", e.Name);
        Assert.True(e.HasHiddenDepth);
        Assert.Empty(e.Folders); // "e" is the deepest kept folder; what lies below it is only counted

        await vm.EnterAsync(e);

        Assert.Equal(DiskPhase.Ready, vm.Phase);
        Assert.Equal(@"C:\D\a\b\c\d\e", vm.Scan!.Root.Path);
        Assert.Equal(2, vm.Scan.Root.Folders[0].Folders.Count); // g and h under f are now visible
        Assert.Equal(1000, vm.Scan.TotalBytes);

        vm.Up();
        Assert.Equal(@"C:\D", vm.Scan!.Root.Path);
        Assert.Equal("e", vm.Current!.Name);
    }

    [Fact]
    public async Task The_biggest_actionable_folder_skips_windows_and_the_profile_itself()
    {
        var fs = new FakeFs()
            .Add(@"C:\Windows\big.dll", 5L << 30)
            .Add(@"C:\Users\Ann\Videos\v.mp4", 3L << 30)
            .Add(@"C:\Users\Ann\Docs\d.txt", 10)
            .Add(@"C:\Games\g.pak", 2L << 30);
        var vm = Make(fs, new Recycler());
        await vm.ScanAsync(@"C:\");

        Assert.Equal("Videos", vm.BiggestActionable()?.Name);
    }

    [Fact]
    public async Task Nothing_is_named_when_nothing_is_big_enough()
    {
        var vm = Make(World(), new Recycler());
        await vm.ScanAsync(@"C:\D");
        Assert.Null(vm.BiggestActionable());
    }
}

public class SoftwareViewModelTests
{
    private sealed class Source : ISoftwareSource
    {
        public List<RawUninstall> Rows = [];
        public IReadOnlyList<RawUninstall> Read() => Rows.ToList();
    }

    private sealed class Uninstaller(Source source) : IUninstaller
    {
        public bool RemoveOnRun = true;
        public int? Code = 0;
        public string? LastExe, LastArgs;

        public Task<int?> RunAsync(string exe, string args, CancellationToken ct = default)
        {
            LastExe = exe; LastArgs = args;
            if (RemoveOnRun) source.Rows.RemoveAll(r => r.KeyName == "foo");
            return Task.FromResult(Code);
        }
    }

    private sealed class Winget : IWinget
    {
        public bool IsAvailable { get; set; } = true;
        public IReadOnlyList<WingetUpgrade>? List = [new("Git", "Git.Git", "2.40", "2.50", "winget")];
        public bool Ok = true;
        public Task<IReadOnlyList<WingetUpgrade>?> ListUpgradesAsync(CancellationToken ct = default) => Task.FromResult(List);
        public Task<bool> UpgradeAsync(string id, CancellationToken ct = default) => Task.FromResult(Ok);
    }

    private sealed class NoLinks : IReparseProbe { public bool IsReparsePoint(string p) => false; }

    private const string Local = @"C:\Users\Ann\AppData\Local";

    private static RawUninstall Raw(string key, string name, long kb = 1000) =>
        new(key, "HKLM64", name, "1.0", "Pub", "20260101", kb, null, @"C:\Foo\unins000.exe", null, false, null, null);

    private static (SoftwareViewModel Vm, Source Src, Uninstaller Un, FakeFs Fs, Winget Wg, StartupViewModelTests.Sink Sink) Make(OperationGate? gate = null)
    {
        var src = new Source { Rows = [Raw("foo", "Foo Studio Pro", 5000), Raw("bar", "Bar Reader", 200), Raw("baz", "Alpha Zip", 9000)] };
        var fs = new FakeFs()
            .Add(Local + @"\Foo Studio Pro\data\cache.bin", 4000)
            .Add(Local + @"\Foo Studio Pro\settings.json", 10)
            .Add(Local + @"\Other App\x.bin", 10);
        var un = new Uninstaller(src);
        var wg = new Winget();
        var sink = new StartupViewModelTests.Sink();
        var vm = new SoftwareViewModel(src, un, wg, fs, new NoLinks(), [Local], p => true, sink, gate, (_, _) => Task.CompletedTask, () => Now)
        {
            MaxChecks = 3,
        };
        return (vm, src, un, fs, wg, sink);
    }

    [Fact]
    public async Task The_list_can_be_searched_and_sorted()
    {
        var (vm, _, _, _, _, _) = Make();
        await vm.LoadAsync();

        Assert.Equal(["Alpha Zip", "Foo Studio Pro", "Bar Reader"], vm.Apps.Select(a => a.Name).ToArray());

        vm.Sort = SoftwareSort.Name;
        Assert.Equal(["Alpha Zip", "Bar Reader", "Foo Studio Pro"], vm.Apps.Select(a => a.Name).ToArray());

        vm.Query = "studio";
        Assert.Equal(["Foo Studio Pro"], vm.Apps.Select(a => a.Name).ToArray());
        Assert.Equal(3, vm.Total);
    }

    [Fact]
    public async Task After_a_real_uninstall_the_leftover_folder_is_offered_and_other_apps_are_left_out()
    {
        var (vm, _, un, _, _, _) = Make();
        await vm.LoadAsync();

        await vm.UninstallAsync(vm.Apps.Single(a => a.Name == "Foo Studio Pro"));

        Assert.Equal(@"C:\Foo\unins000.exe", un.LastExe);
        Assert.Equal(SoftwarePhase.Residuals, vm.Phase);
        var r = Assert.Single(vm.Residuals);
        Assert.Equal(Local + @"\Foo Studio Pro", r.Path);
        Assert.DoesNotContain(vm.Apps, a => a.Name == "Foo Studio Pro");
    }

    [Fact]
    public async Task Cleaning_leftovers_removes_only_the_ticked_folder_and_its_empty_shell()
    {
        var (vm, _, _, fs, _, sink) = Make();
        await vm.LoadAsync();
        await vm.UninstallAsync(vm.Apps.Single(a => a.Name == "Foo Studio Pro"));

        vm.Residuals[0].IsSelected = true; // nothing is ticked for the person
        await vm.CleanResidualsAsync();

        Assert.Equal(SoftwarePhase.Done, vm.Phase);
        Assert.DoesNotContain(fs.Files.Keys, k => k.Contains("Foo Studio Pro"));
        Assert.DoesNotContain(Local + @"\Foo Studio Pro", fs.Dirs);
        Assert.Contains(Local + @"\Other App\x.bin", fs.Files.Keys);
        Assert.Equal("software-residual", sink.Entries.Single().Source);
        Assert.Equal(2, vm.Report!.Deleted);
    }

    [Fact]
    public async Task Leftovers_are_never_ticked_for_the_person_and_cleaning_with_nothing_ticked_does_nothing()
    {
        var (vm, _, _, fs, _, sink) = Make();
        await vm.LoadAsync();
        await vm.UninstallAsync(vm.Apps.Single(a => a.Name == "Foo Studio Pro"));

        Assert.All(vm.Residuals, r => Assert.False(r.IsSelected));
        await vm.CleanResidualsAsync();

        Assert.Empty(fs.DeleteCalls);
        Assert.Empty(sink.Entries);
        Assert.Equal(SoftwarePhase.Residuals, vm.Phase); // still waiting for the person's choice
    }

    [Fact]
    public async Task An_unticked_leftover_is_not_touched()
    {
        var (vm, _, _, fs, _, _) = Make();
        await vm.LoadAsync();
        await vm.UninstallAsync(vm.Apps.Single(a => a.Name == "Foo Studio Pro"));

        vm.Residuals[0].IsSelected = false;
        await vm.CleanResidualsAsync();

        Assert.Contains(Local + @"\Foo Studio Pro\settings.json", fs.Files.Keys);
        Assert.Empty(fs.DeleteCalls);
    }

    [Fact]
    public async Task A_preview_deletes_nothing()
    {
        var (vm, _, _, fs, _, sink) = Make();
        await vm.LoadAsync();
        await vm.UninstallAsync(vm.Apps.Single(a => a.Name == "Foo Studio Pro"));

        vm.Residuals[0].IsSelected = true;
        await vm.CleanResidualsAsync(dryRun: true);

        Assert.Empty(fs.DeleteCalls);
        Assert.Empty(sink.Entries);
        Assert.True(vm.Report!.DryRun);
    }

    [Fact]
    public async Task If_the_uninstall_was_cancelled_nothing_is_offered()
    {
        var (vm, _, un, _, _, _) = Make();
        un.RemoveOnRun = false; // the person pressed Cancel in the uninstaller
        await vm.LoadAsync();

        await vm.UninstallAsync(vm.Apps.Single(a => a.Name == "Foo Studio Pro"));

        Assert.Equal(SoftwarePhase.Ready, vm.Phase);
        Assert.Empty(vm.Residuals);
        Assert.Contains("取消", vm.Message);
    }

    [Fact]
    public async Task If_the_uninstaller_cannot_start_that_is_said_plainly()
    {
        var (vm, _, un, _, _, _) = Make();
        un.Code = null;
        await vm.LoadAsync();

        await vm.UninstallAsync(vm.Apps[0]);

        Assert.Equal(SoftwarePhase.Ready, vm.Phase);
        Assert.Contains("没能启动", vm.Message);
    }

    [Fact]
    public async Task No_uninstall_starts_while_another_operation_runs()
    {
        var gate = new OperationGate();
        var (vm, _, un, _, _, _) = Make(gate);
        await vm.LoadAsync();

        using var other = gate.TryEnter("清理系统垃圾");
        await vm.UninstallAsync(vm.Apps[0]);

        Assert.Null(un.LastExe);
        Assert.Contains("清理系统垃圾", vm.Message);
    }

    [Fact]
    public async Task Updates_are_listed_and_marked_done_after_upgrading()
    {
        var (vm, _, _, _, wg, _) = Make();
        await vm.LoadUpdatesAsync();

        Assert.Equal(UpdatesPhase.Ready, vm.UpdatesPhase);
        Assert.Equal(1, vm.UpgradeCount);

        await vm.UpgradeAsync(vm.Upgrades[0]);

        Assert.Equal(UpgradeState.Done, vm.Upgrades[0].State);
        Assert.Equal(0, vm.UpgradeCount);
        Assert.True(wg.Ok);
    }

    [Fact]
    public async Task Without_winget_or_without_network_the_page_can_tell_them_apart()
    {
        var (vm, _, _, _, wg, _) = Make();
        wg.IsAvailable = false;
        await vm.LoadUpdatesAsync();
        Assert.Equal(UpdatesPhase.Unavailable, vm.UpdatesPhase);

        wg.IsAvailable = true;
        wg.List = null;
        await vm.LoadUpdatesAsync();
        Assert.Equal(UpdatesPhase.Failed, vm.UpdatesPhase);
    }
}

public class OptimizeViewModelTests
{
    private sealed class Optimizer : IOptimizer
    {
        public long? Bin = 2048;
        public bool Ok = true;
        public List<string> Ran = [];
        public long? RecycleBinBytes() => Bin;

        public Task<OptimizeResult> RunAsync(string id, CancellationToken ct = default)
        {
            Ran.Add(id);
            if (id == OptimizeCatalog.Recycle) Bin = 0;
            return Task.FromResult(new OptimizeResult(Ok, Ok ? "好了" : "没成功"));
        }
    }

    [Fact]
    public async Task The_recycle_bin_size_is_shown_and_refreshes_after_emptying()
    {
        var opt = new Optimizer();
        var vm = new OptimizeViewModel(opt);
        await vm.RefreshAsync();
        var rb = vm.Items.Single(i => i.Task.Id == OptimizeCatalog.Recycle);
        Assert.Contains("2 KB", rb.Hint);

        await vm.RunAsync(rb);

        Assert.Equal(OptimizeState.Done, rb.State);
        Assert.Contains("空", rb.Hint);
    }

    [Fact]
    public async Task A_failure_is_shown_as_failed_with_its_message()
    {
        var vm = new OptimizeViewModel(new Optimizer { Ok = false });
        var item = vm.Items.Single(i => i.Task.Id == OptimizeCatalog.Dns);

        await vm.RunAsync(item);

        Assert.Equal(OptimizeState.Failed, item.State);
        Assert.Equal("没成功", item.Result);
    }

    [Fact]
    public async Task Nothing_runs_while_another_operation_is_running()
    {
        var gate = new OperationGate();
        var opt = new Optimizer();
        var vm = new OptimizeViewModel(opt, gate);

        using var other = gate.TryEnter("清理 AI 缓存");
        await vm.RunAsync(vm.Items[0]);

        Assert.Empty(opt.Ran);
        Assert.Contains("清理 AI 缓存", vm.Message);
    }
}

public class CleanWiringTests
{
    private sealed class Env : IAiEnvironment
    {
        public string? Variable(string name) => null;
        public string Folder(KnownFolder f) => f switch
        {
            KnownFolder.UserProfile => @"C:\Users\Ann",
            KnownFolder.LocalAppData => @"C:\Users\Ann\AppData\Local",
            KnownFolder.AppData => @"C:\Users\Ann\AppData\Roaming",
            _ => @"C:\Users\Ann\AppData\Local\Temp",
        };
    }

    private const string Pip = @"C:\Users\Ann\AppData\Local\pip\Cache";

    [Fact]
    public async Task A_real_clean_is_recorded_and_announced_but_a_preview_is_not()
    {
        var fs = new FakeFs().Add(Pip + @"\a.whl", 100).Add(Pip + @"\b.whl", 50);
        var sink = new StartupViewModelTests.Sink();
        var vm = new AiCleanViewModel(fs, new Env(), new Probe(), () => Now, manifest: sink);
        CleanReport? announced = null;
        vm.Cleaned += r => announced = r;

        await vm.ScanAsync();
        await vm.CleanAsync(dryRun: true);
        Assert.Empty(sink.Entries);
        Assert.Null(announced);

        // A preview moves the page to Done; go back to Ready the way the page does, by scanning again.
        await vm.ScanAsync();
        await vm.CleanAsync();

        var entry = Assert.Single(sink.Entries);
        Assert.Equal("ai", entry.Source);
        Assert.Equal(2, entry.Deleted);
        Assert.Equal(150, entry.BytesFreed);
        Assert.Contains(Pip + @"\a.whl", entry.Paths);
        Assert.Equal(150, announced!.BytesFreed);
    }

    [Fact]
    public async Task A_clean_is_refused_while_another_operation_holds_the_gate()
    {
        var fs = new FakeFs().Add(Pip + @"\a.whl", 100);
        var gate = new OperationGate();
        var vm = new AiCleanViewModel(fs, new Env(), new Probe(), () => Now, gate: gate);
        await vm.ScanAsync();

        using var other = gate.TryEnter("卸载 Foo");
        await vm.CleanAsync();

        Assert.Equal(CleanPhase.Ready, vm.Phase);
        Assert.Contains("卸载 Foo", vm.Error);
        Assert.Empty(fs.DeleteCalls);
    }
}

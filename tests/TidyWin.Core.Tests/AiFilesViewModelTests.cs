using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Disk;
using TidyWin.Core.Safety;
using TidyWin.ViewModels;
using Xunit;
using static TidyWin.Core.Tests.CleanExecutorTests;

namespace TidyWin.Core.Tests;

public class AiFilesViewModelTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private sealed class Drives : IDrives
    {
        public IReadOnlyList<DriveEntry> Read() => [new(@"D:\", "Data", 1000 * Gb, 100 * Gb)];
    }

    private sealed class Recycler : IRecycler
    {
        public List<string> Moved = [];
        public Dictionary<string, string> Fail = new(StringComparer.OrdinalIgnoreCase);
        public string? Recycle(string path)
        {
            if (Fail.TryGetValue(path, out var e)) return e;
            Moved.Add(path);
            return null;
        }
    }

    private sealed class Merger : IFileMerger
    {
        public List<(string Duplicate, string Original)> Merged = [];
        public string? Fail;
        public string? Merge(string duplicate, string original)
        {
            if (Fail is not null) return Fail;
            Merged.Add((duplicate, original));
            return null;
        }
    }

    private static readonly string[] SystemRoots = [@"D:\Windows"];

    private static (AiFilesViewModel Vm, Recycler Rec, Merger Merger, StartupViewModelTests.Sink Sink) Make(
        FakeFs fs, Func<string, bool>? running = null, OperationGate? gate = null)
    {
        var rec = new Recycler();
        var merger = new Merger();
        var sink = new StartupViewModelTests.Sink();
        var vm = new AiFilesViewModel(fs, new Drives(), rec, SystemRoots, @"C:\Users\Ann", null, running, sink, gate, () => Now, merger);
        return (vm, rec, merger, sink);
    }

    private static FakeFs World() => new FakeFs()
        .Add(@"D:\AI\ComfyUI\models\checkpoints\sdxl.safetensors", 6 * Gb)
        .Add(@"D:\old\llama.gguf", 4 * Gb, ageDays: 300)
        .Add(@"D:\new\llama.gguf", 4 * Gb, ageDays: 10)
        .Add(@"D:\Windows\System32\ignored.onnx", 3 * Gb);

    private const string Sdxl = @"D:\AI\ComfyUI\models\checkpoints\sdxl.safetensors";

    [Fact]
    public async Task A_scan_finds_things_beyond_system_folders_and_ticks_nothing()
    {
        var (vm, _, _, _) = Make(World());
        await vm.ScanAsync();

        Assert.Equal(FinderPhase.Ready, vm.Phase);
        Assert.Equal(3, vm.Items.Count);
        Assert.DoesNotContain(vm.Items, i => i.Path.Contains("Windows"));
        Assert.All(vm.Items, i => Assert.False(i.IsSelected));
        Assert.Equal(14 * Gb, vm.TotalBytes);
        Assert.Equal(4 * Gb, vm.SafeBytes);
    }

    [Fact]
    public async Task Select_safe_ticks_only_what_has_proof_behind_it()
    {
        var (vm, _, _, _) = Make(World());
        await vm.ScanAsync();

        vm.SelectSafe();

        var one = Assert.Single(vm.Items.Where(i => i.IsSelected));
        Assert.Equal(@"D:\new\llama.gguf", one.Path);
        Assert.Equal("合并(不删除)", one.ActionLabel);
        Assert.Equal(4 * Gb, vm.SelectedBytes);
        Assert.True(vm.HasSelection);
    }

    [Fact]
    public async Task A_duplicate_is_merged_not_recycled_and_the_message_says_nothing_disappeared()
    {
        var (vm, rec, merger, sink) = Make(World());
        await vm.ScanAsync();
        vm.SelectSafe();

        await vm.ApplySelectedAsync();

        Assert.Equal([(@"D:\new\llama.gguf", @"D:\old\llama.gguf")], merger.Merged);
        Assert.Empty(rec.Moved);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("ai-finder-merge", sink.Entries.Single().Source);
        Assert.Contains("已合并", vm.Message);
        Assert.Contains("照常能用", vm.Message);
        Assert.Equal(FinderPhase.Ready, vm.Phase);
    }

    [Fact]
    public async Task A_failed_merge_leaves_the_item_in_place_and_says_why()
    {
        var (vm, _, merger, sink) = Make(World());
        merger.Fail = "两份的内容并不完全一样,没有合并";
        await vm.ScanAsync();
        vm.SelectSafe();

        await vm.ApplySelectedAsync();

        Assert.Equal(3, vm.Items.Count);
        Assert.Contains("内容并不完全一样", vm.Message);
        Assert.Empty(sink.Entries);
    }

    [Fact]
    public async Task Anything_else_goes_to_the_recycle_bin_records_it_and_explains_that_space_is_not_back_yet()
    {
        var (vm, rec, merger, sink) = Make(World());
        await vm.ScanAsync();
        vm.Items.Single(i => i.Path == Sdxl).IsSelected = true;

        await vm.ApplySelectedAsync();

        Assert.Equal([Sdxl], rec.Moved);
        Assert.Empty(merger.Merged);
        Assert.Equal("ai-finder", sink.Entries.Single().Source);
        Assert.Contains("回收站", vm.Message);
        Assert.Contains("还原", vm.Message);
    }

    [Fact]
    public async Task Something_in_use_cannot_be_ticked_and_is_not_touched_even_if_forced()
    {
        var (vm, rec, merger, _) = Make(World(), running: p => p.StartsWith(@"D:\AI\ComfyUI", StringComparison.OrdinalIgnoreCase));
        await vm.ScanAsync();

        var inUse = vm.Items.Single(i => i.Safety == FoundSafety.InUse);
        Assert.False(inUse.CanSelect);

        inUse.IsSelected = true;
        await vm.ApplySelectedAsync();

        Assert.Empty(rec.Moved);
        Assert.Empty(merger.Merged);
    }

    [Fact]
    public async Task A_program_started_after_the_scan_is_noticed_at_the_last_moment()
    {
        var started = false;
        var (vm, _, merger, _) = Make(World(), running: _ => started);
        await vm.ScanAsync();
        vm.SelectSafe();

        started = true;
        await vm.ApplySelectedAsync();

        Assert.Empty(merger.Merged);
        Assert.Equal(3, vm.Items.Count);
        Assert.Contains("正在运行", vm.Message);
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_others_and_is_reported()
    {
        var (vm, rec, merger, _) = Make(World());
        rec.Fail[Sdxl] = "文件正在使用";
        await vm.ScanAsync();
        foreach (var i in vm.Items) i.IsSelected = true;

        await vm.ApplySelectedAsync();

        Assert.Equal([@"D:\old\llama.gguf"], rec.Moved);
        Assert.Single(merger.Merged);
        var left = Assert.Single(vm.Items);
        Assert.Equal(Sdxl, left.Path);
        Assert.Contains("文件正在使用", vm.Message);
    }

    [Fact]
    public async Task Nothing_changes_while_another_operation_runs()
    {
        var gate = new OperationGate();
        var (vm, rec, merger, _) = Make(World(), gate: gate);
        await vm.ScanAsync();
        vm.SelectSafe();

        using var other = gate.TryEnter("清理系统垃圾");
        await vm.ApplySelectedAsync();

        Assert.Empty(rec.Moved);
        Assert.Empty(merger.Merged);
        Assert.Contains("清理系统垃圾", vm.Message);
        Assert.Equal(FinderPhase.Ready, vm.Phase);
    }

    [Fact]
    public async Task Without_a_merger_a_merge_is_refused_rather_than_turned_into_a_delete()
    {
        var rec = new Recycler();
        var vm = new AiFilesViewModel(World(), new Drives(), rec, SystemRoots, @"C:\Users\Ann");
        await vm.ScanAsync();
        vm.SelectSafe();

        await vm.ApplySelectedAsync();

        Assert.Empty(rec.Moved);
        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public async Task Clear_selection_unticks_everything()
    {
        var (vm, _, _, _) = Make(World());
        await vm.ScanAsync();
        vm.SelectSafe();
        vm.ClearSelection();
        Assert.Equal(0, vm.SelectedCount);
    }
}

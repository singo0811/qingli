using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.ViewModels;
using Xunit;
using static TidyWin.Core.Tests.CleanExecutorTests;

namespace TidyWin.Core.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-5, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536 * 1024, "1.5 MB")]
    [InlineData(13_314_398_618, "12.4 GB")]
    public void Formats(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void Split_separates_number_and_unit()
    {
        var (n, u) = ByteSize.Split(2L * 1024 * 1024 * 1024);
        Assert.Equal("2.0", n);
        Assert.Equal("GB", u);
    }
}

public class AiCleanViewModelTests
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
    private const string Hf = @"C:\Users\Ann\.cache\huggingface\hub";
    private const string Ollama = @"C:\Users\Ann\.ollama\models";
    private const string Chats = @"C:\Users\Ann\AppData\Roaming\Cursor\User\workspaceStorage";

    private static AiCleanViewModel Make(FakeFs fs, params string[] links) =>
        new(fs, new Env(), new Probe(links), () => Now);

    private static FakeFs World() => new FakeFs()
        .Add(Pip + @"\a.whl", 100)
        .Add(Hf + @"\models--x\blobs\w.bin", 5000)
        .Add(Ollama + @"\blobs\sha", 9000)
        .Add(Chats + @"\abc\state.vscdb", 777);

    [Fact]
    public async Task Scan_defaults_select_only_auto_rebuild_items()
    {
        var vm = Make(World());
        await vm.ScanAsync();

        Assert.Equal(CleanPhase.Ready, vm.Phase);
        Assert.True(vm.Items.Single(i => i.Path == Pip).IsSelected);
        Assert.False(vm.Items.Single(i => i.Path == Hf).IsSelected);
        Assert.False(vm.Items.Single(i => i.Path == Ollama).IsSelected);
        Assert.Equal(100, vm.SelectedBytes);
        Assert.Equal(100 + 5000 + 9000, vm.FoundBytes);
    }

    [Fact]
    public async Task Items_are_sorted_biggest_first_and_never_items_are_listed_apart()
    {
        var vm = Make(World());
        await vm.ScanAsync();

        Assert.Equal([Ollama, Hf, Pip], vm.Items.Select(i => i.Path).ToArray());
        Assert.Contains(vm.Untouched, i => i.Path == Chats);
        Assert.All(vm.Untouched, i => Assert.False(i.CanSelect));
        Assert.DoesNotContain(vm.Items, i => i.Path == Chats);
    }

    [Fact]
    public async Task A_cache_whose_files_are_all_too_new_is_not_listed()
    {
        // Cursor's Cache has a 7 day minimum age, so a 1 day old file must not produce a card.
        var fs = World().Add(@"C:\Users\Ann\AppData\Roaming\Cursor\Cache\fresh.bin", 4000, ageDays: 1);
        var vm = Make(fs);
        await vm.ScanAsync();

        Assert.DoesNotContain(vm.Items, i => i.Path.EndsWith(@"Cursor\Cache"));
        Assert.DoesNotContain(vm.Items, i => i.Bytes == 0);
    }

    [Fact]
    public async Task An_old_file_in_the_same_cache_does_make_it_appear_with_only_the_old_bytes()
    {
        var fs = World()
            .Add(@"C:\Users\Ann\AppData\Roaming\Cursor\Cache\fresh.bin", 4000, ageDays: 1)
            .Add(@"C:\Users\Ann\AppData\Roaming\Cursor\Cache\stale.bin", 300, ageDays: 20);
        var vm = Make(fs);
        await vm.ScanAsync();

        var cache = vm.Items.Single(i => i.Path.EndsWith(@"Cursor\Cache"));
        Assert.Equal(300, cache.Bytes);
    }

    [Fact]
    public async Task User_installed_models_cannot_be_ticked()
    {
        var vm = Make(World());
        await vm.ScanAsync();
        var ollama = vm.Items.Single(i => i.Path == Ollama);

        ollama.IsSelected = true;

        Assert.False(ollama.IsSelected);
        Assert.False(ollama.CanSelect);
    }

    [Fact]
    public async Task Ticking_a_download_item_updates_the_total()
    {
        var vm = Make(World());
        await vm.ScanAsync();

        vm.Items.Single(i => i.Path == Hf).IsSelected = true;

        Assert.Equal(5100, vm.SelectedBytes);
        Assert.True(vm.HasSelection);
    }

    [Fact]
    public async Task Clean_deletes_only_ticked_folders()
    {
        var fs = World();
        var vm = Make(fs);
        await vm.ScanAsync();

        await vm.CleanAsync();

        Assert.Equal(CleanPhase.Done, vm.Phase);
        Assert.Equal(100, vm.Report!.BytesFreed);
        Assert.DoesNotContain(fs.Files.Keys, k => k.StartsWith(Pip));
        Assert.Contains(fs.Files.Keys, k => k.StartsWith(Hf));
        Assert.Contains(fs.Files.Keys, k => k.StartsWith(Ollama));
        Assert.Contains(fs.Files.Keys, k => k.StartsWith(Chats));
    }

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        var fs = World();
        var vm = Make(fs);
        await vm.ScanAsync();

        await vm.CleanAsync(dryRun: true);

        Assert.True(vm.Report!.DryRun);
        Assert.Equal(100, vm.Report.BytesFreed);
        Assert.Empty(fs.DeleteCalls);
    }

    [Fact]
    public async Task Clean_with_nothing_ticked_does_nothing()
    {
        var fs = World();
        var vm = Make(fs);
        await vm.ScanAsync();
        vm.ClearSelection();

        await vm.CleanAsync();

        Assert.Equal(CleanPhase.Ready, vm.Phase);
        Assert.Empty(fs.DeleteCalls);
    }

    [Fact]
    public async Task A_linked_cache_root_cannot_be_used_to_reach_other_places()
    {
        var fs = World();
        var vm = Make(fs, Pip + @"\sub"); // a sub folder turns into a junction
        fs.Add(Pip + @"\sub\inner.bin", 40);
        await vm.ScanAsync();

        await vm.CleanAsync();

        Assert.Contains(fs.Files.Keys, k => k.EndsWith("inner.bin"));
    }

    [Fact]
    public async Task Strata_has_one_slice_per_tier_in_order()
    {
        var vm = Make(World());
        await vm.ScanAsync();

        Assert.Equal([AiSafety.Regenerable, AiSafety.ReDownloadable, AiSafety.UserChoice], vm.Strata.Select(s => s.Safety).ToArray());
        Assert.Equal(100, vm.Strata[0].Bytes);
    }

    [Fact]
    public async Task Rescan_after_clean_returns_to_ready_with_nothing_left_to_free_in_that_folder()
    {
        var fs = World();
        var vm = Make(fs);
        await vm.ScanAsync();
        await vm.CleanAsync();

        await vm.ScanAsync();

        Assert.Equal(CleanPhase.Ready, vm.Phase);
        Assert.DoesNotContain(vm.Items, i => i.Path == Pip);
    }
}

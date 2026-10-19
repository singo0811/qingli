using TidyWin.Core.AiJunk;
using TidyWin.Core.Memory;
using TidyWin.ViewModels;
using Xunit;
using static TidyWin.Core.Tests.CleanExecutorTests;

namespace TidyWin.Core.Tests;

public class RelativeTimeTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "今天")]
    [InlineData(5, "5 天前")]
    [InlineData(31, "1 个月前")]
    [InlineData(95, "3 个月前")]
    [InlineData(400, "1 年前")]
    public void Describes(int daysAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Describe(Now.AddDays(-daysAgo), Now));
}

public class ModelsViewModelTests
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

    private sealed class FakeOllama : IOllama
    {
        public IReadOnlyList<LoadedModel>? Loaded = [];
        public Task<IReadOnlyList<LoadedModel>?> ListLoadedAsync(CancellationToken ct = default) => Task.FromResult(Loaded);
        public Task<bool> UnloadAsync(string model, CancellationToken ct = default) => Task.FromResult(true);
    }

    private const string Ollama = @"C:\Users\Ann\.ollama\models";
    private const string Lm = @"C:\Users\Ann\.lmstudio\models";
    private const string Hub = @"C:\Users\Ann\.cache\huggingface\hub";
    private static readonly string Shared = new('a', 64);
    private static readonly string OnlyA = new('b', 64);
    private static readonly string OnlyB = new('c', 64);

    private static string Manifest(params string[] hexes) =>
        "{\"layers\":[" + string.Join(",", hexes.Select(h => "{\"digest\":\"sha256:" + h + "\"}")) + "]}";

    private static FakeFs World() => new FakeFs()
        .AddText(Ollama + @"\manifests\registry.ollama.ai\library\llama3\8b", Manifest(Shared, OnlyA))
        .AddText(Ollama + @"\manifests\registry.ollama.ai\library\phi3\mini", Manifest(Shared, OnlyB))
        .Add(Ollama + $@"\blobs\sha256-{Shared}", 1000)
        .Add(Ollama + $@"\blobs\sha256-{OnlyA}", 300)
        .Add(Ollama + $@"\blobs\sha256-{OnlyB}", 200)
        .Add(Lm + @"\pub\repo\m.gguf", 4000)
        .AddText(Hub + @"\models--acme--tiny\refs\main", "newrev")
        .Add(Hub + @"\models--acme--tiny\blobs\w1", 500)
        .Add(Hub + @"\models--acme--tiny\blobs\w2", 600)
        .AddLink(Hub + @"\models--acme--tiny\snapshots\oldrev\m.bin", @"..\..\blobs\w1", ageDays: 90)
        .AddLink(Hub + @"\models--acme--tiny\snapshots\newrev\m.bin", @"..\..\blobs\w2", ageDays: 2);

    private static (ModelsViewModel vm, FakeOllama oll) Make(FakeFs fs, params string[] links)
    {
        var oll = new FakeOllama();
        return (new ModelsViewModel(fs, new Env(), new Probe(links), oll, () => Now, minListedBytes: 0), oll);
    }

    [Fact]
    public async Task Total_does_not_count_old_versions_twice()
    {
        var (vm, _) = Make(World());
        await vm.ScanAsync();

        var wholeRepos = vm.Everything.Where(i => !i.Entry.Id.StartsWith("hf-old:")).Sum(i => i.FreeBytes);
        Assert.Equal(wholeRepos, vm.TotalBytes);
        Assert.Contains(vm.HuggingFace, i => i.Entry.Id.StartsWith("hf-old:")); // the case is really present
    }

    [Fact]
    public async Task Stubs_without_real_weights_are_not_listed_by_default()
    {
        var fs = new FakeFs()
            .AddText(Hub + @"\models--acme--stub\refs\main", "rev")             // only a ref file
            .Add(Hub + @"\models--acme--real\blobs\w1", 5 * 1024 * 1024)       // 5 MB of weights
            .AddText(Hub + @"\models--acme--real\refs\main", "rev")
            .AddLink(Hub + @"\models--acme--real\snapshots\rev\m.bin", @"..\..\blobs\w1", ageDays: 2);

        var vm = new ModelsViewModel(fs, new Env(), new Probe(), new FakeOllama(), () => Now);
        await vm.ScanAsync();

        var only = Assert.Single(vm.HuggingFace);
        Assert.Contains("real", only.Name);
    }

    [Fact]
    public async Task Scan_groups_models_by_source_and_selects_nothing()
    {
        var (vm, _) = Make(World());
        await vm.ScanAsync();

        Assert.Equal(CleanPhase.Ready, vm.Phase);
        Assert.Equal(2, vm.Ollama.Count);
        Assert.Single(vm.LmStudio);
        Assert.Equal(2, vm.HuggingFace.Count); // whole repo + old versions
        Assert.Equal(0, vm.SelectedCount);
        Assert.Equal(0, vm.SelectedBytes);
        Assert.True(vm.HasAny);
    }

    [Fact]
    public async Task Selected_total_comes_from_the_plan_so_shared_layers_are_counted_once()
    {
        var (vm, _) = Make(World());
        await vm.ScanAsync();

        vm.Ollama.Single(i => i.Name == "llama3:8b").IsSelected = true;
        var onlyLlama = vm.SelectedBytes;
        vm.Ollama.Single(i => i.Name == "phi3:mini").IsSelected = true;

        Assert.True(onlyLlama < 1000);        // the shared layer stays while phi3 is kept
        Assert.True(vm.SelectedBytes > 1000); // now the shared layer goes too
        Assert.Equal(2, vm.SelectedCount);
    }

    [Fact]
    public async Task A_model_loaded_in_ollama_cannot_be_selected()
    {
        var (vm, oll) = Make(World());
        oll.Loaded = [new LoadedModel("llama3:8b", 100, 0, null)];
        await vm.ScanAsync();

        var busy = vm.Ollama.Single(i => i.Name == "llama3:8b");
        busy.IsSelected = true;

        Assert.True(busy.InUse);
        Assert.False(busy.IsSelected);
        Assert.Contains("正在被 Ollama 使用", busy.Detail);
    }

    [Fact]
    public async Task A_model_loaded_after_the_scan_is_dropped_at_delete_time()
    {
        var fs = World();
        var (vm, oll) = Make(fs);
        await vm.ScanAsync();
        vm.Ollama.Single(i => i.Name == "llama3:8b").IsSelected = true;
        oll.Loaded = [new LoadedModel("llama3:8b", 100, 0, null)]; // the user starts it meanwhile

        await vm.CleanAsync();

        Assert.Empty(fs.DeleteCalls);
        Assert.Contains("llama3:8b", vm.Notice);
        Assert.Equal(CleanPhase.Ready, vm.Phase);
    }

    [Fact]
    public async Task Deleting_one_ollama_model_keeps_the_other_one_complete()
    {
        var fs = World();
        var (vm, _) = Make(fs);
        await vm.ScanAsync();
        vm.Ollama.Single(i => i.Name == "llama3:8b").IsSelected = true;

        await vm.CleanAsync();

        Assert.Equal(CleanPhase.Done, vm.Phase);
        Assert.DoesNotContain(fs.Files.Keys, k => k.EndsWith("llama3\\8b"));
        Assert.DoesNotContain(fs.Files.Keys, k => k.EndsWith($"sha256-{OnlyA}"));
        Assert.Contains(fs.Files.Keys, k => k.EndsWith($"sha256-{Shared}"));
        Assert.Contains(fs.Files.Keys, k => k.EndsWith($"sha256-{OnlyB}"));
        Assert.Contains(fs.Files.Keys, k => k.EndsWith(@"phi3\mini"));
    }

    [Fact]
    public async Task Deleting_old_hf_versions_leaves_the_current_one_working()
    {
        var fs = World();
        var (vm, _) = Make(fs);
        await vm.ScanAsync();
        vm.HuggingFace.Single(i => i.Entry.Id.StartsWith("hf-old:")).IsSelected = true;

        await vm.CleanAsync();

        Assert.Equal(500, vm.Report!.BytesFreed);
        Assert.DoesNotContain(fs.Files.Keys, k => k.Contains("oldrev") || k.EndsWith(@"blobs\w1"));
        Assert.Contains(fs.Files.Keys, k => k.Contains("newrev"));
        Assert.Contains(fs.Files.Keys, k => k.EndsWith(@"blobs\w2"));
        Assert.Contains(fs.Files.Keys, k => k.EndsWith(@"refs\main"));
    }

    [Fact]
    public async Task Dry_run_removes_nothing()
    {
        var fs = World();
        var (vm, _) = Make(fs);
        await vm.ScanAsync();
        vm.LmStudio.Single().IsSelected = true;

        await vm.CleanAsync(dryRun: true);

        Assert.True(vm.Report!.DryRun);
        Assert.Equal(4000, vm.Report.BytesFreed);
        Assert.Empty(fs.DeleteCalls);
    }

    [Fact]
    public async Task Nothing_selected_means_nothing_happens()
    {
        var fs = World();
        var (vm, _) = Make(fs);
        await vm.ScanAsync();

        await vm.CleanAsync();

        Assert.Equal(CleanPhase.Ready, vm.Phase);
        Assert.Empty(fs.DeleteCalls);
    }

    [Fact]
    public async Task A_model_folder_that_became_a_junction_after_the_scan_is_refused_by_the_guard()
    {
        var fs = World();
        var (vm, _) = Make(fs, Lm + @"\pub"); // pub\ turns into a junction to somewhere else
        await vm.ScanAsync();
        vm.LmStudio.Single().IsSelected = true;

        await vm.CleanAsync();

        Assert.Equal(0, vm.Report!.Deleted);
        Assert.Contains(fs.Files.Keys, k => k.EndsWith("m.gguf"));
    }

    [Fact]
    public async Task No_model_folders_means_an_empty_page()
    {
        var (vm, _) = Make(new FakeFs());
        await vm.ScanAsync();

        Assert.False(vm.HasAny);
        Assert.Equal(CleanPhase.Ready, vm.Phase);
    }
}

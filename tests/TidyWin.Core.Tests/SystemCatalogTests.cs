using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Paths;
using TidyWin.ViewModels;
using Xunit;
using static TidyWin.Core.Tests.CleanExecutorTests;

namespace TidyWin.Core.Tests;

public class SystemCatalogTests
{
    private sealed class Env(Dictionary<string, string>? vars = null) : IAiEnvironment
    {
        public string? Variable(string name) => vars is not null && vars.TryGetValue(name, out var v) ? v : null;
        public string Folder(KnownFolder f) => f switch
        {
            KnownFolder.UserProfile => @"C:\Users\Ann",
            KnownFolder.LocalAppData => @"C:\Users\Ann\AppData\Local",
            KnownFolder.AppData => @"C:\Users\Ann\AppData\Roaming",
            _ => @"C:\Users\Ann\AppData\Local\Temp",
        };
    }

    private const string Temp = @"C:\Users\Ann\AppData\Local\Temp";
    private const string Chrome = @"C:\Users\Ann\AppData\Local\Google\Chrome\User Data\Default";

    private static IReadOnlyList<ResolvedAiTarget> Resolved(Dictionary<string, string>? vars = null) =>
        AiCatalog.Resolve(new Env(vars), SystemCatalog.All);

    [Fact]
    public void Ids_are_unique_and_do_not_collide_with_the_AI_list()
    {
        var ids = SystemCatalog.All.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Empty(ids.Intersect(AiCatalog.All.Select(t => t.Id)));
    }

    [Fact]
    public void No_folder_is_listed_on_both_pages()
    {
        var system = Resolved().Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ai = AiCatalog.Resolve(new Env()).Select(r => r.Path);
        Assert.Empty(ai.Where(system.Contains));
    }

    [Fact]
    public void Only_whole_folder_tiers_are_offered_and_each_one_explains_the_cost()
    {
        foreach (var t in SystemCatalog.All)
        {
            Assert.True(t.Safety is AiSafety.Regenerable or AiSafety.ReDownloadable, t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Consequence), t.Id);
            Assert.False(t.HasWildcard, t.Id);
        }
    }

    [Fact]
    public void Only_auto_rebuild_items_are_ticked_by_default()
    {
        foreach (var t in SystemCatalog.All)
            Assert.Equal(t.Safety == AiSafety.Regenerable, t.SelectedByDefault);
    }

    [Fact]
    public void Every_target_sits_deep_inside_the_user_profile()
    {
        foreach (var r in Resolved())
        {
            Assert.False(WindowsPath.IsRoot(r.Path), r.Target.Id);
            Assert.StartsWith(@"C:\Users\Ann\", r.Path);
            foreach (var shallow in new[] { @"C:\Users\Ann", @"C:\Users\Ann\AppData", @"C:\Users\Ann\AppData\Local",
                         @"C:\Users\Ann\AppData\Roaming", @"C:\Users\Ann\Documents", @"C:\Users\Ann\Desktop" })
                Assert.False(r.Path.Equals(shallow, StringComparison.OrdinalIgnoreCase), $"{r.Target.Id} -> {r.Path}");
        }
    }

    [Fact]
    public void Temp_resolves_to_exactly_the_temp_folder()
    {
        Assert.Equal(Temp, Resolved().Single(r => r.Target.Id == "win.temp").Path);
    }

    [Fact]
    public void Temp_and_logs_have_a_minimum_age_so_running_programs_keep_their_files()
    {
        Assert.True(SystemCatalog.All.Single(t => t.Id == "win.temp").MinAgeDays >= 3);
        Assert.True(SystemCatalog.All.Single(t => t.Id == "vscode.logs").MinAgeDays >= 7);
    }

    [Fact]
    public void Environment_variables_move_the_developer_caches()
    {
        var r = Resolved(new()
        {
            ["NUGET_PACKAGES"] = @"D:\dev\nuget",
            ["GRADLE_USER_HOME"] = @"D:\dev\gradle",
            ["CARGO_HOME"] = @"D:\dev\cargo",
            ["GOCACHE"] = @"D:\dev\gocache",
            ["npm_config_cache"] = @"D:\dev\npm",
        });
        string P(string id) => r.Single(x => x.Target.Id == id).Path;

        Assert.Equal(@"D:\dev\nuget", P("nuget.packages"));
        Assert.Equal(@"D:\dev\gradle\caches", P("gradle.caches"));
        Assert.Equal(@"D:\dev\cargo\registry\cache", P("cargo.cache"));
        Assert.Equal(@"D:\dev\gocache", P("go.build"));
        Assert.Equal(@"D:\dev\npm", P("npm.cache"));
    }

    [Fact]
    public void A_variable_pointing_at_a_drive_root_never_becomes_an_allowed_root()
    {
        var r = Resolved(new() { ["GOCACHE"] = @"D:\" });
        Assert.DoesNotContain(AiCatalog.AllowedRoots(r), WindowsPath.IsRoot);
    }

    private static PathGuard Guard() => new(AiCatalog.AllowedRoots(Resolved()), [], new Probe());

    [Theory]
    [InlineData(Chrome + @"\Cache\Cache_Data\f_000001", true)]
    [InlineData(Chrome + @"\Code Cache\js\a_0", true)]
    [InlineData(Temp + @"\setup.tmp", true)]
    [InlineData(Temp + @"\sub\deep\x.bin", true)]
    [InlineData(@"C:\Users\Ann\.nuget\packages\newtonsoft.json\13.0.3\lib\a.dll", true)]
    // Browser data that is not a cache must stay out of reach.
    [InlineData(Chrome + @"\Login Data", false)]
    [InlineData(Chrome + @"\Cookies", false)]
    [InlineData(Chrome + @"\History", false)]
    [InlineData(Chrome + @"\Extensions\abc\manifest.json", false)]
    [InlineData(@"C:\Users\Ann\AppData\Roaming\Code\User\settings.json", false)]
    [InlineData(@"C:\Users\Ann\AppData\Roaming\Code\User\workspaceStorage\a\state.vscdb", false)]
    [InlineData(@"C:\Users\Ann\AppData\Roaming\discord\Local Storage\leveldb\000003.log", false)]
    // The ordinary personal and system places.
    [InlineData(@"C:\Users\Ann\Documents\thesis.docx", false)]
    [InlineData(@"C:\Users\Ann\Desktop\a.txt", false)]
    [InlineData(@"C:\Windows\System32\kernel32.dll", false)]
    [InlineData(@"C:\Users\Ann\.ssh\id_ed25519", false)]
    // Folders next to a cache, with a similar name, are not the cache.
    [InlineData(@"C:\Users\Ann\AppData\Local\Temp2\x.bin", false)]
    [InlineData(@"C:\Users\Ann\.nuget\NuGet.Config", false)]
    public void Guard_built_from_the_system_list_allows_only_the_disposable_places(string path, bool allowed)
    {
        Assert.Equal(allowed, Guard().Check(path).IsAllowed);
    }

    [Fact]
    public void A_junction_inside_temp_is_not_followed()
    {
        var guard = new PathGuard(AiCatalog.AllowedRoots(Resolved()), [], new Probe(Temp + @"\link"));
        Assert.Equal(GuardVerdict.ReparsePoint, guard.Check(Temp + @"\link\victim.txt").Verdict);
    }
}

public class SystemCleanViewModelTests
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

    private const string Temp = @"C:\Users\Ann\AppData\Local\Temp";
    private const string Pip = @"C:\Users\Ann\AppData\Local\pip\Cache";
    private const string Npm = @"C:\Users\Ann\AppData\Local\npm-cache";
    private const string Docs = @"C:\Users\Ann\Documents";

    private static AiCleanViewModel Make(FakeFs fs) =>
        new(fs, new Env(), new Probe(), () => Now, SystemCatalog.All);

    [Fact]
    public async Task The_system_page_lists_system_places_and_not_AI_ones()
    {
        var fs = new FakeFs()
            .Add(Temp + @"\old.tmp", 500, ageDays: 30)
            .Add(Npm + @"\_cacache\x", 9000)
            .Add(Pip + @"\a.whl", 100);
        var vm = Make(fs);
        await vm.ScanAsync();

        Assert.Equal([Npm, Temp], vm.Items.Select(i => i.Path).ToArray());
        Assert.DoesNotContain(vm.Items, i => i.Path == Pip);
        Assert.True(vm.Items.Single(i => i.Path == Temp).IsSelected);
        Assert.False(vm.Items.Single(i => i.Path == Npm).IsSelected);
    }

    [Fact]
    public async Task Fresh_temp_files_do_not_make_a_card()
    {
        var fs = new FakeFs().Add(Temp + @"\just-now.tmp", 800, ageDays: 0);
        var vm = Make(fs);
        await vm.ScanAsync();
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Cleaning_removes_old_temp_files_and_keeps_new_ones_locked_ones_and_everything_else()
    {
        var fs = new FakeFs()
            .Add(Temp + @"\old.tmp", 500, ageDays: 30)
            .Add(Temp + @"\nested\old2.tmp", 700, ageDays: 30)
            .Add(Temp + @"\fresh.tmp", 900, ageDays: 0)
            .Add(Temp + @"\busy.tmp", 1100, ageDays: 30)
            .Add(Docs + @"\thesis.docx", 4000, ageDays: 400);
        fs.Locked.Add(Temp + @"\busy.tmp");

        var vm = Make(fs);
        await vm.ScanAsync();
        await vm.CleanAsync();

        Assert.Equal(CleanPhase.Done, vm.Phase);
        Assert.False(fs.Files.ContainsKey(Temp + @"\old.tmp"));
        Assert.False(fs.Files.ContainsKey(Temp + @"\nested\old2.tmp"));
        Assert.True(fs.Files.ContainsKey(Temp + @"\fresh.tmp"));
        Assert.True(fs.Files.ContainsKey(Temp + @"\busy.tmp"));
        Assert.True(fs.Files.ContainsKey(Docs + @"\thesis.docx"));
        Assert.Equal(500 + 700, vm.Report!.BytesFreed);
    }

    [Fact]
    public async Task A_dry_run_deletes_nothing()
    {
        var fs = new FakeFs().Add(Temp + @"\old.tmp", 500, ageDays: 30);
        var vm = Make(fs);
        await vm.ScanAsync();
        await vm.CleanAsync(dryRun: true);

        Assert.Empty(fs.DeleteCalls);
        Assert.True(fs.Files.ContainsKey(Temp + @"\old.tmp"));
    }
}

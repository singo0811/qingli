using TidyWin.Core.AiJunk;
using TidyWin.Core.Paths;
using Xunit;

namespace TidyWin.Core.Tests;

public class AiJunkTests
{
    private sealed class FakeEnv(Dictionary<string, string>? vars = null) : IAiEnvironment
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

    private sealed class NoReparse : IReparseProbe { public bool IsReparsePoint(string p) => false; }

    private static string PathOf(IEnumerable<ResolvedAiTarget> r, string id) => r.Single(x => x.Target.Id == id).Path;

    [Fact]
    public void Catalog_ids_are_unique()
    {
        var ids = AiCatalog.All.Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Only_regenerable_targets_are_selected_by_default()
    {
        foreach (var t in AiCatalog.All)
            Assert.Equal(t.Safety == AiSafety.Regenerable, t.SelectedByDefault);
    }

    [Fact]
    public void Wildcards_only_in_last_segment()
    {
        foreach (var t in AiCatalog.All.Where(t => t.HasWildcard))
        {
            var segs = t.RelativePath.Split('\\');
            Assert.DoesNotContain(segs[..^1], s => s.Contains('*'));
        }
    }

    [Fact]
    public void Default_locations_resolve_under_known_folders()
    {
        var r = AiCatalog.Resolve(new FakeEnv());
        Assert.Equal(@"C:\Users\Ann\.cache\huggingface\hub", PathOf(r, "hf.hub"));
        Assert.Equal(@"C:\Users\Ann\AppData\Local\pip\Cache", PathOf(r, "pip.cache"));
    }

    [Fact]
    public void Environment_variable_overrides_default()
    {
        var r = AiCatalog.Resolve(new FakeEnv(new() { ["HF_HUB_CACHE"] = @"D:\models\hub" }));
        Assert.Equal(@"D:\models\hub", PathOf(r, "hf.hub"));
    }

    [Fact]
    public void Home_variable_adds_suffix_and_hub_cache_wins_over_it()
    {
        var home = AiCatalog.Resolve(new FakeEnv(new() { ["HF_HOME"] = @"D:\hf" }));
        Assert.Equal(@"D:\hf\hub", PathOf(home, "hf.hub"));

        var both = AiCatalog.Resolve(new FakeEnv(new() { ["HF_HOME"] = @"D:\hf", ["HF_HUB_CACHE"] = @"E:\hub" }));
        Assert.Equal(@"E:\hub", PathOf(both, "hf.hub"));
    }

    [Fact]
    public void Multi_path_variable_uses_first_entry()
    {
        var r = AiCatalog.Resolve(new FakeEnv(new() { ["CONDA_PKGS_DIRS"] = @"D:\conda\pkgs;E:\other" }));
        Assert.Equal(@"D:\conda\pkgs", PathOf(r, "conda.pkgs"));
    }

    [Fact]
    public void Relative_override_falls_back_to_default()
    {
        var r = AiCatalog.Resolve(new FakeEnv(new() { ["HF_HUB_CACHE"] = @"models\hub" }));
        Assert.Equal(@"C:\Users\Ann\.cache\huggingface\hub", PathOf(r, "hf.hub"));
    }

    [Fact]
    public void Never_targets_are_not_allowed_roots_and_are_protected()
    {
        var r = AiCatalog.Resolve(new FakeEnv());
        var allowed = AiCatalog.AllowedRoots(r);
        var prot = AiCatalog.ProtectedPaths(r);
        Assert.DoesNotContain(@"C:\Users\Ann\AppData\Roaming\Cursor\User\workspaceStorage", allowed);
        Assert.Contains(@"C:\Users\Ann\AppData\Roaming\Cursor\User\workspaceStorage", prot);
    }

    [Fact]
    public void A_variable_pointing_at_a_drive_root_is_dropped_from_allowed_roots()
    {
        var r = AiCatalog.Resolve(new FakeEnv(new() { ["PIP_CACHE_DIR"] = @"C:\" }));
        Assert.DoesNotContain(@"C:\", AiCatalog.AllowedRoots(r));
    }

    [Fact]
    public void Guard_built_from_catalog_refuses_chat_history_and_accepts_cache_files()
    {
        var r = AiCatalog.Resolve(new FakeEnv());
        var guard = new PathGuard(AiCatalog.AllowedRoots(r), AiCatalog.ProtectedPaths(r), new NoReparse());

        Assert.True(guard.Check(@"C:\Users\Ann\.cache\huggingface\hub\models--a--b\blobs\x").IsAllowed);
        Assert.False(guard.Check(@"C:\Users\Ann\AppData\Roaming\Cursor\User\workspaceStorage\abc\state.vscdb").IsAllowed);
        Assert.False(guard.Check(@"C:\Users\Ann\Documents\thesis.docx").IsAllowed);
    }

    // ---- Hugging Face planner ----

    private static HfBlob B(string id, long n) => new(id, n);
    private static readonly DateTime Now = new(2026, 10, 16);

    [Fact]
    public void Old_revision_frees_only_blobs_the_kept_revision_does_not_use()
    {
        var repo = new HfRepo("models--a--b", ["new"],
        [
            new("old", Now.AddDays(-90), [B("config", 1), B("weights-v1", 100)]),
            new("new", Now.AddDays(-1), [B("config", 1), B("weights-v2", 120)]),
        ]);

        var plan = HfCachePlanner.StaleRevisions(repo);

        Assert.Equal(["old"], plan.RevisionsToRemove);
        Assert.Equal(["weights-v1"], plan.BlobsToRemove);
        Assert.Equal(100, plan.BytesFreed);
    }

    [Fact]
    public void Shared_blob_between_two_stale_revisions_is_counted_once()
    {
        var repo = new HfRepo("r", ["c"],
        [
            new("a", Now.AddDays(-50), [B("shared", 10)]),
            new("b", Now.AddDays(-40), [B("shared", 10), B("only-b", 5)]),
            new("c", Now.AddDays(-1), [B("fresh", 7)]),
        ]);

        var plan = HfCachePlanner.StaleRevisions(repo);

        Assert.Equal(15, plan.BytesFreed);
        Assert.Equal(2, plan.BlobsToRemove.Count);
    }

    [Fact]
    public void No_stale_revision_means_nothing_to_remove()
    {
        var repo = new HfRepo("r", ["a"], [new("a", Now, [B("x", 9)])]);
        var plan = HfCachePlanner.StaleRevisions(repo);
        Assert.Empty(plan.RevisionsToRemove);
        Assert.Equal(0, plan.BytesFreed);
    }

    [Fact]
    public void Repo_without_refs_keeps_its_newest_snapshot()
    {
        var repo = new HfRepo("r", [],
        [
            new("old", Now.AddDays(-30), [B("x", 4)]),
            new("newest", Now.AddDays(-2), [B("y", 6)]),
        ]);

        var plan = HfCachePlanner.StaleRevisions(repo);

        Assert.Equal(["old"], plan.RevisionsToRemove);
        Assert.Equal(4, plan.BytesFreed);
    }

    [Fact]
    public void Empty_repo_yields_empty_plan()
    {
        var plan = HfCachePlanner.StaleRevisions(new HfRepo("r", [], []));
        Assert.Empty(plan.RevisionsToRemove);
    }

    [Fact]
    public void Unused_repos_sorted_by_size_and_measured_by_unique_blobs()
    {
        var repos = new[]
        {
            new HfRepo("small-old", ["a"], [new("a", Now.AddDays(-100), [B("s", 10)])]),
            new HfRepo("big-old", ["a", "b"],
            [
                new("a", Now.AddDays(-100), [B("w", 500), B("c", 1)]),
                new("b", Now.AddDays(-95), [B("w", 500), B("d", 2)]),
            ]),
            new HfRepo("recent", ["a"], [new("a", Now.AddDays(-3), [B("z", 9999)])]),
        };

        var list = HfCachePlanner.UnusedRepos(repos, Now, 60);

        Assert.Equal(["big-old", "small-old"], list.Select(x => x.Repo).ToArray());
        Assert.Equal(503, list[0].BytesFreed);
    }

    [Fact]
    public void Unused_days_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HfCachePlanner.UnusedRepos([], Now, 0));
    }
}

using TidyWin.Core.Models;
using Xunit;
using static TidyWin.Core.Tests.CleanExecutorTests;

namespace TidyWin.Core.Tests;

public class OllamaInventoryTests
{
    private const string Root = @"C:\Users\Ann\.ollama\models";
    private static readonly string Shared = new('a', 64);
    private static readonly string OnlyA = new('b', 64);
    private static readonly string OnlyB = new('c', 64);
    private static readonly string Cfg = new('d', 64);

    private static string Manifest(params string[] hexes) =>
        "{\"schemaVersion\":2,\"config\":{\"digest\":\"sha256:" + Cfg + "\",\"size\":10},\"layers\":["
        + string.Join(",", hexes.Select(h => "{\"digest\":\"sha256:" + h + "\",\"size\":1}")) + "]}";

    private static FakeFs World() => new FakeFs()
        .AddText(Root + @"\manifests\registry.ollama.ai\library\llama3\8b", Manifest(Shared, OnlyA))
        .AddText(Root + @"\manifests\registry.ollama.ai\library\phi3\mini", Manifest(Shared, OnlyB))
        .Add(Root + $@"\blobs\sha256-{Shared}", 1000)
        .Add(Root + $@"\blobs\sha256-{OnlyA}", 300)
        .Add(Root + $@"\blobs\sha256-{OnlyB}", 200)
        .Add(Root + $@"\blobs\sha256-{Cfg}", 5);

    [Fact]
    public void Models_are_named_the_way_ollama_names_them()
    {
        var list = OllamaInventory.Scan(Root, World());
        Assert.Contains(list, e => e.Name == "llama3:8b");
        Assert.Contains(list, e => e.Name == "phi3:mini");
    }

    [Theory]
    [InlineData("registry.ollama.ai|library|llama3|8b", "llama3:8b")]
    [InlineData("registry.ollama.ai|jane|tuned|latest", "jane/tuned:latest")]
    [InlineData("hf.co|org|repo|q4", "hf.co/org/repo:q4")]
    public void Display_names(string rel, string expected) =>
        Assert.Equal(expected, OllamaInventory.DisplayName(rel.Split('|')));

    [Fact]
    public void A_shared_layer_only_counts_for_a_model_when_nobody_else_uses_it()
    {
        var all = OllamaInventory.Scan(Root, World());
        var llama = all.Single(e => e.Name == "llama3:8b");

        // Its own layer (300) + its config (5, shared with the other manifest via the same digest => kept) + manifest file.
        // The config blob is referenced by both manifests, so it stays.
        var own = ModelPlanner.ExclusiveBytes(llama, all);

        Assert.Equal(300 + llama.Parts[0].File.Length, own);
    }

    [Fact]
    public void Removing_both_models_frees_the_shared_layer_once()
    {
        var all = OllamaInventory.Scan(Root, World());

        var plan = ModelPlanner.Plan(all, all);

        var blobBytes = 1000 + 300 + 200 + 5;
        var manifestBytes = all.Sum(e => e.Parts[0].File.Length);
        Assert.Equal(blobBytes + manifestBytes, plan.Bytes);
        Assert.Equal(all.Count + 4, plan.Files.Count); // 2 manifests + 4 distinct blobs
    }

    [Fact]
    public void Removing_one_model_keeps_what_the_other_still_needs()
    {
        var all = OllamaInventory.Scan(Root, World());
        var llama = all.Single(e => e.Name == "llama3:8b");

        var plan = ModelPlanner.Plan([llama], all);

        var paths = plan.Files.Select(f => f.Path).ToArray();
        Assert.Contains(Root + $@"\blobs\sha256-{OnlyA}", paths);
        Assert.DoesNotContain(Root + $@"\blobs\sha256-{Shared}", paths);
        Assert.DoesNotContain(Root + $@"\blobs\sha256-{Cfg}", paths);
    }

    [Fact]
    public void A_manifest_cannot_point_outside_the_blob_folder()
    {
        var evil = "{\"layers\":[{\"digest\":\"sha256:..\\\\..\\\\..\\\\Windows\\\\win.ini\"},{\"digest\":\"sha256:" + new string('E', 64) + "\"},{\"digest\":\"../../x\"}]}";
        var fs = new FakeFs()
            .AddText(Root + @"\manifests\registry.ollama.ai\library\evil\x", evil)
            .Add(@"C:\Windows\win.ini", 99);

        var entry = Assert.Single(OllamaInventory.Scan(Root, fs));

        Assert.Single(entry.Parts); // just the manifest itself
    }

    [Fact]
    public void A_layer_missing_from_disk_is_simply_not_listed()
    {
        var fs = new FakeFs().AddText(Root + @"\manifests\registry.ollama.ai\library\m\t", Manifest(Shared));
        var entry = Assert.Single(OllamaInventory.Scan(Root, fs));
        Assert.Single(entry.Parts);
    }

    [Fact]
    public void A_broken_manifest_still_shows_up_so_it_can_be_removed()
    {
        var fs = new FakeFs().AddText(Root + @"\manifests\registry.ollama.ai\library\m\t", "{not json");
        Assert.Single(OllamaInventory.Scan(Root, fs));
    }

    [Fact]
    public void No_ollama_folder_means_no_models() =>
        Assert.Empty(OllamaInventory.Scan(Root, new FakeFs()));
}

public class LmStudioInventoryTests
{
    private const string Root = @"C:\Users\Ann\.lmstudio\models";

    [Fact]
    public void One_entry_per_publisher_and_repo_with_all_its_files()
    {
        var fs = new FakeFs()
            .Add(Root + @"\lmstudio-community\Llama-3-8B-GGUF\llama-3-8b-q4.gguf", 4000)
            .Add(Root + @"\lmstudio-community\Llama-3-8B-GGUF\llama-3-8b-q8.gguf", 8000)
            .Add(Root + @"\TheBloke\Mistral-GGUF\m.gguf", 3000);

        var list = LmStudioInventory.Scan(Root, fs);

        Assert.Equal(["lmstudio-community/Llama-3-8B-GGUF", "TheBloke/Mistral-GGUF"], list.Select(e => e.Name).ToArray());
        Assert.Equal(12000, list[0].OwnBytes);
        Assert.Equal("2 个文件", list[0].Note);
    }
}

public class HfInventoryTests
{
    private const string Hub = @"C:\Users\Ann\.cache\huggingface\hub";
    private const string Repo = Hub + @"\models--acme--tiny";

    private static FakeFs TwoVersions() => new FakeFs()
        .AddText(Repo + @"\refs\main", "newrev")
        .Add(Repo + @"\blobs\cfg", 10)
        .Add(Repo + @"\blobs\w1", 500)
        .Add(Repo + @"\blobs\w2", 600)
        .AddLink(Repo + @"\snapshots\oldrev\config.json", @"..\..\blobs\cfg", ageDays: 90)
        .AddLink(Repo + @"\snapshots\oldrev\model.bin", @"..\..\blobs\w1", ageDays: 90)
        .AddLink(Repo + @"\snapshots\newrev\config.json", @"..\..\blobs\cfg", ageDays: 2)
        .AddLink(Repo + @"\snapshots\newrev\model.bin", @"..\..\blobs\w2", ageDays: 2);

    [Fact]
    public void Repo_names_are_read_from_the_folder_name()
    {
        Assert.True(HfInventory.TryParseRepo("models--acme--tiny", out var kind, out var name));
        Assert.Equal("acme/tiny", name);
        Assert.Contains("模型", kind);
        Assert.False(HfInventory.TryParseRepo(".locks", out _, out _));
        Assert.False(HfInventory.TryParseRepo("weird--thing", out _, out _));
    }

    [Fact]
    public void Old_versions_become_their_own_entry_that_frees_only_what_the_current_version_does_not_use()
    {
        var all = HfInventory.Scan(Hub, TwoVersions());

        var whole = all.Single(e => e.Id == "hf:models--acme--tiny");
        var old = all.Single(e => e.Id == "hf-old:models--acme--tiny");

        Assert.Equal(10 + 500 + 600 + 6, whole.OwnBytes);   // all files incl. the ref (6 bytes)
        Assert.Equal(500, old.OwnBytes);                    // only the old weights
        Assert.Contains(old.Parts, p => p.File.Path.EndsWith(@"blobs\w1"));
        Assert.DoesNotContain(old.Parts, p => p.File.Path.EndsWith(@"blobs\w2"));
        Assert.DoesNotContain(old.Parts, p => p.File.Path.EndsWith(@"blobs\cfg")); // shared with the current version
        Assert.DoesNotContain(old.Parts, p => p.File.Path.Contains(@"snapshots\newrev"));
    }

    [Fact]
    public void Removing_the_old_entry_leaves_the_current_version_intact()
    {
        var all = HfInventory.Scan(Hub, TwoVersions());
        var old = all.Single(e => e.Id.StartsWith("hf-old:"));

        var plan = ModelPlanner.Plan([old], all);

        Assert.DoesNotContain(plan.Files, f => f.Path.Contains("newrev") || f.Path.EndsWith("w2") || f.Path.EndsWith("cfg"));
        Assert.Equal(500, plan.Bytes);
    }

    [Fact]
    public void Selecting_both_entries_does_not_count_anything_twice()
    {
        var all = HfInventory.Scan(Hub, TwoVersions());

        var plan = ModelPlanner.Plan(all, all);

        Assert.Equal(all.Single(e => e.Id.StartsWith("hf:")).OwnBytes, plan.Bytes);
    }

    [Fact]
    public void A_single_version_has_no_old_entry()
    {
        var fs = new FakeFs()
            .AddText(Repo + @"\refs\main", "only")
            .Add(Repo + @"\blobs\w", 100)
            .AddLink(Repo + @"\snapshots\only\m.bin", @"..\..\blobs\w");

        var all = HfInventory.Scan(Hub, fs);

        Assert.Single(all);
    }

    [Fact]
    public void Without_a_readable_ref_old_versions_are_not_offered()
    {
        var fs = TwoVersions();
        fs.Files.Remove(Repo + @"\refs\main");

        var all = HfInventory.Scan(Hub, fs);

        Assert.DoesNotContain(all, e => e.Id.StartsWith("hf-old:"));
    }

    [Fact]
    public void Real_copies_instead_of_links_are_handled()
    {
        var fs = new FakeFs()
            .AddText(Repo + @"\refs\main", "newrev")
            .Add(Repo + @"\snapshots\oldrev\model.bin", 700, ageDays: 80)
            .Add(Repo + @"\snapshots\newrev\model.bin", 800, ageDays: 1);

        var old = HfInventory.Scan(Hub, fs).Single(e => e.Id.StartsWith("hf-old:"));

        Assert.Equal(700, old.OwnBytes);
    }

    [Fact]
    public void A_link_pointing_outside_the_cache_never_adds_anything_outside_to_the_plan()
    {
        var fs = TwoVersions()
            .AddLink(Repo + @"\snapshots\oldrev\sneaky.bin", @"..\..\..\..\..\..\Windows\win.ini", ageDays: 90);
        fs.Add(@"C:\Windows\win.ini", 999);

        var all = HfInventory.Scan(Hub, fs);
        var plan = ModelPlanner.Plan(all, all);

        Assert.DoesNotContain(plan.Files, f => f.Path.StartsWith(@"C:\Windows"));
    }

    [Fact]
    public void Entries_are_sorted_biggest_first()
    {
        var fs = TwoVersions()
            .Add(Hub + @"\models--other--big\blobs\x", 99999)
            .AddLink(Hub + @"\models--other--big\snapshots\r\m.bin", @"..\..\blobs\x")
            .AddText(Hub + @"\models--other--big\refs\main", "r");

        var all = HfInventory.Scan(Hub, fs);

        Assert.Equal("other/big", all[0].Name);
    }
}

public class ModelPlannerTests
{
    private static ModelEntry E(string id, params (string path, long len, string? key)[] parts) =>
        new(id, ModelSource.Ollama, id, "k", DateTime.UtcNow,
            parts.Select(p => new ModelPart(new TidyWin.Core.Clean.FsFile(p.path, p.len, DateTime.UtcNow, false), p.key)).ToList());

    [Fact]
    public void Unshared_parts_always_go()
    {
        var a = E("a", (@"C:\m\a1", 10, null));
        Assert.Equal(10, ModelPlanner.Plan([a], [a]).Bytes);
    }

    [Fact]
    public void A_part_shared_with_a_model_that_stays_is_kept()
    {
        var a = E("a", (@"C:\m\a", 10, null), (@"C:\m\s", 100, "k"));
        var b = E("b", (@"C:\m\b", 20, null), (@"C:\m\s", 100, "k"));
        Assert.Equal(10, ModelPlanner.Plan([a], [a, b]).Bytes);
    }

    [Fact]
    public void A_part_shared_between_two_selected_models_is_counted_once()
    {
        var a = E("a", (@"C:\m\s", 100, "k"));
        var b = E("b", (@"C:\m\s", 100, "k"));
        Assert.Equal(100, ModelPlanner.Plan([a, b], [a, b]).Bytes);
    }
}

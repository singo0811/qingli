using TidyWin.Core.AiJunk;
using TidyWin.Core.Memory;
using TidyWin.Platform;
using TidyWin.ViewModels;
using Xunit;

namespace TidyWin.Platform.Tests;

/// <summary>Real files, real file system, only the user's profile folder is redirected into a sandbox.</summary>
public sealed class RealModelsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "tidywin-models-" + Guid.NewGuid().ToString("N"));

    private sealed class SandboxEnv(string home) : IAiEnvironment
    {
        public string? Variable(string name) => null;
        public string Folder(KnownFolder f) => f switch
        {
            KnownFolder.UserProfile => home,
            KnownFolder.LocalAppData => Path.Combine(home, "Local"),
            KnownFolder.AppData => Path.Combine(home, "Roaming"),
            _ => Path.Combine(home, "Temp"),
        };
    }

    private sealed class NoOllama : IOllama
    {
        public Task<IReadOnlyList<LoadedModel>?> ListLoadedAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LoadedModel>?>(null);
        public Task<bool> UnloadAsync(string model, CancellationToken ct = default) => Task.FromResult(false);
    }

    public RealModelsTests() => Directory.CreateDirectory(_home);
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private ModelsViewModel Make() =>
        new(new WindowsFileSystem(), new SandboxEnv(_home), new FileSystemReparseProbe(), new NoOllama(), minListedBytes: 0);

    private string Write(string rel, string text)
    {
        var p = Path.Combine(_home, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    private static string Hex(char c) => new(c, 64);

    [Fact]
    public async Task Ollama_models_on_a_real_disk_can_be_listed_and_one_removed_without_hurting_the_other()
    {
        var m = @".ollama\models\manifests\registry.ollama.ai\library\";
        Write(m + @"llama3\8b", "{\"layers\":[{\"digest\":\"sha256:" + Hex('a') + "\"},{\"digest\":\"sha256:" + Hex('b') + "\"}]}");
        Write(m + @"phi3\mini", "{\"layers\":[{\"digest\":\"sha256:" + Hex('a') + "\"},{\"digest\":\"sha256:" + Hex('c') + "\"}]}");
        var shared = Write($@".ollama\models\blobs\sha256-{Hex('a')}", new string('s', 1000));
        var onlyLlama = Write($@".ollama\models\blobs\sha256-{Hex('b')}", new string('l', 300));
        var onlyPhi = Write($@".ollama\models\blobs\sha256-{Hex('c')}", new string('p', 200));

        var vm = Make();
        await vm.ScanAsync();
        Assert.Equal(2, vm.Ollama.Count);

        vm.Ollama.Single(i => i.Name == "llama3:8b").IsSelected = true;
        await vm.CleanAsync();

        Assert.Equal(CleanPhase.Done, vm.Phase);
        Assert.False(File.Exists(onlyLlama));
        Assert.True(File.Exists(shared));     // phi3 still needs it
        Assert.True(File.Exists(onlyPhi));
        Assert.True(File.Exists(Path.Combine(_home, @".ollama\models\manifests\registry.ollama.ai\library\phi3\mini")));
        Assert.False(Directory.Exists(Path.Combine(_home, @".ollama\models\manifests\registry.ollama.ai\library\llama3")));

        // The model that stayed is still a complete, readable model.
        await vm.ScanAsync();
        var phi = Assert.Single(vm.Ollama);
        Assert.Equal("phi3:mini", phi.Name);
        Assert.Equal(1000 + 200 + phi.Entry.Parts[0].File.Length, phi.Entry.OwnBytes);
    }

    [Fact]
    public async Task Hugging_face_old_versions_in_copy_mode_are_removed_and_the_current_one_survives()
    {
        var repo = @".cache\huggingface\hub\models--acme--tiny\";
        Write(repo + @"refs\main", "newrev");
        var oldFile = Write(repo + @"snapshots\oldrev\model.bin", new string('o', 700));
        var newFile = Write(repo + @"snapshots\newrev\model.bin", new string('n', 800));
        var token = Write(@".cache\huggingface\token", "hf_secret");

        // make the old copy look old
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-120));
        var vm = Make();
        await vm.ScanAsync();
        var old = vm.HuggingFace.Single(i => i.Entry.Id.StartsWith("hf-old:"));
        Assert.Equal(700, old.FreeBytes);

        old.IsSelected = true;
        await vm.CleanAsync();

        Assert.Equal(700, vm.Report!.BytesFreed);
        Assert.False(File.Exists(oldFile));
        Assert.False(Directory.Exists(Path.GetDirectoryName(oldFile)!));   // emptied folder removed
        Assert.True(File.Exists(newFile));
        Assert.True(File.Exists(Path.Combine(_home, repo, @"refs\main")));
        Assert.True(File.Exists(token));
    }

    [Fact]
    public async Task Removing_a_whole_hugging_face_repo_never_touches_the_login_token_next_to_it()
    {
        var repo = @".cache\huggingface\hub\models--acme--tiny\";
        Write(repo + @"refs\main", "r");
        var f = Write(repo + @"snapshots\r\model.bin", new string('x', 100));
        var token = Write(@".cache\huggingface\token", "hf_secret");

        var vm = Make();
        await vm.ScanAsync();
        vm.HuggingFace.Single(i => i.Entry.Id == "hf:models--acme--tiny").IsSelected = true;
        await vm.CleanAsync();

        Assert.False(File.Exists(f));
        Assert.False(Directory.Exists(Path.Combine(_home, repo.TrimEnd('\\')))); // repo folder gone
        Assert.True(Directory.Exists(Path.Combine(_home, @".cache\huggingface\hub"))); // the hub root itself stays
        Assert.True(File.Exists(token));
    }

    [Fact]
    public async Task Lm_studio_repo_is_removed_as_a_unit()
    {
        var a = Write(@".lmstudio\models\pub\repo\a.gguf", new string('a', 400));
        var b = Write(@".lmstudio\models\pub\repo\b.gguf", new string('b', 600));
        var other = Write(@".lmstudio\models\pub\keep\k.gguf", "keep");

        var vm = Make();
        await vm.ScanAsync();
        vm.LmStudio.Single(i => i.Name == "pub/repo").IsSelected = true;
        await vm.CleanAsync();

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(other));
        Assert.Equal(1000, vm.Report!.BytesFreed);
    }

    [Fact]
    public void Real_symlinks_are_reported_as_links_and_their_target_is_readable()
    {
        var blob = Write(@"t\blobs\w1", "weights");
        var link = Path.Combine(_home, @"t\snapshots\r\model.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try { File.CreateSymbolicLink(link, @"..\..\blobs\w1"); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; } // needs admin or developer mode

        var fs = new WindowsFileSystem();
        var found = fs.EnumerateFilesRecursive(Path.Combine(_home, "t")).Single(f => f.Path == link);

        Assert.True(found.IsLink);
        Assert.EndsWith("w1", fs.LinkTarget(link));
        Assert.True(File.Exists(blob));
    }

    [Fact]
    public void Read_text_refuses_big_files_and_missing_files()
    {
        var fs = new WindowsFileSystem();
        var p = Write("x.txt", new string('z', 50));

        Assert.Equal(50, fs.ReadText(p, 100)!.Length);
        Assert.Null(fs.ReadText(p, 10));
        Assert.Null(fs.ReadText(Path.Combine(_home, "nope.txt"), 100));
        Assert.Null(fs.LinkTarget(p)); // an ordinary file is not a link
    }
}

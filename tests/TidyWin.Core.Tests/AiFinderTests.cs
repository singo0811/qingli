using TidyWin.Core.AiJunk;
using Xunit;
using static TidyWin.Core.Tests.CleanExecutorTests;

namespace TidyWin.Core.Tests;

public class AiFinderTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long Mb = 1024L * 1024;

    private static IReadOnlyList<FoundItem> Run(FakeFs fs, FinderOptions? o = null) =>
        AiFinder.Find([@"D:\"], fs, o ?? new FinderOptions());

    [Fact]
    public void A_model_file_in_a_known_ui_folder_is_named_after_it_and_left_to_the_person()
    {
        var fs = new FakeFs().Add(@"D:\AI\ComfyUI\models\checkpoints\sdxl.safetensors", 6 * Gb);
        var item = Assert.Single(Run(fs));

        Assert.Equal(FoundKind.Model, item.Kind);
        Assert.Equal("ComfyUI", item.Owner);
        Assert.Equal("SAFETENSORS", item.Format);
        Assert.Equal(FoundSafety.Careful, item.Safety);
        Assert.Contains("重新下载", item.IfDeleted);
    }

    [Fact]
    public void Small_files_and_other_kinds_of_files_are_ignored()
    {
        var fs = new FakeFs()
            .Add(@"D:\AI\small.safetensors", 50 * Mb)
            .Add(@"D:\AI\movie.mkv", 8 * Gb)
            .Add(@"D:\AI\notes.txt", 10);
        Assert.Empty(Run(fs));
    }

    [Fact]
    public void A_pt_file_next_to_a_program_is_not_offered_because_it_is_probably_that_programs_data()
    {
        var fs = new FakeFs()
            .Add(@"D:\Apps\Editor\editor.exe", 10 * Mb)
            .Add(@"D:\Apps\Editor\data\denoise.pt", 400 * Mb);
        Assert.Empty(Run(fs));
    }

    [Fact]
    public void The_same_pt_file_in_a_models_folder_is_offered()
    {
        var fs = new FakeFs().Add(@"D:\work\models\denoise.pt", 400 * Mb);
        Assert.Single(Run(fs));
    }

    [Fact]
    public void An_unrecognised_model_beside_a_program_gets_a_warning_instead_of_a_calm_verdict()
    {
        var fs = new FakeFs()
            .Add(@"D:\Apps\Studio\studio.exe", 10 * Mb)
            .Add(@"D:\Apps\Studio\bundled\voice.gguf", 900 * Mb);
        var item = Assert.Single(Run(fs));

        Assert.Equal(FoundSafety.Careful, item.Safety);
        Assert.Contains("软件自带", item.Headline);
    }

    [Fact]
    public void A_second_copy_on_the_same_disk_is_merged_not_deleted_and_the_first_one_is_left_alone()
    {
        var fs = new FakeFs()
            .Add(@"D:\old\llama.gguf", 4 * Gb, ageDays: 300)
            .Add(@"D:\new\LLaMA.gguf", 4 * Gb, ageDays: 10);
        var items = Run(fs);

        var copy = items.Single(i => i.Path.Contains(@"\new\"));
        var original = items.Single(i => i.Path.Contains(@"\old\"));
        Assert.Equal(FoundSafety.Safe, copy.Safety);
        Assert.Equal(FoundAction.Merge, copy.Action);
        Assert.Equal(@"D:\old\llama.gguf", copy.SameAs);
        Assert.Contains("不会删除", copy.IfDeleted);
        Assert.Equal(FoundSafety.Careful, original.Safety);
        Assert.Equal(FoundAction.Recycle, original.Action);
        Assert.Contains("另有 1 份", original.Headline);
        Assert.Equal(4 * Gb, AiFinder.SafeBytes(items));
    }

    [Fact]
    public void A_copy_on_another_disk_cannot_be_merged_so_it_is_not_called_safe()
    {
        var fs = new FakeFs()
            .Add(@"D:\old\llama.gguf", 4 * Gb, ageDays: 300)
            .Add(@"E:\new\llama.gguf", 4 * Gb, ageDays: 10);
        var items = AiFinder.Find([@"D:\", @"E:\"], fs);

        var copy = items.Single(i => i.Path.StartsWith(@"E:\"));
        Assert.Equal(FoundSafety.Careful, copy.Safety);
        Assert.Equal(FoundAction.Recycle, copy.Action);
        Assert.Contains("无法合并", copy.Headline);
        Assert.Equal(0, AiFinder.SafeBytes(items));
    }

    [Fact]
    public void Paths_that_are_already_the_same_file_take_no_space_and_are_not_reported_again()
    {
        var fs = new FakeFs()
            .Add(@"D:\a\m.gguf", 4 * Gb, ageDays: 300)
            .Add(@"D:\b\m.gguf", 4 * Gb, ageDays: 200)
            .Add(@"D:\c\m.gguf", 4 * Gb, ageDays: 10);
        // a and b were merged earlier: they are one file on disk.
        var o = new FinderOptions
        {
            SameFile = (x, y) => new[] { x, y }.All(p => p.StartsWith(@"D:\a\") || p.StartsWith(@"D:\b\")),
        };
        var items = Run(fs, o);

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.Path.StartsWith(@"D:\b\"));
        Assert.Equal(FoundAction.Merge, items.Single(i => i.Path.StartsWith(@"D:\c\")).Action);
    }

    [Fact]
    public void A_copy_inside_a_packaged_program_is_merged_which_keeps_that_programs_path_working()
    {
        // The real machine had six copies of one model inside "portable" packages. Deleting any would break a package;
        // merging cannot, because every path keeps opening the same content.
        var fs = new FakeFs()
            .Add(@"D:\work\embedding\gguf\e.gguf", 500 * Mb, ageDays: 100)
            .Add(@"D:\release\native-package\portable-1\embedding\gguf\e.gguf", 500 * Mb, ageDays: 5);
        var copy = Run(fs).Single(i => i.Path.Contains("portable"));

        Assert.Equal(FoundAction.Merge, copy.Action);
        Assert.Contains("不会删除", copy.IfDeleted);
    }

    [Fact]
    public void Same_name_but_different_size_is_not_a_copy()
    {
        var fs = new FakeFs()
            .Add(@"D:\a\m.gguf", 4 * Gb)
            .Add(@"D:\b\m.gguf", 5 * Gb);
        Assert.DoesNotContain(Run(fs), i => i.Safety == FoundSafety.Safe);
    }

    [Fact]
    public void A_tensorrt_engine_can_be_regenerated()
    {
        var fs = new FakeFs().Add(@"D:\AI\ComfyUI\models\tensorrt\sd.engine", 3 * Gb);
        var item = Assert.Single(Run(fs));
        Assert.Equal(FoundSafety.Safe, item.Safety);
        Assert.Contains("重新生成", item.IfDeleted);
    }

    [Fact]
    public void Whatever_is_running_in_the_owners_folder_is_marked_in_use_and_never_safe()
    {
        var fs = new FakeFs()
            .Add(@"D:\AI\ComfyUI\models\checkpoints\a.safetensors", 6 * Gb)
            .Add(@"D:\AI\ComfyUI\models\tensorrt\a.engine", 2 * Gb);
        var o = new FinderOptions { IsRunningUnder = p => p.StartsWith(@"D:\AI\ComfyUI", StringComparison.OrdinalIgnoreCase) };

        Assert.All(Run(fs, o), i => Assert.Equal(FoundSafety.InUse, i.Safety));
    }

    [Fact]
    public void Folders_other_pages_already_manage_are_left_out()
    {
        var fs = new FakeFs()
            .Add(@"D:\LMStudio\models\x.gguf", 4 * Gb)
            .Add(@"D:\mine\y.gguf", 4 * Gb + 1);
        var o = new FinderOptions { ExcludeUnder = [@"D:\LMStudio\models"] };
        Assert.Equal([@"D:\mine\y.gguf"], Run(fs, o).Select(i => i.Path).ToArray());
    }

    [Fact]
    public void Jan_does_not_match_january()
    {
        var fs = new FakeFs().Add(@"D:\january\m.gguf", 4 * Gb);
        Assert.Equal("未识别位置", Assert.Single(Run(fs)).Owner);
    }

    // ---------------------------------------------------------------- environments

    private static FakeFs Venv(FakeFs fs, string root, long size)
    {
        fs.AddText(root + @"\pyvenv.cfg", "home = C:\\Python");
        fs.Add(root + @"\Lib\site-packages\torch\big.dll", size);
        return fs;
    }

    [Fact]
    public void A_venv_with_a_requirements_file_gets_a_rebuild_hint_but_is_never_called_safe()
    {
        var fs = Venv(new FakeFs(), @"D:\proj\.venv", 4 * Gb).AddText(@"D:\proj\requirements.txt", "torch\n");
        fs.Add(@"D:\proj\.venv\Scripts\python.exe", 10); // its own python is not "a program next to it"
        var env = Assert.Single(Run(fs));

        Assert.Equal(FoundKind.Environment, env.Kind);
        Assert.Equal(FoundSafety.Careful, env.Safety);
        Assert.Contains("requirements.txt", env.Headline);
        Assert.Contains("大概率", env.Headline);
        Assert.Contains("CUDA", env.IfDeleted); // says what a manifest cannot restore
        Assert.True(env.Bytes >= 4 * Gb);
    }

    [Theory]
    [InlineData(@"D:\Users\Ann\AppData\Local\FreeToken\venv")]
    [InlineData(@"D:\Users\Ann\.pplx\users\x\template\venv")]
    [InlineData(@"D:\WeGameApps\LoL\Cross\coach\Python311")]
    [InlineData(@"D:\ProgramData\Tool\venv")]
    public void An_environment_in_a_programs_own_place_is_flagged_as_that_programs_and_never_advised_to_be_deleted(string root)
    {
        var fs = Venv(new FakeFs(), root, 2 * Gb).AddText(Path.GetDirectoryName(root) + @"\requirements.txt", "x");
        var env = Assert.Single(Run(fs, new FinderOptions { UserProfile = @"D:\Users\Ann" }));

        Assert.Equal(FoundSafety.Careful, env.Safety);
        Assert.Contains("软件自己", env.Headline);
        Assert.DoesNotContain("pip freeze", env.IfDeleted);
    }

    [Fact]
    public void Environments_in_the_tool_stores_a_person_manages_are_not_treated_as_a_programs_private_data()
    {
        var fs = Venv(new FakeFs(), @"D:\Users\Ann\.virtualenvs\myproj", 2 * Gb);
        var env = Assert.Single(Run(fs, new FinderOptions { UserProfile = @"D:\Users\Ann" }));
        Assert.DoesNotContain("软件自己", env.Headline);
    }

    [Fact]
    public void A_model_in_a_programs_private_folder_is_flagged_instead_of_described_as_yours()
    {
        var fs = new FakeFs().Add(@"D:\Users\Ann\.dsh\voice-app\Qwen\model.safetensors", 3 * Gb);
        var item = Assert.Single(Run(fs, new FinderOptions { UserProfile = @"D:\Users\Ann" }));

        Assert.Equal(FoundSafety.Careful, item.Safety);
        Assert.Contains("软件自己的数据目录", item.Headline);
    }

    [Fact]
    public void No_environment_is_ever_safe()
    {
        var fs = Venv(new FakeFs(), @"D:\a\.venv", 4 * Gb).AddText(@"D:\a\requirements.txt", "x");
        Venv(fs, @"D:\b\.venv", 4 * Gb);
        Assert.DoesNotContain(Run(fs), i => i.Kind == FoundKind.Environment && i.Safety == FoundSafety.Safe);
    }

    [Fact]
    public void A_venv_without_any_dependency_list_is_left_to_the_person_with_advice()
    {
        var fs = Venv(new FakeFs(), @"D:\proj\.venv", 4 * Gb);
        var env = Assert.Single(Run(fs));

        Assert.Equal(FoundSafety.Careful, env.Safety);
        Assert.Contains("pip freeze", env.IfDeleted);
    }

    [Fact]
    public void A_small_environment_is_not_worth_mentioning()
    {
        Assert.Empty(Run(Venv(new FakeFs(), @"D:\proj\.venv", 20 * Mb)));
    }

    [Fact]
    public void The_conda_installation_itself_is_never_offered_but_its_environments_are()
    {
        var fs = new FakeFs()
            .AddText(@"D:\miniconda3\conda-meta\history", "x")
            .Add(@"D:\miniconda3\conda-meta\python.json", 10)
            .Add(@"D:\miniconda3\condabin\conda.bat", 10)
            .Add(@"D:\miniconda3\pkgs\big\x.dll", 5 * Gb)
            .Add(@"D:\miniconda3\envs\llm\conda-meta\torch.json", 10)
            .Add(@"D:\miniconda3\envs\llm\Lib\torch.dll", 3 * Gb);
        fs.Dirs.Add(@"D:\miniconda3\condabin");
        fs.Dirs.Add(@"D:\miniconda3\envs");

        var items = Run(fs);

        Assert.DoesNotContain(items, i => i.Path == @"D:\miniconda3");
        var env = Assert.Single(items, i => i.Path == @"D:\miniconda3\envs\llm");
        Assert.Equal("conda 环境", env.Format);
        Assert.Equal(FoundSafety.Careful, env.Safety);
    }

    [Fact]
    public void An_environment_nested_inside_another_is_reported_once()
    {
        var fs = Venv(new FakeFs(), @"D:\proj\.venv", 4 * Gb);
        fs.AddText(@"D:\proj\.venv\Lib\site-packages\pkg\tests\venv\pyvenv.cfg", "x");
        Assert.Single(Run(fs));
    }

    [Fact]
    public void An_environment_that_a_running_program_uses_is_in_use()
    {
        var fs = Venv(new FakeFs(), @"D:\proj\.venv", 4 * Gb).AddText(@"D:\proj\requirements.txt", "x");
        fs.Add(@"D:\proj\.venv\Scripts\python.exe", 10); // its own python must not count as "a program next to it"
        var o = new FinderOptions { IsRunningUnder = p => p.Equals(@"D:\proj\.venv", StringComparison.OrdinalIgnoreCase) };
        Assert.Equal(FoundSafety.InUse, Assert.Single(Run(fs, o)).Safety);
    }

    [Fact]
    public void Results_are_biggest_first()
    {
        var fs = new FakeFs()
            .Add(@"D:\a\small.gguf", 300 * Mb)
            .Add(@"D:\b\huge.gguf", 9 * Gb)
            .Add(@"D:\c\mid.gguf", 2 * Gb);
        Assert.Equal(["huge.gguf", "mid.gguf", "small.gguf"], Run(fs).Select(i => Path.GetFileName(i.Path)).ToArray());
    }
}

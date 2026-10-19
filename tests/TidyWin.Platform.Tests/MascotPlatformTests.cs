using TidyWin.Core.Mascot;
using TidyWin.Platform;
using Xunit;

namespace TidyWin.Platform.Tests;

public class AiToolDetectorTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out = output;
    private static readonly DateTime T = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

    private static AiToolDetector With(string[] processes, Dictionary<string, DateTime>? folders = null) =>
        new(() => processes,
            p => folders is not null && folders.TryGetValue(p, out var t) ? t : null,
            s => s.Replace("%USERPROFILE%", @"C:\U").Replace("%APPDATA%", @"C:\U\AppData\Roaming").Replace("%LOCALAPPDATA%", @"C:\U\AppData\Local"));

    private static ToolSignal Of(IReadOnlyList<ToolSignal> l, AiTool t) => l.Single(x => x.Tool == t);

    [Fact]
    public void Every_known_tool_is_reported_even_when_absent()
    {
        var l = With([]).Detect();
        Assert.Equal(Enum.GetValues<AiTool>().Length, l.Count);
        Assert.All(l, s => { Assert.False(s.Running); Assert.Null(s.LastActiveUtc); });
    }

    [Fact]
    public void A_running_program_is_noticed_whatever_the_letter_case()
    {
        var l = With(["OLLAMA", "Cursor", "notepad"]).Detect();
        Assert.True(Of(l, AiTool.Ollama).Running);
        Assert.True(Of(l, AiTool.Cursor).Running);
        Assert.False(Of(l, AiTool.LmStudio).Running);
    }

    private const string StoreChatGpt = @"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop_1.2026.1_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
    private const string CodexApp = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.1007.9446.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";

    private static AiToolDetector WithPaths(string[] processes, params string[] chatGptPaths) =>
        new(() => processes, _ => chatGptPaths, _ => null, s => s.Replace("%USERPROFILE%", @"C:\U").Replace("%APPDATA%", @"C:\U\R").Replace("%LOCALAPPDATA%", @"C:\U\L"));

    [Fact]
    public void Claude_and_ChatGpt_are_told_apart()
    {
        var gpt = WithPaths(["ChatGPT"], StoreChatGpt).Detect();
        Assert.True(Of(gpt, AiTool.ChatGpt).Running);
        Assert.False(Of(gpt, AiTool.Claude).Running);

        var claude = With(["claude"]).Detect();
        Assert.True(Of(claude, AiTool.Claude).Running);
        Assert.False(Of(claude, AiTool.ChatGpt).Running);
    }

    // OpenAI's Codex desktop app is also called ChatGPT.exe (seen on a real machine). It must not dress her as ChatGPT.
    [Fact]
    public void The_Codex_app_that_is_also_called_ChatGPT_exe_is_not_ChatGPT() =>
        Assert.False(Of(WithPaths(["ChatGPT"], CodexApp).Detect(), AiTool.ChatGpt).Running);

    [Fact]
    public void When_both_run_under_the_same_name_the_real_ChatGPT_still_counts() =>
        Assert.True(Of(WithPaths(["ChatGPT"], CodexApp, StoreChatGpt).Detect(), AiTool.ChatGpt).Running);

    [Fact]
    public void A_ChatGPT_exe_whose_path_cannot_be_read_is_not_trusted() =>
        Assert.False(Of(WithPaths(["ChatGPT"]).Detect(), AiTool.ChatGpt).Running);

    [Fact]
    public void The_OpenAI_folder_that_Codex_uses_is_not_taken_as_a_sign_of_ChatGPT()
    {
        var probed = new List<string>();
        new AiToolDetector(() => [], p => { probed.Add(p); return null; },
            s => s.Replace("%USERPROFILE%", @"C:\U").Replace("%APPDATA%", @"C:\U\R").Replace("%LOCALAPPDATA%", @"C:\U\L")).Detect();
        Assert.DoesNotContain(@"C:\U\L\OpenAI", probed);
        Assert.Contains(@"C:\U\L\Packages\OpenAI.ChatGPT-Desktop_*", probed);
    }

    [Fact]
    public void ComfyUi_is_looked_for_where_its_documentation_says()
    {
        var l = With([], new()
        {
            [@"C:\U\AppData\Local\Programs\ComfyUI"] = T,
            [@"C:\U\AppData\Roaming\ComfyUI"] = T.AddDays(-3),
        }).Detect();
        Assert.Equal(T, Of(l, AiTool.ComfyUi).LastActiveUtc);
    }

    [Fact]
    public void LM_Studio_app_state_folder_counts_too()
    {
        var l = With([], new() { [@"C:\U\AppData\Roaming\LM Studio"] = T }).Detect();
        Assert.Equal(T, Of(l, AiTool.LmStudio).LastActiveUtc);
    }

    // ───────── folders with a random suffix (Microsoft Store apps) ─────────

    [Fact]
    public void A_star_folder_matches_the_newest_folder_that_starts_that_way()
    {
        var root = Path.Combine(Path.GetTempPath(), "tidywin-pk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var old = Directory.CreateDirectory(Path.Combine(root, "Claude_aaaa"));
            var fresh = Directory.CreateDirectory(Path.Combine(root, "Claude_bbbb"));
            Directory.CreateDirectory(Path.Combine(root, "Other_cccc"));
            old.LastWriteTimeUtc = T.AddDays(-30);
            fresh.LastWriteTimeUtc = T;

            Assert.Equal(T, AiToolDetector.LastChanged(Path.Combine(root, "Claude_*")));
            Assert.Null(AiToolDetector.LastChanged(Path.Combine(root, "Nothing_*")));
        }
        finally { try { Directory.Delete(root, true); } catch { /* temp folder */ } }
    }

    [Fact]
    public void A_star_in_the_wrong_place_or_a_missing_parent_is_just_not_found()
    {
        Assert.Null(AiToolDetector.LastChanged(@"C:\definitely-not-here-*\x"));
        Assert.Null(AiToolDetector.LastChanged(@"C:\definitely-not-here-123\Claude_*"));
        Assert.Null(AiToolDetector.LastChanged(@"C:\definitely-not-here-123"));
    }

    [Fact]
    public void The_newest_of_a_tools_folders_is_its_last_activity()
    {
        var l = With([], new()
        {
            [@"C:\U\.lmstudio"] = T.AddDays(-40),
            [@"C:\U\.cache\lm-studio"] = T,
        }).Detect();
        Assert.Equal(T, Of(l, AiTool.LmStudio).LastActiveUtc);
    }

    [Fact]
    public void A_folder_with_an_unresolved_variable_is_skipped_not_probed()
    {
        var probed = new List<string>();
        var d = new AiToolDetector(() => [], p => { probed.Add(p); return null; }, s => s); // nothing expanded
        d.Detect();
        Assert.Empty(probed);
    }

    [Fact]
    public void Detection_feeds_the_picker_end_to_end()
    {
        var l = With(["ollama"], new() { [@"C:\U\.cache\huggingface"] = T }).Detect();
        Assert.Equal(AiTool.Ollama, Personas.Pick(l, T.AddHours(1)));
    }

    [Fact]
    public void The_real_detector_runs_on_this_machine_and_is_quick()
    {
        var real = new AiToolDetector();
        real.Detect(); // warm up
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var l = real.Detect();
        foreach (var s in l) _out.WriteLine($"{s.Tool,-12} running={s.Running,-5} last={s.LastActiveUtc:yyyy-MM-dd}");
        Assert.Equal(Enum.GetValues<AiTool>().Length, l.Count);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms");
    }
}

public class AppSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tidywin-settings-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* temp folder */ }
    }

    [Fact]
    public void A_missing_file_means_defaults() =>
        Assert.Equal("auto", AppSettings.Load(File_).Outfit);

    [Fact]
    public void A_choice_survives_a_round_trip()
    {
        Assert.True(new AppSettings { Outfit = "llama" }.Save(File_));
        Assert.Equal("llama", AppSettings.Load(File_).Outfit);
    }

    [Fact]
    public void Saving_twice_overwrites_and_leaves_no_temp_file()
    {
        new AppSettings { Outfit = "llama" }.Save(File_);
        new AppSettings { Outfit = "hardhat" }.Save(File_);
        Assert.Equal("hardhat", AppSettings.Load(File_).Outfit);
        Assert.Equal(["settings.json"], Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"outfit\": 7}")]
    [InlineData("{\"other\": \"x\"}")]
    [InlineData("")]
    public void A_damaged_file_means_defaults_not_a_crash(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, content);
        Assert.Equal("auto", AppSettings.Load(File_).Outfit);
    }

    [Fact]
    public void An_unknown_outfit_in_the_file_is_cleaned_to_auto()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{\"outfit\":\"../../evil\"}");
        Assert.Equal("auto", AppSettings.Load(File_).Outfit);
    }

    [Fact]
    public void A_huge_file_is_not_read()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{\"outfit\":\"llama\"}" + new string(' ', 40 * 1024));
        Assert.Equal("auto", AppSettings.Load(File_).Outfit);
    }

    [Fact]
    public void Saving_an_invalid_choice_stores_auto()
    {
        new AppSettings { Outfit = "bogus" }.Save(File_);
        Assert.Equal("auto", AppSettings.Load(File_).Outfit);
    }

    [Fact]
    public void An_unwritable_location_reports_false_instead_of_throwing()
    {
        // A path under an existing *file* can never be created as a folder.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        Assert.False(new AppSettings().Save(Path.Combine(blocker, "sub", "settings.json")));
    }
}

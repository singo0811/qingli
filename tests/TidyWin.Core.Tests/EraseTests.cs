using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Erase;
using TidyWin.Core.Paths;
using TidyWin.ViewModels;
using Xunit;
using static TidyWin.Core.Tests.CleanExecutorTests;

namespace TidyWin.Core.Tests;

public class EraseTests
{
    private const string Home = @"C:\Users\Ann";

    private sealed class Env(Dictionary<string, string>? vars = null) : IAiEnvironment
    {
        public string? Variable(string name) => vars is not null && vars.TryGetValue(name, out var v) ? v : null;
        public string Folder(KnownFolder f) => f switch
        {
            KnownFolder.UserProfile => Home,
            KnownFolder.LocalAppData => Home + @"\AppData\Local",
            KnownFolder.AppData => Home + @"\AppData\Roaming",
            _ => Home + @"\AppData\Local\Temp",
        };
    }

    private sealed class FakeSystem : IEraseSystem
    {
        public HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);
        public List<SecretVariable> Variables = [];
        public List<CredentialEntry> Credentials = [];
        public List<string> RemovedVariables = [];
        public List<CredentialEntry> RemovedCredentials = [];

        public bool IsRunning(string processName) => Running.Contains(processName);
        public IReadOnlyList<SecretVariable> FindVariables(IEnumerable<string> names) => Variables.Where(v => names.Contains(v.Name)).ToList();
        public bool RemoveUserVariable(string name) { RemovedVariables.Add(name); return true; }
        public IReadOnlyList<CredentialEntry> FindCredentials(IEnumerable<string> keywords) =>
            Credentials.Where(c => keywords.Any(k => c.Target.Contains(k, StringComparison.OrdinalIgnoreCase))).ToList();
        public bool RemoveCredential(CredentialEntry entry) { RemovedCredentials.Add(entry); return true; }
    }

    private sealed class Sink : IManifestSink
    {
        public List<ManifestEntry> Entries = [];
        public void Record(ManifestEntry entry) => Entries.Add(entry);
    }

    private static EraseViewModel Vm(FakeFs fs, FakeSystem? sys = null, Sink? sink = null, Dictionary<string, string>? vars = null) =>
        new(sys ?? new FakeSystem(), new Env(vars), fs, new Probe(), sink);

    // A believable Claude Code footprint.
    private static FakeFs ClaudeDisk() => new FakeFs()
        .Add(Home + @"\.claude\projects\myapp\a.jsonl", 5000)
        .Add(Home + @"\.claude\projects\myapp\b.jsonl", 3000)
        .Add(Home + @"\.claude\history.jsonl", 200)
        .Add(Home + @"\.claude\.credentials.json", 400)
        .Add(Home + @"\.claude\statsig\x.json", 100)
        .Add(Home + @"\.claude\settings.json", 50)
        .Add(Home + @"\.claude\commands\mine.md", 70)
        .Add(Home + @"\.claude.json", 900)
        .Add(Home + @"\other.txt", 10)                       // beside .claude.json, not Claude's
        .Add(Home + @"\.codex\auth.json", 300)               // another tool
        .Add(Home + @"\Documents\notes.txt", 10);

    // ───────── the catalogue ─────────

    [Fact]
    public void Every_tool_covers_conversations_logins_and_settings_and_ids_never_repeat()
    {
        foreach (var t in AiErase.Tools)
            foreach (var kind in new[] { EraseKind.Conversations, EraseKind.Credentials, EraseKind.Settings })
                Assert.True(t.Locations.Any(l => l.Kind == kind), $"{t.Id} has nothing for {kind}");

        var ids = AiErase.Tools.SelectMany(t => t.Locations).Select(l => l.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(AiErase.Tools.Count, AiErase.Tools.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void No_location_can_climb_out_of_its_base_or_use_a_wildcard()
    {
        foreach (var l in AiErase.Tools.SelectMany(t => t.Locations))
        {
            Assert.DoesNotContain("..", l.RelativePath);
            Assert.DoesNotContain('*', l.RelativePath);
            Assert.DoesNotContain('?', l.RelativePath);
            Assert.False(Path.IsPathRooted(l.RelativePath), l.Id);
            Assert.False(string.IsNullOrWhiteSpace(l.RelativePath));
        }
    }

    [Fact]
    public void Settings_are_the_only_kind_not_ticked_for_the_person()
    {
        Assert.False(AiErase.SelectedByDefault(EraseKind.Settings));
        Assert.True(AiErase.SelectedByDefault(EraseKind.Conversations));
        Assert.True(AiErase.SelectedByDefault(EraseKind.Credentials));
        Assert.True(AiErase.SelectedByDefault(EraseKind.CachesAndLogs));
    }

    [Fact]
    public void Every_tool_says_what_to_do_before_and_after_because_local_deletion_does_not_reach_the_server()
    {
        foreach (var t in AiErase.Tools)
        {
            Assert.NotEmpty(t.BeforeSteps);
            Assert.NotEmpty(t.AfterSteps);
            Assert.Contains(t.AfterSteps, s => s.Contains("服务器") || s.Contains("账号"));
        }
    }

    // ───────── where things are ─────────

    [Fact]
    public void Paths_resolve_below_the_profile_and_appdata()
    {
        var claude = AiErase.Resolve(AiErase.ById("claude")!, new Env());
        Assert.Contains(claude, r => r.Path == Home + @"\.claude\projects");
        Assert.Contains(claude, r => r.Path == Home + @"\.claude.json");
        Assert.Contains(claude, r => r.Path == Home + @"\AppData\Roaming\Claude\Cookies");

        var cursor = AiErase.Resolve(AiErase.ById("cursor")!, new Env());
        Assert.Contains(cursor, r => r.Path == Home + @"\AppData\Roaming\Cursor\User\workspaceStorage");
        Assert.Contains(cursor, r => r.Path == Home + @"\.cursor\projects");
    }

    [Fact]
    public void A_variable_can_move_the_tools_folder_when_the_new_place_is_safe()
    {
        var env = new Env(new() { ["CODEX_HOME"] = @"D:\ai\codex-home" });
        Assert.Equal(@"D:\ai\codex-home", AiErase.HomePath(AiErase.ById("codex")!, env));
        Assert.Contains(AiErase.Resolve(AiErase.ById("codex")!, env), r => r.Path == @"D:\ai\codex-home\sessions");

        var inside = new Env(new() { ["CODEX_HOME"] = Home + @"\ai\codex" });
        Assert.Equal(Home + @"\ai\codex", AiErase.HomePath(AiErase.ById("codex")!, inside));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Users\Ann")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\Ann\AppData\Roaming")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Program Files\App")]
    [InlineData("relative\\path")]
    [InlineData("")]
    public void A_variable_that_could_swallow_the_profile_or_a_system_folder_is_ignored(string value)
    {
        var env = new Env(new() { ["CODEX_HOME"] = value });
        Assert.False(AiErase.IsSafeHomeOverride(value, env));
        Assert.Equal(Home + @"\.codex", AiErase.HomePath(AiErase.ById("codex")!, env));
    }

    [Fact]
    public void Only_places_that_exist_are_listed_with_their_real_sizes()
    {
        var fs = ClaudeDisk();
        var scan = AiErase.Scan(AiErase.ById("claude")!, new Env(), fs);

        var projects = scan.Single(s => s.Location.Id == "claude.projects");
        Assert.Equal(2, projects.FileCount);
        Assert.Equal(8000, projects.Bytes);
        Assert.Equal(900, scan.Single(s => s.Location.Id == "claude.globalstate").Bytes);
        Assert.DoesNotContain(scan, s => s.Location.Id == "claude.todos");      // absent
        Assert.DoesNotContain(scan, s => s.Path.Contains(@"\.codex"));          // another tool
    }

    [Fact]
    public void The_store_version_of_Claude_desktop_is_found_by_its_package_folder()
    {
        var pkg = Home + @"\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude";
        var fs = new FakeFs()
            .Add(pkg + @"\claude-code-sessions\s1.json", 120)
            .Add(pkg + @"\config.json", 4000)
            .Add(pkg + @"\claude-code\2.0\claude.exe", 9000)
            .Add(Home + @"\AppData\Local\Packages\Other_abc\LocalCache\Roaming\Claude\config.json", 77); // another app's package
        fs.Dirs.Add(Home + @"\AppData\Local\Packages\Claude_pzs8sxrjxfjjc");
        fs.Dirs.Add(Home + @"\AppData\Local\Packages\Other_abc");

        var scan = AiErase.Scan(AiErase.ById("claude")!, new Env(), fs);

        Assert.Equal(120, scan.Single(s => s.Path == pkg + @"\claude-code-sessions").Bytes);
        Assert.Equal(EraseKind.Conversations, scan.Single(s => s.Path == pkg + @"\claude-code-sessions").Location.Kind);
        Assert.Equal(9000, scan.Single(s => s.Path == pkg + @"\claude-code").Bytes);
        Assert.True(scan.Single(s => s.Path == pkg + @"\config.json").Location.IsFile);
        Assert.DoesNotContain(scan, s => s.Path.Contains(@"\Other_abc"));
    }

    [Fact]
    public void Without_a_disk_to_look_at_package_places_are_simply_not_listed()
    {
        var resolved = AiErase.Resolve(AiErase.ById("claude")!, new Env());
        Assert.DoesNotContain(resolved, r => r.Path.Contains(@"\Packages\"));
    }

    [Fact]
    public void Windsurf_Cline_and_Ollama_resolve_to_their_own_places()
    {
        var wind = AiErase.Resolve(AiErase.ById("windsurf")!, new Env());
        Assert.Contains(wind, r => r.Path == Home + @"\AppData\Roaming\Windsurf\User\workspaceStorage");
        Assert.Contains(wind, r => r.Path == Home + @"\.codeium\windsurf\cascade");

        var cline = AiErase.Resolve(AiErase.ById("cline")!, new Env());
        Assert.Contains(cline, r => r.Path == Home + @"\AppData\Roaming\Code\User\globalStorage\saoudrizwan.claude-dev\tasks");
        Assert.Contains(cline, r => r.Path == Home + @"\.cline\data\secrets.json");

        var ollama = AiErase.Resolve(AiErase.ById("ollama")!, new Env());
        Assert.Contains(ollama, r => r.Path == Home + @"\.ollama\models");
        Assert.Contains(ollama, r => r.Path == Home + @"\AppData\Local\Ollama\server.log");
    }

    [Fact]
    public void Downloaded_models_are_never_ticked_because_they_are_not_a_record_but_a_download()
    {
        var models = AiErase.ById("ollama")!.Locations.Single(l => l.Id == "ollama.models");
        Assert.Equal(EraseKind.Settings, models.Kind);
        Assert.False(AiErase.SelectedByDefault(models.Kind));
    }

    [Fact]
    public void No_tool_lists_a_place_that_another_tool_also_owns()
    {
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in AiErase.Tools)
            foreach (var r in AiErase.Resolve(t, new Env()))
            {
                if (owner.TryGetValue(r.Path, out var other))
                    Assert.Fail($"{r.Path} is listed by both {other} and {t.Id}");
                owner[r.Path] = t.Id;
            }
    }

    // ───────── projects, editors, ComfyUI ─────────

    private const string Proj = @"C:\Users\Ann\code\shop";

    [Fact]
    public void Claude_projects_come_from_the_names_in_the_global_state_and_only_the_names()
    {
        var fs = new FakeFs()
            .AddText(Home + @"\.claude.json", """{"userID":"secret","oauthAccount":{"emailAddress":"a@b.c"},"projects":{"C:/Users/Ann/code/shop":{"allowedTools":[]},"C:/gone/away":{},"C:/":{},"C:/Users/Ann":{}}}""")
            .Add(Proj + @"\.claude\settings.local.json", 40)
            .Add(Proj + @"\CLAUDE.local.md", 20)
            .Add(Proj + @"\CLAUDE.md", 30)               // the person's own instructions: never offered
            .Add(Proj + @"\src\main.cs", 99);

        var found = AiErase.Scan(AiErase.ById("claude")!, new Env(), fs);

        var claudeDir = found.Single(s => s.Path == Proj + @"\.claude");
        Assert.Equal(EraseKind.Settings, claudeDir.Location.Kind);
        Assert.Contains("shop", claudeDir.Location.Title);
        Assert.True(found.Single(s => s.Path == Proj + @"\CLAUDE.local.md").Location.IsFile);
        Assert.DoesNotContain(found, s => s.Path.EndsWith(@"\CLAUDE.md", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, s => s.Path.Contains(@"C:\gone"));          // the folder no longer exists
        Assert.DoesNotContain(found, s => s.Path is @"C:\.claude" or @"C:\Users\Ann\.claude" && s.Location.Id.Contains(".project."));
    }

    [Fact]
    public void Cursor_projects_come_from_the_workspace_folders_the_editor_remembers()
    {
        var storage = Home + @"\AppData\Roaming\Cursor\User\workspaceStorage";
        var fs = new FakeFs()
            .AddText(storage + @"\abc\workspace.json", """{"folder":"file:///c%3A/Users/Ann/code/shop"}""")
            .AddText(storage + @"\def\workspace.json", """{"folder":"vscode-remote://ssh-remote%2Bbox/home/x"}""")   // not on this disk
            .Add(Proj + @"\.cursor\rules\r.mdc", 10);

        var found = AiErase.Scan(AiErase.ById("cursor")!, new Env(), fs);
        Assert.Contains(found, s => s.Path.Equals(Proj + @"\.cursor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, s => s.Path.Contains("ssh-remote") || s.Path.StartsWith("/home"));
    }

    [Fact]
    public void Codex_and_Gemini_projects_are_read_from_their_own_files()
    {
        var fs = new FakeFs()
            .AddText(Home + @"\.codex\config.toml", "model = \"x\"\n[projects.'C:\\Users\\Ann\\code\\shop']\ntrust_level = \"trusted\"\n[projects.\"C:\\\\Users\\\\Ann\\\\code\\\\blog\"]\ntrust_level = \"trusted\"\n")
            .Add(Proj + @"\.codex\config.toml", 10)
            .Add(@"C:\Users\Ann\code\blog\.codex\config.toml", 10)
            .AddText(Home + @"\.gemini\trustedFolders.json", """{"C:\\Users\\Ann\\code\\shop":"TRUST_FOLDER"}""")
            .Add(Proj + @"\.gemini\settings.json", 10);

        var codex = AiErase.Scan(AiErase.ById("codex")!, new Env(), fs);
        Assert.Contains(codex, s => s.Path.Equals(Proj + @"\.codex", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(codex, s => s.Path.Equals(@"C:\Users\Ann\code\blog\.codex", StringComparison.OrdinalIgnoreCase));

        var gemini = AiErase.Scan(AiErase.ById("gemini")!, new Env(), fs);
        Assert.Contains(gemini, s => s.Path.Equals(Proj + @"\.gemini", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_project_folder_that_is_unsafe_is_never_offered()
    {
        var fs = new FakeFs()
            .AddText(Home + @"\.claude.json", """{"projects":{"C:/Windows":{},"C:/Program Files/App":{},"C:/Users":{}}}""")
            .Add(@"C:\Windows\.claude\x", 1).Add(@"C:\Program Files\App\.claude\x", 1).Add(@"C:\Users\.claude\x", 1);

        var found = AiErase.Scan(AiErase.ById("claude")!, new Env(), fs);
        Assert.DoesNotContain(found, s => s.Location.Id.Contains(".project."));
    }

    [Fact]
    public async Task Project_places_are_listed_unticked_and_only_the_ticked_ones_are_deleted()
    {
        var fs = new FakeFs()
            .AddText(Home + @"\.claude.json", """{"projects":{"C:/Users/Ann/code/shop":{}}}""")
            .Add(Proj + @"\.claude\settings.local.json", 40)
            .Add(Proj + @"\src\main.cs", 99);
        var vm = Vm(fs);
        await vm.ChooseAsync("claude");

        var projectItem = vm.Groups.SelectMany(g => g.Items).Single(i => i.Where == Proj + @"\.claude");
        Assert.False(projectItem.IsSelected);

        projectItem.IsSelected = true;
        await vm.EraseAsync();

        Assert.False(fs.Files.ContainsKey(Proj + @"\.claude\settings.local.json"));
        Assert.True(fs.Files.ContainsKey(Proj + @"\src\main.cs"));
    }

    [Fact]
    public void Cline_data_in_each_editor_is_listed_and_only_that_editor_has_to_be_closed()
    {
        var tasks = Home + @"\AppData\Roaming\Cursor\User\globalStorage\saoudrizwan.claude-dev\tasks\t1\log.json";
        var fs = new FakeFs().Add(tasks, 500);
        var cline = AiErase.ById("cline")!;
        var scan = AiErase.Scan(cline, new Env(), fs);

        Assert.Contains(scan, s => s.Path.EndsWith(@"Cursor\User\globalStorage\saoudrizwan.claude-dev\tasks"));
        Assert.Equal(["Cursor"], AiErase.ProcessesFor(cline, scan));   // VS Code has no Cline data here, so it may stay open
    }

    [Fact]
    public async Task Cline_in_a_running_editor_blocks_the_clean_but_not_when_that_editor_has_none()
    {
        var fs = new FakeFs().Add(Home + @"\AppData\Roaming\Cursor\User\globalStorage\saoudrizwan.claude-dev\tasks\t1\log.json", 500);

        var openVsCode = new FakeSystem { Running = { "Code" } };
        var vm = Vm(fs, openVsCode);
        await vm.ChooseAsync("cline");
        Assert.False(vm.Running);

        var openCursor = new FakeSystem { Running = { "Cursor" } };
        var vm2 = Vm(fs, openCursor);
        await vm2.ChooseAsync("cline");
        Assert.True(vm2.Running);
        Assert.False(vm2.CanErase);
    }

    [Fact]
    public void ComfyUI_creations_are_never_ticked_and_its_default_data_folder_is_found()
    {
        var fs = new FakeFs()
            .Add(Home + @"\Documents\ComfyUI\output\a.png", 4000)
            .Add(Home + @"\Documents\ComfyUI\input\ref.png", 100)
            .Add(Home + @"\Documents\ComfyUI\models\checkpoints\m.safetensors", 7000);
        var scan = AiErase.Scan(AiErase.ById("comfyui")!, new Env(), fs);

        var output = scan.Single(s => s.Location.Id == "comfyui.docs.output");
        Assert.Equal(EraseKind.Creations, output.Location.Kind);
        Assert.False(AiErase.SelectedByDefault(EraseKind.Creations));
        Assert.Equal(7000, scan.Single(s => s.Location.Id == "comfyui.docs.models").Bytes);
    }

    [Fact]
    public void ComfyUI_data_folder_chosen_in_its_own_config_is_found_too()
    {
        var fs = new FakeFs()
            .AddText(Home + @"\AppData\Roaming\ComfyUI\config.json", """{"basePath":"D:\\ai\\comfy","other":1}""")
            .Add(@"D:\ai\comfy\output\a.png", 4000)
            .Add(@"D:\ai\comfy\user\default\workflows\w.json", 30);
        var scan = AiErase.Scan(AiErase.ById("comfyui")!, new Env(), fs);

        Assert.Equal(4000, scan.Single(s => s.Path == @"D:\ai\comfy\output").Bytes);
        Assert.Contains(scan, s => s.Path == @"D:\ai\comfy\user\default\workflows");
    }

    [Fact]
    public void ComfyUI_will_not_take_a_drive_root_or_system_folder_as_its_data_folder()
    {
        var fs = new FakeFs()
            .AddText(Home + @"\AppData\Roaming\ComfyUI\config.json", """{"basePath":"C:\\"}""")
            .AddText(Home + @"\AppData\Roaming\Comfy Desktop\extra_models_config.yaml", "comfyui:\n  base_path: C:\\Windows\n")
            .Add(@"C:\output\x.png", 1).Add(@"C:\Windows\output\x.png", 1);
        var scan = AiErase.Scan(AiErase.ById("comfyui")!, new Env(), fs);
        Assert.DoesNotContain(scan, s => s.Path.StartsWith(@"C:\output") || s.Path.StartsWith(@"C:\Windows"));
    }

    // ───────── the guard for single files ─────────

    [Fact]
    public void A_single_file_is_allowed_by_its_exact_path_only()
    {
        var guard = new PathGuard([], [], new Probe(), [Home + @"\.claude.json"]);

        Assert.True(guard.Check(Home + @"\.claude.json").IsAllowed);
        Assert.True(guard.Check(Home + @"\.CLAUDE.JSON").IsAllowed);                 // Windows is case-insensitive
        Assert.False(guard.Check(Home + @"\other.txt").IsAllowed);                   // a neighbour
        Assert.False(guard.Check(Home).IsAllowed);                                   // its folder
        Assert.False(guard.Check(Home + @"\.claude.json\x").IsAllowed);              // below it
        Assert.False(guard.Check(Home + @"\.claude.json.bak").IsAllowed);            // a longer name
    }

    [Fact]
    public void A_single_file_that_is_a_link_is_refused_unless_only_the_link_is_removed()
    {
        var guard = new PathGuard([], [], new Probe(Home + @"\.claude.json"), [Home + @"\.claude.json"]);
        Assert.Equal(GuardVerdict.ReparsePoint, guard.Check(Home + @"\.claude.json").Verdict);
        Assert.True(guard.Check(Home + @"\.claude.json", allowLeafLink: true).IsAllowed);
    }

    [Fact]
    public void A_drive_root_can_never_be_an_allowed_file()
    {
        Assert.Throws<ArgumentException>(() => new PathGuard([], [], new Probe(), [@"C:\"]));
    }

    // ───────── the page's model ─────────

    [Fact]
    public async Task Conversations_logins_and_caches_are_ticked_and_settings_are_not()
    {
        var vm = Vm(ClaudeDisk());
        await vm.ChooseAsync("claude");

        Assert.Equal(ErasePhase.Ready, vm.Phase);
        var all = vm.Groups.SelectMany(g => g.Items).ToList();
        Assert.True(all.Single(i => i.Title == "对话记录").IsSelected);
        Assert.True(all.Single(i => i.Title == "登录凭据").IsSelected);
        Assert.True(all.Single(i => i.Title == "功能开关与统计缓存").IsSelected);
        Assert.False(all.Single(i => i.Title == "设置").IsSelected);
        Assert.False(all.Single(i => i.Title == "自定义命令").IsSelected);
        Assert.Equal(AiErase.KindTitle(EraseKind.Settings), vm.Groups.Last().Title);
    }

    [Fact]
    public async Task Machine_wide_and_process_only_variables_cannot_be_ticked_and_never_count()
    {
        var sys = new FakeSystem
        {
            Variables =
            [
                new SecretVariable("ANTHROPIC_API_KEY", true, false, true),
                new SecretVariable("ANTHROPIC_AUTH_TOKEN", false, true, false),
                new SecretVariable("CLAUDE_CODE_OAUTH_TOKEN", false, false, true),
            ],
        };
        var vm = Vm(new FakeFs(), sys);
        await vm.ChooseAsync("claude");

        var items = vm.Groups.Single(g => g.Title.StartsWith("环境变量")).Items;
        Assert.True(items.Single(i => i.Title == "ANTHROPIC_API_KEY").IsSelected);
        Assert.False(items.Single(i => i.Title == "ANTHROPIC_AUTH_TOKEN").CanTick);
        Assert.False(items.Single(i => i.Title == "ANTHROPIC_AUTH_TOKEN").IsSelected);
        Assert.False(items.Single(i => i.Title == "CLAUDE_CODE_OAUTH_TOKEN").CanTick);
        Assert.Equal(1, vm.SelectedCount);
    }

    [Fact]
    public async Task Nothing_found_is_said_instead_of_showing_an_empty_list()
    {
        var vm = Vm(new FakeFs());
        await vm.ChooseAsync("gemini");
        Assert.True(vm.NothingFound);
        Assert.False(vm.CanErase);
    }

    // ───────── erasing ─────────

    [Fact]
    public async Task Erasing_removes_the_ticked_places_and_leaves_settings_neighbours_and_other_tools_alone()
    {
        var fs = ClaudeDisk();
        var vm = Vm(fs);
        await vm.ChooseAsync("claude");
        await vm.EraseAsync();

        Assert.Equal(ErasePhase.Done, vm.Phase);
        Assert.DoesNotContain(fs.Files.Keys, p => p.Contains(@"\.claude\projects"));
        Assert.DoesNotContain(fs.Files.Keys, p => p.EndsWith("history.jsonl"));
        Assert.DoesNotContain(fs.Files.Keys, p => p.EndsWith(".credentials.json"));
        Assert.DoesNotContain(fs.Files.Keys, p => p.Contains(@"\statsig"));
        Assert.DoesNotContain(fs.Files.Keys, p => p.EndsWith(@"Ann\.claude.json"));

        Assert.Contains(Home + @"\.claude\settings.json", fs.Files.Keys);        // settings were not ticked
        Assert.Contains(Home + @"\.claude\commands\mine.md", fs.Files.Keys);
        Assert.Contains(Home + @"\other.txt", fs.Files.Keys);                     // beside .claude.json
        Assert.Contains(Home + @"\.codex\auth.json", fs.Files.Keys);              // another tool
        Assert.Contains(Home + @"\Documents\notes.txt", fs.Files.Keys);
        Assert.DoesNotContain(fs.DeleteCalls, p => p.Contains("notes.txt") || p.EndsWith("other.txt") || p.Contains(".codex"));
    }

    [Fact]
    public async Task Ticking_settings_removes_them_too_and_the_tools_empty_folder_is_tidied_away()
    {
        var fs = ClaudeDisk();
        var vm = Vm(fs);
        await vm.ChooseAsync("claude");
        vm.SetAll(true);
        await vm.EraseAsync();

        Assert.DoesNotContain(fs.Files.Keys, p => p.Contains(@"\.claude\"));
        Assert.DoesNotContain(Home + @"\.claude", fs.Dirs);
        Assert.Contains(Home + @"\other.txt", fs.Files.Keys);
    }

    [Fact]
    public async Task Untick_one_place_and_it_stays()
    {
        var fs = ClaudeDisk();
        var vm = Vm(fs);
        await vm.ChooseAsync("claude");
        vm.Groups.SelectMany(g => g.Items).Single(i => i.Title == "输入过的提示词历史").IsSelected = false;
        await vm.EraseAsync();

        Assert.Contains(Home + @"\.claude\history.jsonl", fs.Files.Keys);
        Assert.DoesNotContain(fs.Files.Keys, p => p.Contains(@"\projects"));
    }

    [Fact]
    public async Task A_tool_that_is_running_is_never_cleaned()
    {
        var fs = ClaudeDisk();
        var sys = new FakeSystem { Running = { "claude" } };
        var vm = Vm(fs, sys);
        await vm.ChooseAsync("claude");

        Assert.True(vm.Running);
        Assert.False(vm.CanErase);
        await vm.EraseAsync();

        Assert.Equal(ErasePhase.Ready, vm.Phase);
        Assert.Contains("还在运行", vm.Message);
        Assert.Empty(fs.DeleteCalls);
        Assert.Empty(sys.RemovedVariables);
    }

    [Fact]
    public async Task A_dry_run_changes_nothing_but_reports_what_would_go()
    {
        var fs = ClaudeDisk();
        var sys = new FakeSystem { Variables = [new SecretVariable("ANTHROPIC_API_KEY", true, false, false)], Credentials = [new CredentialEntry("claude-code-cred", 1)] };
        var sink = new Sink();
        var vm = Vm(fs, sys, sink);
        await vm.ChooseAsync("claude");
        await vm.EraseAsync(dryRun: true);

        Assert.Empty(fs.DeleteCalls);
        Assert.Empty(sys.RemovedVariables);
        Assert.Empty(sys.RemovedCredentials);
        Assert.Empty(sink.Entries);
        Assert.StartsWith("预演", vm.ResultTitle);
        Assert.Contains("1 个环境变量", vm.ResultTitle);
        Assert.Contains("1 条凭据", vm.ResultTitle);
    }

    [Fact]
    public async Task Ticked_variables_and_credentials_are_removed_and_unticked_ones_are_not()
    {
        var sys = new FakeSystem
        {
            Variables = [new SecretVariable("OPENAI_API_KEY", true, false, false), new SecretVariable("CODEX_API_KEY", true, false, false)],
            Credentials = [new CredentialEntry("codex-login", 1), new CredentialEntry("codex-other", 1)],
        };
        var vm = Vm(new FakeFs(), sys);
        await vm.ChooseAsync("codex");
        vm.Groups.SelectMany(g => g.Items).Single(i => i.Title == "CODEX_API_KEY").IsSelected = false;
        vm.Groups.SelectMany(g => g.Items).Single(i => i.Title == "codex-other").IsSelected = false;
        await vm.EraseAsync();

        Assert.Equal(["OPENAI_API_KEY"], sys.RemovedVariables);
        Assert.Equal(["codex-login"], sys.RemovedCredentials.Select(c => c.Target).ToList());
    }

    [Fact]
    public async Task The_record_keeps_place_names_and_never_file_names()
    {
        var fs = ClaudeDisk();
        var sink = new Sink();
        var vm = Vm(fs, sink: sink);
        await vm.ChooseAsync("claude");
        await vm.EraseAsync();

        var entry = Assert.Single(sink.Entries);
        Assert.Equal("erase-claude", entry.Source);
        Assert.True(entry.Deleted > 0);
        Assert.Contains("对话记录", entry.Paths);
        Assert.DoesNotContain(entry.Paths, p => p.Contains("myapp") || p.Contains(".jsonl") || p.Contains(@"C:\"));
    }

    [Fact]
    public async Task A_file_in_use_is_reported_and_kept_while_the_rest_goes()
    {
        var fs = ClaudeDisk();
        fs.Locked.Add(Home + @"\.claude\.credentials.json");
        var vm = Vm(fs);
        await vm.ChooseAsync("claude");
        await vm.EraseAsync();

        Assert.Contains(Home + @"\.claude\.credentials.json", fs.Files.Keys);
        Assert.DoesNotContain(fs.Files.Keys, p => p.Contains(@"\projects"));
        Assert.Contains("正被某个程序占用", vm.ResultDetail);
    }

    [Fact]
    public async Task A_link_inside_a_folder_is_removed_without_following_it()
    {
        var fs = new FakeFs()
            .Add(Home + @"\.claude\projects\a.jsonl", 10)
            .AddLink(Home + @"\.claude\projects\shortcut", @"D:\Precious\data.txt")
            .Add(@"D:\Precious\data.txt", 99);
        var vm = Vm(fs);
        await vm.ChooseAsync("claude");
        await vm.EraseAsync();

        Assert.Contains(@"D:\Precious\data.txt", fs.Files.Keys);
        Assert.DoesNotContain(fs.DeleteCalls, p => p.StartsWith(@"D:\"));
    }

    [Fact]
    public async Task Choosing_a_tool_does_not_touch_the_disk_and_going_back_resets()
    {
        var fs = ClaudeDisk();
        var vm = Vm(fs);
        await vm.ChooseAsync("claude");
        Assert.Empty(fs.DeleteCalls);

        vm.Back();
        Assert.Equal(ErasePhase.Idle, vm.Phase);
        Assert.Empty(vm.Groups);
        Assert.Null(vm.Tool);
    }

    [Fact]
    public async Task The_cards_summarise_each_tool_and_flag_the_running_ones()
    {
        var fs = ClaudeDisk();
        var vm = Vm(fs, new FakeSystem { Running = { "Cursor" } });
        await vm.LoadCardsAsync();

        Assert.Contains("处", vm.Cards.Single(c => c.Tool.Id == "claude").Status);
        Assert.Equal("没有发现它留下的东西", vm.Cards.Single(c => c.Tool.Id == "gemini").Status);
        Assert.True(vm.Cards.Single(c => c.Tool.Id == "cursor").Running);
        Assert.Contains("正在运行", vm.Cards.Single(c => c.Tool.Id == "cursor").Status);
    }
}

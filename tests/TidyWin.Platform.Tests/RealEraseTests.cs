using System.Diagnostics;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Erase;
using TidyWin.Platform;
using TidyWin.ViewModels;
using Xunit;

namespace TidyWin.Platform.Tests;

/// <summary>
/// Erasing one AI tool on a real disk. Only the profile folders are redirected into a sandbox; scanning, the guard (including the
/// exact-file rule for a file that sits directly in the profile), and deleting are the real thing. Processes, variables and the
/// credential store are faked: a test must never remove anything from the machine it runs on.
/// </summary>
public sealed class RealEraseTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "tidywin-erase-" + Guid.NewGuid().ToString("N"));

    private sealed class SandboxEnv(string home) : IAiEnvironment
    {
        public string? Variable(string name) => null;
        public string Folder(KnownFolder f) => f switch
        {
            KnownFolder.UserProfile => home,
            KnownFolder.LocalAppData => Path.Combine(home, "Local"),
            KnownFolder.AppData => Path.Combine(home, "Roaming"),
            _ => Path.Combine(home, "Local", "Temp"),
        };
    }

    private sealed class QuietSystem : IEraseSystem
    {
        public bool IsRunning(string processName) => false;
        public IReadOnlyList<SecretVariable> FindVariables(IEnumerable<string> names) => [];
        public bool RemoveUserVariable(string name) => false;
        public IReadOnlyList<CredentialEntry> FindCredentials(IEnumerable<string> keywords) => [];
        public bool RemoveCredential(CredentialEntry entry) => false;
    }

    private sealed class Sink : IManifestSink
    {
        public List<ManifestEntry> Entries = [];
        public void Record(ManifestEntry entry) => Entries.Add(entry);
    }

    public RealEraseTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_home, "*", SearchOption.AllDirectories))
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d, false);
            Directory.Delete(_home, true);
        }
        catch { /* best effort */ }
    }

    private string Write(string rel, string text)
    {
        var p = Path.Combine(_home, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    private static bool MakeJunction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    private EraseViewModel Make(Sink? sink = null) =>
        new(new QuietSystem(), new SandboxEnv(_home), new WindowsFileSystem(), new FileSystemReparseProbe(), sink);

    [Fact]
    public async Task Erases_a_claude_footprint_and_nothing_else()
    {
        var transcript = Write(@".claude\projects\app\session.jsonl", new string('x', 4000));
        var history = Write(@".claude\history.jsonl", "prompt");
        var creds = Write(@".claude\.credentials.json", "{\"token\":\"secret\"}");
        var globalState = Write(@".claude.json", "{\"oauthAccount\":{}}");
        var neighbour = Write("notes.txt", "mine");               // sits next to .claude.json
        var settings = Write(@".claude\settings.json", "{}");     // settings stay unless ticked
        var cookies = Write(@"Roaming\Claude\Cookies", "c");
        var cache = Write(@"Roaming\Claude\Cache\Cache_Data\f_1", new string('c', 100));
        var otherTool = Write(@".codex\auth.json", "{}");
        var project = Write(@"code\myproject\CLAUDE.md", "project notes");

        var sink = new Sink();
        var vm = Make(sink);
        await vm.ChooseAsync("claude");
        await vm.EraseAsync();

        Assert.False(File.Exists(transcript));
        Assert.False(File.Exists(history));
        Assert.False(File.Exists(creds));
        Assert.False(File.Exists(globalState));
        Assert.False(File.Exists(cookies));
        Assert.False(File.Exists(cache));
        Assert.False(Directory.Exists(Path.Combine(_home, ".claude", "projects")));

        Assert.True(File.Exists(neighbour));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(otherTool));
        Assert.True(File.Exists(project));

        Assert.Equal(ErasePhase.Done, vm.Phase);
        var entry = Assert.Single(sink.Entries);
        Assert.DoesNotContain(entry.Paths, p => p.Contains(_home, StringComparison.OrdinalIgnoreCase) || p.Contains("session"));
    }

    [Fact]
    public async Task Ticking_everything_leaves_no_trace_folder_behind_but_still_spares_the_neighbours()
    {
        Write(@".codex\sessions\2026\10\rollout.jsonl", "r");
        Write(@".codex\auth.json", "{}");
        Write(@".codex\config.toml", "model='x'");
        var neighbour = Write(@".gitconfig", "[user]");
        var claudeStuff = Write(@".claude\projects\a.jsonl", "x");

        var vm = Make();
        await vm.ChooseAsync("codex");
        vm.SetAll(true);
        await vm.EraseAsync();

        Assert.False(Directory.Exists(Path.Combine(_home, ".codex")));
        Assert.True(File.Exists(neighbour));
        Assert.True(File.Exists(claudeStuff));
    }

    [Fact]
    public async Task A_dry_run_leaves_every_file_where_it_was()
    {
        var a = Write(@".gemini\oauth_creds.json", "{}");
        var b = Write(@".gemini\tmp\abc\chat.json", "{}");

        var vm = Make();
        await vm.ChooseAsync("gemini");
        await vm.EraseAsync(dryRun: true);

        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.StartsWith("预演", vm.ResultTitle);
    }

    [Fact]
    public async Task A_junction_inside_a_place_is_not_followed_and_what_it_points_to_survives()
    {
        var precious = Write(@"Precious\data.txt", "keep me");
        Write(@".claude\projects\a.jsonl", "x");
        var link = Path.Combine(_home, ".claude", "projects", "elsewhere");
        if (!MakeJunction(link, Path.Combine(_home, "Precious"))) return; // junctions not available here

        var vm = Make();
        await vm.ChooseAsync("claude");
        await vm.EraseAsync();

        Assert.True(File.Exists(precious));
    }

    [Fact]
    public async Task A_read_only_file_is_still_erased()
    {
        var p = Write(@".gemini\oauth_creds.json", "{}");
        File.SetAttributes(p, FileAttributes.ReadOnly);

        var vm = Make();
        await vm.ChooseAsync("gemini");
        await vm.EraseAsync();

        Assert.False(File.Exists(p));
    }

    [Fact]
    public void The_real_system_adapter_reports_a_missing_process_and_unset_variables_without_touching_anything()
    {
        var sys = new WindowsEraseSystem();
        Assert.False(sys.IsRunning("tidywin-no-such-process-" + Guid.NewGuid().ToString("N")));
        Assert.Empty(sys.FindVariables(["TIDYWIN_TEST_NO_SUCH_VARIABLE_" + Guid.NewGuid().ToString("N")]));
        Assert.Empty(sys.FindCredentials(["tidywin-no-such-credential-" + Guid.NewGuid().ToString("N")]));
    }
}

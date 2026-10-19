using System.Text;
using TidyWin.Core.AiJunk;
using TidyWin.Core.Clean;
using TidyWin.Core.Disk;
using TidyWin.Core.Insights;
using TidyWin.Core.Mascot;
using TidyWin.Core.Optimize;
using TidyWin.Core.Paths;
using TidyWin.Core.Safety;
using TidyWin.Core.Software;
using TidyWin.Core.Startup;
using TidyWin.Core.Status;
using TidyWin.ViewModels;
using Xunit;
using static TidyWin.Core.Tests.CleanExecutorTests;

namespace TidyWin.Core.Tests;

public class OperationGateTests
{
    [Fact]
    public void Only_one_operation_at_a_time_and_the_next_one_can_start_after_it_ends()
    {
        var gate = new OperationGate();
        var first = gate.TryEnter("清理");
        Assert.NotNull(first);
        Assert.Equal("清理", gate.Current);
        Assert.Null(gate.TryEnter("卸载"));

        first!.Dispose();
        first.Dispose(); // twice is harmless
        Assert.Null(gate.Current);
        Assert.NotNull(gate.TryEnter("卸载"));
    }

    [Fact]
    public void The_busy_message_names_what_is_running() => Assert.Contains("清理", OperationGate.BusyMessage("清理系统垃圾"));
}

public class CommandLineTests
{
    private static string Same(string s) => s;

    [Theory]
    [InlineData("\"C:\\Program Files\\X\\x.exe\" /silent", "C:\\Program Files\\X\\x.exe", "/silent")]
    [InlineData("C:\\Program Files\\X\\x.exe /silent /v", "C:\\Program Files\\X\\x.exe", "/silent /v")]
    [InlineData("C:\\X\\x.exe", "C:\\X\\x.exe", "")]
    [InlineData("rundll32.exe shell32.dll,Control_RunDLL", "rundll32.exe", "shell32.dll,Control_RunDLL")]
    [InlineData("\"C:\\a b\\c.exe\"", "C:\\a b\\c.exe", "")]
    public void Splits_program_and_arguments(string command, string exe, string args)
    {
        var (e, a) = CommandLine.Split(command, null, Same);
        Assert.Equal(exe, e);
        Assert.Equal(args, a);
    }

    [Fact]
    public void Empty_gives_empty() => Assert.Equal(("", ""), CommandLine.Split("  ", null, Same));

    [Fact]
    public void A_real_file_without_an_extension_is_found_through_the_file_check()
    {
        var (e, a) = CommandLine.Split(@"C:\Tools\runner --go", p => p == @"C:\Tools\runner", Same);
        Assert.Equal(@"C:\Tools\runner", e);
        Assert.Equal("--go", a);
    }
}

public class ManifestTests
{
    [Fact]
    public void The_executor_reports_every_path_it_deleted_and_nothing_else()
    {
        var fs = new FakeFs().Add(@"C:\R\a.bin", 10).Add(@"C:\R\b.bin", 20).Add(@"C:\R\locked.bin", 30);
        fs.Locked.Add(@"C:\R\locked.bin");
        var guard = new PathGuard([@"C:\R"], [], new Probe());
        var deleted = new List<string>();

        var report = CleanExecutor.Run(fs.Files.Values.ToList(), guard, fs, dryRun: false, deletedPaths: deleted);

        Assert.Equal(2, report.Deleted);
        Assert.Equal([@"C:\R\a.bin", @"C:\R\b.bin"], deleted.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void A_dry_run_reports_no_deleted_paths()
    {
        var fs = new FakeFs().Add(@"C:\R\a.bin", 10);
        var deleted = new List<string>();
        CleanExecutor.Run(fs.Files.Values.ToList(), new PathGuard([@"C:\R"], [], new Probe()), fs, dryRun: true, deletedPaths: deleted);
        Assert.Empty(deleted);
    }

    [Fact]
    public void A_long_list_is_cut_and_the_rest_is_counted()
    {
        var report = new CleanReport(false, 5000, 1, 0, 0, [], false);
        var paths = Enumerable.Range(0, 5000).Select(i => $@"C:\R\{i}").ToList();
        var e = CleanManifest.Create("system", report, paths, new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(CleanManifest.MaxPaths, e.Paths.Count);
        Assert.Equal(5000 - CleanManifest.MaxPaths, e.MorePaths);
    }

    [Fact]
    public void Json_keeps_paths_readable()
    {
        var e = new ManifestEntry(DateTime.UtcNow, "system", 1, 99, [@"C:\用户\缓存\a.tmp"], 0);
        var json = CleanManifest.ToJson(e);
        Assert.Contains("缓存", json);
        Assert.Contains("\"bytesFreed\": 99", json);
    }
}

public class StartupAdvisorTests
{
    private static StartupEntry Entry(string name, string cmd, StartupSource s = StartupSource.UserRun) => new(name, cmd, s, true);
    private static StartupAdvice Advise(string name, string cmd, bool exists = true) =>
        StartupAdvisor.Advise(Entry(name, cmd), _ => exists);

    [Fact]
    public void A_missing_program_is_dead_and_removable()
    {
        var a = Advise("Old", @"""C:\Gone\old.exe"" /s", exists: false);
        Assert.Equal(StartupKind.Dead, a.Kind);
        Assert.Equal(StartupVerdict.Remove, a.Verdict);
        Assert.False(a.TargetExists);
    }

    [Fact]
    public void A_bare_program_name_cannot_be_judged_dead()
    {
        var a = Advise("Tool", "rundll32.exe x.dll,Run", exists: false);
        Assert.NotEqual(StartupKind.Dead, a.Kind);
    }

    [Theory]
    [InlineData(@"""C:\Users\Ann\AppData\Local\Temp\x.exe""")]
    [InlineData(@"C:\Users\Public\y.exe")]
    [InlineData("powershell.exe -EncodedCommand AAAA")]
    [InlineData(@"""C:\a\b.exe"" http://evil.example/x")]
    public void Odd_places_and_encoded_commands_are_suspicious(string cmd)
    {
        var a = Advise("Thing", cmd);
        Assert.Equal(StartupKind.Suspicious, a.Kind);
        Assert.Equal(StartupVerdict.Remove, a.Verdict);
    }

    [Theory]
    [InlineData("OneDrive", @"""C:\Users\Ann\AppData\Local\Microsoft\OneDrive\OneDrive.exe"" /background", StartupKind.Cloud)]
    [InlineData("RtkAudUService", @"""C:\Windows\System32\RtkAudUService64.exe""", StartupKind.Driver)]
    [InlineData("WeChat", @"""C:\Program Files\Tencent\WeChat\WeChat.exe"" /autorun", StartupKind.Chat)]
    [InlineData("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent", StartupKind.Game)]
    [InlineData("GoogleUpdateTaskMachine", @"""C:\Program Files\Google\Update\GoogleUpdate.exe"" /c", StartupKind.Updater)]
    [InlineData("SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe", StartupKind.System)]
    [InlineData("Ollama", @"""C:\Users\Ann\AppData\Local\Programs\Ollama\ollama app.exe""", StartupKind.Tool)]
    public void Known_programs_are_recognised(string name, string cmd, StartupKind kind) =>
        Assert.Equal(kind, Advise(name, cmd).Kind);

    [Fact]
    public void Short_names_must_match_exactly_so_unrelated_words_are_not_caught()
    {
        Assert.Equal(StartupKind.Chat, Advise("QQ", @"""C:\Tencent\QQ.exe""").Kind);
        Assert.Equal(StartupKind.Other, Advise("Optimizer", @"""C:\Opt\timer.exe""").Kind);
        Assert.Equal(StartupKind.Other, Advise("MyApp", @"""C:\x\qqnotes.exe""").Kind);
    }

    [Fact]
    public void System_and_hardware_items_are_to_keep_everything_else_is_optional()
    {
        Assert.Equal(StartupVerdict.Keep, Advise("SecurityHealth", @"C:\Windows\System32\SecurityHealthSystray.exe").Verdict);
        Assert.Equal(StartupVerdict.Keep, Advise("Nvidia", @"C:\Windows\System32\nvbackend.exe").Verdict);
        Assert.Equal(StartupVerdict.Optional, Advise("Whatever", @"C:\x\whatever.exe").Verdict);
    }

    [Fact]
    public void Droppable_counts_only_enabled_chat_update_game_and_removable_entries()
    {
        (StartupEntry, StartupAdvice) Pair(string n, string c, bool enabled, bool exists = true)
        {
            var e = new StartupEntry(n, c, StartupSource.UserRun, enabled);
            return (e, StartupAdvisor.Advise(e, _ => exists));
        }
        var all = new[]
        {
            Pair("WeChat", @"C:\a\WeChat.exe", true),
            Pair("Steam", @"C:\a\steam.exe", false),   // already off
            Pair("Old", @"C:\a\old.exe", true, exists: false),
            Pair("Nvidia", @"C:\a\nvbackend.exe", true), // hardware: keep
        };
        Assert.Equal(2, StartupAdvisor.Droppable(all));
    }
}

public class SoftwareTests
{
    private static RawUninstall Raw(string name, string? uninstall = "x.exe", string version = "1.0", long? kb = null, string? loc = null,
        bool system = false, string? parent = null, string? release = null, string hive = "HKLM64", string? date = null, string? key = null) =>
        new(key ?? name, hive, name, version, "Pub", date, kb, loc, uninstall, null, system, parent, release);

    [Fact]
    public void System_components_updates_and_nameless_entries_are_not_listed()
    {
        var apps = SoftwareCatalog.Build(
        [
            Raw("Good App", kb: 10),
            Raw("Hidden Runtime", system: true),
            Raw("Security Update for Windows (KB5034441)"),
            Raw("Update for Microsoft Office (KB1234567)"),
            Raw("Patch", release: "Hotfix"),
            Raw("Child", parent: "Parent"),
            Raw("No Uninstaller", uninstall: null),
            Raw("  ", uninstall: "x.exe"),
        ]);
        Assert.Equal(["Good App"], apps.Select(a => a.Name).ToArray());
    }

    [Fact]
    public void The_same_program_registered_twice_is_one_row_and_the_biggest_first()
    {
        var apps = SoftwareCatalog.Build(
        [
            Raw("Small", kb: 100), Raw("Big", kb: 900_000), Raw("Big", kb: null, hive: "HKLM32", key: "Big32"), Raw("None"),
        ]);
        Assert.Equal(["Big", "Small", "None"], apps.Select(a => a.Name).ToArray());
        Assert.Equal(900_000L * 1024, apps[0].SizeBytes);
    }

    [Fact]
    public void Install_date_is_read_and_a_bad_one_is_ignored()
    {
        Assert.Equal(new DateTime(2025, 3, 9), SoftwareCatalog.ParseDate("20250309"));
        Assert.Null(SoftwareCatalog.ParseDate("2025"));
        Assert.Null(SoftwareCatalog.ParseDate("20251399"));
    }

    [Fact]
    public void A_msi_entry_that_would_repair_is_turned_into_remove()
    {
        var app = SoftwareCatalog.Build([Raw("Msi App", uninstall: "MsiExec.exe /I{ABC-123}")])[0];
        var (exe, args) = SoftwareCatalog.UninstallCommand(app, _ => false);
        Assert.Equal("MsiExec.exe", exe);
        Assert.Equal("/X{ABC-123}", args);
    }

    [Fact]
    public void A_normal_uninstaller_is_left_exactly_as_registered()
    {
        var app = SoftwareCatalog.Build([Raw("Plain", uninstall: "\"C:\\Program Files\\Plain\\unins000.exe\" /SILENT")])[0];
        var (exe, args) = SoftwareCatalog.UninstallCommand(app, _ => false);
        Assert.Equal(@"C:\Program Files\Plain\unins000.exe", exe);
        Assert.Equal("/SILENT", args);
    }

    // ───────── leftovers ─────────

    [Theory]
    [InlineData("Notepad++ (64-bit x64)", "notepad")]
    [InlineData("Foo Bar 3.2.1", "foobar")]
    [InlineData("微信 WeChat", "微信wechat")]
    public void Names_are_normalized_without_versions_and_brackets(string name, string expected) =>
        Assert.Equal(expected, ResidualFinder.Normalize(name));

    private static InstalledApp App(string name, string? loc = null) =>
        new("id|" + name, name, "1", "Pub", null, null, loc, "x.exe", "HKLM64");

    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Users\Ann\AppData\Local", @"C:\Users\Ann\AppData\Roaming"];

    [Fact]
    public void Leftover_folders_with_the_apps_name_and_its_old_install_folder_are_found()
    {
        var fs = new FakeFs()
            .Add(@"C:\Program Files\Foo Studio\data.bin", 500)
            .Add(@"C:\Users\Ann\AppData\Roaming\FooStudio\cfg.json", 20)
            .Add(@"C:\Users\Ann\AppData\Local\FooStudio-Cache\x", 30)
            .Add(@"C:\Users\Ann\AppData\Local\Other\x", 40);
        var gone = App("Foo Studio", loc: @"C:\Program Files\Foo Studio");

        var found = ResidualFinder.Find(gone, [], Roots, fs);

        Assert.Equal(
            [@"C:\Program Files\Foo Studio", @"C:\Users\Ann\AppData\Local\FooStudio-Cache", @"C:\Users\Ann\AppData\Roaming\FooStudio"],
            found.Select(r => r.Path).OrderBy(x => x).ToArray());
        Assert.DoesNotContain(found, r => r.Path.EndsWith("Other"));
        Assert.Equal(500, found.Single(r => r.Path.EndsWith("Foo Studio")).Bytes);
    }

    [Fact]
    public void A_folder_that_belongs_to_another_installed_app_is_never_offered()
    {
        var fs = new FakeFs().Add(@"C:\Program Files\Foo Studio Pro\x", 5).Add(@"C:\Users\Ann\AppData\Roaming\FooStudio\y", 5);
        var gone = App("Foo Studio");
        var others = new[] { App("Foo Studio Pro", loc: @"C:\Program Files\Foo Studio Pro") };

        var found = ResidualFinder.Find(gone, others, Roots, fs);

        Assert.DoesNotContain(found, r => r.Path.Contains("Foo Studio Pro"));
        Assert.Contains(found, r => r.Path.EndsWith("FooStudio"));
    }

    [Fact]
    public void Shared_folder_names_and_very_short_names_are_never_matched()
    {
        var fs = new FakeFs().Add(@"C:\Program Files\Common Files\x", 5).Add(@"C:\Users\Ann\AppData\Local\Microsoft\x", 5)
            .Add(@"C:\Users\Ann\AppData\Local\Go\x", 5).Add(@"C:\Users\Ann\AppData\Local\Goland\x", 5);
        Assert.Empty(ResidualFinder.Find(App("Microsoft"), [], Roots, fs));
        Assert.Empty(ResidualFinder.Find(App("Go"), [], Roots, fs)); // "go" is too short to match by name
    }

    [Fact]
    public void An_install_location_outside_every_root_or_a_root_itself_is_ignored()
    {
        var fs = new FakeFs().Add(@"D:\Games\Foo\x", 5).Add(@"C:\Program Files\x", 5);
        Assert.Empty(ResidualFinder.Find(App("Foo Game", @"D:\Games\Foo"), [], Roots, fs));
        Assert.Empty(ResidualFinder.Find(App("Foo Game", @"C:\Program Files"), [], Roots, fs));
    }

    // ───────── winget ─────────

    private static int W(char c) => c >= 0x2E80 && c <= 0xA4CF ? 2 : 1;
    private static string Pad(string s, int width) => s + new string(' ', Math.Max(0, width - s.Sum(W)));

    [Fact]
    public void An_english_winget_table_is_read()
    {
        var sb = new StringBuilder();
        sb.AppendLine("   - ");
        sb.AppendLine(Pad("Name", 26) + Pad("Id", 26) + Pad("Version", 12) + Pad("Available", 12) + "Source");
        sb.AppendLine(new string('-', 90));
        sb.AppendLine(Pad("Git", 26) + Pad("Git.Git", 26) + Pad("2.44.0", 12) + Pad("2.45.1", 12) + "winget");
        sb.AppendLine(Pad("Microsoft Visual Studio Code", 26)[..24] + "  " + Pad("Microsoft.VisualStudioCode", 26) + Pad("1.90.0", 12) + Pad("1.91.1", 12) + "winget");
        sb.AppendLine("2 upgrades available.");

        var list = WingetTableParser.Parse(sb.ToString());

        Assert.Equal(2, list.Count);
        Assert.Equal(("Git", "Git.Git", "2.44.0", "2.45.1", "winget"), (list[0].Name, list[0].Id, list[0].Current, list[0].Available, list[0].Source));
        Assert.Equal("Microsoft.VisualStudioCode", list[1].Id);
    }

    [Fact]
    public void A_chinese_winget_table_with_wide_characters_is_read_by_display_width()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Pad("名称", 22) + Pad("ID", 24) + Pad("版本", 12) + Pad("可用", 12) + "源");
        sb.AppendLine(new string('-', 80));
        sb.AppendLine(Pad("微信", 22) + Pad("Tencent.WeChat", 24) + Pad("3.9.8", 12) + Pad("3.9.12", 12) + "winget");
        sb.AppendLine(Pad("腾讯会议", 22) + Pad("Tencent.TencentMeeting", 24) + Pad("3.19", 12) + Pad("3.20", 12) + "winget");
        sb.AppendLine("2 个升级可用。");

        var list = WingetTableParser.Parse(sb.ToString());

        Assert.Equal(["Tencent.WeChat", "Tencent.TencentMeeting"], list.Select(x => x.Id).ToArray());
        Assert.Equal(["微信", "腾讯会议"], list.Select(x => x.Name).ToArray());
        Assert.Equal(["3.9.12", "3.20"], list.Select(x => x.Available).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("没有可用的升级。")]
    [InlineData("No installed package found matching input criteria.")]
    public void Output_without_a_table_gives_no_upgrades(string text) => Assert.Empty(WingetTableParser.Parse(text));
}

public class DiskTests
{
    private const string R = @"C:\Data";

    [Fact]
    public void Folder_sizes_are_added_up_and_sorted_biggest_first()
    {
        var fs = new FakeFs()
            .Add(R + @"\a\1.bin", 100).Add(R + @"\a\2.bin", 200)
            .Add(R + @"\b\sub\3.bin", 1000)
            .Add(R + @"\root.bin", 7);

        var scan = DiskScanner.Scan(R, fs);

        Assert.Equal(1307, scan.TotalBytes);
        Assert.Equal(4, scan.FileCount);
        Assert.Equal(1307, scan.Root.Bytes);
        Assert.Equal(7, scan.Root.LooseBytes);
        Assert.Equal(["b", "a"], scan.Root.Folders.Select(f => f.Name).ToArray());
        Assert.Equal(1000, scan.Root.Folders[0].Bytes);
        Assert.Equal(1000, scan.Root.Folders[0].Folders.Single().Bytes);
    }

    [Fact]
    public void Folders_deeper_than_the_limit_are_counted_in_their_ancestor_not_kept()
    {
        var deep = R + string.Concat(Enumerable.Range(1, DiskScanner.MaxDepth + 3).Select(i => $@"\d{i}"));
        var fs = new FakeFs().Add(deep + @"\x.bin", 500).Add(R + @"\d1\y.bin", 5);

        var scan = ScanOf(fs);

        Assert.Equal(505, scan.TotalBytes);
        var node = scan.Root;
        var depth = 0;
        while (node.Folders.Count > 0) { node = node.Folders[0]; depth++; }
        Assert.Equal(DiskScanner.MaxDepth, depth);
        Assert.True(node.HasHiddenDepth);
        Assert.Equal(500, node.Bytes);
    }

    private static DiskScan ScanOf(FakeFs fs) => DiskScanner.Scan(R, fs);

    [Fact]
    public void Only_the_biggest_files_are_remembered_in_order()
    {
        var fs = new FakeFs();
        for (var i = 1; i <= DiskScanner.TopFilesKept + 50; i++) fs.Add($@"{R}\f{i}.bin", i);

        var scan = ScanOf(fs);

        Assert.Equal(DiskScanner.TopFilesKept, scan.TopFiles.Count);
        Assert.Equal(DiskScanner.TopFilesKept + 50, scan.TopFiles[0].Length);
        Assert.True(scan.TopFiles.Zip(scan.TopFiles.Skip(1), (a, b) => a.Length >= b.Length).All(x => x));
        Assert.Equal(51, scan.TopFiles[^1].Length);
    }

    [Fact]
    public void Links_never_add_size()
    {
        var fs = new FakeFs().Add(R + @"\real.bin", 10).Add(R + @"\link.bin", 999_999, link: true);
        Assert.Equal(10, ScanOf(fs).TotalBytes);
    }

    [Fact]
    public void A_cancelled_scan_says_so_and_keeps_what_it_had()
    {
        var fs = new FakeFs().Add(R + @"\a.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var scan = DiskScanner.Scan(R, fs, null, cts.Token);
        Assert.True(scan.Cancelled);
        Assert.Equal(0, scan.TotalBytes);
    }

    // ───────── treemap ─────────

    [Fact]
    public void Treemap_boxes_fill_the_area_stay_inside_and_do_not_overlap()
    {
        double[] values = [60, 30, 20, 10, 5, 5, 3, 1];
        var boxes = TreemapLayout.Squarify(values, 10, 20, 600, 400);

        Assert.Equal(600 * 400, boxes.Sum(b => b.W * b.H), 3);
        foreach (var b in boxes)
        {
            Assert.True(b.X >= 10 - 1e-6 && b.Y >= 20 - 1e-6 && b.X + b.W <= 610 + 1e-6 && b.Y + b.H <= 420 + 1e-6);
        }
        for (var i = 0; i < boxes.Length; i++)
            for (var j = i + 1; j < boxes.Length; j++)
            {
                var ox = Math.Min(boxes[i].X + boxes[i].W, boxes[j].X + boxes[j].W) - Math.Max(boxes[i].X, boxes[j].X);
                var oy = Math.Min(boxes[i].Y + boxes[i].H, boxes[j].Y + boxes[j].H) - Math.Max(boxes[i].Y, boxes[j].Y);
                Assert.False(ox > 1e-6 && oy > 1e-6, $"{i} overlaps {j}");
            }
    }

    [Fact]
    public void Treemap_areas_are_proportional_to_the_values_and_keep_the_input_order()
    {
        double[] values = [10, 40, 25];
        var boxes = TreemapLayout.Squarify(values, 0, 0, 100, 100);
        for (var i = 0; i < values.Length; i++)
            Assert.Equal(values[i] / 75 * 10000, boxes[i].W * boxes[i].H, 3);
    }

    [Fact]
    public void Treemap_gives_empty_boxes_to_zero_values_and_handles_nothing()
    {
        var boxes = TreemapLayout.Squarify([0, 5, -3], 0, 0, 50, 50);
        Assert.Equal(0, boxes[0].W * boxes[0].H);
        Assert.Equal(0, boxes[2].W * boxes[2].H);
        Assert.Equal(2500, boxes[1].W * boxes[1].H, 3);
        Assert.Empty(TreemapLayout.Squarify([], 0, 0, 10, 10));
        Assert.All(TreemapLayout.Squarify([1, 2], 0, 0, 0, 10), b => Assert.Equal(0, b.W));
    }

    [Fact]
    public void Treemap_squares_are_reasonably_square()
    {
        var boxes = TreemapLayout.Squarify(Enumerable.Repeat(1.0, 16).ToArray(), 0, 0, 400, 400);
        Assert.All(boxes, b => Assert.Equal(100, b.W, 3));
    }

    // ───────── recycle policy ─────────

    private static readonly string[] SystemRoots = [@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData"];
    private const string Home = @"C:\Users\Ann";

    [Theory]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Program Files\App\a.exe")]
    [InlineData(@"C:\ProgramData\x")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users\Ann")]
    [InlineData(@"C:\Users")]              // contains the whole profile
    [InlineData(@"C:\Users\")]
    [InlineData(@"c:\users")]
    [InlineData(@"C:\Users\Ann\Documents")]
    [InlineData(@"C:\Users\Ann\Desktop")]
    [InlineData(@"C:\Users\Ann\AppData")]
    [InlineData(@"C:\$Recycle.Bin\S-1\x")]
    [InlineData(@"D:\System Volume Information\x")]
    [InlineData("")]
    public void Dangerous_places_are_refused_with_a_reason(string path) =>
        Assert.False(string.IsNullOrEmpty(RecyclePolicy.Refuse(path, SystemRoots, Home)));

    [Theory]
    [InlineData(@"D:\Games\Old\saves.zip")]
    [InlineData(@"C:\Users\Ann\Documents\old-backup.zip")]
    [InlineData(@"C:\Users\Ann\Downloads\setup.exe")]
    [InlineData(@"C:\Users\Ann\AppData\Local\Foo\cache.bin")]
    [InlineData(@"D:\")]
    public void Ordinary_files_are_allowed_except_a_drive_root(string path)
    {
        var reason = RecyclePolicy.Refuse(path, SystemRoots, Home);
        if (path == @"D:\") Assert.NotNull(reason); else Assert.Null(reason);
    }

    [Fact]
    public void A_folder_that_contains_a_protected_one_is_refused_but_a_sibling_profile_is_not()
    {
        // the parent of a system folder or of the person's profile can never be moved as a whole...
        Assert.Contains("包含", RecyclePolicy.Refuse(@"C:\Users", SystemRoots, Home)!);
        Assert.NotNull(RecyclePolicy.Refuse(@"C:\Users", [@"C:\Windows"], @"C:\Users\Ann\Deeper"));
        // ...but another account's folder that does not hold anything protected is the person's own business.
        Assert.Null(RecyclePolicy.Refuse(@"C:\Users\Bob\Downloads\x.iso", SystemRoots, Home));
        Assert.Null(RecyclePolicy.Refuse(@"D:\Users", SystemRoots, Home));
    }

    [Fact]
    public void A_folder_whose_name_merely_starts_like_a_system_folder_is_not_refused() =>
        Assert.Null(RecyclePolicy.Refuse(@"C:\Windows.old.backup\x", SystemRoots, Home));

    [Fact]
    public void Known_space_users_have_an_explanation_and_a_way_out()
    {
        Assert.NotEmpty(KnownSpace.All);
        Assert.All(KnownSpace.All, k =>
        {
            Assert.False(string.IsNullOrWhiteSpace(k.About), k.Id);
            Assert.False(string.IsNullOrWhiteSpace(k.HowTo), k.Id);
            Assert.NotEmpty(k.Locations);
        });
        Assert.Equal(KnownSpace.All.Count, KnownSpace.All.Select(k => k.Id).Distinct().Count());
    }
}

public class StatusTypesTests
{
    [Fact]
    public void Cpu_percent_is_busy_share_of_the_interval()
    {
        var a = new CpuTimes(1000, 2000, 500);
        var b = new CpuTimes(1500, 3000, 1000); // kernel +1000 (idle 500 inside it), user +500
        Assert.Equal((1500.0 - 500) / 1500 * 100, CpuMeter.Percent(a, b), 6);
    }

    [Fact]
    public void Cpu_percent_is_zero_without_elapsed_time_or_when_counters_go_backwards()
    {
        var a = new CpuTimes(10, 20, 30);
        Assert.Equal(0, CpuMeter.Percent(a, a));
        Assert.Equal(0, CpuMeter.Percent(new CpuTimes(100, 200, 300), new CpuTimes(1, 1, 1)));
    }

    [Fact]
    public void Throughput_is_per_second_and_formatted()
    {
        Assert.Equal(1024, Throughput.PerSecond(0, 2048, TimeSpan.FromSeconds(2)), 6);
        Assert.Equal(0, Throughput.PerSecond(500, 100, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, Throughput.PerSecond(0, 100, TimeSpan.Zero));
        Assert.Equal("512 B/s", Throughput.Format(512));
        Assert.Equal("1.5 KB/s", Throughput.Format(1536));
        Assert.Equal("2.0 MB/s", Throughput.Format(2 * 1024 * 1024));
    }

    [Theory]
    [InlineData("svchost.exe")]
    [InlineData("SVCHOST")]
    [InlineData("MsMpEng.exe")]
    [InlineData("vmmemWSL")]
    public void Common_processes_are_explained(string name) => Assert.False(string.IsNullOrWhiteSpace(ProcessExplainer.Explain(name)));

    [Fact]
    public void An_unknown_process_gets_no_made_up_explanation() => Assert.Null(ProcessExplainer.Explain("totally-unknown-thing.exe"));

    [Fact]
    public void Checkup_json_is_read_including_powershell_single_item_quirk()
    {
        var f = CheckupParser.Parse("{\"badDisks\":\"Samsung SSD\",\"problemDevices\":[\"Foo\",\"Bar\"],\"license\":1}");
        Assert.Equal(["Samsung SSD"], f.UnhealthyDisks);
        Assert.Equal(["Foo", "Bar"], f.ProblemDevices);
        Assert.True(f.Activated);
    }

    [Fact]
    public void Checkup_json_empty_lists_and_not_licensed()
    {
        var f = CheckupParser.Parse("{\"badDisks\":[],\"problemDevices\":null,\"license\":5}");
        Assert.Empty(f.UnhealthyDisks);
        Assert.Empty(f.ProblemDevices);
        Assert.False(f.Activated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void Unreadable_checkup_output_means_unknown_never_healthy_or_broken(string text)
    {
        var f = CheckupParser.Parse(text);
        Assert.Empty(f.UnhealthyDisks);
        Assert.Null(f.Activated);
    }

    [Fact]
    public void New_health_deductions_have_their_own_codes()
    {
        var r = HealthScore.Assess(new HealthInputs(0, 50, 50, null, 1, false,
            StartupEnabled: 30, OsUnsupported: true, UpdateStaleDays: 90, DiskUnhealthy: true, ProblemDevices: 2, NotActivated: true));
        var codes = r.Deductions.Select(d => d.Code).ToHashSet();
        Assert.Superset(new HashSet<string> { "startup.many", "os.unsupported", "update.stale", "disk.unhealthy", "devices.problem", "license.missing" }, codes);
        Assert.Equal(100 - 3 - 10 - 5 - 25 - 5 - 3, r.Score);
    }

    [Fact]
    public void Nothing_is_deducted_for_things_that_were_not_measured()
    {
        var r = HealthScore.Assess(new HealthInputs(0, 50, 50, null, 1, false));
        Assert.Empty(r.Deductions);
        Assert.Equal(100, r.Score);
    }
}

public class OptimizeCatalogTests
{
    [Fact]
    public void Every_task_says_when_to_use_it_and_what_it_does_and_destructive_ones_warn()
    {
        Assert.All(OptimizeCatalog.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.When), t.Id);
            Assert.False(string.IsNullOrWhiteSpace(t.Effect), t.Id);
        });
        Assert.Equal(OptimizeCatalog.All.Count, OptimizeCatalog.All.Select(t => t.Id).Distinct().Count());
        Assert.All(OptimizeCatalog.All.Where(t => t.Id is OptimizeCatalog.Recycle or OptimizeCatalog.Explorer or OptimizeCatalog.IconCache),
            t => Assert.False(string.IsNullOrWhiteSpace(t.Warning), t.Id));
    }
}

public class InsightEngineTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static Facts Healthy() => new()
    {
        NowUtc = Now, DiskTotalBytes = 500L << 30, DiskFreeBytes = 300L << 30, DiskUsedPercent = 40, MemoryPercent = 40, UptimeDays = 2,
    };
    private static Insight Find(IEnumerable<Insight> l, string id) => l.Single(i => i.Id == id);

    [Fact]
    public void A_healthy_computer_gets_one_calm_all_good_message_with_real_numbers()
    {
        var list = InsightEngine.Evaluate(Healthy() with { LastCleanUtc = Now.AddDays(-3) });
        var only = Assert.Single(list);
        Assert.Equal("all.good", only.Id);
        Assert.Equal(InsightLevel.Good, only.Level);
        Assert.Contains("300.0 GB", only.Detail);
        Assert.Contains("3 天前", only.Detail);
        Assert.Equal(Destination.None, only.Go);
    }

    [Fact]
    public void A_nearly_full_disk_with_lots_of_safe_junk_says_clean_it_first()
    {
        var f = Healthy() with { DiskFreeBytes = 20L << 30, DiskUsedPercent = 96, SystemSafeBytes = 6L << 30 };
        var i = Find(InsightEngine.Evaluate(f), "disk.low");
        Assert.Equal(InsightLevel.Urgent, i.Level);
        Assert.Equal(Destination.SystemClean, i.Go);
        Assert.Contains("6.0 GB", i.Detail);
        Assert.Contains("20.0 GB", i.Title);
    }

    [Fact]
    public void A_nearly_full_disk_with_little_junk_points_at_the_biggest_folder()
    {
        var f = Healthy() with
        {
            DiskFreeBytes = 10L << 30, DiskUsedPercent = 92, SystemSafeBytes = 100L << 20,
            BiggestFolderName = "Games", BiggestFolderBytes = 180L << 30,
        };
        var i = Find(InsightEngine.Evaluate(f), "disk.low");
        Assert.Equal(Destination.Disk, i.Go);
        Assert.Contains("Games", i.Detail);
        Assert.Contains("180.0 GB", i.Detail);
    }

    [Fact]
    public void A_nearly_full_disk_that_has_not_been_scanned_yet_asks_for_the_scan_first()
    {
        var f = Healthy() with { DiskFreeBytes = 10L << 30, DiskUsedPercent = 92, SystemSafeBytes = null };
        Assert.Equal(Destination.SystemClean, Find(InsightEngine.Evaluate(f), "disk.low").Go);
    }

    [Fact]
    public void Plenty_of_space_never_triggers_a_disk_warning_and_clean_tip_is_shown_instead()
    {
        var list = InsightEngine.Evaluate(Healthy() with { SystemSafeBytes = 2L << 30 });
        Assert.DoesNotContain(list, i => i.Id == "disk.low");
        Assert.Contains("2.0 GB", Find(list, "clean.available").Title);
        Assert.Contains("你还没清理过", Find(list, "clean.available").Detail);
    }

    [Fact]
    public void The_clean_tip_remembers_the_last_clean()
    {
        var f = Healthy() with { SystemSafeBytes = 2L << 30, LastCleanUtc = Now.AddDays(-20), LastCleanBytes = 3L << 30 };
        var d = Find(InsightEngine.Evaluate(f), "clean.available").Detail;
        Assert.Contains("3 周前", d);
        Assert.Contains("3.0 GB", d);
    }

    [Fact]
    public void A_big_drop_in_free_space_since_the_last_visit_is_noticed_and_a_small_one_is_not()
    {
        var big = Healthy() with { DiskFreeBytes = 290L << 30, DiskFreeAtLastVisit = 300L << 30, LastVisitUtc = Now.AddDays(-5) };
        Assert.Contains("10.0 GB", Find(InsightEngine.Evaluate(big), "disk.shrinking").Title);

        var small = Healthy() with { DiskFreeBytes = 299L << 30, DiskFreeAtLastVisit = 300L << 30, LastVisitUtc = Now.AddDays(-5) };
        Assert.DoesNotContain(InsightEngine.Evaluate(small), i => i.Id == "disk.shrinking");

        var sameDay = big with { LastVisitUtc = Now.AddHours(-3) };
        Assert.DoesNotContain(InsightEngine.Evaluate(sameDay), i => i.Id == "disk.shrinking");
    }

    [Fact]
    public void High_memory_names_the_ai_tool_when_one_is_in_use()
    {
        var with = Find(InsightEngine.Evaluate(Healthy() with { MemoryPercent = 91, AiToolsInUse = [AiTool.Ollama] }), "memory.high");
        Assert.Contains("Ollama", with.Detail);
        Assert.Equal(Destination.Memory, with.Go);

        var without = Find(InsightEngine.Evaluate(Healthy() with { MemoryPercent = 91 }), "memory.high");
        Assert.DoesNotContain("Ollama", without.Detail);
    }

    [Fact]
    public void Ai_cache_is_only_suggested_to_people_who_use_ai_tools()
    {
        Assert.DoesNotContain(InsightEngine.Evaluate(Healthy() with { AiCleanBytes = 5L << 30 }), i => i.Id == "ai.clean");
        Assert.Contains(InsightEngine.Evaluate(Healthy() with { AiCleanBytes = 5L << 30, AiToolsInUse = [AiTool.ComfyUi] }), i => i.Id == "ai.clean");
    }

    [Fact]
    public void Startup_findings_are_reported_with_counts()
    {
        var list = InsightEngine.Evaluate(Healthy() with { StartupEnabled = 28, StartupDroppable = 9, StartupDead = 2, StartupSuspicious = 1 });
        Assert.Contains("28", Find(list, "startup.many").Title);
        Assert.Contains("9", Find(list, "startup.many").Detail);
        Assert.Contains("2", Find(list, "startup.dead").Title);
        Assert.Equal(InsightLevel.Warn, Find(list, "startup.suspicious").Level);
    }

    [Fact]
    public void Few_startup_items_are_left_alone()
    {
        var list = InsightEngine.Evaluate(Healthy() with { StartupEnabled = 8, StartupDroppable = 2 });
        Assert.DoesNotContain(list, i => i.Id == "startup.many");
    }

    [Fact]
    public void Urgent_problems_come_first_then_warnings_then_tips()
    {
        var f = Healthy() with
        {
            DiskUnhealthy = true, ProblemDevices = 1, UptimeDays = 20, SystemSafeBytes = 2L << 30, RecycleBinBytes = 4L << 30,
            SecurityRisks = 1,
        };
        var order = InsightEngine.Evaluate(f).Select(i => i.Level).ToList();
        Assert.Equal(order.OrderByDescending(x => x).ToList(), order);
        Assert.Equal("disk.unhealthy", InsightEngine.Evaluate(f)[0].Id);
        Assert.Equal(InsightLevel.Urgent, InsightEngine.Evaluate(f)[1].Level);
    }

    [Fact]
    public void A_snoozed_suggestion_is_hidden_and_the_rest_stay()
    {
        var f = Healthy() with { SystemSafeBytes = 2L << 30, RecycleBinBytes = 4L << 30 };
        var all = InsightEngine.Evaluate(f);
        Assert.Contains(all, i => i.Id == "recycle.big");

        var snoozed = InsightEngine.Evaluate(f, new HashSet<string> { "recycle.big" });
        Assert.DoesNotContain(snoozed, i => i.Id == "recycle.big");
        Assert.Contains(snoozed, i => i.Id == "clean.available");
    }

    [Fact]
    public void When_every_suggestion_is_snoozed_the_message_is_all_good_not_silence()
    {
        var f = Healthy() with { RecycleBinBytes = 4L << 30 };
        var list = InsightEngine.Evaluate(f, new HashSet<string> { "recycle.big" });
        Assert.Equal("all.good", Assert.Single(list).Id);
    }

    [Fact]
    public void Things_that_were_not_measured_never_cause_advice()
    {
        // Every optional measurement is null/false: nothing to say but "all good".
        Assert.Equal("all.good", Assert.Single(InsightEngine.Evaluate(Healthy())).Id);
    }

    [Fact]
    public void Windows_update_and_os_support_have_their_own_messages()
    {
        var list = InsightEngine.Evaluate(Healthy() with { OsUnsupported = true, UpdateStaleDays = 75, RebootPending = true });
        Assert.Contains(list, i => i.Id == "os.unsupported");
        Assert.Equal(InsightLevel.Warn, Find(list, "update.stale").Level);
        Assert.Contains("75", Find(list, "update.stale").Title);
        Assert.DoesNotContain(list, i => i.Id == "uptime.long"); // a pending reboot already says it
    }

    [Fact]
    public void Every_actionable_insight_has_a_button_label_and_a_destination()
    {
        var f = Healthy() with
        {
            DiskUnhealthy = true, DiskFreeBytes = 3L << 30, DiskUsedPercent = 97, SystemSafeBytes = 2L << 30, MemoryPercent = 95,
            SecurityRisks = 1, SecurityWarns = 2, OsUnsupported = true, UpdateStaleDays = 90, RebootPending = true, ProblemDevices = 1,
            NotActivated = true, AiCleanBytes = 2L << 30, AiToolsInUse = [AiTool.Cursor], RecycleBinBytes = 2L << 30, StartupSuspicious = 1,
            StartupDead = 1, StartupDroppable = 6, StartupEnabled = 30, UpgradableApps = 7,
            DiskFreeAtLastVisit = 50L << 30, LastVisitUtc = Now.AddDays(-3),
        };
        var list = InsightEngine.Evaluate(f);
        Assert.True(list.Count >= 12);
        Assert.All(list, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Title), i.Id);
            Assert.False(string.IsNullOrWhiteSpace(i.Detail), i.Id);
            Assert.NotEqual(Destination.None, i.Go);
            Assert.False(string.IsNullOrWhiteSpace(i.ActionLabel), i.Id);
        });
        Assert.Equal(list.Count, list.Select(i => i.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(InsightLevel.Urgent, Mood.Worried)]
    [InlineData(InsightLevel.Tip, Mood.Happy)]
    [InlineData(InsightLevel.Good, Mood.Satisfied)]
    public void The_whale_looks_the_way_the_news_feels(InsightLevel level, Mood mood)
    {
        var i = new Insight("x", level, "标题", "详情", Destination.Disk, "去", 1);
        Assert.Equal(mood, i.Mood);
        Assert.Contains("标题", InsightEngine.Say(i).Line);
    }

    [Theory]
    [InlineData(0.2, "今天")]
    [InlineData(1.2, "昨天")]
    [InlineData(5, "5 天前")]
    [InlineData(21, "3 周前")]
    [InlineData(95, "3 个月前")]
    public void Time_is_said_the_way_people_say_it(double days, string expected) => Assert.Equal(expected, InsightEngine.Ago(days));
}

public class RuleFileTests
{
    private static string One(string body) => "{\"targets\":[{" + body + "}]}";
    private const string Good = "\"id\":\"user.foo\",\"tool\":\"Foo\",\"title\":\"Foo 缓存\",\"anchor\":\"LocalAppData\",\"path\":\"Foo\\\\Cache\",\"safety\":\"regenerable\",\"minAgeDays\":7,\"consequence\":\"下次启动重建\"";

    [Fact]
    public void A_good_rule_becomes_a_target()
    {
        var r = RuleFile.Parse(One(Good));
        Assert.Empty(r.Problems);
        var t = Assert.Single(r.Targets);
        Assert.Equal("user.foo", t.Id);
        Assert.Equal(KnownFolder.LocalAppData, t.Anchor);
        Assert.Equal(@"Foo\Cache", t.RelativePath);
        Assert.Equal(AiSafety.Regenerable, t.Safety);
        Assert.Equal(7, t.MinAgeDays);
        Assert.True(t.SelectedByDefault);
    }

    [Theory]
    [InlineData("\"id\":\"foo\"", "id")]                                            // must start with user.
    [InlineData("\"safety\":\"userchoice\"", "safety")]
    [InlineData("\"safety\":\"never\"", "safety")]
    [InlineData("\"anchor\":\"Temp\"", "anchor")]
    [InlineData("\"path\":\"Foo\"", "两级")]                                         // top-level folder
    [InlineData("\"path\":\"..\\\\..\\\\Documents\"", "..")]
    [InlineData("\"path\":\"C:\\\\Windows\\\\Temp\"", "相对")]
    [InlineData("\"path\":\"Foo\\\\*\"", "通配符")]
    [InlineData("\"path\":\"Documents\\\\Stuff\"", "Documents")]
    [InlineData("\"path\":\"Microsoft\\\\Windows\"", "Microsoft")]
    [InlineData("\"consequence\":\"\"", "consequence")]
    [InlineData("\"title\":\"\"", "title")]
    public void Dangerous_or_incomplete_rules_are_rejected_with_a_reason(string override_, string reasonPart)
    {
        var body = ReplaceKey(Good, override_);
        var r = RuleFile.Parse(One(body));
        Assert.Empty(r.Targets);
        Assert.Contains(r.Problems, p => p.Contains(reasonPart));
    }

    // Replaces the value of the key named in the override, keeping the rest of a good rule.
    private static string ReplaceKey(string good, string replacement)
    {
        var key = replacement[..(replacement.IndexOf(':') + 1)];
        var start = good.IndexOf(key, StringComparison.Ordinal);
        var end = good.IndexOf(",\"", start, StringComparison.Ordinal);
        if (end < 0) end = good.Length;
        return good[..start] + replacement + good[end..];
    }

    [Fact]
    public void A_userprofile_rule_must_not_point_at_a_top_level_appdata_folder()
    {
        var r = RuleFile.Parse(One(ReplaceKey(ReplaceKey(Good, "\"anchor\":\"UserProfile\""), "\"path\":\"AppData\\\\Local\"")));
        Assert.Empty(r.Targets);
    }

    [Fact]
    public void Broken_json_and_wrong_shapes_give_a_problem_not_an_exception()
    {
        Assert.NotEmpty(RuleFile.Parse("{ not json").Problems);
        Assert.NotEmpty(RuleFile.Parse("{\"x\":1}").Problems);
        Assert.NotEmpty(RuleFile.Parse("{\"targets\":[1]}").Problems);
        Assert.Empty(RuleFile.Parse("").Targets);
    }

    [Fact]
    public void One_bad_rule_does_not_stop_the_good_ones()
    {
        var json = "{\"targets\":[{" + Good + "},{\"id\":\"bad\"}]}";
        var r = RuleFile.Parse(json);
        Assert.Single(r.Targets);
        Assert.Single(r.Problems);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated_because_people_edit_these_by_hand()
    {
        var r = RuleFile.Parse("{ // mine\n \"targets\": [ {" + Good + ",}, ] }");
        Assert.Single(r.Targets);
    }

    [Fact]
    public void Rule_targets_are_confined_by_the_same_guard_as_the_built_in_ones()
    {
        var t = RuleFile.Parse(One(Good)).Targets[0];
        var env = new FakeEnv();
        var roots = AiCatalog.AllowedRoots(AiCatalog.Resolve(env, [t]));
        var guard = new PathGuard(roots, [], new Probe());
        Assert.True(guard.Check(@"C:\Users\Ann\AppData\Local\Foo\Cache\x.bin").IsAllowed);
        Assert.False(guard.Check(@"C:\Users\Ann\AppData\Local\Foo\Settings.json").IsAllowed);
        Assert.False(guard.Check(@"C:\Users\Ann\Documents\a.docx").IsAllowed);
    }

    private sealed class FakeEnv : IAiEnvironment
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
}

public class AgeNoteTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0.1, "刚刚还有文件在写入,可能有程序正在用它")]
    [InlineData(3, "3 天前还在用")]
    [InlineData(120, "已 4 个月没有变动,放心清")]
    [InlineData(60, "")]
    public void The_age_hint_tells_whether_somebody_is_using_the_place(double daysAgo, string expected) =>
        Assert.Equal(expected, AiItemViewModel.AgeNoteFor(Now.AddDays(-daysAgo), Now));

    [Fact]
    public void No_files_no_hint() => Assert.Equal("", AiItemViewModel.AgeNoteFor(null, Now));

    [Fact]
    public void The_scanner_records_the_newest_change_in_each_place()
    {
        var fs = new FakeFs().Add(@"C:\Users\Ann\.cache\torch\a", 10, ageDays: 200).Add(@"C:\Users\Ann\.cache\torch\b", 10, ageDays: 40);
        var target = new AiTarget("t", "t", "t", AiCategory.PackageCache, AiSafety.Regenerable, KnownFolder.UserProfile, @".cache\torch", [], "c");
        var item = AiScanner.ScanOne(new ResolvedAiTarget(target, @"C:\Users\Ann\.cache\torch"), fs, CleanExecutorTests.Now);
        Assert.Equal(CleanExecutorTests.Now.AddDays(-40), item.NewestWriteUtc);
    }
}

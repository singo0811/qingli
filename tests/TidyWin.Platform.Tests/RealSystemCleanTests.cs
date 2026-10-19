using System.Diagnostics;
using TidyWin.Core.AiJunk;
using TidyWin.Platform;
using TidyWin.ViewModels;
using Xunit;

namespace TidyWin.Platform.Tests;

/// <summary>
/// The whole system clean-up on a real disk. Only the user's profile folders are redirected into a sandbox;
/// scanning, the age rule, the path guard and deleting are the real thing.
/// </summary>
public sealed class RealSystemCleanTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "tidywin-sys-" + Guid.NewGuid().ToString("N"));

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

    public RealSystemCleanTests() => Directory.CreateDirectory(_home);

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

    private AiCleanViewModel Make() =>
        new(new WindowsFileSystem(), new SandboxEnv(_home), new FileSystemReparseProbe(), catalog: SystemCatalog.All);

    private string Write(string rel, string text, int ageDays = 30)
    {
        var p = Path.Combine(_home, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-ageDays));
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

    [Fact]
    public async Task Cleans_the_disposable_places_and_leaves_everything_else_alone()
    {
        var oldTemp = Write(@"Local\Temp\setup-old.tmp", new string('t', 500));
        var nestedTemp = Write(@"Local\Temp\a\b\nested.tmp", new string('n', 700));
        var freshTemp = Write(@"Local\Temp\fresh.tmp", new string('f', 900), ageDays: 0);
        var chromeCache = Write(@"Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_1", new string('c', 3000));
        var chromeCode = Write(@"Local\Google\Chrome\User Data\Default\Code Cache\js\a", new string('j', 2000));

        // Browser data that is not a cache, and the user's own files.
        var logins = Write(@"Local\Google\Chrome\User Data\Default\Login Data", "secret");
        var cookies = Write(@"Local\Google\Chrome\User Data\Default\Network\Cookies", "cookie");
        var settings = Write(@"Roaming\Code\User\settings.json", "{}");
        var thesis = Write(@"Documents\thesis.docx", "my thesis", ageDays: 400);
        var ssh = Write(@".ssh\id_ed25519", "key", ageDays: 400);

        var vm = Make();
        await vm.ScanAsync();

        // Scanning alone touches nothing and ticks only the auto-rebuild tier.
        Assert.True(File.Exists(oldTemp));
        Assert.All(vm.Items, i => Assert.True(i.Safety == AiSafety.Regenerable ? i.IsSelected : !i.IsSelected));
        Assert.Contains(vm.Items, i => i.Path.EndsWith(@"Temp"));
        Assert.Contains(vm.Items, i => i.Path.EndsWith(@"Default\Cache"));

        await vm.CleanAsync();

        Assert.False(File.Exists(oldTemp));
        Assert.False(File.Exists(nestedTemp));
        Assert.False(File.Exists(chromeCache));
        Assert.False(File.Exists(chromeCode));
        Assert.Equal(500 + 700 + 3000 + 2000, vm.Report!.BytesFreed);

        Assert.True(File.Exists(freshTemp));   // too new
        Assert.True(File.Exists(logins));      // not a cache
        Assert.True(File.Exists(cookies));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(thesis));
        Assert.True(File.Exists(ssh));
        Assert.True(Directory.Exists(Path.Combine(_home, "Local", "Temp"))); // the temp folder itself stays
    }

    [Fact]
    public async Task A_file_held_open_by_another_program_is_skipped_not_forced()
    {
        var busy = Write(@"Local\Temp\busy.tmp", new string('b', 400));
        var free = Write(@"Local\Temp\free.tmp", new string('x', 600));

        await using var hold = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read);

        var vm = Make();
        await vm.ScanAsync();
        await vm.CleanAsync();

        Assert.True(File.Exists(busy));
        Assert.False(File.Exists(free));
        Assert.Contains(vm.Report!.Skipped, s => s.Reason == TidyWin.Core.Clean.SkipReason.InUse);
    }

    [Fact]
    public async Task A_junction_inside_temp_is_never_followed()
    {
        var precious = Write(@"Documents\precious.txt", "keep me", ageDays: 400);
        Write(@"Local\Temp\plain-old.tmp", new string('p', 300));
        var link = Path.Combine(_home, "Local", "Temp", "link");
        if (!MakeJunction(link, Path.Combine(_home, "Documents"))) return; // cannot create junctions here

        var vm = Make();
        await vm.ScanAsync();
        await vm.CleanAsync();

        Assert.True(File.Exists(precious));
        Assert.True(Directory.Exists(Path.Combine(_home, "Documents")));
    }

    [Fact]
    public async Task A_dry_run_on_a_real_disk_deletes_nothing()
    {
        var oldTemp = Write(@"Local\Temp\old.tmp", new string('t', 500));
        var vm = Make();
        await vm.ScanAsync();
        await vm.CleanAsync(dryRun: true);

        Assert.True(File.Exists(oldTemp));
        Assert.True(vm.Report!.DryRun);
    }
}

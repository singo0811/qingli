using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;
using TidyWin.Core.Software;

namespace TidyWin.Platform;

public sealed class WindowsSoftwareSource : ISoftwareSource
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<RawUninstall> Read()
    {
        var list = new List<RawUninstall>();
        ReadHive(list, Registry.LocalMachine, UninstallPath, "HKLM64");
        ReadHive(list, Registry.LocalMachine, Uninstall32Path, "HKLM32");
        ReadHive(list, Registry.CurrentUser, UninstallPath, "HKCU");
        return list;
    }

    private static void ReadHive(List<RawUninstall> list, RegistryKey hive, string path, string label)
    {
        try
        {
            using var root = hive.OpenSubKey(path);
            if (root is null) return;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(name);
                    if (k is null) continue;
                    list.Add(new RawUninstall(
                        name, label,
                        Str(k, "DisplayName"), Str(k, "DisplayVersion"), Str(k, "Publisher"), Str(k, "InstallDate"),
                        k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb : null,
                        Str(k, "InstallLocation"), Str(k, "UninstallString"), Str(k, "QuietUninstallString"),
                        k.GetValue("SystemComponent") is 1, Str(k, "ParentKeyName"), Str(k, "ReleaseType")));
                }
                catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { }
    }

    private static string? Str(RegistryKey k, string name) => k.GetValue(name) as string;
}

/// <summary>Starts the program's own uninstaller. Windows asks for administrator rights itself when it needs them.</summary>
public sealed class ProcessUninstaller : IUninstaller
{
    public async Task<int?> RunAsync(string exe, string args, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            await p.WaitForExitAsync(ct);
            return p.ExitCode;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null; // not found, or the person declined the administrator prompt
        }
    }
}

/// <summary>Asks the Windows Package Manager (winget) which programs have newer versions, and updates one at a time.</summary>
public sealed class WingetClient : IWinget
{
    private static readonly string? Exe = Find();

    public bool IsAvailable => Exe is not null;

    private static string? Find()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "winget.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public async Task<IReadOnlyList<WingetUpgrade>?> ListUpgradesAsync(CancellationToken ct = default)
    {
        var output = await RunAsync("upgrade --include-unknown --accept-source-agreements --disable-interactivity", TimeSpan.FromSeconds(120), ct);
        return output is null ? null : WingetTableParser.Parse(output.Value.Text);
    }

    public async Task<bool> UpgradeAsync(string id, CancellationToken ct = default)
    {
        // The id is shown from winget's own table; still, never let anything but an id reach the command line.
        if (id.Length == 0 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+'))) return false;
        var r = await RunAsync($"upgrade --id {id} --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            TimeSpan.FromMinutes(20), ct);
        return r is { ExitCode: 0 };
    }

    private static async Task<(string Text, int ExitCode)?> RunAsync(string args, TimeSpan timeout, CancellationToken ct)
    {
        if (Exe is null) return null;
        try
        {
            var psi = new ProcessStartInfo(Exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var text = p.StandardOutput.ReadToEndAsync(cts.Token);
            _ = p.StandardError.ReadToEndAsync(cts.Token);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return (await text, p.ExitCode);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }
}

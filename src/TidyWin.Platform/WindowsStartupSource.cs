using System.Security;
using Microsoft.Win32;
using TidyWin.Core.Startup;
using TidyWin.Core.Localization;

namespace TidyWin.Platform;

/// <summary>
/// Reads and switches the programs that start with Windows. Turning one off uses the same "StartupApproved" switch the
/// Task Manager uses, so nothing is deleted and it can be turned back on there or here.
/// </summary>
public sealed class WindowsStartupSource : IStartupSource
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static string UserFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Startup");
    private static string CommonFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "Startup");

    public IReadOnlyList<StartupEntry> Read()
    {
        var list = new List<StartupEntry>();
        ReadRun(list, Registry.CurrentUser, RunPath, StartupSource.UserRun);
        ReadRun(list, Registry.LocalMachine, RunPath, StartupSource.MachineRun);
        ReadRun(list, Registry.LocalMachine, Run32Path, StartupSource.MachineRun32);
        ReadFolder(list, UserFolder, StartupSource.UserFolder);
        ReadFolder(list, CommonFolder, StartupSource.CommonFolder);
        return list;
    }

    private static void ReadRun(List<StartupEntry> list, RegistryKey hive, string path, StartupSource source)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var command = key.GetValue(name) as string;
                if (string.IsNullOrWhiteSpace(command)) continue;
                list.Add(new StartupEntry(name, command, source, IsEnabled(source, name)));
            }
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, StartupSource source)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new StartupEntry(name, file, source, IsEnabled(source, name)));
            }
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { }
    }

    // ───────── enabled / disabled ─────────

    private static (RegistryKey Hive, string Path) Approved(StartupSource s) => s switch
    {
        StartupSource.UserRun => (Registry.CurrentUser, ApprovedRoot + "Run"),
        StartupSource.MachineRun => (Registry.LocalMachine, ApprovedRoot + "Run"),
        StartupSource.MachineRun32 => (Registry.LocalMachine, ApprovedRoot + "Run32"),
        StartupSource.UserFolder => (Registry.CurrentUser, ApprovedRoot + "StartupFolder"),
        _ => (Registry.LocalMachine, ApprovedRoot + "StartupFolder"),
    };

    /// <summary>Windows marks a disabled entry with an odd first byte (3). No value, or an even byte, means enabled.</summary>
    private static bool IsEnabled(StartupSource source, string name)
    {
        try
        {
            var (hive, path) = Approved(source);
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { return true; }
    }

    public string? SetEnabled(StartupEntry entry, bool enabled)
    {
        try
        {
            var (hive, path) = Approved(entry.Source);
            using var key = hive.CreateSubKey(path, writable: true);
            if (key is null) return Loc.T("无法打开注册表项");

            var data = new byte[12];
            if (enabled) data[0] = 2;
            else
            {
                data[0] = 3;
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            }
            key.SetValue(entry.Name, data, RegistryValueKind.Binary);
            return null;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            return entry.IsMachineWide ? Loc.T("这是所有用户共用的启动项,需要用管理员身份运行本程序才能修改。") : Loc.T("没有权限修改。");
        }
        catch (IOException e) { return e.Message; }
    }

    public string? Remove(StartupEntry entry)
    {
        try
        {
            switch (entry.Source)
            {
                case StartupSource.UserFolder:
                case StartupSource.CommonFolder:
                    var file = Path.Combine(entry.Source == StartupSource.UserFolder ? UserFolder : CommonFolder, entry.Name);
                    if (File.Exists(file)) File.Delete(file);
                    break;
                default:
                    var hive = entry.Source == StartupSource.UserRun ? Registry.CurrentUser : Registry.LocalMachine;
                    var path = entry.Source == StartupSource.MachineRun32 ? Run32Path : RunPath;
                    using (var key = hive.OpenSubKey(path, writable: true)) key?.DeleteValue(entry.Name, throwOnMissingValue: false);
                    break;
            }

            var (aHive, aPath) = Approved(entry.Source);
            using (var approved = aHive.OpenSubKey(aPath, writable: true)) approved?.DeleteValue(entry.Name, throwOnMissingValue: false);
            return null;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            return entry.IsMachineWide ? Loc.T("需要管理员权限才能移除。") : Loc.T("没有权限移除。");
        }
        catch (IOException e) { return e.Message; }
    }
}

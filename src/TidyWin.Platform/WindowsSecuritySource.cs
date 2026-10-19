using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TidyWin.Core.Security;

namespace TidyWin.Platform;

/// <summary>Reads security-related state. Everything here is read-only.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsSecuritySource : ISecuritySource
{
    public SecuritySnapshot ReadSnapshot() => new(
        ReadDefender(),
        Firewall("DomainProfile"),
        Firewall("StandardProfile"),
        Firewall("PublicProfile"),
        Uac(),
        ReadHosts(),
        ReadProxy());

    // ───────── registry / files ─────────

    private static bool? Firewall(string profile) => Dword(
        RegistryHive.LocalMachine,
        $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{profile}",
        "EnableFirewall") is { } v ? v != 0 : null;

    private static bool? Uac() => Dword(
        RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA") is { } v ? v != 0 : null;

    private static ProxySetting? ReadProxy()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64).OpenSubKey(key);
            if (k is null) return null;
            var enabled = k.GetValue("ProxyEnable") is int e && e != 0;
            return new ProxySetting(enabled, k.GetValue("ProxyServer") as string, k.GetValue("AutoConfigURL") as string);
        }
        catch { return null; }
    }

    private static int? Dword(RegistryHive hive, string path, string name)
    {
        try
        {
            using var k = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(path);
            return k?.GetValue(name) is int v ? v : null;
        }
        catch { return null; }
    }

    private static string? ReadHosts()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
            if (!File.Exists(path)) return "";
            var info = new FileInfo(path);
            if (info.Length > 2 * 1024 * 1024) return null; // not a normal hosts file; do not read it all
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return null; }
    }

    // ───────── Defender ─────────

    /// <summary>The command is a fixed string with no input from outside, and the program is called by full path.</summary>
    private const string DefenderCommand =
        "Get-MpComputerStatus | Select-Object RealTimeProtectionEnabled,AntivirusEnabled,AntivirusSignatureAge,AMRunningMode,IsTamperProtected | ConvertTo-Json -Compress";

    private static DefenderStatus? ReadDefender()
    {
        try
        {
            var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(ps)) return null;

            var psi = new ProcessStartInfo(ps)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", DefenderCommand }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(true); } catch { /* already gone */ }
                return null;
            }
            return SystemSecurityAdvisor.ParseDefenderJson(output.GetAwaiter().GetResult());
        }
        catch { return null; }
    }

    // ───────── listening ports ─────────

    public IReadOnlyList<Listener> ReadListeners()
    {
        var rows = new List<(IPAddress addr, int port, int pid)>();
        ReadTable(AF_INET, rows);
        ReadTable(AF_INET6, rows);

        var names = new Dictionary<int, (string name, string? path)>();
        var list = new List<Listener>();
        foreach (var (addr, port, pid) in rows)
        {
            if (!names.TryGetValue(pid, out var info))
            {
                info = Describe(pid);
                names[pid] = info;
            }
            list.Add(new Listener(addr.ToString(), port, pid, info.name, info.path));
        }
        return list;
    }

    private static (string name, string? path) Describe(int pid)
    {
        var path = WindowsMemorySource.QueryImagePath(pid);
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
        catch { name = path is null ? $"pid {pid}" : Path.GetFileNameWithoutExtension(path); }
        return (name, path);
    }

    private static void ReadTable(int family, List<(IPAddress, int, int)> rows)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, true, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetExtendedTcpTable(buf, ref size, true, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (rc == 122) { size += 4096; continue; } // table grew between the two calls
                if (rc != 0) return;

                var count = Marshal.ReadInt32(buf);
                var rowSize = family == AF_INET ? 24 : 56;
                for (var i = 0; i < count; i++)
                {
                    var row = buf + 4 + i * rowSize;
                    if (family == AF_INET)
                    {
                        var addr = new IPAddress((long)(uint)Marshal.ReadInt32(row, 4));
                        rows.Add((addr, Port(Marshal.ReadInt32(row, 8)), Marshal.ReadInt32(row, 20)));
                    }
                    else
                    {
                        var bytes = new byte[16];
                        Marshal.Copy(row, bytes, 0, 16);
                        var scope = (uint)Marshal.ReadInt32(row, 16);
                        rows.Add((new IPAddress(bytes, scope), Port(Marshal.ReadInt32(row, 20)), Marshal.ReadInt32(row, 52)));
                    }
                }
                return;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    /// <summary>The port sits in the low 16 bits in network byte order.</summary>
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}

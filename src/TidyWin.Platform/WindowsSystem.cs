using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using TidyWin.Core.Disk;
using TidyWin.Core.Status;

namespace TidyWin.Platform;

public sealed record BatteryState(bool Present, bool Charging, int Percent, int? MinutesLeft);

/// <summary>The small readings the status page and the suggestions need. Every one of them fails to "unknown", never to a guess.</summary>
public static class SystemExtras
{
    public static CpuTimes? ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        return new CpuTimes(ToU64(idle), ToU64(kernel), ToU64(user));
    }

    private static ulong ToU64(FILETIME t) => ((ulong)t.High << 32) | t.Low;

    public static BatteryState? ReadBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return null;
        if (s.BatteryFlag == 128 || s.BatteryFlag == 255 && s.BatteryLifePercent == 255) return new BatteryState(false, false, 0, null);
        var percent = s.BatteryLifePercent == 255 ? 0 : s.BatteryLifePercent;
        return new BatteryState(true, s.ACLineStatus == 1, percent, s.BatteryLifeTime == -1 ? null : s.BatteryLifeTime / 60);
    }

    /// <summary>Windows is waiting for a restart to finish an update.</summary>
    public static bool RebootPending()
    {
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null) return true;
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return wu is not null;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    public static int WindowsBuild => Environment.OSVersion.Version.Build;

    public static string WindowsName => WindowsBuild >= 22000 ? "Windows 11" : "Windows 10";

    /// <summary>Windows 10 stopped getting regular security updates on 14 October 2025.</summary>
    public static bool OsUnsupported(DateTime nowUtc) => WindowsBuild < 22000 && nowUtc >= new DateTime(2025, 10, 15, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>When Windows Update last installed something successfully, or null when it cannot be told.</summary>
    public static DateTime? LastWindowsUpdateUtc()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install");
            if (k?.GetValue("LastSuccessTime") is string s &&
                DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
                return t;
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    /// <summary>Total bytes received and sent by every real network adapter since it came up.</summary>
    public static (long Received, long Sent) NetworkCounters()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = ni.GetIPv4Statistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException) { }
        return (rx, tx);
    }

    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

/// <summary>Runs one short PowerShell script and returns what it printed. Used only for facts Windows offers no light API for.</summary>
public static class PowerShellProbe
{
    public static async Task<string?> RunAsync(string script, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script));
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
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
            return await text;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }
}

/// <summary>Disk health, devices with errors and activation: one PowerShell call, read once and kept for a while.</summary>
public static class CheckupReader
{
    private const string Script = """
        $ErrorActionPreference='SilentlyContinue'
        $d = @(Get-PhysicalDisk | Where-Object { $_.HealthStatus -ne 'Healthy' } | ForEach-Object { $_.FriendlyName })
        $p = @(Get-CimInstance Win32_PnPEntity -Filter "ConfigManagerErrorCode<>0" | ForEach-Object { $_.Name })
        $l = Get-CimInstance SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL AND ApplicationId='55c92734-d682-4d71-983e-d6ec3f16059f'" | Select-Object -First 1 -ExpandProperty LicenseStatus
        [pscustomobject]@{ badDisks=$d; problemDevices=$p; license=$l } | ConvertTo-Json -Compress
        """;

    private static CheckupFacts? _cache;
    private static DateTime _cachedAt;

    public static async Task<CheckupFacts> ReadAsync(CancellationToken ct = default)
    {
        if (_cache is { } c && DateTime.UtcNow - _cachedAt < TimeSpan.FromMinutes(15)) return c;
        var text = await PowerShellProbe.RunAsync(Script, TimeSpan.FromSeconds(40), ct);
        var facts = CheckupParser.Parse(text);
        if (text is not null) { _cache = facts; _cachedAt = DateTime.UtcNow; }
        return facts;
    }
}

public sealed class WindowsDrives : IDrives
{
    public IReadOnlyList<DriveEntry> Read()
    {
        var list = new List<DriveEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DriveEntry(d.Name, d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }
}
